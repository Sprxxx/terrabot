using System;
using System.IO;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Replays regions the game wrote when a big search found no path (WalkToSkill.DumpRegion, tModLoader-Logs/TerraBot):
/// the map (F start, T goal anchor, X unbreakable, U unsafe; then "bad" steps and a "walls" map, 'w' a wall), copied into Dumps/.
/// </summary>
public class DumpReplayTests
{
	private readonly ITestOutputHelper output;
	public DumpReplayTests(ITestOutputHelper o) => output = o;

	/// <summary>Grid (world offset removed), start and goal anchor in grid coordinates.</summary>
	public static (ArrayTileGrid grid, TestBlockActions blocks, TilePos start, TilePos goal) Load(string file)
	{
		string[] lines = File.ReadAllLines(file);
		int wallsAt = Array.FindIndex(lines, l => l.Trim() == "walls");
		string[] mapLines = wallsAt < 0 ? lines : lines.Take(wallsAt).ToArray();
		string[] rows = mapLines.Skip(1).Where(l => l.Length > 0 && !l.StartsWith("bad ")).Select(l => l.Trim()).ToArray();
		TilePos start = default, goal = default;
		for (int y = 0; y < rows.Length; y++)
			for (int x = 0; x < rows[y].Length; x++) {
				if (rows[y][x] == 'F') start = new TilePos(x, y);
				if (rows[y][x] == 'T') goal = new TilePos(x, y);
			}
		// F and T stand on cells of unknown kind: F is the body's feet (open), T a target tile (solid ore).
		string[] clean = rows.Select((r, y) => new string(r.Select((c, x) => c switch {
			'F' => 'S', 'T' => '#', 'X' => '@', 'U' => '#', ',' => '.', _ => c }).ToArray())).ToArray();
		var (g, b) = TestBlockActions.Parse(clean);
		for (int y = 0; y < rows.Length; y++)
			for (int x = 0; x < rows[y].Length; x++)
				if (rows[y][x] == ',')
					b.NoPlace.Add(new TilePos(x, y));
		// The walls behind the cells (dumps since the arrested drop: 'w' a wall).
		if (wallsAt >= 0) {
			string[] walls = lines.Skip(wallsAt + 1).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToArray();
			for (int y = 0; y < walls.Length; y++)
				for (int x = 0; x < walls[y].Length; x++)
					if (walls[y][x] == 'w')
						b.Walls.Add(new TilePos(x, y));
		}
		return (g, b, start, goal);
	}

	/// <summary>The never-plan-again steps a dump lists ("bad fx fy tx ty", region coordinates).</summary>
	public static System.Collections.Generic.HashSet<(TilePos From, TilePos To)> BadEdgesOf(string file)
	{
		var set = new System.Collections.Generic.HashSet<(TilePos, TilePos)>();
		foreach (string l in File.ReadLines(file).Where(l => l.StartsWith("bad "))) {
			int[] v = l.Split(' ').Skip(1).Select(int.Parse).ToArray();
			set.Add((new TilePos(v[0], v[1]), new TilePos(v[2], v[3])));
		}
		return set;
	}

