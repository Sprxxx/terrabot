using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Progression;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Bot.Errands;

/// <summary>
/// The Wall of Flesh expedition (docs ERRAND_DESIGN.md sections 3 and 10; the stage row <see cref="StageTable.WallOfFlesh"/>): one
/// errand from the packing list to the Wall's loot, through its phases: <see cref="DescendPhase"/>, a straight shaft
/// (<see cref="HellevatorSkill"/>) at a column chosen from the known terrain (<see cref="ShaftSite"/>: the fewest lava, water and sand
/// cells on the way down near the preferred offset from home); <see cref="BridgePhase"/>, the hellbridge from the blocks carried and
/// the Underworld's ash; <see cref="FightPhase"/>, the doll, the summon and the fight along the bridge; <see cref="LootPhase"/>.
/// It owns the bot throughout: the surface's stages are not looked at again on the way (seen in game, E1 2026-10-17: with the
/// descent alone as the errand, the plan sent the bot 780 rows back up for an NPC room the moment it stood on the row), a chest or
/// a crystal is taken only within a short detour (<see cref="AcceptDetourTicks"/>), consumption resupplies only the remaining demand
/// (the fight's arrows, the bridge's blocks), and a death sends the bot back down the same shaft to the phase it was in. Blocked
/// after <see cref="MaxFailures"/> hellevator failures or <see cref="DeathsPerColumn"/> deaths at one column (once, the column is
/// chosen again with the failed one left out: a method change, the goal kept), or <see cref="MaxPhaseFailures"/> failures in a row
/// of a later phase's skills; then it ends with the reason, and the plan decides.
/// Seen in game (world_wof M12d-M12f, X1A, X2A): the shaft was planner memory moved 81 columns a failure, to x 4909 on a 4200-wide
/// world; the descent was dropped for wood, NPC rooms and crystals 3-41 times a run; a death ended it and the surface's work came first.
/// </summary>
public sealed class WallOfFleshErrand : Errand
{
	public const string ErrandId = "boss:WallOfFlesh";
	/// <summary>Columns either side of the preferred column looked at for the shaft (policy: a short walk from home).</summary>
	public const int SearchHalfWidth = 120;
	/// <summary>
	/// A chest or crystal whose detour there and back costs more than this (ticks) waits for the errand to end: 20 s of walking,
	/// about a chest in the shaft's own wall, never one 200 rows up (policy; Opportunity.Detour).
	/// </summary>
	public const int AcceptDetourTicks = 20 * 60;
	/// <summary>Hellevator failures in a row before the column is changed, and again before the errand is blocked (policy).</summary>
	public const int MaxFailures = 3;
	/// <summary>
	/// Deaths on the way down one column before it is changed (the way through it is the trouble: a desert, a cave of crawlers), and
	/// again before the errand is blocked (policy). Seen in game (E1, 2026-10-17): x 2260 went through the underground desert, and a
	/// tomb crawler and two rolling cacti killed the bot there at rows 487-683, each death a new trip down the same column.
	/// </summary>
	public const int DeathsPerColumn = 3;
	/// <summary>Failures in a row of a bridge, fight or loot skill before the errand is blocked with the last reason (policy).</summary>
	public const int MaxPhaseFailures = 3;
	/// <summary>
	/// Free slots the bag leaves base with (BotBrain.KeepRoom, relaxed): the shaft's stone and dirt, the Underworld's ash and loot, a
	/// crystal on the way. A policy choice; seen in game (E6, 2026-10-17): a 50-stack bag, mined crystals left on the floor.
	/// </summary>
	public const int LeanFreeSlots = 10;
	/// <summary>
	/// Blasts at walls the bridge cannot mine (hellstone brick, a ruined house on the stand row: pick-gated at 65, and a bomb breaks it,
	/// Projectile.CanExplodeTile) before the bridge phase is blocked; the bombs and dynamite carried are a player's way past hellstone.
	/// Seen in game (E6, 2026-10-17): "the cell (2439,1041) in the bridge's way cannot be mined at pick power 45 (tile 76)" twice, with
	/// 10 Bombs and 2 Dynamite in the bag.
	/// </summary>
	public const int MaxBlasts = 4;
	/// <summary>Columns past the blocker the blast's walk aims for (within the blast's reach of it).</summary>
	public const int BlastPastTiles = 4;

