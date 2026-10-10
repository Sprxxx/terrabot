using System;
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

	private static TilePos FindStand(ArrayTileGrid g, int x)
	{
		for (int y = 3; y < g.Height - 1; y++)
			if (Body.IsStandable(g, x, y))
				return new TilePos(x, y);
		throw new InvalidOperationException("no standable node in column " + x);
	}
}
