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
}
