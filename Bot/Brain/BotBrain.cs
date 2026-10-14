using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Chooses what to do and sequences skills.
/// <list type="bullet">
/// <item>Manual: one movement target (<see cref="SetTarget"/>) or one explicit skill (<see cref="StartSkill"/>).</item>
/// <item>Auto (<see cref="SetAuto"/>): asks <see cref="ProgressionPlanner"/> for the next skill whenever nothing runs,
/// and pre-empts anything but a fight (or a sealed shelter) when a hostile comes close; after a fight it picks up
/// the loot, then re-plans from the new situation.
/// A skill that fails three times in a row stops auto mode, so a stuck goal cannot loop forever.</item>
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
	public ISkill Current { get; private set; }
	public string LastResult { get; private set; } = "";
	public bool Auto { get; private set; }
	public ProgressionPlanner Planner { get; } = new();
	/// <summary>Pick up what a fight dropped (gel, coins...) before going back to the plan.</summary>
	public const int LootRadiusTiles = 15;

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
		Current = skill;
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

	/// <summary>Stop the running skill but keep the target/mode (bot switched off; resumes with a fresh plan).</summary>
	public void Interrupt(BotContext ctx)
	{
		Current?.Stop(ctx);
		Current = null;
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
		if (ctx.Combat.Defense >= BotContext.CavernDefense || ctx.InDangerZone(ctx.Node))
			return;
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		int n = 0;
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.DistanceTo(cx, cy) <= MobZoneTiles * 16f)
				n++;
		if (n < MobZoneSize)
			return;
		ctx.NoteDanger(ctx.Node, BotContext.DangerZoneTicks, MobZoneRadius);
		ctx.Log.Info($"Danger: {n} hostiles about at {ctx.Node}; keeping away from here for now");
	}

	public static void NoteEvilEnemies(BotContext ctx)
	{
		foreach (EntityInfo e in ctx.Hostiles) {
			// The evil biome until iron-level armour, the underground desert (antlions; rolling cacti killed the bot in a
			// desert not yet seen, run 53) until much better gear, as for their tiles (BotContext.OffLimits).
			string biome = ctx.Combat.Defense < BotContext.CavernDefense && System.Array.IndexOf(ctx.Ids.EvilEnemies, e.Type) >= 0 ? "the evil biome"
				: ctx.Combat.Defense < BotContext.DeepDefense && System.Array.IndexOf(ctx.Ids.DesertEnemies, e.Type) >= 0 ? "the desert"
				: null;
			if (biome == null || ctx.InDangerZone(e.FeetTile))
				continue;
			ctx.NoteDanger(e.FeetTile, BotContext.BiomeZoneTicks, BotContext.BiomeZoneRadius);
			ctx.Log.Info($"Danger: a {e.Name} at {e.FeetTile}: {biome} is near; keeping away from there for now");
		}
	}

	/// <summary>Put on carried accessories while a slot is free (Hermes Boots from a chest, a Band of Regeneration...).</summary>
	private static void WearAccessories(BotContext ctx)
	{
		foreach (int item in ctx.Inventory.UnwornAccessories())
			if (ctx.Crafter.TryEquip(item))
				ctx.Log.Info($"Gear: wearing {ctx.Knowledge.ItemName(item)}");
	}
	/// <summary>Torches in the dark, so a watcher can see (auto mode).</summary>
	public LightKeeper Light { get; } = new();
	private int deathsSeen;
	private int lastBreath;
	private bool wasNight;
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
				ctx.Log.Info($"Died during {Current.Name}; starting afresh from {ctx.Node}");
				LastResult = $"{Current.Name} Failed: died";
				if (Auto)
					Planner.OnSkillFinished(Current, SkillStatus.Failed, ctx);
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
		if (Auto && Current is not SurfaceSkill && ctx.Combat.BreathMax > 0 && ctx.Combat.Breath < ctx.Combat.BreathMax * 3 / 4 && SurfaceSkill.HeadUnder(ctx)) {
			ctx.Log.Info($"Danger: breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} under water; surfacing{(Current != null ? $" (pausing {Current.Name})" : "")}, and avoiding this water from now on");
			// Remember the spot: no ore around here, no exploring legs here (seen in game: back to the same flooded cave until it drowned).
			TilePos here = ctx.Node;
			for (int dx = -DrownAvoidRadius; dx <= DrownAvoidRadius; dx++)
				for (int dy = -DrownAvoidRadius; dy <= DrownAvoidRadius; dy++)
					ctx.UnreachableTiles.Add(new TilePos(here.X + dx, here.Y + dy));
			ctx.Explored.Add(here);
			Interrupt(ctx);
			Begin(new SurfaceSkill(), ctx);
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
			Interrupt(ctx);
			// Home if it is near (enemies cannot get through the door), else dig in where it is.
			if (retreatHome is TilePos home)
				Begin(new StayHomeSkill(home) { RecoverTo = RetreatUntil }, ctx);
			else
				Begin(ShelterSkill.Recover(RetreatUntil), ctx);
		}
		// Nightfall: work started by day (a walk back to the spawn, a mine near the surface) is planned again, so the night
		// rule can send the bot home. Seen in game (world_eye run 68): a walk begun by day came up at night among zombies,
		// and one killed the bot (wooden sword, no armour).
		bool night = ctx.IsNight;
		if (Auto && night && !wasNight && Current is WalkToSkill or MineSkill or ExploreSkill or GatherWoodSkill
			&& ctx.Combat.Defense < ProgressionPlanner.NightOutDefense) {
			ctx.Log.Info($"Auto: nightfall; planning again instead of going on with {Current.Name}");
			Interrupt(ctx);
		}
		wasNight = night;
		if (Auto && ctx.Tick % HabitEvery == 0) {
			WearAccessories(ctx);
			NoteEvilEnemies(ctx);
			if (ctx.Node.Y > ctx.SurfaceY)
				NoteCrowd(ctx); // underground: on the surface a night's zombies are the house's business
			// A chest comes into sight while looking for ore or prey: go and loot it first (the planner picks it next).
			if (Current is MineSkill or ExploreSkill or HuntSkill && ProgressionPlanner.NextChest(ctx) is ChestInfo seen) {
				ctx.Log.Info($"Auto: a chest at {seen.TopLeft} came into sight; pausing {Current.Name} to loot it");
				Interrupt(ctx);
			}
			else if (Current is ExploreSkill or HuntSkill || Current is MineSkill m && m.ItemType != ctx.Ids.LifeCrystal) {
				if (ProgressionPlanner.LifeCrystalUseful(ctx) && ProgressionPlanner.SeenLifeCrystal(ctx) is TilePos heart) {
					ctx.Log.Info($"Auto: a life crystal at {heart} came into sight; pausing {Current.Name} to get it");
					Interrupt(ctx);
				}
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
			Interrupt(ctx);
			Begin(new FightSkill(), ctx);
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
		// Not while building (the house and arena place their own torches, and the keeper's took the arena's: boss_auto
		// then hunted gel for an hour) or in a boss fight.
		if (Auto && Current is not Building.BuildSkill && Current is not BossFightSkill)
			Light.Tick(ctx);
		if (status == SkillStatus.Running)
			return;

		string why = Current.FailureReason;
		LastResult = $"{Current.Name} {status}" + (status == SkillStatus.Failed && why.Length > 0 ? $": {why}" : "");
		ctx.Log.Info($"Skill finished: {LastResult}");
		bool wasFight = Current is FightSkill;
		ISkill skill = Current;
		if (Auto)
			Planner.OnSkillFinished(Current, status, ctx);
		Current.Stop(ctx);
		Current = null;
		Target = null;
		if (Auto && wasFight && CollectDropsSkill.AnyInRange(ctx, _ => true, LootRadiusTiles)) {
			ctx.Log.Info("Auto: picking up loot after the fight");
			Begin(new CollectDropsSkill(_ => true, LootRadiusTiles), ctx);
			return;
		}
		if (Auto) {
			failures = status == SkillStatus.Failed ? failures + 1 : 0;
			if (status == SkillStatus.Succeeded && !wasFight) {
				backoffs = 0;
				ctx.DigAnyway = false;
			}
			if (failures >= MaxConsecutiveFailures) {
				if (++backoffs > MaxBackoffs && !TryDigAnyway(ctx)) {
					ctx.Log.Warn($"Auto: {failures} failures in a row after {MaxBackoffs} rests, stopping auto mode ({LastResult})");
					Auto = false;
				}
				else {
					ctx.Log.Warn($"Auto: {failures} failures in a row ({LastResult}); resting {BackoffTicks} ticks, forgetting what was unreachable (not the steps that failed 3 times: those are facts), then trying again ({backoffs}/{MaxBackoffs})");
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
		Current = skill;
		skill.Start(ctx);
	}

	public string Describe() =>
		Current != null ? (Auto ? "auto: " : "") + Current.Describe()
		: Target != null ? $"target {Target.Value} (idle)"
		: Auto ? "auto: " + Planner.Status
		: LastResult.Length > 0 ? $"idle - last: {LastResult}"
		: "idle - no target";
}
