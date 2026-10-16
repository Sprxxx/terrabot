using System.Linq;
using System.Collections.Generic;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Chests in sight are looted; accessories from them are worn.</summary>
public class ChestTests
{
	private readonly ITestOutputHelper output;
	public ChestTests(ITestOutputHelper output) => this.output = output;

	private static FakeGame WithChest(int x, bool locked = false, bool seen = true)
	{
		FakeGame g = EarlyGameTests.Field(80);
		var chest = new TilePos(x, 23); // 2×2 on the ground (feet row 24)
		g.Chests[chest] = new List<(int, int)> { (FakeGame.HermesBoots, 1), (FakeGame.LesserHealingPotion, 3) };
		g.Accessories.Add(FakeGame.HermesBoots);
		if (locked)
			g.LockedChests.Add(chest);
		if (!seen)
			g.HideFrom(x - 2);
		return g;
	}

	private (FakeGame g, BotBrain brain) RunAuto(FakeGame g, int ticks)
	{
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		for (int t = 0; t < ticks; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			brain.Tick(ctx);
			g.Step(ctx);
		}
		return (g, brain);
	}

	[Fact]
	public void SeenChest_IsLootedFirst_AndTheBootsWorn()
	{
		var (g, brain) = RunAuto(WithChest(30), 60 * 30);
		output.WriteLine(brain.Describe());
		Assert.Equal(3, g.Count(FakeGame.LesserHealingPotion));
		Assert.Contains(FakeGame.HermesBoots, g.Equipped);
		Assert.Empty(g.Chests[new TilePos(30, 23)]);
	}

	/// <summary>Measured run S5: a worn Inner Tube floated the bot over a flooded hole for 278 heartbeats.</summary>
	[Fact]
	public void InnerTube_IsNotWorn_AndTakenOffIfWorn()
	{
		FakeGame g = WithChest(30);
		g.Accessories.Add(FakeGame.InnerTube);
		g.Items[FakeGame.InnerTube] = 1;
		var (_, _) = RunAuto(g, 60 * 30);
		Assert.DoesNotContain(FakeGame.InnerTube, g.Equipped);
		Assert.Contains(FakeGame.HermesBoots, g.Equipped);

		FakeGame worn = EarlyGameTests.Field(80);
		worn.Accessories.Add(FakeGame.InnerTube);
		worn.Equipped.Add(FakeGame.InnerTube);
		RunAuto(worn, 60 * 5);
		Assert.DoesNotContain(FakeGame.InnerTube, worn.Equipped);
		Assert.Equal(1, worn.Count(FakeGame.InnerTube));
	}

	[Fact]
	public void AFullBag_OfKeptThings_TrashesTheSpareCopiesOfOneThatDoesNotStack()
	{
		// Measured run M10b: nine Shackles (an accessory, so kept as gear) in a bag of 50 full slots, nothing else to trash;
		// 31 crafts failed "crafting stayed blocked (cursor item / full inventory)".
		FakeGame g = EarlyGameTests.Field(80);
		BotContext ctx = g.CreateContext(output);
		const int shackle = 3000;
		g.Items.Clear();
		for (int i = 0; i < 41; i++) {
			g.Items[2000 + i] = 1; // gear of one kind each
			g.Gear.Add(2000 + i);
			g.Values[2000 + i] = 100;
		}
		g.Items[shackle] = 9;
		g.StackSize[shackle] = 1;
		g.Gear.Add(shackle);
		g.Values[shackle] = 1000;
		g.SlotLimit = 50;
		Assert.Equal(0, g.FreeSlots);
		BotBrain.KeepRoom(ctx);
		output.WriteLine($"trashed: {string.Join(", ", g.Trashed)}; free {g.FreeSlots}");
		Assert.True(g.FreeSlots >= BotBrain.MinFreeSlots);
		Assert.All(g.Trashed, t => Assert.Equal(shackle, t));
		Assert.True(g.Count(shackle) >= 1);
		Assert.All(Enumerable.Range(2000, 41), t => Assert.Equal(1, g.Count(t)));
	}

	/// <summary>Measured run S9: a full bag (dirt, stone, mud, cobwebs) left a mined life crystal on the floor.</summary>
	[Fact]
	public void AFullBag_TrashesWorthlessStacks_NotTheOnesInUse()
	{
		FakeGame g = EarlyGameTests.Field(80);
		BotContext ctx = g.CreateContext(output);
		const int cobweb = 150, mud = 176;
		g.Items.Clear();
		for (int i = 0; i < 10; i++)
			g.Items[2000 + i] = 1; // the hotbar's tools
		foreach (int i in Enumerable.Range(2000, 10))
			g.Gear.Add(i);
		g.Items[FakeGame.DirtBlock] = 2 * FakeGame.MaxStack; // two stacks: one is spare
		g.Items[FakeGame.StoneBlock] = 500;
		g.Items[cobweb] = 61;
		g.Items[mud] = 300;
		g.Items[FakeGame.Wood] = 40;
		g.Items[FakeGame.Gel] = 10;
		foreach (int junk in new[] { FakeGame.DirtBlock, FakeGame.StoneBlock, cobweb, mud, FakeGame.Wood, FakeGame.Gel })
			g.Values[junk] = 0;
		for (int i = 0; i < 33; i++)
			g.Items[1000 + i] = 1; // worth something
		g.SlotLimit = 50;
		Assert.Equal(0, g.FreeSlots);
		BotBrain.KeepRoom(ctx);
		output.WriteLine($"trashed: {string.Join(", ", g.Trashed)}; free {g.FreeSlots}");
		Assert.True(g.FreeSlots >= BotBrain.MinFreeSlots);
		Assert.Equal(FakeGame.DirtBlock, g.Trashed[0]); // the spare block stack first
		Assert.Equal(FakeGame.MaxStack, g.Count(FakeGame.DirtBlock));
		Assert.Equal(500, g.Count(FakeGame.StoneBlock));
		Assert.Equal(40, g.Count(FakeGame.Wood));
		Assert.Equal(10, g.Count(FakeGame.Gel));
		Assert.All(Enumerable.Range(1000, 33), t => Assert.Equal(1, g.Count(t)));
	}

