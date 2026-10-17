using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The hellbridge (docs M12_SPEC.md section 5d, S4, G5): a walking builder that lays one row of blocks (<see cref="Row"/>) from
/// <see cref="FromX"/> to <see cref="ToX"/> over the Underworld's open band, the arena the Wall of Flesh is fought along. Not a site
/// plan: the builder's sites rule out liquid and its chasm bridges stop at 40 tiles, and this one is 1000-1750 long over lava.
/// Each step, standing at the bridge's end:
/// <list type="number">
/// <item>mine what is solid in the next column's <see cref="ClearRows"/> rows over the bridge (the body's three and one more for a
/// jump's head): the ash and the ruined houses' bricks, none of them pick-gated at this depth (spec 5b), top down; never a cell that
/// lets lava in (BlockRules: lava beside or above it), and lava streaming through the body's rows is waited out
/// (<see cref="LavaWaitTicks"/>) as it drains through the gap the next block fills;</item>
/// <item>if the next column's bridge cell is not solid, place a block there: it touches the bridge's last block, which holds it (the
/// game refuses blocks only into lava, TerrariaBlockActions.CanPlaceAt, not over far lava);</item>
/// <item>walk one tile onto it (<see cref="Arrive"/> steering: from rest, never past the end).</item>
/// </list>
/// The state is read from the tiles each tick (the end is the last solid cell from the start), so a fight's pause, a knock off it and
/// the walk back, or a death and a return all go on where the bridge ends.
/// </summary>
public sealed class HellbridgeSkill : ISkill
{
	/// <summary>Rows over the bridge kept open: the body's 3 and a 4th for the head on a jump (spec 5b: "body clearance 4 rows").</summary>
	public const int ClearRows = 4;
	/// <summary>Ticks an action (a mine, a place) gets before it counts as failed; three failures on a cell fail the skill.</summary>
	public const int ActionTicks = 90;
	public const int MaxFailuresPerCell = 3;
	/// <summary>Ticks lava in the body's rows ahead is waited for to drain (a pocket runs down through the open bridge cell in a few seconds).</summary>
	public const int LavaWaitTicks = 300;
	/// <summary>Ticks to wait for the hotbar to be stocked with blocks from the bag (the adapter does it every 30).</summary>
	public const int MaxWaitForBlocks = 90;
	/// <summary>The progress is logged at these shares of the length (the spec's milestones: hellbridge 25 / 50 / 75 %).</summary>
	public static readonly int[] ProgressPercents = { 25, 50, 75 };

	public int Row { get; }
	public int FromX { get; }
	public int ToX { get; }
	public int Dir => ToX >= FromX ? 1 : -1;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hellbridge";
	public string Key => $"Hellbridge {Row} {FromX} {ToX}";
	/// <summary>The bridge's last solid column from the start, as last read.</summary>
	public int End { get; private set; }
	/// <summary>Blocks placed and cells mined by this instance.</summary>
	public int Placed { get; private set; }
	public int Mined { get; private set; }

	private WalkToSkill walk;
	private TilePos? actionCell;
	private bool actionPlace;
	private int actionTicks, lavaTicks, waitForBlocks, loggedPercent, lastEnd = int.MinValue;
	private readonly System.Collections.Generic.Dictionary<TilePos, int> failures = new();

	public HellbridgeSkill(int row, int fromX, int toX)
	{
		Row = row;
		FromX = fromX;
		ToX = toX;
	}

	public double Progress => Math.Clamp((End - FromX) / (double)(ToX - FromX), 0.0, 1.0);

