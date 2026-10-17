using System;
using System.Collections.Generic;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Progression;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Errands;

/// <summary>
/// Access to the Underworld (docs ERRAND_DESIGN.md sections 3 and 10, migration step 3; the stage row
/// <see cref="StageTable.UnderworldAccess"/>): a straight shaft (<see cref="HellevatorSkill"/>) at a column chosen from the known
/// terrain (<see cref="ShaftSite"/>: the fewest lava and water cells on the way down near the preferred offset from home), down to the
/// hellbridge's stand row. Owns the bot from the packing list to the row: the surface's stages are not looked at again on the way, a
/// chest or a crystal is taken only within a short detour (<see cref="AcceptDetourTicks"/>), consumption resupplies only the remaining
/// demand (the fight's arrows, the bridge's blocks), and a death sends the bot back down the same shaft to its frontier. Blocked
/// after <see cref="MaxFailures"/> hellevator failures in a row at one column: once, the column is chosen again (the failed one left
/// out: a method change, the goal kept); then the errand ends with the reason, and the plan decides.
/// Seen in game (world_wof M12d-M12f, X1A, X2A, 2026-10-17): the shaft was planner memory moved 81 columns a failure, to x 4909 on a
/// 4200-wide world; the descent was dropped for wood, NPC rooms and crystals 3-41 times a run; a death ended it and the surface's
/// work came first at home.
/// </summary>
public sealed class AccessErrand : Errand
{
	public const string ErrandId = "access:underworld";
	/// <summary>Columns either side of the preferred column looked at for the shaft (policy: a short walk from home).</summary>
	public const int SearchHalfWidth = 120;
	/// <summary>
	/// A chest or crystal whose detour there and back costs more than this (ticks) waits for the errand to end: 20 s of walking,
	/// about a chest in the shaft's own wall, never one 200 rows up (policy; Opportunity.Detour).
	/// </summary>
	public const int AcceptDetourTicks = 20 * 60;
	/// <summary>Hellevator failures in a row before the column is changed, and again before the errand is blocked (policy).</summary>
	public const int MaxFailures = 3;

	public const string DescendPhase = "descend";

	public override string Id => ErrandId;
	/// <summary>The shaft's column (the body's left one).</summary>
	public int ShaftX { get; private set; }
	/// <summary>The column preferred before the terrain was looked at (the bridge's way from home).</summary>
	public int Preferred { get; }
	/// <summary>The feet row to end on (the hellbridge's block row less one).</summary>
	public int StandRow { get; }
	/// <summary>The deepest feet row reached down the shaft in this life (the frontier).</summary>
	public int DeepestRow { get; private set; } = int.MinValue;
	public int Failures { get; private set; }
	public string LastFailure { get; private set; } = "";
	/// <summary>The column was chosen again once after failures (the one method change allowed).</summary>
	public bool ColumnChanged { get; private set; }
	/// <summary>Where the shaft ended (the bridge's start), once it has.</summary>
	public TilePos? Landed { get; private set; }
	private readonly HashSet<int> failedColumns = new();
	private HellevatorSkill shaft;
	private bool landed;
	private readonly GameIds ids;

	public AccessErrand(int preferred, int standRow, GameIds ids)
	{
		Preferred = preferred;
		StandRow = standRow;
		this.ids = ids;
		ShaftX = preferred;
		Packing = StageTable.UnderworldAccess.Packing(ids);
		Reserve = StageTable.UnderworldAccess.Reserve(ids);
		Identity = $"the shaft at x {ShaftX}";
	}

	/// <summary>A column an earlier access errand ended blocked at: not chosen (nor within HellevatorSkill.Slack of it).</summary>
	public void Exclude(int column) => failedColumns.Add(column);

