using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
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

	[Fact]
	public void OreOnTheMap_AStepBelowTheDepthFloor_IsGoneBackFor()
	{
		// Measured run U22: the floor at row 436 the whole armour phase, and iron seen at (2029,441) and (2122,443), 5-7 rows
		// under it, sat on the map; once the +25 step came the bot went back for it and had 33 iron in about 2.5 game hours.
		// 140 wide, the bot at (3,24) on grass, stone down to row 79; an iron vein at columns 100-115 from row oreTop, seen (on
		// the map) and 97 tiles off: beyond the 40 in sight.
		static FakeGame Ground(int oreTop)
		{
			var rows = Enumerable.Repeat(new string('.', 140), 25).ToList();
			rows[24] = "...S" + new string('.', 136);
			rows.Add(new string('#', 140));
			rows.Add(new string('#', 140));
			while (rows.Count < 80)
				rows.Add(new string('s', 140));
			rows.Add(new string('@', 140));
			var g = new FakeGame(rows.ToArray());
			for (int x = 100; x <= 115; x++)
				for (int y = oreTop; y < oreTop + 3; y++)
					g.TileTypes[new TilePos(x, y)] = FakeGame.TileIron;
			return g;
		}
		FakeGame g = Ground(29);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage;
		ctx.CavernY = 26 - BotContext.ArmedMargin; // the floor at row 26: the vein 3-5 rows under it
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, mine, 60 * 60 * 2);
		output.WriteLine($"{s}: {mine.Describe()} {mine.FailureReason}; iron {g.Count(FakeGame.IronOre)}");
		foreach (string l in ((TestLog)ctx.Log).Lines.Where(l => l.StartsWith("Mine") || l.StartsWith("Explore")).Take(8))
			output.WriteLine(l);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("on the map") && l.Contains("below the depth floor"));
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Count(FakeGame.IronOre) >= 3);

		// More than a step (DepthBonusStep) under the floor, not: a cost up to the next step, not the floor gone.
		FakeGame g2 = Ground(26 + BotContext.DepthBonusStep + 1);
		BotContext ctx2 = g2.CreateContext(output);
		ctx2.Combat.WeaponDamage = BotContext.GoodWeaponDamage;
		ctx2.CavernY = 26 - BotContext.ArmedMargin; // the floor at row 26 again
		Assert.NotEmpty(g2.FindTiles(FakeGame.TileIron, new TilePos(105, 52), 5)); // on the map
		var capped = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		EarlyGameTests.RunSkill(g2, ctx2, capped, 5);
		output.WriteLine(capped.Describe());
		Assert.DoesNotContain(((TestLog)ctx2.Log).Lines, l => l.Contains("on the map"));
		Assert.Contains(((TestLog)ctx2.Log).Lines, l => l.Contains("exploring"));
	}

	/// <summary>
	/// 100 wide, open sky to row 9 (the bot at (50,9)), solid ground to row 120: an antlion charger seen 70 rows under the
	/// start, so the zone its sighting makes in weak gear (<see cref="BotContext.BiomeZoneRadius"/>) spans the whole width.
	/// </summary>
	private static (FakeGame Game, TilePos Seen) GroundUnderASighting()
	{
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 121; y++)
			rows.Add(y == 9 ? new string('.', 50) + "S" + new string('.', 49) : y < 10 ? new string('.', 100) : new string('#', 100));
		rows.Add(new string('@', 100));
		return (new FakeGame(rows.ToArray()), new TilePos(50, 79));
	}

	/// <summary>Explores down for the sighting's own rows from the start; the deepest feet row reached, and how it ended.</summary>
	private (SkillStatus Status, int Deepest) ExploreDownTo(FakeGame g, BotContext ctx, int row)
	{
		var explore = new ExploreSkill("the crystal band", c => c.Node.Y >= row, ExploreBias.Down, heading: 1);
		explore.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int deepest = ctx.Node.Y;
		for (int t = 0; t < ExploreSkill.MaxTicks + 1 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = explore.Tick(ctx);
			g.Step(ctx);
			deepest = System.Math.Max(deepest, ctx.Node.Y);
		}
		output.WriteLine($"{s} ({explore.FailureReason}) after {g.Sim.Ticks} ticks, {explore.Legs} legs, at {g.Sim.Node}, deepest row {deepest}");
		return (s, deepest);
	}

	[Fact]
	public void InAFullSet_ASightingZoneUnderTheStart_IsACost_AndTheExploringStillGoesDownIntoTheBand()
	{
		// Measured run U9: an antlion charger seen at (2142,471) in full armour made a zone 120 tiles across under the spawn for
		// 5 game days; 74% of the exploration legs in it stayed at row 412 or above, life crystals lie evenly in rows 375-900,
		// and 152 of the 175 crystal-seeking hours went on the last crystal, found within 21 hours of the zone lapsing.
		var (g, seen) = GroundUnderASighting();
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = BotContext.CavernDefense; // iron armour: under the underground desert's own gate (DeepDefense)
		FootingTests.SeeADesertMonster(ctx, seen);
		var (s, deepest) = ExploreDownTo(g, ctx, seen.Y);
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(deepest >= seen.Y, $"deepest row {deepest}");
	}

	[Fact]
	public void InWeakGear_ASightingZoneUnderTheStart_IsStillKeptOutOf()
	{
		// Runs 58 and 59: antlion chargers killed the bot in the desert's zones in weak gear; there the zone stays off limits.
		var (g, seen) = GroundUnderASighting();
		BotContext ctx = g.CreateContext(output);
		FootingTests.SeeADesertMonster(ctx, seen);
		int top = seen.Y - BotContext.BiomeZoneRadius;
		var (s, deepest) = ExploreDownTo(g, ctx, seen.Y);
		Assert.NotEqual(SkillStatus.Succeeded, s);
		Assert.True(deepest < top, $"into the zone: deepest row {deepest}, the zone from row {top}");
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
	public void FinishingTheVein_StopsAtMaxVeinExtra()
	{
		// The vein is finished past the amount (the next armour piece needs the same ore), but only up to MaxVeinExtra tiles:
		// with a "veining" flag the done check was skipped for any target, each next tile came from the usual search after
		// the drops were collected, and this job for 3 mined all 63 tiles of the vein in sight.
		FakeGame g = ProgressionTests.Mine(); // iron at columns 10-30, rows 29-31: 63 tiles
		BotContext ctx = g.CreateContext(output);
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, mine, 60 * 240));
		output.WriteLine($"mined {mine.Mined}, iron {g.Count(FakeGame.IronOre)}");
		Assert.True(mine.Mined > 3, $"mined only {mine.Mined}: the rest of the vein left behind");
		Assert.True(mine.Mined <= 3 + MineSkill.MaxVeinExtra, $"mined {mine.Mined} for 3 (vein bound {MineSkill.MaxVeinExtra})");
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

