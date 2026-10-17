using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 12 (docs M12_SPEC.md sections 1 and 5): the hellevator down to the Underworld and the hellbridge over its lava, in
/// a small world laid out as the M12 one is round x 2155: rock from the surface down, a lava cell beside the shaft's line, an ash
/// ceiling over an open band, a ruined tower's roof under the shaft on the bridge's row, a lava sea below.
/// </summary>
public class HellTests
{
	private readonly ITestOutputHelper output;
	public HellTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The surface's feet row, the shaft's column, the lava cell's, the ceiling's rows, the bridge's row (the tower's roof), the lava sea's top.</summary>
	public const int Width = 160, SurfaceFeet = 9, ShaftX = 40, LavaX = 42, LavaY = 30, CeilingTop = 60, CeilingBottom = 72, BridgeRow = 80, SeaTop = 100;
	/// <summary>The tower's roof: columns 36-46 on the bridge's row.</summary>
	public const int TowerFrom = 36, TowerTo = 46;

	public static FakeGame Hell(bool tower = true) => Hell(Width, ShaftX, TowerFrom, TowerTo, tower);

	/// <summary>The same world <paramref name="width"/> wide, the bot at <paramref name="startX"/> and the tower at the columns given.</summary>
	public static FakeGame Hell(int width, int startX, int towerFrom, int towerTo, bool tower = true)
	{
		var rows = new char[SeaTop + 8][];
		for (int y = 0; y < rows.Length; y++) {
			rows[y] = new string(y <= SurfaceFeet ? '.' : y == rows.Length - 1 ? '@' : y >= SeaTop ? '!' : y > CeilingBottom ? '.' : 's', width).ToCharArray();
			if (y > CeilingBottom && y < SeaTop && tower && y == BridgeRow)
				for (int x = towerFrom; x <= towerTo; x++)
					rows[y][x] = 's';
		}
		rows[LavaY][LavaX] = '!';
		rows[LavaY - 1][LavaX] = 's';
		rows[SurfaceFeet][startX] = 'S';
		var g = new FakeGame(rows.Select(r => new string(r)).ToArray());
		g.Items[FakeGame.StoneBlock] = 300;
		return g;
	}

	private BotContext Context(FakeGame g)
	{
		BotContext ctx = g.CreateContext(output);
		ctx.DepthErrand = true;
		g.Blocks.TicksPerBlock = 4;
		g.Refresh(ctx);
		return ctx;
	}

	[Fact]
	public void TheHellevator_DigsDownTheShaft_RoundTheLava_LeavesLedges_AndLandsOnTheTowerRoof()
	{
		FakeGame g = Hell();
		BotContext ctx = Context(g);
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, shaft, 30000);
		output.WriteLine($"{s}: {shaft.Describe()} at {g.Sim.Node}, broken {g.Sim.Broken}, placed {g.Sim.Placed}, fall hits?");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(BridgeRow - 1, g.Sim.Node.Y);
		Assert.InRange(g.Sim.Node.X, TowerFrom - 1, TowerTo);
		Assert.Equal(g.Sim.Node, shaft.Landed);
		Assert.True(g.Grid.Get(LavaX, LavaY).HasLava(), "the lava cell was never opened");
		Assert.True(g.Sim.Placed >= 2, "ledges in the shaft");
	}

	[Fact]
	public void TheHellevator_WithNoGroundUnderTheShaft_FailsSaying_SoThePlannerMovesIt()
	{
		FakeGame g = Hell(tower: false);
		BotContext ctx = Context(g);
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, shaft, 40000);
		output.WriteLine($"{s}: {shaft.FailureReason} at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("no way down", shaft.FailureReason);
		Assert.True(g.Sim.Node.Y <= CeilingBottom, "never dropped into the open band");
	}

	/// <summary>
	/// world_wof M12d (Peppe): the hellevator at x 2155 taken up again from a cave 570 columns off, its legs "no path" three times
	/// ("no way down"), and the planner moved the shaft away, again and again, to x 3127. From far off the bot goes to the shaft's
	/// top first, along the surface, and digs from there.
	/// </summary>
	[Fact]
	public void TheHellevator_StartedFarFromItsShaft_GoesToTheShaftsTop_ThenDigs()
	{
		const int far = 560, cave = 50;
		FakeGame g = Hell(600, far, TowerFrom, TowerTo);
		// A cave far off and deep (the bot there, as after a crystal search): a pocket at rows 47-50.
		for (int x = far - 3; x <= far + 3; x++)
			for (int y = cave - 3; y <= cave; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		var pp = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(far, pp), Y = Core.Pathfinding.Body.StandingTop(cave, pp), ReleaseJump = true };
		BotContext ctx = Context(g);
		ctx.SurfaceY = SurfaceFeet;
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, shaft, 60000);
		output.WriteLine($"{s}: {shaft.FailureReason} at {g.Sim.Node}, tick {ctx.Tick}, broken {g.Sim.Broken}, placed {g.Sim.Placed}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.InRange(g.Sim.Node.X, TowerFrom - 1, TowerTo);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("columns from the shaft") && l.Contains("to the shaft's top"));
		// The way it went: up, along the surface, down the shaft (381 cells broken, 5558 ticks), not a tunnel through the rock to the
		// shaft's line, leg by leg (1586 broken, 14152 ticks: in game the legs' searches gave out at 40001 nodes).
		Assert.True(g.Sim.Broken < 800, $"broken {g.Sim.Broken}");
	}

	/// <summary>The Hell world with the bot standing on the tower's roof at its east end, ready to bridge east.</summary>
	private (FakeGame, BotContext) OnTheTower(int blocks = 300)
	{
		FakeGame g = Hell();
		g.Items[FakeGame.StoneBlock] = blocks;
		var p = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(TowerTo - 1, p), Y = Core.Pathfinding.Body.StandingTop(BridgeRow - 1, p), ReleaseJump = true };
		BotContext ctx = Context(g);
		return (g, ctx);
	}

	[Fact]
	public void TheHellbridge_IsLaidBlockByBlockOverTheLava_ThroughAnAshKnoll_ToItsEnd()
	{
		var (g, ctx) = OnTheTower();
		// An ash knoll across the bridge's way (x 70-75, rows 74-82): mined through at the body's rows, its ground the bridge there.
		for (int x = 70; x <= 75; x++)
			for (int y = 74; y <= 82; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		const int to = 110;
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, to);
		var lines = new System.Collections.Generic.List<string>();
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{s}: {bridge.Describe()}, placed {bridge.Placed}, mined {bridge.Mined}, at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
		for (int x = TowerTo; x <= to; x++)
			Assert.True(g.Grid.Get(x, BridgeRow).IsSolid(), $"bridge cell ({x},{BridgeRow})");
		for (int x = 70; x <= 75; x++)
			for (int y = BridgeRow - HellbridgeSkill.ClearRows; y < BridgeRow; y++)
				Assert.False(g.Grid.Get(x, y).IsSolid(), $"the knoll's cell ({x},{y}) in the way");
		Assert.Equal(to - TowerTo - 6, bridge.Placed); // the knoll's own ground is the bridge for its 6 columns
		Assert.Equal(6 * HellbridgeSkill.ClearRows, bridge.Mined);
		Assert.Equal(BridgeRow - 1, g.Sim.Node.Y);
	}

	[Fact]
	public void TheHellbridge_NeverOpensACellWithLavaBeside_AndSaysWhy()
	{
		var (g, ctx) = OnTheTower();
		for (int x = 60; x <= 61; x++)
			for (int y = 74; y <= 82; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		g.Grid.Set(62, 78, CellKind.Lava); // a lava pocket against the knoll's body-row cell (61,78)
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 110);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{s}: {bridge.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("lava beside or above", bridge.FailureReason);
		Assert.True(g.Grid.Get(61, 78).IsSolid());
	}

	[Fact]
	public void TheBridgesSpan_ForTheM12World_RunsFromTheEdgeBackPastTheShaft()
	{
		// fight_wof's 20.3 a second: L = 1.25 x 62,175 / 20.3 = 3829 (ProgressionPlanner.DefaultBridgeLength). From the tower by the
		// shaft (x 2156) east: the summon end 120 short of the edge, the rest west past the world's middle.
		var (to, back) = ProgressionPlanner.BridgeSpan(2156, 1, ProgressionPlanner.DefaultBridgeLength, 4200);
		Assert.Equal(4200 - ProgressionPlanner.BridgeEdgeRoom, to);
		Assert.Equal(to - ProgressionPlanner.DefaultBridgeLength, back);
		Assert.True(back >= ProgressionPlanner.BridgeEdgeRoom);
		// A short one goes one way only.
		Assert.Equal((2216, 2156), ProgressionPlanner.BridgeSpan(2156, 1, 60, 4200));
		Assert.Equal((2096, 2156), ProgressionPlanner.BridgeSpan(2156, -1, 60, 4200));
	}

	[Fact]
	public void TheHellbridge_TakesAPlatformForDeck_MinesATileInTheWay_AndEscalatesAFailedPlace_ThenFails()
	{
		var (g, ctx) = OnTheTower();
		// A platform in the bridge's row at x 55 (a ruined house's): stood on, so bridge already. A pot-like tile at x 58: mined.
		g.Grid.Set(55, BridgeRow, CellKind.Platform);
		g.Furniture[new TilePos(58, BridgeRow)] = 99;
		g.PickaxeRemoves.Add(99);
		// No block goes in at x 62 (the game refuses it): from the end, a column back, then the skill fails, saying so.
		g.Blocks.NoPlace.Add(new TilePos(62, BridgeRow));
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 110);
		var log = (TestLog)ctx.Log;
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{s}: {bridge.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("from the end and a column back", bridge.FailureReason);
		Assert.Contains(log.Lines, l => l.Contains("trying from a column back"));
		Assert.Equal(61, bridge.End);
		Assert.True(g.Grid.Get(55, BridgeRow).IsPlatform(), "the platform was left as the deck");
		Assert.False(g.Furniture.ContainsKey(new TilePos(58, BridgeRow)));
		Assert.True(g.Grid.Get(58, BridgeRow).IsSolid());
	}

	[Fact]
	public void TheHellbridge_ReplacesAHotDeckCell_MinesAHotOneInTheWay_AndSaysWhyItStopsAtHellstone()
	{
		// Seen in game (measured run M12b): the deck ran over a ruined house's hot bricks (hellstone brick, TileID.Sets.TouchDamageHot),
		// and the bot burnt to death walking along it. A hot cell is the grid's hazard: never stood on, so never deck.
		var (g, ctx) = OnTheTower();
		var hotDeck = new TilePos(55, BridgeRow);
		g.Grid.Set(hotDeck.X, hotDeck.Y, CellKind.Solid | CellKind.Hazard);
		g.TileTypes[hotDeck] = 76;
		var hotWay = new TilePos(58, BridgeRow - 2);
		g.Grid.Set(hotWay.X, hotWay.Y, CellKind.Solid | CellKind.Hazard);
		g.TileTypes[hotWay] = 76;
		const int to = 70;
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, to);
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{st}: {bridge.Describe()}, at {g.Sim.Node}, hot deck {g.Grid.Get(hotDeck.X, hotDeck.Y)}");
		Assert.Equal(SkillStatus.Succeeded, st);
		Assert.True(g.Grid.Get(hotDeck.X, hotDeck.Y).CanStandOn(), "the hot deck cell was mined and a block put in its place");
		Assert.False(g.Grid.Get(hotWay.X, hotWay.Y).IsSolid());
		for (int x = TowerTo; x <= to; x++)
			Assert.True(HellbridgeSkill.Built(g.Grid, BridgeRow, x), $"column {x}");

		// Hellstone itself (pick power 65; the bot's 40 here): no mining it, and the skill says what would.
		var (g2, ctx2) = OnTheTower();
		var hellstone = new TilePos(52, BridgeRow);
		g2.Grid.Set(hellstone.X, hellstone.Y, CellKind.Solid | CellKind.Hazard);
		g2.TileTypes[hellstone] = 58;
		g2.Blocks.Unbreakable.Add(hellstone);
		var stopped = new HellbridgeSkill(BridgeRow, TowerTo, to);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g2, ctx2, stopped, 20000));
		output.WriteLine(stopped.FailureReason);
		Assert.Contains("hellstone", stopped.FailureReason);
		Assert.Equal(51, stopped.End);
	}

	[Fact]
	public void TheHellbridge_OutOfBlocks_Fails_AndKnockedOff_WalksBackToTheEnd()
	{
		var (g, ctx) = OnTheTower(blocks: 10);
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 110);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("out of blocks", bridge.FailureReason);
		Assert.Equal(TowerTo + 10, bridge.End);
	}
}
