using System.Collections.Generic;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Execution;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Pillaring up out of deep water (world_eye J5, J3, J4 on "Journey Test"): the water jump is slow and short, and the blocks have
/// to stack under the rising body.
/// </summary>
public class WaterPillarTests
{
	private readonly ITestOutputHelper output;
	public WaterPillarTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void TheWaterJump_IsVanillas_EightRowsUpAtHalfSpeed()
	{
		// Player.cs:23406-23409 and WaterCollision; the log of J5 shows vy -5.81 rising and 5.01 falling, jumps from row 745 to 737.
		PhysicsParams wet = PhysicsParams.Default.InWater();
		Assert.Equal(0.2f, wet.Gravity);
		Assert.Equal(5.01f, wet.MaxFallSpeed);
		Assert.Equal(30, wet.JumpHeight);
		Assert.Equal(6.01f, wet.JumpSpeed, 3);
		var s = new BodyState { ReleaseJump = true };
		float top = 0f;
		for (int t = 0; t < 200; t++) {
			PlayerPhysics.StepWater(ref s, new MoveInput { Jump = true }, wet);
			if (t == 0)
				Assert.Equal(-5.81f, s.VY, 3);
			top = System.Math.Min(top, s.Y);
		}
		output.WriteLine($"apex {-top:0.#} px");
		Assert.InRange(-top / 16f, 7.5f, 8.5f);
	}

	[Fact]
	public void TheChosenHold_StacksSeveralBlocksAJump_FarFasterThanHoldingTheWholeJump()
	{
		PhysicsParams wet = PhysicsParams.Default.InWater();
		int hold = WaterPillar.HoldTicks(PhysicsParams.Default, 10f);
		int Mean(int h)
		{
			int sum = 0;
			for (int phase = 0; phase < 10; phase++)
				sum += WaterPillar.TicksToRise(wet, h, 10, phase, 12);
			return sum / 10;
		}
		int chosen = Mean(hold), full = Mean(wet.JumpHeight + 1);
		output.WriteLine($"hold {hold}: {chosen} ticks for 12 rows; the whole jump held: {full}");
		// J5: 12 rows of water over the floor, breath 149 when surfacing began, a breath every 7 ticks: 1043 ticks.
		Assert.True(chosen < 300, $"{chosen} ticks for 12 rows");
		Assert.True(chosen < full, $"hold {hold} ({chosen}) not faster than the whole jump ({full})");
	}

	/// <summary>
	/// The flooded jungle cave of J5 death 2 at (3296,741): water from row <paramref name="surface"/> down to the floor under
	/// row <paramref name="floorFeet"/>, air over it, a cave 20 wide with no walls behind (no block fixes to open water).
	/// </summary>
	private static FakeGame Cave(int surface, int floorFeet, int width = 20)
	{
		var rows = new List<string>();
		for (int y = 0; y <= floorFeet; y++) {
			char c = y < surface ? '.' : '~';
			rows.Add("@@" + new string(c, width) + "@@");
		}
		rows.Add(new string('@', width + 4));
		char[] bottom = rows[floorFeet].ToCharArray();
		bottom[2 + width / 2] = 'S';
		rows[floorFeet] = new string(bottom);
		var g = new FakeGame(rows.ToArray());
		g.Items[FakeGame.DirtBlock] = 50;
		return g;
	}

	/// <summary>
	/// Runs <see cref="SurfaceSkill"/> with vanilla's breath (Player.cs:21768-21800: one less every 7 ticks with the head under,
	/// 3 more a tick out of it) until the head is out; the ticks it took, or -1 if breath ran out first.
	/// </summary>
	private int Surface(FakeGame g, BotContext ctx, int breath, int maxTicks = 2000, System.Action<int> during = null)
	{
		var surface = new SurfaceSkill();
		surface.Start(ctx);
		int cd = 0;
		for (int t = 0; t < maxTicks; t++) {
			g.Refresh(ctx);
			ctx.Combat.Breath = breath;
			if (!SurfaceSkill.HeadUnder(ctx)) {
				output.WriteLine($"head out at {ctx.Node} after {t} ticks, breath {breath}, {g.Sim.Placed} blocks placed");
				return t;
			}
			if (breath <= 0) {
				output.WriteLine($"drowning at {ctx.Node} after {t} ticks: {surface.Describe()}, {g.Sim.Placed} blocks placed");
				return -1;
			}
			surface.Tick(ctx);
			g.Step(ctx);
			during?.Invoke(t);
			if (++cd >= SurfaceSkill.TicksPerBreath) {
				cd = 0;
				breath--;
			}
		}
		return -1;
	}

	[Fact]
	public void DeepWater_NoWalls_TheBotPillarsItsHeadOut_BeforeItsBreathRunsOut()
	{
		// J5 death 2: water rows 732-745, the bot on the floor at 745 (here 14 and 27), breath 149 at the start; it jumped from
		// 745 to 737 and back for some 1000 ticks, no block placed, and drowned.
		FakeGame g = Cave(surface: 14, floorFeet: 27);
		BotContext ctx = g.CreateContext(output);
		int t = Surface(g, ctx, breath: 149);
		Assert.True(t >= 0, "drowned");
		Assert.True(t < 149 * SurfaceSkill.TicksPerBreath / 2, $"{t} ticks: more than half the breath");
	}

	[Fact]
	public void DeepWater_SinkingFromMidWater_StillGetsOut()
	{
		// Knocked off the pillar or sinking from higher up: the body comes down to the floor, then stacks from it.
		FakeGame g = Cave(surface: 14, floorFeet: 27);
		g.Sim.State.Y -= 5 * 16;
		BotContext ctx = g.CreateContext(output);
		Assert.True(Surface(g, ctx, breath: 120) >= 0, "drowned");
	}

	[Fact]
	public void KnockedAboutWhilePillaring_TheBotStillGetsItsHeadOut()
	{
		// J3 and J5 death 1: piranhas and spore skeletons hit the bot every 40-50 ticks while it pillared, each hit a knockback
		// (vanilla Player.Hurt: velocity (4.5 * dir, -3.5)), off the column and off the stack.
		FakeGame g = Cave(surface: 14, floorFeet: 27);
		BotContext ctx = g.CreateContext(output);
		int t = Surface(g, ctx, breath: 149, during: tick => {
			if (tick % 45 == 20) {
				g.Sim.State.VX = tick % 90 == 20 ? 4.5f : -4.5f;
				g.Sim.State.VY = -3.5f;
				g.Sim.State.Jump = 0;
			}
		});
		Assert.True(t >= 0, "drowned");
	}

	[Fact]
	public void TheStackCell_IsOverTheTopOfThePillar_NotUnderTheMovingNode()
	{
		// Two blocks already placed over the floor: the next goes on them with the body two rows over it, still in reach.
		FakeGame g = Cave(surface: 2, floorFeet: 20, width: 6);
		BotContext ctx = g.CreateContext(output);
		int x = ctx.Node.X;
		g.Grid.Set(x, 20, CellKind.Solid);
		g.Grid.Set(x, 19, CellKind.Solid);
		g.Sim.State.Y -= 4 * 16 + 4;
		g.Refresh(ctx);
		TilePos? cell = WaterPillar.StackCell(ctx.Grid, ctx.Body, ctx.Physics, ctx.Blocks);
		output.WriteLine($"node {ctx.Node}, stack cell {cell}");
		Assert.Equal(new TilePos(x, 18), cell);
	}
}
