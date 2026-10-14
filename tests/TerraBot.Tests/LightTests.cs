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

	private (FakeGame g, BotContext ctx, BotBrain brain) Run(float light, int torches, int ticks, int gel = 0, int wood = 0)
	{
		FakeGame g = EarlyGameTests.Field(80, 30, 40, 50);
		g.Light = light;
		g.Items[FakeGame.Torch] = torches;
		g.Items[FakeGame.Gel] = gel;
		g.Items[FakeGame.Wood] = wood;
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
		var (g, _, _) = Run(light: 1f, torches: 0, ticks: LightKeeper.CraftEvery + 5, gel: 3, wood: LightKeeper.SpareWood + 5);
		Assert.True(g.Count(FakeGame.Torch) > 0);
	}
}

/// <summary>Out of breath under water: surface before anything else (drowning skips the hurt event).</summary>
public class BreathTests
{
	private readonly ITestOutputHelper output;
	public BreathTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void LowBreathUnderWater_Surfaces_AndGetsItsHeadOut()
	{
		// A pool 6 deep with the bot at the bottom; air above it.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 18; y++)
			rows.Add(new string('.', 40));
		for (int y = 0; y < 6; y++)
			rows.Add("##########" + new string('~', 20) + "##########");
		rows[23] = "##########" + "~~~~S" + new string('~', 15) + "##########";
		rows.Add(new string('#', 40));
		FakeGame g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		var brain = new Bot.Brain.BotBrain();
		brain.SetAuto(true, ctx, Bot.Brain.ProgressionPlanner.Plans.Early);
		g.Refresh(ctx);
		ctx.Combat.Breath = 100; // below three quarters
		brain.Tick(ctx);
		output.WriteLine(brain.Describe());
		var surface = Assert.IsType<Bot.Skills.SurfaceSkill>(brain.Current);
		Assert.Contains("air", surface.Describe()); // walking to the nearest dry spot (or pillaring up)
	}
}
