using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// A hellevator (docs M12_SPEC.md section 1, S3, G4): a shaft dug down from the surface at <see cref="ShaftX"/> to the Underworld,
/// ending standing on solid ground at the hellbridge's row (<see cref="StandRow"/>, the feet's).
/// <list type="bullet">
/// <item>Down in legs of <see cref="LegRows"/> rows, each a walk to "at or below the row" within <see cref="Slack"/> columns of
/// the shaft (<see cref="GoalRow"/> with Below): the paths dig down tile by tile with no fall at all, and keep the lava rules (never
/// open a cell with lava beside or above it, BlockRules), so a lava pocket is gone round, not dug into ("solve, don't ban").</item>
/// <item>After each leg, a ledge: one block over the head in the shaft (<see cref="PlaceLedge"/>), so a later trip down is a series
/// of drops of at most <see cref="LegRows"/> rows, within the paths' safe drop (MoveContext.MaxDrop, 23): there is no rope or
/// mirror, and a death sends the bot home (spec 1b).</item>
/// <item>Once a leg can go no deeper (the shaft has broken through the ash ceiling over the open band, and no ground is within a
/// safe drop under it: the band opens 20-50 rows over the lava sea, spec 1b) within <see cref="FinalLegRows"/> of the row, or
/// once within a leg of it, the last leg is a walk onto that row anywhere within <see cref="SeekTiles"/> of the shaft: through the
/// ceiling and down onto ground on the row (the ruined tower's roof in the M12 world), no drop deeper than a safe fall.</item>
/// </list>
/// Started farther than <see cref="SeekTiles"/> from the shaft (a hellevator taken up again after a fight, a chest or another
/// stage took the bot off), it first goes to the shaft's top: up to near the surface if deep, then along it (<see cref="Approach"/>).
/// Fails with the reason when a leg finds no way (the planner moves the shaft), or when the way to the shaft is not found.
/// </summary>
public sealed class HellevatorSkill : ISkill
{
	/// <summary>Rows a leg goes down, and between ledges: within the paths' safe drop (MoveContext.MaxDrop = SafeFallTiles - 2 = 23).</summary>
	public const int LegRows = 20;
	/// <summary>Columns either side of the shaft a leg may end in: room for the paths to go round a lava cell (spec 1b: 1-4 of them).</summary>
	public const int Slack = 6;
	/// <summary>
	/// A leg that gets no deeper within this many rows of the stand row has met the open band: the ash ceiling's lower edge is
	/// generated between maxTilesY - 190 and - 160 (WorldGen.cs:10125-10136), 30 rows, and the stand row is 2 rows under it at most.
	/// </summary>
	public const int FinalLegRows = 40;
	/// <summary>Columns either side of the shaft the last leg may land in (the tower's roof is 9 wide, 1-10 columns from the shaft).</summary>
	public const int SeekTiles = 40;
	/// <summary>Legs in a row that got nowhere before the shaft is given up.</summary>
	public const int MaxFailedLegs = 3;
	/// <summary>Ticks to put a ledge in before going on without it.</summary>
	public const int LedgeTicks = 60;
	/// <summary>
	/// Farther than this (columns) from the shaft, the bot goes to its top before digging: a leg's goal (<see cref="LegRows"/> down
	/// within <see cref="Slack"/> of the shaft) hundreds of columns off is past the search's reach. Seen in game (world_wof M12d,
	/// Peppe): the hellevator at x 2155 taken up again from a cave at (1581,663), its legs "no path" at 40001 nodes three times,
	/// "no way down", and the planner moved the shaft east by 81 columns, again and again, to x 3127.
	/// </summary>
	public const int ApproachTiles = SeekTiles;

	private WalkToSkill leg, approach;
	private int failedApproaches;
	/// <summary>The column the last leg seeks the row round: the shaft's, or the bot's when it is down there already far from it.</summary>
	private int? seekX;
	private bool finalLeg, forceFinal;
	private int failedLegs, ledges, legStartY;
	private TilePos? ledgeCell;
	private int ledgeTicks;
	private int startY = int.MinValue;

