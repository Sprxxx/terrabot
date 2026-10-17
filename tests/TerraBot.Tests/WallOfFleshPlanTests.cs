using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 12 (docs M12_SPEC.md section 9): the Wall of Flesh plan ("wof"), in <see cref="HellTests.Hell(int, int, int, int, bool)"/>'s
/// world: the house by the spawn at x 30, the shaft's column 60 tiles east (x 90) over the tower's roof (x 84-96, row 80), the
/// open band over a lava sea; the Underworld's first row 42 over the bridge's.
/// </summary>
public class WallOfFleshPlanTests
{
	private readonly ITestOutputHelper output;
	public WallOfFleshPlanTests(ITestOutputHelper output) => this.output = output;

	private const int SpawnX = 30, TowerFrom = 84, TowerTo = 96, Bridge = 60;
	private const int Row = HellTests.BridgeRow;

	private (FakeGame, BotContext, ProgressionPlanner) AfterTheEye(bool guide = true, bool crimson = false)
	{
		FakeGame g = HellTests.Hell(320, SpawnX, TowerFrom, TowerTo);
		if (crimson)
			g.UseCrimson();
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 7; // under CavernDefense: the gear's depth limit is a few rows down (BotContext.DeepestRow)
		ctx.Combat.HasRanged = true;
		ctx.SurfaceY = HellTests.SurfaceFeet + 3;
		ctx.CavernY = HellTests.SurfaceFeet + 25;
		ctx.UnderworldRow = Row - ProgressionPlanner.BridgeRowsUnderTop;
		ctx.SpawnTile = new TilePos(SpawnX, HellTests.SurfaceFeet);
		g.Blocks.TicksPerBlock = 4;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Stations[FakeGame.TileBench] = new TilePos(SpawnX + 2, HellTests.SurfaceFeet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(SpawnX + 5, HellTests.SurfaceFeet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(SpawnX - 2, HellTests.SurfaceFeet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow })
			g.Items[item] = 1;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.MaxCrystalLife; // the Wall wants the most crystals give (400)
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		if (guide)
			g.AliveTownNpcs.Add(FakeGame.GuideNpc);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.WallOfFlesh);
		p.BridgeLength = Bridge;
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.WofArrows;
		g.Items[FakeGame.UnholyArrow] = ProgressionPlanner.WofUnholyArrows;
		g.Items[FakeGame.HealingPotion] = ProgressionPlanner.WofPotions;
		g.Items[FakeGame.StoneBlock] = Bridge + ProgressionPlanner.BridgeBlockMargin + 40;
		g.Refresh(ctx);
		return (g, ctx, p);
	}

