using System.Collections.Generic;
using System.Linq;
using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// A hellevator (docs M12_SPEC.md section 1, S3, G4): a shaft dug down from the surface at <see cref="ShaftX"/> to the Underworld,
/// ending standing on solid ground at the hellbridge's row (<see cref="StandRow"/>, the feet's).
/// <para>
/// A straight drop two columns wide, as a player digs one (the user, 2026-10-17: "I want it to dig a 2 wide drop down to hell"):
/// standing on the shaft's column, the two cells under the feet mined, the body down into them, again (<see cref="Straight"/>,
/// each row read first by <see cref="StraightShaft.Below"/>); no path search. Only where the column cannot go on (lava under it
/// with no clear column within <see cref="Slack"/> to step over to, a cell the pickaxe cannot mine, a drop deeper than a safe fall)
/// one leg below (as follows) takes it past, and the straight dig goes on from where that lands. Seen in game (world_wof M12d-X2B):
/// with legs for the whole way, each a search for "20 rows down within 6 of the column", the bot dug about the caves 50-70
/// columns off the shaft (cobwebs under it at rows 867-881, which no dig move passes), up and down between rows 786 and 885.
/// </para>
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
	/// <summary>
	/// Deep down and within this many columns of the shaft, the way to it is over at this depth (the paths dig); farther off it is
	/// up to the surface and along (a walk through hundreds of columns of rock is past the search's reach: world_wof M12d, 570 columns).
	/// </summary>
	public const int SidewaysTiles = 100;

	private WalkToSkill leg, approach, align;
	private int failedApproaches;
	/// <summary>
	/// Deep down and off the shaft's line with no way over to it: the shaft goes on down from the column the body is on, and the
	/// approach is not tried again in this life (the errand adopts the column, WallOfFleshErrand.Rules).
	/// </summary>
	private bool approachGivenUp;
	/// <summary>The column the straight dig is on (the body's left one): the shaft's, or where a step round lava or a leg put it.</summary>
	private int col = int.MinValue;
	private int alignTries, openTicks, lastLedgeY = int.MinValue, lastLoggedY = int.MinValue;
	/// <summary>
	/// The column is a step over round lava or water (<see cref="StepOver"/>), not yet reached. Not reached at all, the stretch goes
	/// to a leg, never back to digging beside the liquid. Seen in game (world_wof E2, 2026-10-17): "stepping over to column 2125"
	/// and "the shaft goes on from column 2127 (column 2125 not reached)" twice a tick for 2 game hours at (2129,749), until the
	/// errand's watchdog blocked the descent.
	/// </summary>
	private bool steppingOver;
	private TilePos? mineCell;
	private int mineTicks;
	/// <summary>
	/// Digging the straight shaft (not a leg, not the approach): the brain's breath reflex then surfaces only when the way up needs
	/// the breath left (BotBrain), not at three quarters. Seen in game (S1): the shaft through a flooded stretch at rows 760-777,
	/// surfaced at 149/200 breath about ten times, each a pillar of 6 blocks in the shaft, mined again on the way back down.
	/// </summary>
	public bool DiggingStraight => leg == null && approach == null && !forceFinal && col != int.MinValue;
	/// <summary>The next stretch is a leg (the straight dig could not go on: lava, a cell too hard, a deep drop).</summary>
	private bool legNext;
	/// <summary>Ticks on one cell of the floor before the straight dig gives that stretch to a leg (a pickaxe takes a stone in about 23).</summary>
	public const int MineCellTicks = 240;
	/// <summary>Tries to walk onto the shaft's column from beside it before the dig goes on from the column the body is on.</summary>
	public const int MaxAlignTries = 4;
	/// <summary>Rows of the column read ahead before stepping over to it round lava (<see cref="StraightShaft.TroubleAhead"/>).</summary>
	public const int StepOverLookRows = 12;
	/// <summary>The column the last leg seeks the row round: the shaft's, or the bot's when it is down there already far from it.</summary>
	private int? seekX;
	private bool finalLeg, forceFinal;
	private int failedLegs, ledges, legStartY;
	private TilePos? ledgeCell;
	private int ledgeTicks;
	private int startY = int.MinValue;

	public int ShaftX { get; }
	/// <summary>The column the straight dig is on now (int.MinValue before it began): the shaft's, or where the dig went on from.</summary>
	public int Column => col;
	/// <summary>The feet row to end on: the hellbridge's block row less one.</summary>
	public int StandRow { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hellevator";
	public string Key => $"Hellevator {ShaftX} {StandRow}";
	/// <summary>Where the descent ended (the bridge's start), once it has.</summary>
	public TilePos? Landed { get; private set; }

	public HellevatorSkill(int shaftX, int standRow, IReadOnlyList<(int Row, int Col)> route = null)
	{
		ShaftX = shaftX;
		StandRow = standRow;
		if (route != null)
			this.route.AddRange(route.OrderBy(r => r.Row));
		FollowsRoute = this.route.Count > 0;
	}

	/// <summary>
	/// The shaft as it was dug: from each row on, the column it goes down (a step round lava or water, a leg, a column it went on from).
	/// A later trip down follows it: down the open shaft already dug, onto the next column where it moved over, digging only where the
	/// route is blocked now, and that new stretch replaces the route below it. Navigation design (docs NAVIGATION_GAPS.md section 5,
	/// after Baritone's backtrack favoring): seen in game (world_wof E24, E28, 2026-10-18), every trip back after a death dug a fresh
	/// column (2117, 2177, 2172, 2158, 2152), met new water and lava pockets, and took up to 15 game hours.
	/// </summary>
	public IReadOnlyList<(int Row, int Col)> Route => route;
	private readonly List<(int Row, int Col)> route = new();
	/// <summary>The trip was started with a route from an earlier one.</summary>
	public bool FollowsRoute { get; }

	/// <summary>The column the shaft goes down at <paramref name="row"/>: the route's, else the shaft's.</summary>
	public int ColumnAt(int row)
	{
		int c = ShaftX;
		foreach (var (r, x) in route) {
			if (r > row)
				break;
			c = x;
		}
		return c;
	}

	/// <summary>The dig is on column <paramref name="c"/> from <paramref name="row"/> on: the route below is this branch now.</summary>
	private void Note(int row, int c)
	{
		if (c == int.MinValue || ColumnAt(row) == c)
			return; // as the route has it
		route.RemoveAll(r => r.Row >= row);
		if (ColumnAt(row) != c)
			route.Add((row, c));
	}

	public string Describe() => $"Hellevator x {ShaftX} to row {StandRow} [{State}]"
		+ (leg != null ? " > " + leg.Describe() : approach != null ? " > " + approach.Describe() : align != null ? " > " + align.Describe() : "");

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
		align?.Stop(ctx);
		align = null;
		mineCell = null;
	}

	public void Pause(BotContext ctx)
	{
		leg?.Pause(ctx);
		approach?.Pause(ctx);
		align?.Stop(ctx);
		align = null;
		mineCell = null;
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
		if (leg == null && !forceFinal && StandRow - n.Y <= FinalLegRows && Math.Abs(n.X - ColumnAt(n.Y)) > SeekTiles) {
			forceFinal = true;
			seekX = n.X;
			ctx.Log.Info($"Hellevator: down at {n} already, {Math.Abs(n.X - ColumnAt(n.Y))} columns from the shaft; onto row {StandRow} within {SeekTiles} of here");
		}
		// Once begun, the walk to the shaft's top goes on to it (seen in game, S1: given up 40 columns off, the shaft was dug at 2115).
		if (leg == null && !forceFinal && !approachGivenUp && (approach != null || Math.Abs(n.X - ColumnAt(n.Y)) > ApproachTiles))
			return Approach(ctx, n);
		// The straight drop, down to the last leg's rows (the ceiling and the open band over the lava are the last leg's).
		if (leg == null && !forceFinal && !legNext && StandRow - n.Y > FinalLegRows && Straight(ctx, n) is SkillStatus dug)
			return dug;
		if (leg == null) {
			legNext = false;
			finalLeg = forceFinal || StandRow - n.Y <= LegRows;
			legStartY = n.Y;
			int to = Math.Min(n.Y + LegRows, StandRow);
			int around = seekX ?? ColumnAt(n.Y);
			int legX = ColumnAt(to);
			IGoal goal = finalLeg
				? new GoalStandOnRow(StandRow, around - SeekTiles, around + SeekTiles, ctx.Physics)
				: new GoalRow(to, legX, ctx.Physics) { Below = true, Accept = t => Math.Abs(t.X - legX) <= Slack };
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
		if (!finalLeg) {
			PlaceLedge(ctx, n);
			lastLedgeY = n.Y;
		}
		// The straight dig goes on from where the leg came down (beside the shaft: back onto it first).
		col = Math.Abs(n.X - ColumnAt(n.Y)) <= Slack ? n.X : ColumnAt(n.Y);
		Note(n.Y, col);
		ctx.Log.Info($"Hellevator: at {n}, {StandRow - n.Y} rows to go");
		return SkillStatus.Running;
	}

	/// <summary>
	/// One tick of the straight drop on column <see cref="col"/>: falling (kept over the column), onto the column, the floor's two
	/// cells mined, a ledge every <see cref="LegRows"/> rows, a step over round lava; null when this stretch needs a leg instead.
	/// </summary>
	private SkillStatus? Straight(BotContext ctx, TilePos n)
	{
		var p = ctx.Physics;
		if (col == int.MinValue) {
			col = ColumnAt(n.Y);
			Note(n.Y, col);
		}
		// On a route from an earlier trip: where it moved over, onto its column (the dug way), never a fresh column beside it.
		if (FollowsRoute && ctx.Body.Grounded && !steppingOver && ColumnAt(n.Y) != col) {
			col = ColumnAt(n.Y);
			alignTries = 0;
			steppingOver = true;
			ctx.Log.Info($"Hellevator: the dug shaft goes on at column {col} from row {n.Y} (the route of an earlier trip)");
		}
		if (!ctx.Body.Grounded) {
			ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(col, p), p);
			State = $"dropping down the shaft at x {col}";
			return SkillStatus.Running;
		}
		if (lastLedgeY == int.MinValue)
			lastLedgeY = n.Y;
		if (lastLoggedY == int.MinValue || n.Y - lastLoggedY >= LegRows) {
			lastLoggedY = n.Y;
			ctx.Log.Info($"Hellevator: at {n}, {StandRow - n.Y} rows to go (straight, column {col})");
		}
		// A ledge every LegRows rows: a later trip down is a series of safe drops onto them (each mined and put back on the way).
		if (n.Y - lastLedgeY >= LegRows && ledgeCell == null) {
			lastLedgeY = n.Y;
			PlaceLedge(ctx, n);
			if (ledgeCell != null)
				return SkillStatus.Running;
		}
		if (n.X != col) {
			// Back onto the column (a fight, a surfacing pushed the body off it); seen in game (S1): given up after two tries, the
			// shaft went on from wherever the body was, 2149, 2138, 2120.
			if (Math.Abs(n.X - col) <= ApproachTiles && alignTries < MaxAlignTries) {
				if (align == null) {
					// Onto the column at this row or below it: the shaft under the spot is dug out (nothing to stand on there), so
					// the way back is into the shaft (seen in the test: StandAt the column's cell ended beside it, a column off).
					int onto = col;
					align = new WalkToSkill(new GoalRow(n.Y, col, ctx.Physics) { Below = true, Accept = t => t.X == onto }) { Quiet = true, Underground = true, MaxNoPathAttempts = 1 };
					align.Start(ctx);
					alignTries++;
				}
				SkillStatus a = align.Tick(ctx);
				State = $"onto the shaft's column x {col}";
				if (a == SkillStatus.Running)
					return SkillStatus.Running;
				align.Stop(ctx);
				align = null;
				if (ctx.Node.X == col) {
					alignTries = 0;
					steppingOver = false;
				}
				return SkillStatus.Running;
			}
			if (steppingOver) {
				ctx.Log.Info($"Hellevator: column {col} (the step over the liquid) not reached from {n}; a leg round it instead");
				steppingOver = false;
				col = n.X;
				Note(n.Y, col);
				alignTries = 0;
				legNext = true;
				return null;
			}
			// On a route from an earlier trip: a leg back to it, not a fresh column from here (the route below stays). Seen in game
			// (world_wof E32, 2026-10-18): a fight chased a Skeleton 17 columns off at row 474, the dig went on from there, and the
			// route recorded below (rows 585-704, round a water pocket) was thrown away for a fresh column through new pockets.
			if (FollowsRoute && failedLegs < MaxFailedLegs - 1) {
				ctx.Log.Info($"Hellevator: column {col} of the route not reached from {n}; a leg back to it");
				alignTries = 0;
				legNext = true;
				return null;
			}
			ctx.Log.Info($"Hellevator: the shaft goes on from column {n.X} (column {col} not reached{(col != ShaftX ? $", x {ShaftX} planned" : "")})");
			col = n.X;
			Note(n.Y, col);
		}
		steppingOver = false;
		ShaftRow row = StraightShaft.Below(ctx.Moves, col, n.Y);
		switch (row.Kind) {
			case ShaftKind.Dig: {
				openTicks = 0;
				TilePos? next = null;
				foreach (TilePos c in row.Cells)
					if (ctx.Grid.Get(c.X, c.Y).IsBlockTile()) {
						next = c;
						break;
					}
				if (next is not TilePos cell) {
					State = "the floor is open; dropping";
					return SkillStatus.Running;
				}
				if (mineCell != cell) {
					mineCell = cell;
					mineTicks = 0;
				}
				if (++mineTicks > MineCellTicks) {
					ctx.Log.Info($"Hellevator: mining {cell} under the shaft did nothing in {MineCellTicks} ticks (it reads {ctx.Grid.Get(cell.X, cell.Y)}); a leg past it");
					mineCell = null;
					legNext = true;
					return null;
				}
				ctx.Output.Tool = ToolRequest.Pickaxe;
				ctx.Output.Aim(cell.X, cell.Y);
				ctx.Output.UseItem = true;
				ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(col, p), p);
				State = $"digging {cell}";
				return SkillStatus.Running;
			}
			case ShaftKind.Open:
				// Dug before (the shaft from an earlier trip): nothing to mine, the body drops; or a platform under the feet.
				ctx.Output.Down = true;
				ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(col, p), p);
				State = "dropping down the dug shaft";
				if (row.LandFeet == n.Y && ++openTicks > LedgeTicks) {
					openTicks = 0;
					legNext = true;
					return null;
				}
				return SkillStatus.Running;
			case ShaftKind.Lava:
				if (StepOver(ctx, n) is int over) {
					ctx.Log.Info($"Hellevator: lava or water beside the shaft at {string.Join(" ", row.Cells)}; stepping over to column {over} (clear for {StepOverLookRows} rows)");
					col = over;
					Note(n.Y, col);
					alignTries = 0;
					steppingOver = true;
					return SkillStatus.Running;
				}
				ctx.Log.Info($"Hellevator: lava or water beside the shaft at {string.Join(" ", row.Cells)} and no clear column within {Slack}; a leg round it");
				legNext = true;
				return null;
			default:
				ctx.Log.Info($"Hellevator: the column cannot go on at {n} ({row.Kind}: {row.Why}); a leg past it");
				legNext = true;
				return null;
		}
	}

	/// <summary>The nearest column within <see cref="Slack"/> whose floor and next rows are clear of lava, reachable on the feet row.</summary>
	private int? StepOver(BotContext ctx, TilePos n)
	{
		Core.Pathfinding.MoveContext m = ctx.Moves;
		for (int d = 2; d <= Slack; d++)
			foreach (int c in new[] { col + d, col - d }) {
				ShaftRow r = StraightShaft.Below(m, c, n.Y);
				if (r.Kind is not (ShaftKind.Dig or ShaftKind.Open))
					continue;
				if (StraightShaft.TroubleAhead(m, c, n.Y + 1, StepOverLookRows) != null)
					continue;
				if (StraightShaft.SideStepsClear(m, n.X, c, n.Y))
					return c;
			}
		return null;
	}

	/// <summary>
	/// The way to the shaft from far off: deep down, up to near the surface's height first (a walk to one far tile from the caves
	/// wanders them, ProgressionPlanner.ExitWalk); then to the shaft's top, the highest ground within <see cref="Slack"/> of it.
	/// </summary>
	private SkillStatus Approach(BotContext ctx, TilePos n)
	{
		// Deep down: over to the shaft's line at this depth or lower (the paths dig), never up to the surface to come down again; with
		// no way over (a cave between), the shaft goes on down from the column the body is on (approachGivenUp), as a player digs
		// down where they stand. Seen in game (world_wof E1, 2026-10-17): 46 columns off the shaft at row 404 the old rule went "up
		// to row 292", and the errand's watchdog blocked it; X1A and X2A climbed 600 rows to come down the same shaft again.
		int surface = Brain.ProgressionPlanner.SurfaceRow(ctx);
		bool deep = surface > 0 && n.Y > surface + Brain.ProgressionPlanner.TravelFromSurfaceRows;
		bool sideways = deep && Math.Abs(n.X - ShaftX) <= SidewaysTiles;
		if (approach == null) {
			if (sideways) {
				int shaftX = ShaftX, row = n.Y;
				approach = new WalkToSkill(new GoalRow(row, shaftX, ctx.Physics) { Below = true, Accept = t => Math.Abs(t.X - shaftX) <= Slack }) { Quiet = true, Underground = true };
				State = $"over to the shaft's line at row {row} or below, {Math.Abs(n.X - ShaftX)} columns off";
			}
			else if (deep) {
				// Far off and deep: up to near the surface's height first (a walk to one far tile from the caves wanders them,
				// ProgressionPlanner.ExitWalk), then to the shaft's top.
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
		if (s == SkillStatus.Failed && sideways) {
			approachGivenUp = true;
			col = ctx.Node.X;
			Note(ctx.Node.Y, col);
			ctx.Log.Info($"Hellevator: no way over to the shaft's line from {ctx.Node} ({why}); the shaft goes on down from column {col}");
			return SkillStatus.Running;
		}
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
