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
}
