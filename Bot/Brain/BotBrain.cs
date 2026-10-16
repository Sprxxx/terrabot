using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Chooses what to do and sequences skills.
/// <list type="bullet">
/// <item>Manual: one movement target (<see cref="SetTarget"/>) or one explicit skill (<see cref="StartSkill"/>).</item>
/// <item>Auto (<see cref="SetAuto"/>): asks <see cref="ProgressionPlanner"/> for the next skill (the <see cref="Work"/>)
/// whenever nothing runs. Reflexes (surfacing, a retreat, a fight, picking up its loot) take over as the
/// <see cref="Override"/>: the work is paused, not dropped, and when the override is over the planner is asked again;
/// if it still wants the same job (<see cref="ISkill.Key"/>), the paused instance resumes with what it had learnt
/// (AltoClef's interrupt-and-resume). Seen in game: every fight restarted the job from scratch, and state had to be
/// moved out of the skills one patch at a time to survive it.
/// Work that fails three times in a row brings a rest, and auto mode stops only after the rests run out.</item>
/// </list>
/// </summary>
public sealed class BotBrain
{
	public const int MaxConsecutiveFailures = 3;
	/// <summary>
	/// After <see cref="MaxConsecutiveFailures"/> failures in a row the brain rests this long, forgets what it found
	/// unreachable or failing (the world changes: mined tunnels, new caves seen), and tries again, like a player who
	/// keeps at it. Seen in game: auto mode stopped for good when the iron hunt failed three times.
	/// </summary>
	public const int BackoffTicks = 60 * 10;
	/// <summary>Ticks at the start of each rest spent hopping sideways, off whatever spot the plans kept failing from.</summary>
	public const int UnstickTicks = 45;
	/// <summary>Rounds of rest-and-retry without one success before auto mode stops for good.</summary>
	public const int MaxBackoffs = 5;

	private int failures;
	private int backoffs;
	private long backoffUntil = -1;

	public TilePos? Target { get; private set; }
	/// <summary>The job: the planner's skill (or the manual walk or skill). Paused while an <see cref="Override"/> runs.</summary>
	public ISkill Work { get; private set; }
	/// <summary>A reflex that has taken over from the work: surfacing, a retreat, a fight, picking up its loot.</summary>
	public ISkill Override { get; private set; }
	/// <summary>What runs now.</summary>
	public ISkill Current => Override ?? Work;
	private bool replan;
	private string replanWhy = "";

	/// <summary>
	/// Work that has neither moved the bot <see cref="ProgressTiles"/> tiles nor changed what it is doing (its
	/// description) for this long is failed, so the planner and the rests take over (AltoClef's progress checkers).
	/// Seen in game: with paused work resuming, a walk "waiting to land" held the bot in one spot for three game days;
	/// the restarts that used to break such hangs are gone.
	/// </summary>
	public const int NoProgressTicks = 2 * 3600;
	public const int ProgressTiles = 3;
	private ISkill watched;
	private TilePos watchedAt;
	private string watchedState;
	private long watchedSince;

	/// <summary>Skills that wait in place on purpose: the night at home or dug in, a wait, a boss fight.</summary>
	private static bool WaitsOnPurpose(ISkill s) => s is StayHomeSkill or ShelterSkill or WaitSkill or BossFightSkill;

	/// <summary>
	/// The body in the same place (within <see cref="SamePlaceTiles"/>) for <see cref="SamePlaceTicks"/>, not waiting on purpose:
	/// whatever the jobs are, it is stuck there (seen in game, measured run S2: afloat in a pocket for 490,000 ticks while
	/// fights, loot and mining took turns, so the work watchdog never fired). A player drinks a recall potion, or climbs out.
	/// </summary>
	public const int SamePlaceTiles = 15, SamePlaceTicks = 3 * 3600;
	private TilePos placeAnchor;
	private long placeSince;

	private void WatchPlace(BotContext ctx)
	{
		TilePos n = ctx.Node;
		if (Current != null && WaitsOnPurpose(Current) || n.ChebyshevTo(placeAnchor) > SamePlaceTiles) {
			placeAnchor = n;
			placeSince = ctx.Tick;
			return;
		}
		if (ctx.Tick - placeSince < SamePlaceTicks)
			return;
		placeSince = ctx.Tick;
		if (ctx.SpawnTile is TilePos home && home.ChebyshevTo(n) > SamePlaceTiles && Planner.RequestRecall(ctx)) {
			ctx.Log.Warn($"Auto: in the same place ({n}) for {SamePlaceTicks / 3600} game hours; drinking a recall potion");
			Interrupt(ctx);
			return;
		}
		ctx.Log.Warn($"Auto: in the same place ({n}) for {SamePlaceTicks / 3600} game hours; climbing out");
		BeginOverride(new WalkToSkill(new Core.Pathfinding.GoalRow(n.Y - 2 * SamePlaceTiles, n.X, ctx.Physics)) { Quiet = true, ToSurface = true }, ctx);
	}

