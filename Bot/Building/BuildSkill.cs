using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Building;

/// <summary>
/// Picks where a blueprint goes: the spot within <see cref="SearchRadius"/> columns whose floor row is the ground
/// under the plan's middle and that needs the fewest edits (blocks to place, tiles to mine). Sites with
/// unbreakable or liquid cells, trees, furniture or platforms in the room, or more than <see cref="MaxEdits"/>
/// edits are skipped.
/// </summary>
public static class SiteFinder
{
	public const int SearchRadius = 40;
	public const int MaxEdits = 40;
	public const int GroundScan = 15;

	public static TilePos? Find(BotContext ctx, Blueprint bp, TilePos around, IReadOnlyCollection<TilePos> avoid = null)
	{
		TilePos? best = null;
		int bestScore = int.MaxValue;
		for (int x0 = around.X - SearchRadius; x0 <= around.X + SearchRadius; x0++) {
			int mid = x0 + bp.Width / 2;
			int? ground = GroundAt(ctx.Grid, mid, around.Y);
			if (ground == null)
				continue;
			var origin = new TilePos(x0, ground.Value - (bp.Height - 1));
			if (avoid != null && avoid.Contains(origin))
				continue;
			int edits = Edits(ctx, bp, origin);
			if (edits < 0 || edits > MaxEdits)
				continue;
			int score = edits * 10 + Math.Abs(x0 - around.X);
			if (score < bestScore) {
				bestScore = score;
				best = origin;
			}
		}
		return best;
	}

	/// <summary>First standable floor row in the column, scanning down from <see cref="GroundScan"/> above <paramref name="y"/>.</summary>
	private static int? GroundAt(ITileGrid g, int x, int y)
	{
		for (int gy = y - GroundScan; gy <= y + GroundScan; gy++)
			if (g.Get(x, gy).IsSolid() && !g.Get(x, gy - 1).IsSolid())
				return gy;
		return null;
	}

	/// <summary>Number of edits to build here, or -1 if it cannot be built here.</summary>
	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin)
	{
		ITileGrid g = ctx.Grid;
		int edits = 0;
		for (int y = 0; y < bp.Height; y++) {
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				if (c == PlanCell.None)
					continue;
				int wx = origin.X + x, wy = origin.Y + y;
				CellKind k = g.Get(wx, wy);
				if (k.IsLiquid() || ctx.World.IsTreeTrunk(wx, wy))
					return -1;
				if (c == PlanCell.Block) {
					if (k.IsPlatform())
						return -1;
					if (!k.IsSolid())
						edits++;
				}
				else if (k.IsSolid()) {
					if (float.IsInfinity(ctx.Blocks?.BreakTicks(wx, wy) ?? float.PositiveInfinity))
						return -1;
					edits += 2;
				}
				else if (k.IsPlatform() || ctx.World.TileTypeAt(wx, wy) is int t && t >= 0 && ctx.Seen.Stations.ContainsKey(t)) {
					return -1; // furniture or a station in the way
				}
			}
		}
		return edits;
	}
}

/// <summary>
/// Builds a <see cref="Blueprint"/> at a site, the way a player would, one swing at a time:
/// <list type="number">
/// <item>mine everything solid where the room, doorway and furniture go (top-down);</item>
/// <item>place blocks for the frame (bottom-up, each next to an existing tile);</item>
/// <item>put walls behind the room;</item>
/// <item>place the furniture (table, chair, torch), never standing where it goes;</item>
/// <item>step outside and place the door;</item>
/// <item>run the game's housing check on the room.</item>
/// </list>
/// While it runs, every cell of the plan is reserved (<see cref="BotContext.Reserved"/>), so walking there never
/// digs through the frame or pillars up inside the room. An action that has no effect after
/// <see cref="MaxTicksPerAction"/> ticks in range, or whose spot cannot be reached, counts as a failure; three
/// on the same cell fail the skill.
/// </summary>
public sealed class BuildSkill : ISkill
{
	public const int MaxTicksPerAction = 90;
	public const int MaxFailuresPerCell = 3;
	/// <summary>Ticks to wait for hotbar stocking when the hotbar block stack ran out but the backpack has more.</summary>
	public const int MaxWaitForBlocks = 90;
	private int waitForBlocks;

	private enum Kind { Mine, Block, Wall, Item, Wait }

	private readonly record struct Action(Kind Kind, TilePos Cell, TilePos Aim, int Item, (TilePos Min, TilePos Max)? Keep, bool Outside);

	private readonly Blueprint bp;
	private readonly Dictionary<TilePos, int> failures = new();
	private HashSet<TilePos> reserved;
	private WalkToSkill walk;
	private Action? current;
	private int actionTicks;