	public int ShaftX { get; }
	/// <summary>The feet row to end on: the hellbridge's block row less one.</summary>
	public int StandRow { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hellevator";
	public string Key => $"Hellevator {ShaftX} {StandRow}";
	/// <summary>Where the descent ended (the bridge's start), once it has.</summary>
	public TilePos? Landed { get; private set; }

	public HellevatorSkill(int shaftX, int standRow)
	{
		ShaftX = shaftX;
		StandRow = standRow;
	}

	public string Describe() => $"Hellevator x {ShaftX} to row {StandRow} [{State}]" + (leg != null ? " > " + leg.Describe() : approach != null ? " > " + approach.Describe() : "");

	public void Start(BotContext ctx)
	{
		if (startY == int.MinValue)
			startY = ctx.Node.Y;
		ctx.Log.Info($"Hellevator start: a shaft at x {ShaftX} from {ctx.Node} down to feet row {StandRow} ({StandRow - ctx.Node.Y} rows)");
	}

	public void Stop(BotContext ctx)
	{
		leg?.Stop(ctx);
		leg = null;
		approach?.Stop(ctx);
		approach = null;
	}

	public void Pause(BotContext ctx)
	{
		leg?.Pause(ctx);
		approach?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		leg?.Resume(ctx);
		approach?.Resume(ctx);
	}

	/// <summary>Standing on the stand row on solid ground (the bridge's start).</summary>
	public static bool Arrived(BotContext ctx, int standRow) =>
		ctx.Node.Y == standRow && ctx.Body.Grounded && (ctx.Grid.Get(ctx.Node.X, standRow + 1).IsSolid() || ctx.Grid.Get(ctx.Node.X + 1, standRow + 1).IsSolid());

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		}
		if (!ctx.AllowBreak)
			return Fail(ctx, "digging is off");
		TilePos n = ctx.Node;
		if (Arrived(ctx, StandRow)) {
			Landed = n;
			ctx.Log.Info($"Hellevator done: standing at {n} on the row, {n.Y - (startY == int.MinValue ? n.Y : startY)} rows down from where it began, {ledges} ledges left in the shaft");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (ledgeCell is TilePos cell) {
			// The ledge: a block over the head (its side neighbour is the shaft's wall), placed between legs.
			State = $"ledge at {cell}";
			if (ctx.Grid.Get(cell.X, cell.Y).IsSolid() || ++ledgeTicks > LedgeTicks) {
				if (ctx.Grid.Get(cell.X, cell.Y).IsSolid())
					ledges++;
				ledgeCell = null;
			}
			else {
				ctx.Output.Tool = ToolRequest.Block;
				ctx.Output.Aim(cell.X, cell.Y);
				ctx.Output.UseItem = true;
				return SkillStatus.Running;
			}
		}
		// Down there already (within the last leg's rows or under the row), far from the shaft: onto the row from here, never back up
		// to the shaft. Seen in game (world_wof M12d): in the Underworld at (2152,1061) by the old shaft at 2155, the shaft moved to
		// 2479, and the last leg walked the lava's edge 300 columns toward it, burnt to death twice.
		if (leg == null && !forceFinal && StandRow - n.Y <= FinalLegRows && Math.Abs(n.X - ShaftX) > SeekTiles) {
			forceFinal = true;
			seekX = n.X;
			ctx.Log.Info($"Hellevator: down at {n} already, {Math.Abs(n.X - ShaftX)} columns from the shaft; onto row {StandRow} within {SeekTiles} of here");
		}
		if (leg == null && !forceFinal && Math.Abs(n.X - ShaftX) > ApproachTiles)
			return Approach(ctx, n);
		if (leg == null) {
			finalLeg = forceFinal || StandRow - n.Y <= LegRows;
			legStartY = n.Y;
			int to = Math.Min(n.Y + LegRows, StandRow);
			int around = seekX ?? ShaftX;
			IGoal goal = finalLeg
				? new GoalStandOnRow(StandRow, around - SeekTiles, around + SeekTiles, ctx.Physics)
				: new GoalRow(to, ShaftX, ctx.Physics) { Below = true, Accept = t => Math.Abs(t.X - ShaftX) <= Slack };
			leg = new WalkToSkill(goal) { Quiet = true, Underground = true };
			leg.Start(ctx);
			State = finalLeg ? $"down onto row {StandRow} within {SeekTiles} of x {around}" : $"digging down to row {to}";
		}
		SkillStatus s = leg.Tick(ctx);
		if (s == SkillStatus.Running)
			return SkillStatus.Running;
		string why = leg.FailureReason;
		leg.Stop(ctx);
		leg = null;
		n = ctx.Node;
		if (s == SkillStatus.Failed || n.Y <= legStartY && !Arrived(ctx, StandRow)) {
			if (!finalLeg && StandRow - n.Y <= FinalLegRows) {
				forceFinal = true;
				ctx.Log.Info($"Hellevator: no deeper from {n} within {Slack} of the shaft (the ceiling's underside: open below, no ground within a safe drop); "
					+ $"looking for a way onto row {StandRow} within {SeekTiles} columns");
				return SkillStatus.Running;
			}
			if (++failedLegs >= MaxFailedLegs)
				return Fail(ctx, $"no way down from {n} at x {ShaftX} ({failedLegs} legs got nowhere{(string.IsNullOrEmpty(why) ? "" : ": " + why)})");
			ctx.Log.Info($"Hellevator: the leg from row {legStartY} got nowhere ({why}); again from {n}");
			return SkillStatus.Running;
		}
		failedLegs = 0;
		if (!finalLeg)
			PlaceLedge(ctx, n);
		ctx.Log.Info($"Hellevator: at {n}, {StandRow - n.Y} rows to go");
		return SkillStatus.Running;
	}

