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
/// under the plan's middle and that costs least (<see cref="Score"/>: the edits, blocks to place and tiles to mine, dearer
/// in the evil biome, and the walk). Sites with unbreakable or liquid cells, trees, furniture or platforms in the room, or
/// more than <see cref="MaxEdits"/> edits are skipped.
/// </summary>
public static class SiteFinder
{
	public const int SearchRadius = 40;
	public const int MaxEdits = 40;
	public const int GroundScan = 15;

	/// <summary>
	/// Best site within <see cref="Blueprint.SearchRadius"/> of <paramref name="around"/>. <paramref name="avoid"/>:
	/// origins that failed before; <paramref name="keepOut"/>: rectangles (min, max) the plan must not overlap.
	/// </summary>
	public static TilePos? Find(BotContext ctx, Blueprint bp, TilePos around, IReadOnlyCollection<TilePos> avoid = null,
		IReadOnlyList<(TilePos Min, TilePos Max)> keepOut = null)
	{
		TilePos? best = null;
		long bestScore = long.MaxValue;
		Func<int, int?> groundAt = Ground(ctx.Grid, bp, around);
		// An arena's middle within the radius, either side alike: by its left edge, a 96-wide arena could never go left of the
		// house within 80 tiles (seen in game, measured run S12: "over the house" for 92 of 161 sites, none on the left tried).
		for (int x0 = around.X - bp.SearchRadius - Shift(bp); x0 <= around.X + bp.SearchRadius - Shift(bp); x0++) {
			int mid = x0 + bp.Width / 2;
			int? ground = groundAt(mid);
			if (ground == null)
				continue;
			var origin = new TilePos(x0, ground.Value - (bp.Height - 1));
			if (avoid != null && avoid.Contains(origin))
				continue;
			if (keepOut != null && keepOut.Any(r => origin.X <= r.Max.X && origin.X + bp.Width - 1 >= r.Min.X
				&& origin.Y <= r.Max.Y && origin.Y + bp.Height - 1 >= r.Min.Y))
				continue;
			int edits = Edits(ctx, bp, origin);
			if (edits < 0 || edits > bp.MaxEdits)
				continue;
			long score = Score(ctx, bp, origin, edits, around);
			if (score < bestScore) {
				bestScore = score;
				best = origin;
			}
		}
		return best;
	}

	/// <summary>
	/// A site's cost: its build steps (edits and items to place) at <see cref="StepScore"/> each, times
	/// <see cref="EvilStepFactor"/> in the evil biome, plus the columns from <paramref name="around"/> to its middle.
	/// </summary>
	public static long Score(BotContext ctx, Blueprint bp, TilePos origin, int edits, TilePos around)
	{
		long steps = edits + bp.Items.Count;
		if (!bp.IsHouse && InEvilBiome(ctx, bp, origin))
			steps *= EvilStepFactor;
		return steps * StepScore + Math.Abs(origin.X + Shift(bp) - around.X);
	}

	/// <summary>
	/// A build step's cost in columns walked: an edit took about 47 ticks in the arena builds off the evil biome (measured runs
	/// U2, U10 and U20: 949 edits in 45,000 ticks) and a column walked is a 5-tick Walk step, so a step is worth about 10.
	/// </summary>
	public const int StepScore = 10;

	/// <summary>
	/// How much more a build step costs in the evil biome. Seen in game (measured runs U14 and U23, the same site (1896,241) on
	/// the corruption's edge): 186 and 196 edits, 91,800 ticks each and not done ("pausing Build to fight", corrupt thorns,
	/// the walk under the floor), at least 468 ticks an edit against 47 off it. The Eye fight there has the zone's spawns too
	/// (vanilla NPC.SpawnNPC in ZoneCorrupt/ZoneCrimson: spawnRate x0.65, maxSpawns x1.3): eaters of souls with the boss.
	/// </summary>
	public const int EvilStepFactor = 10;

	/// <summary>
	/// Is the arena's middle, where the bot stands to fight, on the evil biome's ground (<see cref="BotContext.InEvilBiome"/>)
	/// or in the game's Corruption zone (<see cref="BotContext.CorruptionAround"/>, its spawns)?
	/// </summary>
	public static bool InEvilBiome(BotContext ctx, Blueprint bp, TilePos origin)
	{
		var mid = new TilePos(origin.X + bp.Width / 2, origin.Y + bp.StandRow);
		return ctx.InEvilBiome(mid) || ctx.CorruptionAround(mid) >= BotContext.CorruptionZoneTiles;
	}