	public TilePos Origin { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Build";

	public BuildSkill(Blueprint blueprint, TilePos origin)
	{
		bp = blueprint;
		Origin = origin;
	}

	public string Describe() => $"Build {bp.Name} at {Origin} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		reserved = new HashSet<TilePos>();
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++)
				if (bp.At(x, y) != PlanCell.None)
					reserved.Add(new TilePos(Origin.X + x, Origin.Y + y));
		ctx.Reserved = reserved;
		ctx.Log.Info($"Build start: {bp.Name} at {Origin} ({bp.Width}x{bp.Height})");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		if (ReferenceEquals(ctx.Reserved, reserved))
			ctx.Reserved = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		Action? next = NextAction(ctx, out string problem);
		if (problem != null)
			return Fail(ctx, problem);
		if (next == null) {
			string why = ctx.World.CheckHousing(Origin.X + bp.Inside.X, Origin.Y + bp.Inside.Y);
			if (why != null)
				return Fail(ctx, "built, but the housing check says: " + why);
			ctx.Log.Info($"Build done: {bp.Name} at {Origin} is valid housing");
			State = "done";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		Action a = next.Value;
		if (a.Kind == Kind.Wait) {
			State = "waiting for blocks on the hotbar";
			return SkillStatus.Running;
		}
		if (current != a) {
			current = a;
			actionTicks = 0;
			walk?.Stop(ctx);
			walk = null;
		}

		var reach = new GoalReachTile(a.Aim, ctx.Physics) { Exclude = n => Excluded(a, n) };
		TilePos node = ctx.Node;
		if (walk == null && ctx.Body.Grounded && reach.IsGoal(node)) {
			if (++actionTicks > MaxTicksPerAction)
				return Failed(ctx, a.Cell, $"no effect after {MaxTicksPerAction} ticks");
			State = $"{a.Kind} {a.Cell}";
			ctx.Output.UseItem = true;
			ctx.Output.Aim(a.Aim.X, a.Aim.Y);
			switch (a.Kind) {
				case Kind.Mine: ctx.Output.Tool = ToolRequest.Pickaxe; break;
				case Kind.Block: ctx.Output.Tool = ToolRequest.Block; break;
				default:
					ctx.Output.Tool = ToolRequest.Item;
					ctx.Output.ToolItem = a.Item;
					break;
			}
			return SkillStatus.Running;
		}
		if (walk == null) {
			walk = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 2 };
			walk.Start(ctx);
		}
		State = $"walking to {a.Kind} {a.Cell}";
		SkillStatus w = walk.Tick(ctx);
		if (w == SkillStatus.Failed) {
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			return Failed(ctx, a.Cell, "cannot get there: " + why);
		}
		if (w == SkillStatus.Succeeded) {
			walk.Stop(ctx);
			walk = null;
		}
		return SkillStatus.Running;
	}

	/// <summary>Standing spots that would block the action: inside the footprint, or inside the house for the door.</summary>
	private bool Excluded(Action a, TilePos n)
	{
		int bx0 = n.X, bx1 = n.X + Body.WidthTiles - 1, by0 = n.Y - Body.HeightTiles + 1, by1 = n.Y;
		if (a.Keep is var (min, max) && bx1 >= min.X && bx0 <= max.X && by1 >= min.Y && by0 <= max.Y)
			return true;
		return a.Outside && bx1 >= Origin.X && bx0 < Origin.X + bp.Width && by0 < Origin.Y + bp.Height && by1 >= Origin.Y;
	}

	private SkillStatus Failed(BotContext ctx, TilePos cell, string why)
	{
		int n = failures.GetValueOrDefault(cell) + 1;
		failures[cell] = n;
		ctx.Log.Info($"Build: {current?.Kind} {cell} failed ({why}), attempt {n}/{MaxFailuresPerCell}");
		current = null;
		walk?.Stop(ctx);
		walk = null;
		return n >= MaxFailuresPerCell ? Fail(ctx, $"{why} at {cell}") : SkillStatus.Running;
	}

	/// <summary>The next thing to do, in phase order; null when the plan is complete. Sets <paramref name="problem"/> if stuck.</summary>
	private Action? NextAction(BotContext ctx, out string problem)
	{
		problem = null;
		ITileGrid g = ctx.Grid;
		IWorldInfo w = ctx.World;
		TilePos node = ctx.Node;
		var placedItems = bp.Items.Where(it => IsPlaced(ctx, it)).ToList();
		bool CoveredByPlaced(int x, int y) => placedItems.Any(it => it.Covers(x - Origin.X, y - Origin.Y));

		// Stick with the current target while it still needs doing: re-picking "the nearest" every tick makes the bot
		// step off a cell it stands on, which makes the next cell the nearest one, and so on (oscillation).
		if (current is Action cur && StillNeeded(ctx, cur, CoveredByPlaced))
			return cur;

		// 1. Mine the room, doorway and furniture cells clear (top-down, nearest first).
		Action? best = null;
		for (int y = 0; y < bp.Height && best == null; y++) {
			int bestDist = int.MaxValue;
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				int wx = Origin.X + x, wy = Origin.Y + y;
				if (c is PlanCell.Wall or PlanCell.Clear && g.Get(wx, wy).IsSolid() && !CoveredByPlaced(wx, wy)) {
					int d = Math.Abs(wx - node.X);
					if (d < bestDist) {
						bestDist = d;
						var cell = new TilePos(wx, wy);
						best = new Action(Kind.Mine, cell, cell, 0, null, false);
					}
				}
			}
		}
		if (best != null)
			return best;

		// 2. Frame blocks, bottom-up, each one next to something already there.
		List<TilePos> missingBlocks = Cells(PlanCell.Block).Where(c => !g.Get(c.X, c.Y).IsSolid()).ToList();
		if (missingBlocks.Count > 0) {
			if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0) {
				// The hotbar stack ran out; if more blocks are in the backpack, hotbar stocking brings them up shortly.
				if (ctx.Inventory.CountAny(ctx.Ids.BuildingBlocks) > 0 && ++waitForBlocks <= MaxWaitForBlocks)
					return new Action(Kind.Wait, node, node, 0, null, false);
				problem = $"out of blocks ({missingBlocks.Count} frame cells left)";
				return null;
			}
			waitForBlocks = 0;
			TilePos? b = missingBlocks.Where(c => HasSolidNeighbour(g, c)).OrderByDescending(c => c.Y).ThenBy(c => Math.Abs(c.X - node.X)).Cast<TilePos?>().FirstOrDefault();
			if (b == null) {
				problem = "frame cells with nothing to attach to";
				return null;
			}
			return new Action(Kind.Block, b.Value, b.Value, 0, (b.Value, b.Value), false);
		}

		// 3. Walls behind the room.
		List<TilePos> missingWalls = Cells(PlanCell.Wall).Where(c => !w.HasWall(c.X, c.Y)).ToList();
		if (missingWalls.Count > 0) {
			if (ctx.Inventory.Count(bp.WallItem) <= 0) {
				problem = $"out of walls ({missingWalls.Count} left)";
				return null;
			}
			TilePos? c = missingWalls.Where(m => w.CanPlaceWall(bp.WallItem, m.X, m.Y)).OrderBy(m => Math.Abs(m.X - node.X) + Math.Abs(m.Y - node.Y)).Cast<TilePos?>().FirstOrDefault();
			if (c == null) {
				problem = "walls with nothing to attach to";
				return null;
			}
			return new Action(Kind.Wall, c.Value, c.Value, bp.WallItem, null, false);
		}

		// 4./5. Furniture, then the door from outside.
		foreach (PlanItem it in bp.Items.OrderBy(i => i.PlaceLast)) {
			if (placedItems.Contains(it))
				continue;
			if (ctx.Inventory.Count(it.ItemType) <= 0) {
				problem = $"missing item {it.ItemType}";
				return null;
			}
			var min = new TilePos(Origin.X + it.X, Origin.Y + it.Y);
			var max = new TilePos(min.X + it.Width - 1, min.Y + it.Height - 1);
			return new Action(Kind.Item, min, AimFor(ctx, it), it.ItemType, (min, max), it.PlaceLast);
		}
		return null;
	}

	private bool StillNeeded(BotContext ctx, Action a, Func<int, int, bool> coveredByPlaced) => a.Kind switch {
		Kind.Mine => ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsSolid() && !coveredByPlaced(a.Cell.X, a.Cell.Y),
		Kind.Block => !ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsSolid() && ctx.Blocks != null && ctx.Blocks.BlockCount > 0,
		Kind.Wall => !ctx.World.HasWall(a.Cell.X, a.Cell.Y) && ctx.Inventory.Count(a.Item) > 0,
		Kind.Wait => false, // re-check every tick
		_ => ctx.World.TileTypeAt(a.Aim.X, a.Aim.Y) != ctx.Knowledge.TilePlacedBy(a.Item) && ctx.Inventory.Count(a.Item) > 0,
	};

	private TilePos AimFor(BotContext ctx, PlanItem it)
	{
		var (_, _, ox, oy) = ctx.World.Footprint(it.ItemType);
		return new TilePos(Origin.X + it.X + ox, Origin.Y + it.Y + oy);
	}

	private bool IsPlaced(BotContext ctx, PlanItem it) => IsPlaced(ctx, bp, Origin, it);

	/// <summary>Is the item already standing where the plan puts it (its tile at the aim cell)?</summary>
	public static bool IsPlaced(BotContext ctx, Blueprint bp, TilePos origin, PlanItem it)
	{
		int tile = ctx.Knowledge.TilePlacedBy(it.ItemType);
		var (_, _, ox, oy) = ctx.World.Footprint(it.ItemType);
		return tile >= 0 && ctx.World.TileTypeAt(origin.X + it.X + ox, origin.Y + it.Y + oy) == tile;
	}

	private IEnumerable<TilePos> Cells(PlanCell kind)
	{
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++)
				if (bp.At(x, y) == kind)
					yield return new TilePos(Origin.X + x, Origin.Y + y);
	}

	private static bool HasSolidNeighbour(ITileGrid g, TilePos c) =>
		g.Get(c.X - 1, c.Y).IsSolid() || g.Get(c.X + 1, c.Y).IsSolid() || g.Get(c.X, c.Y - 1).IsSolid() || g.Get(c.X, c.Y + 1).IsSolid();

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Build {bp.Name} at {Origin} failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
