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
/// the map (F start, T goal anchor, X unbreakable, U unsafe), copied into Dumps/.
/// </summary>
public class DumpReplayTests
{
	private readonly ITestOutputHelper output;
	public DumpReplayTests(ITestOutputHelper o) => output = o;

	/// <summary>Grid (world offset removed), start and goal anchor in grid coordinates.</summary>
	public static (ArrayTileGrid grid, TestBlockActions blocks, TilePos start, TilePos goal) Load(string file)
	{
		string[] lines = File.ReadAllLines(file);
		string[] rows = lines.Skip(1).Where(l => l.Length > 0 && !l.StartsWith("bad ")).Select(l => l.Trim()).ToArray();
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

	[Fact]
	public void CaveBelowSpawn_HasAWayBackUp()
	{
		// world_eye runs 22, 25, 26: from this cave, 57 tiles below the spawn, no 40,000-node search found a way back.
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2124_339_to_2088_282.txt");
		var (g, b, start, home) = Load(file);
		b.BlockCount = 136; // as in game
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var goal = new GoalNearTile(home, 2, PhysicsParams.Default);
		output.WriteLine($"start {start} startable {Body.IsStartable(g, start)} home {home}");
		var off = new PathSearch(ctx, start, goal, new SearchLimits { CheckBrokenSupport = false });
		off.Step(1e9);
		output.WriteLine("without the broken-support check: " + off.Result);
		var search = new PathSearch(ctx, start, goal, new SearchLimits());
		var sw = System.Diagnostics.Stopwatch.StartNew();
		search.Step(1e9);
		output.WriteLine(search.Result.ToString() + $" ({sw.ElapsedMilliseconds} ms)");
		Assert.Equal(PathStatus.Found, search.Result.Status);
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
}
