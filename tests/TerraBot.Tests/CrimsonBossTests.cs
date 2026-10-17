using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 10 in a Crimson world: the evil boss plan takes the Brain of Cthulhu's path (a Bloody Spine of vicious powder and
/// vertebrae, ProgressionPlanner's crimson stages), in a small Crimson world laid out as <see cref="EvilBossTests"/>' Corruption one
/// (the spawn at the west end, the crimson a few hundred tiles east of it on the surface).
/// </summary>
public class CrimsonBossTests
{
	private readonly ITestOutputHelper output;
	public CrimsonBossTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The crimson's columns, its surface feet row, and the surface layer's last row (Main.worldSurface).</summary>
	private const int CrimsonFrom = 300, CrimsonTo = 480, Feet = 44, SurfaceRow = 50;
	private static readonly TilePos InTheCrimson = new(350, Feet);

	/// <summary>500 wide, feet row 44 over dirt and stone; x 300-480 is crimstone under crimson grass.</summary>
	internal static FakeGame CrimsonWorld()
	{
		FakeGame g = ProgressionTests.Mine(width: 500, sky: Feet + 1).UseCrimson();
		for (int x = CrimsonFrom; x <= CrimsonTo; x++)
			for (int y = Feet + 1; y < Feet + 13; y++)
				g.TileTypes[new TilePos(x, y)] = y == Feet + 1 ? FakeGame.TileCrimsonGrass : FakeGame.TileCrimstone;
		return g;
	}

	private BotContext Armoured(FakeGame g)
	{
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		ctx.SurfaceY = SurfaceRow;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Refresh(ctx);
		return ctx;
	}