	/// <summary>True if the work is stuck (and is failed now).</summary>
	private bool WorkStuck(BotContext ctx)
	{
		if (Work == null || Override != null || WaitsOnPurpose(Work)) {
			watched = null;
			return false;
		}
		// The work's own state, not its sub-skills': a walk that restarts every tick ("planning", "done", "waiting to land")
		// changed the text each tick and the watchdog never fired (measured run P2: 6 game days at one spot).
		string state = Work.Describe();
		int sub = state.IndexOf(" > ", System.StringComparison.Ordinal);
		if (sub >= 0)
			state = state.Substring(0, sub);
		if (!ReferenceEquals(watched, Work) || ctx.Node.ChebyshevTo(watchedAt) >= ProgressTiles || state != watchedState) {
			watched = Work;
			watchedAt = ctx.Node;
			watchedState = state;
			watchedSince = ctx.Tick;
			return false;
		}
		return ctx.Tick - watchedSince > NoProgressTicks;
	}
	public string LastResult { get; private set; } = "";
	public bool Auto { get; private set; }
	public ProgressionPlanner Planner { get; } = new();
	/// <summary>Pick up what a fight dropped (gel, coins...) before going back to the plan.</summary>
	public const int LootRadiusTiles = 15;
	/// <summary>
	/// A boss's loot is looked for this far round (tiles): the Eye dies over the arena and its drops fall to the floor
	/// (BossFightSkill.ArenaHalfWidth 20 to either side of the kite's centre, and the fall), farther than a fight's.
	/// </summary>
	public const int BossLootRadiusTiles = 2 * BossFightSkill.ArenaHalfWidth;

	public void SetTarget(TilePos target, BotContext ctx)
	{
		Interrupt(ctx);
		Auto = false;
		Target = target;
		LastResult = "";
	}

	/// <summary>Run <paramref name="skill"/> now (replacing whatever runs, and forgetting any walk target).</summary>
	public void StartSkill(ISkill skill, BotContext ctx)
	{
		Interrupt(ctx);
		Auto = false;
		Target = null;
		LastResult = "";
		Work = skill;
		skill.Start(ctx);
	}

	public void SetAuto(bool on, BotContext ctx, string plan = null)
	{
		Interrupt(ctx);
		Target = null;
		Auto = on;
		failures = 0;
		backoffs = 0;
		ctx.DigAnyway = false; // a last resort of the run before, not of this one (it leaked into the next self-test scenario)
		ctx.DepthBonus = 0;
		backoffUntil = -1;
		Planner.Reset(plan);
		ctx.UnreachableTiles.Clear();
		LastResult = "";
		ctx.Log.Info($"Auto mode {(on ? "ON" : "OFF")}" + (on ? $", plan '{Planner.Plan}'" : ""));
	}

	/// <summary>Forget the target, leave auto mode and stop the running skill.</summary>
	public void Clear(BotContext ctx)
	{
		Interrupt(ctx);
		Target = null;
		Auto = false;
	}

	/// <summary>Stop the running skills (override and work) but keep the target/mode (bot switched off; resumes with a fresh plan).</summary>
	public void Interrupt(BotContext ctx)
	{
		Override?.Stop(ctx);
		Override = null;
		Work?.Stop(ctx);
		Work = null;
		replan = false;
	}

	/// <summary>
	/// A reflex takes over: the work is paused (its controls and sub-skills released, the instance kept), or a weaker
	/// reflex is replaced.
	/// </summary>
	private void BeginOverride(ISkill skill, BotContext ctx)
	{
		if (Override != null)
			Override.Stop(ctx);
		else
			Work?.Pause(ctx);
		Override = skill;
		skill.Start(ctx);
	}

	/// <summary>Ask the planner again at the next chance (nightfall, a chest in sight...): the same job goes on, another replaces it.</summary>
	private void RequestReplan(string why)
	{
		replan = true;
		replanWhy = why;
	}

	private static bool SameJob(ISkill a, ISkill b) => a != null && b != null && a.Key != null && a.Key == b.Key;

	/// <summary>
	/// The planner's choice now against the (paused or running) work: the same job keeps its instance, another replaces it.
	/// <paramref name="paused"/>: the work was stopped for an override and is started again if it goes on.
	/// </summary>
	private void ChooseWork(BotContext ctx, bool paused, string why)
	{
		ISkill next = Planner.Next(ctx);
		if (SameJob(next, Work)) {
			if (paused) {
				ctx.Log.Info($"Auto: back to {Work.Name} after {why}");
				Work.Resume(ctx);
			}
			return;
		}
		ctx.Log.Info($"Auto: {Planner.Status}{(next != null ? " -> " + next.Name : "")} (after {why}, instead of {(paused ? "going back to" : "going on with")} {Work.Name})");
		if (!paused)
			Work.Stop(ctx);
		Work = next;
		next?.Start(ctx);
	}

