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
/// Milestone 10 (docs M10_SPEC.md): the evil boss plan, the Eater of Worlds by Worm Food, in a small Corruption world like
/// the M10 one (the spawn at the west end, the corruption a few hundred tiles east of it on the surface).
/// </summary>
public class EvilBossTests
{
	private readonly ITestOutputHelper output;
	public EvilBossTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The corruption's columns, its surface feet row, and the surface layer's last row (Main.worldSurface).</summary>
	private const int CorruptFrom = 300, CorruptTo = 480, Feet = 44, SurfaceRow = 50;
	private static readonly TilePos InTheCorruption = new(350, Feet);

	/// <summary>500 wide, feet row 44 over dirt and stone; x 300-480 is ebonstone under corrupt grass.</summary>
	internal static FakeGame CorruptWorld()
	{
		FakeGame g = ProgressionTests.Mine(width: 500, sky: Feet + 1);
		for (int x = CorruptFrom; x <= CorruptTo; x++)
			for (int y = Feet + 1; y < Feet + 13; y++)
				g.TileTypes[new TilePos(x, y)] = y == Feet + 1 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		return g;
	}

	/// <summary>Iron armour worn (three pieces, defense 9 with the set bonus), the iron pickaxe (40: ebonstone wants 65).</summary>
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
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.BossArrows;
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

