using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Journey Mode (FakeJourney): the research habit researches a stack once the bag holds the whole count research takes, never
/// partly and never gear in use; the planner duplicates a researched item instead of gathering, crafting or hunting it
/// (stations, blocks, arrows, potions, ingredients, life crystals to max life); with Journey off nothing changes.
/// </summary>
public class JourneyTests
{
	private readonly ITestOutputHelper output;
	public JourneyTests(ITestOutputHelper output) => this.output = output;

	private (FakeGame, BotContext) Journey(bool on = true)
	{
		FakeGame g = EarlyGameTests.Field(60);
		g.Journey.On = on;
		BotContext ctx = g.CreateContext(output);
		return (g, ctx);
	}

	/// <summary>The habit's pass on a tick it acts on (every ResearchHabit.Every ticks).</summary>
	private static bool Habit(BotContext ctx)
	{
		ctx.Tick = ResearchHabit.Every * 100;
		return ResearchHabit.Tick(ctx, null);
	}

	private ISkill Resolve(ProgressionPlanner p, BotContext ctx, Need need)
	{
		ISkill s = p.ResolveForTest(need, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void Habit_ResearchesAFullStack_InOneGo()
	{
		var (g, ctx) = Journey();
		g.Items[FakeGame.Wood] = 120;
		Assert.True(Habit(ctx));
		Assert.Equal(100, g.Journey.ResearchDone(FakeGame.Wood));
		Assert.True(g.Journey.IsResearched(FakeGame.Wood));
		Assert.Equal(20, g.Count(FakeGame.Wood));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("Journey: researched 100 item9"));
	}

	[Fact]
	public void Habit_ResearchesStackAfterStack_WhenTheCountIsInSeveral()
	{
		// Wood in stacks of 40: 120 carried in three stacks, the 100 research takes go in at once, stack after stack.
		var (g, ctx) = Journey();
		g.StackSize[FakeGame.Wood] = 40;
		g.Items[FakeGame.Wood] = 120;
		Assert.True(Habit(ctx));
		Assert.True(g.Journey.IsResearched(FakeGame.Wood));
		Assert.Equal(3, g.Journey.Researched.Count);
		Assert.Equal(20, g.Count(FakeGame.Wood));
	}

	[Fact]
	public void Habit_WaitsForTheFullCount_NeverResearchesPartly()
	{
		// The user's way: "I wait until I have the number I need for researching."
		var (g, ctx) = Journey();
		g.Items[FakeGame.Wood] = 99;
		g.Items[FakeGame.LifeCrystal] = 4;
		Assert.False(Habit(ctx));
		Assert.Empty(g.Journey.Researched);
		Assert.Equal(99, g.Count(FakeGame.Wood));
	}

	[Fact]
	public void KeepRoom_TrashesAsBefore_NoPartialResearchOfTheTrash()
	{
		var (g, ctx) = Journey();
		g.SlotLimit = 3;
		g.Values[FakeGame.StoneBlock] = 0; // junk for KeepRoom (a building block, but two stacks of it)
		g.Items[FakeGame.StoneBlock] = 999 + 50;
		g.Items[FakeGame.Wood] = 10;
		BotBrain.KeepRoom(ctx);
		Assert.Contains(FakeGame.StoneBlock, g.Trashed);
		Assert.Empty(g.Journey.Researched);
		Assert.Equal(0, g.Journey.ResearchDone(FakeGame.StoneBlock));
	}

	[Fact]
	public void Habit_GearInUse_IsResearched_AndComesBackDuplicated()
	{
		// The user (2026-10-17): accessories researched "and THEN" worn, "same thing with ammo": the one in use goes into
		// research and a copy comes back from the menu at once (before: a spare had to be carried).
		var (g, ctx) = Journey();
		g.StackSize[FakeGame.IronPickaxe] = 1; // a tool: one a slot
		g.Items[FakeGame.IronPickaxe] = 1;
		Assert.True(Habit(ctx));
		Assert.True(g.Journey.IsResearched(FakeGame.IronPickaxe));
		Assert.Equal(1, g.Count(FakeGame.IronPickaxe));
		Assert.Contains(g.Journey.Duplicated, d => d.Type == FakeGame.IronPickaxe);

		var (a, actx) = Journey();
		a.Items[FakeGame.WoodenArrow] = 99; // research takes 99: all of them, and a stack comes back
		Assert.True(Habit(actx));
		Assert.True(a.Journey.IsResearched(FakeGame.WoodenArrow));
		Assert.True(a.Count(FakeGame.WoodenArrow) > 0);
	}

	[Fact]
	public void Habit_SkipsTheItemInHand_MidSwing()
	{
		var (g, ctx) = Journey();
		g.Items[FakeGame.Wood] = 100;
		g.Journey.InUse = FakeGame.Wood;
		Assert.False(Habit(ctx));
		Assert.Equal(100, g.Count(FakeGame.Wood));
		// Not offered again at once (RefusedTicks), then researched once it is out of hand.
		g.Journey.InUse = 0;
		ctx.Tick = ResearchHabit.Every * 100 + ResearchHabit.Every;
		Assert.False(ResearchHabit.Tick(ctx, null));
		ctx.Tick = ResearchHabit.Every * 100 + ResearchHabit.RefusedTicks;
		Assert.True(ResearchHabit.Tick(ctx, null));
	}

	[Fact]
	public void Habit_AStationResearched_IsDuplicatedBack()
	{
		// A work bench takes 1 to research: the bag's only one goes in, and one stack comes back (the bag keeps it).
		var (g, ctx) = Journey();
		g.Items[FakeGame.WorkBench] = 1;
		Assert.True(Habit(ctx));
		Assert.True(g.Journey.IsResearched(FakeGame.WorkBench));
		Assert.True(g.Count(FakeGame.WorkBench) >= 1);
		Assert.Single(g.Journey.Duplicated);
	}

	[Fact]
	public void Habit_NotInAFight()
	{
		var (g, ctx) = Journey();
		g.Items[FakeGame.Wood] = 100;
		g.AddSlime(1, 6, 24);
		g.Refresh(ctx);
		Assert.False(Habit(ctx));
		Assert.Equal(0, g.Journey.ResearchDone(FakeGame.Wood));
	}

	[Fact]
	public void ResearchedWood_IsDuplicated_NotGathered()
	{
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.Wood);
		var p = new ProgressionPlanner();
		var dup = Assert.IsType<DuplicateSkill>(Resolve(p, ctx, new HaveItem(FakeGame.Wood, 30, "wood")));
		Assert.Equal(FakeGame.Wood, dup.ItemType);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, dup, 60));
		Assert.True(g.Count(FakeGame.Wood) >= 30);
		Assert.Null(Resolve(p, ctx, new HaveItem(FakeGame.Wood, 30, "wood")));
	}

	[Fact]
	public void ResearchedStation_IsDuplicated_ThenPlaced()
	{
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.Furnace);
		g.Stations[FakeGame.TileBench] = new Core.Grid.TilePos(5, 24);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		var dup = Assert.IsType<DuplicateSkill>(Resolve(p, ctx, new StationPlaced(FakeGame.TileFurnace, "furnace")));
		Assert.Equal(FakeGame.Furnace, dup.ItemType);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, dup, 60));
		Assert.IsType<PlaceItemSkill>(Resolve(p, ctx, new StationPlaced(FakeGame.TileFurnace, "furnace")));
	}

	[Fact]
	public void ResearchedIngredients_TheCraftDrawsOnDuplication()
	{
		// Torches from gel and wood: both researched, neither hunted nor chopped; once in the bag, crafted.
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.Gel, FakeGame.Wood);
		var p = new ProgressionPlanner();
		var need = new HaveItem(FakeGame.Torch, 3, "torches");
		var gel = Assert.IsType<DuplicateSkill>(Resolve(p, ctx, need));
		Assert.Equal(FakeGame.Gel, gel.ItemType);
		EarlyGameTests.RunSkill(g, ctx, gel, 60);
		var wood = Assert.IsType<DuplicateSkill>(Resolve(p, ctx, need));
		Assert.Equal(FakeGame.Wood, wood.ItemType);
		EarlyGameTests.RunSkill(g, ctx, wood, 60);
		Assert.IsType<CraftSkill>(Resolve(p, ctx, need));
	}

	[Fact]
	public void ResearchedArrowsAndPotions_AreDuplicated()
	{
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.WoodenArrow, FakeGame.HealingPotion);
		var p = new ProgressionPlanner();
		Assert.Equal(FakeGame.WoodenArrow, Assert.IsType<DuplicateSkill>(Resolve(p, ctx, new HaveItem(FakeGame.WoodenArrow, 500, "arrows"))).ItemType);
		// A FromBag need (never gathered on Classic): researched, it is in the menu as surely as in the bag.
		Assert.Equal(FakeGame.HealingPotion,
			Assert.IsType<DuplicateSkill>(Resolve(p, ctx, new FromBag(new HaveItem(FakeGame.HealingPotion, 5, "healing potions")))).ItemType);
	}

	[Fact]
	public void TenLifeCrystals_AreResearched_ThenDuplicatedToMaxLife()
	{
		// Research takes 10 (Sacrifices.tsv "H"). Carried at 100 life: researched, not used (used, 10 make 300; researched, the
		// menu gives every crystal to 400), and the planner then duplicates and uses them.
		var (g, ctx) = Journey();
		g.Items[FakeGame.LifeCrystal] = 10;
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Crystals);
		int t = 0;
		for (; t < 60 * 60 * 2 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | {brain.Planner.Status}, max life {g.LifeMax}");
		Assert.True(g.Journey.IsResearched(FakeGame.LifeCrystal));
		Assert.Contains(g.Journey.Duplicated, d => d.Type == FakeGame.LifeCrystal);
		Assert.Equal(ProgressionPlanner.MaxCrystalLife, g.LifeMax);
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
	}

	[Fact]
	public void ResearchedLifeCrystals_MaxLife200InOneGo()
	{
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.LifeCrystal);
		var p = new ProgressionPlanner();
		var dup = Assert.IsType<DuplicateSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Equal(FakeGame.LifeCrystal, dup.ItemType);
		Assert.True(dup.Count >= 5, $"{dup.Count} crystals for 100 -> 200");
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, dup, 60));
		Assert.True(g.Count(FakeGame.LifeCrystal) >= 5); // one click: a full stack
		for (int i = 0; i < 5; i++) {
			Assert.IsType<UseItemSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
			Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new UseItemSkill(FakeGame.LifeCrystal), 200));
		}
		Assert.Equal(200, g.LifeMax);
		Assert.Null(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Single(g.Journey.Duplicated); // in one go: no second click, no mining
	}

	[Fact]
	public void FewerCrystalsThanResearchTakes_AreKept_ForResearch()
	{
		// The user's way (Journey test run J1: every crystal was used at once, max life 100 to 200 and none researched): 4 of
		// the 10 research takes are kept, and the plan goes for more rather than using them; Classic uses them as before.
		var (g, ctx) = Journey();
		g.Items[FakeGame.LifeCrystal] = 4;
		var p = new ProgressionPlanner();
		Assert.IsNotType<UseItemSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.True(ProgressionPlanner.HoldCrystalsForResearch(ctx));
		var (gc, cc) = Journey(on: false);
		gc.Items[FakeGame.LifeCrystal] = 4;
		Assert.IsType<UseItemSkill>(Resolve(new ProgressionPlanner(), cc, new LifeAtLeast(200, "max life")));
	}

	[Fact]
	public void TheDoll_IsResearched_AndDuplicatedBack()
	{
		// An accessory by its stats (so "gear"), but never worn nor in use: the one carried is researched (1) and comes back.
		var (g, ctx) = Journey();
		g.Gear.Add(FakeGame.GuideVoodooDoll);
		g.Items[FakeGame.GuideVoodooDoll] = 1;
		Assert.True(Habit(ctx));
		Assert.True(g.Journey.IsResearched(FakeGame.GuideVoodooDoll));
		Assert.True(g.Count(FakeGame.GuideVoodooDoll) >= 1);
	}

	[Fact]
	public void ResearchedDoll_IsDuplicated_NoHunt()
	{
		var (g, ctx) = Journey();
		g.Journey.MarkResearched(FakeGame.GuideVoodooDoll);
		var dup = new DuplicateSkill(FakeGame.GuideVoodooDoll, 1);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, dup, 60));
		Assert.True(g.Count(FakeGame.GuideVoodooDoll) >= 1);
	}

	[Fact]
	public void JourneyOff_NothingChanges()
	{
		// The same bag and research counts with Journey off: no research, and every need resolved as on Classic.
		var (g, ctx) = Journey(on: false);
		g.Journey.MarkResearched(FakeGame.Wood, FakeGame.LifeCrystal, FakeGame.Furnace);
		g.Items[FakeGame.StoneBlock] = 150;
		g.Items[FakeGame.LifeCrystal] = 10;
		Assert.False(Habit(ctx));
		Assert.Empty(g.Journey.Researched);
		Assert.False(ctx.Infinite(FakeGame.Wood));
		var p = new ProgressionPlanner();
		Assert.IsType<GatherWoodSkill>(Resolve(p, ctx, new HaveItem(FakeGame.Wood, 30, "wood")));
		Assert.IsType<UseItemSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.IsNotType<DuplicateSkill>(Resolve(p, ctx, new StationPlaced(FakeGame.TileFurnace, "furnace")));
		Assert.Empty(g.Journey.Duplicated);
	}

	[Fact]
	public void JourneyOn_NothingResearched_PlansAsOnClassic()
	{
		var (g, ctx) = Journey();
		var p = new ProgressionPlanner();
		Assert.IsType<GatherWoodSkill>(Resolve(p, ctx, new HaveItem(FakeGame.Wood, 30, "wood")));
		Assert.IsNotType<DuplicateSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Empty(g.Journey.Duplicated);
	}
}