	/// <summary>Arenas are searched by their middle (see <see cref="Find"/>); houses, small, by their left edge as before.</summary>
	private static int Shift(Blueprint bp) => bp.IsHouse ? 0 : bp.Width / 2;

	/// <summary>First standable floor row in the column, scanning down from <see cref="GroundScan"/> above <paramref name="y"/>.</summary>
	private static int? GroundAt(ITileGrid g, int x, int y)
	{
		for (int gy = y - GroundScan; gy <= y + GroundScan; gy++)
			if (g.Get(x, gy).IsBlock() && !g.Get(x, gy - 1).IsBlock())
				return gy;
		return null;
	}

	/// <summary>
	/// The ground row under each column a site's middle may be over. A house, small and by the spawn: within
	/// <see cref="GroundScan"/> rows of <paramref name="around"/>. An arena: the ground's surface followed out from
	/// <paramref name="around"/> column by column, as a player walks it, up a hill to its top and down a slope or a cliff to its
	/// foot, at most as far up or down as the search goes sideways (<see cref="Blueprint.SearchRadius"/>). Seen in game
	/// (measured runs U14 and U23): with the house on a hilltop (roof row 263, floor 268) and the ground round it at rows
	/// 283-285, the rows 248..278 round the roof had "no ground under the middle" for 128 of 161 sites within 80 and 215 of
	/// 321 within 160; the only site left was on the corruption's edge, and the arena was never built.
	/// </summary>
	private static Func<int, int?> Ground(ITileGrid g, Blueprint bp, TilePos around)
	{
		if (bp.IsHouse)
			return x => GroundAt(g, x, around.Y);
		int reach = bp.SearchRadius;
		var rows = new Dictionary<int, int?>();
		int? here = Follow(g, around.X, around.Y, around.Y, reach);
		rows[around.X] = here;
		foreach (int dir in new[] { -1, 1 }) {
			int from = here ?? around.Y;
			for (int x = around.X + dir; Math.Abs(x - around.X) <= reach; x += dir) {
				int? gy = Follow(g, x, from, around.Y, reach);
				rows[x] = gy;
				if (gy is int r)
					from = r;
			}
		}
		return x => rows.TryGetValue(x, out int? r) ? r : null;
	}

	/// <summary>
	/// The ground's surface in column <paramref name="x"/> next to a column whose ground is at row <paramref name="from"/>: up
	/// through the solid to its top, or down through the open to the first block; null past <paramref name="reach"/> rows from
	/// <paramref name="anchorY"/>.
	/// </summary>
	private static int? Follow(ITileGrid g, int x, int from, int anchorY, int reach)
	{
		int gy = from;
		if (g.Get(x, gy).IsBlock()) {
			while (g.Get(x, gy - 1).IsBlock())
				if (anchorY - --gy > reach)
					return null;
		}
		else {
			while (!g.Get(x, gy).IsBlock())
				if (++gy - anchorY > reach)
					return null;
		}
		return gy;
	}

