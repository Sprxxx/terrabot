using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Errands;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The errand layer (docs ERRAND_DESIGN.md, migration steps 1 and 3): the Underworld's access errand proposed by the Wall of
/// Flesh plan under <see cref="ProgressionPlanner.Schedulers.Errands"/>, run by the brain in <see cref="HellTests.Hell(int, int, int, int, bool)"/>'s
/// world (the house at x 30, the preferred column 60 east, the tower's roof under it on the bridge's row). What the design asks
/// of it: one instance owns the bot to the row, the surface's stages are never asked again, a near chest is taken and a far one
/// left, consumption resupplies only the remaining demand, a death leaves it in place, failures change the column once and then
/// block it with the reason, and it survives a save.
/// </summary>
public class ErrandTests
{
	private readonly ITestOutputHelper output;
	public ErrandTests(ITestOutputHelper output) => this.output = output;

	private const int SpawnX = 30, TowerFrom = 84, TowerTo = 96, Bridge = 60;
	private const int Preferred = SpawnX + ProgressionPlanner.ShaftOffsetTiles;
	private const int Row = HellTests.BridgeRow, StandRow = Row - 1;

	/// <summary>The Wall's plan after the Eye, every stage before the Underworld met (as WallOfFleshPlanTests has it).</summary>
	private (FakeGame, BotContext, BotBrain) AfterTheEye()
	{
		FakeGame g = HellTests.Hell(320, SpawnX, TowerFrom, TowerTo);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 7;
		ctx.Combat.HasRanged = true;
		ctx.SurfaceY = HellTests.SurfaceFeet + 3;
		ctx.CavernY = HellTests.SurfaceFeet + 25;
		ctx.UnderworldRow = Row - ProgressionPlanner.BridgeRowsUnderTop;
		ctx.SpawnTile = new TilePos(SpawnX, HellTests.SurfaceFeet);
		g.Blocks.TicksPerBlock = 4;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.NightmarePickaxe] = 1; // the expedition's prerequisite (StageTable.WallOfFlesh)
		g.Stations[FakeGame.TileBench] = new TilePos(SpawnX + 2, HellTests.SurfaceFeet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(SpawnX + 5, HellTests.SurfaceFeet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(SpawnX - 2, HellTests.SurfaceFeet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow })
			g.Items[item] = 1;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.MaxCrystalLife;
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		g.AliveTownNpcs.Add(FakeGame.GuideNpc);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.WallOfFlesh);
		Assert.Equal(ProgressionPlanner.Schedulers.Errands, brain.Planner.Scheduler);
		brain.Planner.BridgeLength = Bridge;
		BuildingTests.HouseAlreadyThere(g, brain.Planner, ctx);
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.WofArrows;
		g.Items[FakeGame.UnholyArrow] = ProgressionPlanner.WofUnholyArrows;
		g.Items[FakeGame.HealingPotion] = ProgressionPlanner.WofPotions;
		g.Items[FakeGame.Torch] = 50;
		g.Items[FakeGame.StoneBlock] = Bridge + ProgressionPlanner.BridgeBlockMargin + 40;
		g.Refresh(ctx);
		return (g, ctx, brain);
	}

	private static TestLog Log(BotContext ctx) => (TestLog)ctx.Log;

	/// <summary>Ticks the brain until <paramref name="until"/> holds or the ticks run out; returns the ticks spent.</summary>
	private static int Run(FakeGame g, BotContext ctx, BotBrain brain, int maxTicks, System.Func<bool> until)
	{
		int t = 0;
		for (; t < maxTicks && !until(); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		return t;
	}

	private static void Die(FakeGame g, BotContext ctx)
	{
		ctx.Deaths++;
		g.Sim.State = new BodyState { X = Body.CanonicalLeft(SpawnX, ctx.Physics), Y = Body.StandingTop(HellTests.SurfaceFeet, ctx.Physics), ReleaseJump = true };
	}

	[Fact]
	public void ThePlan_ProposesTheWallOfFleshErrand_AtTheUnderworldStage_WithTheRowsPackingList()
	{
		var (_, ctx, brain) = AfterTheEye();
		ProgressionPlanner p = brain.Planner;
		Assert.Null(p.Next(ctx));
		var access = Assert.IsType<WallOfFleshErrand>(p.Proposed);
		Assert.Contains("the errand boss:WallOfFlesh", p.Status);
		Assert.Equal(StandRow, access.StandRow);
		Assert.Equal(Preferred, access.Preferred);
		Assert.Equal(Errand.PreparePhase, access.Phase);
		Assert.Contains(access.Packing, n => n is HaveItem { Count: ProgressionPlanner.WofFightArrows, Label: "arrows for the fight" });
		Assert.Contains(access.Packing, n => n is BridgeBlocks);
		Assert.Contains(access.Reserve, n => n is HaveItem { Label: "healing potions" });
		Assert.Same(access, p.TakeProposed());
		Assert.Null(p.Proposed);
	}

	[Fact]
	public void TheColumn_IsChosenFromTheKnownTerrain_AwayFromLava()
	{
		var (g, ctx, _) = AfterTheEye();
		// Lava beside the preferred column's way down: a player who can see it digs a few columns over.
		for (int y = 30; y <= 34; y++)
			g.Grid.Set(Preferred + 2, y, CellKind.Lava);
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		access.Begin(ctx);
		output.WriteLine(Log(ctx).Lines.Last(l => l.Contains("shaft's column")));
		Assert.NotInRange(access.ShaftX, Preferred, Preferred + 3); // x 92 would be beside or in the shaft's two columns
		Assert.InRange(access.ShaftX, Preferred - 4, Preferred + 4); // the nearest clear column, not a far one
		Assert.Equal($"the shaft at x {access.ShaftX}", access.Identity);
		ShaftSite.Count(g.Grid, access.ShaftX, HellTests.SurfaceFeet, StandRow, out int lava, out _, out _);
		Assert.Equal(0, lava);
	}

	[Fact]
	public void TheColumn_AvoidsSand_TheUndergroundDesert()
	{
		var (g, ctx, _) = AfterTheEye();
		// A desert under the preferred column (E1, 2026-10-17: antlions and a tomb crawler at rows 490-560 of x 2260).
		for (int y = 30; y <= 60; y++)
			for (int x = Preferred - 6; x <= Preferred + 8; x++)
				g.Grid.Set(x, y, CellKind.Solid | CellKind.Falling);
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		access.Begin(ctx);
		output.WriteLine(Log(ctx).Lines.Last(l => l.Contains("shaft's column")));
		Assert.True(access.ShaftX < Preferred - 7 || access.ShaftX > Preferred + 8, $"x {access.ShaftX} is not clear of the sand");
		ShaftSite.Count(g.Grid, access.ShaftX, HellTests.SurfaceFeet, StandRow, out _, out _, out int sand);
		Assert.Equal(0, sand);
	}

	[Fact]
	public void ThePolicy_TakesANearChest_AndLeavesAFarOne_OnceDescending()
	{
		var (_, ctx, brain) = AfterTheEye();
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		Assert.True(access.Accepts(new Opportunity(OpportunityKind.Chest, new TilePos(Preferred + 60, 50), 99999f), ctx), "the prepare phase takes everything, as the plan did");
		access.Begin(ctx);
		ProgressionPlanner p = brain.Planner;
		Assert.IsType<HellevatorSkill>(access.NextSkill(ctx, p)); // packed: descending
		Assert.Equal(WallOfFleshErrand.DescendPhase, access.Phase);
		Assert.True(access.Accepts(new Opportunity(OpportunityKind.Chest, new TilePos(Preferred + 2, 40), WallOfFleshErrand.AcceptDetourTicks - 1), ctx));
		Assert.False(access.Accepts(new Opportunity(OpportunityKind.LifeCrystal, new TilePos(Preferred + 60, 50), WallOfFleshErrand.AcceptDetourTicks + 1), ctx));
	}

	[Fact]
	public void TheDetour_CountsTheRockInTheWay()
	{
		var (g, ctx, _) = AfterTheEye();
		g.Refresh(ctx);
		TilePos n = ctx.Node;
		float open = Opportunity.Detour(ctx, new TilePos(n.X + 20, n.Y)); // along the surface: air
		float rock = Opportunity.Detour(ctx, new TilePos(n.X + 20, n.Y + 20)); // down into the stone
		output.WriteLine($"open {open:0}, through rock {rock:0}");
		Assert.True(rock > open + 20 * g.Blocks.TicksPerBlock, "the rock's mining time is in the estimate");
	}

	[Fact]
	public void TheBrain_RunsOneErrandInstance_ToTheRow_AndOnToTheBridge_WithoutAskingThePlanAgain()
	{
		var (g, ctx, brain) = AfterTheEye();
		TestLog log = Log(ctx);
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		int before = log.Lines.Count;
		int ticks = Run(g, ctx, brain, 40000, () => brain.Planner.UnderworldReachedOnce);
		output.WriteLine($"{ticks} ticks: {brain.Describe()} at {g.Sim.Node}; {brain.Planner.ErrandOutcomes.Count} outcomes");
		Assert.True(brain.Planner.UnderworldReachedOnce, "the Underworld reached");
		// The same errand goes on down there (E1, 2026-10-17: with the descent alone as the errand, the plan sent the bot 780 rows
		// back up for an NPC room the moment it stood on the row).
		Run(g, ctx, brain, 3000, () => brain.Work is HellbridgeSkill);
		Assert.Same(access, brain.Errand);
		Assert.Equal(WallOfFleshErrand.BridgePhase, access.Phase);
		Assert.IsType<HellbridgeSkill>(brain.Work);
		Assert.Empty(brain.Planner.ErrandOutcomes);
		// The plan's surface stages were never asked for on the way: no "Auto: need", no "instead of going back to".
		var planLines = log.Lines.Skip(before).Where(l => l.StartsWith("Auto: ") && (l.Contains("need ") || l.Contains("instead of going back") || l.Contains("NPC room"))).ToList();
		Assert.True(planLines.Count == 0, string.Join("\n", planLines));
		Assert.Contains(log.Lines, l => l.Contains("Errand boss:WallOfFlesh start"));
		Assert.Contains(log.Lines, l => l.Contains("on the row at") && l.Contains("the bridge next"));
		Assert.True(ctx.Reservations.Count > 0); // still held
		// The bridge is built, and the fight's hunt begins, all by the same errand.
		Run(g, ctx, brain, 40000, () => access.Phase == WallOfFleshErrand.FightPhase);
		Assert.Equal(WallOfFleshErrand.FightPhase, access.Phase);
		Assert.True(brain.Planner.HellbridgeDone, "the bridge built");
		Assert.Same(access, brain.Errand);
	}

	[Fact]
	public void AChestInTheShaftsWall_IsLooted_AndTheSameErrandGoesOn()
	{
		var (g, ctx, brain) = AfterTheEye();
		TestLog log = Log(ctx);
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 3000, () => access.Phase == WallOfFleshErrand.DescendPhase && g.Sim.Node.Y >= HellTests.SurfaceFeet + 5);
		var chest = new TilePos(access.ShaftX + 2, g.Sim.Node.Y + 12);
		g.Chests[chest] = new List<(int, int)> { (FakeGame.HealingPotion, 3) };
		int potions = g.Count(FakeGame.HealingPotion);
		Run(g, ctx, brain, 20000, () => ctx.OpenedChests.Contains(chest) || brain.Planner.UnderworldReachedOnce);
		Assert.Contains(log.Lines, l => l.Contains("taking the Chest") && l.Contains("then back to Hellevator"));
		Assert.Contains(chest, ctx.OpenedChests);
		Assert.Equal(potions + 3, g.Count(FakeGame.HealingPotion));
		Assert.Same(access, brain.Errand);
		Assert.Contains(log.Lines, l => l.Contains("back to Hellevator after lootchest"));
		Run(g, ctx, brain, 40000, () => brain.Planner.UnderworldReachedOnce);
		Assert.True(brain.Planner.UnderworldReachedOnce, brain.Describe());
	}

	[Fact]
	public void ArrowsShot_DoNotResupply_UntilTheFightsDemandIsShort()
	{
		var (g, ctx, brain) = AfterTheEye();
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 3000, () => access.Phase == WallOfFleshErrand.DescendPhase);
		ISkill shaft = brain.Work;
		Assert.IsType<HellevatorSkill>(shaft);
		Assert.Equal(ProgressionPlanner.WofFightArrows, ctx.Reservations.Reserved(FakeGame.WoodenArrow));
		// 300 arrows shot at cave bats: below the plan's 800 refill line, above the fight's 600: no resupply, the same shaft.
		g.Items[FakeGame.WoodenArrow] = 700;
		g.Items[FakeGame.UnholyArrow] = 0;
		g.Refresh(ctx);
		Assert.Same(shaft, access.NextSkill(ctx, brain.Planner));
		Assert.DoesNotContain("resupply", access.Status);
		// Short of the fight's 600 on the way down: still the shaft (the arrows are the fight phase's demand; E5 went for wood in the Underworld).
		g.Items[FakeGame.WoodenArrow] = 500;
		g.Refresh(ctx);
		Assert.Same(shaft, access.NextSkill(ctx, brain.Planner));
		Assert.DoesNotContain("resupply", access.Status);
		Assert.Contains(access.RemainingDemand(ctx), n => n is BridgeBlocks);
		Assert.DoesNotContain(access.RemainingDemand(ctx), n => n is HaveItem);
	}

	[Fact]
	public void ADeathMidDescent_KeepsTheErrand_AndItGoesBackDownToTheRow()
	{
		var (g, ctx, brain) = AfterTheEye();
		TestLog log = Log(ctx);
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 20000, () => g.Sim.Node.Y >= HellTests.SurfaceFeet + 30);
		int frontier = g.Sim.Node.Y;
		Die(g, ctx);
		Run(g, ctx, brain, 2, () => false);
		Assert.Same(access, brain.Errand);
		Assert.Equal(1, access.Deaths);
		Assert.Contains(log.Lines, l => l.Contains("Errand boss:WallOfFlesh: died in the descend; back down"));
		int before = log.Lines.Count;
		int ticks = Run(g, ctx, brain, 40000, () => brain.Planner.UnderworldReachedOnce);
		output.WriteLine($"frontier was row {frontier}; {ticks} ticks after the death: {brain.Describe()} at {g.Sim.Node}");
		Assert.True(brain.Planner.UnderworldReachedOnce, "back down to the row");
		Assert.DoesNotContain(log.Lines.Skip(before), l => l.StartsWith("Auto: ") && l.Contains("need "));
		Assert.Same(access, brain.Errand);
		Assert.Empty(brain.Planner.ErrandOutcomes);
	}

	[Fact]
	public void HellevatorFailures_ChangeTheColumnOnce_ThenBlockTheErrandWithTheReason()
	{
		var (_, ctx, brain) = AfterTheEye();
		ProgressionPlanner p = brain.Planner;
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		access.Begin(ctx);
		int first = access.ShaftX;
		ISkill shaft = access.NextSkill(ctx, p);
		Assert.IsType<HellevatorSkill>(shaft);
		for (int i = 0; i < WallOfFleshErrand.MaxFailures; i++) {
			Assert.Equal(OutcomeKind.Running, access.Check(ctx).Kind);
			access.OnSkillFinished(shaft, SkillStatus.Failed, "no way down from (90,40)", ctx);
			shaft = access.NextSkill(ctx, p);
		}
		// The method change: another column, the failed one left out, the goal kept.
		Assert.Equal(OutcomeKind.Running, access.Check(ctx).Kind);
		Assert.True(access.ColumnChanged);
		Assert.NotEqual(first, access.ShaftX);
		Assert.True(System.Math.Abs(access.ShaftX - first) > HellevatorSkill.Slack);
		Assert.Equal(0, access.Failures);
		shaft = access.NextSkill(ctx, p);
		Assert.Equal(access.ShaftX, ((HellevatorSkill)shaft).ShaftX);
		for (int i = 0; i < WallOfFleshErrand.MaxFailures; i++) {
			access.OnSkillFinished(shaft, SkillStatus.Failed, "no way to the shaft", ctx);
			shaft = access.NextSkill(ctx, p);
		}
		Outcome blocked = access.Check(ctx);
		output.WriteLine(blocked.ToString());
		Assert.Equal(OutcomeKind.Blocked, blocked.Kind);
		Assert.Contains("hellevator failures in a row", blocked.Reason);
		Assert.Equal("no way to the shaft", blocked.Evidence);
		// The plan records it and proposes the next errand elsewhere; after enough blocked columns it is stuck, not looping.
		p.OnErrandEnded(access, blocked, ctx);
		Assert.Null(p.Next(ctx));
		var next = Assert.IsType<WallOfFleshErrand>(p.TakeProposed());
		next.Begin(ctx);
		Assert.True(System.Math.Abs(next.ShaftX - access.ShaftX) > HellevatorSkill.Slack, $"not the blocked column {access.ShaftX} again: {next.ShaftX}");
		Assert.IsType<HellevatorSkill>(next.NextSkill(ctx, p)); // descending
		p.OnErrandEnded(next, Outcome.Blocked("test", ""), ctx);
		Assert.Null(p.Next(ctx));
		var third = Assert.IsType<WallOfFleshErrand>(p.TakeProposed());
		third.Begin(ctx);
		Assert.IsType<HellevatorSkill>(third.NextSkill(ctx, p));
		p.OnErrandEnded(third, Outcome.Blocked("another", ""), ctx);
		Assert.Null(p.Next(ctx));
		Assert.Null(p.Proposed);
		Assert.True(p.Stuck, p.Status);
		Assert.Contains("no way to the Underworld", p.Status);
	}

	[Fact]
	public void DeathsOnOneColumn_ChangeTheColumn_ThenBlock()
	{
		var (g, ctx, brain) = AfterTheEye();
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		access.Begin(ctx);
		int first = access.ShaftX;
		Assert.IsType<HellevatorSkill>(access.NextSkill(ctx, brain.Planner));
		for (int i = 0; i < WallOfFleshErrand.DeathsPerColumn; i++) {
			Assert.Equal(OutcomeKind.Running, access.Check(ctx).Kind);
			access.OnDeath(ctx);
		}
		Assert.Equal(OutcomeKind.Running, access.Check(ctx).Kind);
		Assert.True(access.ColumnChanged);
		Assert.NotEqual(first, access.ShaftX);
		Assert.Equal(0, access.ColumnDeaths);
		for (int i = 0; i < WallOfFleshErrand.DeathsPerColumn; i++)
			access.OnDeath(ctx);
		Outcome o = access.Check(ctx);
		Assert.Equal(OutcomeKind.Blocked, o.Kind);
		Assert.Contains("deaths on the way down", o.Reason);
	}

	/// <summary>E3 (2026-10-17): the bridge blocked, and the plan proposed the errand again "[prepare]" with a new column, the shaft dug and the bridge begun.</summary>
	[Fact]
	public void AnErrandBlockedAtTheBridge_IsProposedAgainAtTheBridge_NotFromTheTop()
	{
		var (g, ctx, brain) = AfterTheEye();
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var first = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 40000, () => first.Phase == WallOfFleshErrand.BridgePhase && brain.Work is HellbridgeSkill);
		Assert.Equal(WallOfFleshErrand.BridgePhase, first.Phase);
		int shaft = first.ShaftX;
		ProgressionPlanner p = brain.Planner;
		p.OnErrandEnded(first, Outcome.Blocked("3 bridge failures in a row", "test"), ctx);
		g.Refresh(ctx);
		Assert.Null(p.Next(ctx));
		var again = Assert.IsType<WallOfFleshErrand>(p.TakeProposed());
		Assert.Equal(WallOfFleshErrand.BridgePhase, again.Phase);
		again.Begin(ctx);
		Assert.Equal(shaft, again.ShaftX); // the dug shaft, not a new column
		// Back at the spawn (a death), with an NPC room unbuilt: the expedition is taken up again before the surface's stages (E4).
		Die(g, ctx);
		g.Refresh(ctx);
		Assert.Null(p.Next(ctx));
		Assert.IsType<WallOfFleshErrand>(p.TakeProposed());
		Assert.Contains("taken up again", p.Status);
		// And on the way back down, the watchdog sees progress (E4: the measure ran backwards and blocked the trip at row 440).
		again.OnDeath(ctx);
		float atSpawn = again.Progress(ctx);
		g.Sim.State = new BodyState { X = Body.CanonicalLeft(shaft, ctx.Physics), Y = Body.StandingTop(HellTests.SurfaceFeet + 20, ctx.Physics), ReleaseJump = true };
		g.Refresh(ctx);
		Assert.True(again.Progress(ctx) > atSpawn, "deeper is progress on the trip back");
	}

	/// <summary>E6 (2026-10-17): a 50-stack bag from the checkpoint; the expedition leaves base with room for the road.</summary>
	[Fact]
	public void TheErrand_LeavesBaseWithALeanBag()
	{
		var (g, ctx, brain) = AfterTheEye();
		g.SlotLimit = 50;
		for (int i = 0; i < 60; i++)
			g.Items[9000 + i] = 1; // junk of no value, no use
		g.Refresh(ctx);
		Assert.True(g.FreeSlots < WallOfFleshErrand.LeanFreeSlots, $"{g.FreeSlots} free to begin with");
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 3000, () => access.Phase == WallOfFleshErrand.DescendPhase);
		output.WriteLine($"{g.FreeSlots} free slots after packing, trashed {g.Trashed.Count}");
		Assert.True(g.FreeSlots >= WallOfFleshErrand.LeanFreeSlots, $"{g.FreeSlots} free slots");
		Assert.Equal(ProgressionPlanner.WofArrows, g.Count(FakeGame.WoodenArrow)); // the packing stayed
		Assert.True(g.Count(FakeGame.StoneBlock) >= Bridge + ProgressionPlanner.BridgeBlockMargin);
	}

	/// <summary>E6 (2026-10-17): the bridge stopped on hellstone brick with 10 Bombs in the bag; a player bombs it.</summary>
	[Fact]
	public void TheBridge_StoppedByAHotWall_IsBlastedThrough_ThenGoesOn()
	{
		var (g, ctx, brain) = AfterTheEye();
		g.Items[FakeGame.Bomb] = 3;
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 40000, () => access.Phase == WallOfFleshErrand.BridgePhase && brain.Work is HellbridgeSkill);
		// A hot wall across the bridge's way, three rows high, a few columns ahead.
		int wallX = g.Sim.Node.X + 6;
		for (int y = Row - 3; y <= Row - 1; y++) {
			g.Grid.Set(wallX, y, CellKind.Solid | CellKind.Hazard);
			g.Blocks.Unbreakable.Add(new TilePos(wallX, y)); // hellstone brick: pick-gated at 65
		}
		g.Refresh(ctx);
		Run(g, ctx, brain, 6000, () => brain.Work is BlastOutSkill || brain.Errand == null);
		Assert.Same(access, brain.Errand);
		Assert.IsType<BlastOutSkill>(brain.Work);
		Assert.Equal(1, access.Blasts);
		Assert.Contains(Log(ctx).Lines, l => l.Contains("a blast through it next"));
		Assert.Equal(0, access.PhaseFailures);
	}

	/// <summary>
	/// E6 (2026-10-17) and the user: "You could just use a pickaxe that can mine hellstone?" Without the Nightmare Pickaxe and with its
	/// scales trashed, the expedition's prerequisite has the Eater of Worlds fought again for them before the packing.
	/// </summary>
	[Fact]
	public void WithoutAHellstonePickaxe_ThePrerequisite_FightsTheEaterAgainForScales_BeforePacking()
	{
		var (g, ctx, brain) = AfterTheEye();
		g.Items.Remove(FakeGame.NightmarePickaxe);
		g.Items[FakeGame.DemonBow] = 1; // the plan's evil bow stage met
		g.Items[FakeGame.DemoniteBar] = 12; // the bars in hand: the scales are the first unmet ingredient
		g.Items[FakeGame.WormFood] = 1;
		g.Items[FakeGame.ShadowScale] = 0;
		g.BossesDefeated.Add(FakeGame.EowHead);
		g.Refresh(ctx);
		ProgressionPlanner p = brain.Planner;
		Assert.Null(p.Next(ctx));
		var errand = Assert.IsType<WallOfFleshErrand>(p.TakeProposed());
		errand.Begin(ctx);
		ISkill first = errand.NextSkill(ctx, p);
		output.WriteLine($"{errand.Status} | {p.Status} -> {first?.Describe()}");
		Assert.NotNull(first);
		Assert.StartsWith("prerequisite", errand.Status);
		Assert.Equal(Errand.PreparePhase, errand.Phase);
		Assert.True(p.Status.Contains("Eater of Worlds") || p.Status.Contains("Shadow Scale") || p.Status.Contains("corruption", System.StringComparison.OrdinalIgnoreCase), p.Status);
		// The scales in the bag: the pickaxe is crafted (bars from the ore at the furnace, then the anvil), no fight.
		g.Items[FakeGame.ShadowScale] = 6;
		g.Refresh(ctx);
		ISkill next = errand.NextSkill(ctx, p);
		output.WriteLine($"{errand.Status} | {p.Status} -> {next?.Describe()}");
		Assert.NotNull(next);
		Assert.DoesNotContain("Eater", p.Status);
	}

	[Fact]
	public void NoProgress_ForTheWatchdogsWindow_BlocksTheErrand()
	{
		var (_, ctx, brain) = AfterTheEye();
		var access = new WallOfFleshErrand(Preferred, StandRow, ctx.Ids);
		access.Begin(ctx);
		Assert.IsType<HellevatorSkill>(access.NextSkill(ctx, brain.Planner)); // descending from here
		access.Rules(ctx);
		Assert.Equal(OutcomeKind.Running, access.Check(ctx).Kind);
		ctx.Tick += access.NoProgressTicks + 1;
		Outcome o = access.Check(ctx);
		Assert.Equal(OutcomeKind.Blocked, o.Kind);
		Assert.Contains("no progress", o.Reason);
	}

	[Fact]
	public void TheErrand_SurvivesASaveAndALoad_AndGoesOnUnderItsPlan()
	{
		var (g, ctx, brain) = AfterTheEye();
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 20000, () => g.Sim.Node.Y >= HellTests.SurfaceFeet + 25);
		Dictionary<string, string> saved = access.Save();
		output.WriteLine(string.Join(", ", saved.Select(kv => kv.Key + "=" + kv.Value)));
		var loaded = Assert.IsType<WallOfFleshErrand>(ErrandFactory.Create(WallOfFleshErrand.ErrandId, saved, ctx.Ids));
		Assert.True(loaded.Restored);
		Assert.Equal(access.ShaftX, loaded.ShaftX);
		Assert.Equal(access.Phase, loaded.Phase);
		Assert.Equal(access.StandRow, loaded.StandRow);
		Assert.Equal(access.Identity, loaded.Identity);
		// A new brain (the game started again, the player loaded): auto mode under the same plan takes the errand up again.
		var again = new BotBrain();
		again.RestoreErrand(loaded);
		again.SetAuto(true, ctx, ProgressionPlanner.Plans.WallOfFlesh);
		Assert.Same(loaded, again.Errand);
		Assert.Equal(ProgressionPlanner.WofFightArrows, ctx.Reservations.Reserved(FakeGame.WoodenArrow));
		Assert.Equal(loaded.ShaftX, again.Planner.ShaftX);
		Assert.Contains(Log(ctx).Lines, l => l.Contains("goes on from the save"));
		// Under another plan it is over.
		var other = new BotBrain();
		other.RestoreErrand(ErrandFactory.Create(WallOfFleshErrand.ErrandId, saved, ctx.Ids));
		other.SetAuto(true, ctx, ProgressionPlanner.Plans.Boss);
		Assert.Null(other.Errand);
	}

	[Fact]
	public void TheReservations_KeepTheBagsHabitsOffThePackingList()
	{
		var (g, ctx, brain) = AfterTheEye();
		Run(g, ctx, brain, 10, () => brain.Errand != null);
		var access = Assert.IsType<WallOfFleshErrand>(brain.Errand);
		Run(g, ctx, brain, 3000, () => access.Phase == WallOfFleshErrand.DescendPhase);
		Assert.True(ctx.Reservations.Count > 0);
		HashSet<int> kept = BotBrain.KeptItems(ctx, g.Stacks());
		Assert.Contains(FakeGame.WoodenArrow, kept);
		Assert.Contains(FakeGame.StoneBlock, kept);
		Assert.Contains(FakeGame.HealingPotion, kept);
		Assert.Equal(g.Count(FakeGame.WoodenArrow) - ProgressionPlanner.WofFightArrows, ctx.Spare(FakeGame.WoodenArrow));
		Assert.Equal(0, ctx.Spare(FakeGame.HealingPotion)); // all kept back
	}
}
