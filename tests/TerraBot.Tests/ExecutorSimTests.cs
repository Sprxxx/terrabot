using System;
using System.Linq;
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

	/// <param name="actual">Physics the simulated player really has; the planner always sees Default.</param>
	private (SkillStatus status, WalkToSkill skill, SimWorld sim) Run(ArrayTileGrid g, int maxTicks = 2000, bool trace = false, PhysicsParams actual = null)
	{
		var sim = new SimWorld(g, actual ?? PhysicsParams.Default, g.Markers['S']);
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
	public void RunningJump_EightWideGap()
	{
		var g = ArrayTileGrid.Parse(
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
			"##############################");
		var (status, skill, sim) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
		Assert.Contains("run", skill.LastPlan.Steps.Single(s => s.Kind == MoveKind.Jump).Template.Variant);
	}

	[Fact]
	public void DropsThroughPlatformFloor_LandsOnPlatformBelow()
	{
		var g = ArrayTileGrid.Parse(
			"....................",
			"....................",
			"....................",
			"......S.............",
			"====================",
			"....................",
			"....................",
			"....................",
			"....................",
			"====================",
			"....................",
			"....................",
			"......G.............",
			"####################");
		var (status, skill, _) = Run(g);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
		Assert.Equal(2, skill.LastPlan.Steps.Count(s => s.Kind == MoveKind.DropThrough));
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

	/// <summary>The in-game player's speed toggled to these values (moveSpeed +21%) during the self-test runs.</summary>
	private static readonly PhysicsParams Faster = PhysicsParams.Default with { MaxRunSpeed = 3.63f, RunAcceleration = 0.097f };

	public static TheoryData<string> Courses => new() { "flat", "stairs", "ledge", "gap", "shaft", "uneven" };

	private static ArrayTileGrid Course(string name) => name switch {
		"flat" => ArrayTileGrid.Parse(
			"..............................", "..............................", "..............................",
			"..............................", "..S......................G....", "##############################"),
		"stairs" => ArrayTileGrid.Parse(
			"..........................", "..........................", "..........................",
			"..........................", "..........................", "..........................",
			"....................G.....", "................##########", "............##############",
			"..S.....##################", "##########################"),
		"ledge" => ArrayTileGrid.Parse(
			"......................", "......................", "......................", "......................",
			"......................", "......................", "......................", "..............G.......",
			"..........############", "..........############", "..........############", "..........############",
			"..S.......############", "######################"),
		"gap" => ArrayTileGrid.Parse(
			"....................", "....................", "....................", "....................",
			"....................", "....................", "....................", "....................",
			"....................", "....................", "..S.............G...", "#######....#########",
			"#######!!!!#########", "####################"),
		"shaft" => ArrayTileGrid.Parse(
			"..................", "..................", "..................", "..S...............",
			"#######..#########", "#######..#########", "#######..#########", "#######..#########",
			"#######..#########", "#######..#########", "#######..#########", "#######.G#########",
			"##################"),
		_ => ArrayTileGrid.Parse(
			"................................................", "................................................",
			"................................................", "................................................",
			"................................................", "................................................",
			"................................................", "...........................####.................",
			"......................#....####.........G.......", "..S.........###.......#....####.......#######...",
			"#######....#####.....####..####...#.############", "#######....######...#####..####..##.############",
			"################################################"),
	};

	[Theory]
	[MemberData(nameof(Courses))]
	public void PlanAtNormalSpeed_ExecutesAtPlus21PercentSpeed(string course)
	{
		var (status, skill, _) = Run(Course(course), 4000, actual: Faster);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, skill.Replans);
	}

	public static TheoryData<string, float> CoursesAtThreshold()
	{
		var d = new TheoryData<string, float>();
		foreach (string c in new[] { "flat", "stairs", "ledge", "gap", "shaft", "uneven" })
			foreach (float f in new[] { 1f + PhysicsParams.HorizontalTolerance, 1f / (1f + PhysicsParams.HorizontalTolerance) })
				d.Add(c, f);
		return d;
	}

	/// <summary>
	/// The largest speed change the skill keeps its plan for: the bot sees the new physics (like in game), the
	/// remaining jumps are re-fitted, and the run must finish without landing off-path or getting stuck.
	/// </summary>
	[Theory]
	[MemberData(nameof(CoursesAtThreshold))]
	public void SpeedChangeMidRun_AtIgnoreThreshold_IsAbsorbed(string course, float factor)
	{
		PhysicsParams d = PhysicsParams.Default;
		var changed = d with { MaxRunSpeed = d.MaxRunSpeed * factor, AccRunSpeed = d.AccRunSpeed * factor, RunAcceleration = d.RunAcceleration * factor };
		Assert.False(changed.MateriallyDiffers(d));

		ArrayTileGrid g = Course(course);
		var sim = new SimWorld(g, d, g.Markers['S']);
		var log = new System.Collections.Generic.List<string>();
		var ctx = new BotContext { Grid = g, Log = new ListLog(log) };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < 4000 && status == SkillStatus.Running; t++) {
			if (t == 30)
				sim.Physics = ctx.Physics = changed;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		foreach (string l in log)
			output.WriteLine(l);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.DoesNotContain(log, l => l.Contains("OffPath") || l.Contains("Stuck"));
	}

	[Theory]
	[MemberData(nameof(Courses))]
	public void PlanAtPlus21PercentSpeed_ExecutesAtNormalSpeed(string course)
	{
		ArrayTileGrid g = Course(course);
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		var ctx = new BotContext { Grid = g, Physics = Faster, Log = new TestLog(output) };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < 4000 && status == SkillStatus.Running; t++) {
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		// The bot does not know its real speed here (in game it always does, and re-fits jumps: see
		// SpeedChangeMidRun_*). A running jump at the edge of the faster reach may land a tile short, so only
		// require that it recovers and finishes.
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(skill.Replans <= 2, $"replans {skill.Replans}");
	}

	private (WalkToSkill skill, System.Collections.Generic.List<string> log) RunWithPhysicsChange(PhysicsParams changeTo)
	{
		ArrayTileGrid g = Course("uneven");
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		var log = new System.Collections.Generic.List<string>();
		var ctx = new BotContext { Grid = g, Log = new ListLog(log) };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < 4000 && status == SkillStatus.Running; t++) {
			ctx.Physics = t >= 60 && t < 160 ? changeTo : PhysicsParams.Default; // a buff that comes and goes
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		Assert.Equal(SkillStatus.Succeeded, status);
		return (skill, log);
	}

	[Fact]
	public void SmallSpeedBuff_KeepsThePlan()
	{
		var (_, log) = RunWithPhysicsChange(Faster);
		Assert.Single(log, l => l.StartsWith("Plan #"));
		Assert.Contains(log, l => l.Contains("keeping the plan"));
	}

	[Fact]
	public void JumpChange_Replans()
	{
		var (_, log) = RunWithPhysicsChange(PhysicsParams.Default with { JumpHeight = 20, JumpSpeed = 6.51f });
		Assert.True(log.Count(l => l.StartsWith("Plan #")) >= 2);
		Assert.Contains(log, l => l.StartsWith("Physics changed ("));
	}

	private sealed class ListLog : IBotLog
	{
		private readonly System.Collections.Generic.List<string> lines;
		public ListLog(System.Collections.Generic.List<string> lines) => this.lines = lines;
		public void Info(string m) => lines.Add(m);
		public void Warn(string m) => lines.Add(m);
		public void Error(string m, Exception ex = null) => lines.Add(m);
		public void Debug(string m) { }
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
	public void OvershootOntoLaterPathNode_ResyncsInsteadOfReplanning()
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
		for (int t = 0; t < 20; t++)
			sim.Step(exec.Tick(sim.State));
		sim.State.X += 4 * 16; // as if it skidded 4 tiles further than the current step expects
		for (int t = 0; t < 400 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		output.WriteLine($"{exec.Status}: {exec.Reason}; resyncs {exec.Resyncs}");
		Assert.Equal(ExecStatus.Finished, exec.Status);
		Assert.Equal(1, exec.Resyncs);
	}

	[Fact]
	public void InvisibleWall_IsDetectedAsStuck()
	{
		// The planner sees flat ground; the "real" world has a wall the planner does not know about.
		string[] rows = {
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..S......................G....",
			"##############################",
		};
		var planGrid = ArrayTileGrid.Parse(rows);
		var realGrid = ArrayTileGrid.Parse(rows);
		realGrid.Fill(12, 0, 12, 4, CellKind.Solid);

		var ctx = new MoveContext(planGrid, PhysicsParams.Default);
		PathResult plan = new AStarPathfinder().FindPath(ctx, planGrid.Markers['S'], new GoalStandAt(planGrid.Markers['G'], PhysicsParams.Default));
		var exec = new PathExecutor(ctx, plan.Steps);
		var sim = new SimWorld(realGrid, PhysicsParams.Default, planGrid.Markers['S']);
		int t = 0;
		for (; t < 400 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		output.WriteLine($"{exec.Status} after {t} ticks: {exec.Reason}");
		Assert.Equal(ExecStatus.Stuck, exec.Status);
		Assert.True(t < 120, $"took {t} ticks");
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
		// A full-height wall appears on the planned route: re-validation must flag the step as invalid.
		g.Fill(15, 0, 15, 4, CellKind.Solid);
		for (int t = 0; t < 40 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		Assert.Equal(ExecStatus.Blocked, exec.Status);
		output.WriteLine(exec.Reason);
	}
}