	/// <summary>The column from the known terrain (option B, docs ERRAND_DESIGN.md section 8), once, when the errand begins.</summary>
	public override void Begin(BotContext ctx)
	{
		base.Begin(ctx);
		if (!Restored)
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

	public override bool GoalMet(BotContext ctx) => landed || HellevatorSkill.Arrived(ctx, StandRow);

	public override float Progress(BotContext ctx) => DeepestRow == int.MinValue ? 0f : DeepestRow;

	public override void Rules(BotContext ctx)
	{
		// The gear's depth limit does not hold on the way down (BotContext.DeepestRow), as the plan's errand had it.
		ctx.DepthErrand = true;
		TilePos n = ctx.Node;
		if (Phase == DescendPhase && Math.Abs(n.X - ShaftX) <= HellevatorSkill.SeekTiles && n.Y > DeepestRow)
			DeepestRow = n.Y;
	}

	public override IEnumerable<Need> RemainingDemand(BotContext ctx)
	{
		yield return new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), ProgressionPlanner.WofFightArrows, "arrows for the fight");
		yield return new BridgeBlocks("bridge blocks");
	}

	public override ISkill NextSkill(BotContext ctx, IErrandSupport planner)
	{
		if (Phase == PreparePhase) {
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
			ctx.Log.Info($"Errand {Id}: packed ({Describe(Packing, ctx)}; kept back {Describe(Reserve, ctx)}); {Identity} from {ctx.Node} to row {StandRow}");
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
		if (shaft == null) {
			shaft = new HellevatorSkill(ShaftX, StandRow);
			Status = DeepestRow > int.MinValue ? $"back down {Identity} to the frontier at row {DeepestRow}" : $"down {Identity} to row {StandRow}";
		}
		return shaft;
	}

	protected override Outcome CheckOwn(BotContext ctx)
	{
		if (GoalMet(ctx)) {
			Landed ??= ctx.Node;
			return Outcome.Done($"on row {StandRow} at {Landed}");
		}
		if (Failures >= MaxFailures) {
			if (!ColumnChanged && !LastFailure.StartsWith("the packing") && !LastFailure.StartsWith("the remaining")) {
				ColumnChanged = true;
				failedColumns.Add(ShaftX);
				Failures = 0;
				int old = ShaftX;
				ChooseColumn(ctx, $"column {old} given up after {MaxFailures} failures, the last '{LastFailure}'");
				shaft = null;
				if (ShaftX != old)
					return Outcome.Running;
			}
			return Outcome.Blocked($"{MaxFailures} hellevator failures in a row at x {ShaftX}", LastFailure);
		}
		return Outcome.Running;
	}

	public override bool Accepts(in Opportunity o, BotContext ctx) => Phase == PreparePhase || o.DetourTicks <= AcceptDetourTicks;

	public override void OnSkillFinished(ISkill skill, SkillStatus status, string why, BotContext ctx)
	{
		if (!ReferenceEquals(skill, shaft))
			return;
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
	}

	public override void OnDeath(BotContext ctx)
	{
		base.OnDeath(ctx);
		shaft = null;
		DeepestRow = int.MinValue; // this life's frontier: the way back down the dug shaft is progress again
		ctx.Log.Info($"Errand {Id}: died; back down {Identity} from the spawn (death {Deaths} of the errand)");
	}

	public override string Describe() => $"{Id} [{Phase}] {Identity} to row {StandRow}" + (DeepestRow > int.MinValue ? $", frontier row {DeepestRow}" : "") + (Failures > 0 ? $", {Failures} failures" : "");

	public override Dictionary<string, string> Save()
	{
		Dictionary<string, string> s = base.Save();
		s["shaftX"] = ShaftX.ToString();
		s["preferred"] = Preferred.ToString();
		s["standRow"] = StandRow.ToString();
		s["failures"] = Failures.ToString();
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
		ColumnChanged = state.GetValueOrDefault("columnChanged", "0") == "1";
		foreach (string c in state.GetValueOrDefault("failedColumns", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
			if (int.TryParse(c, out int fc))
				failedColumns.Add(fc);
		Identity = $"the shaft at x {ShaftX}";
	}

	public static AccessErrand FromState(IReadOnlyDictionary<string, string> state, GameIds ids)
	{
		int preferred = int.TryParse(state.GetValueOrDefault("preferred", "0"), out int p) ? p : 0;
		int standRow = int.TryParse(state.GetValueOrDefault("standRow", "0"), out int r) ? r : 0;
		return new AccessErrand(preferred, standRow, ids);
	}

	private static string Describe(IReadOnlyList<Need> needs, BotContext ctx)
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
/// lava and water cells on the way down, nearness breaking ties. Seen in game (world_wof M12e, M12f, X1A-X2B): the fixed column
/// x 2155 had lava under it at row 865, and every run's descent ended there.
/// </summary>
public static class ShaftSite
{
	/// <summary>A lava cell on the way counts this many water cells (lava kills; water floods the shaft and is swum out of).</summary>
	public const int LavaWeight = 3;
	/// <summary>Each column of distance from the preferred one costs this much of a liquid cell (ties go to the nearer column).</summary>
	public const float NearnessPerColumn = 0.05f;

	public static int Choose(ITileGrid g, int preferred, int halfWidth, int fromRow, int toRow, ICollection<int> excluded, int excludeSlack, out string why)
	{
		int best = preferred;
		float bestScore = float.MaxValue;
		int bestLava = 0, bestWater = 0, prefLava = -1, prefWater = -1;
		for (int c = preferred - halfWidth; c <= preferred + halfWidth; c++) {
			if (NearExcluded(c, excluded, excludeSlack))
				continue;
			Count(g, c, fromRow, toRow, out int lava, out int water);
			if (c == preferred) {
				prefLava = lava;
				prefWater = water;
			}
			float score = lava * LavaWeight + water + Math.Abs(c - preferred) * NearnessPerColumn;
			if (score < bestScore) {
				bestScore = score;
				best = c;
				bestLava = lava;
				bestWater = water;
			}
		}
		why = $"the shaft's column is x {best} ({bestLava} lava and {bestWater} water cells beside its way down rows {fromRow}-{toRow}"
			+ (best != preferred ? $"; the preferred x {preferred} has {Math.Max(0, prefLava)} and {Math.Max(0, prefWater)}" : ", the preferred column") + ")";
		return best;
	}

	private static bool NearExcluded(int c, ICollection<int> excluded, int slack)
	{
		foreach (int e in excluded)
			if (Math.Abs(c - e) <= slack)
				return true;
		return false;
	}

	/// <summary>Liquid cells in the shaft's two columns and the wall cell either side, over the rows given.</summary>
	public static void Count(ITileGrid g, int col, int fromRow, int toRow, out int lava, out int water)
	{
		lava = water = 0;
		for (int y = fromRow; y <= toRow; y++)
			for (int x = col - 1; x <= col + 2; x++) {
				CellKind k = g.Get(x, y);
				if (k.HasLava())
					lava++;
				else if (k.IsLiquid())
					water++;
			}
	}
}