	private ISkill Next(ProgressionPlanner p, BotContext ctx)
	{
		ISkill s = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void TheWofPlan_IsTheBossPlan_ThenTheSpecsStages()
	{
		var boss = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids).ToList();
		var wof = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.WallOfFlesh, FakeGame.Ids).ToList();
		Assert.Contains(ProgressionPlanner.Plans.WallOfFlesh, ProgressionPlanner.Plans.All);
		Assert.Equal(boss.Select(n => (n.GetType(), n.Label)), wof.Take(boss.Count).Select(n => (n.GetType(), n.Label)));
		Assert.DoesNotContain(wof, n => n is BossDefeated { Npc: FakeGame.EowHead } or BossDefeated { Npc: FakeGame.SkeletronHeadNpc });
		Assert.Equal(new[] { "bow", "evil bow", "evil pickaxe", "unholy arrows", "unholy arrows for the wall", "arrows", "max life", "max life for the wall", "healing potions",
			"bridge blocks", "the Guide", "the Underworld", "hellbridge", "Wall of Flesh", "the Wall of Flesh's loot" }, wof.Skip(boss.Count).Select(n => n.Label));
		// The surface's work only before the descent: the evil bow and pickaxe from the bag, the unholy arrows hunted for.
		foreach (int i in new[] { 1, 2, 4 })
			Assert.IsType<BeforeTheUnderworld>(Assert.IsType<PrepFor>(wof[boss.Count + i]).Inner);
		var unholy = Assert.IsType<HaveItem>(Assert.IsType<BeforeTheUnderworld>(Assert.IsType<PrepFor>(wof[boss.Count + 4]).Inner).Inner);
		Assert.Equal((FakeGame.UnholyArrow, ProgressionPlanner.WofUnholyArrows), (unholy.Accepts.Single(), unholy.Count));
		var arrows = Assert.IsType<HaveItem>(Assert.IsType<PrepFor>(wof[boss.Count + 5]).Inner);
		Assert.Equal((1000, 800), (arrows.Count, arrows.RefillBelow));
		var wall = Assert.IsType<BossDefeated>(wof[^2]);
		Assert.Equal((FakeGame.WofNpc, FakeGame.GuideVoodooDoll), (wall.Npc, wall.SummonItem));
		Assert.True(wall.ByVoodooDoll);
		Assert.False(wall.NightOnly);
		Assert.Equal(new[] { FakeGame.WofEyeNpc, FakeGame.HungryNpc, FakeGame.HungryIINpc, FakeGame.LeechHeadNpc, FakeGame.LeechBodyNpc, FakeGame.LeechTailNpc }, wall.Minions);
	}

	[Fact]
	public void TheWholePlan_DownTheShaft_TheBridge_TheHunt_AndTheSummonOffTheEnd()
	{
		var (g, ctx, p) = AfterTheEye();
		Assert.True(ctx.DeepestRow < Row, "the gear's limit, before the errand");

		// S3: the hellevator, 60 tiles east of the spawn; the depth limit lifted, the Voodoo Demon spared.
		var shaft = Assert.IsType<HellevatorSkill>(Next(p, ctx));
		Assert.True(p.WofPrepMet);
		Assert.Equal(SpawnX + ProgressionPlanner.ShaftOffsetTiles, shaft.ShaftX);
		Assert.Equal(Row - 1, shaft.StandRow);
		Assert.True(ctx.DepthErrand);
		Assert.Equal(int.MaxValue, ctx.DeepestRow);
		Assert.Contains(FakeGame.VoodooDemonNpc, ctx.SparedTypes);
		Assert.Contains(FakeGame.GuideVoodooDoll, ctx.NeededItems);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, shaft, 30000));
		p.OnSkillFinished(shaft, SkillStatus.Succeeded, ctx);
		Assert.True(p.UnderworldReachedOnce);
		var (row, back, to) = Assert.IsType<(int, int, int)>(p.Hellbridge);
		int from = shaft.Landed.Value.X + 1; // under the body's front column, going east
		output.WriteLine($"bridge row {row} x {back}-{to}");
		// A bridge's length east (the world's width unknown here: no edge), nothing left for the flight side.
		Assert.Equal((Row, from, from + Bridge), (row, back, to));

		// S4: the bridge.
		g.Refresh(ctx);
		var bridge = Assert.IsType<HellbridgeSkill>(Next(p, ctx));
		Assert.Equal((Row, from, from + Bridge), (bridge.Row, bridge.FromX, bridge.ToX));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, bridge, 30000));
		p.OnSkillFinished(bridge, SkillStatus.Succeeded, ctx);
		int TowerFrom = from, ToEnd = from + Bridge; // the names below: the arena's ends

		// S5: at its far end with no doll, the hunt; there the Voodoo Demon is not spared.
		g.Refresh(ctx);
		var hunt = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("a Guide Voodoo Doll", hunt.What);
		Assert.True(p.HellbridgeDone);
		p.SpareRules(ctx); // as the brain does each tick
		Assert.DoesNotContain(FakeGame.VoodooDemonNpc, ctx.SparedTypes);

		// S6: with a doll, at the end, lava under where it lands: the fight, its summon the throw.
		g.Items[FakeGame.GuideVoodooDoll] = 1;
		g.Refresh(ctx);
		ISkill s = Next(p, ctx);
		if (s is WalkToSkill toEnd) { // a step short of the end's stand: up to it
			Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, toEnd, 2000));
			g.Refresh(ctx);
			s = Next(p, ctx);
		}
		var fight = Assert.IsType<BossFightSkill>(s);
		Assert.True(fight.IsWallOfFlesh);
		Assert.NotNull(fight.Summon);
		Assert.Equal(((int, int, int)?)(TowerFrom, ToEnd, Row - 1), fight.Arena);
		Assert.Contains("summoning", p.Status);
		g.WorldWidthTiles = 200; // the end (x 144) in the world's east half
		var log = (TestLog)ctx.Log;
		SkillStatus fs = SkillStatus.Running;
		fight.Start(ctx);
		for (int t = 0; t < 400 && fs == SkillStatus.Running && !log.Lines.Any(l => l.Contains("is here")); t++) {
			g.Refresh(ctx);
			ctx.Tick = 100000 + t;
			fs = fight.Tick(ctx);
			g.Step(ctx);
		}
		Assert.Equal(1, g.Throws);
		Assert.Equal(1, g.WofSpawns);
		Assert.Contains(log.Lines, l => l.Contains("wall of flesh summoned") && l.Contains("from the east"));
		Assert.False(g.TownNpcAlive(FakeGame.GuideNpc));
	}

	[Fact]
	public void WithNoGuideAlive_ThePlanWaitsForHim_BeforeTheShaft()
	{
		var (_, ctx, p) = AfterTheEye(guide: false);
		var wait = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("the Guide", wait.What);
	}

	[Fact]
	public void ShortOfUnholyArrows_ThePlanHuntsWormTeeth_BeforeTheShaft()
	{
		// Measured in game (m12_synthetic_classic, Classic): an unholy arrow took 41.6 of the Wall's life, a wooden one 10.1; the fight
		// on wooden arrows (21 a second) and the one with 75 unholy (32.5) were both lost. So WofUnholyArrows before the descent.
		var (g, ctx, p) = AfterTheEye();
		g.Items[FakeGame.UnholyArrow] = 75;
		g.Refresh(ctx);
		Assert.IsNotType<HellevatorSkill>(Next(p, ctx));
		Assert.Contains($"item{FakeGame.WormTooth}", p.Status);
		// Teeth in the bag: the arrows are made from them at the anvil, then down.
		g.Items[FakeGame.WormTooth] = 15;
		g.Refresh(ctx);
		ISkill s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.UnholyArrow } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.UnholyArrow}"), p.Status);
		g.Items[FakeGame.WormTooth] = 0;
		g.Items[FakeGame.UnholyArrow] = ProgressionPlanner.WofUnholyArrows;
		g.Refresh(ctx);
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
	}

	[Fact]
	public void ShortOfUnholyArrows_InACrimsonWorld_ThePlanHuntsVertebrae()
	{
		var (g, ctx, p) = AfterTheEye(crimson: true);
		g.Items[FakeGame.UnholyArrow] = 0;
		g.Refresh(ctx);
		Assert.IsNotType<HellevatorSkill>(Next(p, ctx));
		Assert.Contains($"item{FakeGame.Vertebrae}", p.Status);
		Assert.DoesNotContain($"item{FakeGame.WormTooth}", p.Status);
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(false, false)]
	[InlineData(true, true)]
	public void TheEvilBowAndPickaxe_AreMadeFromTheBag_BeforeTheShaft(bool crimson, bool scales)
	{
		var (g, ctx, p) = AfterTheEye(crimson: crimson);
		int ore = crimson ? FakeGame.CrimtaneOre : FakeGame.DemoniteOre, bow = crimson ? FakeGame.TendonBow : FakeGame.DemonBow;
		int pick = crimson ? FakeGame.DeathbringerPickaxe : FakeGame.NightmarePickaxe;
		g.Items[ore] = 60; // 20 bars: the bow's 8 and the pickaxe's 12
		if (scales)
			g.Items[crimson ? FakeGame.TissueSample : FakeGame.ShadowScale] = 6;
		g.Refresh(ctx);
		ISkill s = null;
		for (int i = 0; i < 40 && (s = Next(p, ctx)) is not (HellevatorSkill or null); i++) {
			Assert.IsNotType<MineSkill>(s); // nothing gathered for them
			SkillStatus st = EarlyGameTests.RunSkill(g, ctx, s, 6000);
			p.OnSkillFinished(s, st, ctx);
			g.Refresh(ctx);
		}
		Assert.IsType<HellevatorSkill>(s);
		Assert.Equal(1, g.Count(bow));
		Assert.Equal(scales ? 1 : 0, g.Count(pick));
		Assert.Equal(scales ? (crimson ? 70 : 65) : 40, ctx.PickPower);
	}

	[Fact]
	public void DownThere_TheSurfacesPrepIsMoot_NothingIsGoneUpFor()
	{
		var (g, ctx, p) = AfterTheEye();
		var shaft = Assert.IsType<HellevatorSkill>(Next(p, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, shaft, 30000));
		// As a run resumed from the "underworld reached" checkpoint: down there, the bridge not known yet. Short of unholy arrows,
		// with demonite and scales in the bag: no hunt and no craft up at the house, the bridge.
		g.Items[FakeGame.UnholyArrow] = 0;
		g.Items[FakeGame.DemoniteOre] = 60;
		g.Items[FakeGame.ShadowScale] = 6;
		g.Refresh(ctx);
		Assert.IsType<HellbridgeSkill>(Next(p, ctx));
		Assert.True(p.UnderworldReachedOnce);
		// And once the bridge is known, likewise.
		g.Refresh(ctx);
		Assert.IsType<HellbridgeSkill>(Next(p, ctx));
	}

	[Fact]
	public void NoLavaUnderWhereTheDollWouldLand_TheBridgeGoesOn()
	{
		var (g, ctx, p) = AfterTheEye();
		var shaft = Assert.IsType<HellevatorSkill>(Next(p, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, shaft, 30000));
		p.OnSkillFinished(shaft, SkillStatus.Succeeded, ctx);
		g.Refresh(ctx);
		var bridge = Assert.IsType<HellbridgeSkill>(Next(p, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, bridge, 30000));
		p.OnSkillFinished(bridge, SkillStatus.Succeeded, ctx);
		// An ash knoll under the landing columns (x 147-150): the doll would lie on it.
		for (int x = bridge.ToX + 3; x <= bridge.ToX + 6; x++)
			g.Grid.Set(x, Row + 8, CellKind.Solid);
		g.Items[FakeGame.GuideVoodooDoll] = 1;
		g.Refresh(ctx);
		var more = Assert.IsType<HellbridgeSkill>(Next(p, ctx));
		Assert.Equal(bridge.ToX + ProgressionPlanner.BridgeExtendTiles, more.ToX);
		Assert.False(p.HellbridgeDone);
	}

	[Fact]
	public void AHellbridgeStepThatKeepsFailing_IsNotStartedAgainAndAgain_ThePlanIsStuckThere()
	{
		// Seen in game (measured run M12a): "could not place a block at (2156,1042) (3 tries)", and the same step started again 33
		// times in a few seconds.
		var (g, ctx, p) = AfterTheEye();
		var shaft = Assert.IsType<HellevatorSkill>(Next(p, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, shaft, 30000));
		p.OnSkillFinished(shaft, SkillStatus.Succeeded, ctx);
		int from = shaft.Landed.Value.X + 1;
		g.Blocks.NoPlace.Add(new TilePos(TowerTo + 3, Row)); // past the tower's roof: the game takes no block there
		for (int i = 0; i < ProgressionPlanner.MaxBridgeFails; i++) {
			g.Refresh(ctx);
			var bridge = Assert.IsType<HellbridgeSkill>(Next(p, ctx));
			Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, bridge, 20000));
			p.OnSkillFinished(bridge, SkillStatus.Failed, ctx);
		}
		g.Refresh(ctx);
		Assert.Null(Next(p, ctx));
		Assert.True(p.Stuck);
		Assert.Contains("hellbridge failed 2 times", p.Status);
		Assert.True(from > 0);
	}

	[Fact]
	public void ASparedVoodooDemon_IsNeitherFoughtNorDanger_NorShotThrough()
	{
		FakeGame g = EarlyGameTests.Field(120);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(10f, 15, 0.1f, 16f);
		var demon = new EntityInfo(5, FakeGame.VoodooDemonNpc, "Voodoo Demon", 9 * 16f, 20 * 16f, 30, 40, 0f, 0f, 140, 140, 32, 14);
		g.ExtraHostiles.Add(demon);
		g.Refresh(ctx);
		Assert.True(ProgressionPlanner.InDanger(ctx));
		ctx.SparedTypes.Add(FakeGame.VoodooDemonNpc);
		// Over ground (the field's dirt under it): its doll would land there; fought as any other.
		Assert.False(ctx.Spared(demon));
		Assert.True(ProgressionPlanner.InDanger(ctx));
		// Over lava: spared.
		for (int x = 7; x <= 13; x++)
			for (int y = 25; y <= 26; y++)
				g.Grid.Set(x, y, CellKind.Lava);
		Assert.True(ctx.Spared(demon));
		Assert.False(ProgressionPlanner.InDanger(ctx));
		var fight = new FightSkill();
		fight.Start(ctx);
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			fight.Tick(ctx);
			Assert.False(ctx.Output.UseItem);
			g.Step(ctx);
		}
		// A hellbat beyond it, in the same line: not shot either (the arrow would meet the demon first, or pierce on).
		var bat = new EntityInfo(6, 60, "Hellbat", 16 * 16f, 20 * 16f, 18, 18, 0f, 0f, 35, 35, 35, 14);
		g.ExtraHostiles.Add(bat);
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		Assert.True(ctx.SparedInLine(cx, cy, bat.CenterX, bat.CenterY, 640f));
		Assert.False(ctx.SparedInLine(cx, cy, cx, cy - 300f, 640f));
	}

	/// <summary>The bot on the tower's roof in the Underworld, the brain in auto mode on the plan "hellbridge" (60 tiles).</summary>
	private (FakeGame, BotContext, BotBrain) OnTheTowerAuto(bool guide, int demonX)
	{
		FakeGame g = HellTests.Hell();
		var p = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(HellTests.TowerTo - 1, p), Y = Core.Pathfinding.Body.StandingTop(Row - 1, p), ReleaseJump = true };
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 13;
		ctx.UnderworldRow = Row - ProgressionPlanner.BridgeRowsUnderTop;
		g.Blocks.TicksPerBlock = 4;
		g.Items[FakeGame.StoneBlock] = 300;
		g.Items[FakeGame.IronBow] = 1;
		g.Items[FakeGame.WoodenArrow] = 200;
		if (guide)
			g.AliveTownNpcs.Add(FakeGame.GuideNpc);
		// A Voodoo Demon (AI style 14, a flier) hanging over the lava at the column given, level with the bot.
		g.ExtraHostiles.Add(new EntityInfo(5, FakeGame.VoodooDemonNpc, "Voodoo Demon", demonX * 16f, (Row - 3) * 16f, 30, 40, 0f, 0f, 140, 140, 32, 14));
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Hellbridge);
		brain.Planner.BridgeLength = 40;
		g.Refresh(ctx);
		return (g, ctx, brain);
	}

	[Fact]
	public void TheVoodooRule_TheGuideAlive_TheDemonByTheBridgeIsNotFought_AndTheBridgeIsBuilt()
	{
		// Over the lava 7 columns past where the 40-tile bridge from the tower's end (x 46-86) ends: never over its deck.
		var (g, ctx, brain) = OnTheTowerAuto(guide: true, demonX: HellTests.TowerTo + 40 + 8);
		bool fought = false;
		int t = 0;
		for (; t < 20000 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			brain.Tick(ctx);
			fought |= brain.Current is FightSkill;
			g.Step(ctx);
		}
		output.WriteLine($"after {t}: {brain.Describe()} / {brain.Planner.Status}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		Assert.False(fought);
		Assert.Contains(FakeGame.VoodooDemonNpc, ctx.SparedTypes);
	}

	[Fact]
	public void TheVoodooRule_NoGuide_TheDemonIsFoughtAsAnyOther()
	{
		var (g, ctx, brain) = OnTheTowerAuto(guide: false, demonX: HellTests.TowerTo + 4);
		bool fought = false;
		for (int t = 0; t < 600 && !fought; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			brain.Tick(ctx);
			fought |= brain.Current is FightSkill;
			g.Step(ctx);
		}
		Assert.True(fought, brain.Describe());
		Assert.Empty(ctx.SparedTypes);
	}

	[Fact]
	public void KeepRoom_NeverTrashesTheDoll_NorTheShadowScales_NorAStagesItem()
	{
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		g.SlotLimit = 5;
		g.Items[FakeGame.GuideVoodooDoll] = 1;
		g.Items[FakeGame.ShadowScale] = 3;
		g.Items[FakeGame.Gel] = 5; // kept: torches
		g.Items[FakeGame.Lens] = 2; // kept
		g.Items[777] = 4; // a needed item
		ctx.NeededItems.Add(777);
		foreach (int i in new[] { FakeGame.GuideVoodooDoll, FakeGame.ShadowScale, 777 })
			g.Values[i] = 1;
		BotBrain.KeepRoom(ctx);
		output.WriteLine($"trashed {string.Join(",", g.Trashed)}");
		Assert.DoesNotContain(FakeGame.GuideVoodooDoll, g.Trashed);
		Assert.DoesNotContain(FakeGame.ShadowScale, g.Trashed);
		Assert.DoesNotContain(777, g.Trashed);
	}

	[Fact]
	public void OnTheErrand_LootUnderTheBridge_IsNotFetched()
	{
		// Seen in game (measured run M12b): after a fight on the bridge the pickup walked down to row 1049, among the hot hellstone.
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		ctx.UnderworldRow = 10;
		var below = new DropInfo(1, FakeGame.Gel, "Gel", 1, 5 * 16f, 30 * 16f, 8, 8);
		var level = new DropInfo(2, FakeGame.Gel, "Gel", 1, 8 * 16f, 24 * 16f, 8, 8);
		Assert.False(CollectDropsSkill.TooDeep(ctx, below));
		ctx.DepthErrand = true;
		Assert.True(CollectDropsSkill.TooDeep(ctx, below));
		Assert.False(CollectDropsSkill.TooDeep(ctx, level));
	}

	[Fact]
	public void KilledInTheUnderworldOnItsErrand_NoDangerZone_OverTheBridge()
	{
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		ctx.UnderworldRow = 10;
		ctx.DepthErrand = true;
		ctx.NoteDeathByEnemy(new TilePos(30, 20));
		Assert.Empty(ctx.DangerZones);
		ctx.DepthErrand = false;
		ctx.NoteDeathByEnemy(new TilePos(30, 20));
		Assert.Single(ctx.DangerZones);
	}

	/// <summary>
	/// world_wof M12c (Peppe): no life crystal found near home in three game days under the Wall's 400-life stage, and the Wall never
	/// reached. Past the earlier bosses' 200, the life is a best effort: worked on for WofLifeTicks, then let go for a day.
	/// </summary>
	[Fact]
	public void TheWallsExtraLife_IsLetGo_AfterADayWithoutIt()
	{
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		g.LifeMax = ProgressionPlanner.BossLife;
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		var need = new BestEffort(new LifeAtLeast(ProgressionPlanner.MaxCrystalLife, "max life for the wall"), 1000);
		Assert.NotNull(p.ResolveForTest(need, ctx)); // works on it: crystals
		ctx.Tick += 1001;
		Assert.Null(p.ResolveForTest(need, ctx));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("max life for the wall") && l.Contains("not met; the plan goes on without it"));
		ctx.Tick += 100;
		Assert.Null(p.ResolveForTest(need, ctx)); // let go
		// world_wof M12d: let go for a fixed day, it came back 13 game hours into the descent's stages (the hellevator begun)
		// and sent the bot crystal hunting again. It stays let go however long the stages after it take...
		ctx.Tick += 3 * 24 * 3600;
		Assert.Null(p.ResolveForTest(need, ctx));
		// ...until a death says it was wanted after all.
		ctx.Deaths++;
		Assert.NotNull(p.ResolveForTest(need, ctx));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("a death since it was let go"));
	}

	/// <summary>
	/// world_wof M12e (Peppe): at row 852 of the shaft the arrows shot at cave bats fell under the 800 refill and the plan sent the
	/// bot back up 570 rows for 2 wood. On the way down, the fight's own need (WofFightArrows) holds; after a death, at home, the
	/// refill again.
	/// </summary>
	[Fact]
	public void OnTheWayDown_ArrowsUnderTheRefill_ButEnoughForTheFight_DoNotSendTheBotBackUp()
	{
		var (g, ctx, p) = AfterTheEye();
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.WofFightArrows - ProgressionPlanner.WofUnholyArrows + 50; // 650 in all
		g.Refresh(ctx);
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
		ctx.Deaths++; // home again: the refill
		g.Refresh(ctx);
		Assert.IsNotType<HellevatorSkill>(Next(p, ctx));
	}

	/// <summary>On the way down with fewer arrows than the fight needs, they are made.</summary>
	[Fact]
	public void OnTheWayDown_FewerArrowsThanTheFightNeeds_AreMade()
	{
		var (g, ctx, p) = AfterTheEye();
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
		g.Items[FakeGame.WoodenArrow] = 100;
		g.Refresh(ctx);
		Assert.IsNotType<HellevatorSkill>(Next(p, ctx));
	}

	/// <summary>
	/// world_wof M12d-M12f: the hellevator, once begun, left for wood stocks, an NPC room, the crystal search. On the way down the
	/// errand owns the bot: a surface stage unmet (here max life under the earlier bosses' 200) waits; after a death it comes first.
	/// </summary>
	[Fact]
	public void OnTheWayDown_TheSurfacesWork_Waits_UntilADeath()
	{
		var (g, ctx, p) = AfterTheEye();
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
		g.LifeMax = 100;
		g.Refresh(ctx);
		Assert.IsType<HellevatorSkill>(Next(p, ctx));
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("on the way down to the Underworld"));
		ctx.Deaths++;
		g.Refresh(ctx);
		Assert.IsNotType<HellevatorSkill>(Next(p, ctx));
	}
}
