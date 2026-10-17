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

	/// <summary>world_wof E1 (2026-10-17): "Peppe got melted" at (2237,842) digging in to recover, lava beside the hole.</summary>
	[Fact]
	public void AShelter_DoesNotDigIn_BesideLava()
	{
		// A deeper field than Field(): 40 rows of stone under the surface at row 9, the body at x 20.
		var rows = new string[50];
		for (int y = 0; y < rows.Length; y++)
			rows[y] = new string(y < 10 ? '.' : 's', 40);
		rows[9] = rows[9].Remove(20, 1).Insert(20, "S");
		var g = new FakeGame(rows);
		BotContext ctx = g.CreateContext(output);
		TilePos n = g.Sim.Node;
		int ground = n.Y + 1;
		// Lava beside the column under the feet, and under the column two to the right.
		g.Grid.Set(n.X + 2, ground + 2, CellKind.Lava);
		g.Grid.Set(n.X + 3, ground + ShelterSkill.Depth, CellKind.Lava);
		g.Refresh(ctx);
		TilePos target = ShelterSkill.DigTarget(ctx, n);
		output.WriteLine($"dig in at {target} (body {n})");
		Assert.True(target.X + 1 < n.X + 2 - 1 || target.X > n.X + 3, $"column {target.X} has lava beside or under it");
		for (int y = ground; y <= ground + ShelterSkill.Depth + 1; y++)
			for (int x = target.X - 1; x <= target.X + 2; x++)
				Assert.False(g.Grid.Get(x, y).HasLava(), $"lava at ({x},{y}) by the hole");
	}

	/// <summary>E6 (2026-10-17): mined life crystals left on the floor, the bag full of kept stacks; a player drops junk for a crystal.</summary>
	[Fact]
	public void ACollect_MakesRoomForALifeCrystal_InAFullBag()
	{
		FakeGame g = EarlyGameTests.Field(80, 70);
		BotContext ctx = g.CreateContext(output);
		g.SlotLimit = 50;
		for (int i = 0; i < 50; i++)
			g.Items[9000 + i] = 1; // junk of no value
		TilePos n = g.Sim.Node;
		g.Drops.Add(new DropInfo(1, FakeGame.LifeCrystal, "Life Crystal", 1, (n.X + 2) * 16f, n.Y * 16f, 14, 26));
		g.Refresh(ctx);
		Assert.Equal(0, g.FreeSlots);
		var collect = new CollectDropsSkill(d => d.Type == FakeGame.LifeCrystal, 10);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, collect, 600);
		output.WriteLine($"{s}: crystals {g.Count(FakeGame.LifeCrystal)}, trashed {g.Trashed.Count}");
		Assert.Equal(1, g.Count(FakeGame.LifeCrystal));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("made room for the Life Crystal"));
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