	/// <summary>
	/// Why no site was found, in one line for the log: for each origin tried, the first reason it was skipped, counted, with
	/// the cell that ruled out the most sites for that reason ("a tree in the floor 29 (14 at (1986,283))"), and the fewest
	/// edits of any site that could be built. Seen in game (measured run U9): "a tree in the floor 29, unbreakable tile 2
	/// under 18 18" said what but not where: it took a parse of the saved world to find the trees and work benches.
	/// </summary>
	public static string Explain(BotContext ctx, Blueprint bp, TilePos around, IReadOnlyList<(TilePos Min, TilePos Max)> keepOut = null,
		IReadOnlyCollection<TilePos> avoid = null)
	{
		var why = new Dictionary<string, int>();
		var where = new Dictionary<string, Dictionary<TilePos, int>>();
		void Count(string r, TilePos? at = null)
		{
			why[r] = why.GetValueOrDefault(r) + 1;
			if (at is TilePos c) {
				if (!where.TryGetValue(r, out var cells))
					where[r] = cells = new Dictionary<TilePos, int>();
				cells[c] = cells.GetValueOrDefault(c) + 1;
			}
		}
		int fewest = int.MaxValue;
		TilePos fewestAt = default;
		Func<int, int?> groundAt = Ground(ctx.Grid, bp, around);
		for (int x0 = around.X - bp.SearchRadius - Shift(bp); x0 <= around.X + bp.SearchRadius - Shift(bp); x0++) {
			int? ground = groundAt(x0 + bp.Width / 2);
			if (ground == null) {
				Count("no ground under the middle");
				continue;
			}
			var origin = new TilePos(x0, ground.Value - (bp.Height - 1));
			if (avoid != null && avoid.Contains(origin)) {
				Count("given up before");
				continue;
			}
			if (keepOut != null && keepOut.Any(r => origin.X <= r.Max.X && origin.X + bp.Width - 1 >= r.Min.X
				&& origin.Y <= r.Max.Y && origin.Y + bp.Height - 1 >= r.Min.Y)) {
				Count("over the house");
				continue;
			}
			int edits = Edits(ctx, bp, origin, out string reason, out TilePos? cell);
			if (edits < 0) {
				Count(reason, cell);
				continue;
			}
			Count(edits > bp.MaxEdits ? "too many edits" : !bp.IsHouse && InEvilBiome(ctx, bp, origin) ? "fine, in the evil biome" : "fine");
			if (edits < fewest) {
				fewest = edits;
				fewestAt = origin;
			}
		}
		string Most(string r) => where.TryGetValue(r, out var cells) && cells.MaxBy(kv => kv.Value) is var (c, n) ? $" ({n} at {c})" : "";
		return string.Join(", ", why.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}{Most(kv.Key)}"))
			+ (fewest < int.MaxValue ? $"; fewest edits {fewest} at {fewestAt}" : "");
	}

	/// <summary>Number of edits to build here, or -1 if it cannot be built here.</summary>
	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin) => Edits(ctx, bp, origin, out _);

	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin, out string reason) => Edits(ctx, bp, origin, out reason, out _);

	/// <summary>As <see cref="Edits(BotContext, Blueprint, TilePos)"/>, with why a site cannot be built and the cell that rules it out.</summary>
	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin, out string reason, out TilePos? cell)
	{
		reason = null;
		cell = null;
		ITileGrid g = ctx.Grid;
		int edits = 0;
		for (int y = 0; y < bp.Height; y++) {
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				if (c == PlanCell.None)
					continue;
				int wx = origin.X + x, wy = origin.Y + y;
				CellKind k = g.Get(wx, wy);
				cell = new TilePos(wx, wy);
				if (k.IsLiquid()) {
					reason = "liquid";
					return -1;
				}
				if (ctx.World.IsTreeTrunk(wx, wy)) {
					if (!bp.ChopTrees) {
						reason = c == PlanCell.Block ? "a tree in the floor" : "a tree in the room";
						return -1;
					}
					// Felled with the axe; in the floor row (a tree standing lower, its trunk up through the floor) the floor block
					// then goes in where it stood. Seen in game (measured run U9): trees at (1986,283), (2124,284) and (2166,285),
					// in dips beside the house, ruled out 29 of the 47 arena sites clear of the house, and auto mode stopped.
					edits += c == PlanCell.Block ? 2 : 1;
					continue;
				}
				if (c == PlanCell.Block) {
					if (k.IsPlatform() && !k.IsShaped()) {
						reason = "a platform in the floor";
						return -1;
					}
					if (!k.IsBlock())
						edits++;
				}
				else if (k.IsBlock()) {
					// A slope or half block of the ground is a block to mine, like any other: seen in game (measured run S10),
					// read as platforms they ruled out 122 of 161 arena sites round the house, and the boss plan stopped.
					// A block under a tree cannot be mined while the tree stands; once it is felled, it can.
					bool underTree = bp.ChopTrees && ctx.World.IsTreeTrunk(wx, wy - 1);
					if (!underTree && float.IsInfinity(ctx.Blocks?.BreakTicks(wx, wy) ?? float.PositiveInfinity)) {
						reason = $"unbreakable tile {ctx.World.TileTypeAt(wx, wy)} under {ctx.World.TileTypeAt(wx, wy - 1)}";
						return -1;
					}
					edits += 2;
				}
				// Furniture in an arena's open air (a work bench, a table: solid-topped, read as platforms) stays and does no harm:
				// seen in game (a run from U7's arena checkpoint), the bot's own benches and tables ruled out 94 of 161 sites, and
				// the one left was then over the house. A house's room must be clear.
				else if (bp.IsHouse && (k.IsPlatform() || ctx.World.TileTypeAt(wx, wy) is int t && t >= 0 && ctx.Seen.Stations.ContainsKey(t))) {
					reason = k.IsPlatform() ? $"a platform (tile {ctx.World.TileTypeAt(wx, wy)}) in the room" : "a station in the room";
					return -1; // furniture or a station in the way
				}
			}
		}
		cell = null;
		return edits;
	}
}