	/// <summary>
	/// The way to the shaft from far off: deep down, up to near the surface's height first (a walk to one far tile from the caves
	/// wanders them, ProgressionPlanner.ExitWalk); then to the shaft's top, the highest ground within <see cref="Slack"/> of it.
	/// </summary>
	private SkillStatus Approach(BotContext ctx, TilePos n)
	{
		if (approach == null) {
			int surface = Brain.ProgressionPlanner.SurfaceRow(ctx);
			if (surface > 0 && n.Y > surface + Brain.ProgressionPlanner.TravelFromSurfaceRows) {
				int row = surface + Brain.ProgressionPlanner.TravelFromSurfaceRows - 3;
				approach = new WalkToSkill(new GoalRow(row, n.X, ctx.Physics) { Accept = t => !ctx.OffLimits(t) }) { Quiet = true, ToSurface = true };
				State = $"up to row {row}, {Math.Abs(n.X - ShaftX)} columns from the shaft";
			}
			else {
				TilePos top = ShaftTop(ctx);
				approach = new WalkToSkill(new GoalNearTile(top, Slack, ctx.Physics)) { Quiet = true };
				State = $"to the shaft's top at {top}";
			}
			ctx.Log.Info($"Hellevator: {Math.Abs(n.X - ShaftX)} columns from the shaft at {n}; {State} first");
			approach.Start(ctx);
		}
		SkillStatus s = approach.Tick(ctx);
		if (s == SkillStatus.Running)
			return SkillStatus.Running;
		string why = approach.FailureReason;
		approach.Stop(ctx);
		approach = null;
		if (s == SkillStatus.Failed && ++failedApproaches >= MaxFailedLegs)
			return Fail(ctx, $"no way to the shaft at x {ShaftX} from {ctx.Node} ({failedApproaches} walks failed{(string.IsNullOrEmpty(why) ? "" : ": " + why)})");
		if (s == SkillStatus.Succeeded)
			failedApproaches = 0;
		return SkillStatus.Running;
	}

	/// <summary>The highest ground to stand on within <see cref="Slack"/> columns of the shaft, coming down from the sky.</summary>
	private TilePos ShaftTop(BotContext ctx)
	{
		TilePos best = new(ShaftX, Math.Max(1, ctx.SurfaceY));
		bool found = false;
		for (int x = ShaftX - Slack; x <= ShaftX + Slack; x++)
			for (int y = 1; y < ctx.Node.Y + 400 && y < StandRow; y++) {
				if (ctx.Grid.Get(x, y + 1).CanStandOn() && Body.IsStandable(ctx.Grid, new TilePos(x, y))) {
					if (!found || y < best.Y || y == best.Y && Math.Abs(x - ShaftX) < Math.Abs(best.X - ShaftX))
						best = new TilePos(x, y);
					found = true;
					break;
				}
				if (found && y > best.Y)
					break;
			}
		return best;
	}

	/// <summary>
	/// The ledge's cell: over the head in the body's left column (the cell 3 rows above the feet ends 6 px above the body's top, so
	/// the game's no-overlap rule for solid blocks lets it in), if it is open and held by a solid neighbour (the shaft's wall).
	/// </summary>
	private void PlaceLedge(BotContext ctx, TilePos n)
	{
		var cell = new TilePos(n.X, n.Y - Body.HeightTiles);
		if (!ctx.AllowPlace || ctx.Blocks == null || ctx.Blocks.BlockCount <= 0 || ctx.Grid.Get(cell.X, cell.Y).IsSolid() || ctx.Grid.Get(cell.X, cell.Y).IsLiquid())
			return;
		if (!ctx.Grid.Get(cell.X - 1, cell.Y).IsSolid() && !ctx.Grid.Get(cell.X + 1, cell.Y).IsSolid() && !ctx.Grid.Get(cell.X, cell.Y - 1).IsSolid())
			return;
		ledgeCell = cell;
		ledgeTicks = 0;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Hellevator failed: {reason}");
		return SkillStatus.Failed;
	}
}