	/// <summary>Hurt this badly with no potion to drink and enemies about: dig in and recover instead of fighting on.</summary>
	public const float RetreatBelow = 0.3f;
	/// <summary>With <see cref="CrowdSize"/> or more hostiles within <see cref="CrowdTiles"/>, retreat already below this.</summary>
	public const float CrowdRetreatBelow = 0.5f;
	public const int CrowdSize = 2, CrowdTiles = 8;
	/// <summary>
	/// With <see cref="MobSize"/> or more within <see cref="CrowdTiles"/>, retreat below this already: seen in game (world_eye
	/// runs 61 and 63), at 50% among 6-8 slimes in a cave, the bot was dead before it got away.
	/// </summary>
	public const float MobRetreatBelow = 0.7f;
	/// <summary>Retreat while life still covers this many of the biggest recent hit.</summary>
	public const float HitsInHand = 2.5f;
	public const int MobSize = 4;

	/// <summary>
	/// Life fraction below which the bot retreats to dig in. Earlier against a crowd: seen in game, digging in at 30%
	/// among three slimes or bats, it died before the hole was deep enough, four times in one run.
	/// </summary>
	public static float RetreatThreshold(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		int near = 0;
		foreach (EntityInfo h in ctx.Hostiles)
			if (h.DistanceTo(cx, cy) <= CrowdTiles * 16f)
				near++;
		float byCount = near >= MobSize ? MobRetreatBelow : near >= CrowdSize ? CrowdRetreatBelow : RetreatBelow;
		// Hit hard: away while a few more such hits can still be taken. Seen in game (world_eye run 68): zombies hitting for
		// 20 with no armour, the retreat at 30% was two hits from death, and the next hit came before the bot got away.
		float byHits = ctx.Combat.LifeMax > 0 ? HitsInHand * ctx.BiggestRecentHit / (float)ctx.Combat.LifeMax : 0f;
		return System.Math.Min(MobRetreatBelow, System.Math.Max(byCount, byHits));
	}
	/// <summary>Tiles round a spot where the bot ran out of breath that it no longer mines.</summary>
	public const int DrownAvoidRadius = 6;
	/// <summary>How often (ticks) the brain checks for accessories to put on and chests in sight.</summary>
	public const int HabitEvery = 60;

	/// <summary>
	/// An eater of souls or a devourer in sight in weak gear: the evil biome is there, so the spot becomes a danger zone,
	/// as a player knows a biome by its monsters too. Seen in game (runs 47 and 49): three deaths to them on the
	/// corruption's edge west of the spawn, where too few of the seen tiles were corruption for the tile count.
	/// </summary>
	/// <summary>
	/// Before giving up for good: once, dig through what is normally unsafe (sand overhead, water beside) and start the
	/// rests again, like a trapped player who digs out regardless.
	/// </summary>
	private bool TryDigAnyway(BotContext ctx)
	{
		if (ctx.DigAnyway)
			return false;
		ctx.DigAnyway = true;
		backoffs = 0;
		failures = 0;
		ctx.Log.Warn("Auto: still stuck after all the rests; digging through sand and beside water anyway (not lava)");
		return true;
	}

	private static void LogBreathMystery(BotContext ctx)
	{
		TilePos hn = ctx.Node;
		ctx.Log.Warn($"Breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} falling but the head cells read {ctx.Grid.Get(hn.X, hn.Y - 2)}/{ctx.Grid.Get(hn.X + 1, hn.Y - 2)}"
			+ $" (above {ctx.Grid.Get(hn.X, hn.Y - 3)}/{ctx.Grid.Get(hn.X + 1, hn.Y - 3)}) at {hn}");
	}

	/// <summary>Vanilla aiStyle of worms (devourers, giant worms, the eater of worlds): they burrow through blocks.</summary>
	public const int WormAi = 6;
	public const float WormNearPx = 30 * 16f;

