using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Torches in the dark (so a watching player can see), placed between swings, crafted when low.</summary>
public class LightTests
{
	private readonly ITestOutputHelper output;
	public LightTests(ITestOutputHelper output) => this.output = output;

	private (FakeGame g, BotContext ctx, BotBrain brain) Run(float light, int torches, int ticks, int gel = 0)
	{
		FakeGame g = EarlyGameTests.Field(80, 30, 40, 50);
		g.Light = light;
		g.Items[FakeGame.Torch] = torches;
		g.Items[FakeGame.Gel] = gel;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		for (int t = 0; t < ticks; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		return (g, ctx, brain);
	}

	[Fact]
	public void Dark_PlacesATorch_WhileTheSkillGoesOn()
	{
		var (g, _, brain) = Run(light: 0.05f, torches: 20, ticks: 400);
		int placed = g.Furniture.Values.Count(t => t == FakeGame.TileTorch);
		output.WriteLine($"{placed} torch(es), {brain.Light.Placed} counted; {brain.Describe()}");
		Assert.True(placed >= 1);
		Assert.Equal(20 - placed, g.Count(FakeGame.Torch));
		// Spaced out, not one per tick: the bot walks about 3 px/tick, so 400 ticks is some 75 tiles.
		Assert.True(placed <= 75 / (LightKeeper.Spacing - 1) + 1, $"{placed} torches");
	}

	[Fact]
	public void Lit_PlacesNone()
	{
		var (g, _, _) = Run(light: 1f, torches: 5, ticks: 400);
		Assert.Equal(5, g.Count(FakeGame.Torch));
	}

	[Fact]
	public void LowOnTorches_CraftsMoreFromGelAndWood()
	{
		var (g, _, _) = Run(light: 1f, torches: 0, ticks: LightKeeper.CraftEvery + 5, gel: 3);
		Assert.True(g.Count(FakeGame.Torch) > 0);
	}
}
