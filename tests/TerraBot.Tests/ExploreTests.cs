using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Only what the player has seen counts; exploring reveals the rest.</summary>
public class ExploreTests
{
	private readonly ITestOutputHelper output;
	public ExploreTests(ITestOutputHelper output) => this.output = output;

	/// <summary>140 wide: grass at row 25, stone below, an iron vein at columns 100-115 (rows 29-31), all of it past column 50 unseen.</summary>
	private static FakeGame FarOre()
	{
		FakeGame g = ProgressionTests.Mine(140, oreRows: 0);
		for (int x = 100; x <= 115; x++)
			for (int y = 29; y <= 31; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileIron;
		g.HideFrom(50);
		return g;
	}

	[Fact]
	public void UnseenOre_IsNotKnown()
	{
		FakeGame g = FarOre();
		Assert.Empty(g.FindTiles(FakeGame.TileIron, new TilePos(105, 30), 20));
	}

	[Fact]
	public void Explore_RevealsTheFarOre()
	{
		FakeGame g = FarOre();
		BotContext ctx = g.CreateContext(output);
		var explore = new ExploreSkill("iron ore", c => c.World.FindTiles(FakeGame.TileIron, c.Node, MineSkill.SearchRadius).Count > 0, ExploreBias.Sideways, heading: 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, explore, 60 * 60 * 3);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, {explore.Legs} legs, at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}

	[Fact]
	public void Mine_ExploresForOreItHasNotSeen()
	{
		FakeGame g = FarOre();
		BotContext ctx = g.CreateContext(output);
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, mine, 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks ({mine.Describe()}), ore {g.Count(FakeGame.IronOre)}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Count(FakeGame.IronOre) >= 3);
	}

	[Fact]
	public void JustBelowTheDepthLimit_DigsBackUp_InsteadOfGoingNowhere()
	{
		// world_eye run 36: 4 rows below the floor, "dig up to the floor" was within the walk's tolerance, and 14 legs in
		// a row ended at once without a step.
		var rows = new System.Collections.Generic.List<string> { new string('.', 60), new string('.', 60) };
		for (int y = 2; y < 22; y++)
			rows.Add(new string('s', 60));
		for (int y = 22; y < 25; y++)
			rows.Add(new string('s', 30) + (y == 24 ? "...S...." : "........") + new string('s', 22));
		for (int y = 25; y < 31; y++)
			rows.Add(new string('s', 60));
		rows.Add(new string('@', 60));
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 20 - BotContext.ShallowMargin; // floor at row 20 with no armour
		Assert.Equal(20, ctx.DeepestRow);
		var explore = new ExploreSkill("the way up", c => c.Node.Y <= c.DeepestRow, ExploreBias.Down, heading: 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, explore, 60 * 60 * 2);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, {explore.Legs} legs, at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(explore.Legs <= 3, $"{explore.Legs} legs");
	}

	[Fact]
	public void AfterOreFound_TheNextExploration_CarriesOnTheWayTheLastOneWent()
	{
		// Audit of the 10-day world_eye runs (2026-10-16): each exploration turned the other way from the one before, so the
		// second walked back over the ground the first had just explored. Here the first goes east and finds a little ore;
		// with it mined, the next exploration goes on east, not back west over the way it came.
		FakeGame g = ProgressionTests.Mine(200, oreRows: 0);
		for (int x = 100; x <= 102; x++)
			g.TileTypes[new TilePos(x, 29)] = FakeGame.TileIron;
		g.HideFrom(50);
		BotContext ctx = g.CreateContext(output);
		var log = (TestLog)ctx.Log;
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 10);
		mine.Start(ctx);
		string[] Starts() => log.Lines.Where(l => l.StartsWith("Explore start")).ToArray();
		for (int t = 0; t < 60 * 60 * 6 && Starts().Length < 2; t++) {
			g.Refresh(ctx);
			if (mine.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
		}
		string[] starts = Starts();
		output.WriteLine(string.Join("\n", starts) + $"\nmined {mine.Mined}, at {g.Sim.Node}");
		Assert.True(mine.Mined >= 3, $"the ore not mined ({mine.Describe()})");
		Assert.Equal(2, starts.Length);
		Assert.Contains("heading right", starts[0]);
		Assert.Contains("heading right", starts[1]);
	}

