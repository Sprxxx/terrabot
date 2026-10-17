using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The day before a night-only stage (the Eye's lenses from demon eyes, the Eye's summon): spent on the plan's later work,
/// not waiting for dark. Measured runs U31 and U32 (world_eye): 49 and 70 heartbeats of "Wait for night", in U32 from t518400
/// to t568800 for the sixth lens, every other stage of the plan met.
/// </summary>
public class NightWaitTests
{
	private readonly ITestOutputHelper output;
	public NightWaitTests(ITestOutputHelper output) => this.output = output;

	private const int Feet = 44;

	/// <summary>
	/// Every stage of the boss plan before the Eye met, by day, in the M10-like corrupt world: stations, a house, iron gear worn
	/// (defense 9), the bow and the Eye's 500 arrows, max life 200, the arena, and a demon altar seen.
	/// </summary>
	private (FakeGame, BotContext, ProgressionPlanner) TheEyeNext(string plan = ProgressionPlanner.Plans.Boss, BotBrain brain = null)
	{
		FakeGame g = EvilBossTests.CorruptWorld();
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		ctx.SurfaceY = 50;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Stations[FakeGame.TileBench] = new TilePos(5, Feet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, Feet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, Feet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow })
			g.Items[item] = 1;
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BossArrows;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.BossLife;
		ProgressionPlanner p;
		if (brain != null) {
			brain.SetAuto(true, ctx, plan);
			p = brain.Planner;
		}
		else {
			p = new ProgressionPlanner();
			p.Reset(plan);
		}
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		// The arena: built (the planner's own site and blueprint, run in the fake).
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
		var build = Assert.IsType<BuildSkill>(Next(p, ctx));
		Assert.False(build.Blueprint.IsHouse);
		SkillStatus built = EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 4);
		Assert.Equal(SkillStatus.Succeeded, built);
		p.OnSkillFinished(build, built, ctx);
		build.Stop(ctx);
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BossArrows;
		// The summon's altar seen too (by day the summon's station is looked for first: ProgressionPlanner.StationsByDay).
		g.AddAltar(150, Feet - 1); // past the house and the arena
		g.Refresh(ctx);
		return (g, ctx, p);
	}

	private ISkill Next(ProgressionPlanner p, BotContext ctx)
	{
		ISkill s = p.Next(ctx);
		output.WriteLine($"{(ctx.IsNight ? "night" : "day")}: {p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void ByDay_TheLensWait_GoesToTheNextMilestonesPrep_AndAtNightTheLensHuntIsFirstAgain()
	{
		var (g, ctx, p) = TheEyeNext();
		g.Items[FakeGame.Lens] = 5; // one short, as in U32 from t518400
		ISkill day = Next(p, ctx);
		// Not the wait: the worm's arrows (its 1000 against the 500 in the bag), wood for them first.
		Assert.IsNotType<WaitSkill>(day);
		var wood = Assert.IsType<GatherWoodSkill>(day);
		Assert.Equal(FakeGame.Wood, wood.ItemType);
		Assert.True(p.DayWorkBeforeNight);
		Assert.Contains("waiting for night to hunt", p.Status);
		Assert.False(ctx.EvilErrand);

		// Nightfall: the night-only stage first again, the plan's order.
		g.IsNight = true;
		g.Refresh(ctx);
		var hunt = Assert.IsType<HuntSkill>(Next(p, ctx));
		Assert.Equal((FakeGame.Lens, FakeGame.DemonEyeAi), (hunt.ItemType, hunt.AiStyle));
		Assert.False(p.DayWorkBeforeNight);
	}

	[Fact]
	public void ByDay_TheSummonInHand_TheDayGoesToTheNextMilestonesPrep_AndAtNightToTheArenaAndTheFight()
	{
		// Measured run U31: the Suspicious Looking Eye made at t578160, then "waiting for night to summon the Eye of Cthulhu"
		// from t603000 to t655800, every other stage met.
		var (g, ctx, p) = TheEyeNext();
		g.Items[FakeGame.SuspiciousEye] = 1;
		ISkill day = Next(p, ctx);
		Assert.IsType<GatherWoodSkill>(day);
		Assert.Contains("summon the Eye of Cthulhu", p.Status);
		Assert.True(p.DayWorkBeforeNight);

		g.IsNight = true;
		g.Refresh(ctx);
		ISkill night = Next(p, ctx);
		Assert.True(night is BossFightSkill || night is WalkToSkill && p.Status.Contains("walking to the arena to summon"), p.Status);
		Assert.False(p.DayWorkBeforeNight);
	}

	[Fact]
	public void ByDay_WithTheLaterWorkDone_TheNightIsWaitedFor_AsBefore()
	{
		// The worm already down: its prep is moot, and the plan has nothing after the Eye: the wait (by the arena), as before.
		var (g, ctx, p) = TheEyeNext();
		g.BossesDefeated.Add(FakeGame.EowHead);
		g.Items[FakeGame.Lens] = 5;
		var wait = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("night", wait.What);
		Assert.False(p.DayWorkBeforeNight);
		Assert.False(p.Stuck);
	}

	[Fact]
	public void ByDay_TheEvilBossPlan_SpendsTheEyesWaitOnItsOwnLaterStages()
	{
		// The plan's own later stages (the worm's prep), in their order, before the next milestone's.
		var (g, ctx, p) = TheEyeNext(ProgressionPlanner.Plans.EvilBoss);
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.WormArrows; // its arrows in
		g.Items[FakeGame.Lens] = 5;
		ISkill day = Next(p, ctx);
		// The rotten chunks, hunted in the corruption by day (the demon bow and unholy arrows: nothing in the bag for them).
		var hunt = Assert.IsType<HuntSkill>(day);
		Assert.Equal(FakeGame.RottenChunk, hunt.ItemType);
		Assert.True(ctx.EvilErrand);
		Assert.True(p.DayWorkBeforeNight);
	}

	[Fact]
	public void AtNightfall_TheDaysWork_IsPlannedAgain_AndTheNightOnlyStageGoesFirst()
	{
		// Armoured, the summon in hand: the nightfall re-plan was only for the unarmoured or a lens hunt due, so the day's work
		// would have gone on into the night, the Eye not summoned.
		var brain = new BotBrain();
		var (g, ctx, p) = TheEyeNext(brain: brain);
		g.Items[FakeGame.SuspiciousEye] = 1;
		for (int t = 0; t < 120; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"day: {brain.Describe()} | {p.Status}");
		Assert.IsType<GatherWoodSkill>(brain.Work);
		Assert.True(p.DayWorkBeforeNight);
		g.IsNight = true;
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"night: {brain.Describe()} | {p.Status}");
		Assert.True(brain.Work is BossFightSkill || brain.Work is WalkToSkill && p.Status.Contains("walking to the arena to summon"), p.Status);
	}

	[Fact]
	public void AtDaybreak_TheNightsLensHunt_IsPlannedAgain_AndTheDayGoesToTheDaysWork()
	{
		var brain = new BotBrain();
		var (g, ctx, p) = TheEyeNext(brain: brain);
		g.Items[FakeGame.Lens] = 5;
		g.IsNight = true;
		for (int t = 0; t < 120; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"night: {brain.Describe()} | {p.Status}");
		Assert.IsType<HuntSkill>(brain.Work);
		g.IsNight = false;
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"day: {brain.Describe()} | {p.Status}");
		Assert.IsType<GatherWoodSkill>(brain.Work);
		Assert.True(p.DayWorkBeforeNight);
	}
}
