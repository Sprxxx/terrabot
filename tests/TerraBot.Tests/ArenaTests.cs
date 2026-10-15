using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 8: the boss arena (flat floor, open air, torches, campfire) and the heart lantern in the house.</summary>
public class ArenaTests
{
	private readonly ITestOutputHelper output;
	public ArenaTests(ITestOutputHelper output) => this.output = output;

	/// <summary>A field 140 wide: ground row 25 (feet row 24), a dirt mound, a pit and two trees inside where the arena goes.</summary>
	private static FakeGame RoughField()
	{
		FakeGame g = EarlyGameTests.Field(140, 50, 75);
		for (int x = 40; x <= 44; x++)          // a mound 2 high
			for (int y = 23; y <= 24; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		for (int x = 60; x <= 63; x++)          // a pit 2 deep
			for (int y = 25; y <= 26; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		return g;
	}

	private static void GiveArenaKit(FakeGame g)
	{
		g.Items[FakeGame.DirtBlock] = 30;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
	}

	[Fact]
	public void Arena_Shape()
	{
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		Assert.Equal(Blueprint.ArenaWidth, bp.Width);
		Assert.Equal(Blueprint.ArenaClearance + 1, bp.Height);
		Assert.All(Enumerable.Range(0, bp.Width), x => Assert.Equal(PlanCell.Block, bp.At(x, bp.Height - 1)));
		Assert.All(Enumerable.Range(0, bp.Width), x => Assert.Equal(PlanCell.Clear, bp.At(x, 0)));
		PlanItem fire = Assert.Single(bp.Items, i => i.ItemType == FakeGame.Campfire);
		Assert.Equal(bp.Width / 2 - 1, fire.X);
		Assert.Equal(bp.Height - 3, fire.Y); // 2 tall, standing on the floor
		int torches = Enumerable.Range(0, bp.Width).Count(x => x % Blueprint.ArenaTorchSpacing == Blueprint.ArenaTorchSpacing / 2 && System.Math.Abs(x - bp.Width / 2) > 2);
		Assert.Equal(torches, bp.Items.Count(i => i.ItemType == FakeGame.Torch)); // every 12 tiles, none beside the campfire
		Assert.False(bp.IsHouse);
		Assert.Equal(bp.Height - 2, bp.StandRow);
	}

	[Fact]
	public void SiteFinder_Arena_AcceptsTreesBumpsAndPits_ButNotTheHouse()
	{
		FakeGame g = RoughField();
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var house = (new TilePos(0, 15), new TilePos(30, 30));
		TilePos? site = SiteFinder.Find(ctx, bp, new TilePos(70, 24), keepOut: new[] { house });
		Assert.NotNull(site);
		output.WriteLine($"site {site}, {SiteFinder.Edits(ctx, bp, site.Value)} edits");
		Assert.Equal(25, site.Value.Y + bp.Height - 1); // floor on the ground row
		Assert.True(site.Value.X > 30, "overlaps the house");
		// Trees rule a house site out but not an arena: they get felled.
		Assert.True(SiteFinder.Edits(ctx, bp, new TilePos(40, 25 - bp.Height + 1)) > 0);
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(45, 25 - 5)));
	}

	[Fact]
	public void SiteFinder_Arena_SlopesOfTheGround_AreBlocks_NotPlatforms()
	{
		// Measured run S10: slopes and half blocks of the ground read as platforms ruled out 122 of 161 arena sites round
		// the house ("a platform in the room"), and the boss plan stopped at max life 200 with the bow made.
		FakeGame g = EarlyGameTests.Field(140, 50, 75);
		for (int x = 2; x < 130; x += 9) {
			g.Grid.Set(x, 24, CellKind.Platform | CellKind.Shaped); // a slope on the ground, in the room
			g.Grid.Set(x + 4, 25, CellKind.Platform | CellKind.Shaped); // a slope in the floor row
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, bp, new TilePos(70, 24));
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, new TilePos(70, 24))}");
		Assert.NotNull(site);
		Assert.Equal(25, site.Value.Y + bp.Height - 1);
		Assert.True(SiteFinder.Edits(ctx, bp, site.Value) > 0); // the slopes in the room get mined
	}

	[Fact]
	public void BuildSkill_Arena_FellsTrees_LevelsTheGround_AndPlacesTheCampfire()
	{
		FakeGame g = RoughField();
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(35, 25 - bp.Height + 1); // over the mound (40-44), trees (50, 75) and pit (60-63)
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks; placed {g.Sim.Placed}, broken {g.Sim.Broken}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site));
		Assert.DoesNotContain(g.Trees.Keys, t => t.X >= site.X && t.X < site.X + bp.Width);
		Assert.Equal(FakeGame.TileCampfire, g.TileTypeAt(site.X + bp.Width / 2, site.Y + bp.StandRow));
		Assert.Null(ctx.Reserved);
	}

	[Fact]
	public void ArenaPlan_CraftsTorchesAndTheCampfire_ThenBuilds()
	{
		FakeGame g = RoughField();
		g.Items[FakeGame.Wood] = 30;
		g.Items[FakeGame.Gel] = 6;
		g.Items[FakeGame.DirtBlock] = 30;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Arena);
		int t = 0;
		for (; t < 60 * 60 * 10 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | {brain.Planner.Status}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status + " / " + brain.LastResult);
		TilePos site = brain.Planner.ArenaSite.Value;
		Assert.True(ProgressionPlanner.ArenaReady(ctx, Blueprint.Arena(FakeGame.Ids), site));
	}

	[Fact]
	public void HeartLantern_IsHungInTheBuiltHouse()
	{
		// The house first (as in the settle plan), then the boss prep adds the lantern under its roof.
		FakeGame g = EarlyGameTests.Field(60);
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
		g.Items[FakeGame.HeartLantern] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		Blueprint house = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, house, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(house, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(house, site), SkillStatus.Succeeded, ctx);

		var need = new HouseBuilt("heart lantern", HeartLantern: true);
		var build = Assert.IsType<BuildSkill>(p.ResolveForTest(need, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, build, 60 * 60));
		Assert.Equal(FakeGame.TileLantern, g.TileTypeAt(site.X + 2, site.Y + 1));
		Assert.Null(g.CheckHousing(site.X + house.Inside.X, site.Y + house.Inside.Y));
		Assert.Null(p.ResolveForTest(need, ctx));
	}

	[Fact]
	public void HeartLantern_IsCraftedFromACrystalAndChains()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.LifeCrystal] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var need = new HaveItem(FakeGame.HeartLantern, 1, "heart lantern");
		ISkill s = p.ResolveForTest(need, ctx);
		output.WriteLine($"status [{p.Status}] stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}");
		Assert.IsType<MineSkill>(s); // iron ore for the bar for the chains
		g.Items[FakeGame.Chain] = 4;
		Assert.IsType<CraftSkill>(p.ResolveForTest(need, ctx));
	}

	[Fact]
	public void BossPlan_BuildsTheArena_AndSummonsInIt()
	{
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var stages = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids);
		int arena = stages.ToList().FindIndex(n => n is PrepFor { Inner: ArenaBuilt });
		int boss = stages.ToList().FindIndex(n => n is BossDefeated);
		Assert.True(arena >= 0 && arena < boss);
		Assert.Contains(stages, n => n is PrepFor { Inner: HouseBuilt { HeartLantern: true } });
		Assert.True(bp.Width / 2 - BossFightSkill.ArenaSoftMargin >= 20);
	}
}

