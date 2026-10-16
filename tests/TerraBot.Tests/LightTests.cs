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
	public void Dark_WithTwelveTorches_StillLightsTheWay()
	{
		// User report (2026-10-14): walking the caves in the dark with exactly 12 torches and 15 wood, none placed: 12 were
		// all kept for crafting, and 15 wood was all kept back from making more.
		var (g, _, brain) = Run(light: 0.05f, torches: 12, ticks: 400);
		int placed = g.Furniture.Values.Count(t => t == FakeGame.TileTorch);
		output.WriteLine($"{placed} torch(es); {brain.Describe()}");
		Assert.True(placed >= 1);
	}

	[Fact]
	public void LowOnTorches_CraftsMoreFromTheLastFifteenWood()
	{
		var (g, _, _) = Run(light: 1f, torches: 0, ticks: LightKeeper.CraftEvery + 5, gel: 3, wood: 15);
		Assert.True(g.Count(FakeGame.Torch) > 0);
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
		Assert.True(surface.Describe().Contains("air") || surface.Describe().Contains("pillaring up"), surface.Describe()); // walking to a dry spot, or straight up
	}

	/// <summary>
	/// A flooded shaft <paramref name="width"/> wide from column 4 (unbreakable walls), the bot at its bottom at node (4,12),
	/// head in row 10; air from row 2 up. <paramref name="row9"/> replaces the shaft's cells in row 9, just over the head.
	/// </summary>
	private static FakeGame Shaft(int width, string row9)
	{
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 3; y++)
			rows.Add(new string('.', 14));
		for (int y = 3; y <= 12; y++)
			rows.Add("@@@@" + (y == 9 ? row9 : new string('~', width)) + new string('@', 10 - width));
		rows[12] = "@@@@S" + new string('~', width - 1) + new string('@', 10 - width);
		rows.Add(new string('@', 14));
		return new FakeGame(rows.ToArray());
	}

	[Fact]
	public void ASlopeOverTheHead_IsMinedOnTheWayUp_NotTakenForOpenWater()
	{
		// Measured run U6: a slope over the head (and a half block over the next column) read as open water: the column
		// search went on up through it and the mining never looked at it (only solid tiles), so the bot jumped into it,
		// the jump ended there (Collision.up), and it drowned pillaring in place.
		FakeGame g = Shaft(2, "~/");
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.Breath = 100;
		var surface = new Bot.Skills.SurfaceSkill();
		surface.Start(ctx);
		surface.Tick(ctx);
		output.WriteLine(surface.Describe() + " " + ctx.Output);
		Assert.Equal("Surface [mining up through (5,9)]", surface.Describe());
		Assert.Equal(Core.Physics.ToolRequest.Pickaxe, ctx.Output.Tool);
		Assert.False(ctx.Output.Jump);
	}

	[Fact]
	public void OffCentreUnderABlock_TheBotSteersOverItsColumn_BeforeItJumps()
	{
		// Measured runs U6/U8: the node on the way-up column, the 20 px hitbox 0.3-1.5 px into the next column, a block over
		// that one: every jump hit it and ended at once; the bot jumped in place until it drowned (12 of 26 drownings in
		// U1-U8 had the hitbox across three columns).
		FakeGame g = Shaft(3, "~~@");
		Core.Physics.PhysicsParams p = Core.Physics.PhysicsParams.Default;
		g.Sim.State.X = Core.Pathfinding.Body.CanonicalLeft(4, p) + Core.Pathfinding.Body.CenterOffset(p) + 1f; // 1 px into column 6
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.Breath = 100;
		Assert.Equal(4, ctx.Node.X);
		var surface = new Bot.Skills.SurfaceSkill();
		surface.Start(ctx);
		bool jumped = false;
		for (int t = 0; t < 120 && !jumped; t++) {
			g.Refresh(ctx);
			surface.Tick(ctx);
			if (ctx.Output.Jump) {
				jumped = true;
				Assert.False(Bot.Skills.SurfaceSkill.OffColumns(ctx.Body.X, 4, p), $"jumped at x {ctx.Body.X} (tick {t}), into the block over column 6");
			}
			g.Step(ctx);
		}
		output.WriteLine($"{surface.Describe()} at x {ctx.Body.X}");
		Assert.True(jumped, "never jumped");
	}
}
