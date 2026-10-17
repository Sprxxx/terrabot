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
		// Without the auto-trash, researched gel is still not walked to: a click in the menu gives a stack (the user: "no point
		// in mining heart crystals if its already researched").
		Assert.False(CollectDropsSkill.AnyInRange(ctx, _ => true, 20));
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

	/// <summary>An item nothing in the plan uses (a gem, a pumpkin): researched with one.</summary>
	private const int Junk = 9001;

	[Fact]
	public void TrashResearched_PickedUpResearchedJunkGoes_WhatTheBotUsesStays()
	{
		var (g, ctx) = Journey();
		ctx.TrashResearched = true;
		g.Journey.Caps[Junk] = 1;
		g.Journey.MarkResearched(Junk, FakeGame.Wood, FakeGame.StoneBlock);
		g.Items[Junk] = 5;
		g.Items[FakeGame.Wood] = 20;
		g.Items[FakeGame.StoneBlock] = 40;
		g.Items[FakeGame.Lens] = 3; // not researched: stays for research
		Assert.True(ResearchHabit.TrashResearched(ctx, null) > 0);
		Assert.Equal(0, g.Count(Junk));
		Assert.Equal(20, g.Count(FakeGame.Wood));
		Assert.Equal(40, g.Count(FakeGame.StoneBlock));
		Assert.Equal(3, g.Count(FakeGame.Lens));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.StartsWith("Journey: trashed 5 "));
	}

	[Fact]
	public void TrashResearched_Off_OrNoDuplication_OrClassic_TrashesNothing()
	{
		var (g, ctx) = Journey();
		g.Journey.Caps[Junk] = 1;
		g.Journey.MarkResearched(Junk);
		g.Items[Junk] = 5;
		Assert.Equal(0, ResearchHabit.TrashResearched(ctx, null)); // the setting off
		ctx.TrashResearched = true;
		g.Journey.ChecklistMode = true;
		Assert.Equal(0, ResearchHabit.TrashResearched(ctx, null)); // nothing would come back
		g.Journey.ChecklistMode = false;
		g.Journey.On = false;
		Assert.Equal(0, ResearchHabit.TrashResearched(ctx, null)); // Classic
		Assert.Equal(5, g.Count(Junk));
	}

	/// <summary>An accessory (one a slot), and a tool nothing in the plan uses (a Magic Mirror, one a slot).</summary>
	private const int Accessory = 9101, Mirror = 9102;

	/// <summary>
	/// The user (2026-10-17): an accessory "should research, and THEN equip". Worn first, the research slot never saw it (it
	/// takes bag stacks only); now it is researched, a copy comes back from the menu, and that copy is worn.
	/// </summary>
	[Fact]
	public void AnAccessory_IsResearchedFirst_ThenWorn()
	{
		var (g, ctx) = Journey();
		g.Accessories.Add(Accessory);
		g.StackSize[Accessory] = 1;
		g.Journey.Caps[Accessory] = 1;
		g.Items[Accessory] = 1;
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		for (int t = 0; t < 600 && !g.Equipped.Contains(Accessory); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		var log = ((TestLog)ctx.Log).Lines;
		int researched = log.FindIndex(l => l.StartsWith($"Journey: researched 1 item{Accessory}"));
		int worn = log.FindIndex(l => l.StartsWith($"Gear: wearing item{Accessory} "));
		output.WriteLine($"researched at line {researched}, worn at line {worn}");
		Assert.True(g.Journey.IsResearched(Accessory));
		Assert.Contains(g.Journey.Duplicated, d => d.Type == Accessory);
		Assert.Contains(Accessory, g.Equipped);
		Assert.True(researched >= 0 && worn > researched, "researched, then worn");
	}

	/// <summary>
	/// The user (2026-10-17): coins researched "when available". A stack of 100 copper turns into a silver coin (Player.DoCoins),
	/// so the bag never holds the 100 research takes: 99 go in, then the last one, and a stack comes back from the menu.
	/// </summary>
	[Fact]
	public void Coins_GoInWhatASlotHolds_ThenTheRest()
	{
		var (g, ctx) = Journey();
		g.StackSize[FakeGame.CopperCoin] = 100;
		g.Journey.Caps[FakeGame.CopperCoin] = 100;
		ctx.Tick = ResearchHabit.Every * 100;
		g.Items[FakeGame.CopperCoin] = 98;
		Assert.False(ResearchHabit.Tick(ctx, null)); // not yet what a slot holds
		g.Items[FakeGame.CopperCoin] = 99;
		Assert.True(ResearchHabit.Tick(ctx, null));
		Assert.Equal(99, g.Journey.ResearchDone(FakeGame.CopperCoin));
		g.Items[FakeGame.CopperCoin] = 1;
		Assert.True(ResearchHabit.Tick(ctx, null));
		Assert.True(g.Journey.IsResearched(FakeGame.CopperCoin));
		Assert.True(g.Count(FakeGame.CopperCoin) > 0); // money back from the menu
		ctx.TrashResearched = true;
		Assert.Equal(0, ResearchHabit.TrashResearched(ctx, null)); // and never trashed
	}

	/// <summary>
	/// Journey run J2: the only Magic Mirror went into research and never came back (only what the bag "keeps" was duplicated
	/// back), and a second one was trashed later as "nothing needs it". A thing a slot holds one of is never junk.
	/// </summary>
	[Fact]
	public void AOneASlotTool_ComesBackAfterResearch_AndIsNotTrashed()
	{
		var (g, ctx) = Journey();
		ctx.TrashResearched = true;
		g.StackSize[Mirror] = 1;
		g.Journey.Caps[Mirror] = 1;
		g.Items[Mirror] = 1;
		ctx.Tick = ResearchHabit.Every * 100;
		Assert.True(ResearchHabit.Tick(ctx, null));
		Assert.True(g.Journey.IsResearched(Mirror));
		Assert.Equal(1, g.Count(Mirror));
		Assert.Equal(0, ResearchHabit.TrashResearched(ctx, null));
		Assert.Equal(1, g.Count(Mirror));
	}

	/// <summary>
	/// world_boc B4 (Nenne): the bag full, a dozen slots stacks of 9999 of researched things the plan keeps (a work bench, life
	/// crystals...), nothing trashed, and the Vertebrae dropped round the bot never picked up. In Journey a researched stack goes
	/// first: the menu gives it back when it is needed.
	/// </summary>
	[Fact]
	public void BagFull_AResearchedStackTheBotKeeps_IsTrashedForRoom()
	{
		var (g, ctx) = Journey();
		g.SlotLimit = 4;
		g.Journey.MarkResearched(FakeGame.WorkBench, FakeGame.LifeCrystal);
		g.StackSize[FakeGame.WorkBench] = 9999;
		g.StackSize[FakeGame.LifeCrystal] = 9999;
		g.Items[FakeGame.WorkBench] = 9999;
		g.Items[FakeGame.LifeCrystal] = 9999;
		g.Items[FakeGame.Torch] = 50;
		g.Items[FakeGame.WoodenSword] = 1;
		Assert.Equal(0, g.FreeSlots);
		BotBrain.KeepRoom(ctx);
		output.WriteLine($"free {g.FreeSlots}; trashed {string.Join(",", g.Trashed)}");
		Assert.True(g.FreeSlots >= BotBrain.MinFreeSlots);
		Assert.All(g.Trashed, t => Assert.True(t == FakeGame.WorkBench || t == FakeGame.LifeCrystal, $"trashed item{t}"));
		Assert.Equal(50, g.Count(FakeGame.Torch)); // the last torch stack stays
		Assert.Equal(1, g.Count(FakeGame.WoodenSword)); // gear stays
	}
}
