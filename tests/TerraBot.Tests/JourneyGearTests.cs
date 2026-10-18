using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The user (2026-10-17): "make sure Journey mode is considered and that we research and use everything". Researched gear is a
/// click in the duplication menu away: the best upgrade over what is worn and carried is duplicated and worn, and a researched
/// fight potion is duplicated before the summon and drunk.
/// </summary>
public class JourneyGearTests
{
	private readonly ITestOutputHelper output;
	public JourneyGearTests(ITestOutputHelper output) => this.output = output;

	private const int Shield = 156, Stool = 4341, MoltenHelmet = 231, Ironskin = 292, IronskinBuff = 5;

	private (FakeGame, BotContext, BotBrain) Journey()
	{
		FakeGame g = EarlyGameTests.Field(60);
		g.Journey.On = true;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		return (g, ctx, brain);
	}

	private static void Run(FakeGame g, BotContext ctx, BotBrain brain, int ticks)
	{
		for (int t = 0; t < ticks; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
	}

	[Fact]
	public void TheBestResearchedGear_IsDuplicated_AndWorn()
	{
		var (g, ctx, brain) = Journey();
		TestLog log = (TestLog)ctx.Log;
		g.Accessories.UnionWith(new[] { Shield, Stool });
		g.AccessoryValues[Shield] = 8f;
		g.AccessoryValues[Stool] = 0f;
		g.StackSize[Shield] = 1;
		g.StackSize[Stool] = 1;
		g.Armor[FakeGame.IronHelmet] = (ArmorSlot.Head, 2);
		g.Armor[MoltenHelmet] = (ArmorSlot.Head, 8);
		g.StackSize[MoltenHelmet] = 1;
		g.Equipped.Add(FakeGame.IronHelmet);
		foreach (int t in new[] { Shield, Stool, MoltenHelmet }) {
			g.Journey.Caps[t] = 1;
			g.Journey.Done[t] = 1;
		}
		Run(g, ctx, brain, 60 * 5);
		foreach (string l in log.Lines.Where(l => l.StartsWith("Journey: duplicated") || l.StartsWith("Gear:")))
			output.WriteLine(l);
		Assert.Contains(MoltenHelmet, g.Equipped);
		Assert.DoesNotContain(FakeGame.IronHelmet, g.Equipped);
		Assert.Contains(Shield, g.Equipped);
		Assert.DoesNotContain(Stool, g.Equipped); // worth nothing worn: not duplicated either
		Assert.DoesNotContain(g.Journey.Duplicated, d => d.Type == Stool);
		// Once had, not duplicated again.
		int dups = g.Journey.Duplicated.Count;
		Run(g, ctx, brain, 60 * 3);
		Assert.Equal(dups, g.Journey.Duplicated.Count);
	}

	[Fact]
	public void BeforeTheSummon_AResearchedPotionNotCarried_IsDuplicatedAndDrunk()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Journey.On = true;
		BotContext ctx = g.CreateContext(output);
		g.BuffItems[Ironskin] = IronskinBuff;
		g.Journey.Caps[Ironskin] = 20;
		g.Journey.Done[Ironskin] = 20;
		g.StackSize[Ironskin] = 30;
		var ready = new ReadyForBossSkill();
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, ready, 300));
		Assert.Contains(g.Journey.Duplicated, d => d.Type == Ironskin);
		Assert.Contains(IronskinBuff, g.ActiveBuffs);
		Assert.Equal(1, ready.Used);
	}

	[Fact]
	public void OnClassic_NothingIsDuplicated()
	{
		var (g, ctx, brain) = Journey();
		g.Journey.On = false;
		g.Accessories.Add(Shield);
		g.Journey.Caps[Shield] = 1;
		g.Journey.Done[Shield] = 1;
		Run(g, ctx, brain, 60 * 3);
		Assert.Empty(g.Journey.Duplicated);
		Assert.False(JourneyGear.Tick(ctx));
	}

	/// <summary>
	/// The user (2026-10-18): "Researched blocks that are not needed get trashed to make room for items that are NOT researched. We
	/// should really only need one stack of building blocks." One stack of one researched block, one stack of each other researched
	/// item, one of each researched thing that does not stack; nothing unresearched is touched.
	/// </summary>
	[Fact]
	public void ResearchedSpares_AreTrashed_OneStackOfOneBlockKept()
	{
		var (g, ctx, _) = Journey();
		const int Pick = 1;
		g.StackSize[Pick] = 1;
		g.Journey.MarkResearched(FakeGame.StoneBlock, FakeGame.DirtBlock, FakeGame.Wood, Pick);
		g.Items[FakeGame.StoneBlock] = 2500; // three stacks
		g.Items[FakeGame.DirtBlock] = 500;
		g.Items[FakeGame.Wood] = 1200;       // two stacks
		g.Items[Pick] = 2;                   // two pickaxes
		g.Items[FakeGame.IronOre] = 1500;    // not researched: both stacks stay
		g.Refresh(ctx);
		int trashed = ResearchHabit.TrashSpares(ctx);
		output.WriteLine($"trashed {trashed}: stone {g.Count(FakeGame.StoneBlock)}, dirt {g.Count(FakeGame.DirtBlock)}, wood {g.Count(FakeGame.Wood)}, picks {g.Count(Pick)}, ore {g.Count(FakeGame.IronOre)}");
		Assert.Equal(999, g.Count(FakeGame.StoneBlock)); // the biggest block, one stack
		Assert.Equal(0, g.Count(FakeGame.DirtBlock));     // another block: none
		Assert.Equal(999, g.Count(FakeGame.Wood));
		Assert.Equal(1, g.Count(Pick));
		Assert.Equal(1500, g.Count(FakeGame.IronOre));
		// Classic: nothing is touched.
		g.Journey.On = false;
		g.Items[FakeGame.Wood] = 1200;
		Assert.Equal(0, ResearchHabit.TrashSpares(ctx));
		Assert.Equal(1200, g.Count(FakeGame.Wood));
	}
}