	private static bool WormNear(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.AiStyle == WormAi && e.DistanceTo(cx, cy) <= WormNearPx)
				return true;
		return false;
	}

	/// <summary>Hostiles in sight at once (within <see cref="MobZoneTiles"/>) that make a place one to keep away from without armour.</summary>
	public const int MobZoneSize = 5, MobZoneTiles = 20, MobZoneRadius = 25;

	/// <summary>
	/// A crowd of hostiles in weak gear (no armour): somewhere to stay away from for a while, as a player backs off from a
	/// cave full of slimes. Seen in game (world_eye runs 61 and 63): 7-8 slimes in one cave killed the bot twice.
	/// </summary>
	public static void NoteCrowd(BotContext ctx)
	{
		// Not once armed (a broadsword): with one, the slime caves under the spawn are mining ground, and fenced off they left
		// nowhere to mine at all (world_eye run 72). The retreat at 70% among four or more still holds.
		if (ctx.Combat.Defense >= BotContext.CavernDefense || ctx.Combat.WeaponDamage >= BotContext.GoodWeaponDamage || ctx.InDangerZone(ctx.Node))
			return;
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		int n = 0;
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.DistanceTo(cx, cy) <= MobZoneTiles * 16f)
				n++;
		if (n < MobZoneSize)
			return;
		ctx.NoteDanger(ctx.Node, BotContext.DangerZoneTicks, MobZoneRadius, seen: true);
		ctx.Log.Info($"Danger: {n} hostiles about at {ctx.Node}; keeping away from here for now");
	}

	public static void NoteEvilEnemies(BotContext ctx)
	{
		foreach (EntityInfo e in ctx.Hostiles) {
			// The evil biome until iron-level armour, the underground desert (antlions; rolling cacti killed the bot in a
			// desert not yet seen, run 53) until much better gear, as for their tiles (BotContext.OffLimits).
			// Over evil ground only: eaters of souls fly far from it. Seen in game (measured run S15), three over the house marked
			// it for 5 days, and every mining job from the spawn failed "off limits" at once: 153 rests, 1.1 game days.
			// Not on an errand there in full armour (BotContext.EvilErrand): its eaters of souls are what the hunt is for.
			bool evilErrand = ctx.EvilErrand && ctx.Combat.FullArmour;
			string biome = !evilErrand && ctx.Combat.Defense < BotContext.CavernDefense && System.Array.IndexOf(ctx.Ids.EvilEnemies, e.Type) >= 0 && ctx.EvilGroundNear(e.FeetTile) ? "the evil biome"
				: ctx.Combat.Defense < BotContext.DeepDefense && System.Array.IndexOf(ctx.Ids.DesertEnemies, e.Type) >= 0 ? "the desert"
				: null;
			if (biome == null || ctx.InDangerZone(e.FeetTile))
				continue;
			// Seen, not a death: in full armour the zone is smaller and shorter (BotContext.ZoneRadius), and a cost to exploring.
			ctx.NoteDanger(e.FeetTile, BotContext.BiomeZoneTicks, BotContext.BiomeZoneRadius, seen: true);
			ctx.Log.Info($"Danger: a {e.Name} at {e.FeetTile}: {biome} is near; keeping away from there for now ({ctx.ZoneRadius(ctx.DangerZones[^1])} tiles round)");
		}
	}

	/// <summary>
	/// Put on carried accessories while a slot is free (Hermes Boots from a chest, a Band of Regeneration...), but none that
	/// holds the body at the water's surface: the paths wade to flooded floors. Seen in game (measured run S5): an Inner Tube
	/// from a chest, worn, held the bot afloat over a flooded hole at vy -3 (vanilla Player.TryFloatingInFluid) for 278
	/// heartbeats, every walk failing "never landed" and every work bench with it.
	/// </summary>
	private static void WearAccessories(BotContext ctx)
	{
		foreach (int item in ctx.Ids.FloatingAccessories)
			if (ctx.Inventory.IsEquipped(item) && ctx.Crafter.TryTakeOff(item))
				ctx.Log.Info($"Gear: taking off {ctx.Knowledge.ItemName(item)} (it floats the body on water; the paths wade to the floor)");
		foreach (int item in ctx.Inventory.UnwornAccessories())
			if (System.Array.IndexOf(ctx.Ids.FloatingAccessories, item) < 0 && ctx.Crafter.TryEquip(item))
				ctx.Log.Info($"Gear: wearing {ctx.Knowledge.ItemName(item)}");
	}
	/// <summary>
	/// Free slots kept in the bag: one for what is mined or dropped next, one for a chest's best item. Seen in game (measured
	/// run S9): the bag full of dirt, stone, mud, cobwebs and seeds, a mined life crystal could not be picked up (the bot
	/// walked round its drop and gave up), and chests kept Hermes Boots and a Band of Regeneration ("inventory full?").
	/// </summary>
	public const int MinFreeSlots = 2;

	/// <summary>
	/// The bag nearly full: trash stacks worth nothing (vanilla Item.value 0) that nothing the bot does needs, a spare stack
	/// of a building block first (one of each is kept), as AltoClef throws away junk to make room for what it wants. With
	/// nothing worthless left, the stack worth least in all that nothing needs: not gear (tools, weapons, armour, potions,
	/// ammo), not one of the plan's items, not an ingredient of the gear still to make. Seen in game (measured run S11): 50 kinds
	/// of item, each worth a little (gems, seeds, pumpkins, copper tools), and no slot for the bow for 167 heartbeats.
	/// </summary>
	public static void KeepRoom(BotContext ctx)
	{
		if (ctx.Inventory == null || ctx.Knowledge == null || ctx.Crafter == null)
			return;
		for (int guard = 0; guard < MinFreeSlots && ctx.Inventory.FreeSlots < MinFreeSlots; guard++) {
			IReadOnlyList<(int Type, int Stack)> stacks = ctx.Inventory.Stacks();
			HashSet<int> kept = KeptItems(ctx, stacks);
			int junk = -1, junkStacks = 0;
			long cheapest = long.MaxValue;
			int cheap = -1;
			foreach (var g in stacks.GroupBy(s => s.Type)) {
				int n = g.Count();
				bool block = ctx.Ids.BuildingBlocks.Contains(g.Key);
				int value = ctx.Knowledge.ItemValue(g.Key);
				if (block ? n < 2 : kept.Contains(g.Key))
					continue;
				if (value == 0) {
					// Spare block stacks before the rest, then whatever has the most stacks.
					int rank = (block ? 1000 : 0) + n;
					if (rank > junkStacks) {
						junkStacks = rank;
						junk = g.Key;
					}
				}
				else if (!block && (long)value * g.Min(s => s.Stack) < cheapest) {
					cheapest = (long)value * g.Min(s => s.Stack);
					cheap = g.Key;
				}
			}
			int trash = junk >= 0 ? junk : cheap;
			if (trash < 0 || !ctx.Crafter.TryTrash(trash))
				return;
			ctx.Log.Info($"Inventory: {ctx.Inventory.FreeSlots} free slot(s) after trashing a stack of {ctx.Knowledge.ItemName(trash)}"
				+ (junk >= 0 ? " (worth nothing)" : $" (worth {cheapest} copper, the least of anything not needed)"));
		}
	}

	/// <summary>What the bag must keep: gear (tools, weapons, armour, potions, ammo), the plan's own items, and the ingredients (two recipes deep) of the gear to make.</summary>
	private static HashSet<int> KeptItems(BotContext ctx, IReadOnlyList<(int Type, int Stack)> stacks)
	{
		GameIds ids = ctx.Ids;
		var kept = new HashSet<int> { ids.Wood, ids.Gel, ids.Torch, ids.WorkBenchItem, ids.WoodWall, ids.Door, ids.Chair, ids.Table, ids.Acorn,
			ids.WoodenArrow, ids.Lens, ids.LifeCrystal, ids.RecallPotion, ids.Campfire, ids.HeartLantern, ids.SuspiciousEye,
			// The evil boss plan's (Milestone 10): the summon's makings, the Eye's demonite, the teeth for unholy arrows.
			ids.WormFood, ids.VilePowder, ids.VileMushroom, ids.RottenChunk, ids.WormTooth, ids.DemoniteOre, ids.DemoniteBar, ids.Bottle };
		foreach (var s in stacks)
			if (ctx.Inventory.IsGear(s.Type))
				kept.Add(s.Type);
		// The Bottle in the list for its glass (two recipes deep: glass, sand), as a station the summon's powder is made at.
		int[] gear = { ids.IronTierPickaxe, ids.IronTierBroadsword, ids.IronTierHelmet, ids.IronTierChainmail, ids.IronTierGreaves, ids.IronTierBow,
			ids.SilverTierPickaxe, ids.SilverTierBroadsword, ids.SilverTierHelmet, ids.SilverTierChainmail, ids.SilverTierGreaves, ids.SuspiciousEye,
			ids.HeartLantern, ids.Campfire, ids.WoodenArrow, ids.WormFood, ids.DemonBow, ids.UnholyArrow, ids.Bottle };
		var frontier = new List<int>(gear.Where(g => g > 0));
		for (int depth = 0; depth < 2; depth++) {
			var next = new List<int>();
			foreach (int item in frontier)
				foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(item))
					foreach (Ingredient ing in r.Ingredients)
						foreach (int a in ing.Accepts)
							if (kept.Add(a))
								next.Add(a);
			frontier = next;
		}
		foreach (int g in gear)
			kept.Add(g);
		return kept;
	}

	/// <summary>Torches in the dark, so a watcher can see (auto mode).</summary>
	public LightKeeper Light { get; } = new();
	private int deathsSeen;
	private int lastBreath;
	private bool wasNight;
	private bool stuckWork;
	/// <summary>Life to recover to before coming out of a retreat.</summary>
	public const float RetreatUntil = 0.8f;

	public void Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		// After a death the bot is back at the spawn: whatever it was doing (a retreat, a walk) is stale. A boss fight
		// is left to notice it itself ("died").
		if (ctx.Deaths != deathsSeen) {
			deathsSeen = ctx.Deaths;
			if (Current != null && Current is not BossFightSkill) {
				ctx.Log.Info($"Died during {Current.Name}{(Override != null && Work != null ? $" (paused: {Work.Name})" : "")}; starting afresh from {ctx.Node}");
				LastResult = $"{Current.Name} Failed: died";
				if (Auto) {
					if (Override != null)
						Planner.OnSkillFinished(Override, SkillStatus.Failed, ctx);
					if (Work != null)
						Planner.OnSkillFinished(Work, SkillStatus.Failed, ctx);
				}
				Interrupt(ctx);
			}
		}
		// Losing breath without the head cells reading as liquid (world_eye run 52: drowned from full life during a mine
		// with no surfacing at all): say what the head cells are, to find out why.
		if (ctx.Tick % 60 == 0) {
			// Falling since the last check, not coming back after leaving the water.
			bool breathFalling = ctx.Combat.Breath < lastBreath;
			lastBreath = ctx.Combat.Breath;
			if (Auto && breathFalling && ctx.Combat.BreathMax > 0 && ctx.Combat.Breath < ctx.Combat.BreathMax / 2 && !SurfaceSkill.HeadUnder(ctx))
				LogBreathMystery(ctx);
		}
		// Out of breath under water comes before everything: drowning skips the hurt event and kills in seconds.
		// Or sooner, when the way up would take more than half the breath left: seen in game (measured run O1), surfacing at
		// 3/4 breath with air 22 rows up, and drowned on the way.
		if (Auto && Current is not SurfaceSkill && ctx.Combat.BreathMax > 0 && ctx.Combat.Breath < ctx.Combat.BreathMax && SurfaceSkill.HeadUnder(ctx)
			&& (ctx.Combat.Breath < ctx.Combat.BreathMax * 3 / 4 || ctx.Tick % 10 == 0 && ctx.Combat.Breath * (long)SurfaceSkill.TicksPerBreath < 2L * SurfaceSkill.EscapeUp(ctx).Ticks)) {
			ctx.Log.Info($"Danger: breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} under water; surfacing{(Current != null ? $" (pausing {Current.Name})" : "")}");
			// No more marking the water's surroundings unreachable (13x13 tiles each time): surfacing works (no drownings in
			// runs N1, N2), and ore and crystals by water are worth a breath, as for a player (user, 2026-10-14).
			ctx.Explored.Add(ctx.Node);
			BeginOverride(new SurfaceSkill(), ctx);
		}
		// Seen on Classic: demon eyes wore the bot down from 200 to 0 while it fought on without potions. A player
		// would hide and regenerate; demon eyes cannot get through blocks.
		// A worm (devourer, giant worm) digs through blocks, so a hole is no hiding place: home at any distance, or fight on.
		// Seen in game (run 50): dug in beside a devourer, killed in the hole.
		bool wormNear = false;
		TilePos? retreatHome = null;
		if (Auto && Current is not BossFightSkill && Current is not ShelterSkill { Retreat: true } && Current is not SurfaceSkill && Current is not StayHomeSkill
			&& ctx.Combat.LifeMax > 0 && ctx.Combat.LifeFraction < RetreatThreshold(ctx) && !ctx.Combat.CanQuickHeal && ProgressionPlanner.InDanger(ctx)
			&& ((retreatHome = Planner.HomeToRetreatTo(ctx, anyDistance: wormNear = WormNear(ctx))) != null || !wormNear)) {
			ctx.Log.Info($"Danger: life {ctx.Combat.Life}/{ctx.Combat.LifeMax} and no potion; retreating{(Current != null ? $" from {Current.Name}" : "")}");
			// Home if it is near (enemies cannot get through the door), else dig in where it is.
			if (retreatHome is TilePos home)
				BeginOverride(new StayHomeSkill(home) { RecoverTo = RetreatUntil }, ctx);
			else
				BeginOverride(ShelterSkill.Recover(RetreatUntil), ctx);
		}
		// Nightfall: work started by day (a walk back to the spawn, a mine near the surface) is planned again, so the night
		// rule can send the bot home. Seen in game (world_eye run 68): a walk begun by day came up at night among zombies,
		// and one killed the bot (wooden sword, no armour).
		bool night = ctx.IsNight;
		if (Auto && night && !wasNight && Work is WalkToSkill or MineSkill or ExploreSkill or GatherWoodSkill
			&& (ctx.Combat.Defense < ProgressionPlanner.NightOutDefense || Planner.NightHuntDue(ctx)))
			RequestReplan("nightfall");
		wasNight = night;
		if (Auto && ctx.Tick % HabitEvery == 0)
			WatchPlace(ctx);
		if (Auto && ctx.Tick % HabitEvery == 0) {
			KeepRoom(ctx);
			WearAccessories(ctx);
			NoteEvilEnemies(ctx);
			if (ctx.Node.Y > ctx.SurfaceY)
				NoteCrowd(ctx); // underground: on the surface a night's zombies are the house's business
			// A chest comes into sight while looking for ore or prey: go and loot it first (the planner picks it next).
			// On a walk too (a trip, to a station): seen in game, the bot ran past a life crystal on its way (user report,
			// 2026-10-14). Not during a reflex (a flight, a fight).
			bool onAWalk = Override == null && Work is WalkToSkill;
			if ((Current is MineSkill or ExploreSkill or HuntSkill || onAWalk) && ProgressionPlanner.NextChest(ctx) is ChestInfo seen)
				RequestReplan($"a chest at {seen.TopLeft} came into sight");
			// A life crystal: whatever the work (building, gathering, crafting, going home...), only not during a reflex.
			else if (Override == null && Work != null && !(Work is MineSkill m && m.ItemType == ctx.Ids.LifeCrystal)) {
				if (ProgressionPlanner.LifeCrystalUseful(ctx) && ProgressionPlanner.SeenLifeCrystal(ctx) is TilePos heart)
					RequestReplan($"a life crystal at {heart} came into sight");
			}
		}
		// In the house for the night, zombies and demon eyes outside cannot get in: only what actually hits the bot is
		// fought. Seen in game: out of the house three times in one night to fight zombies at the door; the third killed it.
		// On the way home for the night, keep going: zombies are slower than a running player and cannot open the door
		// (seen in game, world_eye run 41: stopped at dusk to fight zombies with a copper sword, then dug in among them, dead).
		// Only on the last stretch, and only what is not hitting it: walking without fighting back at all, zombies killed it
		// 160 tiles out, and twice more just below the spawn (world_eye run 46).
		// Running home hurt is a flight: hit or not it keeps going (fighting would hand it straight back to the retreat).
		bool safeAtHome = Current is StayHomeSkill { Home: true } && ctx.RecentAttacker < 0
			// A hurt flight at any distance (with a worm near the retreat is home from anywhere: seen in game, world_eye run 69,
			// 120 tiles out the fight took it back every tick and the retreat gave it up again, until a devourer killed the bot).
			|| Current is StayHomeSkill { Home: false, RecoverTo: > 0 }
			|| Current is StayHomeSkill { Home: false } walking && walking.Inside.ChebyshevTo(ctx.Node) <= ProgressionPlanner.RetreatHomeTiles
				&& ctx.RecentAttacker < 0;
		if (Auto && !safeAtHome && Current is not FightSkill && Current is not ShelterSkill { Retreat: true } && Current is not SurfaceSkill && Current is not BossFightSkill && Current is not ShelterSkill { Sealed: true } && ProgressionPlanner.InDanger(ctx, toFight: true)) {
			if (Current != null)
				ctx.Log.Info($"Danger: pausing {Current.Name} to fight");
			BeginOverride(new FightSkill(), ctx);
		}
		// Work asked to be planned again (nightfall, a chest in sight): only between reflexes, and the same job goes on.
		if (replan && Override == null && Work != null && Auto) {
			replan = false;
			ChooseWork(ctx, paused: false, replanWhy);
		}
		if (Current == null && Target != null)
			Begin(new WalkToSkill(Target.Value), ctx);
		if (Current == null && Auto && ctx.Tick < backoffUntil) {
			// Resting after failures in a row (danger and breath are still handled above). First hop sideways a little, a
			// new way each rest: seen in game, the bot sat on a spot the planner could not start from, and all five rests
			// failed from the same tile.
			if (backoffUntil - ctx.Tick > BackoffTicks - UnstickTicks) {
				ctx.Output = MoveInput.None;
				ctx.Output.Horizontal = backoffs % 2 == 0 ? 1 : -1;
				ctx.Output.Jump = (ctx.Tick / 15) % 2 == 0;
			}
			return;
		}
		if (Current == null && Auto) {
			ISkill next = Planner.Next(ctx);
			if (next == null) {
				if (Planner.Complete && LastResult != "auto: done") {
					ctx.Log.Info("Auto: " + Planner.Status);
					LastResult = "auto: done";
				}
				else if (Planner.Stuck) {
					// Not the end: rest and try again with the given-up sites forgotten (seen in game: "no building site"
					// stopped a run that had just made its iron pickaxe). Only after the rests run out does auto mode stop.
					LastResult = "auto: " + Planner.Status;
					if (++backoffs > MaxBackoffs && !TryDigAnyway(ctx)) {
						ctx.Log.Warn("Auto: " + Planner.Status + $"; still stuck after {MaxBackoffs} rests, stopping auto mode");
						Auto = false;
					}
					else {
						ctx.Log.Warn("Auto: " + Planner.Status + $"; resting {BackoffTicks} ticks and trying again ({backoffs}/{MaxBackoffs})");
						Planner.ForgetGivenUp();
						backoffUntil = ctx.Tick + BackoffTicks;
					}
				}
				return;
			}
			ctx.Log.Info($"Auto: {Planner.Status} -> {next.Name}");
			Begin(next, ctx);
		}
		if (Current == null)
			return;

		SkillStatus status = Current.Tick(ctx);
		if (status == SkillStatus.Running && WorkStuck(ctx)) {
			ctx.Log.Warn($"Auto: no progress in {NoProgressTicks / 3600} game hours ({Work.Describe()} at {ctx.Node}); giving the job up");
			stuckWork = true;
			status = SkillStatus.Failed;
		}
		// Not while building (the house and arena place their own torches, and the keeper's took the arena's: boss_auto
		// then hunted gel for an hour) or in a boss fight.
		if (Auto && Current is not Building.BuildSkill && Current is not BossFightSkill)
			Light.Tick(ctx);
		if (status == SkillStatus.Running)
			return;

		ISkill skill = Current;
		string why = stuckWork ? $"no progress in {NoProgressTicks / 3600} game hours" : skill.FailureReason;
		stuckWork = false;
		watched = null;
		LastResult = $"{skill.Name} {status}" + (status == SkillStatus.Failed && why.Length > 0 ? $": {why}" : "");
		ctx.Log.Info($"Skill finished: {LastResult}");
		bool wasFight = skill is FightSkill;
		if (Auto)
			Planner.OnSkillFinished(skill, status, ctx);
		skill.Stop(ctx);
		if (Override != null) {
			// A reflex is over: its loot, then back to the paused work if the planner still wants it. Reflexes do not count
			// as failures or successes of the work.
			Override = null;
			if (Auto && wasFight && CollectDropsSkill.AnyInRange(ctx, _ => true, LootRadiusTiles)) {
				ctx.Log.Info("Auto: picking up loot after the fight");
				BeginOverride(new CollectDropsSkill(_ => true, LootRadiusTiles), ctx);
				return;
			}
			if (Work != null) {
				if (Auto)
					ChooseWork(ctx, paused: true, skill.Name.ToLowerInvariant());
				else
					Work.Resume(ctx);
			}
			return;
		}
		Work = null;
		Target = null;
		// A boss down: its loot first, before the plan moves on, as after any fight. The Eye of Cthulhu drops 30-90 demonite
		// ore and 20-50 unholy arrows in a Classic Corruption world (ItemDropDatabase.cs:498-500), the makings of the Demon
		// Bow the evil boss plan wants; items on the ground are not saved with the world, so left lying they are lost.
		if (Auto && skill is BossFightSkill && status == SkillStatus.Succeeded && CollectDropsSkill.AnyInRange(ctx, _ => true, BossLootRadiusTiles)) {
			ctx.Log.Info("Auto: picking up the boss's loot");
			BeginOverride(new CollectDropsSkill(_ => true, BossLootRadiusTiles), ctx);
		}
		if (Auto) {
			failures = status == SkillStatus.Failed ? failures + 1 : 0;
			if (status == SkillStatus.Succeeded) {
				backoffs = 0;
				ctx.DigAnyway = false;
			}
			if (failures >= MaxConsecutiveFailures) {
				if (++backoffs > MaxBackoffs && !TryDigAnyway(ctx)) {
					// Not the end of the game: seen in game (measured run S0), auto mode stopped at day 6 of 10 with the bot afloat in
					// a pool, four game days lost. Home by a recall potion if there is one, and rest on.
					backoffs = MaxBackoffs;
					bool recall = ctx.SpawnTile is TilePos home && home.ChebyshevTo(ctx.Node) > ProgressionPlanner.RecallDistance && Planner.RequestRecall(ctx);
					ctx.Log.Warn($"Auto: {failures} failures in a row after {MaxBackoffs} rests ({LastResult}); {(recall ? "drinking a recall potion and " : "")}resting on, not stopping");
					failures = 0;
					backoffUntil = ctx.Tick + BackoffTicks * 2;
					ctx.UnreachableTiles.Clear();
				}
				else {
					ctx.Log.Warn($"Auto: {failures} failures in a row ({LastResult}); resting {BackoffTicks} ticks, forgetting what was unreachable (not the steps that failed 3 times, nor the tiles mining gave up on: those are facts that fade), then trying again ({backoffs}/{MaxBackoffs})");
					failures = 0;
					backoffUntil = ctx.Tick + BackoffTicks;
					ctx.UnreachableTiles.Clear();
					ctx.NoPlace.Clear();
					ctx.NoBreak.Clear();
					ctx.ActionFailures.Clear();
					// Walks failing in a row far from home with a recall potion in the bag: drink it, as a player stuck in a
					// shaft would. Seen in game (world_eye run 62): 120 rows down, every walk to the surface failed on the same
					// steps for a game hour, with 3 recall potions carried.
					if (skill is WalkToSkill && ctx.SpawnTile is TilePos spawn && spawn.ChebyshevTo(ctx.Node) > ProgressionPlanner.RecallDistance
						&& Planner.RequestRecall(ctx))
						ctx.Log.Info($"Auto: walks keep failing {spawn.ChebyshevTo(ctx.Node)} tiles from the spawn; drinking a recall potion next");
				}
			}
		}
	}

	private void Begin(ISkill skill, BotContext ctx)
	{
		Work = skill;
		skill.Start(ctx);
	}

	public string Describe() =>
		Current != null ? (Auto ? "auto: " : "") + Current.Describe()
		: Target != null ? $"target {Target.Value} (idle)"
		: Auto ? "auto: " + Planner.Status
		: LastResult.Length > 0 ? $"idle - last: {LastResult}"
		: "idle - no target";
}
