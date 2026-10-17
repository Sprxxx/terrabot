using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The reflex table (docs ERRAND_DESIGN.md section 5, migration step 2): the survival responses by bid. A higher bid replaces a
/// running reflex, a lower one waits for it: surfacing outbids a fight, a fight never cancels a surfacing, a retreat never cancels
/// the Dungeon Guardian's recall.
/// </summary>
public class ReflexTests
{
	private readonly ITestOutputHelper output;
	public ReflexTests(ITestOutputHelper output) => this.output = output;

	private (FakeGame, BotContext, BotBrain) Field()
	{
		FakeGame g = EarlyGameTests.Field(80, 70);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.WeaponDamage = 12;
		g.Items[FakeGame.WoodenSword] = 1;
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		g.Refresh(ctx);
		return (g, ctx, brain);
	}

	private static void Tick(FakeGame g, BotContext ctx, BotBrain brain, int n = 1)
	{
		for (int i = 0; i < n; i++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
	}

	[Fact]
	public void Surfacing_OutbidsAFight_AndAFight_NeverCancelsSurfacing()
	{
		var (g, ctx, brain) = Field();
		TilePos n = g.Sim.Node;
		g.AddSlime(1, n.X + 3, n.Y);
		Tick(g, ctx, brain, 2);
		Assert.IsType<FightSkill>(brain.Override);
		// The head under water with little breath: surfacing takes over from the fight.
		for (int y = n.Y - 3; y <= n.Y; y++)
			for (int x = n.X - 1; x <= n.X + 2; x++)
				g.Grid.Set(x, y, CellKind.Water);
		ctx.Combat.Breath = 60;
		Tick(g, ctx, brain, 1);
		Assert.IsType<SurfaceSkill>(brain.Override);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("surfacing (pausing Fight)"));
		// The slime still beside: the fight does not take the surfacing back.
		ctx.Combat.Breath = 50;
		Tick(g, ctx, brain, 3);
		Assert.IsType<SurfaceSkill>(brain.Override);
	}

	[Fact]
	public void ARetreat_DoesNotCancelTheGuardiansRecall()
	{
		var (g, ctx, brain) = Field();
		TilePos n = g.Sim.Node;
		g.Items[FakeGame.RecallPotion] = 1;
		g.ExtraHostiles.Add(new EntityInfo(7, FakeGame.GuardianNpc, "Dungeon Guardian", (n.X + 12) * 16f, n.Y * 16f, 32, 32, 0, 0, 9999, 9999, 1000));
		Tick(g, ctx, brain, 2);
		var recall = Assert.IsType<UseItemSkill>(brain.Override);
		Assert.Equal(FakeGame.RecallPotion, recall.ItemType);
		// Hurt badly with a slime at the door and no potion to drink: the retreat's gate opens, but its bid is under the recall's.
		g.AddSlime(1, n.X + 2, n.Y);
		ctx.Combat.Life = 20;
		ctx.Combat.LifeMax = 200;
		ctx.Combat.CanQuickHeal = false;
		Tick(g, ctx, brain, 2);
		Assert.Same(recall, brain.Override);
		Assert.DoesNotContain(((TestLog)ctx.Log).Lines, l => l.Contains("retreating"));
	}
}
