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

/// <summary>Milestone 7: the boss stage of the planner (summon item, altar, night).</summary>
public class BossPlannerTests
{
	private readonly ITestOutputHelper output;
	public BossPlannerTests(ITestOutputHelper output) => this.output = output;

	private static readonly BossDefeated Eye = new(FakeGame.EyeNpc, FakeGame.SuspiciousEye, new[] { FakeGame.ServantNpc }, "Eye of Cthulhu");

	private ISkill Next(FakeGame g, bool night, out ProgressionPlanner p)
	{
		g.IsNight = night;
		BotContext ctx = g.CreateContext(output);
		p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{(night ? "night" : "day")}: {p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void Lenses_WaitForNight_ThenHuntDemonEyes_WithoutSheltering()
	{
		FakeGame day = ProgressionTests.Mine();
		day.AddAltar(30, 23);
		var wait = Assert.IsType<WaitSkill>(Next(day, night: false, out _));
		Assert.Equal("night", wait.What);
		FakeGame night = ProgressionTests.Mine();
		night.AddAltar(30, 23);
		var hunt = Assert.IsType<HuntSkill>(Next(night, night: true, out _));
		Assert.Equal(FakeGame.DemonEyeAi, hunt.AiStyle);
		Assert.Equal(6, hunt.Amount);
	}

	[Fact]
	public void SummonItem_IsMadeAtAFoundAltar()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddAltar(30, 23);
		g.Items[FakeGame.Lens] = 6;
		ISkill s = Next(g, night: true, out ProgressionPlanner p);
		Assert.True(s is WalkToSkill || s is CraftSkill { ItemType: FakeGame.SuspiciousEye }, p.Status);
		Assert.True(p.KnownStations.ContainsKey(FakeGame.TileDemonAltar));
	}

	[Fact]
	public void NoAltarAnywhere_IsStuck_WithAClearReason()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.Lens] = 6;
		Assert.Null(Next(g, night: true, out ProgressionPlanner p));
		Assert.True(p.Stuck);
		Assert.Contains("nothing can place one", p.Status + p.ResolveForTestStuck);
	}

	[Fact]
	public void WithTheSummonItem_WaitsForNight_ThenFights()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		Assert.IsType<WaitSkill>(Next(g, night: false, out _));
		FakeGame g2 = ProgressionTests.Mine();
		g2.Items[FakeGame.SuspiciousEye] = 1;
		var fight = Assert.IsType<BossFightSkill>(Next(g2, night: true, out _));
		Assert.Equal(FakeGame.EyeNpc, fight.BossType);
	}

	[Fact]
	public void Hurt_RestsBeforeSummoning_OnceOnly()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.Life = ctx.Combat.LifeMax / 2;
		var p = new ProgressionPlanner();
		var rest = Assert.IsType<WaitSkill>(p.ResolveForTest(Eye, ctx));
		Assert.Equal("life", rest.What);
		// A rest that timed out (still hurt) is not started again straight away: summon anyway.
		p.OnSkillFinished(rest, SkillStatus.Failed, ctx);
		Assert.IsType<BossFightSkill>(p.ResolveForTest(Eye, ctx));
	}

	[Fact]
	public void LateInTheNight_WaitsForTheNextOne()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.NightTicksLeft = 60 * 60; // a minute: the Eye would fly off at dawn mid-fight
		var p = new ProgressionPlanner();
		var wait = Assert.IsType<WaitSkill>(p.ResolveForTest(Eye, ctx));
		Assert.Equal("next night", wait.What);
		ctx.NightTicksLeft = 60 * 60 * 8;
		Assert.IsType<BossFightSkill>(p.ResolveForTest(Eye, ctx));
	}

	[Fact]
	public void AfterTheBoss_PrepStagesAreMoot()
	{
		// Seen in game: right after winning, the plan went back to crafting arrows for a boss already dead.
		FakeGame g = ProgressionTests.Mine();
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		Assert.Null(p.ResolveForTest(new PrepFor(FakeGame.EyeNpc, new HaveItem(FakeGame.WoodenArrow, 500, "arrows")), ctx));
		Assert.NotNull(p.ResolveForTest(new HaveItem(FakeGame.WoodenArrow, 500, "arrows"), ctx));
	}

	[Fact]
	public void AlreadyDefeated_IsDone()
	{
		FakeGame g = ProgressionTests.Mine();
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		Assert.Null(Next(g, night: true, out ProgressionPlanner p));
		Assert.False(p.Stuck);
	}
}
