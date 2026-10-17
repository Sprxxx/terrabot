using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Down a shaft deeper than a safe fall (<see cref="Core.Pathfinding.Moves.ArrestedDropMove"/>): measured run U30 (world_eye),
/// the corruption chasm under the lip at (1779,341), 55 rows of open shaft below it, and the DemonAltar at (1748,411) beyond
/// its foot; for 117,000 ticks every walk there ended "no path ... start h 112, lowest h 112".
/// </summary>
public class ShaftDescentTests
{
	private readonly ITestOutputHelper output;
	public ShaftDescentTests(ITestOutputHelper output) => this.output = output;

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m);
		public void Debug(string m) { }
	}

	/// <summary>
	/// Unbreakable rock ('@', ebonstone under an iron pickaxe) with a lip at row 9 (feet) left of a shaft at columns
	/// <paramref name="x0"/>..<paramref name="x1"/> from row 10 down <paramref name="depth"/> rows to a floor, the goal 'G'
	/// on it; with <paramref name="walls"/>, a background wall behind every open cell of the shaft (a chasm's ebonstone wall).
	/// </summary>
	private static (ArrayTileGrid g, TestBlockActions b) Shaft(int x0, int x1, int depth, bool walls, int width = 30)
	{
		var rows = new List<string>();
		int floor = 10 + depth;
		for (int y = 0; y <= floor; y++) {
			var r = new char[width];
			for (int x = 0; x < width; x++)
				r[x] = y < 10 ? '.' : y < floor && x >= x0 && x <= x1 ? '.' : '@';
			if (y == 9)
				r[x0 - 3] = 'S';
			if (y == floor - 1)
				r[x0 + 1] = 'G';
			rows.Add(new string(r));
		}
		(ArrayTileGrid g, TestBlockActions b) = TestBlockActions.Parse(rows.ToArray());
		b.BlockCount = 200;
		if (walls)
			for (int y = 10; y < floor; y++)
				for (int x = x0; x <= x1; x++)
					b.Walls.Add(new TilePos(x, y));
		return (g, b);
	}

	private PathResult Plan(ArrayTileGrid g, TestBlockActions b)
	{
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default), new SearchLimits { SegmentAfter = 0 });
		output.WriteLine(r.ToString());
		foreach (PathStep s in r.Steps)
			output.WriteLine("  " + s);
		return r;
	}

	[Fact]
	public void AChasmDeeperThanASafeFall_WithItsWallBehind_IsPlannedDown_BlockByBlock_NoFallOverASafeOne()
	{
		// The U30 chasm under (1779,341): 55 rows to its foot, 10 columns wide, ebonstone each side, its wall behind.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 21, 55, walls: true);
		PathResult r = Plan(g, b);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.Contains(r.Steps, s => s.Kind == MoveKind.ArrestedDrop);
		int maxDrop = new MoveContext(g, PhysicsParams.Default).MaxDrop;
		foreach (PathStep s in r.Steps.Where(s => s.Kind == MoveKind.ArrestedDrop)) {
			Assert.InRange(s.To.Y - s.From.Y, 1, maxDrop);
			Assert.Equal(ActionKind.Place, Assert.Single(s.Actions).Kind);
		}
	}

	[Fact]
	public void AWideShaft_WithNothingToFixABlockTo_IsNotPlannedDown()
	{
		// Vanilla takes a block into open air only beside a solid tile or a wall: mid-shaft with neither, no catch. A 10-wide
		// shaft with no wall behind: only down its sides, against the rock.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 21, 55, walls: false);
		PathResult r = Plan(g, b);
		foreach (PathStep s in r.Steps.Where(s => s.Kind == MoveKind.ArrestedDrop)) {
			TilePos c = s.Actions[0].Cell;
			Assert.True(g.Get(c.X - 1, c.Y).IsSolid() || g.Get(c.X + 1, c.Y).IsSolid() || g.Get(c.X, c.Y - 1).IsSolid() || g.Get(c.X, c.Y + 1).IsSolid(),
				$"a block at {c} with nothing to hold it");
		}
	}

	/// <summary>
	/// Runs a walk to 'G' in the simulator, the brain's fall reflex after it each tick as BotBrain does (with the walk's planned
	/// catch handed over through <see cref="BotContext.PlannedCatch"/>); the worst fall in rows, as vanilla counts it.
	/// </summary>
	private (SkillStatus status, SimWorld sim, int worst, bool reflexActed, WalkToSkill walk) Walk(ArrayTileGrid g, TestBlockActions b, int maxTicks = 6000)
	{
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S'], b);
		var ctx = new BotContext { Grid = g, Blocks = b, AllowBreak = true, AllowPlace = true, Log = new Log(output) };
		var skill = new WalkToSkill(g.Markers['G']);
		var arrest = new FallArrest();
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		int fallStart = (int)(sim.State.Y / 16f), worst = 0;
		bool acted = false;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			ctx.PlannedCatch = null;
			status = skill.Tick(ctx);
			arrest.Tick(ctx);
			acted |= arrest.Active;
			bool wasDown = sim.State.VY == 0f;
			sim.Step(ctx.Output);
			int row = (int)(sim.State.Y / 16f);
			if (sim.State.VY == 0f) {
				if (!wasDown)
					worst = Math.Max(worst, row - fallStart);
				fallStart = row;
			}
			else if (sim.State.Jump > 0)
				fallStart = row;
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks at {sim.Node}, replans {skill.Replans}, placed {sim.Placed}, out of range {sim.OutOfRange}, worst fall {worst} rows, reflex {(acted ? "acted" : "idle")}");
		return (status, sim, worst, acted, skill);
	}

	[Fact]
	public void Sim_DownAChasmDeeperThanASafeFall_LandsOnItsOwnBlocks_AndTheReflexLeavesItToTheWalk()
	{
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 21, 55, walls: true);
		var (status, sim, worst, reflex, walk) = Walk(g, b);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, walk.Replans); // each landing where the plan has it: off the block onwards, not back over it
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles, $"fell {worst} rows");
		Assert.True(sim.Placed >= 2, $"placed {sim.Placed}"); // 55 rows: at least two catches of at most 23
		Assert.False(reflex, "the fall reflex took over a planned catch");
	}

	[Fact]
	public void Sim_ACatchThatCannotBeMade_TheReflexStillStopsTheFall()
	{
		// The walk's block refused (an NPC in the cell, say): no catch made, and the fall reflex has it, as for any fall.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 21, 55, walls: true);
		PathResult r = Plan(g, b);
		PathStep first = r.Steps.First(s => s.Kind == MoveKind.ArrestedDrop);
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S'], b);
		var ctx = new BotContext { Grid = g, Blocks = b, AllowBreak = true, AllowPlace = true, Log = new Log(output) };
		var exec = new Core.Execution.PathExecutor(ctx.Moves, r.Steps);
		var arrest = new FallArrest();
		int fallStart = (int)(sim.State.Y / 16f), worst = 0;
		bool acted = false;
		for (int t = 0; t < 600; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			MoveInput input = exec.Tick(sim.State);
			// The game refuses the planned cell: aimed there, nothing happens.
			if (input.UseItem && input.HasAim && input.AimX == first.Actions[0].Cell.X && input.AimY == first.Actions[0].Cell.Y)
				input.UseItem = false;
			ctx.Output = input;
			ctx.PlannedCatch = exec.CatchCell;
			arrest.Tick(ctx);
			acted |= arrest.Active;
			bool wasDown = sim.State.VY == 0f;
			sim.Step(ctx.Output);
			int row = (int)(sim.State.Y / 16f);
			if (sim.State.VY == 0f) {
				if (!wasDown)
					worst = Math.Max(worst, row - fallStart);
				fallStart = row;
				if (t > 30 && exec.Status != Core.Execution.ExecStatus.Running)
					break;
			}
			else if (sim.State.Jump > 0)
				fallStart = row;
		}
		output.WriteLine($"at {sim.Node}, {exec.Status}: {exec.Reason}; placed {sim.Placed}, worst fall {worst}");
		Assert.True(acted, "the reflex never acted");
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles, $"fell {worst} rows");
	}

	/// <summary>
	/// The U30 region dump from the chasm's lip (WalkToSkill.DumpRegion, nopath_1779_341_to_1750_411.txt: origin (1738,329),
	/// pick 40, so 'X' ebonstone is unbreakable), with the chasm's wall behind its open cells: the run's fall arrest placed a block
	/// at (1776,370), mid-shaft with open cells all round ("Fall: placing a block at (1776,370) to land on, 28 rows below where
	/// the fall began"), which vanilla takes only against a wall there (and ChasmRunner puts wall 3 over its carve).
	/// </summary>
	[Fact]
	public void U30_FromTheChasmLip_TheWayDownTowardsTheAltar_IsPlanned()
	{
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_1779_341_to_1750_411.txt");
		var (g, b, start, _) = DumpReplayTests.Load(file);
		var origin = new TilePos(1738, 329);
		var altar = new TilePos(1750 - origin.X, 411 - origin.Y);
		b.BlockCount = 1565; // "blocks 1565" in the dump's header
		var goal = new GoalNearTile(altar, 2, PhysicsParams.Default);
		// The chasm's open cells, x 1763..1781, rows 342..419 in the world (its shaft and the cavern it opens into).
		for (int y = 342 - origin.Y; y < g.Height; y++)
			for (int x = 1763 - origin.X; x <= 1781 - origin.X; x++)
				if (!g.Get(x, y).IsSolid())
					b.Walls.Add(new TilePos(x, y));
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		var search = new PathSearch(ctx, start, goal, SearchLimits.Default);
		search.Step(1e9);
		PathResult r = search.Result;
		output.WriteLine($"{r}; best {r.Best} = world ({r.Best.X + origin.X},{r.Best.Y + origin.Y}), start h {goal.Heuristic(start):0}, best h {goal.Heuristic(r.Best):0}");
		foreach (PathStep s in r.Steps)
			output.WriteLine("  " + s);
		Assert.True(r.Status is PathStatus.Found or PathStatus.Partial, r.ToString());
		Assert.Contains(r.Steps, s => s.Kind == MoveKind.ArrestedDrop);
		Assert.True(goal.Heuristic(r.Best) < goal.Heuristic(start), "no nearer the altar than the lip");
		Assert.True(r.Best.Y + origin.Y >= 380, $"not down the shaft: {r.Best}");
	}
}