	public const string DescendPhase = "descend", BridgePhase = "bridge", FightPhase = "fight", LootPhase = "loot";

	public override string Id => ErrandId;
	/// <summary>The shaft's column (the body's left one).</summary>
	public int ShaftX { get; private set; }
	/// <summary>The column preferred before the terrain was looked at (the bridge's way from home).</summary>
	public int Preferred { get; }
	/// <summary>The feet row to end the shaft on (the hellbridge's block row less one).</summary>
	public int StandRow { get; }
	/// <summary>The deepest feet row reached down the shaft in this life.</summary>
	public int DeepestRow { get; private set; } = int.MinValue;
	/// <summary>The deepest feet row reached in any life (the frontier the trip back after a death aims for).</summary>
	public int Frontier { get; private set; } = int.MinValue;
	public int Failures { get; private set; }
	/// <summary>Deaths on the way down the current column.</summary>
	public int ColumnDeaths { get; private set; }
	/// <summary>Failures in a row of the current later phase's skills.</summary>
	public int PhaseFailures { get; private set; }
	public string LastFailure { get; private set; } = "";
	/// <summary>The column was chosen again once after failures or deaths (the one method change allowed).</summary>
	public bool ColumnChanged { get; private set; }
	/// <summary>Where the shaft ended (the bridge's start), once it has.</summary>
	public TilePos? Landed { get; private set; }
	private readonly HashSet<int> failedColumns = new();
	private HellevatorSkill shaft;
	private ISkill phaseSkill;
	private bool landed, done;
	private TilePos? blastAt;
	public int Blasts { get; private set; }
	private float bridgeBest;
	private TilePos lastDeathAt;
	private IErrandSupport planner;
	private readonly GameIds ids;

	public WallOfFleshErrand(int preferred, int standRow, GameIds ids, string startPhase = PreparePhase)
	{
		Preferred = preferred;
		StandRow = standRow;
		this.ids = ids;
		ShaftX = preferred;
		Prerequisites = StageTable.WallOfFlesh.Prerequisites(ids);
		Packing = StageTable.WallOfFlesh.Packing(ids);
		Reserve = StageTable.WallOfFlesh.Reserve(ids);
		Identity = $"the shaft at x {ShaftX}";
		Phase = startPhase;
	}

	/// <summary>A column an earlier errand ended blocked at: not chosen (nor within HellevatorSkill.Slack of it).</summary>
	public void Exclude(int column) => failedColumns.Add(column);

	/// <summary>The column from the known terrain (option B, docs ERRAND_DESIGN.md section 8), once, when the errand begins.</summary>
	public override void Begin(BotContext ctx)
	{
		base.Begin(ctx);
		if (!Restored && (Phase == PreparePhase || Phase == DescendPhase))
			ChooseColumn(ctx, "begun");
	}

	private void ChooseColumn(BotContext ctx, string when)
	{
		int top = Math.Max(1, ProgressionPlanner.SurfaceRow(ctx));
		int chosen = ShaftSite.Choose(ctx.Grid, Preferred, SearchHalfWidth, top, StandRow, failedColumns, HellevatorSkill.Slack, out string why);
		ShaftX = chosen;
		Identity = $"the shaft at x {ShaftX}";
		ctx.Log.Info($"Errand {Id} ({when}): {why}");
	}

	/// <summary>In the Underworld (on or below its first row).</summary>
	public static bool DownThere(BotContext ctx) => ctx.UnderworldRow > 0 && ctx.Node.Y >= ctx.UnderworldRow;

	public override bool GoalMet(BotContext ctx) => done;