	private static BossDefeated Eater => ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.EvilBoss, FakeGame.Ids).OfType<BossDefeated>().Last();

	[Fact]
	public void TheEvilBossPlan_IsTheBossPlan_ThenTheEaterOfWorlds_InTheSpecsOrder()
	{
		var boss = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids).ToList();
		var evil = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.EvilBoss, FakeGame.Ids).ToList();
		// The Eye's plan as it was, first (it must still beat the Eye if it has not).
		Assert.Equal(boss.Select(n => (n.GetType(), n.Label)), evil.Take(boss.Count).Select(n => (n.GetType(), n.Label)));
		var eye = Assert.IsType<BossDefeated>(boss[^1]);
		Assert.True(eye.NightOnly);
		Assert.False(eye.InEvilBiome);
		// Then (a) the Demon Bow and (b) unholy arrows from the bag, (c) the chunks, (f) the powder (its mushrooms and bottle
		// come from its recipe), (g) Worm Food, (h) the fight.
		Assert.Equal(new[] { "bow", "arrows", "max life", "demon bow", "unholy arrows", "rotten chunks", "vile powder", "worm food", "Eater of Worlds" },
			evil.Skip(boss.Count).Select(n => n.Label));
		Assert.All(evil.Skip(boss.Count).Take(8), n => Assert.Equal(FakeGame.EowHead, Assert.IsType<PrepFor>(n).BossNpc));
		Assert.IsType<FromBag>(((PrepFor)evil[boss.Count + 3]).Inner);
		Assert.IsType<FromBag>(((PrepFor)evil[boss.Count + 4]).Inner);
		BossDefeated eater = Eater;
		Assert.Equal((FakeGame.EowHead, FakeGame.WormFood), (eater.Npc, eater.SummonItem));
		Assert.Equal(new[] { FakeGame.EowBody, FakeGame.EowTail }, eater.Minions);
		Assert.False(eater.NightOnly);
		Assert.True(eater.InEvilBiome);
	}

	[Fact]
	public void TheEaterOfWorldsStages_ComeInOrder_FromTheEyesDemoniteToTheFight()
	{
		FakeGame g = CorruptWorld();
		var (ctx, p) = AfterTheEye(g);

		// (a) The Eye's demonite in the bag: bars, then the Demon Bow.
		g.Items[FakeGame.DemoniteOre] = 30;
		ISkill s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.DemoniteBar } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.DemoniteBar}"), p.Status);
		g.Items[FakeGame.DemoniteOre] = 6;
		g.Items[FakeGame.DemonBow] = 1;

		// (b) A worm tooth in the bag: unholy arrows.
		g.Items[FakeGame.WormTooth] = 2;
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.UnholyArrow } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.UnholyArrow}"), p.Status);
		g.Items[FakeGame.WormTooth] = 0;
		g.Items[FakeGame.UnholyArrow] = 10;

		// (c) No more teeth: the rotten chunks, hunted in the corruption.
		s = Next(p, ctx);
		var hunt = Assert.IsType<HuntSkill>(s);
		Assert.Equal(FakeGame.RottenChunk, hunt.ItemType);
		Assert.Equal(FakeGame.FlyingAi, hunt.AiStyle);
		TilePos ground = Assert.IsType<TilePos>(hunt.Ground);
		Assert.True(ctx.InEvilBiome(ground), $"hunting ground {ground}");
		Assert.True(ctx.CorruptionAround(ground) >= BotContext.CorruptionZoneTiles, $"{ctx.CorruptionAround(ground)} corruption tiles round {ground}");
		Assert.True(ctx.EvilErrand);
		g.Items[FakeGame.RottenChunk] = ProgressionPlanner.WormFoodChunks;

		// (d) The powder's mushrooms: none seen from home, so to the corruption first...
		s = Next(p, ctx);
		Assert.IsType<WalkToSkill>(s);
		Assert.Contains("corruption", p.Status);
		// ...where the mushrooms seen are picked (only the mushroom frame of the corrupt plants).
		MoveTo(g, ctx, InTheCorruption.X, Feet);
		g.AddPlant(356, Feet, mushroom: true);
		g.AddPlant(358, Feet, mushroom: false);
		s = Next(p, ctx);
		var pick = Assert.IsType<MineSkill>(s);
		Assert.Equal((FakeGame.VileMushroom, FakeGame.TileCorruptPlants, FakeGame.VileMushroomFrame), (pick.ItemType, pick.TileType, pick.FrameX));
		Assert.Equal(6, pick.Amount);
		g.Items[FakeGame.VileMushroom] = 6;

		// (e) The placed bottle: glass from sand first (none carried).
		s = Next(p, ctx);
		Assert.True(s is MineSkill { ItemType: FakeGame.SandBlock } || p.Status.Contains($"item{FakeGame.SandBlock}"), p.Status);
		g.Items[FakeGame.Bottle] = 2;
		s = Next(p, ctx);
		Assert.True(s is PlaceItemSkill { ItemType: FakeGame.Bottle } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.Bottle}"), p.Status);
		g.Stations[FakeGame.TileBottles] = new TilePos(352, Feet);
		g.Items[FakeGame.Bottle] = 1;
		g.Refresh(ctx);
		g.Refresh(ctx); // the station scan is every 20 refreshes: the next one sees the bottle
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx);

		// (f) The powder at the bottle.
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.VilePowder } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.VilePowder}"), p.Status);
		// A craft broken off half way (5 of the 30): the mushrooms left still do, none gathered again.
		g.Items[FakeGame.VilePowder] = 5;
		g.Items[FakeGame.VileMushroom] = 5;
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.VilePowder } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.VilePowder}"), p.Status);
		g.Items[FakeGame.VilePowder] = ProgressionPlanner.WormFoodPowder;
		g.Items[FakeGame.VileMushroom] = 0;

		// (g) Worm Food at the demon altar, found in the corruption.
		g.AddAltar(340, Feet - 1);
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx);
		s = Next(p, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.WormFood } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.WormFood}"), p.Status);
		g.Items[FakeGame.VilePowder] = 0;
		g.Items[FakeGame.RottenChunk] = 0;
		g.Items[FakeGame.WormFood] = 1;

		// (h) The fight, here in the corruption, by day.
		s = Next(p, ctx);
		var fight = Assert.IsType<BossFightSkill>(s);
		Assert.Equal((FakeGame.WormFood, FakeGame.EowHead), (fight.SummonItem, fight.BossType));
		Assert.Equal(new[] { FakeGame.EowBody, FakeGame.EowTail }, fight.MinionTypes);
		g.BossesDefeated.Add(FakeGame.EowHead);
		Assert.Null(Next(p, ctx));
		Assert.True(p.Complete, p.Status);
	}

	[Fact]
	public void TheHuntForRottenChunks_GoesIntoTheCorruption_InFullArmour_ForTheErrandOnly()
	{
		FakeGame g = CorruptWorld();
		BotContext ctx = Armoured(g);
		var inside = new TilePos(400, Feet);
		Assert.True(ctx.InEvilBiome(inside));
		// No errand: the gate as before (ebonstone wants pick power 65; measured run S4, pockets the bot could not dig out of).
		Assert.True(ctx.EvilStoneTooHard);
		Assert.True(ctx.OffLimits(inside));
		Assert.True(ctx.OffLimitsForPaths(inside));

		var p = new ProgressionPlanner();
		var hunt = Assert.IsType<HuntSkill>(p.ResolveForTest(new HaveItem(FakeGame.RottenChunk, ProgressionPlanner.WormFoodChunks, "rotten chunks"), ctx));
		output.WriteLine($"{p.Status} -> {hunt.Describe()}");
		Assert.NotNull(hunt.Ground);
		Assert.True(ctx.EvilErrand);
		// Its surface is open to the hunt, paths and mining alike...
		Assert.False(ctx.OffLimits(inside));
		Assert.False(ctx.OffLimitsForPaths(inside));
		Assert.Null(MineSkill.WhyNotMine(ctx, ctx.Moves, new TilePos(400, Feet + 1)));
		// ...not the ground below the surface row (the chasms' floors, the orbs' ebonstone shells)...
		Assert.True(ctx.OffLimits(new TilePos(400, SurfaceRow + 4)));
		// ...nor in weaker armour.
		ctx.Combat.ArmourPieces = 2;
		ctx.Combat.Defense = 5;
		Assert.True(ctx.OffLimits(inside));
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		// The next pass that chooses other work ends the errand.
		Assert.IsType<GatherWoodSkill>(p.ResolveForTest(new HaveItem(FakeGame.Wood, 10, "wood"), ctx));
		Assert.False(ctx.EvilErrand);
		Assert.True(ctx.OffLimits(inside));
	}

	[Fact]
	public void AHuntWithAGround_WalksThereFirst_AndWandersRoundIt()
	{
		FakeGame g = CorruptWorld();
		BotContext ctx = Armoured(g);
		MoveTo(g, ctx, 220, Feet);
		ctx.EvilErrand = true;
		var ground = new TilePos(360, Feet);
		var hunt = new HuntSkill(FakeGame.RottenChunk, FakeGame.FlyingAi, 15) { Ground = ground };
		hunt.Start(ctx);
		int arrivedAt = -1, farthest = 0, walksBack = 0;
		string state = "";
		for (int t = 0; t < 60 * 60 * 2; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
			if (arrivedAt < 0 && ground.ChebyshevTo(ctx.Node) <= HuntSkill.Tolerance + 1)
				arrivedAt = t;
			if (arrivedAt >= 0 && hunt.State != state && hunt.State == "going to the hunting ground")
				walksBack++;
			state = hunt.State;
			if (arrivedAt >= 0)
				farthest = System.Math.Max(farthest, System.Math.Abs(ctx.Node.X - ground.X));
		}
		output.WriteLine($"{hunt.Describe()}; at the ground from t{arrivedAt}, wandered up to {farthest} tiles from it, {walksBack} walks back to it");
		Assert.True(arrivedAt >= 0, "never got to the hunting ground");
		Assert.True(farthest > HuntSkill.WanderTiles, "no wanders round the ground");
		Assert.True(farthest <= HuntSkill.GroundTiles, $"wandered {farthest} tiles off the ground");
		Assert.Equal(0, walksBack); // the wanders stay within the ground's reach: no walk back between them
	}

	[Fact]
	public void TheCorruptionsSurface_OnTheMap_IsTheHuntingGround_ThoughTooFewOfItsTilesAreSeenForTheZone()
	{
		// Measured run M10a: the checkpoint's map showed the corruption 130-210 tiles west of the spawn, by its surface only
		// (the map shows what daylight lit), and the bot explored underground for it for 12 game hours ("none seen yet").
		FakeGame g = CorruptWorld();
		for (int x = 0; x < g.Grid.Width; x++)
			for (int y = Feet + 2; y < g.Grid.Height; y++)
				g.Unrevealed.Add(new TilePos(x, y)); // only the top layer seen, the corrupt grass
		BotContext ctx = Armoured(g);
		var middle = new TilePos((CorruptFrom + CorruptTo) / 2, Feet);
		Assert.True(ctx.InEvilBiome(middle));
		Assert.True(ctx.CorruptionAround(middle) < BotContext.CorruptionZoneTiles, $"{ctx.CorruptionAround(middle)} seen corruption tiles");

		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(new HaveItem(FakeGame.RottenChunk, ProgressionPlanner.WormFoodChunks, "rotten chunks"), ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		var hunt = Assert.IsType<HuntSkill>(s);
		Assert.True(hunt.Ground is TilePos gr && gr.X >= CorruptFrom && gr.X <= CorruptTo && gr.Y == Feet, $"hunting ground {hunt.Ground}");
	}

	[Fact]
	public void EvilGroundSeenOnlyUnderground_IsRemembered_AndTheErrandGoesToTheSurfaceAboveIt()
	{
		// Evil ground seen anywhere counts (BotContext.KnownEvil), not only round the surface samples: here a pocket of
		// ebonstone 76 rows under a surface that shows none, seen from below on an earlier trip.
		const int width = 500, deep = 120;
		var rows = Enumerable.Repeat(new string('.', width), Feet + 1).ToList();
		rows[Feet] = "...S" + new string('.', width - 4);
		rows.Add(new string('#', width));
		while (rows.Count < deep + 20)
			rows.Add(new string('s', width));
		rows.Add(new string('@', width));
		var g = new FakeGame(rows.ToArray());
		const int pocketFrom = 380, pocketTo = 400;
		for (int x = 0; x < width; x++)
			for (int y = Feet + 2; y < rows.Count; y++)
				if (x >= pocketFrom && x <= pocketTo && y >= deep && y < deep + 5)
					g.TileTypes[new TilePos(x, y)] = FakeGame.TileEbonstone;
				else
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = Armoured(g);
		Assert.True(ctx.InEvilBiome(new TilePos((pocketFrom + pocketTo) / 2, deep))); // a look at it, as a path's gate looks
		Assert.True(ctx.KnownEvil.Count > 0);

		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(new HaveItem(FakeGame.RottenChunk, ProgressionPlanner.WormFoodChunks, "rotten chunks"), ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		var hunt = Assert.IsType<HuntSkill>(s);
		Assert.True(hunt.Ground is TilePos gr && gr.X >= pocketFrom - BotContext.DesertChunk && gr.X <= pocketTo + BotContext.DesertChunk && gr.Y == Feet,
			$"hunting ground {hunt.Ground}");
	}

	[Fact]
	public void AFlierHunt_SeesItsPreyFromATunnel_AndGoesOutToTheOpenGroundFirst()
	{
		// Measured run M10a: at (1868-1889,333-340), in tunnels under the corruption's surface, 45 fights against eaters of
		// souls ended with 0 kills; no shot was ever clear from there.
		FakeGame g = CorruptWorld();
		for (int x = 340; x <= 372; x++)
			for (int y = Feet + 4; y <= Feet + 6; y++)
				g.Grid.Set(x, y, CellKind.Empty); // a tunnel under the corruption's surface
		BotContext ctx = Armoured(g);
		MoveTo(g, ctx, 350, Feet + 6);
		ctx.EvilErrand = true;
		var ground = new TilePos(360, Feet);
		Assert.False(FightSkill.OpenSky(ctx, ctx.Node));
		Assert.True(FightSkill.OpenSky(ctx, ground));
		var hunt = new HuntSkill(FakeGame.RottenChunk, FakeGame.FlyingAi, 15) { Ground = ground };
		hunt.Start(ctx);
		var eater = new EntityInfo(5, 6, "Eater of Souls", 355 * 16f, (Feet - 10) * 16f, 30, 30, 0, 0, 40, 40, 22, AiStyle: FakeGame.FlyingAi);
		ctx.Hostiles = new[] { eater };
		hunt.Tick(ctx);
		output.WriteLine(hunt.Describe());
		Assert.Equal(HuntSkill.ToOpenGround, hunt.State);
	}

	[Fact]
	public void TheWandersRoundAHuntingGround_GoToTheSurface_NotIntoAChasmsFloor()
	{
		// Measured run M10a: the wanders from the hunting ground (1915,310) went under the corruption's surface, and its
		// chasms' floors lie far down (three deaths at (1777,401), a chasm's floor 105 rows under its edge).
		const int width = 500, chasmFrom = 388, chasmTo = 393, chasmFloor = Feet + 60;
		var rows = Enumerable.Repeat(new string('.', width), Feet + 1).ToList();
		rows[Feet] = "...S" + new string('.', width - 4);
		while (rows.Count < chasmFloor + 5)
			rows.Add(new string('s', width));
		rows.Add(new string('@', width));
		var g = new FakeGame(rows.ToArray());
		for (int x = chasmFrom; x <= chasmTo; x++)
			for (int y = Feet + 1; y < chasmFloor; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		BotContext ctx = Armoured(g);
		var ground = new TilePos(360, Feet);
		MoveTo(g, ctx, ground.X, Feet);
		ctx.EvilErrand = true;
		var hunt = new HuntSkill(FakeGame.RottenChunk, FakeGame.FlyingAi, 15) { Ground = ground };
		hunt.Start(ctx);
		hunt.Tick(ctx); // the first wander: one wander's reach east, over the chasm
		string d = hunt.Describe();
		output.WriteLine(d);
		Assert.Equal("wandering", hunt.State);
		var m = System.Text.RegularExpressions.Regex.Match(d, @"goal \w+\((\d+),(\d+)\)");
		Assert.True(m.Success, d);
		int goalX = int.Parse(m.Groups[1].Value), goalY = int.Parse(m.Groups[2].Value);
		Assert.True(goalY <= Feet && (goalX < chasmFrom || goalX > chasmTo), $"wandering to ({goalX},{goalY})"); // up on the surface
	}

	/// <summary>The chasm world's chasm: its columns and its floor's feet row, far deeper than a safe fall (25) under the lips.</summary>
	private const int ChasmFrom = 388, ChasmTo = 393, ChasmFloor = Feet + 60;

	/// <summary>
	/// <see cref="CorruptWorld"/>'s corruption over deep stone, with a chasm at x 388-393 down to row <see cref="ChasmFloor"/>,
	/// as the M10 world's at x 1700-1704 and 1777 (floors at rows 362-401, the surface at 269-294).
	/// </summary>
	private static FakeGame ChasmWorld(int floor = ChasmFloor, bool corrupt = true)
	{
		const int width = 500;
		var rows = Enumerable.Repeat(new string('.', width), Feet + 1).ToList();
		rows[Feet] = "...S" + new string('.', width - 4);
		while (rows.Count < floor + 5)
			rows.Add(new string('s', width));
		rows.Add(new string('@', width));
		var g = new FakeGame(rows.ToArray());
		for (int x = CorruptFrom; x <= CorruptTo && corrupt; x++)
			for (int y = Feet + 1; y < Feet + 13; y++)
				g.TileTypes[new TilePos(x, y)] = y == Feet + 1 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		for (int x = ChasmFrom; x <= ChasmTo; x++)
			for (int y = Feet + 1; y < floor; y++) {
				g.Grid.Set(x, y, CellKind.Empty);
				g.TileTypes.Remove(new TilePos(x, y));
			}
		return g;
	}

	[Fact]
	public void TheCorruptionsChasms_AreBridged_BeforeTheHunt_AndTheHuntComesAfter()
	{
		// Measured run M10c (world_eow): three deaths by falls into the corruption's chasms, "Hurt 715 (fall damage) at tile
		// (1702,364)", 665 at (1701,362) and 765 at (1778,396), the surface at rows 269-294: knocked off a lip by eaters of
		// souls and a zombie, and run off one. Players bridge them with a row of dirt; so does the plan, once, first.
		FakeGame g = ChasmWorld();
		BotContext ctx = Armoured(g);
		g.Items[FakeGame.DirtBlock] = 1000;
		MoveTo(g, ctx, 350, Feet);
		var p = new ProgressionPlanner();
		var chunks = new HaveItem(FakeGame.RottenChunk, ProgressionPlanner.WormFoodChunks, "rotten chunks");
		ISkill s = p.ResolveForTest(chunks, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		var build = Assert.IsType<BuildSkill>(s);
		Assert.True(build.Blueprint.IsBridge);
		Assert.Equal(new TilePos(ChasmFrom, Feet + 1), build.Origin);
		Assert.Equal(ChasmTo - ChasmFrom + 1, build.Blueprint.Width);
		Assert.Contains($"bridging the chasm at x {ChasmFrom}-{ChasmTo}", p.Status);

		build.Start(ctx);
		SkillStatus st = SkillStatus.Running;
		int lowest = ctx.Node.Y;
		for (int t = 0; t < 60 * 60 * 2 && st == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			st = build.Tick(ctx);
			g.Step(ctx);
			lowest = System.Math.Max(lowest, ctx.Node.Y);
		}
		output.WriteLine($"{st}: {build.Describe()}, at {ctx.Node}, feet down to row {lowest}");
		Assert.Equal(SkillStatus.Succeeded, st);
		for (int x = ChasmFrom; x <= ChasmTo; x++)
			Assert.True(g.Grid.Get(x, Feet + 1).IsBlock(), $"no block at ({x},{Feet + 1})");
		Assert.True(lowest <= Feet + 1, $"down to row {lowest} building it");
		p.OnSkillFinished(build, st, ctx);

		// Bridged: the hunt, and the bridge is kept from the paths' picks.
		s = p.ResolveForTest(chunks, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.IsType<HuntSkill>(s);
		p.Next(ctx);
		Assert.Contains(new TilePos(ChasmFrom + 2, Feet + 1), ctx.Protected);
	}

	[Fact]
	public void LootAtAChasmsFloor_DeeperThanASafeFall_IsLeft_TheLootOnTheSurfaceIsNot()
	{
		// Measured run M10c: a fight's coins had fallen to a corruption chasm's floor, "Collect [fetching Copper Coin x11]" at
		// (1700,361), some 90 rows under the surface the fight was on. Here the floor is 34 rows down (a safe fall is 25), in
		// the boss loot's reach (BotBrain.BossLootRadiusTiles, 40), and a coin lies on the surface too.
		FakeGame g = ChasmWorld(floor: Feet + 35, corrupt: false);
		BotContext ctx = g.CreateContext(output);
		MoveTo(g, ctx, ChasmFrom - 4, Feet);
		g.Drops.Add(new DropInfo(900, 71, "Copper Coin", 11, ChasmFrom * 16f + 20, (Feet + 34) * 16f + 4, 8, 8)); // on the chasm's floor
		g.Drops.Add(new DropInfo(901, 71, "Copper Coin", 3, (ChasmFrom - 12) * 16f + 4, Feet * 16f + 4, 8, 8)); // on the surface
		g.Refresh(ctx);
		Assert.True(CollectDropsSkill.AnyInRange(ctx, _ => true, BotBrain.BossLootRadiusTiles));
		var collect = new CollectDropsSkill(_ => true, BotBrain.BossLootRadiusTiles);
		collect.Start(ctx);
		SkillStatus st = SkillStatus.Running;
		int lowest = ctx.Node.Y;
		for (int t = 0; t < 60 * 60 && st == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			st = collect.Tick(ctx);
			g.Step(ctx);
			lowest = System.Math.Max(lowest, ctx.Node.Y);
		}
		output.WriteLine($"{st}: {collect.Describe()}, at {ctx.Node}, feet down to row {lowest}, drops left {string.Join(", ", g.Drops.Select(d => d.Tile))}");
		Assert.Equal(SkillStatus.Succeeded, st);
		Assert.True(lowest <= Feet + 1, $"went down to row {lowest} for the coins in the chasm");
		Assert.DoesNotContain(g.Drops, d => d.Id == 901);
		Assert.False(CollectDropsSkill.AnyInRange(ctx, _ => true, BotBrain.BossLootRadiusTiles)); // the brain starts no collect for it
	}

	[Fact]
	public void ChasmOpenings_AreTheDropsDeeperThanASafeFall_NotTheDipsNorACliffsFoot()
	{
		FakeGame g = ChasmWorld();
		for (int x = 420; x <= 423; x++)
			for (int y = Feet + 1; y < Feet + 10; y++)
				g.Grid.Set(x, y, CellKind.Empty); // a dip 9 rows deep: walked down into and out of, no bridge
		for (int x = 435; x < 435 + Chasms.MaxBridgeTiles + 5; x++)
			for (int y = Feet + 1; y < Feet + 31; y++)
				g.Grid.Set(x, y, CellKind.Empty); // a valley 30 rows down, wider than any chasm: its foot is walked to, no bridge
		var found = Chasms.Openings(g.Grid, CorruptFrom, CorruptTo, Core.Physics.PhysicsParams.Default.SafeFallTiles);
		output.WriteLine(string.Join(", ", found));
		Assert.Equal(new[] { new ChasmOpening(ChasmFrom, ChasmTo, Feet + 1) }, found);
	}

	[Fact]
	public void WormFood_IsSummonedByDay_InTheCorruption_WhileTheEyeStillWaitsForNight()
	{
		FakeGame g = CorruptWorld();
		BotContext ctx = Armoured(g);
		g.Items[FakeGame.WormFood] = 1;
		g.Items[FakeGame.SuspiciousEye] = 1;
		var p = new ProgressionPlanner();
		Assert.False(ctx.IsNight);

		// From home, by day: to the corruption, not a wait for night.
		ISkill fromHome = p.ResolveForTest(Eater, ctx);
		output.WriteLine($"{p.Status} -> {fromHome?.Describe()}");
		var walk = Assert.IsType<WalkToSkill>(fromHome);
		Assert.True(ctx.EvilErrand);
		Assert.True(ctx.InEvilBiome(walk.Target) || ctx.CorruptionAround(walk.Target) >= BotContext.CorruptionZoneTiles, $"walking to {walk.Target}");

		// In the corruption, by day: summoned and fought there.
		MoveTo(g, ctx, InTheCorruption.X, Feet);
		Assert.True(ctx.CorruptionAround(ctx.Node) >= BotContext.CorruptionZoneTiles);
		ISkill there = p.ResolveForTest(Eater, ctx);
		output.WriteLine($"{p.Status} -> {there?.Describe()}");
		var fight = Assert.IsType<BossFightSkill>(there);
		Assert.Equal(FakeGame.EowHead, fight.BossType);
		Assert.Null(fight.Arena); // no arena: the open ground of the corruption

		// The Eye's rules are its own: by day it still waits for night.
		var eye = new BossDefeated(FakeGame.EyeNpc, FakeGame.SuspiciousEye, new[] { FakeGame.ServantNpc }, "Eye of Cthulhu");
		var wait = Assert.IsType<WaitSkill>(p.ResolveForTest(eye, ctx));
		Assert.Equal("night", wait.What);
	}

	[Fact]
	public void TheMushroomJob_PicksOnlyTheMushrooms_AmongTheCorruptPlants()
	{
		FakeGame g = CorruptWorld();
		BotContext ctx = Armoured(g);
		MoveTo(g, ctx, InTheCorruption.X, Feet);
		ctx.EvilErrand = true;
		g.AddPlant(354, Feet, mushroom: false);
		g.AddPlant(357, Feet, mushroom: true);
		g.AddPlant(361, Feet, mushroom: true);
		var pick = new MineSkill(FakeGame.VileMushroom, FakeGame.TileCorruptPlants, 2) { FrameX = FakeGame.VileMushroomFrame, Bias = ExploreBias.Sideways };
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, pick, 60 * 60);
		output.WriteLine($"{st}: {pick.Describe()}, mushrooms {g.Count(FakeGame.VileMushroom)}");
		Assert.Equal(SkillStatus.Succeeded, st);
		Assert.Equal(2, g.Count(FakeGame.VileMushroom));
		Assert.Equal(FakeGame.TileCorruptPlants, g.TileTypeAt(354, Feet)); // the plain plant is left
	}

	/// <summary>The wide world's corruption columns: x 400-580 of 900, the ground either side of it not seen yet.</summary>
	private const int WideFrom = 400, WideTo = 580;

	/// <summary>
	/// 900 wide, the corruption at x 400-580 (corrupt grass over ebonstone), the ground either side not seen yet: new ground
	/// for an exploration to go into, as the M10 world's beyond its corruption (x 1765-1960).
	/// </summary>
	private static FakeGame WideCorruptWorld()
	{
		FakeGame g = ProgressionTests.Mine(width: 900, sky: Feet + 1);
		for (int x = WideFrom; x <= WideTo; x++)
			for (int y = Feet + 1; y < Feet + 13; y++)
				g.TileTypes[new TilePos(x, y)] = y == Feet + 1 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		for (int x = 0; x < g.Grid.Width; x++)
			if (x < WideFrom || x > WideTo)
				for (int y = 0; y < g.Grid.Height; y++)
					g.Unrevealed.Add(new TilePos(x, y));
		return g;
	}

	/// <summary>The planner's mushroom job, the bot standing in the wide world's corruption at <paramref name="x"/>.</summary>
	private (FakeGame, BotContext, ISkill) MushroomJobIn(int x)
	{
		FakeGame g = WideCorruptWorld();
		BotContext ctx = Armoured(g);
		MoveTo(g, ctx, x, Feet);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(new HaveItem(FakeGame.VileMushroom, 6, "vile mushrooms"), ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.IsType<MineSkill>(s);
		s.Start(ctx);
		return (g, ctx, s);
	}

	private static void Step(FakeGame g, BotContext ctx, ISkill s)
	{
		g.Refresh(ctx);
		s.Tick(ctx);
		g.Step(ctx);
	}

	[Fact]
	public void NoMushroomInSight_TheJobWalksTheKnownCorruptionEndToEnd_NotOffIntoNewGround()
	{
		// Measured run M10b: with none in sight, "Explore for Vile Mushroom (Sideways)" legs out to (1625,268), 140 tiles past
		// the corruption's west end, while the mushrooms it got grew back in the corruption it had seen.
		var (g, ctx, job) = MushroomJobIn(450);
		int minX = ctx.Node.X, maxX = ctx.Node.X;
		string explored = null;
		for (int t = 0; t < 60 * 60; t++) {
			Step(g, ctx, job);
			minX = System.Math.Min(minX, ctx.Node.X);
			maxX = System.Math.Max(maxX, ctx.Node.X);
			if (explored == null && job.Describe().Contains("Explore"))
				explored = job.Describe();
		}
		output.WriteLine($"{job.Describe()}; walked x {minX}-{maxX}");
		Assert.Null(explored);
		const int tolerance = 10; // the pass's end (PatrolSkill.ArriveTiles) and the last chunk's columns (16-tile chunks)
		Assert.True(minX <= WideFrom + tolerance && maxX >= WideTo - tolerance, $"walked x {minX}-{maxX}, not end to end of {WideFrom}-{WideTo}");
		Assert.True(minX >= WideFrom - tolerance && maxX <= WideTo + tolerance, $"walked x {minX}-{maxX}, off the corruption {WideFrom}-{WideTo}");
	}

	[Fact]
	public void AMushroomGrownBack_OnTheStretchWalked_IsPicked()
	{
		// A plant grows back on corrupt grass by the game's random tile updates (WorldGen.cs:68894), a mushroom 1 in 24 or so
		// (WorldGen.cs:54373): here one appears behind the bot, out of sight, on ground it walked a while ago.
		var (g, ctx, job) = MushroomJobIn(450);
		for (int t = 0; t < 900; t++)
			Step(g, ctx, job);
		int x = ctx.Node.X > (WideFrom + WideTo) / 2 ? WideFrom + 12 : WideTo - 12;
		output.WriteLine($"at tick 900 at {ctx.Node}: a mushroom grows at ({x},{Feet}) [{job.Describe()}]");
		g.AddPlant(x, Feet, mushroom: true);
		int ticks = 0;
		for (; ticks < 60 * 60 && g.Count(FakeGame.VileMushroom) == 0; ticks++)
			Step(g, ctx, job);
		output.WriteLine($"{ticks} ticks later: {g.Count(FakeGame.VileMushroom)} mushroom(s), at {ctx.Node} [{job.Describe()}]");
		Assert.Equal(1, g.Count(FakeGame.VileMushroom));
	}

	[Fact]
	public void ThePlainCorruptPlants_OnTheStretchWalked_AreCut_SoNewOnesGrow()
	{
		// World generation put a plant on every corrupt grass top (WorldGen.cs:15544-15548), and a cell with one grows
		// nothing: only cut cells grow back, a mushroom at 0.0016 a cell each game hour (PatrolSkill's remarks).
		var (g, ctx, job) = MushroomJobIn(450);
		int[] plants = { 420, 470, 520, 560 };
		foreach (int x in plants)
			g.AddPlant(x, Feet, mushroom: false);
		for (int t = 0; t < 60 * 60; t++)
			Step(g, ctx, job);
		output.WriteLine(job.Describe());
		Assert.All(plants, x => Assert.NotEqual(FakeGame.TileCorruptPlants, g.TileTypeAt(x, Feet)));
	}

	[Fact]
	public void TheDemonBow_IsMadeOnlyFromTheDemoniteInTheBag()
	{
		FakeGame g = CorruptWorld();
		BotContext ctx = Armoured(g);
		var bow = new FromBag(new HaveItem(FakeGame.DemonBow, 1, "demon bow"));
		var p = new ProgressionPlanner();
		p.KnownStations[FakeGame.TileFurnace] = new TilePos(8, Feet);
		p.KnownStations[FakeGame.TileAnvil] = new TilePos(1, Feet);
		Assert.Null(p.ResolveForTest(bow, ctx)); // none in the bag: the iron bow does, no mining for demonite
		Assert.Null(p.ResolveForTestStuck);
		g.Items[FakeGame.DemoniteOre] = 23; // a bar short of 8 bars
		Assert.Null(p.ResolveForTest(bow, ctx));
		g.Items[FakeGame.DemoniteOre] = 24;
		ISkill s = p.ResolveForTest(bow, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.True(s is CraftSkill { ItemType: FakeGame.DemoniteBar } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.DemoniteBar}"), p.Status);
		g.Items[FakeGame.DemoniteOre] = 0;
		g.Items[FakeGame.DemoniteBar] = 8; // smelted already
		s = p.ResolveForTest(bow, ctx);
		Assert.True(s is CraftSkill { ItemType: FakeGame.DemonBow } || s is WalkToSkill && p.Status.Contains($"item{FakeGame.DemonBow}"), p.Status);
	}
}