	public string Describe() => $"Hellbridge row {Row} {FromX}->{ToX} at {End} ({Progress:P0}) [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		End = BridgeEnd(ctx.Grid, Row, FromX, ToX);
		ctx.Log.Info($"Hellbridge start: row {Row} from x {FromX} to {ToX} ({Math.Abs(ToX - FromX)} tiles), built to {End}, "
			+ $"{(ctx.Inventory != null ? ctx.Inventory.CountAny(ctx.Ids.BuildingBlocks) + ctx.Inventory.Count(ctx.Ids.AshBlock) : 0)} blocks carried");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		actionCell = null;
	}

	public void Pause(BotContext ctx) => Stop(ctx);
	public void Resume(BotContext ctx) { }

	/// <summary>
	/// The last column of the bridge going from <paramref name="fromX"/> toward <paramref name="toX"/> (the start itself if the next
	/// is not): its cell in the row solid and the body's rows over it open (<see cref="Built"/>).
	/// </summary>
	public static int BridgeEnd(ITileGrid g, int row, int fromX, int toX)
	{
		int dir = toX >= fromX ? 1 : -1, x = fromX;
		while (x != toX && Built(g, row, x + dir))
			x += dir;
		return x;
	}

	/// <summary>
	/// A column of the bridge done: its cell in the row one to stand on (a block, or a tile already there that is stood on: a
	/// platform, a slope or half block of a ruined house's bricks, which the grid reads as platforms), the <see cref="ClearRows"/>
	/// over it open, the top one (the jump's spare) left in only where opening it would let lava in.
	/// </summary>
	public static bool Built(ITileGrid g, int row, int x)
	{
		if (!g.Get(x, row).CanStandOn())
			return false;
		for (int y = row - ClearRows; y < row; y++)
			if (InTheWay(g.Get(x, y)) && !(y == row - ClearRows && BlockRules.LetsLavaIn(g, new TilePos(x, y))))
				return false;
		return true;
	}

	/// <summary>A cell over the bridge the body cannot pass: a block, a slope or half block, a platform (mined: a jump would stop on it).</summary>
	private static bool InTheWay(CellKind k) => k.IsSolid() || k.IsShaped() || k.IsPlatform();

	/// <summary>The node standing at the end with the body's front column on it (the body is 2 wide: going west, the end is its left column).</summary>
	private TilePos StandAt(int end) => new((Dir > 0 ? end - 1 : end) - (standBack ? Dir : 0), Row - 1);

	/// <summary>
	/// Placing the next block failed from the end (<see cref="MaxFailuresPerCell"/> tries): the next tries from a column back (the
	/// body's hitbox, the reach), then the skill fails (and the planner does not start the same step again at once). A tile in the
	/// cell that is neither stood on nor solid is mined before any try. Seen in game (measured run M12a, world_wof): "could not place a block at (2156,1042) (3 tries)" by the ruined
	/// tower's roof, the planner starting the same step again 33 times in a few seconds.
	/// </summary>
	private bool standBack;

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		}
		ITileGrid g = ctx.Grid;
		// The action under way done (the cell mined, or the block in): counted, and the next. A mined cell is done once no tile is
		// left in it (a non-solid one, such as a platform, too).
		if (actionCell is TilePos done && (actionPlace ? g.Get(done.X, done.Y).IsSolid()
				: ctx.World != null ? ctx.World.TileTypeAt(done.X, done.Y) < 0 : !g.Get(done.X, done.Y).IsSolid())) {
			if (actionPlace)
				Placed++;
			else
				Mined++;
			actionCell = null;
		}
		if (!g.Get(FromX, Row).CanStandOn())
			return Fail(ctx, $"no ground at the bridge's start ({FromX},{Row})");
		// From where it ended last tick while that column still stands (a scan of up to 1700 columns a tick otherwise).
		int scanFrom = End != FromX && (End - FromX) * Dir > 0 && Built(g, Row, End) ? End : FromX;
		End = BridgeEnd(g, Row, scanFrom, ToX);
		LogProgress(ctx);
		if (End != lastEnd) {
			standBack = false; // on: from the end again
			lastEnd = End;
		}
		if (End == ToX) {
			State = "done";
			ctx.Log.Info($"Hellbridge done: row {Row} from x {FromX} to {ToX}, {Placed} blocks placed and {Mined} cells mined here");
			return SkillStatus.Succeeded;
		}
		TilePos stand = StandAt(End), n = ctx.Node;
		bool onBridge = n.Y == Row - 1 && ctx.Body.Grounded && (Dir > 0 ? n.X >= Math.Min(FromX, End) - 1 && n.X <= stand.X : n.X <= Math.Max(FromX, End) && n.X >= stand.X);
		if (!onBridge || walk != null) {
			// Off it (knocked down, a fight took us away, back from the house): a walk to the end.
			if (walk == null) {
				walk = new WalkToSkill(new GoalStandAt(stand, ctx.Physics)) { Quiet = true };
				walk.Start(ctx);
				State = $"walking to the bridge's end at {stand}";
			}
			SkillStatus ws = walk.Tick(ctx);
			if (ws == SkillStatus.Running)
				return SkillStatus.Running;
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			if (ws == SkillStatus.Failed && ctx.Node != stand)
				return Fail(ctx, $"no way back to the bridge's end at {stand} ({why})");
			return SkillStatus.Running;
		}
		if (n.X != stand.X) {
			// Along the bridge to its end, braking there: from rest each step, never past it.
			ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(stand.X, ctx.Physics), ctx.Physics);
			State = $"walking along it to {stand}";
			return SkillStatus.Running;
		}
		int c = End + Dir;
		// 1. The body's rows over the next column, top down.
		for (int y = Row - ClearRows; y <= Row - 1; y++) {
			var cell = new TilePos(c, y);
			CellKind k = g.Get(c, y);
			if (k.HasLava() && !k.IsSolid()) {
				if (y == Row - ClearRows)
					continue; // the head's spare row: lava there flows down through the body's rows below, waited out there
				if (++lavaTicks > LavaWaitTicks)
					return Fail(ctx, $"lava streams through the bridge's way at {cell} (waited {LavaWaitTicks} ticks)");
				State = $"waiting for the lava at {cell} to drain";
				return SkillStatus.Running;
			}
			if (!InTheWay(k))
				continue;
			if (BlockRules.LetsLavaIn(g, cell)) {
				if (y == Row - ClearRows)
					continue; // the jump's spare row may stay: the body passes under it
				return Fail(ctx, $"the cell {cell} in the bridge's way has lava beside or above it: mining it would flood the bridge");
			}
			return Act(ctx, cell, place: false);
		}
		lavaTicks = 0;
		// 2. The bridge's own cell.
		var deck = new TilePos(c, Row);
		if (!g.Get(c, Row).CanStandOn()) {
			if (g.Get(c, Row).HasLava()) {
				if (++lavaTicks > LavaWaitTicks)
					return Fail(ctx, $"lava in the bridge's cell {deck}: no block goes into lava");
				State = $"waiting for the lava in {deck} to go";
				return SkillStatus.Running;
			}
			// A tile there that is neither stood on nor solid (a pot, a chain, a banner: placing a block over it fails, Player.PlaceThing):
			// mined first.
			if (ctx.World != null && ctx.World.TileTypeAt(c, Row) >= 0)
				return Act(ctx, deck, place: false);
			return Act(ctx, deck, place: true);
		}
		// 3. One tile on.
		ctx.Output.Horizontal = Dir;
		State = "stepping on";
		return SkillStatus.Running;
	}

	/// <summary>Mine or place at the cell: aim, use, and count the action's failures (three on a cell fail the skill).</summary>
	private SkillStatus Act(BotContext ctx, TilePos cell, bool place)
	{
		if (actionCell != cell || actionPlace != place) {
			actionCell = cell;
			actionPlace = place;
			actionTicks = 0;
		}
		if (place) {
			if (!ctx.AllowPlace)
				return Fail(ctx, "building is off");
			if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0) {
				int carried = ctx.Inventory?.CountAny(ctx.Ids.BuildingBlocks) ?? 0;
				if (carried > 0 && ++waitForBlocks <= MaxWaitForBlocks) {
					State = "waiting for blocks on the hotbar";
					return SkillStatus.Running;
				}
				return Fail(ctx, $"out of blocks at x {cell.X} ({Math.Abs(cell.X - FromX)} of {Math.Abs(ToX - FromX)} tiles built)");
			}
			waitForBlocks = 0;
		}
		else if (!ctx.AllowBreak)
			return Fail(ctx, "digging is off");
		if (++actionTicks > ActionTicks) {
			int f = failures.TryGetValue(cell, out int was) ? was + 1 : 1;
			failures[cell] = f;
			actionTicks = 0;
			if (f == 1)
				ctx.Log.Info($"Hellbridge: {(place ? "placing at" : "mining")} {cell} did nothing in {ActionTicks} ticks: the cell reads {ctx.Grid.Get(cell.X, cell.Y)}, "
					+ $"tile {ctx.World?.TileTypeAt(cell.X, cell.Y) ?? -1}, us at {ctx.Node} ({ctx.Body.X:0},{ctx.Body.Y:0} px), {ctx.Blocks?.BlockCount ?? 0} blocks on the hotbar");
			// Escalate rather than repeat: from a column back, then give up (standBack).
			if (place && f == MaxFailuresPerCell && !standBack) {
				standBack = true;
				ctx.Log.Info($"Hellbridge: placing at {cell} failed {f} times from the end; trying from a column back");
			}
			else if (f >= (place ? 2 : 1) * MaxFailuresPerCell)
				return Fail(ctx, $"could not {(place ? "place a block at" : "mine")} {cell} ({f} tries{(place ? ", from the end and a column back" : "")})");
			else
				ctx.Log.Info($"Hellbridge: {(place ? "placing" : "mining")} {cell} did nothing in {ActionTicks} ticks, try {f + 1}");
		}
		ctx.Output.Tool = place ? ToolRequest.Block : ToolRequest.Pickaxe;
		ctx.Output.Aim(cell.X, cell.Y);
		ctx.Output.UseItem = true;
		ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(StandAt(End).X, ctx.Physics), ctx.Physics);
		State = place ? $"placing {cell}" : $"mining {cell}";
		return SkillStatus.Running;
	}

	private void LogProgress(BotContext ctx)
	{
		int pct = (int)(Progress * 100);
		foreach (int p in ProgressPercents)
			if (pct >= p && loggedPercent < p) {
				loggedPercent = p;
				ctx.Log.Info($"Hellbridge: {p} % (x {End} of {FromX}->{ToX})");
			}
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Hellbridge failed: {reason}");
		return SkillStatus.Failed;
	}
}
