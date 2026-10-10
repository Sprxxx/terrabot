using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 7 preparation in <see cref="FakeGame"/>: life crystals.</summary>
public class BossPrepTests
{
	private readonly ITestOutputHelper output;
	public BossPrepTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void MineSkill_BreaksLifeCrystals_InStonePockets()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(40, 29);
		g.AddHeart(52, 31);
		BotContext ctx = g.CreateContext(output);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 2), 60 * 120);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, crystals {g.Count(FakeGame.LifeCrystal)}, out of range {g.Sim.OutOfRange}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(2, g.Count(FakeGame.LifeCrystal));
	}

	[Fact]
	public void UseItem_ConsumesALifeCrystal()
	{
		FakeGame g = EarlyGameTests.Field(30);
		g.Items[FakeGame.LifeCrystal] = 2;
		BotContext ctx = g.CreateContext(output);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new UseItemSkill(FakeGame.LifeCrystal), 200));
		Assert.Equal(1, g.Count(FakeGame.LifeCrystal));
		Assert.Equal(120, g.LifeMax);
	}

	[Fact]
	public void Planner_UsesCarriedCrystals_BeforeMiningMore()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.LifeCrystal] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		ISkill next = Resolve(p, ctx, new LifeAtLeast(200, "max life"));
		Assert.IsType<UseItemSkill>(next);

		g.Items[FakeGame.LifeCrystal] = 0;
		var mine = Assert.IsType<MineSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Equal(FakeGame.TileHeart, mine.TileType);
	}

	private ISkill Resolve(ProgressionPlanner p, BotContext ctx, Need need)
	{
		ISkill s = p.ResolveForTest(need, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}
}
