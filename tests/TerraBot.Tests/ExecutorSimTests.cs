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
	public void AJump_WhoseTargetIsReachedBeforeTakeOff_CountsAsDone()
	{
		// world_eye runs 68 and 73: "Jump (2077,362)->(2077,361) timed out ... at (2077,361)": aligning for a one-row hop onto
		// a slope walked the bot up it, and the jump waited to be back on its start until it timed out (three times, banned).
		var g = ArrayTileGrid.Parse(
			"..............",
			"..............",
			"..............",
			"..............",
			"......G.......",
			"....#####.....",
			"..S.#####.....",
			"##############");
		var ctx = new MoveContext(g, PhysicsParams.Default);
		PathResult plan = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default));
		PathStep jump = plan.Steps.First(st => st.Kind == MoveKind.Jump);
		jump = jump with { From = new TilePos(jump.To.X, jump.To.Y + 1) }; // a one-row hop, as in game (the start right below)
		var exec = new PathExecutor(ctx, new[] { jump });
		var sim = new SimWorld(g, PhysicsParams.Default, jump.To); // already standing on the jump's target
		for (int t = 0; t < 300 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		output.WriteLine($"{exec.Status}: {exec.Reason}");
		Assert.Equal(ExecStatus.Finished, exec.Status);
	}

	[Fact]
	public void ADropFromAPlatformInItsFeetRow_GoesDownThroughIt()
	{
		// Measured run S9: all 34 "Drop edge" stalls began "from feet Platform, Shaped": the plan stood the body in a sloped
		// platform, the body stood on top of it, and the drop down waited, grounded, until it timed out.
		var g = ArrayTileGrid.Parse(
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"....==....",
			"..........",
			"##########");
		var ctx = new MoveContext(g, PhysicsParams.Default);
		var drop = new PathStep(MoveKind.Drop, new TilePos(4, 6), new TilePos(4, 7), 20f);
		var exec = new PathExecutor(ctx, new[] { drop });
		var sim = new SimWorld(g, PhysicsParams.Default, new TilePos(4, 5)); // on top of the platform
		sim.State.Y -= 4f; // coming down onto it, not yet settled
		sim.State.VY = 1f;
		for (int t = 0; t < 400 && exec.Status == ExecStatus.Running; t++)
			sim.Step(exec.Tick(sim.State));
		output.WriteLine($"{exec.Status}: {exec.Reason} at {sim.Node}");
		Assert.Equal(ExecStatus.Finished, exec.Status);
		Assert.Equal(new TilePos(4, 7), sim.Node);
	}

	[Fact]
	public void ADropFromASlopeInItsFeetRow_MinesTheSlope()
	{
		// Measured run S12: 18 of 23 Drop stalls held Down on "Platform, Shaped" cells: slopes of solid blocks (read as
		// platforms so feet can ride them), which no Down drops through. A player mines the slope.
		var g = ArrayTileGrid.Parse(
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..../.....",
			"..........",
			"##########");
		var ctx = new MoveContext(g, PhysicsParams.Default);
		var drop = new PathStep(MoveKind.Drop, new TilePos(4, 6), new TilePos(4, 7), 20f);
		var exec = new PathExecutor(ctx, new[] { drop });
		var sim = new SimWorld(g, PhysicsParams.Default, new TilePos(4, 5));
		sim.State.Y -= 4f;
		sim.State.VY = 1f;
		bool mined = false;
		for (int t = 0; t < 60 && exec.Status == ExecStatus.Running; t++) {
			MoveInput o = exec.Tick(sim.State);
			mined |= o.UseItem && o.Tool == ToolRequest.Pickaxe && o.AimX == 4 && o.AimY == 6;
			sim.Step(o);
		}
		Assert.True(mined, "never swung at the slope");
	}

	[Fact]
	public void InsideABlockThatWillNotGo_PlansFromWhereTheBodyStands()
	{
		// Measured run T0a: the grid had a solid cell in the body's head row at (2574,588) that mining never cleared; every
		// plan from (2574,590) failed "InvalidStart" for the last 83 heartbeats. The body stood there: the grid was wrong.
		string[] rows = {
			"....................",
			"....................",
			"....................",
			"....................",
			"...S..........G.....",
			"####################",
		};
		var planGrid = ArrayTileGrid.Parse(rows);
		for (int x = 2; x <= 4; x++)
			planGrid.Set(x, 2, CellKind.Solid); // what the grid makes of the head cells: no node near to start from either
		var realGrid = ArrayTileGrid.Parse(rows); // what is really there
		var blocks = new TestBlockActions(planGrid);
		var sim = new SimWorld(realGrid, PhysicsParams.Default, planGrid.Markers['S'], new TestBlockActions(realGrid));
		var log = new System.Collections.Generic.List<string>();
		var ctx = new BotContext { Grid = planGrid, Blocks = blocks, Log = new EchoLog(output, log) };
		var skill = new WalkToSkill(planGrid.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < 1200 && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		output.WriteLine($"{status}: {skill.FailureReason} at {sim.Node}");
		Assert.DoesNotContain(log, l => l.Contains("InvalidStart"));
		Assert.Contains(log, l => l.Contains("planning from where the body stands"));
	}

	private sealed class EchoLog : IBotLog
	{
		private readonly ITestOutputHelper o;
		private readonly System.Collections.Generic.List<string> lines;
		public EchoLog(ITestOutputHelper o, System.Collections.Generic.List<string> lines) { this.o = o; this.lines = lines; }
		public void Info(string m) { lines.Add(m); o.WriteLine("INFO  " + m); }
		public void Warn(string m) { lines.Add(m); o.WriteLine("WARN  " + m); }
		public void Error(string m, Exception ex = null) { lines.Add(m); o.WriteLine("ERROR " + m); }
		public void Debug(string m) { lines.Add(m); o.WriteLine("DEBUG " + m); }
	}

	[Fact]
	public void GroundedBesideASlopeInItsRows_PlansFromWhereTheBodyStands()
	{
		// Measured run U1: grounded at (1902,480) with a shaped cell in its body's rows; the grid's body check failed, no node
		// near was startable, and one explore leg failed "InvalidStart" 33 times.
		string[] rows = {
			"....................",
			"....................",
			"....................",
			"....................",
			"...S..........G.....",
			"####################",
		};
		var planGrid = ArrayTileGrid.Parse(rows);
		for (int x = 2; x <= 4; x++)
			planGrid.Set(x, 2, CellKind.Platform | CellKind.Shaped); // slopes in the head row, as the grid reads them
		var realGrid = ArrayTileGrid.Parse(rows);
		var sim = new SimWorld(realGrid, PhysicsParams.Default, planGrid.Markers['S'], new TestBlockActions(realGrid));
		var log = new System.Collections.Generic.List<string>();
		var ctx = new BotContext { Grid = planGrid, Blocks = new TestBlockActions(planGrid), Log = new EchoLog(output, log) };
		var skill = new WalkToSkill(planGrid.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		for (int t = 0; t < 600 && skill.Tick(ctx) == SkillStatus.Running; t++) {
			ctx.Tick = t;
			sim.Step(ctx.Output);
			ctx.Body = sim.State;
		}
		Assert.DoesNotContain(log, l => l.Contains("InvalidStart"));
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

	/// <summary>
	/// A ledge (feet row 4) beside a 2-wide shaft (columns 8-9) 40 rows deep, with <paramref name="plug"/> across the shaft
	/// at row 11 and the floor on the far side level with it: a drop of 6 rows onto the plug and a walk on reaches the goal.
	/// The ground under the shaft at x 2049-2050 in measured runs U4, U6 and U22, where three falls ended at (2049,554).
	/// </summary>
	private static ArrayTileGrid ShaftWithAPlug(char plug)
	{
		const int Width = 24, PlugRow = 11, Bottom = 46;
		var rows = new string[Bottom + 2];
		for (int y = 0; y < rows.Length; y++) {
			var r = new System.Text.StringBuilder();
			for (int x = 0; x < Width; x++) {
				bool ledge = x <= 7 && y >= 5;
				bool shaft = x is 8 or 9;
				bool farSide = x >= 10 && y >= PlugRow;
				r.Append(y >= Bottom ? '#' : shaft && y == PlugRow ? plug : ledge || farSide ? '#' : '.');
			}
			rows[y] = r.ToString();
		}
		rows[4] = rows[4].Remove(2, 1).Insert(2, "S");
		rows[PlugRow - 1] = rows[PlugRow - 1].Remove(15, 1).Insert(15, "G");
		return ArrayTileGrid.Parse(rows);
	}

	/// <summary>
	/// Closed-loop walk that also measures falls as vanilla counts them (Player.fallStart: from the highest point since the
	/// feet left the ground to where they land), in rows. <paramref name="eachTick"/> may change the world as the walk runs.
	/// </summary>
	private (SkillStatus status, WalkToSkill skill, float longestFall) RunMeasuringFalls(ArrayTileGrid g, int maxTicks, Action<WalkToSkill, SimWorld> eachTick = null)
	{
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		var ctx = new BotContext { Grid = g, Log = new TestLog(output) };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		float top = sim.State.Y, longest = 0f;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
			eachTick?.Invoke(skill, sim);
			top = Math.Min(top, sim.State.Y);
			if (sim.State.Grounded) {
				longest = Math.Max(longest, (sim.State.Y - top) / Body.TileSize);
				top = sim.State.Y;
			}
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks at {sim.Node}, replans {skill.Replans}, longest fall {longest:0.0} rows");
		return (status, skill, longest);
	}

	/// <summary>
	/// A cobweb across a shaft is no floor: the planner reads it solid (a block to cut), but the body sinks through it and
	/// falls on. Seen in game (measured run U22): the last drop of a walk landed on webs over the shaft at x 2050, broke
	/// three of them ("trashing a stack of Cobweb" three times) and fell 70 rows to (2050,554), 455 damage.
	/// </summary>
	[Fact]
	public void ADropOntoACobwebOverAShaft_IsNotPlanned_TheWalkNeverFallsFurtherThanIsSafe()
	{
		ArrayTileGrid g = ShaftWithAPlug('*');
		var (status, skill, longest) = RunMeasuringFalls(g, 3000);
		Assert.True(longest <= PhysicsParams.Default.SafeFallTiles, $"fell {longest:0.0} rows (safe: {PhysicsParams.Default.SafeFallTiles})");
		Assert.Equal(SkillStatus.Succeeded, status);
	}

	/// <summary>
	/// The landing of a drop goes (cut, mined, fallen away) while the body walks to the edge: the executor must see it before
	/// stepping off, not fall down the shaft under it. It re-checked a step only before it started, every 15 ticks.
	/// </summary>
	[Fact]
	public void ADropsLandingThatGoesBeforeTheBodyStepsOff_StopsAtTheEdge()
	{
		ArrayTileGrid g = ShaftWithAPlug('#');
		var landing = new TilePos(8, 10);
		int ticksOnTheDrop = 0;
		bool removed = false;
		var (status, _, longest) = RunMeasuringFalls(g, 3000, (skill, sim) => {
			if (removed || skill.Executor?.Current is not PathStep step || step.Kind != MoveKind.Drop || step.To != landing || !sim.State.Grounded)
				return;
			if (++ticksOnTheDrop < 3)
				return;
			g.Set(8, 11, CellKind.Empty); // the plug is gone, three ticks into the drop, the body still on the ledge
			g.Set(9, 11, CellKind.Empty);
			removed = true;
		});
		Assert.True(removed, "the plan never dropped onto the plug");
		Assert.True(longest <= PhysicsParams.Default.SafeFallTiles, $"fell {longest:0.0} rows (safe: {PhysicsParams.Default.SafeFallTiles})");
		Assert.Equal(SkillStatus.Succeeded, status);
	}

	/// <summary>
	/// Closed loop on a goal, with a plan grid and the "real" grid the body moves in. <paramref name="goal"/> gets the
	/// simulator so it can depend on where the body is.
	/// </summary>
	private (SkillStatus status, WalkToSkill skill, SimWorld sim) RunGoal(ArrayTileGrid planGrid, ArrayTileGrid realGrid, Func<SimWorld, IGoal> goal, int maxTicks)
	{
		var sim = new SimWorld(realGrid, PhysicsParams.Default, planGrid.Markers['S']);
		var ctx = new BotContext { Grid = planGrid, Log = new EchoLog(output, new System.Collections.Generic.List<string>()) };
		var skill = new WalkToSkill(goal(sim));
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks at {sim.Node}, plans {skill.Plans}, replans {skill.Replans}: {skill.FailureReason}");
		return (status, skill, sim);
	}

	/// <summary>
	/// Measured run U23 (t10800-16200, 1.5 game hours): mining iron at (2075,358) with a copper pickaxe (tileBoost -1), the
	/// plan ended at (2076,357), whose feet row holds a slope at (2077,357). The body rode up the slope and read (2076,356),
	/// a row out of reach; the executor counted that as the plan's end (SameNode), the walk saw no goal and planned the same
	/// "Drop (2076,356)->(2075,357) | Walk (2075,357)->(2076,357)" again: 418 "Path finished at (2076,356) (Found)" lines,
	/// the ore count at 7/15 for 6 heartbeats. Here the slope is a full block in the world the body moves in (the simulator
	/// steps up onto blocks, not slopes), so the body ends a row up, as in game. The walk must end where the body, read
	/// where it stands, reaches the tile.
	/// </summary>
	[Fact]
	public void AReachGoalBesideASlope_EndsWhereTheBodyAsReadReachesIt_NotInAReplanLoop()
	{
		string[] rows = {
			".......#..",
			".......#..",
			".......#..",
			".......#..",
			"....S./#..",
			"##########",
		};
		var planGrid = ArrayTileGrid.Parse(rows);
		var realGrid = ArrayTileGrid.Parse(rows);
		realGrid.Set(6, 4, CellKind.Solid); // the body rides on top of the slope
		var tile = new TilePos(4, 5); // under the start: not a goal from there (standing on it)
		var reach = new GoalReachTile(tile, PhysicsParams.Default, tileBoost: -1);
		Assert.True(reach.IsGoal(new TilePos(5, 4)));  // beside the slope, as the grid has it
		Assert.False(reach.IsGoal(new TilePos(5, 3))); // where the body standing there is read
		var (status, skill, sim) = RunGoal(planGrid, realGrid, _ => reach, 3000);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(reach.IsGoal(sim.Node), $"ended at {sim.Node}, out of reach of {tile}");
		Assert.True(skill.Plans <= 2, $"{skill.Plans} plans");
	}

	/// <summary>
	/// A plan followed to its end without the goal holding is no success: it counts as a re-plan, so the walk fails instead
	/// of planning for ever (Baritone: a plan that gets nowhere is a failure). Here the goal never holds where the body
	/// stands (a stand-in for any disagreement between a plan's end and the goal; measured run U23 planned 418 times).
	/// </summary>
	[Fact]
	public void PlansThatEndWithoutTheGoal_FailTheWalk_InsteadOfLoopingForEver()
	{
		string[] rows = {
			"....................",
			"....................",
			"....................",
			"....................",
			"..S.................",
			"####################",
		};
		var grid = ArrayTileGrid.Parse(rows);
		var (status, skill, _) = RunGoal(grid, ArrayTileGrid.Parse(rows),
			sim => new GoalReachTile(new TilePos(10, 3), PhysicsParams.Default) { Exclude = n => n == sim.Node }, 20_000);
		Assert.Equal(SkillStatus.Failed, status);
		Assert.True(skill.Plans <= WalkToSkill.MaxReplans + 2, $"{skill.Plans} plans");
	}
}