	/// <summary>
	/// The descent: rows dug this life, or before any (the trip back after a death) minus the distance to the frontier, so coming
	/// nearer counts. The bridge: its built share. The fight and the loot: the ticks (no watchdog: a hunt for a Voodoo Demon takes
	/// what it takes; the phase's failures in a row are the check there).
	/// </summary>
	public override float Progress(BotContext ctx)
	{
		switch (Phase) {
			case DescendPhase:
				if (DeepestRow > int.MinValue)
					return DeepestRow;
				if (Frontier == int.MinValue)
					return 0f;
				TilePos n = ctx.Node;
				return -(Math.Abs(n.X - ShaftX) + Math.Abs(Frontier - n.Y)) - 1f;
			case BridgePhase:
				// Down there: the bridge's built share, the most it has ever read (the share is the last solid cell from the start, and a
				// gap behind the frontier reads it back: E8 2026-10-17, the bridge at x 2992 and advancing, "no progress in 2 game hours").
				// On the way back down after a death: the depth (it grows as the shaft is dropped down; E4 had it the other way round).
				if (!DownThere(ctx))
					return 50000f + ctx.Node.Y;
				if (planner is ProgressionPlanner p)
					bridgeBest = Math.Max(bridgeBest, (float)p.HellbridgeProgress(ctx));
				return 100000f + bridgeBest * 10000f;
			case FightPhase:
			case LootPhase:
				return 1e9f + ctx.Tick;
			default:
				return 0f;
		}
	}

	public override void Rules(BotContext ctx)
	{
		if (Phase == PreparePhase)
			return;
		// The gear's depth limit does not hold on the expedition (BotContext.DeepestRow), as the plan's errand had it.
		ctx.DepthErrand = true;
		// The dig went on from another column (a cave between, HellevatorSkill.Approach): the errand's identity follows it, so the
		// trip back after a death goes to the column actually dug, and the logs say where the shaft is.
		if (shaft != null && shaft.Column != int.MinValue && Math.Abs(shaft.Column - ShaftX) > HellevatorSkill.Slack) {
			ctx.Log.Info($"Errand {Id}: the shaft goes on at x {shaft.Column}, not x {ShaftX} (the dig moved over there); that is the shaft now");
			ShaftX = shaft.Column;
			Identity = $"the shaft at x {ShaftX}";
		}
		if (Phase != DescendPhase)
			return;
		TilePos n = ctx.Node;
		if (n.Y > DeepestRow && (shaft == null || shaft.Column == int.MinValue || Math.Abs(n.X - shaft.Column) <= HellevatorSkill.SeekTiles)) {
			DeepestRow = n.Y;
			if (n.Y > Frontier)
				Frontier = n.Y;
		}
	}

	/// <summary>Down there with the bridge known: a fight keeps to the bridge (knocked off it, it walks back first).</summary>
	/// <summary>
	/// While the bridge is built, the fight's deck is the part that stands (<see cref="ProgressionPlanner.HellbridgeStanding"/>); for the
	/// fight and the loot, the whole bridge. Seen in game (world_wof E10, 2026-10-17): with the planned span as the deck, a fight chased
	/// a Lava Slime 550 columns along the Underworld's floor past the bridge's end at x 2418, a Demon killed the bot there, and the
	/// bridge made no progress for two game hours meanwhile (the watchdog blocked the errand for it).
	/// </summary>
	public override (int MinX, int MaxX, int StandY)? FightDeck(BotContext ctx) =>
		Phase != PreparePhase && Phase != DescendPhase && DownThere(ctx) && planner is ProgressionPlanner p
			? Phase == BridgePhase ? p.HellbridgeStanding(ctx) : p.HellbridgeArena
			: null;

	/// <summary>A chest in the deck's way the bridge stopped at (<see cref="HellbridgeSkill.Chest"/>): looted next, once per cell.</summary>
	private TilePos? lootChest;
	private readonly HashSet<TilePos> chestsLooted = new();

