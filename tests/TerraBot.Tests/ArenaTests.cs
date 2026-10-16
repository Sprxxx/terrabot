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
	public void Arena_HasAPostAtEachEnd_AndTheFightStaysInsideThem()
	{
		// Measured run U17: knocked past the open east end at 234/2800, down a 41-row drop, killed there.
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		int floor = Blueprint.ArenaClearance;
		for (int y = floor - Blueprint.ArenaEndPostHeight; y < floor; y++) {
			Assert.Equal(PlanCell.Block, bp.At(0, y));
			Assert.Equal(PlanCell.Block, bp.At(bp.Width - 1, y));
		}
		Assert.NotEqual(PlanCell.Block, bp.At(1, floor - 1));
		Assert.NotEqual(PlanCell.Block, bp.At(0, floor - Blueprint.ArenaEndPostHeight - 1)); // jumped over
		var p = new ProgressionPlanner();
		typeof(ProgressionPlanner).GetProperty(nameof(ProgressionPlanner.ArenaSite)).SetValue(p, new TilePos(100, 10));
		var ctx = new BotContext { Ids = FakeGame.Ids };
		Assert.Equal((101, 100 + bp.Width - 2, 10 + bp.StandRow), p.ArenaBounds(ctx));
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
	public void SiteFinder_Arena_FurnitureInItsOpenAir_DoesNotRuleItOut()
	{
		// A run from U7's arena checkpoint: the bot's own benches and tables (solid-topped, read as platforms) ruled out 94
		// of 161 arena sites.
		FakeGame g = EarlyGameTests.Field(160, 50, 75);
		for (int x = 10; x < 150; x += 20) {
			g.Grid.Set(x, 24, CellKind.Platform); // a work bench on the ground
			g.Furniture[new TilePos(x, 24)] = FakeGame.TileBench;
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint arena = Blueprint.Arena(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, arena, new TilePos(80, 24));
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, arena, new TilePos(80, 24))}");
		Assert.NotNull(site);
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(5, 24 - 4))); // a house's room must be clear
	}

	[Fact]
	public void SiteFinder_Arena_ATreeStandingInADip_IsFelledAndFilledIn_NotARuleOut()
	{
		// Measured run U9: trees standing in dips beside the house, their trunks up through the floor row ((1986,283) on
		// ground at row 284), ruled out 29 of the 47 arena sites clear of the house ("a tree in the floor"), the bot's
		// work benches on bumps the other 18, and auto mode stopped at max life 200.
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 20; x < 290; x += 40) {
			g.Grid.Set(x, 25, CellKind.Empty); // a dip one deep, a tree standing in it
			g.Trees[new TilePos(x, 25)] = 8;
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var anchor = new TilePos(150, 24);
		TilePos? site = SiteFinder.Find(ctx, bp, anchor);
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, anchor)}");
		Assert.NotNull(site);
		Assert.Equal(25, site.Value.Y + bp.Height - 1);
		// A house's floor still may not have one (no axe in its plan).
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(15, 25 - 5)));
	}

	[Fact]
	public void SiteFinder_Explain_SaysWhereAndTheFewestEdits()
	{
		// Measured run U9: "a tree in the floor 29, unbreakable tile 2 under 18 18" said what ruled the sites out but not
		// where; finding the trees and the benches took a parse of the saved world.
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 100; x <= 120; x++)
			g.Grid.Set(x, 24, CellKind.Water); // a pond in most of the sites
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		string why = SiteFinder.Explain(ctx, bp, new TilePos(150, 24));
		output.WriteLine(why);
		Assert.Matches(@"liquid \d+ \(\d+ at \(\d+,24\)\)", why);
		Assert.Matches(@"fewest edits \d+ at \(\d+,18\)", why);
	}

	[Fact]
	public void BuildSkill_Arena_FellsATreeWhoseTopIsInTheFloor_ThenFillsItIn()
	{
		// A tree standing in a pit, all of it at or below the floor row: nothing of it in the open air to fell it by, and a
		// floor block cannot go into its trunk. The floor row's trees are felled too (SiteFinder counts them as felled).
		FakeGame g = EarlyGameTests.Field(140);
		for (int x = 58; x <= 62; x++)
			for (int y = 25; y <= 27; y++)
				g.Grid.Set(x, y, CellKind.Empty); // a pit 5 wide and 3 deep
		g.Trees[new TilePos(60, 27)] = 3; // its top in the floor row
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(20, 25 - bp.Height + 1);
		Assert.True(SiteFinder.Edits(ctx, bp, site) > 0);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks; placed {g.Sim.Placed}, broken {g.Sim.Broken}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.DoesNotContain(new TilePos(60, 27), g.Trees.Keys);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site));
	}

	[Fact]
	public void ArenaPlan_NoSiteNearTheHouse_LooksFartherOut()
	{
		// Measured runs U8cp and U9: no site within 80 tiles was a stop ("stuck on arena: no arena site"), five rests and
		// auto mode off, with max life 200 and the bow made. A player walks a little further for good ground.
		FakeGame g = EarlyGameTests.FieldFrom(400, 200);
		for (int x = 120; x <= 280; x++)
			g.Grid.Set(x, 24, CellKind.Water); // a lake round the bot: every site within 80 has some of it
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		ISkill s = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}; site {p.ArenaSite}");
		Assert.Null(p.ResolveForTestStuck);
		Assert.NotNull(p.ArenaSite);
		int mid = p.ArenaSite.Value.X + Blueprint.ArenaWidth / 2;
		Assert.InRange(System.Math.Abs(mid - 200), Blueprint.ArenaSearchRadius + 1, Blueprint.ArenaFarSearchRadius);
		Assert.False(p.ArenaOnOpenGround);
	}

	[Fact]
	public void ArenaPlan_NoSiteAnywhere_TheBossIsFoughtOnOpenGround()
	{
		// Measured runs U8cp and U9: "no arena site" stopped auto mode before the summon. The arena is a convenience: the
		// fight does without one (BossFightSkill.Arena null), as a player fights the Eye on the surface.
		FakeGame g = EarlyGameTests.FieldFrom(500, 250);
		for (int x = 0; x < 500; x++)
			g.Grid.Set(x, 24, CellKind.Water); // water everywhere
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		ISkill s = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}");
		Assert.Null(p.ResolveForTestStuck);
		Assert.Null(s);
		Assert.True(p.ArenaOnOpenGround);
		Assert.Null(p.ArenaSite);
		Assert.Null(p.ArenaBounds(ctx));
		Assert.Null(p.ResolveForTest(new ArenaBuilt("arena"), ctx)); // not looked for again at every plan
		p.ForgetGivenUp();
		Assert.False(p.ArenaOnOpenGround); // after a rest it is looked for again
	}

	[Fact]
	public void SiteFinder_Arena_LooksLeftOfTheHouse_AsFarAsRight()
	{
		// Measured run S12: searched by its left edge within 80 tiles, a 96-wide arena could never go left of the house:
		// "over the house" for 92 of 161 sites, and with the right side no good, no site at all.
		FakeGame g = EarlyGameTests.Field(320, 50, 75);
		for (int x = 175; x < 320; x++) // a lake right of the house
			g.Grid.Set(x, 24, CellKind.Water);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var house = (new TilePos(155, 15), new TilePos(172, 30));
		var anchor = new TilePos(160, 24);
		TilePos? site = SiteFinder.Find(ctx, bp, anchor, keepOut: new[] { house });
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, anchor, new[] { house })}");
		Assert.NotNull(site);
		Assert.True(site.Value.X + bp.Width - 1 < 155, "left of the house");
	}

	[Fact]
	public void AnArenaSiteGivenUpOverACell_TheNextSiteIsNotOverItEither()
	{
		// Measured run S16: a floor block that would not go in failed the build; the next site was one column over, with
		// the same cell in its floor, eleven times over (12 game hours).
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 5; x < 300; x += 30)
			g.Grid.Set(x, 25, CellKind.Empty); // a hole in the floor every 30 tiles: every site has some
		GiveArenaKit(g);
		g.Items[FakeGame.DirtBlock] = 200;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		var build = Assert.IsType<BuildSkill>(p.ResolveForTest(new ArenaBuilt("arena"), ctx));
		TilePos first = build.Origin;
		int hole = Enumerable.Range(first.X, Blueprint.ArenaWidth).First(x => (x - 5) % 30 == 0);
		var bad = new TilePos(hole, 25);
		g.Blocks.NoPlace.Add(bad); // nothing goes in that one
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 3));
		output.WriteLine(build.FailureReason);
		for (int i = 0; i < ProgressionPlanner.MaxBuildFailuresPerSite; i++)
			p.OnSkillFinished(build, SkillStatus.Failed, ctx);
		ISkill next = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}; site {p.ArenaSite}");
		Assert.NotNull(p.ArenaSite);
		Assert.False(bad.X >= p.ArenaSite.Value.X && bad.X <= p.ArenaSite.Value.X + Blueprint.ArenaWidth - 1, "the new site is over the cell again");
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
	public void HeartLantern_ComesFromASpareCrystal_NotAHuntForOne()
	{
		// Measured runs U2 and U4: the lantern's crystal was looked for from d5 +10h and d7 +13h to the end of the run, and
		// the boss was never summoned. Without a crystal in the bag the house goes on without the lantern.
		FakeGame g = EarlyGameTests.Field(60);
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		Blueprint house = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, house, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(house, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(house, site), SkillStatus.Succeeded, ctx);
		var need = new HouseBuilt("heart lantern", HeartLantern: true);
		ISkill without = p.ResolveForTest(need, ctx);
		output.WriteLine($"no crystal: {p.Status} -> {without?.Describe() ?? "nothing to do"}");
		Assert.Null(without);
		Assert.Null(p.ResolveForTestStuck);
		g.Items[FakeGame.LifeCrystal] = 1; // one turns up
		ISkill with = p.ResolveForTest(need, ctx);
		output.WriteLine($"a crystal: {p.Status} -> {with?.Describe()}");
		Assert.NotNull(with);
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
	public void UnderTheArenaNearItsMiddle_TheWalkBackGoesOntoTheFloor_NotDoneAtOnce()
	{
		// Measured run U11cp: knocked to 5 rows under the floor and within 6 tiles of the middle, the walk back was "reached"
		// at once and began again every tick, 6460 times, while the Eye of Cthulhu killed the bot.
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Body = new Core.Physics.BodyState { X = 97 * 16f, Y = 25 * 16f - P.Height, ReleaseJump = true };
		var lines = new System.Collections.Generic.List<string>();
		ctx.Log = new CountLog(lines);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Hostiles = new[] { new EntityInfo(1, FakeGame.EyeNpc, "Eye of Cthulhu", 97 * 16f, 2 * 16f, 100, 110, 0, 0, 2800, 2800, 15,
			EyePattern.EyeAiStyle, 0, 0, 10, 0) };
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc) { Arena = (50, 145, 19) }; // the floor 5 rows up
		fight.Start(ctx);
		for (int t = 0; t < 30; t++) {
			ctx.Tick = t;
			fight.Tick(ctx);
		}
		int starts = lines.Count(l => l.Contains("off the arena"));
		output.WriteLine($"{fight.Describe()}; {starts} walk(s) back begun");
		Assert.Equal(1, starts);
	}

	private sealed class CountLog : IBotLog
	{
		private readonly System.Collections.Generic.List<string> lines;
		public CountLog(System.Collections.Generic.List<string> lines) => this.lines = lines;
		public void Info(string m) => lines.Add(m);
		public void Warn(string m) => lines.Add(m);
		public void Error(string m, System.Exception ex = null) => lines.Add(m);
		public void Debug(string m) { }
	}

	[Fact]
	public void SlidAFewPixelsPastTheEnd_IsStillOnTheArena_NoWalkBack()
	{
		// Checkpoint run U16cp: "off the arena at (2030,266)" with the floor from 2031, four walks back without dodging,
		// and the Eye of Cthulhu killed the bot during one.
		var (ctx, fight) = Setup(50 * 16f - 12f, 0f); // 12 px past the west end: the node reads column 49
		Assert.Equal(49, ctx.Node.X);
		fight.Tick(ctx);
		output.WriteLine(fight.State);
		Assert.DoesNotContain("walking back", fight.State);
	}

	[Fact]
	public void OffTheArena_WalksBack()
	{
		var (ctx, fight) = Setup(160 * 16f, 0f);
		fight.Tick(ctx);
		Assert.StartsWith("walking back to the arena", fight.State);
	}
}
