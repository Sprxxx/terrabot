using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 6: building a house, wearing armour, and the "settle" plan, in <see cref="FakeGame"/>.</summary>
public class BuildingTests
{
	private readonly ITestOutputHelper output;
	public BuildingTests(ITestOutputHelper output) => this.output = output;

	private static void GiveHouseKit(FakeGame g)
	{
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
	}

	private SkillStatus Run(FakeGame g, BotContext ctx, ISkill skill, int maxTicks)
	{
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, skill, maxTicks);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks ({skill.Describe()}); placed {g.Sim.Placed}, broken {g.Sim.Broken}, out of range {g.Sim.OutOfRange}");
		return s;
	}

	[Fact]
	public void SmallHouse_IsBigEnoughForVanilla()
	{
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		Assert.Equal(12, bp.Width);
		Assert.Equal(6, bp.Height);
		Assert.True(bp.Width * bp.Height >= 60);
		Assert.Equal(40, bp.Count(PlanCell.Wall));
		Assert.Equal(PlanCell.Clear, bp.At(0, 2)); // doorway
		Assert.Equal(PlanCell.Block, bp.At(0, 1)); // the door's top anchor
	}

	[Fact]
	public void SiteFinder_AvoidsTrees_AndPrefersFlatGround()
	{
		FakeGame g = EarlyGameTests.Field(80, 10);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']);
		Assert.NotNull(site);
		output.WriteLine($"site {site}");
		Assert.Equal(25 - (bp.Height - 1), site.Value.Y); // floor row = ground row 25
		Assert.False(site.Value.X <= 10 && site.Value.X + bp.Width > 10, "house over the tree");
		Assert.Equal(0, SiteFinder.Edits(ctx, bp, site.Value) - CountMissingRoof(bp));
	}

	/// <summary>On flat open ground only the frame above the floor has to be placed.</summary>
	private static int CountMissingRoof(Blueprint bp) => bp.Count(PlanCell.Block) - bp.Width;

	[Fact]
	public void BuildSkill_BuildsValidHousing_OnFlatGround()
	{
		FakeGame g = EarlyGameTests.Field(60);
		GiveHouseKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value;
		var build = new BuildSkill(bp, site);
		Assert.Equal(SkillStatus.Succeeded, Run(g, ctx, build, 60 * 120));
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
		Assert.Null(ctx.Reserved);
		Assert.Equal(40, g.Walls.Count);
		Assert.True(g.Sim.Node.X < site.X, "the bot should end outside, by the door");
	}

	[Fact]
	public void BuildSkill_ClearsADirtMound_First()
	{
		FakeGame g = EarlyGameTests.Field(60);
		for (int x = 18; x <= 22; x++)
			for (int y = 22; y <= 24; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		GiveHouseKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		var site = new TilePos(15, 25 - bp.Height); // right over the mound
		Assert.True(SiteFinder.Edits(ctx, bp, site) > 0);
		Assert.Equal(SkillStatus.Succeeded, Run(g, ctx, new BuildSkill(bp, site), 60 * 150));
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
	}

	[Fact]
	public void BuildSkill_FailsCleanly_WithoutWalls()
	{
		FakeGame g = EarlyGameTests.Field(60);
		GiveHouseKit(g);
		g.Items[FakeGame.WoodWall] = 5;
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		var build = new BuildSkill(bp, SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value);
		Assert.Equal(SkillStatus.Failed, Run(g, ctx, build, 60 * 120));
		Assert.Contains("out of walls", build.FailureReason);
		Assert.Null(ctx.Reserved);
	}

	[Fact]
	public void Planner_WearsCarriedArmour_AndCraftsMissingPieces()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.IronHelmet] = 1;
		g.Items[FakeGame.IronBar] = 25;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		HouseAlreadyThere(g, p, ctx);

		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		var equip = Assert.IsType<EquipSkill>(next);
		Assert.Equal(FakeGame.IronHelmet, equip.ItemType);

		g.Equipped.Add(FakeGame.IronHelmet);
		g.Items[FakeGame.IronHelmet] = 0;
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		// Chainmail next: crafted at the anvil (or walking there first).
		Assert.True(next is CraftSkill { ItemType: FakeGame.IronChainmail } || next is WalkToSkill && p.Status.Contains("item81"), p.Status);
	}

	[Fact]
	public void Night_WithAHouse_StaysHome_UntilGettingHomeFails()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		var home = Assert.IsType<StayHomeSkill>(next);

		// Seen in game: no path home, and the same failing walk again and again until the brain gave up.
		p.OnSkillFinished(home, SkillStatus.Failed, ctx);
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsNotType<StayHomeSkill>(next);
	}

	/// <summary>Builds a valid house next to the start through the fake's own placement, and tells the planner about it.</summary>
	private void HouseAlreadyThere(FakeGame g, ProgressionPlanner p, BotContext ctx)
	{
		GiveHouseKit(g);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(bp, site), SkillStatus.Succeeded, ctx);
		Assert.Equal(site, p.HouseSite);
	}

	[Fact]
	public void AutoBrain_RunsTheSettleChain()
	{
		FakeGame g = ProgressionTests.Mine(110, oreRows: 8, veinWidth: 40, treeColumns: new[] { 37, 44, 51, 58, 65, 72, 79, 86, 93, 100, 107 });
		g.AddSlime(7, 30, 24);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Settle);
		int t = 0;
		for (; t < 60 * 60 * 40 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		string inv = string.Join(", ", g.Items.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"));
		output.WriteLine($"after {t} ticks: {brain.Describe()} | equipped {string.Join(",", g.Equipped)} | inv {inv} | out of range {g.Sim.OutOfRange}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status + " / " + brain.LastResult);
		Assert.True(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves }.All(g.Equipped.Contains));
		Assert.Equal(1, g.Count(FakeGame.IronBroadsword));
		TilePos site = brain.Planner.HouseSite.Value;
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
	}
}