	/// <summary>The chest's top-left cell for <see cref="LootChestSkill"/>: the seen chest the cell belongs to (a chest is 2 by 2), else the cell.</summary>
	private static TilePos ChestTopLeft(BotContext ctx, TilePos cell) =>
		ctx.World?.SeenChests(cell, 2).Select(c => (TilePos?)c.TopLeft)
			.FirstOrDefault(tl => cell.X - tl.Value.X is 0 or 1 && cell.Y - tl.Value.Y is 0 or 1) ?? cell;

	/// <summary>
	/// What each phase still needs: the way down and the bridge, the bridge's blocks; the fight, its arrows. Not the fight's arrows
	/// while the bridge is built: arrows shot at the Underworld's fliers put the count under the fight's 600, and the resupply went
	/// looking for wood for arrows down there ("GatherWood 17/20 [walking to a tree]"), where a Bone Serpent killed the bot (E5,
	/// 2026-10-17). The arrows are seen to once, before the summon.
	/// </summary>
	public override IEnumerable<Need> RemainingDemand(BotContext ctx)
	{
		switch (Phase) {
			case DescendPhase:
			case BridgePhase:
				yield return new BridgeBlocks("bridge blocks") { Batch = BridgeBatch };
				break;
			case FightPhase:
				yield return new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), ProgressionPlanner.WofFightArrows, "arrows for the fight");
				break;
		}
	}

	/// <summary>Blocks got at a time for the bridge once down there (the Underworld's ash, mined where the bot stands): a stretch, not the whole bridge first (policy).</summary>
	public const int BridgeBatch = 600;

	private IEnumerable<Need> PhaseNeeds()
	{
		switch (Phase) {
			case BridgePhase:
				yield return new BridgeBlocks("bridge blocks") { Batch = BridgeBatch };
				yield return new HellbridgeBuilt("hellbridge");
				break;
			case FightPhase:
				yield return new BossDefeated(ids.WallOfFlesh, ids.GuideVoodooDoll, ids.WallOfFleshMinions, "Wall of Flesh") { NightOnly = false, ByVoodooDoll = true };
				break;
			case LootPhase:
				yield return new BossLoot(ids.WallOfFlesh, "the Wall of Flesh's loot");
				break;
		}
	}

	public override ISkill NextSkill(BotContext ctx, IErrandSupport planner)
	{
		this.planner = planner;
		if (Phase == PreparePhase) {
			ISkill ready = planner.ResolveNeeds(Prerequisites, ctx, out string notReady);
			if (notReady != null) {
				LastFailure = $"a prerequisite cannot be met: {notReady}";
				Failures = MaxFailures;
				return null;
			}
			if (ready != null) {
				Status = $"prerequisite: {planner.Status}";
				return ready;
			}
			ISkill pack = planner.ResolveNeeds(Packing, ctx, out string blocked);
			if (blocked != null) {
				LastFailure = $"the packing list cannot be met: {blocked}";
				Failures = MaxFailures;
				return null;
			}
			if (pack != null) {
				Status = $"packing: {planner.Status}";
				return pack;
			}
			Packed(ctx, DescendPhase);
			int before = ctx.Inventory?.FreeSlots ?? 0;
			BotBrain.KeepRoom(ctx, -1, LeanFreeSlots, relaxed: true); // a lean bag for the road: spare gear and junk go, the packing stays
			ctx.Log.Info($"Errand {Id}: packed ({Describe(Packing)}; kept back {Describe(Reserve)}); the bag {before} -> {ctx.Inventory?.FreeSlots ?? 0} free slots; {Identity} from {ctx.Node} to row {StandRow}");
		}
		// The remaining demand: a resupply only when the rest of the work is not feasible with what is carried.
		ISkill more = planner.ResolveNeeds(RemainingDemand(ctx), ctx, out string demandBlocked);
		if (demandBlocked != null) {
			LastFailure = $"the remaining demand cannot be met: {demandBlocked}";
			Failures = MaxFailures;
			return null;
		}
		if (more != null) {
			Status = $"resupply: {planner.Status}";
			return more;
		}
		if (Phase == DescendPhase && (landed || HellevatorSkill.Arrived(ctx, StandRow))) {
			Landed ??= ctx.Node;
			Phase = BridgePhase;
			ctx.Log.Info($"Errand {Id}: on the row at {Landed} after {Deaths} deaths; the bridge next");
		}
		if (Phase == DescendPhase || !DownThere(ctx)) {
			// The way down, or back down after a death in a later phase (the shaft is dug: a series of drops).
			if (shaft == null) {
				shaft = new HellevatorSkill(ShaftX, StandRow);
				Status = Phase != DescendPhase ? $"back down {Identity} to the {Phase}" : DeepestRow > int.MinValue || Frontier > int.MinValue ? $"back down {Identity} to the frontier at row {Frontier}" : $"down {Identity} to row {StandRow}";
			}
			return shaft;
		}
		if (ctx.World != null && ctx.World.BossDefeated(ids.WallOfFlesh) && Phase != LootPhase) {
			Phase = LootPhase;
			PhaseFailures = 0;
			ctx.Log.Info($"Errand {Id}: the Wall of Flesh is defeated; its loot next");
		}
		// The bridge stopped at a wall nothing it has mines: a blast through it, with the bombs carried, then the bridge again.
		if (blastAt is TilePos wall) {
			blastAt = null;
			Blasts++;
			int dir = wall.X >= ctx.Node.X ? 1 : -1;
			var past = new TilePos(wall.X + dir * BlastPastTiles, StandRow);
			Status = $"bombing the wall at {wall} in the bridge's way (blast {Blasts} of {MaxBlasts})";
			phaseSkill = new BlastOutSkill(new GoalNearTile(past, 3, ctx.Physics));
			return phaseSkill;
		}
		if (lootChest is TilePos ch) {
			lootChest = null;
			TilePos topLeft = ChestTopLeft(ctx, ch);
			Status = $"looting the chest at {topLeft} in the bridge's way";
			phaseSkill = new LootChestSkill(topLeft);
			return phaseSkill;
		}
		ISkill s = planner.ResolveNeeds(PhaseNeeds(), ctx, out string phaseBlocked);
		if (phaseBlocked != null) {
			LastFailure = $"the {Phase} cannot go on: {phaseBlocked}";
			PhaseFailures = MaxPhaseFailures;
			return null;
		}
		if (s != null) {
			phaseSkill = s;
			Status = $"{Phase}: {planner.Status}";
			return s;
		}
		switch (Phase) {
			case BridgePhase:
				Phase = FightPhase;
				PhaseFailures = 0;
				ctx.Log.Info($"Errand {Id}: the hellbridge is built; the fight next");
				return NextSkill(ctx, planner);
			case FightPhase:
				if (ctx.World != null && ctx.World.BossDefeated(ids.WallOfFlesh))
					return NextSkill(ctx, planner);
				LastFailure = $"nothing to do for the fight: {planner.Status}";
				PhaseFailures = MaxPhaseFailures;
				return null;
			default:
				done = true;
				return null;
		}
	}

	protected override Outcome CheckOwn(BotContext ctx)
	{
		if (done)
			return Outcome.Done($"the Wall of Flesh defeated and its loot collected ({Deaths} deaths)");
		if (Phase == BridgePhase || Phase == FightPhase || Phase == LootPhase) {
			if (PhaseFailures >= MaxPhaseFailures)
				return Outcome.Blocked($"{PhaseFailures} {Phase} failures in a row", LastFailure);
			if (!DownThere(ctx))
				return DescentOutcome(ctx); // the trip back down after a death: the shaft's rules
			return Outcome.Running;
		}
		return DescentOutcome(ctx);
	}

	private Outcome DescentOutcome(BotContext ctx)
	{
		bool failed = Failures >= MaxFailures, deadly = ColumnDeaths >= DeathsPerColumn;
		if (!failed && !deadly)
			return Outcome.Running;
		string what = failed ? $"{MaxFailures} hellevator failures in a row at x {ShaftX}" : $"{DeathsPerColumn} deaths on the way down x {ShaftX}";
		string evidence = failed ? LastFailure : $"the last at {lastDeathAt}";
		if (!ColumnChanged && Phase == DescendPhase && !LastFailure.StartsWith("the packing") && !LastFailure.StartsWith("the remaining")) {
			ColumnChanged = true;
			failedColumns.Add(ShaftX);
			Failures = 0;
			ColumnDeaths = 0;
			int old = ShaftX;
			ChooseColumn(ctx, $"column {old} given up: {what} ({evidence})");
			shaft = null;
			if (ShaftX != old)
				return Outcome.Running;
		}
		return Outcome.Blocked(what, evidence);
	}

	public override bool Accepts(in Opportunity o, BotContext ctx) => Phase == PreparePhase || o.DetourTicks <= AcceptDetourTicks;

	public override void OnSkillFinished(ISkill skill, SkillStatus status, string why, BotContext ctx)
	{
		if (ReferenceEquals(skill, shaft)) {
			shaft = null;
			if (status == SkillStatus.Succeeded) {
				landed = true;
				Landed = ((HellevatorSkill)skill).Landed ?? ctx.Node;
				Failures = 0;
				return;
			}
			if (why == "died")
				return;
			Failures++;
			LastFailure = string.IsNullOrEmpty(why) ? "failed" : why;
			return;
		}
		if (!ReferenceEquals(skill, phaseSkill))
			return;
		phaseSkill = null;
		// The bridge stopped at a wall it cannot mine, with explosives carried: the next skill is the blast, not a failure counted.
		// Only without the pickaxe for it: with power 65 a brick is mined, and a bomb on the bridge is the bot's own death (E7, 2026-10-17:
		// "no way out of the blast's 64 px box", then "put on display by Peppe's Bomb"; the user: "you can mine the hellstone with the pickaxe").
		if (skill is HellbridgeSkill { Blocker: TilePos wall } && status == SkillStatus.Failed && ctx.PickPower < StageTable.HellstonePickPower
			&& Blasts < MaxBlasts && BlastOutSkill.Carried(ctx).Count > 0) {
			blastAt = wall;
			if (planner is ProgressionPlanner p)
				p.BridgeBlasted();
			ctx.Log.Info($"Errand {Id}: the bridge cannot mine {wall} ({why}); a blast through it next ({BlastOutSkill.Carried(ctx).Count} kinds of explosive carried)");
			return;
		}
		// A chest in the deck's way (E10, 2026-10-17): looted first, once per cell; a second stop at the same one is the failure it is.
		if (skill is HellbridgeSkill { Chest: TilePos chestCell } && status == SkillStatus.Failed && chestsLooted.Add(chestCell)) {
			lootChest = chestCell;
			ctx.Log.Info($"Errand {Id}: {why}; looting it next");
			return;
		}
		if (status == SkillStatus.Succeeded)
			PhaseFailures = 0;
		else if (why != "died" && !(why ?? "").StartsWith("out of blocks")) { // the bridge's blocks used up is consumption: the demand resupplies them
			PhaseFailures++;
			LastFailure = string.IsNullOrEmpty(why) ? $"{skill.Name} failed" : why;
		}
	}

	public override void OnDeath(BotContext ctx)
	{
		base.OnDeath(ctx);
		shaft = null;
		phaseSkill = null;
		if (Phase == DescendPhase) {
			ColumnDeaths++;
			lastDeathAt = DeepestRow > int.MinValue ? new TilePos(ShaftX, DeepestRow) : ctx.Node;
		}
		DeepestRow = int.MinValue; // this life's frontier: the way back down the dug shaft is progress again
		ctx.Log.Info($"Errand {Id}: died in the {Phase}; back down {Identity} from the spawn (death {Deaths} of the errand"
			+ (Phase == DescendPhase ? $", {ColumnDeaths} on this column)" : ")"));
	}

	public override string Describe() => $"{Id} [{Phase}] {Identity} to row {StandRow}" + (Frontier > int.MinValue ? $", frontier row {Frontier}" : "")
		+ (Failures > 0 ? $", {Failures} failures" : "") + (PhaseFailures > 0 ? $", {PhaseFailures} {Phase} failures" : "");

	public override Dictionary<string, string> Save()
	{
		Dictionary<string, string> s = base.Save();
		s["shaftX"] = ShaftX.ToString();
		s["preferred"] = Preferred.ToString();
		s["standRow"] = StandRow.ToString();
		s["failures"] = Failures.ToString();
		s["columnDeaths"] = ColumnDeaths.ToString();
		s["frontier"] = Frontier.ToString();
		s["landed"] = landed ? "1" : "0";
		s["blasts"] = Blasts.ToString();
		s["columnChanged"] = ColumnChanged ? "1" : "0";
		s["failedColumns"] = string.Join(",", failedColumns);
		return s;
	}

	public override void Load(IReadOnlyDictionary<string, string> state)
	{
		base.Load(state);
		if (int.TryParse(state.GetValueOrDefault("shaftX", ""), out int x))
			ShaftX = x;
		Failures = int.TryParse(state.GetValueOrDefault("failures", "0"), out int f) ? f : 0;
		ColumnDeaths = int.TryParse(state.GetValueOrDefault("columnDeaths", "0"), out int cd) ? cd : 0;
		Frontier = int.TryParse(state.GetValueOrDefault("frontier", ""), out int fr) ? fr : int.MinValue;
		landed = state.GetValueOrDefault("landed", "0") == "1";
		Blasts = int.TryParse(state.GetValueOrDefault("blasts", "0"), out int bl) ? bl : 0;
		ColumnChanged = state.GetValueOrDefault("columnChanged", "0") == "1";
		foreach (string c in state.GetValueOrDefault("failedColumns", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
			if (int.TryParse(c, out int fc))
				failedColumns.Add(fc);
		Identity = $"the shaft at x {ShaftX}";
	}

	public static WallOfFleshErrand FromState(IReadOnlyDictionary<string, string> state, GameIds ids)
	{
		int preferred = int.TryParse(state.GetValueOrDefault("preferred", "0"), out int p) ? p : 0;
		int standRow = int.TryParse(state.GetValueOrDefault("standRow", "0"), out int r) ? r : 0;
		return new WallOfFleshErrand(preferred, standRow, ids);
	}

	private static string Describe(IReadOnlyList<Need> needs)
	{
		var parts = new List<string>();
		foreach (Need n in needs)
			parts.Add(n is HaveItem h ? $"{h.Count} {h.Label}" : n.Label);
		return string.Join(", ", parts);
	}

	private static int[] Tiers(params int[] items)
	{
		var list = new List<int>();
		foreach (int i in items)
			if (i > 0 && !list.Contains(i))
				list.Add(i);
		return list.ToArray();
	}
}

/// <summary>
/// The shaft's column from the known terrain (docs ERRAND_DESIGN.md section 8, option B: the path grid reads all terrain, so it is
/// used): among the columns within reach of the preferred one, the one whose two-wide shaft and its side walls meet the fewest
/// lava, water and sand cells on the way down, nearness breaking ties. Seen in game (world_wof M12e, M12f, X1A-X2B): the fixed column
/// x 2155 had lava under it at row 865, and every run's descent ended there. Sand (the falling blocks: sand, silt, slush) is the
/// underground desert: seen in game (E1, 2026-10-17), the column x 2260, clear of liquids, went through it at rows 490-560, among
/// antlions and tomb crawlers (a death), and a cell there could not be mined.
/// </summary>
public static class ShaftSite
{
	/// <summary>A lava cell on the way counts this many water cells (lava kills; water floods the shaft and is swum out of).</summary>
	public const int LavaWeight = 3;
	/// <summary>A falling-block cell (sand: the desert's ground, which also pours into the shaft) counts this many water cells.</summary>
	public const int SandWeight = 1;
	/// <summary>
	/// A row of a cave deeper than a safe fall (an air run of more than <see cref="DeepRows"/> rows in a shaft column) counts this many
	/// water cells: the straight dig cannot cross it (nothing to place blocks against in open air, no rope), and the pathfinder legs
	/// wandered the caves. Seen in game (E1, 2026-10-17): the shafts at x 2260 and 2267 met a cave 50 columns wide at rows 321-348,
	/// and three trips through it ended "no progress"; x 2117 met one at row 879.
	/// </summary>
	public const int DeepWeight = 2;
	/// <summary>An air run longer than this in a column is a cave the dig cannot drop through (MoveContext.MaxDrop, 23).</summary>
	public const int DeepRows = 23;
	/// <summary>Each column of distance from the preferred one costs this much of a liquid cell (ties go to the nearer column).</summary>
	public const float NearnessPerColumn = 0.05f;

	public static int Choose(ITileGrid g, int preferred, int halfWidth, int fromRow, int toRow, ICollection<int> excluded, int excludeSlack, out string why)
	{
		int best = preferred;
		float bestScore = float.MaxValue;
		int bestLava = 0, bestWater = 0, bestSand = 0, bestDeep = 0, prefLava = -1, prefWater = -1, prefSand = -1, prefDeep = -1;
		for (int c = preferred - halfWidth; c <= preferred + halfWidth; c++) {
			if (NearExcluded(c, excluded, excludeSlack))
				continue;
			Count(g, c, fromRow, toRow, out int lava, out int water, out int sand);
			int deep = DeepCaveRows(g, c, fromRow, toRow);
			if (c == preferred) {
				prefLava = lava;
				prefWater = water;
				prefSand = sand;
				prefDeep = deep;
			}
			float score = lava * LavaWeight + water + sand * SandWeight + deep * DeepWeight + Math.Abs(c - preferred) * NearnessPerColumn;
			if (score < bestScore) {
				bestScore = score;
				best = c;
				bestLava = lava;
				bestWater = water;
				bestSand = sand;
				bestDeep = deep;
			}
		}
		why = $"the shaft's column is x {best} ({bestLava} lava, {bestWater} water, {bestSand} sand cells and {bestDeep} rows of deep cave beside its way down rows {fromRow}-{toRow}"
			+ (best != preferred ? $"; the preferred x {preferred} has {Math.Max(0, prefLava)}, {Math.Max(0, prefWater)}, {Math.Max(0, prefSand)} and {Math.Max(0, prefDeep)}" : ", the preferred column") + ")";
		return best;
	}

	private static bool NearExcluded(int c, ICollection<int> excluded, int slack)
	{
		foreach (int e in excluded)
			if (Math.Abs(c - e) <= slack)
				return true;
		return false;
	}

	/// <summary>Rows of the shaft's two columns that lie in an air run longer than <see cref="DeepRows"/> (a cave the dig cannot drop through).</summary>
	public static int DeepCaveRows(ITileGrid g, int col, int fromRow, int toRow)
	{
		int deep = 0;
		for (int x = col; x <= col + 1; x++) {
			int run = 0;
			for (int y = fromRow; y <= toRow + 1; y++) {
				CellKind k = y <= toRow ? g.Get(x, y) : CellKind.Solid;
				if (!k.CanStandOn() && !k.IsLiquid())
					run++;
				else {
					if (run > DeepRows)
						deep += run;
					run = 0;
				}
			}
		}
		return deep;
	}

	/// <summary>Liquid and falling-block (sand) cells in the shaft's two columns and the wall cell either side, over the rows given.</summary>
	public static void Count(ITileGrid g, int col, int fromRow, int toRow, out int lava, out int water, out int sand)
	{
		lava = water = sand = 0;
		for (int y = fromRow; y <= toRow; y++)
			for (int x = col - 1; x <= col + 2; x++) {
				CellKind k = g.Get(x, y);
				if (k.HasLava())
					lava++;
				else if (k.IsLiquid())
					water++;
				else if ((k & CellKind.Falling) != 0)
					sand++;
			}
	}
}
