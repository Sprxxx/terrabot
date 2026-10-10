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
