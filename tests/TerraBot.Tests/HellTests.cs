using System.Linq;
using TerraBot.Bot;
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

	public static FakeGame Hell(bool tower = true)
	{
		var rows = new char[SeaTop + 8][];
		for (int y = 0; y < rows.Length; y++) {
			rows[y] = new string(y <= SurfaceFeet ? '.' : y == rows.Length - 1 ? '@' : y >= SeaTop ? '!' : y > CeilingBottom ? '.' : 's', Width).ToCharArray();
			if (y > CeilingBottom && y < SeaTop && tower && y == BridgeRow)
				for (int x = TowerFrom; x <= TowerTo; x++)
					rows[y][x] = 's';
		}
		rows[LavaY][LavaX] = '!';
		rows[LavaY - 1][LavaX] = 's';
		rows[SurfaceFeet][ShaftX] = 'S';
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
