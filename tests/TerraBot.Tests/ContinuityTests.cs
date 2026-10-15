using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Work paused by a reflex resumes as the same job (AltoClef's interrupt-and-resume), instead of a fresh skill starting
/// over. Seen in game (world_eye runs 59-73): every fight threw the job away, and its state had to be moved out of the
/// skills, one patch at a time, to survive.
/// </summary>
public class ContinuityTests
{
	private readonly ITestOutputHelper output;
	public ContinuityTests(ITestOutputHelper output) => this.output = output;

	/// <summary>A brain in auto mode on the mining fixture, ticked until its work is a mining job.</summary>
	private (FakeGame g, BotContext ctx, BotBrain brain, MineSkill mine) Mining()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Iron);
		for (int t = 0; t < 600 && brain.Work is not MineSkill; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(brain.Describe());
		return (g, ctx, brain, Assert.IsType<MineSkill>(brain.Work));
	}

	[Fact]
	public void AMiningJobPausedByAFight_ResumesAsTheSameInstance()
	{
		var (g, ctx, brain, mine) = Mining();
		g.AddSlime(9, ctx.Node.X + 4, ctx.Node.Y);
		bool fought = false;
		for (int t = 0; t < 60 * 30 && !(fought && brain.Override == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
			if (brain.Override is FightSkill) {
				fought = true;
				Assert.Same(mine, brain.Work); // paused, not dropped
			}
		}
		output.WriteLine(brain.Describe());
		Assert.True(fought);
		Assert.Null(brain.Override);
		Assert.Same(mine, brain.Work); // the same job goes on
	}

	[Fact]
	public void AWalkHeldInTheAir_FailsInsteadOfWaitingForever()
	{
		// The first measured run with resuming work: "waiting to land" at one spot for three game days. Restarts used to
		// break it; with the job resumed, nothing did.
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		var walk = new WalkToSkill(new TilePos(ctx.Node.X + 20, ctx.Node.Y));
		walk.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < WalkToSkill.MaxAirborneTicks + 10 && s == SkillStatus.Running; t++) {
			var body = ctx.Body;
			body.VY = 0.3f; // held in the air (a cobweb), never landing
			ctx.Body = body;
			s = walk.Tick(ctx);
		}
		output.WriteLine(walk.FailureReason);
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("never landed", walk.FailureReason);
	}

	[Fact]
	public void BobbingInWaterOverASlope_CountsAsLanded()
	{
		// world_eye (measured run B): "never landed ... (vy -0.2, feet Water/Water, under Platform, Shaped/Solid)": standing
		// in water on a slope, the body never comes to rest at vy 0.
		var g = new FakeGame(new[] {
			"..........",
			"..........",
			"..~~~~~~..",
			"..~~~~~~..",
			"..~~S~~~..",
			"##########" });
		BotContext ctx = g.CreateContext(output);
		TilePos start = ctx.Node;
		var walk = new WalkToSkill(new TilePos(start.X + 3, start.Y));
		walk.Start(ctx);
		bool planned = false;
		for (int t = 0; t < WalkToSkill.MaxAirborneTicks && !planned; t++) {
			var body = ctx.Body;
			body.VY = -0.2f;
			ctx.Body = body;
			walk.Tick(ctx);
			planned = walk.State != "waiting to land";
		}
		output.WriteLine(walk.Describe());
		Assert.True(planned);
	}

	[Fact]
	public void AfloatInDeepWater_SwimsTowardsTheTarget()
	{
		// Measured run D: "never landed ... under Water/Water", every walk failing from the middle of a pool.
		var g = new FakeGame(new[] {
			"....................",
			"..~~~~~~~~~~~~~~....",
			"..~~~~S~~~~~~~~~....",
			"..~~~~~~~~~~~~~~....",
			"..~~~~~~~~~~~~~~....",
			"##~~~~~~~~~~~~~~####",
			"####################" });
		BotContext ctx = g.CreateContext(output);
		TilePos start = ctx.Node;
		var walk = new WalkToSkill(new TilePos(start.X + 12, start.Y));
		walk.Start(ctx);
		var body = ctx.Body;
		body.VY = -0.2f; // afloat, not landing
		ctx.Body = body;
		walk.Tick(ctx);
		output.WriteLine(walk.Describe());
		Assert.Contains("swimming", walk.Describe());
		Assert.Equal(1, ctx.Output.Horizontal); // towards the target
	}

	[Fact]
	public void BoxedInByOldStepBans_TheBansAreForgiven_AndTheWalkGoes()
	{
		// Measured run G1: "no path ... expanded 1": every step from the spot had been banned earlier, five rests followed
		// and auto mode stopped. A ban is a guess about a step, not a fact for ever.
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		TilePos n = ctx.Node;
		for (int dx = -4; dx <= 4; dx++)
			for (int dy = -4; dy <= 4; dy++)
				if (dx != 0 || dy != 0)
					ctx.BadEdges.Add((n, new TilePos(n.X + dx, n.Y + dy)));
		var walk = new WalkToSkill(new TilePos(n.X + 15, n.Y));
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, walk, 60 * 20);
		output.WriteLine($"{st}: {walk.Describe()}");
		Assert.Equal(SkillStatus.Succeeded, st);
	}

	/// <summary>A job that never gets anywhere: stands still, always the same state.</summary>
	private sealed class NoProgressSkill : ISkill
	{
		public string Name => "Stuck";
		public string Describe() => "Stuck [same as ever]";
		public void Start(BotContext ctx) { }
		public SkillStatus Tick(BotContext ctx) => SkillStatus.Running;
		public void Stop(BotContext ctx) { }
	}

	[Fact]
	public void WorkWithNoProgressForGameHours_IsGivenUp()
	{
		// AltoClef's progress checkers: with restarts gone, a hang has to be caught for what it is.
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.StartSkill(new NoProgressSkill(), ctx);
		for (int t = 0; t <= BotBrain.NoProgressTicks + 60 && brain.Work != null; t++) {
			ctx.Tick = t;
			brain.Tick(ctx);
		}
		output.WriteLine(brain.LastResult);
		Assert.Null(brain.Work);
		Assert.Contains("no progress", brain.LastResult);
	}

	[Fact]
	public void SkillKeys_AreTheSameForTheSameJob_AndDifferForAnother()
	{
		Assert.Equal(new MineSkill(11, 6, 24).Key, new MineSkill(11, 6, 24).Key);
		Assert.NotEqual(new MineSkill(11, 6, 24).Key, new MineSkill(11, 6, 30).Key);
		Assert.NotEqual(new MineSkill(11, 6, 24).Key, new MineSkill(14, 9, 24).Key);
		Assert.Equal(new WalkToSkill(new TilePos(5, 7)).Key, new WalkToSkill(new TilePos(5, 7)).Key);
		Assert.NotEqual(new WalkToSkill(new TilePos(5, 7)).Key, new WalkToSkill(new TilePos(5, 7)) { Travel = true }.Key);
		Assert.Null(((ISkill)new FightSkill()).Key); // reflexes are never "the same job"
	}

	[Fact]
	public void APausedWalk_GoesOnWithItsPlan_WithoutPlanningAgain()
	{
		// Measured run N1: 3,668 walk starts in a run, every fight planning the whole way again (about 75 ticks standing).
		FakeGame game = EarlyGameTests.Field(60);
		BotContext ctx = game.CreateContext(null);
		var walk = new WalkToSkill(new TilePos(50, 24));
		walk.Start(ctx);
		for (int t = 0; t < 40; t++) {
			game.Refresh(ctx);
			Assert.Equal(SkillStatus.Running, walk.Tick(ctx));
			game.Step(ctx);
		}
		walk.Pause(ctx);
		for (int t = 0; t < 30; t++) { // a reflex runs; the bot stands
			game.Refresh(ctx);
			ctx.Output = Core.Physics.MoveInput.None;
			game.Step(ctx);
		}
		walk.Resume(ctx);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(game, ctx, walk, 600));
		Assert.Equal(1, walk.Plans);
	}

	[Fact]
	public void ALongWalkInSegments_CarriesStraightOn_FromOneToTheNext()
	{
		// Every segment's end stopped the bot for the next segment's search (Baritone plans the next one while walking).
		FakeGame game = EarlyGameTests.Field(160);
		BotContext ctx = game.CreateContext(null);
		var walk = new WalkToSkill(new TilePos(150, 24)) { Limits = new Core.Pathfinding.SearchLimits { SegmentAfter = 40, SegmentMinTiles = 12 } };
		int still = 0, moving = 0;
		walk.Start(ctx);
		SkillStatus st = SkillStatus.Running;
		for (int t = 0; t < 2000 && st == SkillStatus.Running; t++) {
			game.Refresh(ctx);
			st = walk.Tick(ctx);
			if (t > 5)
				_ = ctx.Output.Horizontal == 0 ? still++ : moving++;
			game.Step(ctx);
		}
		output.WriteLine($"{st}: plans {walk.Plans}, splices {walk.Splices}, ticks moving {moving}, standing {still}");
		Assert.Equal(SkillStatus.Succeeded, st);
		Assert.True(walk.Plans >= 3, "the walk should come in segments");
		Assert.Equal(walk.Plans - 1, walk.Splices);
	}

	[Fact]
	public void AfloatAgainstAWallOnTheTargetsSide_SwimsForTheShoreBehind()
	{
		// Measured run S0: afloat with a wall on the target's side, every walk swam into the wall for 1,200 ticks and failed
		// until auto mode stopped (four game days lost). A player swims for the shore.
		var g = new FakeGame(new[] {
			"..............######",
			"..............######",
			"..~~~~~~~~~~~~######",
			"..~~~~~~S~~~~~######",
			"##~~~~~~~~~~~~######",
			"##~~~~~~~~~~~~######",
			"####################" });
		BotContext ctx = g.CreateContext(output);
		var walk = new WalkToSkill(new TilePos(18, 0)); // beyond the wall
		walk.Start(ctx);
		var body = ctx.Body;
		body.VY = -0.2f;
		ctx.Body = body;
		walk.Tick(ctx);
		output.WriteLine(walk.Describe());
		Assert.Contains("swimming", walk.Describe());
		Assert.Equal(-1, ctx.Output.Horizontal); // to the dry shore on the left, not into the wall
	}

	[Fact]
	public void InTheSamePlaceForHours_WhateverTheJobs_TheBotActs()
	{
		// Measured run S2: afloat in a pocket for 490,000 ticks while fights, loot and mining took turns (the work watchdog
		// never fired). After three game hours in one place the bot drinks a recall potion or climbs out.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 20; y++)
			rows.Add(new string('@', 30));
		rows[10] = new string('@', 10) + "....." + new string('@', 15);
		rows[11] = new string('@', 10) + "....." + new string('@', 15);
		rows[12] = new string('@', 10) + "..S.." + new string('@', 15);
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		var brain = new Bot.Brain.BotBrain();
		brain.SetAuto(true, ctx, Bot.Brain.ProgressionPlanner.Plans.Early); // wants wood: no tree in here
		for (int t = 0; t < Bot.Brain.BotBrain.SamePlaceTicks + 600 && brain.Auto; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("in the same place"));
	}
}