/// <summary>The planner's cost estimate on a recipe graph shaped like the real game's: many items, many ways.</summary>
public class PlannerCostTests
{
	private readonly ITestOutputHelper output;
	public PlannerCostTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void DeepWideRecipeGraph_StaysCheap()
	{
		// 40 crafted items, each with 3 recipes from two earlier items (and one back to a later item, a cycle):
		// without memoising crafted costs this is about 3^40 evaluations. Seen in game: a frozen game.
		FakeGame g = ProgressionTests.Mine();
		const int first = 20000, n = 40;
		for (int i = 0; i < n; i++) {
			int a = i == 0 ? FakeGame.Wood : first + i - 1, b = i < 2 ? FakeGame.StoneBlock : first + i - 2;
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(1, a), new Ingredient(1, b) } });
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(2, a) } });
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(1, b), new Ingredient(1, first + System.Math.Min(n - 1, i + 1)) } });
		}
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var watch = System.Diagnostics.Stopwatch.StartNew();
		ISkill s = p.ResolveForTest(new HaveItem(first + n - 1, 1, "top item"), ctx);
		output.WriteLine($"{watch.ElapsedMilliseconds} ms, {p.CostEvaluations} cost evaluations -> {s?.Describe()} {p.ResolveForTestStuck}");
		// Found obtainable (resolving all 40 levels then stops at the planner's depth limit, which is fine here).
		Assert.DoesNotContain("no way", p.ResolveForTestStuck ?? "");
		Assert.True(p.CostEvaluations < 20000, $"{p.CostEvaluations} evaluations");
	}
}

/// <summary>Kiting inside the arena: brake before its ends, walk back when off it.</summary>
public class ArenaFightTests
{
	private readonly ITestOutputHelper output;
	public ArenaFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly Core.Physics.PhysicsParams P = Core.Physics.PhysicsParams.Default;

	/// <summary>Ground row 25 (feet row 24), an arena over columns 50-145, the Eye hovering far above the middle.</summary>
	private (BotContext ctx, BossFightSkill fight) Setup(float bodyX, float vx)
	{
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new Core.Physics.BodyState { X = bodyX, Y = 25 * 16f - P.Height, VX = vx, ReleaseJump = true };
		ctx.Hostiles = new[] { new EntityInfo(1, FakeGame.EyeNpc, "Eye of Cthulhu", 97 * 16f, 2 * 16f, 100, 110, 0, 0, 2800, 2800, 15,
			EyePattern.EyeAiStyle, 0, 0, 10, 0) };
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc) { Arena = (50, 145, 24) };
		fight.Start(ctx);
		return (ctx, fight);
	}

	[Fact]
	public void RunningAtTheEnd_BrakesInTime()
	{
		// 3 px/tick needs 3² / (2 × 0.28) ≈ 16 px to stop; 12 px from the end it must already be braking.
		float maxX = 146 * 16f - P.Width;
		var (ctx, fight) = Setup(maxX - 12f, 3f);
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void OffTheArena_WalksBack()
	{
		var (ctx, fight) = Setup(160 * 16f, 0f);
		fight.Tick(ctx);
		Assert.StartsWith("walking back to the arena", fight.State);
	}
}
