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
	/// <summary>
	/// The errand that owns the bot (docs ERRAND_DESIGN.md; ProgressionPlanner.Schedulers.Errands): its skills are the work, the
	/// planner is not asked while it runs, a reflex pauses its skill and hands back to it, optional work is offered to it with a
	/// detour cost (<see cref="OfferOpportunities"/>), a death leaves it in place.
	/// </summary>
	public Errands.Errand Errand { get; private set; }
	/// <summary>Opportunities the errand declined, by key, with the detour cost then: offered again only when much nearer.</summary>
	private readonly Dictionary<string, float> declined = new();
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
		BeginOverride(new WalkToSkill(new Core.Pathfinding.GoalRow(n.Y - 2 * SamePlaceTiles, n.X, ctx.Physics)) { Quiet = true, ToSurface = true }, ctx, ClimbOutBid);
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
	private readonly HashSet<int> lootGivenUp = new();
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
		// An errand loaded from the player's save goes on under the same plan (a run resumed from a checkpoint); otherwise auto mode
		// begins afresh and whatever errand there was is over.
		bool keepErrand = on && Errand != null && Errand.Restored && Errand.Plan == (plan ?? Planner.Plan);
		Planner.Reset(plan);
		if (keepErrand) {
			Errand.ReserveAgain(ctx);
			Planner.AdoptErrand(Errand);
			ctx.Log.Info($"Errand {Errand.Id} goes on from the save: {Errand.Describe()}");
		}
		else if (Errand != null)
			EndErrand(ctx, Errands.Outcome.Abandoned("auto mode begun afresh"));
		ctx.Reservations.Clear();
		Errand?.ReserveAgain(ctx);
		ctx.UnreachableTiles.Clear();
		LastResult = "";
		ctx.Log.Info($"Auto mode {(on ? "ON" : "OFF")}" + (on ? $", plan '{Planner.Plan}', scheduler {Planner.Scheduler}" : ""));
	}

	/// <summary>An errand loaded with the player (TerraBotPlayer.LoadData): taken up again by <see cref="SetAuto"/> under its plan.</summary>
	public void RestoreErrand(Errands.Errand e) => Errand = e;

	private void StartErrand(Errands.Errand e, BotContext ctx)
	{
		Errand = e;
		e.Plan = Planner.Plan;
		declined.Clear();
		failures = 0;
		backoffs = 0;
		backoffUntil = -1;
		e.Begin(ctx);
		Planner.AdoptErrand(e);
		ctx.Log.Info($"Errand {e.Id} start: {e.Describe()}; owns the bot until its goal holds or it is blocked (reflexes preempt and hand back; a death sends it back to its frontier)");
	}

	/// <summary>The errand is over: its outcome recorded with the planner, its reservations released, its skill dropped.</summary>
	private void EndErrand(BotContext ctx, Errands.Outcome outcome)
	{
		Errands.Errand e = Errand;
		ctx.Log.Info($"Errand {e.Id} ({e.Identity}) ended after {(ctx.Tick - e.StartedTick) / 3600.0:0.0} game hours, {e.Deaths} deaths: {outcome}");
		Planner.OnErrandEnded(e, outcome, ctx);
		e.End(ctx, outcome);
		Errand = null;
		declined.Clear();
		if (Work != null) {
			Work.Stop(ctx);
			Work = null;
		}
		failures = 0;
		LastResult = $"errand {e.Id} {outcome}";
	}

	/// <summary>
	/// Optional work in sight (a chest, a life crystal), offered to the errand with the detour's cost (Opportunity.Detour): accepted,
	/// it runs as a reflex does (the errand's skill paused, resumed after); declined, it is remembered and offered again only once the
	/// detour is less than half what it was. Never during a reflex. No other code path leaves an errand for it.
	/// </summary>
	private readonly ShotDodge deckDodge = new();
	/// <summary>Dodges planned on the errand's deck outside a fight (<see cref="ShotDodge"/>).</summary>
	public int DeckDodges => deckDodge.Dodges;

	private void OfferOpportunities(BotContext ctx)
	{
		if (Override != null || Work == null || Errand == null)
			return;
		if (ProgressionPlanner.NextChest(ctx) is ChestInfo chest) {
			var o = new Errands.Opportunity(Errands.OpportunityKind.Chest, chest.TopLeft, Errands.Opportunity.Detour(ctx, chest.TopLeft));
			if (Offer(o, ctx))
				BeginOverride(new LootChestSkill(chest.TopLeft), ctx);
		}
		if (Override == null && ProgressionPlanner.LifeCrystalUseful(ctx) && !ctx.Infinite(ctx.Ids.LifeCrystal)
			&& !(Work is MineSkill m && m.ItemType == ctx.Ids.LifeCrystal) && ProgressionPlanner.SeenLifeCrystal(ctx) is TilePos heart) {
			var o = new Errands.Opportunity(Errands.OpportunityKind.LifeCrystal, heart, Errands.Opportunity.Detour(ctx, heart));
			if (Offer(o, ctx))
				BeginOverride(new MineSkill(ctx.Ids.LifeCrystal, ctx.Knowledge.SourceOf(ctx.Ids.LifeCrystal).TileType, ctx.Inventory.Count(ctx.Ids.LifeCrystal) + 1), ctx);
		}
	}

	private bool Offer(in Errands.Opportunity o, BotContext ctx)
	{
		if (declined.TryGetValue(o.Key, out float before) && o.DetourTicks > before / 2f)
			return false;
		if (Errand.Accepts(o, ctx)) {
			declined.Remove(o.Key);
			ctx.Log.Info($"Errand {Errand.Id}: taking the {o.Kind} at {o.Where} (a detour of about {o.DetourTicks:0} ticks), then back to {Work.Name}");
			return true;
		}
		if (!declined.ContainsKey(o.Key))
			ctx.Log.Info($"Errand {Errand.Id}: leaving the {o.Kind} at {o.Where} (a detour of about {o.DetourTicks:0} ticks); it waits for the errand");
		declined[o.Key] = o.DetourTicks;
		return false;
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
	private void BeginOverride(ISkill skill, BotContext ctx, int bid = LootBid)
	{
		if (Override != null)
			Override.Stop(ctx);
		else
			Work?.Pause(ctx);
		Override = skill;
		overrideBid = bid;
		skill.Start(ctx);
	}

	/// <summary>
	/// The reflex table (docs ERRAND_DESIGN.md section 5, migration step 2): the survival responses in one ordered table, each with a
	/// gate (its conditions, as they were in the cascade) and a bid. The highest bid whose gate is open runs as the <see cref="Override"/>;
	/// a running reflex is replaced only by a higher bid (hysteresis: a flight is not cancelled by a fight, a surfacing by nothing), and
	/// a reflex never asks the planner nor ends an errand. Loot after a fight and the climb-out bid lowest, so any reflex replaces them.
	/// </summary>
	private readonly record struct Reflex(string Name, System.Func<BotContext, int> Bid, System.Func<BotContext, ISkill> Fire);
	public const int SurfaceBid = 100, GuardianBid = 90, CaughtBid = 75, RetreatBid = 70, FightBid = 60, ClimbOutBid = 50, LootBid = 10;
	private int overrideBid;
	private Reflex[] reflexes;

	private Reflex[] Reflexes => reflexes ??= new[] {
		new Reflex("surface", _ => SurfaceBid, SurfaceGate),
		new Reflex("guardian", _ => GuardianBid, GuardianGate),
		new Reflex("retreat", _ => RetreatBid, RetreatGate),
		new Reflex("fight", ctx => FightBackReason(ctx) != null ? CaughtBid : FightBid, FightGate),
	};

	/// <summary>One pass of the table: the first (highest) reflex whose gate opens and whose bid beats the running override's.</summary>
	private void TickReflexes(BotContext ctx)
	{
		if (!Auto)
			return;
		foreach (Reflex r in Reflexes) {
			int bid = r.Bid(ctx);
			if (Override != null && bid <= overrideBid)
				continue;
			ISkill s = r.Fire(ctx);
			if (s == null)
				continue;
			BeginOverride(s, ctx, bid);
			if (r.Name == "fight")
				fightBackFight = fightBackPending ? Override : null;
			return;
		}
	}

	/// <summary>
	/// Out of breath under water comes before everything: drowning skips the hurt event and kills in seconds. Or sooner, when the way
	/// up would take more than half the breath left: seen in game (measured run O1), surfacing at 3/4 breath with air 22 rows up, and
	/// drowned on the way.
	/// </summary>
	private ISkill SurfaceGate(BotContext ctx)
	{
		if (Current is SurfaceSkill || ctx.Combat.BreathMax <= 0 || ctx.Combat.Breath >= ctx.Combat.BreathMax || !SurfaceSkill.HeadUnder(ctx))
			return null;
		if (!(ctx.Combat.Breath < ctx.Combat.BreathMax * 3 / 4 && Current is not HellevatorSkill { DiggingStraight: true }
			|| ctx.Tick % 10 == 0 && ctx.Combat.Breath * (long)SurfaceSkill.TicksPerBreath < 2L * SurfaceSkill.EscapeUp(ctx).Ticks))
			return null;
		ctx.Log.Info($"Danger: breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} under water; surfacing{(Current != null ? $" (pausing {Current.Name})" : "")}");
		// No more marking the water's surroundings unreachable (13x13 tiles each time): surfacing works (no drownings in
		// runs N1, N2), and ore and crystals by water are worth a breath, as for a player (user, 2026-10-14).
		ctx.Explored.Add(ctx.Node);
		return new SurfaceSkill();
	}

	/// <summary>
	/// A Dungeon Guardian in sight (docs M11_SPEC.md section 4): it cannot be fought (9999 defence and life, so every hit does 1,
	/// NPC.cs:321, 4796-4810) nor outrun (8 px/tick through blocks, 1000 contact damage, NPC.cs:22462-22475). A recall potion at
	/// once, as a player drinks one; never a fight with it (it is left out of them, <see cref="BotContext.IgnoreEvenClose"/>). In
	/// a boss fight the fight's own escape drinks it (BossFightSkill). With no potion there is nothing to do but go on.
	/// </summary>
	private ISkill GuardianGate(BotContext ctx)
	{
		int guardian = ctx.Ids.DungeonGuardian;
		if (guardian <= 0)
			return null;
		foreach (EntityInfo e in ctx.Hostiles) {
			if (e.Type != guardian)
				continue;
			ctx.IgnoreEvenClose(e.Id, GuardianIgnoreTicks);
			if (Current is UseItemSkill u && u.ItemType == ctx.Ids.RecallPotion || Current is BossFightSkill)
				return null;
			bool recall = ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0;
			if (guardiansSeen.Add(e.Id) || recall)
				ctx.Log.Warn($"Danger: a Dungeon Guardian at {e.FeetTile}, us at {ctx.Node}: {(recall ? "drinking a recall potion" : "no recall potion to escape it with")}"
					+ (Current != null ? $" (was {Current.Name})" : ""));
			return recall ? new UseItemSkill(ctx.Ids.RecallPotion) : null;
		}
		return null;
	}

	/// <summary>
	/// Hurt with no potion and enemies about: hide and regenerate, as a player would (seen on Classic: demon eyes wore the bot down
	/// from 200 to 0 while it fought on without potions; they cannot get through blocks). Home if it is near (enemies cannot get
	/// through the door), else dig in where it is. A worm (devourer, giant worm) digs through blocks, so a hole is no hiding place:
	/// home if it is near, else fight on (run 50: dug in beside a devourer, killed in the hole). Not home from any distance: a worm
	/// is faster than a walk (measured run M10e: flights home of 300 tiles from the corruption, 3 of the run's 5 deaths).
	/// </summary>
	private ISkill RetreatGate(BotContext ctx)
	{
		if (Current is BossFightSkill || Current is ShelterSkill { Retreat: true } || Current is SurfaceSkill || Current is StayHomeSkill)
			return null;
		if (RetreatHeldOff(ctx) || ctx.Combat.LifeMax <= 0 || ctx.Combat.LifeFraction >= RetreatThreshold(ctx) || !NoPotion(ctx) || !ProgressionPlanner.InDanger(ctx))
			return null;
		if (ctx.Combat.HasRanged && FastFlierNear(ctx))
			return null;
		TilePos? retreatHome = Planner.HomeToRetreatTo(ctx);
		if (retreatHome == null && WormNear(ctx))
			return null;
		ctx.Log.Info($"Danger: life {ctx.Combat.Life}/{ctx.Combat.LifeMax} and {(ctx.Combat.PotionSickTicks > 0 ? "potion sick" : "no potion")}; retreating{(Current != null ? $" from {Current.Name}" : "")}");
		return retreatHome is TilePos home ? new StayHomeSkill(home) { RecoverTo = RetreatUntil } : ShelterSkill.Recover(RetreatUntil);
	}

	/// <summary>A retreat (or a sealed hole) caught: hit, or something within a swing (ShelterSkill.FightBackReason); the fight's bid rises over the retreat's.</summary>
	private string FightBackReason(BotContext ctx) =>
		Current is ShelterSkill caught ? caught.FightBackReason(ctx) : Current is StayHomeSkill fleeing ? fleeing.FightBackReason(ctx) : null;

	private bool fightBackPending;

	/// <summary>
	/// In the house for the night, zombies and demon eyes outside cannot get in: only what actually hits the bot is fought (seen in
	/// game: out of the house three times in one night to fight zombies at the door; the third killed it). On the way home for the
	/// night, keep going: zombies are slower than a running player and cannot open the door (world_eye run 41); only on the last
	/// stretch, and only what is not hitting it (world_eye run 46). Running home hurt is a flight: hit or not it keeps going. A
	/// retreat (or a sealed hole) is not stopped for every hostile in sight, only when it is caught; afterwards the retreat is
	/// decided afresh, not before FightBackHoldTicks (RetreatHeldOff).
	/// </summary>
	private ISkill FightGate(BotContext ctx)
	{
		fightBackPending = false;
		if (Current is FightSkill || Current is SurfaceSkill || Current is BossFightSkill)
			return null;
		string fightBack = FightBackReason(ctx);
		bool safeAtHome = Current is StayHomeSkill { Home: true } && ctx.RecentAttacker < 0
			// A hurt flight at any distance (with a worm near the retreat is home from anywhere: seen in game, world_eye run 69,
			// 120 tiles out the fight took it back every tick and the retreat gave it up again, until a devourer killed the bot).
			|| Current is StayHomeSkill { Home: false, RecoverTo: > 0 } && fightBack == null
			|| Current is StayHomeSkill { Home: false } walking && walking.Inside.ChebyshevTo(ctx.Node) <= ProgressionPlanner.RetreatHomeTiles
				&& ctx.RecentAttacker < 0 && fightBack == null;
		bool shelterHolds = Current is ShelterSkill { Retreat: true } or ShelterSkill { Sealed: true } && fightBack == null;
		if (safeAtHome || shelterHolds || !ProgressionPlanner.InDanger(ctx, toFight: true))
			return null;
		if (fightBack != null) {
			ctx.Log.Info($"Danger: {fightBack}; fighting back");
			fightBackSince = ctx.Tick;
			fightBackPending = true;
		}
		else if (Current != null)
			ctx.Log.Info($"Danger: pausing {Current.Name} to fight");
		return new FightSkill { Deck = Errand?.FightDeck(ctx) };
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
	/// <summary>
	/// Potion sick (it has just healed) with at least this fraction of life, and more life than the hardest recent hit: not
	/// "no potion", the fight goes on. Seen in game (measured runs U14 and U19): five retreats began right after a quick-heal,
	/// with "no potion" true only for the sickness, at 53%, 52%, 59%, 49% and 66% life (U19: 132/200 from a fight with
	/// one spore skeleton), and all five died in the shelter. Half life keeps the four at 52% and over fighting, and is the
	/// crowd's retreat threshold (<see cref="CrowdRetreatBelow"/>); the fifth (78/160, held in a web) is the fight-back's.
	/// </summary>
	public const float PotionSickFightOnAbove = 0.5f;

	/// <summary>No potion to drink now, other than for the sickness of one just drunk while life is still good (<see cref="PotionSickFightOnAbove"/>).</summary>
	public static bool NoPotion(BotContext ctx) =>
		!ctx.Combat.CanQuickHeal
		&& !(ctx.Combat.PotionSickTicks > 0 && ctx.Combat.LifeFraction >= PotionSickFightOnAbove && ctx.Combat.Life > ctx.BiggestRecentHit);

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

	/// <summary>A Dungeon Guardian seen is left out of every fight this long (a game hour): it cannot be hurt but 1 a hit.</summary>
	public const int GuardianIgnoreTicks = 3600;
	private readonly HashSet<int> guardiansSeen = new();

	/// <summary>Where a blast out was last looked for: not again from the same node (each look plans several walks).</summary>
	private TilePos? blastLookedAt;

	/// <summary>
	/// Walks failing "no path" in a row, with an explosive that breaks tiles in the bag: blow a way through the wall the pickaxe
	/// cannot break (BlastOutSkill), before resting. Seen in game (measured runs M10j, M10k): 12 game hours of "no path" from
	/// one spot under ebonstone, the rests and the climb-out all failing, with 7 bombs and 2 dynamite carried.
	/// </summary>
	private bool TryBlastOut(BotContext ctx, ISkill failed)
	{
		if (failed is not WalkToSkill walk || walk.LastPlan?.Status != Core.Pathfinding.PathStatus.NoPath || walk.Goal == null
			|| blastLookedAt == ctx.Node || BlastOutSkill.Carried(ctx).Count == 0)
			return false;
		blastLookedAt = ctx.Node;
		failures = 0;
		ctx.Log.Warn($"Auto: {MaxConsecutiveFailures} walks in a row found no path from {ctx.Node} ({LastResult}); looking for a wall to blast through");
		Begin(new BlastOutSkill(walk.Goal), ctx);
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
	public const float WormNearPx = ShelterSkill.BurrowerNearPx;

	/// <summary>
	/// A worm, or anything else that comes through blocks (vanilla NPC.noTileCollide: a Burning Sphere, Vile Spit), within
	/// <see cref="WormNearPx"/>: a hole is no hiding place from it. Seen in game (world_wof E8, 2026-10-17): dug in under the
	/// hellbridge at 39/280 life, "Shelter [Waiting]", and a Fire Imp's spheres came through the blocks and killed the bot.
	/// </summary>
	private static bool WormNear(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (EntityInfo e in ctx.Hostiles)
			if ((e.AiStyle == WormAi || e.NoTileCollide) && e.DistanceTo(cx, cy) <= WormNearPx)
				return true;
		return false;
	}

	/// <summary>
	/// NPC AI styles that fly faster than the player runs, checked in the decompile: 5 (vanilla AI_005_EaterOfSouls: an eater
	/// of souls, type 6, flies at up to 4 px a tick and homes in on its target) against the player's 3 (Player.maxRunSpeed).
	/// It collides with tiles (no noTileCollide), so a finished seal stops it, but it outpaces the flight and the dig.
	/// </summary>
	public static readonly int[] FastFlierAiStyles = { 5 };
	/// <summary>A fast flier this close (tiles) catches the bot before a shelter is dug: it is fought with the bow instead.</summary>
	public const int FastFlierTiles = 10;

	/// <summary>
	/// Is a fast flier (<see cref="FastFlierAiStyles"/>) within <see cref="FastFlierTiles"/>? Then a retreat to dig in is the
	/// wrong reflex: with a bow it is fought on, quick-healing as soon as a potion can be drunk (FightSkill). Seen in game
	/// (measured run M10a): "Danger: life 106/220 and no potion; retreating", then 6 hits from eaters of souls during the
	/// flight and the dig, the walk to the dig column re-planned 5 times, and "Died (17 damage: ... torn in half by Eater of
	/// Souls) ... Shelter [Digging]"; the bot had an Iron Bow and some 300 arrows.
	/// </summary>
	public static bool FastFlierNear(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (EntityInfo e in ctx.Hostiles)
			if (System.Array.IndexOf(FastFlierAiStyles, e.AiStyle) >= 0 && e.DistanceTo(cx, cy) <= FastFlierTiles * 16f)
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
	/// Wear the best armour carried, slot by slot, by defence (vanilla Item.defense): a looted Ancient Shadow Helmet (7) over the
	/// plan's Iron Helmet (4), as a player does the moment it is picked up. The plan equips its own tier once and never looked again
	/// (the user, 2026-10-17, world_wof E8: "It has an ancient shadow helmet in the inventory and still a shitty iron helm equipped").
	/// Set bonuses are not weighed: a piece worth more defence than the bonus it costs (iron's 2) wins; a mixed set loses nothing else.
	/// Journey: a piece not yet researched is researched first (ResearchFirst), as the accessories are.
	/// </summary>
	private static void WearBestArmour(BotContext ctx)
	{
		if (ctx.Inventory == null || ctx.Crafter == null)
			return;
		foreach (ArmorSlot slot in new[] { ArmorSlot.Head, ArmorSlot.Body, ArmorSlot.Legs }) {
			int worn = ctx.Inventory.Worn(slot);
			int wornDefense = worn >= 0 ? ctx.Inventory.ArmorDefense(worn) : 0;
			int best = -1, bestDefense = wornDefense;
			foreach (var stack in ctx.Inventory.Stacks()) {
				if (stack.Type == worn || ctx.Inventory.ArmorSlotOf(stack.Type) != slot)
					continue;
				int d = ctx.Inventory.ArmorDefense(stack.Type);
				if (d > bestDefense && !ResearchFirst(ctx, stack.Type)) {
					best = stack.Type;
					bestDefense = d;
				}
			}
			if (best >= 0 && ctx.Crafter.TryEquip(best))
				ctx.Log.Info($"Gear: wearing {ctx.Knowledge?.ItemName(best) ?? best.ToString()} (defence {bestDefense}) over {(worn >= 0 ? ctx.Knowledge?.ItemName(worn) ?? worn.ToString() : "nothing")} ({wornDefense})");
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
			// Not the Guide Voodoo Doll, an accessory (Item.cs:6839-6845): worn, it is not in the bag to be thrown (docs M12_SPEC.md S6).
			if (System.Array.IndexOf(ctx.Ids.FloatingAccessories, item) < 0 && item != ctx.Ids.GuideVoodooDoll && !ResearchFirst(ctx, item) && ctx.Crafter.TryEquip(item))
				ctx.Log.Info($"Gear: wearing {ctx.Knowledge.ItemName(item)}");
	}

	/// <summary>
	/// Journey: an accessory not yet researched is researched first (ResearchHabit, a copy duplicated back), then worn, as the
	/// user plays it (2026-10-17: "it should research, and THEN equip"); worn, the research slot never sees it. Not when the
	/// research slot refused it lately, nor with no duplication to get it back.
	/// </summary>
	private static bool ResearchFirst(BotContext ctx, int item)
	{
		IJourney j = ctx.Journey;
		return j != null && j.Enabled && j.DuplicationAllowed && j.ResearchLeft(item) > 0
			&& !(ctx.ResearchRefused.TryGetValue(item, out long at) && ctx.Tick - at < ResearchHabit.RefusedTicks && ctx.Tick >= at);
	}

	/// <summary>
	/// Trash anything carried that stops pickups (<see cref="GameIds.PickupBlockers"/>: the Encumbering Stone), as a player who
	/// finds nothing comes to the bag throws it out: with it carried, no wood, ore, crystal or coin is picked up. Seen in game
	/// (measured run U28, world_eye): looted from a chest at (2541,587) at t465000 with the rest of it, and from then on no
	/// count in the bag rose for the 400000 ticks left (Wood 38 down to 4 as arrows were made, "need 16 more Wood" for the last
	/// 30 game hours), and 101 of the run's 107 Collect heartbeats were spent standing on drops that never came.
	/// </summary>
	public static void DropPickupBlockers(BotContext ctx)
	{
		if (ctx.Inventory == null || ctx.Crafter == null || ctx.Knowledge == null)
			return;
		foreach (int item in ctx.Ids.PickupBlockers)
			if (ctx.Inventory.Count(item) > 0 && ctx.Crafter.TryTrash(item))
				ctx.Log.Info($"Inventory: trashing the {ctx.Knowledge.ItemName(item)} (while it is carried nothing but hearts and stars is picked up)");
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
	public static void KeepRoom(BotContext ctx) => KeepRoom(ctx, -1);

	/// <summary>
	/// <see cref="KeepRoom(BotContext)"/>, for a craft of <paramref name="craftFor"/> that found no room (-1: the habit). For a
	/// craft, when nothing is trashable by the habit's rules, what is kept only for being gear (spare tools and weapons, food,
	/// throwables, vanity) is trashable too, the least worth (value times stack) first; the plan's items and their ingredients
	/// and the craft's own ingredients stay. Seen in game (measured run M10i, world_eow): "crafting 30 Vile Powder (Vile
	/// Mushroom 6/6)" refused "crafting stayed blocked" for 6 game hours and more, no slot free and nothing trashed: the bag's
	/// 50 stacks all "gear" (a copper shortsword, a wooden sword, a copper pickaxe beside the silver one, shuriken, glowsticks,
	/// a lemon, a flare gun...) or makings.
	/// </summary>
	public static void KeepRoom(BotContext ctx, int craftFor) => KeepRoom(ctx, craftFor, MinFreeSlots, relaxed: craftFor >= 0);

	/// <summary>
	/// <see cref="KeepRoom(BotContext, int)"/> to <paramref name="target"/> free slots; <paramref name="relaxed"/>: what is kept only for
	/// being gear (spare tools and weapons, food, throwables, vanity) may go too, the best tool of each kind, the plan's items, the
	/// errand's reservations and the craft's makings staying. An errand leaving base packs a lean bag this way (seen in game, E6
	/// 2026-10-17: a 50-stack bag from the checkpoint, one slot freed at a time and refilled by the digging; mined life crystals
	/// found no slot and were left on the floor).
	/// </summary>
	public static void KeepRoom(BotContext ctx, int craftFor, int target, bool relaxed)
	{
		if (ctx.Inventory == null || ctx.Knowledge == null || ctx.Crafter == null)
			return;
		// The habit trashes a couple of stacks a pass; the lean bag for the road as many as it takes (a whole bag at most).
		int atMost = relaxed && craftFor < 0 ? 50 : target;
		for (int guard = 0; guard < atMost && ctx.Inventory.FreeSlots < target; guard++) {
			IReadOnlyList<(int Type, int Stack)> stacks = ctx.Inventory.Stacks();
			// Journey: a stack of a researched item first, whatever keeps it: the menu gives it back the moment it is needed.
			if (ResearchedSpare(ctx, stacks) is int spare && ctx.Crafter.TryTrash(spare)) {
				ctx.Log.Info($"Inventory: {ctx.Inventory.FreeSlots} free slot(s) after trashing a stack of {ctx.Knowledge.ItemName(spare)} (researched: duplicated again when needed)");
				continue;
			}
			var (trash, junk, cheapest) = PickTrash(ctx, stacks, KeptItems(ctx, stacks));
			// For a craft with nothing trashable by the habit's rules: what is kept only for being gear may go too (the craft's
			// own makings stay). Not "every stack kept" as the test for it: a lone stack of a building block is neither kept nor
			// trashed, and with one in the bag the relaxed pass never came (measured run M10m: "crafting 30 Vile Powder" blocked
			// 6+ game hours again, nothing trashed).
			if (trash < 0 && relaxed) {
				HashSet<int> kept = KeptItems(ctx, stacks, gearToo: false);
				// The best tool of each kind and the best weapon carried stay: the spares may go, never the last. Seen in game
				// (world_wof M12c, Peppe): for a work bench's craft, "trashing a stack of Copper Axe (worth 400 copper, the least of
				// anything not needed)", the only axe; then "GatherWood failed: no axe in the hotbar", every wood job, 6 game hours on.
				kept.UnionWith(BestTools(ctx, stacks));
				if (craftFor >= 0)
					foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(craftFor))
						foreach (Ingredient ing in r.Ingredients)
							foreach (int a in ing.Accepts)
								kept.Add(a);
				(trash, junk, cheapest) = PickTrash(ctx, stacks, kept);
			}
			if (trash < 0 || !ctx.Crafter.TryTrash(trash)) {
				if (craftFor >= 0)
					ctx.Log.Info($"Inventory: no room for the craft and nothing to trash ({(trash < 0 ? "all kept" : $"the {ctx.Knowledge.ItemName(trash)} would not go")})");
				return;
			}
			ctx.Log.Info($"Inventory: {ctx.Inventory.FreeSlots} free slot(s) after trashing a stack of {ctx.Knowledge.ItemName(trash)}"
				+ (junk ? " (worth nothing)" : $" (worth {cheapest} copper, the least of anything not needed)"));
		}
	}

	/// <summary>
	/// Journey (duplication on): the stack of a researched item KeepRoom trashes first, or null: not gear (a tool, a weapon, armour,
	/// ammo, potions: in use, and their prefixes), and not the last stack of the torches or the building blocks the light and the
	/// paths draw on without the menu; a second stack of anything first, then the one most carried. Seen in game (world_boc B4, The
	/// Aggressive Site, Nenne): the bag's 50 slots full, a dozen of them stacks of 9999 duplicated back after research (Work Bench,
	/// Campfire, Heart Lantern, door, chair, table, Life Crystal, Acorn, a second Torch and Wood stack), every one kept by the plan;
	/// nothing trashed, and the 12 Vertebrae the crimeras dropped round the bot never picked up: "Hunt for Vertebra 0/15" for 30
	/// game hours.
	/// </summary>
	private static int? ResearchedSpare(BotContext ctx, IReadOnlyList<(int Type, int Stack)> stacks)
	{
		IJourney j = ctx.Journey;
		if (j == null || !j.Enabled || !j.DuplicationAllowed)
			return null;
		int? best = null;
		(int Stacks, long Carried) bestKey = (0, -1);
		foreach (var g in stacks.GroupBy(s => s.Type)) {
			int type = g.Key, n = g.Count();
			if (!ctx.Infinite(type) || ctx.Inventory.IsGear(type) || System.Array.IndexOf(ctx.Ids.Coins, type) >= 0)
				continue;
			bool lastInUse = n < 2 && (type == ctx.Ids.Torch || ctx.Ids.BuildingBlocks.Contains(type));
			if (lastInUse)
				continue;
			(int, long) key = (n, g.Sum(s => (long)s.Stack));
			if (best == null || key.CompareTo(bestKey) > 0) {
				best = type;
				bestKey = key;
			}
		}
		return best;
	}

	/// <summary>The most powerful carried item of each tool kind and the best weapon (<see cref="IInventory.ToolPower"/>).</summary>
	internal static IEnumerable<int> BestTools(BotContext ctx, IReadOnlyList<(int Type, int Stack)> stacks)
	{
		foreach (ToolKind kind in System.Enum.GetValues<ToolKind>()) {
			int best = -1, power = 0;
			foreach (var s in stacks) {
				int p = ctx.Inventory.ToolPower(s.Type, kind);
				if (p > power) {
					power = p;
					best = s.Type;
				}
			}
			if (best > 0)
				yield return best;
		}
	}

	/// <summary>The stack KeepRoom would trash with this kept set (-1 for none), whether it is worth nothing, and its worth.</summary>
	private static (int Trash, bool Junk, long Cheapest) PickTrash(BotContext ctx, IReadOnlyList<(int Type, int Stack)> stacks, HashSet<int> kept)
	{
		int junk = -1, junkStacks = 0;
		long cheapest = long.MaxValue;
		int cheap = -1;
		foreach (var g in stacks.GroupBy(s => s.Type)) {
			int n = g.Count();
			bool block = ctx.Ids.BuildingBlocks.Contains(g.Key);
			int value = ctx.Knowledge.ItemValue(g.Key);
			// A kept thing that does not stack, carried several times over (one is kept): the spares are the bag's to trash.
			// Seen in game (measured run M10b): nine Shackles (an accessory, so "gear") among 50 full slots; with nothing
			// else to trash, 31 crafts failed "crafting stayed blocked (cursor item / full inventory)".
			bool spare = !block && kept.Contains(g.Key) && n > 1 && g.All(s => s.Stack == 1);
			if (!spare && (block ? n < 2 : kept.Contains(g.Key)))
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
		return junk >= 0 ? (junk, true, 0) : (cheap, false, cheapest);
	}

	/// <summary>What the bag must keep: gear (tools, weapons, armour, potions, ammo), the plan's own items, and the ingredients (two recipes deep) of the gear to make.</summary>
	internal static HashSet<int> KeptItems(BotContext ctx, IReadOnlyList<(int Type, int Stack)> stacks, bool gearToo = true)
	{
		GameIds ids = ctx.Ids;
		var kept = new HashSet<int> { ids.Wood, ids.Gel, ids.Torch, ids.WorkBenchItem, ids.WoodWall, ids.Door, ids.Chair, ids.Table, ids.Acorn,
			ids.WoodenArrow, ids.Lens, ids.LifeCrystal, ids.RecallPotion, ids.Campfire, ids.HeartLantern, ids.SuspiciousEye,
			// The evil boss plan's (Milestone 10): the summon's makings, the Eye's demonite, the teeth for unholy arrows.
			ids.WormFood, ids.VilePowder, ids.VileMushroom, ids.RottenChunk, ids.WormTooth, ids.DemoniteOre, ids.DemoniteBar, ids.Bottle,
			// The Wall of Flesh's (Milestone 12, G10): the Guide Voodoo Doll (worth 1000 copper, cheap enough to be the stack trashed) and
			// the Shadow Scales trashed in measured run M10u (log lines 7864-7876), the Shadow armour's.
			ids.GuideVoodooDoll, ids.ShadowScale };
		// In a Crimson world its twins: the Bloody Spine's makings, the Eye's crimtane, and the Creepers' Tissue Samples.
		if (ids.Crimson)
			kept.UnionWith(new[] { ids.BloodySpine, ids.ViciousPowder, ids.ViciousMushroom, ids.Vertebrae, ids.CrimtaneOre, ids.CrimtaneBar, ids.TissueSample });
		// Whatever a stage of the plan wants (the planner's pass, BotContext.NeededItems), and what the errand that owns the bot has
		// reserved (its packing list and reserve: the bridge's blocks, the fight's arrows, the potions kept back).
		kept.UnionWith(ctx.NeededItems);
		kept.UnionWith(ctx.Reservations.Items());
		kept.Remove(0);
		foreach (var s in stacks)
			if (gearToo && ctx.Inventory.IsGear(s.Type))
				kept.Add(s.Type);
		// The Bottle in the list for its glass (two recipes deep: glass, sand), as a station the summon's powder is made at.
		int[] gear = { ids.IronTierPickaxe, ids.IronTierBroadsword, ids.IronTierHelmet, ids.IronTierChainmail, ids.IronTierGreaves, ids.IronTierBow,
			ids.SilverTierPickaxe, ids.SilverTierBroadsword, ids.SilverTierHelmet, ids.SilverTierChainmail, ids.SilverTierGreaves, ids.SuspiciousEye,
			ids.HeartLantern, ids.Campfire, ids.WoodenArrow, ids.WorldEvilSummon, ids.EvilBow, ids.UnholyArrow, ids.Bottle };
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
	/// <summary>Falls that would hurt are stopped here, after whatever skill has the keys (one owner for them all).</summary>
	public FallArrest Falls { get; } = new();
	private int deathsSeen;
	private int lastBreath;
	private bool wasNight;
	private bool stuckWork;
	/// <summary>Life to recover to before coming out of a retreat.</summary>
	public const float RetreatUntil = 0.8f;

	/// <summary>
	/// A retreat that turned to fight (<see cref="ShelterSkill.FightBackReason"/>) is not taken up again for this long, nor while
	/// that fight is still being hit this recently: two immunity windows (<see cref="BotContext.HurtImmunityTicks"/>), so
	/// an enemy still on the bot has bitten again in that time. Without it the retreat and the fight would take turns every
	/// tick (the fight starts below the retreat threshold, so the retreat rule fires on its first tick).
	/// </summary>
	public const int FightBackHoldTicks = 2 * BotContext.HurtImmunityTicks;
	private long fightBackSince = long.MinValue / 2;
	private ISkill fightBackFight;

	private bool RetreatHeldOff(BotContext ctx) =>
		ctx.Tick - fightBackSince < FightBackHoldTicks
		|| fightBackFight != null && ReferenceEquals(Current, fightBackFight) && ctx.Tick - ctx.LastEnemyHitTick < FightBackHoldTicks;

	public void Tick(BotContext ctx)
	{
		// While the bot has the keys (auto mode, or a skill running): never a fall that hurts, whatever the skill did with them.
		bool driving = Auto || Current != null;
		ctx.NoteZone();
		ctx.PlannedCatch = null;
		ctx.PlannedLanding = null;
		TickSkills(ctx);
		if (driving || Current != null)
			Falls.Tick(ctx);
	}

	private void TickSkills(BotContext ctx)
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
					if (Work != null) {
						Planner.OnSkillFinished(Work, SkillStatus.Failed, ctx);
						Errand?.OnSkillFinished(Work, SkillStatus.Failed, "died", ctx);
					}
				}
				Interrupt(ctx);
			}
			// The errand stays: back to its frontier (docs ERRAND_DESIGN.md section 12, the death policy).
			if (Auto && Errand != null)
				Errand.OnDeath(ctx);
		}
		// The errand's rules hold every tick it owns the bot (the depth limit lifted on the way down), before the habits and reflexes read them.
		if (Auto && Errand != null) {
			Errand.Rules(ctx);
			if (ctx.Tick % HabitEvery == 0)
				Planner.AdoptErrand(Errand); // the plan's status line follows the errand's progress (the self-test's stall check reads it)
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
		if (Auto)
			Planner.SpareRules(ctx); // the Voodoo Demon spared before the danger check (docs M12_SPEC.md G7)
		// The reflexes (surfacing, the Dungeon Guardian, a retreat, a fight) from the table, by bid.
		TickReflexes(ctx);
		// Nightfall: work started by day (a walk back to the spawn, a mine near the surface) is planned again, so the night
		// rule can send the bot home. Seen in game (world_eye run 68): a walk begun by day came up at night among zombies,
		// and one killed the bot (wooden sword, no armour).
		bool night = ctx.IsNight;
		if (Auto && night && !wasNight && Work is WalkToSkill or MineSkill or ExploreSkill or GatherWoodSkill
			&& (ctx.Combat.Defense < ProgressionPlanner.NightOutDefense || Planner.NightHuntDue(ctx)))
			RequestReplan("nightfall");
		// The walk looking for the Dungeon is by day (docs M11_SPEC.md S2): at nightfall the planner digs in instead.
		else if (Auto && night && !wasNight && (Work is TravelUntilSkill || Work is ExploreSkill { JobKey: string dk } && dk.StartsWith("Explore dungeon")))
			RequestReplan("nightfall");
		// The day's work before a night-only stage (the Eye's lenses or summon; ProgressionPlanner.DayWorkBeforeNight), whatever
		// it is (a build, a hunt, a trip): planned again at nightfall, so the night-only stage goes first again, as by the plan's
		// order. Without it, armoured with the summon in hand, a mine begun by day went on into the night.
		else if (Auto && night && !wasNight && Work != null && Planner.DayWorkBeforeNight)
			RequestReplan("nightfall");
		// Daybreak: the hunt for night prey (demon eyes leave at dawn) is planned again, so the day goes to the day's work.
		else if (Auto && !night && wasNight && Work is HuntSkill { AiStyle: int ai } && System.Array.IndexOf(ProgressionPlanner.NocturnalAiStyles, ai) >= 0)
			RequestReplan("daybreak");
		wasNight = night;
		if (Auto && ctx.Tick % HabitEvery == 0)
			WatchPlace(ctx);
		// Journey Mode: research what the bag holds enough of (nothing at all on Classic).
		if (Auto)
			ResearchHabit.Tick(ctx, Current);
		if (Auto && ctx.Tick % HabitEvery == 0) {
			DropPickupBlockers(ctx);
			ResearchHabit.TrashResearched(ctx, Current);
			KeepRoom(ctx);
			WearAccessories(ctx);
			WearBestArmour(ctx);
			NoteEvilEnemies(ctx);
			if (ctx.Node.Y > ctx.SurfaceY)
				NoteCrowd(ctx); // underground: on the surface a night's zombies are the house's business
			// A chest comes into sight while looking for ore or prey: go and loot it first (the planner picks it next).
			// On a walk too (a trip, to a station): seen in game, the bot ran past a life crystal on its way (user report,
			// 2026-10-14). Not during a reflex (a flight, a fight).
			bool onAWalk = Override == null && Work is WalkToSkill;
			// Optional loot waits while an operation owns the bot (the Wall's descent, ProgressionPlanner.Schedulers.Operation).
			if (Planner.DescentOwns(ctx)) { }
			// An errand decides for itself, by the detour's cost.
			else if (Errand != null)
				OfferOpportunities(ctx);
			else if ((Current is MineSkill or ExploreSkill or HuntSkill || onAWalk) && ProgressionPlanner.NextChest(ctx) is ChestInfo seen)
				RequestReplan($"a chest at {seen.TopLeft} came into sight");
			// A life crystal: whatever the work (building, gathering, crafting, going home...), only not during a reflex.
			else if (Override == null && Work != null && !(Work is MineSkill m && m.ItemType == ctx.Ids.LifeCrystal)) {
				// Not once researched (Journey): the menu gives every crystal still to use.
				if (ProgressionPlanner.LifeCrystalUseful(ctx) && !ctx.Infinite(ctx.Ids.LifeCrystal) && ProgressionPlanner.SeenLifeCrystal(ctx) is TilePos heart)
					RequestReplan($"a life crystal at {heart} came into sight");
			}
		}
		// Work asked to be planned again (nightfall, a chest in sight): only between reflexes, and the same job goes on. Not an
		// errand's work: nightfall is no reason to leave a shaft, and its opportunities were offered to it.
		if (replan && Errand != null)
			replan = false;
		if (replan && Override == null && Work != null && Auto) {
			replan = false;
			ChooseWork(ctx, paused: false, replanWhy);
		}
		// The errand's outcome is checked every tick (its goal, its failures, no progress).
		if (Auto && Errand != null) {
			Errands.Outcome outcome = Errand.Check(ctx);
			if (outcome.Over) {
				EndErrand(ctx, outcome);
				if (Override == null)
					return;
			}
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
		if (Current == null && Auto && Errand != null) {
			// The errand's own next skill; nothing, and it says why (blocked) or is done.
			ISkill job = Errand.NextSkill(ctx, Planner);
			if (job == null) {
				Errands.Outcome outcome = Errand.Check(ctx);
				EndErrand(ctx, outcome.Over ? outcome : Errands.Outcome.Blocked("nothing to do", Errand.Describe()));
				return;
			}
			Planner.AdoptErrand(Errand);
			ctx.Log.Info($"Errand {Errand.Id}: {Errand.Status} -> {job.Name}");
			Begin(job, ctx);
		}
		if (Current == null && Auto) {
			ISkill next = Planner.Next(ctx);
			if (next == null && Planner.TakeProposed() is Errands.Errand proposed) {
				StartErrand(proposed, ctx);
				return; // its first skill next tick
			}
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
		// On the errand's deck (the hellbridge), shots are dodged whatever the work: the bridge and the crafting at its end took the
		// sickles that threw the bot off it (E11, E12, 2026-10-17); only a fight dodged. The fights keep their own dodge.
		if (status == SkillStatus.Running && ctx.Shots.Count > 0 && Current is not FightSkill && Current is not BossFightSkill
			&& Errand?.FightDeck(ctx) is (int dx0, int dx1, int standY) && ctx.Node.Y == standY && ctx.Body.Grounded
			&& ctx.Node.X >= dx0 - FightSkill.DeckSlack && ctx.Node.X <= dx1 + FightSkill.DeckSlack)
			deckDodge.Apply(ctx, (dx0, dx1, standY), Current.Name);
		if (status == SkillStatus.Running && WorkStuck(ctx)) {
			ctx.Log.Warn($"Auto: no progress in {NoProgressTicks / 3600} game hours ({Work.Describe()} at {ctx.Node}); giving the job up");
			stuckWork = true;
			status = SkillStatus.Failed;
		}
		// Not while building (the house and arena place their own torches, and the keeper's took the arena's: boss_auto
		// then hunted gel for an hour) or in a boss fight. Nor while surfacing: the hand is the water pillar's, a block every useTime as the
		// body rises (WaterPillar), and a torch taken in hand mid-jump (seen in game, world_eye J5: "Light: torch #100" during a
		// Surface) costs that jump's blocks, a slot switch each way.
		if (Auto && Current is not Building.BuildSkill && Current is not BossFightSkill && Current is not SurfaceSkill)
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
		// A retreat that found no hole, or whose hole failed: not straight back into another at the same spot; the fight rule has
		// the hold first. With a worm near the retreat rule goes home at once (or not at all), never into a hole.
		if (skill is ShelterSkill { Retreat: true } && status == SkillStatus.Failed && !WormNear(ctx))
			fightBackSince = ctx.Tick;
		if (Auto) {
			Planner.OnSkillFinished(skill, status, ctx);
			if (Errand != null && Override == null)
				Errand.OnSkillFinished(skill, status, why, ctx);
		}
		skill.Stop(ctx);
		if (Override != null) {
			// A reflex is over: its loot, then back to the paused work if the planner still wants it. Reflexes do not count
			// as failures or successes of the work.
			Override = null;
			// Drops given up on by an earlier fight's collect (no way there, or stood on and not picked up) stay given up on while
			// they lie there: each fight's collect began afresh and stood on the same ones again. The set keeps only drops still
			// on the ground (ids are item slots, reused once a drop is gone).
			lootGivenUp.IntersectWith(ctx.Drops.Select(d => d.Id));
			if (Auto && wasFight && CollectDropsSkill.AnyInRange(ctx, d => !lootGivenUp.Contains(d.Id), LootRadiusTiles)) {
				ctx.Log.Info("Auto: picking up loot after the fight");
				BeginOverride(new CollectDropsSkill(_ => true, LootRadiusTiles, lootGivenUp), ctx);
				return;
			}
			if (Work != null) {
				if (Auto && Errand == null)
					ChooseWork(ctx, paused: true, skill.Name.ToLowerInvariant());
				else {
					// An errand's work goes on as it was: the planner is not asked (docs ERRAND_DESIGN.md section 4, step 2).
					Work.Resume(ctx);
					if (Errand != null) {
						ctx.Log.Info($"Errand {Errand.Id}: back to {Work.Name} after {skill.Name.ToLowerInvariant()}");
						Errand.OnEmergencyOver(ctx);
					}
				}
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
		// An errand's failures are its own (its outcome check): no rests, no blasts, no give-ups here.
		if (Auto && Errand != null) {
			if (status == SkillStatus.Succeeded)
				ctx.DigAnyway = false;
			return;
		}
		if (Auto) {
			failures = status == SkillStatus.Failed ? failures + 1 : 0;
			if (status == SkillStatus.Succeeded) {
				backoffs = 0;
				ctx.DigAnyway = false;
				if (skill is not BlastOutSkill)
					blastLookedAt = null; // something worked since: a spot looked at before may be worth a look again
			}
			if (failures >= MaxConsecutiveFailures && TryBlastOut(ctx, skill))
				return;
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
		Falls.Active ? (Auto ? "auto: " : "") + "fall: " + Falls.State + (Current != null ? " (" + Current.Describe() + ")" : "")
		: Current != null ? (Auto ? "auto: " : "") + (Errand != null && Override == null ? Errand.Id + " > " : "") + Current.Describe()
		: Target != null ? $"target {Target.Value} (idle)"
		: Auto ? "auto: " + Planner.Status
		: LastResult.Length > 0 ? $"idle - last: {LastResult}"
		: "idle - no target";
}
