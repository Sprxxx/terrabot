using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Upgraded Research (workshop 2710510282), as the user plays with it (2026-10-17), and the same without it:
/// <list type="bullet">
/// <item>"Researched Consumables are infinite" keeps a used crystal, a placed station, a thrown bomb in the bag: each is seen
/// done by its effect (the swing, the tile), once, not used on for want of the stack shrinking;</item>
/// <item>"Use Creative Panel as Item Checklist": no duplication, so nothing is planned from the menu and research leaves one;</item>
/// <item>"Auto-Trash picked up Researched items": a drop deleted on pickup is not walked to;</item>
/// <item>"Craft from Researched stations": a researched station needs no placing nor walk.</item>
/// </list>
/// </summary>
public class ResearchModTests
{
	private readonly ITestOutputHelper output;
	public ResearchModTests(ITestOutputHelper output) => this.output = output;

	private (FakeGame, BotContext) Journey()
	{
		FakeGame g = EarlyGameTests.Field(60);
		g.Journey.On = true;
		return (g, g.CreateContext(output));
	}

	[Fact]
	public void KeptLifeCrystal_IsUsedOnce_AndTheUseSucceeds()
	{
		FakeGame g = EarlyGameTests.Field(30);
		g.KeepsConsumables = true;
		g.Items[FakeGame.LifeCrystal] = 1;
		BotContext ctx = g.CreateContext(output);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new UseItemSkill(FakeGame.LifeCrystal), 200));
		Assert.Equal(120, g.LifeMax); // one use, not one a swing for three seconds
		Assert.Equal(1, g.Count(FakeGame.LifeCrystal));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("the stack kept"));
	}

	[Fact]
	public void KeptWorkBench_IsPlacedOnce_AndThePlaceSucceeds()
	{
		FakeGame g = EarlyGameTests.Field(40);
		g.KeepsConsumables = true;
		g.Items[FakeGame.WorkBench] = 1;
		BotContext ctx = g.CreateContext(output);
		var place = new PlaceItemSkill(FakeGame.WorkBench);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, place, 600));
		Assert.True(g.Stations.ContainsKey(FakeGame.TileBench));
		Assert.Equal(place.PlacedAt, g.Stations[FakeGame.TileBench]);
		Assert.Equal(1, g.Count(FakeGame.WorkBench));
		Assert.Single(g.Furniture.Where(kv => kv.Value == FakeGame.TileBench).Select(kv => kv.Key.Y).Distinct());
	}

	[Fact]
	public void ChecklistMode_NothingIsDuplicated_TheItemIsGatheredAsOnClassic()
	{
		var (g, ctx) = Journey();
		g.Journey.ChecklistMode = true;
		g.Journey.MarkResearched(FakeGame.Wood, FakeGame.LifeCrystal);
		Assert.False(ctx.Infinite(FakeGame.Wood));
		ISkill s = new ProgressionPlanner().ResolveForTest(new HaveItem(FakeGame.Wood, 20, "wood"), ctx);
		output.WriteLine(s?.Describe());
		Assert.IsNotType<DuplicateSkill>(s);
		Assert.Equal(0, g.Journey.Duplicate(FakeGame.Wood));
	}

	[Fact]
	public void ChecklistMode_ResearchLeavesOne_AndCrystalsAreNotHeld()
	{
		var (g, ctx) = Journey();
		g.Journey.ChecklistMode = true;
		g.Journey.Caps[FakeGame.Wood] = 1; // the checklist's every cap is 1
		g.Items[FakeGame.Wood] = 1;
		ctx.Tick = ResearchHabit.Every * 100;
		Assert.False(ResearchHabit.Tick(ctx, null)); // the only one: kept
		g.Items[FakeGame.Wood] = 2;
		Assert.True(ResearchHabit.Tick(ctx, null));
		Assert.Equal(1, g.Count(FakeGame.Wood));
		g.Items[FakeGame.LifeCrystal] = 4;
		Assert.False(ProgressionPlanner.HoldCrystalsForResearch(ctx));
		Assert.IsType<UseItemSkill>(new ProgressionPlanner().ResolveForTest(new LifeAtLeast(200, "max life"), ctx));
	}

	[Fact]
	public void AutoTrash_AResearchedDropNotCarried_IsNotWalkedTo()
	{
		var (g, ctx) = Journey();
		g.Journey.AutoTrash = true;
		g.Journey.MarkResearched(FakeGame.Gel);
		int x = g.Sim.Node.X + 8, y = g.Sim.Node.Y;
		g.Drops.Add(new DropInfo(7001, FakeGame.Gel, "Gel", 2, x * 16f, y * 16f + 4, 8, 8));
		g.Refresh(ctx);
		Assert.False(CollectDropsSkill.AnyInRange(ctx, _ => true, 20));
		g.Drops.Add(new DropInfo(7002, FakeGame.Lens, "Lens", 1, x * 16f, y * 16f + 4, 8, 8));
		g.Refresh(ctx);
		Assert.True(CollectDropsSkill.AnyInRange(ctx, _ => true, 20)); // not researched: picked up as ever
		g.Journey.AutoTrash = false;
		g.Drops.RemoveAll(d => d.Type == FakeGame.Lens);
		g.Refresh(ctx);
		Assert.True(CollectDropsSkill.AnyInRange(ctx, _ => true, 20)); // without the mod's auto-trash: gel is fetched
	}

	[Fact]
	public void CraftFromResearchedStations_TheAnvilNeedsNoPlacingNorWalk()
	{
		var (g, ctx) = Journey();
		g.Items[FakeGame.IronBar] = 10;
		g.Items[FakeGame.Wood] = 3;
		var need = new HaveItem(FakeGame.IronPickaxe, 1, "iron pickaxe");
		ISkill without = new ProgressionPlanner().ResolveForTest(need, ctx);
		output.WriteLine($"without: {without?.Describe()}");
		Assert.IsNotType<CraftSkill>(without);
		g.Journey.StationsByResearch.Add(FakeGame.TileAnvil);
		Assert.IsType<CraftSkill>(new ProgressionPlanner().ResolveForTest(need, ctx));
	}
}
