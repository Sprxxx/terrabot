using TerraBot.Bot;
using TerraBot.Bot.Brain;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>The user (2026-10-18): a box with the long-term goal beside the fast-changing status ("preparing the arena").</summary>
public class GoalTests
{
	private readonly ITestOutputHelper output;
	public GoalTests(ITestOutputHelper output) => this.output = output;

	[Theory]
	[InlineData("boss:WallOfFlesh", "Wall of Flesh")]
	[InlineData("boss:EyeOfCthulhu", "Eye of Cthulhu")]
	[InlineData("access:underworld", "Underworld access")]
	public void ErrandNames_ReadAsPlainWords(string id, string name) => Assert.Equal(name, BotBrain.ErrandName(id));

	[Fact]
	public void TheGoal_IsTheStageWorkedOn_AndHoldsWhileItLasts()
	{
		FakeGame g = EarlyGameTests.Field(60, 30, 40);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		string first = null;
		int changes = 0;
		for (int t = 0; t < 600; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
			string goal = brain.Goal;
			if (first == null && !string.IsNullOrEmpty(goal))
				first = goal;
			else if (first != null && goal != first) {
				changes++;
				first = goal;
			}
		}
		output.WriteLine($"goal {brain.Goal}, {changes} changes in 600 ticks; status {brain.Planner.Status}");
		Assert.False(string.IsNullOrEmpty(brain.Goal));
		Assert.True(changes <= 3, $"the goal changed {changes} times in 10 s");
	}
}