	[Fact]
	public void BigCave170RowsDown_HasAWayBackToTheSpawn()
	{
		// Measured run J2: 31 searches of 40,000 nodes (5 s each, the bot standing still) from this cave to the spawn ended
		// "no path" with no node better than the start.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2053_451_to_2095_282.txt");
		var (g, b, start, _) = Load(file);
		var goalAt = new TilePos(2095 - 2041, 282 - 270); // header: goal Near(2095,282)±4, origin (2041,270)
		b.BlockCount = 860;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true, BadEdges = BadEdgesOf(file) };
		var goal = new GoalNearTile(goalAt, 4, PhysicsParams.Default);
		var search = new PathSearch(ctx, start, goal, new SearchLimits { SegmentAfter = 0 });
		search.Step(1e9);
		output.WriteLine($"with the dump's bans: {search.Result}, best {search.Result.Best} (start {start}, goal {goalAt})");
		var clean = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var free = new PathSearch(clean, start, goal, new SearchLimits { SegmentAfter = 0 });
		free.Step(1e9);
		output.WriteLine($"without bans: {free.Result}, best {free.Result.Best}");
		var relaxed = new PathSearch(ctx, start, goal, new SearchLimits { SegmentAfter = 0, CheckBrokenSupport = false });
		relaxed.Step(1e9);
		output.WriteLine($"relaxed: {relaxed.Result}, best {relaxed.Result.Best}");
		// As the game had it: 'U' (unsafe to break) cells unbreakable.
		string[] rows = File.ReadAllLines(file).Skip(1).Where(l => l.Length > 0 && !l.StartsWith("bad ")).Select(l => l.Trim().Replace('U', '@')).ToArray();
		string tmp = Path.Combine(Path.GetTempPath(), "cave_u.txt");
		File.WriteAllLines(tmp, new[] { File.ReadLines(file).First() }.Concat(rows));
		var (gu, bu, su, _) = Load(tmp);
		bu.BlockCount = 860;
		var uctx = new MoveContext(gu, PhysicsParams.Default) { Blocks = bu, AllowBreak = true, AllowPlace = true, BadEdges = BadEdgesOf(file) };
		var us = new PathSearch(uctx, su, goal, new SearchLimits { SegmentAfter = 0 });
		us.Step(1e9);
		output.WriteLine($"U unbreakable: {us.Result}, best {us.Result.Best}");
		int uCount = rows.Sum(r => r.Count(c => c == '@'));
		output.WriteLine($"unbreakable cells in the region: {uCount}");
		Assert.True(search.Result.Status is PathStatus.Found or PathStatus.Partial, search.Result.ToString());
	}

	[Fact]
	public void OreFourTilesUp_FromACaveFloor_IsReachable()
	{
		// world_eye run 26: a 1000+ node search found no way to mine ore 4 tiles above the bot.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2077_361_to_2075_357.txt");
		var (g, b, start, ore) = Load(file);
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var goal = new GoalReachTile(ore, PhysicsParams.Default);
		output.WriteLine($"start {start} standable {Body.IsStandable(g, start)} startable {Body.IsStartable(g, start)} goal-at-start {goal.IsGoal(start)}");
		var search = new PathSearch(ctx, start, goal, new SearchLimits { MaxExpansions = 12_000 });
		search.Step(1e9);
		output.WriteLine(search.Result.ToString());
		Assert.NotEqual(PathStatus.NoPath, search.Result.Status);
	}

	/// <summary>
	/// Measured run U10 (t99000-136800): from (2035,353) and (2032,348), with a copper pickaxe (tileBoost -1: the log's
	/// "start h 15" for Reach(2030,359) is 3 columns of 5.2 ticks), every walk to iron or silver ore 4-8 tiles away under
	/// a pool ended "no path ... expanded 4001", "start h 10, lowest h 10": three stretches of 1.5 game hours each. Both
	/// dumps replay to a path at once, because the dump drew the cells that mattered as plain rock: at 22:44:51 the bot had
	/// placed blocks into the flooded row (2036..2040,354) to walk over it ("Walk (2037,353)->(2036,353) 54.2t
	/// {place(2036,354)}", 3x for the water), and a block keeps the water of its cell (tModLoader's Liquid.Update leaves a
	/// solid tile's liquid alone; PlaceTile only reads it). The first "no path" came 0.08 s later. The way down from
	/// (2035,353) mines (2036,354) and lands with it in the head row; its water counted as the head under, 200x: 19,000
	/// ticks for a 95-tick step. Dumps now draw such a block '&amp;'. The dump holds about 640 standing nodes, so a 4,000-node search cannot run out in it as in the open cave the game
	/// searched (up to row 306): the step itself is checked, then the plan.
	/// </summary>
	[Theory]
	[InlineData("nopath_2035_353_to_2030_359.txt", 2018, 341)]
	[InlineData("nopath_2032_348_to_2030_356.txt", 2018, 336)]
	public void OreUnderAPool_PastBlocksPlacedIntoTheWater_IsReachable(string name, int originX, int originY)
	{
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", name);
		var (g, b, start, ore) = Load(file);
		b.BlockCount = 721; // as in the header
		for (int x = 2036; x <= 2040; x++)
			g.Set(x - originX, 354 - originY, CellKind.Solid | CellKind.Water);
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		float headUnderStep = ctx.HeadUnderCostMultiplier * ctx.WalkTileCost;

		// The way down: from (2035,353), mine (2036,354) and land at (2035,356) with the feet in the pool's water.
		var top = new TilePos(2035 - originX, 353 - originY);
		var edges = new System.Collections.Generic.List<PathStep>();
		foreach (IMoveType m in ctx.Moves)
			m.Expand(ctx, top, edges);
		PathStep down = edges.Single(e => e.To == new TilePos(top.X, top.Y + 3));
		output.WriteLine($"the way down: {down}");
		Assert.True(down.Cost < headUnderStep, $"{down}: priced as a dive");

		var goal = new GoalReachTile(ore, PhysicsParams.Default, tileBoost: -1);
		var search = new PathSearch(ctx, start, goal, TerraBot.Bot.Skills.MineSkill.ReachLimits);
		search.Step(1e9);
		output.WriteLine($"start {start} h {goal.Heuristic(start):0.#}: {search.Result} {search.Result.Why}");
		foreach (PathStep s in search.Result.Steps)
			output.WriteLine("  " + s);
		Assert.Equal(PathStatus.Found, search.Result.Status);
		Assert.True(search.Result.Cost < headUnderStep, $"cost {search.Result.Cost:0}");
		// The next dump draws the block as it is.
		Assert.Equal(CellKind.Solid | CellKind.Water, CellKindExtensions.FromChar(g.Get(2036 - originX, 354 - originY).ToChar()));
	}

	[Fact]
	public void CaveBelowSpawn_HasAWayBackUp()
	{
		// world_eye runs 22, 25, 26: from this cave, 57 tiles below the spawn, no 40,000-node search found a way back.
		// The strict search (no step on what the path digs away) finds none; WalkToSkill then plans without the check.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2124_339_to_2088_282.txt");
		var (g, b, start, home) = Load(file);
		b.BlockCount = 136; // as in game
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var goal = new GoalNearTile(home, 2, PhysicsParams.Default);
		// Whole-way reachability: segments off (in game the walk goes in segments, see below).
		var strict = new PathSearch(ctx, start, goal, new SearchLimits { SegmentAfter = 0 });
		strict.Step(1e9);
		output.WriteLine("strict: " + strict.Result);
		var relaxed = new PathSearch(ctx, start, goal, new SearchLimits { CheckBrokenSupport = false, SegmentAfter = 0 });
		relaxed.Step(1e9);
		output.WriteLine("relaxed: " + relaxed.Result);
		Assert.True(strict.Result.Status == PathStatus.Found || strict.Result.Status == PathStatus.NoPath && relaxed.Result.Status == PathStatus.Found);
		// In game: a segment of the way after a few thousand nodes, not the whole 57-row climb searched while standing still.
		var segment = new PathSearch(ctx, start, goal, new SearchLimits());
		segment.Step(1e9);
		output.WriteLine("segment: " + segment.Result);
		Assert.Equal(PathStatus.Partial, segment.Result.Status);
		Assert.True(segment.Result.Expanded <= SearchLimits.Default.SegmentAfter + 1);
		Assert.True(segment.Result.Best.ChebyshevTo(start) >= SearchLimits.Default.SegmentMinTiles);
	}

	[Fact]
	public void CaveBelowSpawn_Run38_NoBlocks_OnlyTheRelaxedSearchGetsOut()
	{
		// world_eye run 38: the same cave as runs 22-26, with every block held back for building: three 40,000-node
		// searches found no way home. Without placing, the only way up stands on blocks the path itself digs away, which
		// the broken-support check rules out; WalkToSkill then plans again without the check.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2124_339_to_2092_282.txt");
		var (g, b, start, home) = Load(file);
		b.BlockCount = 0;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true, BadEdges = BadEdgesOf(file) };
		var goal = GoalOf(file, home);
		var strict = new PathSearch(ctx, start, goal, new SearchLimits());
		strict.Step(1e9);
		output.WriteLine("with the broken-support check: " + strict.Result);
		var relaxed = new PathSearch(ctx, start, goal, new SearchLimits { CheckBrokenSupport = false });
		relaxed.Step(1e9);
		output.WriteLine("without: " + relaxed.Result);
		Assert.Equal(PathStatus.NoPath, strict.Result.Status);
		Assert.Equal(PathStatus.Found, relaxed.Result.Status);
	}

	[Fact]
	public void PocketBesideAMinecartTrack_DigsUpThroughTheCeiling()
	{
		// world_eye run 49: the only way on from this pocket was straight up through its ceiling, and no move did that, so
		// every walk home found "no path" (strict and relaxed). Pillars now mine the row above the head first.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2114_359_to_2080_281.txt");
		var (g, b, start, home) = Load(file);
		b.BlockCount = 588;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var search = new PathSearch(ctx, start, GoalOf(file, home), new SearchLimits());
		search.Step(1e9);
		output.WriteLine(search.Result.ToString());
		Assert.NotEqual(PathStatus.NoPath, search.Result.Status);
	}

	[Fact(Skip = "debug aid: prints which nodes the moves reach from a dump's start")]
	public void Debug_ReachableFromCave()
	{
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2124_339_to_2088_282.txt");
		var (g, b, start, home) = Load(file);
		b.BlockCount = 136;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var seen = new System.Collections.Generic.HashSet<TilePos> { start };
		var queue = new System.Collections.Generic.Queue<TilePos>();
		queue.Enqueue(start);
		var edges = new System.Collections.Generic.List<PathStep>();
		while (queue.Count > 0) {
			TilePos n = queue.Dequeue();
			edges.Clear();
			foreach (IMoveType m in ctx.Moves)
				m.Expand(ctx, n, edges);
			foreach (PathStep e in edges)
				if (!float.IsPositiveInfinity(e.Cost) && seen.Add(e.To))
					queue.Enqueue(e.To);
		}
		for (int y = 0; y < g.Height; y++)
			output.WriteLine(new string(Enumerable.Range(0, g.Width).Select(x => seen.Contains(new TilePos(x, y)) ? 'o' : g.Get(x, y).ToChar()).ToArray()));
	}

	/// <summary>The goal a dump's header names (Reach / StandAt / Near), at the T cell.</summary>
	public static IGoal GoalOf(string file, TilePos t)
	{
		string head = File.ReadLines(file).First();
		if (head.Contains("goal Reach("))
			return new GoalReachTile(t, PhysicsParams.Default);
		if (head.Contains("goal Near("))
			return new GoalNearTile(t, int.Parse(head.Split(")±")[1].Split(' ')[0]), PhysicsParams.Default);
		return new GoalStandAt(t, PhysicsParams.Default);
	}

	[Fact(Skip = "survey: replays every dump in Dumps/ and prints the result")]
	public void Survey_AllDumps()
	{
		foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Dumps"), "*.txt")) {
			var (g, b, start, t) = Load(file);
			b.BlockCount = 100;
			var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true, BadEdges = BadEdgesOf(file) };
			var search = new PathSearch(ctx, start, GoalOf(file, t), new SearchLimits());
			search.Step(1e9);
			output.WriteLine($"{Path.GetFileName(file)}: {search.Result}");
		}
	}

	[Fact]
	public void DeepCaveOnATrack_HasAWayUp()
	{
		// Measured run R1: "up to the surface" from row 451 found no node closer than the start, every time, and the bot
		// went back and forth along the cave floor for game hours (user report, 2026-10-15).
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2053_451_to_2053_303.txt");
		var (g, b, start, goal) = Load(file);
		b.BlockCount = 1317;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b };
		var edges = new System.Collections.Generic.List<PathStep>();
		foreach (IMoveType m in ctx.Moves)
			m.Expand(ctx, start, edges);
		output.WriteLine($"start {start} standable {Body.IsStandable(g, start)}; moves: {string.Join(" | ", edges)}");
		output.WriteLine(WalkAround(g, start));
		var search = new PathSearch(ctx, start, new GoalNearTile(goal, 4, PhysicsParams.Default), new SearchLimits { SegmentAfter = 0, MaxExpansions = 20000 });
		search.Step(1e9);
		output.WriteLine($"{search.Result} best {search.Result.Best}");
		Assert.True(search.Result.Best.Y < start.Y - 5, $"no way up: {search.Result}");
	}

	private static string WalkAround(ArrayTileGrid g, TilePos c)
	{
		var sb = new System.Text.StringBuilder();
		for (int y = c.Y - 6; y <= c.Y + 3; y++) {
			for (int x = c.X - 6; x <= c.X + 7; x++)
				sb.Append(x == c.X && y == c.Y ? 'F' : g.Get(x, y).ToChar());
			sb.Append('|');
		}
		return sb.ToString();
	}
}