/// <summary>Crystal searches resume in the crystal band (BotContext.CrystalBandTop), not from the spawn and the rows above it.</summary>
public class CrystalBandTests
{
	private readonly ITestOutputHelper output;
	public CrystalBandTests(ITestOutputHelper output) => this.output = output;

	/// <summary>
	/// 80 wide, 200 deep: open sky down to row 9 (the surface), ground below, and a cave at columns 30-50, rows 110-120 with
	/// the bot in it at (40,120). Surface row 10 and caverns from 40: the crystal band from row 20. In a full set of armour.
	/// </summary>
	private static (FakeGame, BotContext) DeepCave(ITestOutputHelper output)
	{
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 200; y++) {
			char[] r = (y < 10 ? new string('.', 80) : new string('#', 80)).ToCharArray();
			if (y >= 110 && y <= 120)
				for (int x = 30; x <= 50; x++)
					r[x] = '.';
			if (y == 120)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 40;
		ctx.Combat.Defense = 9;
		ctx.Combat.ArmourPieces = 3;
		return (g, ctx);
	}

	private static void MoveTo(FakeGame g, BotContext ctx, int x, int feetRow)
	{
		var p = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(x, p), Y = Core.Pathfinding.Body.StandingTop(feetRow, p), ReleaseJump = true };
		g.Refresh(ctx);
	}

	private static void Ticks(FakeGame g, BotContext ctx, ISkill skill, int n)
	{
		for (int t = 0; t < n; t++) {
			g.Refresh(ctx);
			skill.Tick(ctx);
			g.Step(ctx);
		}
	}

	[Fact]
	public void ACrystalSearch_AfterADeath_GoesBackToWhereItLeftOffInTheBand_NotFromTheSpawn()
	{
		// Measured run U9: 14 of 28 crystal explorations began within 5 tiles of the spawn (after deaths, recalls and "back to
		// the spawn to look from there"), and 25% of the 288 crystal legs were above row 375, the top of the crystal band: the
		// shaft was dug again from the spawn each time.
		var (g, ctx) = DeepCave(output);
		var first = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		first.Start(ctx);
		Ticks(g, ctx, first, 100);
		Assert.Contains("Explore", first.Describe());
		first.Stop(ctx);
		MoveTo(g, ctx, 5, 9); // killed, and back at the spawn on the surface
		var next = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		next.Start(ctx);
		Ticks(g, ctx, next, 3);
		output.WriteLine(next.Describe());
		Assert.DoesNotContain("exploring", next.Describe());
		Assert.Contains("left off", next.Describe());
	}

	[Fact]
	public void TheSearchIsResumed_FromItsLatestSpot_NotFromInsideADangerZone()
	{
		// Where it left off: the latest spot, not the deepest (a crystal is as likely at any row of the band). A danger zone
		// round a death rules a spot out, and so does a death within a leg of it.
		var (g, ctx) = DeepCave(output);
		MoveTo(g, ctx, 5, 9);
		Assert.Equal(20, ctx.CrystalBandTop);
		ctx.CrystalSearch.AddRange(new[] { new TilePos(70, 90), new TilePos(45, 150), new TilePos(40, 130) });
		Assert.Equal(new TilePos(40, 130), MineSkill.SearchToResume(ctx));
		ctx.NoteDanger(new TilePos(42, 160), BotContext.DangerZoneTicks); // a death 30 rows below the latest spot
		Assert.Equal(new TilePos(70, 90), MineSkill.SearchToResume(ctx));
		MoveTo(g, ctx, 60, 60); // within a leg's reach of it: the exploration's own legs get there
		Assert.Null(MineSkill.SearchToResume(ctx));
	}

	[Fact]
	public void AfterADeath_TheSearchIsNotResumedWithinALegOfIt()
	{
		// Measured run U26: killed at (2152,720), (2138,748), (2163,772) and (2078,722), and after each the bot walked back
		// 396-424 tiles to where the search left off, (2113,682), (2103,706), (2106,706), (2114,683): 39-42 tiles from the
		// latest death, outside its 30-tile zone but within one leg (60 tiles) of it; the first legs from there were hurt by
		// the same wall creepers again.
		var (g, ctx) = DeepCave(output);
		MoveTo(g, ctx, 5, 9);
		ctx.CrystalSearch.AddRange(new[] { new TilePos(70, 90), new TilePos(25, 145) });
		ctx.NoteDanger(new TilePos(20, 180), BotContext.DangerZoneTicks);
		Assert.False(ctx.OffLimits(new TilePos(25, 145)), "35 tiles from the death: outside its zone");
		Assert.Equal(new TilePos(70, 90), MineSkill.SearchToResume(ctx));
		// Monsters merely seen there (a sighting's zone, not a death): resumed to as before.
		ctx.DangerZones.Clear();
		ctx.NoteDanger(new TilePos(20, 180), BotContext.DangerZoneTicks, BotContext.DangerZoneRadius, seen: true);
		Assert.Equal(new TilePos(25, 145), MineSkill.SearchToResume(ctx));
	}

	[Fact]
	public void TheSearchIsResumed_AboveTheGearsFloor_BeforeBelowIt()
	{
		// Measured run U26: the resume points (2113,682) and (2103,706) were 56-80 rows below the gear's floor (row 626), the
		// four deaths at rows 720-772; every crystal mined in the phase was at row 567 or above.
		var (g, ctx) = DeepCave(output);
		MoveTo(g, ctx, 5, 9);
		Assert.Equal(190, ctx.DeepestRow);
		ctx.CrystalSearch.AddRange(new[] { new TilePos(70, 90), new TilePos(40, 195) });
		Assert.Equal(new TilePos(70, 90), MineSkill.SearchToResume(ctx));
		ctx.CrystalSearch.RemoveAt(0); // only one below the floor: a cost, not a ban, so that one
		Assert.Equal(new TilePos(40, 195), MineSkill.SearchToResume(ctx));
	}

	/// <summary>
	/// 120 wide, 300 deep, all rock and unseen but for pockets (revealed, 5 wide, 3 high, standing row Y): the bot's at
	/// (40,<paramref name="botRow"/>) and the given ones. Surface row 10, caverns from 40 (band from row 20), full armour: the
	/// gear's floor row 190, a crystal's 290.
	/// </summary>
	private (FakeGame, BotContext) Pockets(int botRow, params (int X, int Y)[] pockets)
	{
		var all = new System.Collections.Generic.List<(int X, int Y)>(pockets) { (38, botRow) };
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 300; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			foreach (var (px, py) in all)
				if (y >= py - 2 && y <= py)
					for (int x = px; x < px + 5; x++)
						r[x] = '.';
			if (y == botRow)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 12; y < 300; y++)
				if (!all.Any(p => x >= p.X && x < p.X + 5 && y >= p.Y - 2 && y <= p.Y + 1))
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 40;
		ctx.Combat.Defense = 9;
		ctx.Combat.ArmourPieces = 3;
		Assert.Equal(190, ctx.DeepestRow);
		return (g, ctx);
	}

	private TilePos FirstLeg(FakeGame g, BotContext ctx)
	{
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		Ticks(g, ctx, mine, 3);
		string leg = ((TestLog)ctx.Log).Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		return new TilePos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
	}

	[Fact]
	public void ExploringForACrystal_InTheBand_DepthBuysNothing_TheNearerFrontierWins()
	{
		// Vanilla WorldGen "Life Crystals": each crystal's row is drawn evenly from the band, so going deeper in it finds no more.
		// Measured run U26: from (2166,567) all 15 legs of one exploration went down, to row 724; in the crystal phase 20 of
		// 45 legs were below the gear's floor (row 626), and 73 of its 81 hurts and all 4 deaths, but no crystal mined.
		var (g, ctx) = Pockets(100, (58, 100), (78, 145));
		TilePos leg = FirstLeg(g, ctx);
		Assert.True(leg.Y <= 100, $"the level frontier 18 tiles off, not the one 45 rows deeper: {leg}");
	}

	[Fact]
	public void ExploringForACrystal_BelowTheGearsFloor_EachRowCosts()
	{
		// Measured run U26 (gear's floor row 626, a crystal's 726): the four deaths at rows 720-772, 94-146 rows below the floor.
		var (g, ctx) = Pockets(185, (83, 185), (50, 215));
		TilePos leg = FirstLeg(g, ctx);
		Assert.True(leg.Y <= 190, $"the frontier at the floor 43 tiles off, not the one 25 rows below it 30 tiles off: {leg}");
	}

	[Fact]
	public void DiggingOn_IntoAnOpenCave_CountsWhereTheLegEnds_ItsFloor()
	{
		// Measured run U26: "leg 2 to (2101,726) (digging on)" from (2103,706): open cave there, so the walk's goal was its floor,
		// StandAt(2101,760), 34 rows below a crystal's floor (726) and 134 below the gear's (626); wall creepers hurt the bot
		// there 9 times and killed it at (2138,748). Likewise (2102,739) to row 760 and (2083,706) to row 729.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 300; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y >= 148 && y <= 150)
				for (int x = 38; x <= 42; x++)
					r[x] = '.';
			if (y >= 165 && y <= 205)
				for (int x = 25; x <= 55; x++)
					r[x] = '.'; // a cave below, its floor row 206, 15 rows below the gear's floor
			if (y == 150)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()); // all of it seen: no frontier, digging on
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 40;
		ctx.Combat.Defense = 9;
		ctx.Combat.ArmourPieces = 3;
		Assert.Equal(190, ctx.DeepestRow);
		TilePos leg = FirstLeg(g, ctx);
		Assert.True(leg.Y < 165, $"not down into the cave, whose floor is below the gear's: {leg}");
	}

	[Fact]
	public void NowhereToTravel_InTheCrystalBand_LooksOnFromThere_NotFromTheSpawn()
	{
		// Measured run U9: "no more Life Crystal found around here and nowhere to travel; back to the spawn to look from there",
		// 11 times, and each time the search went down from the spawn again.
		var (g, ctx) = DeepCave(output);
		ctx.SpawnTile = new TilePos(5, 9);
		var p = new ProgressionPlanner();
		p.NoteFruitless(FakeGame.LifeCrystal); // nowhere to travel in this narrow world
		ISkill skill = p.ResolveForTest(new LifeAtLeast(200, "max life"), ctx);
		output.WriteLine($"{p.Status} -> {skill?.Describe()}");
		Assert.IsType<MineSkill>(skill);
		// The search from here came to nothing again, the bot still here: the spawn, as before.
		p.NoteFruitless(FakeGame.LifeCrystal);
		skill = p.ResolveForTest(new LifeAtLeast(200, "max life"), ctx);
		output.WriteLine($"{p.Status} -> {skill?.Describe()}");
		Assert.IsType<WalkToSkill>(skill);
	}

	[Fact]
	public void ATripToNewGround_IsNotToWhereTheFruitlessSearchJustWas()
	{
		// Measured run U29 (d8 +8.5h to d10 +23h): standing at (2331,193), the crystal trip's "new ground" was (2335,193), 4
		// tiles off: the walk there ended at once, the job's three explorations ended at once over the ground it had explored,
		// and the next trip went there again; 63,674 explorations in the run, 24,517 of them from (2331,193), and runs of
		// 757 to 6,030 "travelling to new ground at (2335,193)" (later (2215,196)) in a row, the game clock running on.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 60; y++) {
			char[] r = (y < 50 ? new string('.', 1300) : new string('#', 1300)).ToCharArray();
			if (y == 49)
				r[770] = 'S'; // 120 tiles east of the spawn: the first trip's ground
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(650, 49);
		var p = new ProgressionPlanner();
		p.NoteFruitless(FakeGame.LifeCrystal);
		ISkill skill = p.ResolveForTest(new LifeAtLeast(200, "max life"), ctx);
		output.WriteLine($"{p.Status} -> {skill?.Describe()}");
		Assert.IsType<WalkToSkill>(skill);
		var m = System.Text.RegularExpressions.Regex.Match(p.Status, @"new ground at \((\d+),(\d+)\)");
		Assert.True(m.Success, p.Status);
		Assert.True(System.Math.Abs(int.Parse(m.Groups[1].Value) - 770) > ExploreSkill.MaxLegTiles, $"new ground where the bot stands: {p.Status}");
	}

	[Fact]
	public void ExploringForACrystal_AboveTheBand_PullsDownIntoIt()
	{
		// Measured run U9: 25% of the 288 crystal legs were above row 375, the top of the crystal band. A frontier down in the
		// band now beats one along the surface that is nearer.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 60; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y == 9)
				r[40] = 'S';
			if (y >= 18 && y <= 20)
				for (int x = 68; x <= 73; x++)
					r[x] = '.'; // a cave pocket, seen, with unseen rock round it
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 12; y < 60; y++)
				if (!(x >= 68 && x <= 73 && y >= 18 && y <= 21))
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 100; // the crystal band from row 40
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		var log = (TestLog)ctx.Log;
		Ticks(g, ctx, mine, 3);
		string leg = log.Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		Assert.True(int.Parse(m.Groups[2].Value) >= 18, "into the cave pocket, deeper towards the band, not along the surface");
	}

	/// <summary>
	/// The bag as the game adapter reads it: a drop picked up shows in the count a tick later (the drop gone from the
	/// world first). Every other question goes to the fake as it is.
	/// </summary>
	private sealed class LaggedBag : IInventory
	{
		private readonly FakeGame g;
		private readonly int item;
		public int Shown;
		public LaggedBag(FakeGame g, int item) { this.g = g; this.item = item; }
		public int Count(int itemType) => itemType == item ? Shown : g.Count(itemType);
		public bool HasRoomFor(int itemType) => g.HasRoomFor(itemType);
		public bool CanPickUp(int itemType) => g.CanPickUp(itemType);
		public bool IsEquipped(int itemType) => g.IsEquipped(itemType);
		public System.Collections.Generic.IReadOnlyList<int> UnwornAccessories() => g.UnwornAccessories();
		public int FreeSlots => g.FreeSlots;
		public System.Collections.Generic.IReadOnlyList<(int Type, int Stack)> Stacks() => g.Stacks();
		public bool IsGear(int itemType) => g.IsGear(itemType);
	}

	[Fact]
	public void ACrystalMined_LeavesNoFruitlessExploration_ForTheNextJob()
	{
		// Measured runs U25-U29: after each crystal mined, "Mine: no Life Crystal seen within 40 tiles; exploring" and an
		// "Explore start" came between the pickup and "Mine done" (U25 02:54:54.344, the drop collected, the bag's count a tick
		// behind), and that exploration stayed counted: the next crystal job began at 1 of its 3. U29 (d3 +3.7h to +13.5h):
		// two explorations near the spawn, and the third job failed "after 3 explorations in a row" before exploring at all.
		var (g, ctx) = DeepCave(output);
		g.AddHeart(44, 119);
		var bag = new LaggedBag(g, FakeGame.LifeCrystal);
		ctx.Inventory = bag;
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < 60 * 60 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = mine.Tick(ctx);
			bag.Shown = g.Count(FakeGame.LifeCrystal); // what the next tick's read will show
			g.Step(ctx);
		}
		output.WriteLine(string.Join("\n", ((TestLog)ctx.Log).Lines));
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(0, ctx.ExploresWithoutOre.GetValueOrDefault(FakeGame.LifeCrystal));
	}

	/// <summary>Runs the job until <paramref name="until"/> holds (checked each tick), at most <paramref name="max"/> ticks.</summary>
	private static void TicksUntil(FakeGame g, BotContext ctx, ISkill skill, System.Func<bool> until, int max)
	{
		for (int t = 0; t < max && !until(); t++) {
			g.Refresh(ctx);
			if (skill.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
		}
	}

	[Fact]
	public void AnExplorationCutOff_AfterLegsToNewGround_IsNotCountedFruitless_UntilItsLegsMakeAWholeOne()
	{
		// Measured run U29 (d3 +3.7h to +13.5h): the crystal search near the spawn walked 10 legs into the band, to (2189,499),
		// before a recall potion cut it off ("in the same place for 3 game hours"), and 4 more before a chest at (2160,537)
		// did; each counted as a whole fruitless exploration, and the third job failed "after 3 explorations in a row" and
		// went off to new ground at (1735,269). U28 (d3 +16.1h): 3 legs, cut off by a chest at (1983,331), counted the same.
		var (g, ctx) = Pockets(100);
		var log = (TestLog)ctx.Log;
		int Legs(string n) => log.Lines.Count(l => l.StartsWith($"Explore: leg {n} to"));
		var first = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		first.Start(ctx);
		TicksUntil(g, ctx, first, () => Legs("3") > 0, 60 * 60 * 2); // two legs walked, the third begun
		Assert.True(Legs("3") > 0, "the exploration did not get to its third leg");
		first.Stop(ctx); // something else came first
		Assert.Equal(0, ctx.ExploresWithoutOre.GetValueOrDefault(FakeGame.LifeCrystal));
		Assert.Equal(2, ctx.ExploreLegsCut.GetValueOrDefault(FakeGame.LifeCrystal));
		// Cut off before a leg got anywhere: counted, as before (world_eye run 42: a fight every few seconds restarted the job
		// and its same first leg for 12 game hours).
		var second = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		second.Start(ctx);
		Ticks(g, ctx, second, 2);
		Assert.Contains("Explore", second.Describe());
		second.Stop(ctx);
		Assert.Equal(1, ctx.ExploresWithoutOre.GetValueOrDefault(FakeGame.LifeCrystal));
		// Legs of cut-off explorations that add up to a whole one's (ExploreSkill.MaxLegs): that one counts.
		ctx.ExploreLegsCut[FakeGame.LifeCrystal] = ExploreSkill.MaxLegs - 1;
		int before = log.Lines.Count;
		var third = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		third.Start(ctx);
		TicksUntil(g, ctx, third, () => log.Lines.Skip(before).Any(l => l.StartsWith("Explore: leg 3 to")), 60 * 60 * 2);
		third.Stop(ctx);
		Assert.Equal(2, ctx.ExploresWithoutOre.GetValueOrDefault(FakeGame.LifeCrystal));
		Assert.Equal(1, ctx.ExploreLegsCut.GetValueOrDefault(FakeGame.LifeCrystal));
	}

	[Fact]
	public void ExploringForACrystal_OverTheOreGroundExploredAlready_DigsOnThroughIt_ToTheFreshGroundBelow()
	{
		// Measured run U27 (d4 +0.5h, full armour, the gear's floor row 601, a crystal's 701): from the spawn, "the best way on
		// from (2058,346), (2056,366), goes back to ground explored and seen already": three explorations ended after 2, 0
		// and 0 legs within 1.4 seconds, the job failed, and the bot went to new ground at (1975,280). The ore phase had
		// explored the ground round the spawn down to its floor (row 436-461; legs at x 1990-2210 to row ~450); six crystals
		// lay under it at rows 525-602, x 2018-2201 (found in U24-U26). U28 the same at (2012,353), U27 again at (1963,318) x3.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 300; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y >= 58 && y <= 60)
				for (int x = 38; x <= 42; x++)
					r[x] = '.';
			if (y == 60)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 122; y < 300; y++)
				g.Unrevealed.Add(new TilePos(x, y)); // seen down to row 121: the ore phase's ground
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 40;
		ctx.Combat.Defense = 9;
		ctx.Combat.ArmourPieces = 3;
		Assert.Equal(190, ctx.DeepestRow);
		foreach (var (x, y) in new[] { (42, 80), (38, 80), (42, 100), (65, 60), (15, 60) })
			ctx.Explored.Add(new TilePos(x, y)); // its legs, every way on from here
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		Ticks(g, ctx, mine, 3);
		var log = (TestLog)ctx.Log;
		Assert.DoesNotContain(log.Lines, l => l.Contains("explored already"));
		string leg = log.Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		Assert.True(int.Parse(m.Groups[2].Value) >= 110, $"through the ground explored already, to the fresh ground under it: {leg}");
	}

	[Fact]
	public void ExploringForACrystal_ThroughGroundExploredAlready_StopsAtTheGearsFloor()
	{
		// The deaths of measured run U26 (rows 720-772, the gear's floor 626) stay out: below the gear's own floor ground
		// explored already still ends the exploration (the crystal's floor, 100 rows deeper, is a cost there, not a way through).
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 300; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y >= 178 && y <= 180)
				for (int x = 38; x <= 42; x++)
					r[x] = '.';
			if (y == 180)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 232; y < 300; y++)
				g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 40;
		ctx.Combat.Defense = 9;
		ctx.Combat.ArmourPieces = 3;
		Assert.Equal(190, ctx.DeepestRow);
		foreach (var (x, y) in new[] { (42, 200), (38, 200), (42, 220), (65, 180), (15, 180), (42, 160), (38, 160) })
			ctx.Explored.Add(new TilePos(x, y));
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		Ticks(g, ctx, mine, 3);
		var log = (TestLog)ctx.Log;
		Assert.Contains(log.Lines, l => l.Contains("explored already"));
	}

	[Fact]
	public void ExploringForACrystal_FromTheSurface_DigsDownIntoTheBand_NotAlongTheSurface()
	{
		// Measured run U28: 88 of its 133 crystal legs ended at rows 300-349, above the band's top (row 375), most of them
		// frontiers along the surface ("leg 1 to (2406,304) (frontier) from (2346,298)", then (2436,309), (2466,318) ... 16
		// legs to (2683,348)); U27 54 of 145. U25, whose search began in the band, 1 of 36. A frontier ahead always came
		// before digging, whatever its depth; above the band a dig down into it now weighs against it.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 120; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y == 9)
				r[40] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 12; y < 120; y++)
				g.Unrevealed.Add(new TilePos(x, y)); // the surface seen, the ground under it not
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 100; // the crystal band from row 40
		Assert.Equal(40, ctx.CrystalBandTop);
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		Ticks(g, ctx, mine, 3);
		var log = (TestLog)ctx.Log;
		string leg = log.Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		Assert.True(int.Parse(m.Groups[2].Value) >= 9 + ExploreSkill.FallbackDepth, $"down into the ground, not along the surface: {leg}");
	}

	[Fact]
	public void ExploringForACrystal_FromTheSurface_ACaveGoingDown_StillBeatsDigging()
	{
		// The frontier-first rule's reason stays (measured run S1: ore found in 12 of 70 cave legs, fewer digging blind): a
		// cave frontier deep enough to beat a dig's pull is still taken.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 120; y++) {
			char[] r = (y < 10 ? new string('.', 120) : new string('#', 120)).ToCharArray();
			if (y == 9)
				r[40] = 'S';
			if (y >= 48 && y <= 50)
				foreach (int x0 in new[] { 5, 70 })
					for (int x = x0; x <= x0 + 5; x++)
						r[x] = '.'; // cave pockets in the band either way, seen, unseen rock round them
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { RevealRadius = 0 };
		for (int x = 0; x < 120; x++)
			for (int y = 12; y < 120; y++)
				if (!((x >= 70 && x <= 75 || x >= 5 && x <= 10) && y >= 48 && y <= 51))
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 10;
		ctx.CavernY = 100;
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		Ticks(g, ctx, mine, 3);
		string leg = ((TestLog)ctx.Log).Lines.First(l => l.Contains("Explore: leg 1 to"));
		output.WriteLine(leg);
		Assert.Contains("(frontier)", leg);
		var m = System.Text.RegularExpressions.Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		Assert.True(int.Parse(m.Groups[2].Value) >= 48, $"into the cave in the band: {leg}");
	}
}
