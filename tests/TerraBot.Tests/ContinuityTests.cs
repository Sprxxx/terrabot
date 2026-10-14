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
