using System;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Hand-made grids. 'S' = start node (left body column, feet row), 'G' = goal feet tile.</summary>
public class PathfinderTests
{
	private readonly ITestOutputHelper output;

	public PathfinderTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void FlatGround_WalksStraight()
	{
		var g = ArrayTileGrid.Parse(
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
"....................",
			"..S.............G...",
			"####################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.All(r.Steps, s => Assert.Equal(MoveKind.Walk, s.Kind));
		Assert.Equal(13, r.Steps.Count); // node 2 -> node 15 (body over column 16)
	}

	[Fact]
	public void StepUp_UsesStepNotJump()
	{
		var g = ArrayTileGrid.Parse(
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..S.......############",
			"######################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.True(r.Has(MoveKind.StepUp));
		Assert.False(r.Has(MoveKind.Jump));
	}

	[Fact]
	public void ThreeTileLedge_NeedsJump()
	{
		var g = ArrayTileGrid.Parse(
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..........############",
			"..........############",
			"..S.......############",
			"######################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep jump = Assert.Single(r.Steps, s => s.Kind == MoveKind.Jump);
		Assert.Equal(-3, jump.To.Y - jump.From.Y);
	}

	[Fact]
	public void SevenTileWall_IsUnreachable()
	{
		var g = ArrayTileGrid.Parse(
			"......................",
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..........############",
			"..........############",
			"..........############",
			"..........############",
			"..........############",
			"..........############",
			"..S.......############",
			"######################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status);
	}

	[Fact]
	public void Gap_IsJumpedNotWalkedOrDropped()
	{
		var g = ArrayTileGrid.Parse(
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
			"....................",
"....................",
			"..S.............G...",
			"#######....#########",
			"#######!!!!#########",
			"####################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.False(r.Has(MoveKind.Drop));
		Assert.Contains(r.Steps, s => s.Kind == MoveKind.Jump && Math.Abs(s.To.X - s.From.X) >= 4);
		int floorRow = g.Markers['S'].Y;
		Assert.All(r.Steps, s => Assert.Equal(floorRow, s.To.Y)); // never goes into the pit
	}

	private static readonly string[] WideLavaGap = {
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..S.......................G...",
		"##########........############",
		"##########!!!!!!!!############",
		"##############################",
	};

	[Fact]
	public void EightWideGap_NeedsRunningJump()
	{
		var g = ArrayTileGrid.Parse(WideLavaGap);
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep jump = Assert.Single(r.Steps, s => s.Kind == MoveKind.Jump);
		Assert.True(jump.Template.RunupTiles > 0, jump.ToString());
		Assert.All(r.Steps, s => Assert.Equal(9, s.To.Y));
	}

	[Fact]
	public void EightWideGap_WithoutRoomToRun_IsUnreachable()
	{
		string[] rows = (string[])WideLavaGap.Clone();
		for (int y = 0; y <= 9; y++)
			rows[y] = "########" + rows[y].Substring(8); // wall right behind the edge: only the edge nodes remain
		var g = ArrayTileGrid.Parse(rows);
		g.Markers['S'] = new TilePos(8, 9);
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status);
	}

	[Fact]
	public void UnreachableTarget_OnTallPillar_ReturnsNoPath()
	{
		var g = ArrayTileGrid.Parse(
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..............##......",
			"..S...........##......",
			"######################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status);
		Assert.Empty(r.Steps);
		Assert.True(r.Expanded > 0);
	}

	[Fact]
	public void UnreachableTarget_SealedRoom_ReturnsNoPath()
	{
		var g = ArrayTileGrid.Parse(
			"..........##########",
			"..........#........#",
			"..........#........#",
			"..........#.....G..#",
			"..S.......##########",
			"####################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status);
	}

	[Fact]
	public void LowCeiling_BlocksJump()
	{
		// Low corridor (22 px headroom above the head): walking works, but a jump onto the 2-high ledge needs 32 px.
		var g = ArrayTileGrid.Parse(
			"######################",
			"#.....................",
			"#.....................",
			"#............G........",
			"#.........############",
			"#.S.......############",
			"######################");
		g.Fill(0, 0, 9, 1, CellKind.Solid);
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status);
	}

	[Fact]
	public void Drop_DownLedge()
	{
		var g = ArrayTileGrid.Parse(
			"......................",
			"......................",
			"......................",
			"..S...................",
			"#######...............",
			"#######...............",
			"#######...............",
			"#######...............",
			"#######...............",
			"#######.........G.....",
			"######################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep drop = Assert.Single(r.Steps, s => s.Kind == MoveKind.Drop);
		Assert.Equal(6, drop.To.Y - drop.From.Y);
	}

	[Fact]
	public void Drop_BeyondSafeFall_IsRefused()
	{
		var rows = new string[34];
		rows[0] = "........";
		rows[1] = "........";
		rows[2] = ".S......";
		for (int y = 3; y < 33; y++)
			rows[y] = "####....";
		rows[32] = "####..G.";
		rows[33] = "########";
		var g = ArrayTileGrid.Parse(rows);
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.NoPath, r.Status); // 30-tile drop > 25-tile fall-damage limit
	}

	[Fact]
	public void Platform_JumpedThroughFromBelow()
	{
		var g = ArrayTileGrid.Parse(
			"..................",
			"..................",
			"..................",
			"..................",
			".......G..........",
			"......====........",
			"..................",
			"..................",
			"..................",
			"..S...............",
			"##################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep jump = Assert.Single(r.Steps, s => s.Kind == MoveKind.Jump);
		Assert.Equal(-5, jump.To.Y - jump.From.Y);
	}

	[Theory]
	[InlineData(5, 8, 7)]   // surface block: stand on it
	[InlineData(5, 12, 7)]  // buried block: climb the column to the surface
	[InlineData(5, 2, 7)]   // air above the ground: drop to the floor below
	[InlineData(12, 1, 4)]  // air above a pillar: stand on the pillar
	[InlineData(12, 6, 4)]  // inside the pillar: stand on top of it
	public void GoalResolver_PicksStandingSpot(int tx, int ty, int expectedFeetY)
	{
		var g = new ArrayTileGrid(16, 14);
		g.Fill(0, 8, 15, 13, CellKind.Solid);  // ground: top row 8
		g.Fill(12, 5, 12, 7, CellKind.Solid);  // pillar at x = 12, top row 5
		GoalStandAt goal = GoalResolver.StandNear(g, new TilePos(tx, ty), TestUtil.P);
		Assert.Equal(new TilePos(tx, expectedFeetY), goal.Feet);
	}

	[Fact]
	public void GoalResolver_WithDigging_StandsOnBuriedBlock()
	{
		var g = new ArrayTileGrid(16, 14);
		g.Fill(0, 8, 15, 13, CellKind.Solid);
		Assert.Equal(new TilePos(5, 11), GoalResolver.StandNear(g, new TilePos(5, 12), TestUtil.P, canDig: true).Feet);
		Assert.Equal(new TilePos(5, 7), GoalResolver.StandNear(g, new TilePos(5, 12), TestUtil.P, canDig: false).Feet);
	}

	[Fact]
	public void IncrementalSearch_MatchesOneShot()
	{
		var g = new ArrayTileGrid(200, 30);
		g.Fill(0, 25, 199, 29, CellKind.Solid);
		for (int x = 20; x < 200; x += 20)
			g.Fill(x, 22, x + 1, 24, CellKind.Solid); // 3-high bumps to jump over
		var ctx = new MoveContext(g, TestUtil.P);
		var goal = new GoalStandAt(new TilePos(190, 24), TestUtil.P);
		PathResult oneShot = new AStarPathfinder().FindPath(ctx, new TilePos(2, 24), goal);

		var search = new PathSearch(ctx, new TilePos(2, 24), goal);
		int slices = 0;
		while (!search.Step(0.0))
			slices++;
		output.WriteLine($"{oneShot}; incremental took {slices + 1} slices");
		Assert.Equal(PathStatus.Found, search.Result.Status);
		Assert.Equal(oneShot.Steps, search.Result.Steps);
	}

	[Fact]
	public void PlatformFloor_IsDroppedThrough()
	{
		var g = ArrayTileGrid.Parse(
			"..................",
			"..................",
			"..................",
			"......S...........",
			"====================",
			"..................",
			"..................",
			"..................",
			"......G...........",
			"##################");
		PathResult r = TestUtil.Plan(g, output);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep drop = Assert.Single(r.Steps, s => s.Kind == MoveKind.DropThrough);
		Assert.Equal(5, drop.To.Y - drop.From.Y);
	}

	[Fact]
	public void SearchBudget_ReturnsPartialTowardGoal()
	{
		var g = new ArrayTileGrid(400, 12);
		g.Fill(0, 11, 399, 11, CellKind.Solid);
		g.Markers['S'] = new TilePos(2, 10);
		g.Markers['G'] = new TilePos(390, 10);
		PathResult r = TestUtil.Plan(g, null, new SearchLimits { MaxExpansions = 50 });
		Assert.Equal(PathStatus.Partial, r.Status);
		Assert.True(r.Best.X > 2);
		Assert.Equal(r.Best, r.Steps.Last().To);
	}
}
