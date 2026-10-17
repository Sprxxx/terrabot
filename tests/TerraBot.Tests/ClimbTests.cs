using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Pathfinding.Moves;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Ropes (<see cref="ClimbMove"/>): the user's world Journey Test (2026-10-17), a house a mod builds over the spawn, its way in a
/// rope from the spawn 21 tiles up a shaft to the platforms of its floor (the self-test's spawn-area dump: rope at x 3201, rows
/// 414-434, platforms x 3199-3203 on row 413, the shaft x 3199-3203 between walls).
/// </summary>
public class ClimbTests
{
	private readonly ITestOutputHelper output;
	public ClimbTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The house over the rope: a room (rows 1-6) on a floor with platforms over the shaft (row 7), the rope 21 tiles down to the ground.</summary>
	private static string[] RopeHouse(bool rope = true)
	{
		var rows = new List<string> {
			"@@@@@@@@@@@@@@@@@@@@",
			"@..................@",
			"@..................@",
			"@..................@",
			"@..................@",
			"@..................@",
			"@.........G........@",
			"@@@@@@@@=====@@@@@@@",
		};
		for (int y = 8; y <= 28; y++) {
			char[] r = (y < 21 ? "@@@@@@@@.....@@@@@@@" : "....................").ToCharArray();
			if (rope)
				r[10] = '|';
			if (y == 28)
				r[9] = 'S';
			rows.Add(new string(r));
		}
		rows.Add(new string('@', 20));
		return rows.ToArray();
	}

	private PathResult Plan(string[] rows, bool tools)
	{
		(ArrayTileGrid g, TestBlockActions b) = TestBlockActions.Parse(rows);
		b.BlockCount = tools ? 200 : 0;
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = tools, AllowPlace = tools };
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default), new SearchLimits { SegmentAfter = 0 });
		output.WriteLine(r.ToString());
		foreach (PathStep s in r.Steps)
			output.WriteLine("  " + s + (s.Via is TilePos v ? $" via {v}" : ""));
		return r;
	}

	[Fact]
	public void UpTheRope_IntoTheHouse_NotAPillarUpTheShaft()
	{
		PathResult r = Plan(RopeHouse(), tools: true);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep climb = Assert.Single(r.Steps, s => s.Kind == MoveKind.Climb);
		Assert.Equal(new TilePos(10, 10), climb.Via); // the hang: rope column 10, feet two under the rope's top cell (row 8)
		Assert.DoesNotContain(r.Steps, s => s.Kind == MoveKind.Pillar || s.HasActions);
	}

	[Fact]
	public void NoRope_TheShaftIsNotClimbed()
	{
		PathResult r = Plan(RopeHouse(rope: false), tools: false);
		Assert.NotEqual(PathStatus.Found, r.Status);
	}

	[Fact]
	public void TheHang_IsUnderTheRopesTop_AndOnlyFromUnderTheRope()
	{
		ArrayTileGrid g = ArrayTileGrid.Parse(RopeHouse());
		Assert.True(ClimbMove.FindHang(g, new TilePos(9, 28), out TilePos hang));
		Assert.Equal(new TilePos(10, 10), hang);
		Assert.True(ClimbMove.FindHang(g, new TilePos(10, 28), out _)); // the rope in the body's left column
		Assert.False(ClimbMove.FindHang(g, new TilePos(12, 28), out _)); // beside it: no hold
		// 21 tiles at the rope's speeds: about 100 ticks (0.2 px/tick a tick to 3, then 0.02 a tick).
		float t = ClimbMove.ClimbTicks(18 * 16);
		output.WriteLine($"{t:0.0} ticks for 18 tiles");
		Assert.InRange(t, 80f, 110f);
	}
}
