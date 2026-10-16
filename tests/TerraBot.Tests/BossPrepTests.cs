using TerraBot.Bot.Building;
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
	public void ALifeCrystalBesideWater_IsChosen_AndMiningTakesItOn()
	{
		// User report (2026-10-14): the bot ran past a life crystal. The planner chose "mine the life crystal" for one beside
		// water, and mining skipped it (no ore beside liquid), explored away, and was sent back, round and round.
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(16, 27);
		g.Grid.Fill(18, 27, 19, 28, CellKind.Water); // a pool beside it
		BotContext ctx = g.CreateContext(output);
		TilePos heart = Assert.IsType<TilePos>(ProgressionPlanner.SeenLifeCrystal(ctx));
		Assert.True(MineSkill.WouldMine(ctx, ctx.Moves, heart));
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1), 60 * 120);
		output.WriteLine($"{st}, crystals {g.Count(FakeGame.LifeCrystal)}");
		Assert.Equal(SkillStatus.Succeeded, st);
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
	public void ArmouredNights_GoToTheLenses_WhateverStageThePlanIsAt()
	{
		// Measured run S13: all else ready by day 8, then 34 heartbeats waiting for night and 28 hunting, the lenses still
		// short at day 10, while the armoured nights before went to mining.
		FakeGame g = ProgressionTests.Mine();
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 25;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		Assert.False(p.NightHuntDue(ctx)); // not settled yet: no house to come back to
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		ctx.SurfaceY = 25;
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		ISkill night = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {night?.Describe()}");
		var hunt = Assert.IsType<HuntSkill>(night);
		Assert.Equal(FakeGame.DemonEyeAi, hunt.AiStyle);
		Assert.Equal(6, hunt.Amount);
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense - 1; // not armoured: not out at night for them
		Assert.False(p.NightHuntDue(ctx));
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		g.Items[FakeGame.SuspiciousEye] = 1; // the summon in hand: no more lenses
		Assert.False(p.NightHuntDue(ctx));
	}

	[Fact]
	public void AHuntInTown_WalksOutOfIt_OneWay_NotBackAndForth()
	{
		// Measured runs U2/U3: night hunts by the house (the Guide's: vanilla spawns nothing hostile there) failed "no NPC with
		// AI style 2 found after 12 wanders", wandering 30 tiles out and back.
		FakeGame g = EarlyGameTests.Field(400);
		g.IsNight = true;
		var lines = new System.Collections.Generic.List<string>();
		BotContext ctx = g.CreateContext(output);
		var hunt = new HuntSkill(FakeGame.Lens, FakeGame.DemonEyeAi, 6);
		hunt.Start(ctx);
		int startX = ctx.Node.X;
		var xs = new System.Collections.Generic.List<int>();
		for (int t = 0; t < 60 * 60 * 2 && xs.Count < 4; t++) {
			g.Refresh(ctx);
			ctx.Combat.InTown = System.Math.Abs(ctx.Node.X - startX) < 100; // the town's range round the house
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			if (hunt.State == "wandering" && (xs.Count == 0 || xs[^1] != ctx.Node.X / 25))
				xs.Add(ctx.Node.X / 25);
			g.Step(ctx);
		}
		output.WriteLine($"{hunt.Describe()}; went from {startX} to {ctx.Node.X}");
		Assert.True(System.Math.Abs(ctx.Node.X - startX) >= 60, $"stayed within {System.Math.Abs(ctx.Node.X - startX)} tiles of the house");
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
	public void NoAltarFoundAround_TravelsToNewGround_NotStuck()
	{
		// Measured run S17: all else ready at d5 +21h, then "no DemonAltar found after 10 explorations" and auto mode
		// stopped. As for ore that ran out: new ground, farther each time.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Items[FakeGame.Lens] = 6;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = ctx.Node;
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage;
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationExplores; i++)
			Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx));
		ISkill next = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()} (stuck: {p.ResolveForTestStuck})");
		Assert.Null(p.ResolveForTestStuck);
		Assert.True(next is WalkToSkill, p.Status);
		Assert.Contains("new ground", p.Status);
	}

	[Fact]
	public void NoAltarSeen_Explores_ThenIsStuck_WithAClearReason()
	{
		// Only what is revealed counts: none seen means go and look, a few times, before giving up.
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationExplores; i++)
			Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx));
		Assert.Null(p.ResolveForTest(Eye, ctx));
		Assert.Contains("nothing can place one", p.ResolveForTestStuck);
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
	public void WithTheSummonItem_ByDay_GoesToTheArenaFirst_ThenWaitsThere()
	{
		// Measured run S14: the summon made at an altar 360 rows down, the bot waited there all day, set off at nightfall,
		// and the walk to the arena took the night.
		FakeGame g = EarlyGameTests.Field(160, 50, 75);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.ResolveForTest(new ArenaBuilt("arena"), ctx); // picks the site
		Assert.NotNull(p.ArenaSite);
		g.Items[FakeGame.SuspiciousEye] = 1;
		ISkill day = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {day?.Describe()}");
		Assert.IsType<WalkToSkill>(day);
		Assert.Contains("walking to the arena", p.Status);
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
	public void Arrows_AreNotToppedUpAfterEveryShot()
	{
		// The boss scenario: each arrow shot at a demon eye topped the 500 up again, until the wood ran out.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var arrows = new HaveItem(FakeGame.WoodenArrow, 500, "arrows") { RefillBelow = 400 };
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.NotNull(p.ResolveForTest(arrows, ctx)); // never had the 500 yet
		g.Items[FakeGame.WoodenArrow] = 500;
		Assert.Null(p.ResolveForTest(arrows, ctx));
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.Null(p.ResolveForTest(arrows, ctx)); // a few shot: still enough
		g.Items[FakeGame.WoodenArrow] = 399;
		Assert.NotNull(p.ResolveForTest(arrows, ctx)); // running low: back up to 500
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.NotNull(p.ResolveForTest(arrows, ctx));
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
