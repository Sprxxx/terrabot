using System;
using System.Linq;
using System.Diagnostics;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>A* runs synchronously on the game thread, so keep an eye on how long a long plan takes.</summary>
public class PerformanceTests
{
	private readonly ITestOutputHelper output;

	public PerformanceTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void JumpLibraryBuild_IsFast()
	{
		var p = PhysicsParams.Default with { MaxRunSpeed = 3.1f }; // bypass the cache
		JumpLibrary.Build(p);
		var sw = Stopwatch.StartNew();
		JumpLibrary lib = JumpLibrary.Build(p with { MaxRunSpeed = 3.2f });
		output.WriteLine($"JumpLibrary.Build: {sw.Elapsed.TotalMilliseconds:0.0} ms, {lib.Entries.Count} entries");
		Assert.True(sw.Elapsed.TotalMilliseconds < 500);
	}

	[Fact]
	public void LongRollingTerrain_PlansInReasonableTime()
	{
		const int w = 320, h = 80;
		var g = new ArrayTileGrid(w, h);
		var rng = new Random(1234);
		int ground = 50;
		for (int x = 0; x < w; x++) {
			if (x > 5 && x < w - 5 && rng.Next(100) < 25)
				ground += rng.Next(-2, 3);
			ground = Math.Clamp(ground, 30, 70);
			bool gap = x > 10 && x < w - 10 && x % 37 < 3; // 3-wide gaps every 37 tiles
			if (!gap)
				g.Fill(x, ground, x, h - 1, CellKind.Solid);
			else
				g.Fill(x, h - 1, x, h - 1, CellKind.Solid);
		}
		TilePos start = FindStand(g, 2);
		TilePos goal = FindStand(g, w - 6);

		var ctx = new MoveContext(g, PhysicsParams.Default);
		new AStarPathfinder().FindPath(ctx, start, new GoalStandAt(goal, PhysicsParams.Default)); // warm up JIT
		PathResult r = new AStarPathfinder().FindPath(ctx, start, new GoalStandAt(goal, PhysicsParams.Default));
		output.WriteLine($"{start} -> {goal}: {r}");
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.True(r.ElapsedMs < 1000, $"took {r.ElapsedMs} ms");
	}

	[Fact]
	public void TunnelThroughSolidRock_StaysWithinBudget()
	{
		// 60 tiles of solid stone between two pockets: every node is reachable by digging, the worst case for A*.
		var g = new ArrayTileGrid(90, 40);
		g.Fill(0, 0, 89, 39, CellKind.Solid);
		g.Fill(2, 18, 6, 20, CellKind.Empty);
		g.Fill(80, 18, 85, 20, CellKind.Empty);
		var blocks = new TestBlockActions(g);
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = blocks, AllowPlace = false };
		var goal = new GoalStandAt(new TilePos(83, 20), PhysicsParams.Default);
		new AStarPathfinder().FindPath(ctx, new TilePos(3, 20), goal); // JIT warm-up
		PathResult r = new AStarPathfinder().FindPath(ctx, new TilePos(3, 20), goal);
		output.WriteLine(r.ToString());
		Assert.True(r.Status is PathStatus.Found or PathStatus.Partial, r.ToString());
		Assert.True(r.ElapsedMs < 5000, $"took {r.ElapsedMs} ms");
	}

	/// <summary>Time per expansion on a real cave (measured run J2's dump), and the share each move type takes.</summary>
	[Fact]
	public void Benchmark_CaveSegment()
	{
		string file = System.IO.Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_2053_451_to_2095_282.txt");
		var (g, b, start, _) = DumpReplayTests.Load(file);
		b.BlockCount = 860;
		var goal = new GoalNearTile(new TilePos(2095 - 2041, 282 - 270), 4, PhysicsParams.Default);
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b };
		new PathSearch(ctx, start, goal, new SearchLimits { SegmentAfter = 0, MaxExpansions = 3000 }).Step(); // JIT
		double total = double.MaxValue;
		PathSearch s = null;
		for (int i = 0; i < 8; i++) {
			var sw = Stopwatch.StartNew();
			s = new PathSearch(ctx, start, goal, new SearchLimits { SegmentAfter = 0, MaxExpansions = 3000 });
			s.Step();
			total = Math.Min(total, sw.Elapsed.TotalMilliseconds);
		}
		output.WriteLine($"{s.Result}: {total * 1000 / Math.Max(1, s.Result.Expanded):0.0} us per expansion");
		var timed = MoveContext.DefaultMoves.Select(m => new TimedMove(m)).ToArray();
		var tctx = new MoveContext(g, PhysicsParams.Default, timed) { Blocks = b };
		for (int i = 0; i < 3; i++)
			new PathSearch(tctx, start, goal, new SearchLimits { SegmentAfter = 0, MaxExpansions = 3000 }).Step();
		foreach (TimedMove m in timed) { m.Ticks = 0; m.Edges = 0; }
		var ts = new PathSearch(tctx, start, goal, new SearchLimits { SegmentAfter = 0, MaxExpansions = 3000 });
		var all = Stopwatch.StartNew();
		ts.Step();
		output.WriteLine($"timed run {all.Elapsed.TotalMilliseconds:0} ms");
		foreach (TimedMove m in timed)
			output.WriteLine($"  {m.Inner.GetType().Name}: {m.Ticks * 1000.0 / Stopwatch.Frequency:0.0} ms, {m.Edges} edges");
	}

	private sealed class TimedMove : IMoveType
	{
		public readonly IMoveType Inner;
		public long Ticks;
		public int Edges;
		public TimedMove(IMoveType inner) => Inner = inner;
		public MoveKind Kind => Inner.Kind;
		public void Expand(MoveContext ctx, TilePos from, System.Collections.Generic.List<PathStep> output)
		{
			long t0 = Stopwatch.GetTimestamp();
			int n = output.Count;
			Inner.Expand(ctx, from, output);
			Ticks += Stopwatch.GetTimestamp() - t0;
			Edges += output.Count - n;
		}
		public bool IsValid(MoveContext ctx, in PathStep step) => Inner.IsValid(ctx, step);
	}

	private static TilePos FindStand(ArrayTileGrid g, int x)
	{
		for (int y = 3; y < g.Height - 1; y++)
			if (Body.IsStandable(g, x, y))
				return new TilePos(x, y);
		throw new InvalidOperationException("no standable node in column " + x);
	}
}
