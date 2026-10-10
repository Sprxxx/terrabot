using System;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Execution;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Closed loop: WalkToSkill (planner + executor) drives the approximate collision simulator. Catches control
/// bugs (overshoot, jumps that never fire, wrong step completion) before anything runs in the game.
/// </summary>
public class ExecutorSimTests
{
	private readonly ITestOutputHelper output;

	public ExecutorSimTests(ITestOutputHelper output) => this.output = output;

	private sealed class TestLog : IBotLog
	{
		private readonly ITestOutputHelper o;
		public TestLog(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m + " " + ex);
		public void Debug(string m) => o.WriteLine("DEBUG " + m);
	}

	private (SkillStatus status, WalkToSkill skill, SimWorld sim) Run(ArrayTileGrid g, int maxTicks = 2000, bool trace = false)
	{
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		var ctx = new BotContext { Grid = g, Log = new TestLog(output), Trace = trace };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks at {sim.Node}, replans {skill.Replans}");
		return (status, skill, sim);
	}

	[Fact]
	public void Walks_FlatGround()
	{
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..S......................G....",
			"##############################");
		var (status, skill, sim) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
		Assert.InRange(sim.Ticks, 23 * 5, 23 * 5 + 60); // ~5.3 ticks/tile plus acceleration and braking
	}

	[Fact]
	public void Walks_UpStairs()
	{
		var g = ArrayTileGrid.Parse(
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"....................G.....",
			"................##########",
			"............##############",
			"..S.....##################",
			"##########################");
		var (status, skill, _) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Jumps_OntoLedge()
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
			"..........############",
			"..........############",
			"..........############",
			"..........############",
			"..S.......############",
			"######################");
		var (status, skill, _) = Run(g, trace: true);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Jumps_Gap()
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
		var (status, skill, _) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Drops_IntoNarrowShaft()
	{
		var g = ArrayTileGrid.Parse(
			"..................",
			"..................",
			"..................",
			"..S...............",
			"#######..#########",
			"#######..#########",
			"#######..#########",
			"#######..#########",
			"#######..#########",
			"#######..#########",
			"#######..#########",
			"#######.G#########",
			"##################");
		var (status, skill, _) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void UnevenTerrain_MixedMoves()
	{
		var g = ArrayTileGrid.Parse(
			"................................................",
			"................................................",
			"................................................",
			"................................................",
			"................................................",
			"................................................",
			"................................................",
			"...........................####.................",
			"......................#....####.........G.......",
			"..S.........###.......#....####.......#######...",
			"#######....#####.....####..####...#.############",
			"#######....######...#####..####..##.############",
			"################################################");
		var (status, skill, _) = Run(g, 4000);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Unreachable_FailsCleanly()
	{
		var g = ArrayTileGrid.Parse(
			"..........##########",
			"..........#........#",
			"..........#........#",
			"..........#.....G..#",
			"..S.......##########",
			"####################");
		var (status, skill, _) = Run(g, 1000);
		Assert.Equal(SkillStatus.Failed, status);
		Assert.Contains("no path", skill.FailureReason);
	}

	[Fact]
	public void BlockedPath_IsDetected()
	{
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..S......................G....",
			"##############################");
		var ctx = new MoveContext(g, PhysicsParams.Default);
		PathResult plan = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default));
		var exec = new PathExecutor(ctx, plan.Steps);
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		for (int t = 0; t < 10; t++)
			sim.Step(exec.Tick(sim.State));
		// Wall up ahead (3 high, can't step or jump... well, it could jump; block the full height instead).
		g.Fill(15, 0, 15, 4, CellKind.Solid);
		for (int t = 0; t < 40 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		Assert.Equal(ExecStatus.Blocked, exec.Status);
		output.WriteLine(exec.Reason);
	}
}
