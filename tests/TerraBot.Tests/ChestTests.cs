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