	[Fact]
	public void DiggingOn_FacingBackDownItsOwnTunnel_GoesIntoFreshRock()
	{
		// Audit of the 10-day world_eye runs (2026-10-16): at the gear's depth floor legs are level tunnels, and "digging on"
		// 25 tiles from where the bot stood, facing back from a tunnel's far end, went back through the open tunnel.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 40; y++) {
			if (y < 10)
				rows.Add(new string('.', 120));
			else if (y >= 20 && y <= 22)
				rows.Add(new string('s', 20) + (y == 22 ? new string('.', 60) + "S." : new string('.', 62)) + new string('s', 38));
			else
				rows.Add(new string(y == 39 ? '@' : 's', 120));
		}
		var g = new FakeGame(rows.ToArray());
		g.HideFrom(86); // the rock east of the tunnel's end not seen yet
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 22 - BotContext.ShallowMargin; // the depth floor at the tunnel: no digging down
		Assert.Equal(22, ctx.DeepestRow);
		foreach (int x in new[] { 45, 70 })
			ctx.Explored.Add(new TilePos(x, 22)); // the legs that dug the tunnel eastwards
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: -1);
		explore.Start(ctx);
		g.Refresh(ctx);
		explore.Tick(ctx);
		string leg = ((TestLog)ctx.Log).Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\) \((.+?)\) from \((\d+),");
		Assert.Equal("digging on", m.Groups[3].Value);
		Assert.True(int.Parse(m.Groups[1].Value) > int.Parse(m.Groups[4].Value), "back through the open tunnel");
	}

	[Fact]
	public void FruitlessExplorations_AreCountedAcrossRestarts()
	{
		// world_eye run 42: a fight every few seconds restarted mining, its own count went back to 0, and it never gave up.
		FakeGame g = ProgressionTests.Mine(oreRows: 0);
		BotContext ctx = g.CreateContext(output);
		ctx.ExploresWithoutOre[FakeGame.IronOre] = MineSkill.MaxExplores;
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, mine, 60);
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("explorations", mine.FailureReason);
		// ...and the next job, after travelling on, starts afresh (run 47: every job failed at once, travel went 1,900 tiles).
		Assert.False(ctx.ExploresWithoutOre.ContainsKey(FakeGame.IronOre));
	}
}