	private static void MoveTo(FakeGame g, BotContext ctx, int x, int feetRow)
	{
		var p = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(x, p), Y = Core.Pathfinding.Body.StandingTop(feetRow, p), ReleaseJump = true };
		g.Refresh(ctx);
	}

	/// <summary>The boss plan done (the Eye defeated, settled in a house, armoured, max life 200, the bow and arrows), by day.</summary>
	private (BotContext, ProgressionPlanner) AfterTheEye(FakeGame g)
	{
		BotContext ctx = Armoured(g);
		g.Stations[FakeGame.TileBench] = new TilePos(5, Feet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, Feet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, Feet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow })
			g.Items[item] = 1;
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BrainArrows;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.BossLife;
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.EvilBoss);
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		return (ctx, p);
	}

	private ISkill Next(ProgressionPlanner p, BotContext ctx)
	{
		ISkill s = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	private static BossDefeated Brain => ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.EvilBoss, FakeGame.CrimsonIds).OfType<BossDefeated>().Last();

	[Fact]
	public void TheEvilBossPlan_InACrimsonWorld_IsTheBossPlan_ThenTheBrainOfCthulhu_InOrder()
	{
		var boss = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.CrimsonIds).ToList();
		var evil = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.EvilBoss, FakeGame.CrimsonIds).ToList();
		Assert.Equal(boss.Select(n => (n.GetType(), n.Label)), evil.Take(boss.Count).Select(n => (n.GetType(), n.Label)));
		// (a) the Tendon Bow from the bag, (b) vertebrae, (c) vicious powder (its mushrooms and bottle from its recipe), (d) the
		// Bloody Spine, (e) unholy arrows from the vertebrae left over, (f) the arena in the crimson, (g) the fight.
		Assert.Equal(new[] { "bow", "arrows", "max life", "tendon bow", "vertebrae", "vicious powder", "bloody spine", "unholy arrows",
			ProgressionPlanner.CrimsonArenaLabel, "Brain of Cthulhu" }, evil.Skip(boss.Count).Select(n => n.Label));
		Assert.All(evil.Skip(boss.Count).Take(9), n => Assert.Equal(FakeGame.BrainNpc, Assert.IsType<PrepFor>(n).BossNpc));
		Assert.IsType<FromBag>(((PrepFor)evil[boss.Count + 3]).Inner);
		Assert.Equal(FakeGame.TendonBow, ((FromBag)((PrepFor)evil[boss.Count + 3]).Inner).Inner.Accepts.Single());
		var vertebrae = Assert.IsType<StepToward>(((PrepFor)evil[boss.Count + 4]).Inner);
		Assert.Equal(new[] { (FakeGame.BloodySpine, 1) }, vertebrae.Made);
		var vh = Assert.IsType<HaveItem>(vertebrae.Inner);
		Assert.Equal((FakeGame.Vertebrae, ProgressionPlanner.SpineVertebrae), (vh.Accepts.Single(), vh.Count));
		Assert.IsType<FromBag>(((PrepFor)evil[boss.Count + 7]).Inner);
		Assert.True(Assert.IsType<ArenaBuilt>(((PrepFor)evil[boss.Count + 8]).Inner).InEvilBiome);
		BossDefeated brain = Brain;
		Assert.Equal((FakeGame.BrainNpc, FakeGame.BloodySpine), (brain.Npc, brain.SummonItem));
		Assert.Equal(new[] { FakeGame.CreeperNpc }, brain.Minions);
		Assert.False(brain.NightOnly);
		Assert.True(brain.InEvilBiome);
		// Nothing of the Corruption's path: no Worm Food, rotten chunks or vile powder.
		int[] corruption = { FakeGame.WormFood, FakeGame.RottenChunk, FakeGame.VilePowder, FakeGame.DemonBow };
		Assert.DoesNotContain(evil.OfType<PrepFor>(), n => n.Inner is HaveItem h && h.Accepts.Any(corruption.Contains)
			|| n.Inner is StepToward st && st.Inner is HaveItem sh && sh.Accepts.Any(corruption.Contains)
			|| n.Inner is FromBag fb && fb.Inner.Accepts.Any(corruption.Contains));
		// The Corruption's plan is as it was.
		Assert.Equal("Eater of Worlds", ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.EvilBoss, FakeGame.Ids).Last().Label);
	}

	[Fact]
	public void TheBrainsStages_ComeInOrder_FromTheEyesCrimtaneToTheFight()
	{
		FakeGame g = CrimsonWorld();
		var (ctx, p) = AfterTheEye(g);
		Assert.True(ctx.Ids.Crimson);

		// (a) The Eye's crimtane in the bag: bars, then the Tendon Bow.
		g.Items[FakeGame.CrimtaneOre] = 30;
		ISkill s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.CrimtaneBar } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.CrimtaneBar}"), p.Status);
		g.Items[FakeGame.CrimtaneOre] = 6;
		g.Items[FakeGame.TendonBow] = 1;

		// (b) The vertebrae, hunted from the crimson's fliers (crimeras), on its ground.
		s = Next(p, ctx);
		var hunt = Assert.IsType<HuntSkill>(s);
		Assert.Equal(FakeGame.Vertebrae, hunt.ItemType);
		Assert.Equal(FakeGame.FlyingAi, hunt.AiStyle);
		TilePos ground = Assert.IsType<TilePos>(hunt.Ground);
		Assert.True(ctx.InEvilBiome(ground), $"hunting ground {ground}");
		Assert.True(ctx.CorruptionAround(ground) >= BotContext.CorruptionZoneTiles, $"{ctx.CorruptionAround(ground)} crimson tiles round {ground}");
		Assert.Contains("crimson", p.Status);
		Assert.True(ctx.EvilErrand);
		// Two more than the spine takes: the unholy arrows' (e).
		g.Items[FakeGame.Vertebrae] = ProgressionPlanner.SpineVertebrae + 2;

		// (c) The powder's mushrooms: none seen from home, so to the crimson first...
		s = Next(p, ctx);
		Assert.IsType<WalkToSkill>(s);
		Assert.Contains("crimson", p.Status);
		// ...where the mushrooms seen are picked (only the vicious mushroom frame of the crimson plants).
		MoveTo(g, ctx, InTheCrimson.X, Feet);
		g.AddPlant(356, Feet, mushroom: true);
		g.AddPlant(358, Feet, mushroom: false);
		s = Next(p, ctx);
		var pick = Assert.IsType<MineSkill>(s);
		Assert.Equal((FakeGame.ViciousMushroom, FakeGame.TileCrimsonPlants, FakeGame.ViciousMushroomFrame), (pick.ItemType, pick.TileType, pick.FrameX));
		Assert.Equal(6, pick.Amount);
		g.Items[FakeGame.ViciousMushroom] = 6;

		// The placed bottle (as the Corruption's powder: EvilBossTests), then the powder at it.
		g.Items[FakeGame.Bottle] = 1;
		g.Stations[FakeGame.TileBottles] = new TilePos(352, Feet);
		for (int i = 0; i < 22; i++)
			g.Refresh(ctx);
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.ViciousPowder } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.ViciousPowder}"), p.Status);
		g.Items[FakeGame.ViciousPowder] = ProgressionPlanner.SpinePowder;
		g.Items[FakeGame.ViciousMushroom] = 0;

		// (d) The Bloody Spine at the altar found in the crimson (a crimson altar is tile 26 too).
		g.AddAltar(340, Feet - 1);
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx);
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.BloodySpine } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.BloodySpine}"), p.Status);
		g.Items[FakeGame.ViciousPowder] = 0;
		g.Items[FakeGame.Vertebrae] = 2;
		g.Items[FakeGame.BloodySpine] = 1;

		// (e) The vertebrae left over: unholy arrows, from the bag.
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.UnholyArrow } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.UnholyArrow}"), p.Status);
		g.Items[FakeGame.Vertebrae] = 0;
		g.Items[FakeGame.UnholyArrow] = 10;

		// (f) The arena in the crimson, built.
		BuildTheCrimsonArena(g, ctx, p);

		// (g) The fight, on it, by day: the Brain's own rules.
		var fight = FightNext(g, ctx, p);
		Assert.Equal((FakeGame.BloodySpine, FakeGame.BrainNpc), (fight.SummonItem, fight.BossType));
		Assert.Equal(new[] { FakeGame.CreeperNpc }, fight.MinionTypes);
		Assert.True(fight.IsBrain);
		Assert.False(fight.IsWorm);
		Assert.Equal(p.EvilArenaBounds(ctx), fight.Arena);
		Assert.Contains("in the crimson", p.Status);
		g.BossesDefeated.Add(FakeGame.BrainNpc);
		Assert.Null(Next(p, ctx));
		Assert.True(p.Complete, p.Status);
		Assert.Contains("Brain of Cthulhu", p.Status);
	}

	[Fact]
	public void TheVertebrae_AreNotMadeIntoUnholyArrows_BeforeTheSpineIsMade()
	{
		// Unholy arrows come from vertebrae too (Recipe.cs:2283): from the bag before the spine they would eat the spine's.
		FakeGame g = CrimsonWorld();
		var (ctx, p) = AfterTheEye(g);
		g.Items[FakeGame.Vertebrae] = 10;
		MoveTo(g, ctx, InTheCrimson.X, Feet);
		ISkill s = Next(p, ctx);
		Assert.False(s is CraftSkill { ItemType: FakeGame.UnholyArrow }, p.Status);
		var hunt = Assert.IsType<HuntSkill>(s);
		Assert.Equal(FakeGame.Vertebrae, hunt.ItemType);
		Assert.Equal(ProgressionPlanner.SpineVertebrae, hunt.Amount);
	}

	[Fact]
	public void TheBloodySpine_IsUsedByDay_InTheCrimson()
	{
		FakeGame g = CrimsonWorld();
		BotContext ctx = Armoured(g);
		g.Items[FakeGame.BloodySpine] = 1;
		var p = new ProgressionPlanner();
		Assert.False(ctx.IsNight);

		// From home, by day: to the crimson, not a wait for night.
		ISkill fromHome = p.ResolveForTest(Brain, ctx);
		output.WriteLine($"{p.Status} -> {fromHome?.Describe()}");
		var walk = Assert.IsType<WalkToSkill>(fromHome);
		Assert.True(ctx.InEvilBiome(walk.Target) || ctx.CorruptionAround(walk.Target) >= BotContext.CorruptionZoneTiles, $"walking to {walk.Target}");
		Assert.Contains("crimson", p.Status);

		// In the crimson, by day: summoned and fought there.
		MoveTo(g, ctx, InTheCrimson.X, Feet);
		var fight = Assert.IsType<BossFightSkill>(p.ResolveForTest(Brain, ctx));
		output.WriteLine($"{p.Status} -> {fight.Describe()}");
		Assert.Equal(FakeGame.BrainNpc, fight.BossType);
		Assert.True(fight.IsBrain);
	}

	[Fact]
	public void WhileTheBrainIsUp_HiddenBehindItsCreepers_ThePlanStaysOnTheFight()
	{
		// The Brain takes no damage while a Creeper lives (dontTakeDamage): it is in view all the same (the game side sees it as a
		// boss's part), and so is the plan's fight, not a new spine.
		FakeGame g = CrimsonWorld();
		var (ctx, p) = AfterTheEye(g);
		MoveTo(g, ctx, InTheCrimson.X, Feet);
		g.ExtraHostiles.Add(new EntityInfo(1, FakeGame.BrainNpc, "Brain of Cthulhu", InTheCrimson.X * 16f, (Feet - 20) * 16f, 160, 110, 0f, 1f, 1250, 1250, 30,
			BrainPattern.BrainAiStyle, DontTakeDamage: true));
		g.Refresh(ctx);
		Assert.True(ProgressionPlanner.BossUp(ctx, FakeGame.BrainNpc));
		ISkill s = Next(p, ctx);
		var fight = Assert.IsType<BossFightSkill>(s);
		Assert.True(fight.IsBrain);

		// Out of view, but the bar shows the Creepers' life: still up.
		g.ExtraHostiles.Clear();
		g.BossBars[FakeGame.BrainNpc] = 2000;
		g.Refresh(ctx);
		Assert.True(ProgressionPlanner.BossUp(ctx, FakeGame.BrainNpc));
	}

	[Fact]
	public void ByDay_TheEyesSummonInHand_TheDayGoesToTheBrainsPrep_NotTheWorms()
	{
		// Measured run J2 (world_eye on the Crimson world The Aggressive Site, log line 16832): "by day, before the night (walking to
		// the arena to summon the Eye of Cthulhu): exploring for the corruption (none seen yet) to hunt for Rotten Chunk", the worm's
		// prep in a world with no corruption. As NightWaitTests' boss plan by day, here in a Crimson world: the Brain's prep instead.
		FakeGame g = CrimsonWorld();
		BotContext ctx = Armoured(g);
		g.Stations[FakeGame.TileBench] = new TilePos(5, Feet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, Feet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, Feet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow, FakeGame.SuspiciousEye })
			g.Items[item] = 1;
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BrainArrows;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.BossLife;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
		var build = Assert.IsType<BuildSkill>(Next(p, ctx));
		SkillStatus built = EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 4);
		Assert.Equal(SkillStatus.Succeeded, built);
		p.OnSkillFinished(build, built, ctx);
		build.Stop(ctx);
		// The Brain's arrows in the bag already, so its next prep is the vertebrae (no crimtane for a Tendon Bow).
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BrainArrows;
		g.Refresh(ctx);

		ISkill day = Next(p, ctx);
		Assert.True(p.DayWorkBeforeNight, p.Status);
		Assert.Contains("summon the Eye of Cthulhu", p.Status);
		var hunt = Assert.IsType<HuntSkill>(day);
		Assert.Equal(FakeGame.Vertebrae, hunt.ItemType);
		Assert.Contains("crimson", p.Status);
		Assert.DoesNotContain($"item{FakeGame.RottenChunk}", p.Status);
	}

	/// <summary>The plan's next job is the arena in the crimson: built in the fake, and the plan told it is done.</summary>
	private BuildSkill BuildTheCrimsonArena(FakeGame g, BotContext ctx, ProgressionPlanner p)
	{
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
		ISkill s = Next(p, ctx);
		var build = Assert.IsType<BuildSkill>(s);
		Assert.True(build.Blueprint.InEvilBiome, build.Describe());
		Assert.Equal(build.Origin, p.EvilArenaSite);
		var mid = new TilePos(build.Origin.X + build.Blueprint.Width / 2, build.Origin.Y + build.Blueprint.StandRow);
		Assert.True(ctx.InEvilBiome(mid), $"the arena's middle {mid} is not on the crimson's ground");
		Assert.True(ctx.CorruptionAround(mid) >= BotContext.CorruptionZoneTiles, $"{ctx.CorruptionAround(mid)} crimson tiles round {mid}");
		SkillStatus built = EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 4);
		output.WriteLine($"build: {built} {build.FailureReason}");
		Assert.Equal(SkillStatus.Succeeded, built);
		p.OnSkillFinished(build, built, ctx);
		build.Stop(ctx);
		g.Refresh(ctx);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, build.Blueprint, build.Origin));
		return build;
	}

	private BossFightSkill FightNext(FakeGame g, BotContext ctx, ProgressionPlanner p)
	{
		ISkill s = Next(p, ctx);
		if (s is WalkToSkill && p.EvilArenaBounds(ctx) is (int x0, int x1, int y)) {
			MoveTo(g, ctx, (x0 + x1) / 2, y);
			s = Next(p, ctx);
		}
		return Assert.IsType<BossFightSkill>(s);
	}
}
