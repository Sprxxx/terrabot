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
	/// world_wof E1 (2026-10-17): 46 columns off the shaft at row 404 after a cave, the old rule went "up to row 292" to come down
	/// the shaft again (X1A and X2A climbed 600 rows). Deep and near, the way is over to the shaft's line at this depth, never up.
	/// </summary>
	[Fact]
	public void TheHellevator_DeepAndNearItsShaft_GoesOverAtThatDepth_NotUp()
	{
		const int off = 86, cave = 30; // deep (over TravelFromSurfaceRows under the surface) and 49 rows over the stand row: an approach, not the last leg
		FakeGame g = Hell();
		for (int x = off - 3; x <= off + 3; x++)
			for (int y = cave - 3; y <= cave; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		var pp = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(off, pp), Y = Core.Pathfinding.Body.StandingTop(cave, pp), ReleaseJump = true };
		BotContext ctx = Context(g);
		ctx.SurfaceY = SurfaceFeet;
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		int highest = int.MaxValue;
		shaft.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < 30000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = shaft.Tick(ctx);
			g.Step(ctx);
			highest = System.Math.Min(highest, g.Sim.Node.Y);
		}
		output.WriteLine($"{s}: {shaft.FailureReason} at {g.Sim.Node}, highest row {highest}, broken {g.Sim.Broken}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.InRange(g.Sim.Node.X, TowerFrom - 1, TowerTo);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("over to the shaft's line at row"));
		Assert.True(highest >= cave - 8, $"it went up to row {highest}: the way over is at this depth");
	}

	/// <summary>
	/// world_wof M12d (Peppe): the hellevator at x 2155 taken up again from a cave 570 columns off, its legs "no path" three times
	/// ("no way down"), and the planner moved the shaft away, again and again, to x 3127. From far off the bot goes to the shaft's
	/// top first, along the surface, and digs from there.
	/// </summary>
	[Fact]
	public void TheHellevator_StartedFarFromItsShaft_GoesToTheShaftsTop_ThenDigs()
	{
		const int far = 560, cave = 36;
		FakeGame g = Hell(600, far, TowerFrom, TowerTo);
		// A cave far off and deep (the bot there, as after a crystal search): a pocket at rows 33-36.
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
		// The way it went: up, along the surface, down the shaft (325 cells broken, 5028 ticks), not a tunnel through the rock to the
		// shaft's line, leg by leg (1614 broken, 14237 ticks: in game the legs' searches gave out at 40001 nodes).
		Assert.True(g.Sim.Broken < 800, $"broken {g.Sim.Broken}");
	}

	/// <summary>
	/// world_wof M12d: in the Underworld already by an old shaft, the shaft moved 300 columns off; the last leg walked the lava's edge
	/// toward it and the bot burnt to death twice. Down there, the row is got onto from where the bot is, not back up to the shaft.
	/// </summary>
	[Fact]
	public void TheHellevator_StartedInTheUnderworldFarFromItsShaft_GetsOntoTheRowFromThere()
	{
		const int far = 140, island = 90;
		FakeGame g = Hell();
		// Ground round the bot in the open band (an ash knoll over the lava sea), at x 135-145, rows 90-99.
		for (int x = far - 5; x <= far + 5; x++)
			for (int y = island; y < SeaTop; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		var pp = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(far, pp), Y = Core.Pathfinding.Body.StandingTop(island - 1, pp), ReleaseJump = true };
		BotContext ctx = Context(g);
		ctx.SurfaceY = SurfaceFeet;
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, shaft, 30000);
		output.WriteLine($"{s}: {shaft.FailureReason} at {g.Sim.Node}, tick {ctx.Tick}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(BridgeRow - 1, g.Sim.Node.Y);
		Assert.InRange(g.Sim.Node.X, far - HellevatorSkill.SeekTiles, far + HellevatorSkill.SeekTiles);
		Assert.DoesNotContain(((TestLog)ctx.Log).Lines, l => l.Contains("first") && l.Contains("columns from the shaft"));
	}

	/// <summary>
	/// The user's hellevator, "a 2 wide drop down to hell": straight down the shaft's two columns, through cobwebs (world_wof's
	/// shaft had them at rows 867-881, where the pathfinder legs wandered 50-70 columns off), round the lava cell beside the line,
	/// never far from the column until the last leg's rows, and few cells broken beyond the shaft's own.
	/// </summary>
	[Fact]
	public void TheHellevator_DigsAStraightTwoWideDrop_ThroughCobwebs_WithoutWandering()
	{
		FakeGame g = Hell();
		for (int y = 40; y <= 48; y++)
			for (int x = ShaftX - 3; x <= ShaftX + 4; x++)
				g.Grid.Set(x, y, CellKind.Solid | CellKind.Web);
		BotContext ctx = Context(g);
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		shaft.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int maxOff = 0, t = 0;
		for (; t < 30000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = shaft.Tick(ctx);
			g.Step(ctx);
			if (g.Sim.Node.Y < BridgeRow - 1 - HellevatorSkill.FinalLegRows)
				maxOff = System.Math.Max(maxOff, System.Math.Abs(g.Sim.Node.X - ShaftX));
		}
		output.WriteLine($"{s} at {g.Sim.Node} after {t} ticks, broken {g.Sim.Broken}, placed {g.Sim.Placed}, farthest {maxOff} columns off the shaft above the last leg");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(maxOff <= HellevatorSkill.Slack, $"{maxOff} columns off");
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("(straight, column"));
	}

	/// <summary>
	/// In game (S1) fights and surfacing pushed the body off the shaft and the dig went on from wherever it stood (2149, 2138, 2120).
	/// Pushed 5 columns off mid-shaft (a side pocket dug there), the bot goes back onto its column and digs on from it.
	/// </summary>
	[Fact]
	public void TheHellevator_PushedOffItsColumn_GoesBackOntoIt()
	{
		FakeGame g = Hell();
		BotContext ctx = Context(g);
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		shaft.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		bool pushed = false;
		int t = 0;
		for (; t < 30000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			if (!pushed && g.Sim.Node.Y >= 20 && ctx.Body.Grounded) {
				// A side pocket off the shaft (as a fight's or a surfacing's moves leave), and the body in it.
				int y = g.Sim.Node.Y;
				for (int x = ShaftX - 6; x < ShaftX; x++)
					for (int r = y - 2; r <= y; r++)
						g.Grid.Set(x, r, CellKind.Empty);
				var pp = Core.Physics.PhysicsParams.Default;
				g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(ShaftX - 5, pp), Y = Core.Pathfinding.Body.StandingTop(y, pp), ReleaseJump = true };
				pushed = true;
				g.Refresh(ctx);
			}
			s = shaft.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"{s} at {g.Sim.Node} after {t} ticks");
		Assert.True(pushed);
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.DoesNotContain(((TestLog)ctx.Log).Lines, l => l.Contains("the shaft goes on from column"));
	}

	/// <summary>
	/// In game (S2) the straight shaft mined into a water pocket at row 418 and filled 16 rows deep: minutes of surfacing and diving
	/// for three rows. A water pocket beside the shaft's line is stepped round as lava is, so the shaft stays dry.
	/// </summary>
	[Fact]
	public void TheHellevator_SteersRoundAWaterPocket_TheShaftStaysDry()
	{
		FakeGame g = Hell();
		for (int x = ShaftX + 2; x <= ShaftX + 6; x++)
			for (int y = 16; y <= 20; y++)
				g.Grid.Set(x, y, CellKind.Water);
		BotContext ctx = Context(g);
		var shaft = new HellevatorSkill(ShaftX, BridgeRow - 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, shaft, 30000);
		output.WriteLine($"{s} at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("lava or water beside the shaft"));
		for (int y = 16; y <= 20; y++)
			Assert.True(g.Grid.Get(ShaftX + 2, y).IsLiquid(), $"the pocket was opened at row {y}");
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

	/// <summary>
	/// X1B and X2B (2026-10-17): the bridge stopped at "the cell (2255,1040) in the bridge's way cannot be mined at pick power 45 (tile 1)":
	/// plain stone, a ruined house's floor with furniture on it, which the game refuses to mine while the furniture stands. The
	/// furniture is mined first, then the floor, and the bridge goes on.
	/// </summary>
	[Fact]
	public void TheHellbridge_MinesTheFurnitureOnABlockInItsWay_ThenTheBlock()
	{
		var (g, ctx) = OnTheTower();
		var floor = new TilePos(60, BridgeRow - 2);
		var pot = new TilePos(60, BridgeRow - 3);
		g.Grid.Set(floor.X, floor.Y, CellKind.Solid);
		g.Blocks.Unbreakable.Add(floor); // the game: CanKillTile is false under furniture
		g.Furniture[pot] = 99;
		g.PickaxeRemoves.Add(99);
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		bridge.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < 20000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = bridge.Tick(ctx);
			g.Step(ctx);
			if (!g.Furniture.ContainsKey(pot))
				g.Blocks.Unbreakable.Remove(floor); // the furniture gone, the floor is a block like any other
		}
		output.WriteLine($"{s}: {bridge.FailureReason}, end {bridge.End}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.False(g.Furniture.ContainsKey(pot), "the furniture mined first");
		Assert.False(g.Grid.Get(floor.X, floor.Y).IsSolid(), "then the floor");
		Assert.Equal(70, bridge.End);
	}

	/// <summary>E5 (2026-10-17): "the cell (2413,1040) in the bridge's way cannot be mined at pick power 45 (tile 19)": a platform two rows over the deck, which a pickaxe takes.</summary>
	[Fact]
	public void TheHellbridge_MinesAPlatformInItsWay_ThoughThePathsCallItUnbreakable()
	{
		var (g, ctx) = OnTheTower();
		var platform = new TilePos(60, BridgeRow - 2);
		g.Grid.Set(platform.X, platform.Y, CellKind.Platform);
		g.Blocks.Unbreakable.Add(platform); // the paths: frame-important, never mined
		g.Furniture[platform] = 19;
		g.PickaxeRemoves.Add(19);
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		bridge.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < 20000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = bridge.Tick(ctx);
			g.Step(ctx);
			if (!g.Furniture.ContainsKey(platform) && g.Grid.Get(platform.X, platform.Y).IsPlatform())
				g.Grid.Set(platform.X, platform.Y, CellKind.Empty); // the pickaxe took it
		}
		output.WriteLine($"{s}: {bridge.FailureReason}, end {bridge.End}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.False(g.Furniture.ContainsKey(platform), "the platform mined");
		Assert.Equal(70, bridge.End);
	}

	/// <summary>world_wof E4 (2026-10-17): knocked off the bridge, the fight went on 25 rows down on the Underworld's floor and died there.</summary>
	[Fact]
	public void AFight_KnockedOffTheDeck_WalksBackOntoIt_First()
	{
		var (g, ctx) = OnTheTower();
		// The tower's roof is the deck; the body on the band's floor under it, 10 rows down, 6 columns off.
		int floorY = BridgeRow + 10;
		for (int x = TowerFrom - 10; x <= TowerTo + 10; x++)
			g.Grid.Set(x, floorY + 1, CellKind.Solid);
		for (int y = BridgeRow + 1; y <= floorY; y++)
			for (int x = TowerFrom - 10; x <= TowerTo + 10; x++)
				g.Grid.Set(x, y, CellKind.Empty);
		var pp = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(TowerTo + 6, pp), Y = Core.Pathfinding.Body.StandingTop(floorY, pp), ReleaseJump = true };
		ctx.Combat.WeaponReach = 30f;
		g.Items[FakeGame.StoneBlock] = 100;
		var fight = new FightSkill { Deck = (TowerFrom, TowerTo, BridgeRow - 1) };
		fight.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int t = 0;
		for (; t < 6000 && s == SkillStatus.Running && !(g.Sim.Node.Y == BridgeRow - 1 && g.Sim.Node.X >= TowerFrom && g.Sim.Node.X <= TowerTo); t++) {
			g.Refresh(ctx);
			s = fight.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"{s} after {t} ticks at {g.Sim.Node}: {fight.Describe()}");
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("off the deck") && l.Contains("walking back"));
		Assert.Equal(BridgeRow - 1, g.Sim.Node.Y);
		Assert.InRange(g.Sim.Node.X, TowerFrom, TowerTo);
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

	/// <summary>
	/// world_wof E9 (2026-10-17): "placing at (2397,1042) did nothing in 90 ticks: the cell reads Empty, tile -1", six tries from the end
	/// and a column back with three Bone Serpents under the bridge, and the errand blocked on it. The game refuses a solid block where
	/// an NPC is (Collision.EmptyTile) and the bot's grid never sees NPCs: the world now says what is in the cell, and the bridge waits.
	/// </summary>
	[Fact]
	public void TheHellbridge_WaitsForAnNpcInTheDeckCell_ThenPlaces_AndFailsSayingSo_WhenItStays()
	{
		var (g, ctx) = OnTheTower();
		var cell = new TilePos(62, BridgeRow);
		g.Blocks.NoPlace.Add(cell);
		g.PlaceBlockers[cell] = "Bone Serpent #22";
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		var log = (TestLog)ctx.Log;
		bridge.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int t = 0, waited = 0;
		for (; t < 20000 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = bridge.Tick(ctx);
			g.Step(ctx);
			// The serpent moves on after 200 ticks of waiting (fewer than the 300 the bridge gives it).
			if (log.Lines.Any(l => l.Contains("waiting for it to go")) && ++waited == 200) {
				g.Blocks.NoPlace.Remove(cell);
				g.PlaceBlockers.Remove(cell);
			}
		}
		output.WriteLine($"{s} after {t} ticks: {bridge.Describe()}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Contains(log.Lines, l => l.Contains("Bone Serpent #22 in the bridge's cell (62,") && l.Contains("waiting for it to go"));
		Assert.DoesNotContain(log.Lines, l => l.Contains("did nothing in"));
		Assert.True(HellbridgeSkill.Built(g.Grid, BridgeRow, 62));

		// It stays: the skill fails saying what sat in the cell, not "could not place a block".
		var (g2, ctx2) = OnTheTower();
		g2.Blocks.NoPlace.Add(cell);
		g2.PlaceBlockers[cell] = "Bone Serpent #22";
		var stuck = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g2, ctx2, stuck, 20000));
		output.WriteLine(stuck.FailureReason);
		Assert.Contains("Bone Serpent #22 in the bridge's cell", stuck.FailureReason);
		Assert.DoesNotContain("could not place", stuck.FailureReason);
		Assert.Equal(61, stuck.End);
	}

	/// <summary>
	/// world_wof E10 (2026-10-17): "could not mine (3198,1042) (3 tries)", tile 21: a ruined house's chest in the deck's cell, which the
	/// game keeps while it holds items. The bridge says so at the first try, names the cell, and goes on once it is looted empty.
	/// </summary>
	[Fact]
	public void TheHellbridge_StopsAtAChestInItsDeck_SayingItIsLootedFirst_ThenMinesItEmpty()
	{
		var (g, ctx) = OnTheTower();
		var chest = new TilePos(58, BridgeRow);
		g.Furniture[chest] = 21; // the bottom-left cell of a chest with items: the pickaxe does nothing to it
		g.Chests[new TilePos(58, BridgeRow - 1)] = new System.Collections.Generic.List<(int, int)> { (FakeGame.HealingPotion, 2) };
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, bridge, 20000));
		output.WriteLine(bridge.FailureReason);
		Assert.Equal(chest, bridge.Chest);
		Assert.Contains("chest", bridge.FailureReason);
		Assert.DoesNotContain("tries", bridge.FailureReason);
		// Looted empty (the errand's LootChest): the pickaxe takes it, and the bridge goes on over its cell.
		g.Chests[new TilePos(58, BridgeRow - 1)].Clear();
		var again = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, again, 20000));
		Assert.Null(again.Chest);
		Assert.True(HellbridgeSkill.Built(g.Grid, BridgeRow, 58));
	}

	/// <summary>
	/// world_wof E11 (2026-10-17): "could not mine (3967,1037) (3 tries)", tile 634, an ash tree on a knoll in the bridge's way at 94 %.
	/// The game keeps the block under a trunk (WorldGen.CanKillTile), so the bridge goes for the tree; a pickaxe does nothing to it.
	/// </summary>
	[Fact]
	public void TheHellbridge_ChopsAnAshTreeOnAKnollInItsWay_WithTheAxe()
	{
		var (g, ctx) = OnTheTower();
		const int knoll = 58;
		for (int y = BridgeRow - 3; y <= BridgeRow - 1; y++)
			g.Grid.Set(knoll, y, CellKind.Solid); // three rows of ash in the body's way
		var tree = new TilePos(knoll, BridgeRow - 4); // its base on the knoll's top, the jump's spare row
		g.Trees[tree] = 4;
		g.Blocks.KeptByTheGame = (x, y) => g.Trees.ContainsKey(new TilePos(x, y - 1));
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		var log = (TestLog)ctx.Log;
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{s}: {bridge.Describe()} at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Empty(g.Trees);
		Assert.DoesNotContain(log.Lines, l => l.Contains("did nothing in"));
		for (int x = TowerTo; x <= 70; x++)
			Assert.True(HellbridgeSkill.Built(g.Grid, BridgeRow, x), $"column {x}");
	}

	/// <summary>A ruined house's Shadow Chest (locked, kept by the game) in the deck's row, on its floor: passed over, not built, not a stop.</summary>
	[Fact]
	public void TheHellbridge_PassesOverALockedChestInItsDeck_StandingInItsDip()
	{
		var (g, ctx) = OnTheTower();
		var topLeft = new TilePos(58, BridgeRow - 1);
		g.Chests[topLeft] = new System.Collections.Generic.List<(int, int)> { (FakeGame.HealingPotion, 2) };
		g.LockedChests.Add(topLeft);
		foreach (int x in new[] { 58, 59 }) {
			g.Furniture[new TilePos(x, BridgeRow)] = 21;
			g.Furniture[new TilePos(x, BridgeRow - 1)] = 21;
			g.Grid.Set(x, BridgeRow + 1, CellKind.Solid); // the house's floor it stands on
		}
		var bridge = new HellbridgeSkill(BridgeRow, TowerTo, 70);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, bridge, 20000);
		output.WriteLine($"{s}: {bridge.Describe()} at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Furniture.ContainsKey(new TilePos(58, BridgeRow)), "the chest left where it is");
		Assert.Null(bridge.Chest);
		Assert.False(HellbridgeSkill.Built(g.Grid, BridgeRow, 58));
		Assert.True(HellbridgeSkill.Passable(ctx, g.Grid, BridgeRow, 58));
		Assert.Equal(70, HellbridgeSkill.BridgeEnd(g.Grid, BridgeRow, TowerTo, 70, ctx));
		for (int x = 60; x <= 70; x++)
			Assert.True(HellbridgeSkill.Built(g.Grid, BridgeRow, x), $"column {x}");
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
