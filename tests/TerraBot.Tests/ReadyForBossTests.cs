using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// world_wof E26 (2026-10-17): the Wall fought with no buff, no food and no minion, Burger and Steak in the bag. The user: "We should
/// also make sure to do whatever possible to make bosses easier. Just because we use a bow or a melee weapon doesnt mean we cant use
/// a summon." Before the summon, the bag's fight potions and food are used and the staves' minions summoned.
/// </summary>
public class ReadyForBossTests
{
	private readonly ITestOutputHelper output;
	public ReadyForBossTests(ITestOutputHelper output) => this.output = output;

	private const int Ironskin = 292, Regeneration = 289, Burger = 5042, ImpStaff = 1157, FinchStaff = 4281;
	private const int IronskinBuff = 5, RegenerationBuff = 2, WellFed2 = 206;

	[Fact]
	public void BeforeTheSummon_ThePotionsAndFoodAreUsed_AndTheStavesMinionsSummoned()
	{
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		g.BuffItems[Ironskin] = IronskinBuff;
		g.BuffItems[Regeneration] = RegenerationBuff;
		g.BuffItems[Burger] = WellFed2;
		g.Items[Ironskin] = 3;
		g.Items[Regeneration] = 2;
		g.Items[Burger] = 1;
		g.ActiveBuffs.Add(RegenerationBuff); // on already: not drunk again
		g.Staves.Add((ImpStaff, 10, 1f));
		g.Staves.Add((FinchStaff, 10, 1f));
		g.Items[ImpStaff] = 1;
		g.Items[FinchStaff] = 1;
		g.MaxMinions = 2; // a Summoning Potion's slot
		var ready = new ReadyForBossSkill();
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, ready, 600));
		output.WriteLine($"used {ready.Used}, summoned {ready.Summoned}; minions {g.Minions}, mana {g.Mana}");
		Assert.Equal(2, g.Count(Ironskin));
		Assert.Equal(2, g.Count(Regeneration));
		Assert.Equal(0, g.Count(Burger));
		Assert.Contains(IronskinBuff, g.ActiveBuffs);
		Assert.Contains(WellFed2, g.ActiveBuffs);
		Assert.Equal(2, g.Minions);
		Assert.Equal(0, g.Mana);
		Assert.NotNull(g.LastMinionMouse); // aimed beside the body, where the minion is to come
		Assert.Equal(2, ready.Used);
		Assert.Equal(2, ready.Summoned);
	}

	[Fact]
	public void WithNothingToUse_ItIsReadyAtOnce()
	{
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var ready = new ReadyForBossSkill();
		ready.Start(ctx);
		Assert.Equal(SkillStatus.Succeeded, ready.Tick(ctx));
	}

	[Fact]
	public void NoManaOrNoSlot_NoSummon_AndTheSkillStillEnds()
	{
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		g.Staves.Add((ImpStaff, 10, 1f));
		g.Items[ImpStaff] = 1;
		g.Mana = 5;
		var ready = new ReadyForBossSkill();
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, ready, 200));
		Assert.Equal(0, g.Minions);
		Assert.Equal(0, ready.Summoned);
	}

	/// <summary>Review (2026-10-17): the boss fight runs Ready before its summon (not only Ready on its own); a hostile near cuts it short.</summary>
	[Fact]
	public void TheBossFight_GetsReadyBeforeItSummons()
	{
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.BuffItems[Ironskin] = IronskinBuff;
		g.Items[Ironskin] = 1;
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc);
		fight.Start(ctx);
		int eyeWhenDrunk = -1;
		for (int t = 0; t < 200 && eyeWhenDrunk < 0; t++) {
			g.Refresh(ctx);
			fight.Tick(ctx);
			g.Step(ctx);
			if (g.Count(Ironskin) == 0)
				eyeWhenDrunk = g.Count(FakeGame.SuspiciousEye);
		}
		Assert.Equal(1, eyeWhenDrunk); // drunk while the summon item was still in the bag
		Assert.Contains(IronskinBuff, g.ActiveBuffs);
	}

	[Fact]
	public void AHostileNear_CutsTheReadyingShort()
	{
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		g.BuffItems[Ironskin] = IronskinBuff;
		g.Items[Ironskin] = 1;
		g.Refresh(ctx);
		g.ExtraHostiles.Add(new EntityInfo(9, 1, "Slime", ctx.Body.CenterX(ctx.Physics) + 5 * 16f, ctx.Body.CenterY(ctx.Physics), 24, 24, 0, 0, 40, 40, 7, 1));
		g.Refresh(ctx);
		var ready = new ReadyForBossSkill();
		ready.Start(ctx);
		Assert.Equal(SkillStatus.Succeeded, ready.Tick(ctx));
		Assert.Equal(1, g.Count(Ironskin));
	}
}
