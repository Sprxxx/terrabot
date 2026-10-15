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
