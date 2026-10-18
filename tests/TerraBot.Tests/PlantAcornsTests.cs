using TerraBot.Core.Grid;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>world_wof E48-E50 (2026-10-18): the base's forest cut, 76 acorns carried, the Guide's room waited three runs for wood.</summary>
public class PlantAcornsTests
{
	private readonly ITestOutputHelper output;
	public PlantAcornsTests(ITestOutputHelper output) => this.output = output;

	private const int Acorn = 27;

	[Fact]
	public void Acorns_ArePlanted_WhereTreesGrow_SpacedApart()
	{
		FakeGame g = EarlyGameTests.Field(80);
		BotContext ctx = g.CreateContext(output);
		g.Items[Acorn] = 10;
		var plant = new PlantAcornsSkill(Acorn, 5);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, plant, 60 * 120));
		var saplings = g.Furniture.Where(kv => kv.Value == FakeGame.TileSapling).Select(kv => kv.Key).OrderBy(t => t.X).ToList();
		output.WriteLine(string.Join(" ", saplings));
		Assert.Equal(5, plant.Planted);
		Assert.Equal(5, saplings.Count);
		Assert.Equal(5, g.Count(Acorn));
		for (int i = 1; i < saplings.Count; i++)
			Assert.True(saplings[i].X - saplings[i - 1].X >= 3, $"{saplings[i - 1]} and {saplings[i]} too close");
	}

	[Fact]
	public void NoTreeInReach_AndAcornsCarried_TheyArePlantedBeforeTheNextSearch()
	{
		FakeGame g = EarlyGameTests.Field(80);
		BotContext ctx = g.CreateContext(output);
		g.Items[Acorn] = 40;
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		ISkill first = p.ResolveForTest(new HaveItem(FakeGame.Wood, 10, "Wood"), ctx);
		Assert.IsType<GatherWoodSkill>(first);
		p.OnSkillFinished(first, SkillStatus.Failed, ctx);
		ISkill next = p.ResolveForTest(new HaveItem(FakeGame.Wood, 10, "Wood"), ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsType<PlantAcornsSkill>(next);
		p.OnSkillFinished(next, SkillStatus.Succeeded, ctx);
		Assert.IsType<GatherWoodSkill>(p.ResolveForTest(new HaveItem(FakeGame.Wood, 10, "Wood"), ctx)); // once a day
	}

	/// <summary>world_wof E48-E54 (2026-10-18): no tree within the search's 280 tiles; one far off on the map is walked to.</summary>
	[Fact]
	public void NoTreeInReach_ATreeOnTheMap_IsWalkedTo()
	{
		FakeGame g = EarlyGameTests.Field(700, 650);
		g.TreeSight = 40; // seen from near only (the map remembers it: MapTrees)
		BotContext ctx = g.CreateContext(output);
		var gather = new GatherWoodSkill(FakeGame.Wood, 3);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, gather, 60 * 60 * 30);
		foreach (string l in ((TestLog)ctx.Log).Lines.Where(l => l.StartsWith("GatherWood")))
			output.WriteLine(l);
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("to a tree on the map at (650,"));
	}

	/// <summary>world_wof E55/E56 (2026-10-18): remembered trees that were stumps by then, walked to and "chopped" for no wood.</summary>
	[Fact]
	public void ARememberedStump_IsNotWalkedBackTo()
	{
		FakeGame g = EarlyGameTests.Field(120);
		g.TreeSight = 10;
		var stump = new TilePos(60, 24);
		g.Trees[stump] = 1; // the base alone: no trunk above
		BotContext ctx = g.CreateContext(output);
		ctx.KnownTrees.Add(stump);
		var gather = new GatherWoodSkill(FakeGame.Wood, 3);
		EarlyGameTests.RunSkill(g, ctx, gather, 600);
		Assert.DoesNotContain(((TestLog)ctx.Log).Lines, l => l.Contains("back to one seen at (60,24)"));
		Assert.DoesNotContain(stump, ctx.KnownTrees);
	}
}