/// <summary>Water: ore beside it is left alone, swimming with the head under costs a lot.</summary>
public class WaterTests
{
	private readonly ITestOutputHelper output;
	public WaterTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void OreBesideWater_IsMinedToo_AndTheVeinIsFinished()
	{
		// It used to be left alone (a ban); user, 2026-10-14: solve the problem, don't ban the place. The bot copes with the
		// water (surfacing for breath), and once the amount is in it finishes the vein instead of leaving ore behind.
		FakeGame g = ProgressionTests.Mine(); // iron at columns 10-30, rows 29-31
		for (int x = 14; x <= 20; x++)
			g.Grid.Set(x, 28, CellKind.Water | CellKind.Empty); // a flooded pocket over part of the vein
		BotContext ctx = g.CreateContext(output);
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, mine, 60 * 120));
		output.WriteLine($"mined {mine.Mined}, iron {g.Count(FakeGame.IronOre)}");
		Assert.True(mine.Mined > 3, $"mined only {mine.Mined}: the rest of the vein left behind");
		Assert.Equal(0, ctx.Deaths);
	}

	[Fact]
	public void OreSeenBefore_FarAway_IsGoneBackFor_NotExploredPast()
	{
		// User report (2026-10-14): it left ore behind and later explored elsewhere instead of going back to it.
		FakeGame g = ProgressionTests.Mine(width: 200); // iron at columns 10-30, seen (revealed)
		g.Sim.State.X += 100 * 16; // now 70-90 tiles from it, beyond the 40-tile look-around
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		mine.Start(ctx);
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			mine.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(mine.Describe());
		Assert.DoesNotContain("exploring", mine.Describe());
	}

	[Fact]
	public void MiningStone_StopsAtTheAmount_NoVeinOfTheWholeWorld()
	{
		// settle_auto with vein finishing: "need 20 more Stone Block" after 776 blocks broken; done was never reached (a new
		// target was always picked first), and a "vein" of stone is the whole world.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		var mine = new MineSkill(FakeGame.StoneBlock, FakeGame.TileStone, 5);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, mine, 60 * 120));
		output.WriteLine($"mined {mine.Mined}");
		Assert.True(mine.Mined <= 8, $"mined {mine.Mined} for 5");
	}

	[Fact]
	public void HeadUnderWater_CostsFarMoreThanWading()
	{
		var grid = ArrayTileGrid.Parse("......", "~~~~~~", "~~~~~~", "~~~~~~", "######");
		var moves = new TerraBot.Core.Pathfinding.MoveContext(grid, TerraBot.Core.Physics.PhysicsParams.Default);
		float wading = moves.Adjust(10f, new TilePos(1, 1)); // body rows -1..1: the head (row -1) is out
		float under = moves.Adjust(10f, new TilePos(1, 3)); // body rows 1..3, all water
		Assert.True(under >= wading * 4f, $"under {under}, wading {wading}");
	}

	[Fact]
	public void ACaveBehind_IsExplored_BeforeDiggingBlindAhead()
	{
		// Measured run S1: 287 blind digging legs against 141 into caves, ore found in 12 of 70 explorations: a frontier only
		// counted ahead, and behind only after a blind leg had failed.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 40; y++)
			rows.Add(new string('s', 90));
		rows[20] = new string('s', 5) + new string('.', 45) + new string('s', 40);
		rows[21] = new string('s', 5) + new string('.', 45) + new string('s', 40);
		rows[22] = new string('s', 5) + new string('.', 43) + "S." + new string('s', 40);
		rows.Add(new string('@', 90));
		var g = new FakeGame(rows.ToArray());
		for (int x = 0; x < 30; x++) // the cave's far end not seen yet
			for (int y = 0; y < 41; y++)
				g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		var explore = new ExploreSkill("nothing", _ => false, ExploreBias.Down, heading: 1);
		explore.Start(ctx);
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			explore.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(explore.Describe());
		Assert.Contains("(frontier)", string.Join(" ", ((TestLog)ctx.Log).Lines));
	}

	/// <summary>
	/// Measured runs T0a/T0b: every mining job looked for one ore only (164-199 "Mine" heartbeats a run, ore in 13 of 42 and 16
	/// of 60 explorations) while ore of the other tier was in sight. A player takes whatever ore turns up.
	/// </summary>
	[Fact]
	public void MiningForIronGear_TakesSilverOreInSight_AndThePlanPicksAgain()
	{
		FakeGame g = ProgressionTests.Mine();
		foreach (TilePos c in g.TileTypes.Where(kv => kv.Value == FakeGame.TileIron).Select(kv => kv.Key).ToList())
			g.TileTypes[c] = FakeGame.TileSilver; // only silver-tier ore in this ground
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 24);
		BotContext ctx = g.CreateContext(output);
		var p = new Bot.Brain.ProgressionPlanner();
		ISkill job = p.ResolveForTest(new Bot.Brain.Wearing(new[] { FakeGame.IronHelmet, FakeGame.SilverHelmet }, "helmet"), ctx);
		output.WriteLine($"{p.Status} -> {job?.Describe()}");
		var mine = Assert.IsType<MineSkill>(job);
		Assert.Equal(FakeGame.IronOre, mine.ItemType);
		Assert.Contains(mine.Also, a => a.Item == FakeGame.SilverOre);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, mine, 60 * 60 * 2));
		Assert.True(g.Count(FakeGame.SilverOre) > 0, "no silver ore taken");
		ISkill then = p.ResolveForTest(new Bot.Brain.Wearing(new[] { FakeGame.IronHelmet, FakeGame.SilverHelmet }, "helmet"), ctx);
		output.WriteLine($"{p.Status} -> {then?.Describe()}");
		Assert.False(then is MineSkill { ItemType: FakeGame.IronOre }, "back to looking for iron with the silver in the bag");
	}

	[Fact]
	public void AFrontierLookedAtAlready_IsNotGoneBackTo()
	{
		// Measured run S7: legs 10, 12 and 14 of one exploration went to the same frontier, (2024,476): the cells unseen
		// near it stayed unseen however often the bot stood there, with "digging on" legs between, back and forth.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 40; y++)
			rows.Add(new string('s', 90));
		rows[20] = new string('s', 5) + new string('.', 45) + new string('s', 40);
		rows[21] = new string('s', 5) + new string('.', 45) + new string('s', 40);
		rows[22] = new string('s', 5) + new string('.', 43) + "S." + new string('s', 40);
		rows.Add(new string('@', 90));
		var g = new FakeGame(rows.ToArray());
		for (int x = 6; x < 20; x++) // in the rock above the cave's far end: no looking reveals it
			for (int y = 16; y < 20; y++)
				g.NeverRevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		var explore = new ExploreSkill("nothing", _ => false, ExploreBias.Down, heading: -1);
		explore.Start(ctx);
		for (int t = 0; t < 60 * 60 * 2 && explore.Legs < 8; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			if (explore.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
		}
		var frontierLegs = ((TestLog)ctx.Log).Lines.Where(l => l.Contains("(frontier)")).ToList();
		foreach (string l in frontierLegs)
			output.WriteLine(l);
		Assert.True(frontierLegs.Count <= 3, $"{frontierLegs.Count} legs to frontiers by the unseeable rock");
	}

	[Fact]
	public void ACrystalInTheUndergroundDesert_IsGoneFor_InAFullSetOfArmour()
	{
		// Measured run S1: two crystals in the underground desert "in a biome the gear is too weak for" (its gate is defense
		// 16, more than full iron gives) while the bot explored for others for three game days.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 100; y++)
			rows.Add(y == 29 ? ".....S" + new string('.', 94) : y < 30 ? new string('.', 100) : new string('#', 100));
		var g = new FakeGame(rows.ToArray());
		for (int x = 0; x < 100; x++)
			for (int y = 30; y < 100; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileSand;
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 30;
		var c = new TilePos(50, 60);
		ctx.Combat.Defense = 4;
		Assert.Equal("in a biome the gear is too weak for", MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true));
		ctx.Combat.Defense = 9; // full iron
		Assert.Null(MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true));
		Assert.NotNull(MineSkill.WhyNotMine(ctx, ctx.Moves, c)); // ore there still waits for better gear
	}

	[Fact]
	public void ACrystalJustBelowTheDepthLimit_IsGoneFor_InAFullSetOfArmour()
	{
		// Measured run S4: four crystals seen below row 701 left alone while the bot explored six game days and found one.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 320; y++)
			rows.Add(y == 9 ? ".....S" + new string('.', 54) : y < 10 ? new string('.', 60) : new string('#', 60));
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 30;
		ctx.Combat.Defense = 9; // full iron: DeepestRow = caverns + 150
		var c = new TilePos(20, ctx.DeepestRow + 50);
		Assert.Null(MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true));
		Assert.NotNull(MineSkill.WhyNotMine(ctx, ctx.Moves, c)); // ore that deep waits
		Assert.NotNull(MineSkill.WhyNotMine(ctx, ctx.Moves, new TilePos(20, ctx.DeepestRow + MineSkill.ValuableDepthMargin + 5), valuable: true));
	}

	[Fact]
	public void ACrystalJustBelowTheDepthLimit_IsGoneFor_InAMixedFullSet()
	{
		// Measured run U2: a silver helmet with iron chainmail and greaves is 7 defense (no set bonus); four crystals at rows
		// 525-534 were left "below row 511", and the heart lantern's crystal was looked for to the end of the run.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 320; y++)
			rows.Add(y == 9 ? ".....S" + new string('.', 54) : y < 10 ? new string('.', 60) : new string('#', 60));
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 30;
		ctx.Combat.Defense = 7;
		ctx.Combat.ArmourPieces = 3;
		var c = new TilePos(20, ctx.DeepestRow + 50);
		Assert.Null(MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true));
		ctx.Combat.ArmourPieces = 2; // not a full set: the gear's own limit
		Assert.NotNull(MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true));
	}

	[Fact]
	public void ExploringForACrystalInAFullSet_GoesAsDeepAsTheCrystalMayBe()
	{
		// Measured run U7: crystals were taken down to row 701 but explored for only down to 601; five days from full armour
		// to max life 200.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 320; y++)
			rows.Add(y == 9 ? ".....S" + new string('.', 54) : y < 10 ? new string('.', 60) : new string('#', 60));
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 20;
		ctx.Combat.Defense = 9;
		int limit = ctx.DeepestRow;
		TilePos FirstLeg(int? deepest)
		{
			var explore = new ExploreSkill("crystal", _ => false, ExploreBias.Down, heading: 1) { DeepestRow = deepest };
			var lines = (TestLog)ctx.Log;
			int before = lines.Lines.Count;
			g.Sim.State.Y = (limit - 2 - 2) * 16f; // standing just above the gear's limit
			explore.Start(ctx);
			g.Refresh(ctx);
			explore.Tick(ctx);
			string leg = lines.Lines.Skip(before).First(l => l.Contains("Explore: leg 1 to"));
			var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
			return new TilePos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
		}
		Assert.True(FirstLeg(null).Y <= limit, "the gear's own limit");
		Assert.True(FirstLeg(limit + MineSkill.ValuableDepthMargin).Y > limit, "down to the crystal depth");
	}

	[Fact]
	public void ExploredGround_IsRememberedAllGame_NotOnlyTheLast300Legs()
	{
		// Measured runs T0b, U1, U5: with the last 300 leg targets kept, of some 850 a run makes, later days' crystal searches
		// went back over the same legs ("leg 2 to (2094,318)" on days 6, 7 and 8 of U5).
		var ctx = new BotContext();
		ctx.Explored.Add(new TilePos(2094, 318));
		for (int i = 0; i < 1000; i++)
			ctx.Explored.Add(new TilePos(100 + (i % 50) * 25, 400 + i / 50 * 25));
		Assert.True(ExploreSkill.NearVisited(ctx, 2100, 322));
		Assert.False(ExploreSkill.NearVisited(ctx, 2120, 318));
		Assert.True(ctx.Explored.AnyWithin(-5, -5, 6, 6) == false);
		ctx.Explored.Add(new TilePos(-3, -20));
		Assert.True(ctx.Explored.AnyWithin(-5, -17, 3, 3));
	}
}
