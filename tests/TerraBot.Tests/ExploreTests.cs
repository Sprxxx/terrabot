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
	public void NoCaveInView_DigsAKeptMine_StairsThenTunnels_UntilOreShows()
	{
		// Measured run R1: with no cave in view, 126 legs of 20-25 tiles dug from wherever the bot stood, none kept, exploring
		// 13% of the run. Now: one mine, a staircase down (2 across, 2 down a leg) and tunnels every 12 rows, kept on the context.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 70; y++)
			rows.Add(new string('s', 70));
		for (int y = 20; y <= 22; y++)
			rows[y] = new string('s', 28) + (y == 22 ? "..S..." : "......") + new string('s', 36);
		rows.Add(new string('@', 70));
		var g = new FakeGame(rows.ToArray());
		for (int x = 58; x <= 60; x++)
			for (int y = 33; y <= 35; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileIron;
		for (int x = 0; x < 70; x++)
			for (int y = 0; y < 71; y++)
				if (System.Math.Abs(x - 30) > 8 || System.Math.Abs(y - 22) > 8)
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		var explore = new ExploreSkill("iron ore", c => c.World.FindTiles(FakeGame.TileIron, c.Node, MineSkill.SearchRadius).Count > 0, ExploreBias.Down, heading: 1);
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, explore, 60 * 60 * 5);
		output.WriteLine($"{st} after {g.Sim.Ticks} ticks, {explore.Legs} legs, at {g.Sim.Node}; {ctx.Mine}");
		Assert.Equal(SkillStatus.Succeeded, st);
		Assert.NotNull(ctx.Mine);
		Assert.True(ctx.Mine.Tunnels.Count >= 1, "a tunnel dug at the mine's first tunnel row");
		// Kept: a new exploration goes on with the same mine, not a new one.
		BranchMine kept = ctx.Mine;
		var again = new ExploreSkill("gold ore", _ => false, ExploreBias.Down, heading: -1);
		EarlyGameTests.RunSkill(g, ctx, again, 600);
		Assert.Same(kept, ctx.Mine);
	}
}