	/// <summary>Measured run S11: 50 kinds of item each worth a little, and no slot for the bow for 167 heartbeats.</summary>
	[Fact]
	public void AFullBag_WithNothingWorthless_TrashesTheCheapestUnneededStack()
	{
		FakeGame g = EarlyGameTests.Field(80);
		BotContext ctx = g.CreateContext(output);
		const int pumpkin = 1725, gem = 181, copperShortsword = 3507;
		g.Items.Clear();
		g.Items[copperShortsword] = 1; // a weapon: kept, cheap as it is
		g.Gear.Add(copperShortsword);
		for (int i = 0; i < 9; i++)
			g.Items[2000 + i] = 1; // the rest
		foreach (int i in Enumerable.Range(2000, 9))
			g.Values[i] = 1000;
		g.Items[FakeGame.IronBar] = 7; // for the bow: kept
		g.Items[FakeGame.IronOre] = 2; // for bars: kept
		g.Items[pumpkin] = 15;
		g.Items[gem] = 6;
		for (int i = 0; i < 36; i++)
			g.Items[3000 + i] = 1;
		g.Values[copperShortsword] = 10;
		g.Values[FakeGame.IronBar] = 1;
		g.Values[FakeGame.IronOre] = 1;
		g.Values[pumpkin] = 2; // 30 in all
		g.Values[gem] = 375;
		foreach (int i in Enumerable.Range(3000, 36))
			g.Values[i] = 100;
		g.SlotLimit = 50;
		Assert.Equal(0, g.FreeSlots);
		BotBrain.KeepRoom(ctx);
		output.WriteLine($"trashed: {string.Join(", ", g.Trashed)}; free {g.FreeSlots}");
		Assert.True(g.FreeSlots >= BotBrain.MinFreeSlots);
		Assert.Equal(pumpkin, g.Trashed[0]);
		Assert.Equal(1, g.Count(copperShortsword));
		Assert.Equal(7, g.Count(FakeGame.IronBar));
		Assert.Equal(2, g.Count(FakeGame.IronOre));
	}

	[Fact]
	public void LockedChest_IsLeftAlone()
	{
		var (g, _) = RunAuto(WithChest(30, locked: true), 60 * 10);
		Assert.Equal(0, g.Count(FakeGame.LesserHealingPotion));
	}

	[Fact]
	public void UnseenChest_IsNotKnown()
	{
		FakeGame g = WithChest(60, seen: false);
		BotContext ctx = g.CreateContext(output);
		Assert.Null(ProgressionPlanner.NextChest(ctx));
	}

	[Fact]
	public void ChestFarBelowTheDepthLimit_WaitsForArmour()
	{
		// A chest 22 rows into the caverns drew the bot down in four runs (world_eye runs 35-38).
		FakeGame g = WithChest(30);
		BotContext ctx = g.CreateContext(output);
		Assert.NotNull(ProgressionPlanner.NextChest(ctx));
		ctx.CavernY = 23 - ProgressionPlanner.ChestDepthMargin - 1 - BotContext.ShallowMargin; // the chest (row 23) 1 row too deep
		Assert.Null(ProgressionPlanner.NextChest(ctx));
		ctx.Combat.Defense = BotContext.CavernDefense; // armour: the limit goes down
		Assert.NotNull(ProgressionPlanner.NextChest(ctx));
	}

	[Fact]
	public void LootChestSkill_WalksUpAndLoots()
	{
		FakeGame g = WithChest(25);
		BotContext ctx = g.CreateContext(output);
		var loot = new LootChestSkill(new TilePos(25, 23));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, loot, 60 * 20));
		output.WriteLine(loot.Loot);
		Assert.Contains($"item{FakeGame.HermesBoots}", loot.Loot);
	}
}

/// <summary>Life crystals are mined and used on sight, whatever stage the plan is at.</summary>
public class LifeCrystalOnSightTests
{
	private readonly ITestOutputHelper output;
	public LifeCrystalOnSightTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void SeenCrystal_IsMinedAndUsed_DuringTheEarlyPlan()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(20, 29);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early); // the early plan has no life stage at all
		for (int t = 0; t < 60 * 90 && g.LifeMax == 100; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"max life {g.LifeMax}; {brain.Describe()}");
		Assert.Equal(120, g.LifeMax);
	}

	[Fact]
	public void UnseenCrystal_IsNotKnown()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(20, 29);
		g.Unrevealed.Add(new TilePos(20, 29));
		g.Unrevealed.Add(new TilePos(21, 29));
		g.Unrevealed.Add(new TilePos(20, 30));
		g.Unrevealed.Add(new TilePos(21, 30));
		BotContext ctx = g.CreateContext(output);
		Assert.Null(ProgressionPlanner.SeenLifeCrystal(ctx));
	}
}