/// <summary>
/// Builds a <see cref="Blueprint"/> at a site, the way a player would, one swing at a time:
/// <list type="number">
/// <item>fell trees in the plan's cells, the floor row's too (arenas);</item>
/// <item>mine everything solid where the room, doorway and furniture go (top-down);</item>
/// <item>place blocks for the frame (bottom-up, each next to an existing tile);</item>
/// <item>put walls behind the room;</item>
/// <item>place the furniture (table, chair, torch), never standing where it goes;</item>
/// <item>step outside and place the door;</item>
/// <item>run the game's housing check on the room (houses).</item>
/// </list>
/// While it runs, every cell of the plan is reserved (<see cref="BotContext.Reserved"/>), so walking there never
/// digs through the frame or pillars up inside the room; an arena's floor blocks are rebuilt instead
/// (<see cref="Blueprint.RebuildBlocks"/>: a walk may take one out at the cost of putting it back). An action that has no effect after
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

	private enum Kind { Chop, Mine, Block, Wall, Item, Wait }

	private readonly record struct Action(Kind Kind, TilePos Cell, TilePos Aim, int Item, (TilePos Min, TilePos Max)? Keep, bool Outside);

	private readonly Blueprint bp;
	private readonly Dictionary<TilePos, int> failures = new();
	private HashSet<TilePos> reserved;
	private HashSet<TilePos> rebuilt;
	private WalkToSkill walk;
	private ChopTreeSkill chop;
	private Action? current;
	private int actionTicks;

	public TilePos Origin { get; }
	public Blueprint Blueprint => bp;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Build";
	public string Key => $"Build {bp.GetType().Name} {Origin} {bp.Width}x{bp.Height}";

	public BuildSkill(Blueprint blueprint, TilePos origin)
	{
		bp = blueprint;
		Origin = origin;
	}

	public string Describe() => $"Build {bp.Name} at {Origin} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		reserved = new HashSet<TilePos>();
		rebuilt = bp.RebuildBlocks ? new HashSet<TilePos>() : null;
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				bool itemCell = bp.Items.Any(it => it.Covers(x, y));
				if (rebuilt != null && c == PlanCell.Block && !itemCell)
					rebuilt.Add(new TilePos(Origin.X + x, Origin.Y + y));
				else if (c != PlanCell.None && (!bp.ReserveBlocksOnly || c == PlanCell.Block || itemCell))
					reserved.Add(new TilePos(Origin.X + x, Origin.Y + y));
			}
		ctx.Reserved = reserved;
		ctx.Rebuilt = rebuilt;
		ctx.Log.Info($"Build start: {bp.Name} at {Origin} ({bp.Width}x{bp.Height})");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		chop?.Stop(ctx);
		chop = null;
		if (ReferenceEquals(ctx.Reserved, reserved))
			ctx.Reserved = null;
		if (rebuilt != null && ReferenceEquals(ctx.Rebuilt, rebuilt))
			ctx.Rebuilt = null;
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
		chop?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		walk?.Resume(ctx);
		chop?.Resume(ctx);
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
			if (bp.IsHouse) {
				string why = ctx.World.CheckHousing(Origin.X + bp.Inside.X, Origin.Y + bp.Inside.Y);
				if (why != null)
					return Fail(ctx, "built, but the housing check says: " + why);
			}
			ctx.Log.Info($"Build done: {bp.Name} at {Origin}{(bp.IsHouse ? " is valid housing" : "")}");
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

		if (a.Kind == Kind.Chop) {
			// Trees take several swings (and their own reach rules): the tree-felling skill does it.
			if (chop == null || chop.Base != a.Cell) {
				chop?.Stop(ctx);
				chop = new ChopTreeSkill(a.Cell);
				chop.Start(ctx);
			}
			State = $"felling the tree at {a.Cell}";
			SkillStatus cs = chop.Tick(ctx);
			if (cs == SkillStatus.Running)
				return SkillStatus.Running;
			string why = chop.FailureReason;
			chop.Stop(ctx);
			chop = null;
			return cs == SkillStatus.Failed ? Failed(ctx, a.Cell, why) : SkillStatus.Running;
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

	/// <summary>The cell whose action failed <see cref="MaxFailuresPerCell"/> times and failed the build, if that is why.</summary>
	public TilePos? FailedCell { get; private set; }

	private SkillStatus Failed(BotContext ctx, TilePos cell, string why)
	{
		int n = failures.GetValueOrDefault(cell) + 1;
		failures[cell] = n;
		ctx.Log.Info($"Build: {current?.Kind} {cell} failed ({why}), attempt {n}/{MaxFailuresPerCell}");
		current = null;
		walk?.Stop(ctx);
		walk = null;
		if (n < MaxFailuresPerCell)
			return SkillStatus.Running;
		FailedCell = cell;
		return Fail(ctx, $"{why} at {cell}");
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

		// 0. Fell trees standing in the plan's cells (at the trunk's lowest tile; the whole tree comes down): the open air, and
		// the floor row too, where a tree standing lower has its trunk (SiteFinder counts it as felled and filled in).
		if (bp.ChopTrees) {
			TilePos? tree = null;
			int treeDist = int.MaxValue;
			for (int x = 0; x < bp.Width; x++)
				for (int y = 0; y < bp.Height; y++) {
					int wx = Origin.X + x, wy = Origin.Y + y;
					if (bp.At(x, y) != PlanCell.None && w.IsTreeTrunk(wx, wy) && Math.Abs(wx - node.X) < treeDist) {
						treeDist = Math.Abs(wx - node.X);
						tree = TreeBase(w, wx, wy);
						break;
					}
				}
			if (tree is TilePos t) {
				if (ctx.AxePower <= 0) {
					problem = $"a tree at {t} is in the way and there is no axe";
					return null;
				}
				return new Action(Kind.Chop, t, t, 0, null, false);
			}
		}

		// 1. Mine the room, doorway and furniture cells clear (top-down, nearest first).
		Action? best = null;
		for (int y = 0; y < bp.Height && best == null; y++) {
			int bestDist = int.MaxValue;
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				int wx = Origin.X + x, wy = Origin.Y + y;
				if (c is PlanCell.Wall or PlanCell.Clear && g.Get(wx, wy).IsBlock() && !CoveredByPlaced(wx, wy)) {
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
		List<TilePos> missingBlocks = Cells(PlanCell.Block).Where(c => !g.Get(c.X, c.Y).IsBlock()).ToList();
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
		Kind.Chop => ctx.World.IsTreeTrunk(a.Cell.X, a.Cell.Y),
		Kind.Mine => ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsBlock() && !coveredByPlaced(a.Cell.X, a.Cell.Y),
		Kind.Block => !ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsBlock() && ctx.Blocks != null && ctx.Blocks.BlockCount > 0,
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

	/// <summary>
	/// The base of the tree a tree tile belongs to: the trunk tile with trunk above and none below (what the game
	/// adapter's tree scan calls a tree). Branches and roots are tree tiles beside the trunk, so the neighbouring
	/// columns are searched too; seen in game, axing a branch had no effect.
	/// </summary>
	private static TilePos TreeBase(IWorldInfo w, int x, int y)
	{
		foreach (int dx in new[] { 0, -1, 1, -2, 2 })
			for (int yy = y - 1; yy <= y + TreeBaseScan; yy++)
				if (w.IsTreeTrunk(x + dx, yy) && w.IsTreeTrunk(x + dx, yy - 1) && !w.IsTreeTrunk(x + dx, yy + 1))
					return new TilePos(x + dx, yy);
		int by = y;
		while (w.IsTreeTrunk(x, by + 1))
			by++;
		return new TilePos(x, by);
	}

	private const int TreeBaseScan = 30;

	private static bool HasSolidNeighbour(ITileGrid g, TilePos c) =>
		g.Get(c.X - 1, c.Y).IsBlock() || g.Get(c.X + 1, c.Y).IsBlock() || g.Get(c.X, c.Y - 1).IsBlock() || g.Get(c.X, c.Y + 1).IsBlock();

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Build {bp.Name} at {Origin} failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
