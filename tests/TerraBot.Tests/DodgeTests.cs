using System;
using System.Linq;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

public class DodgeTests
{
	private readonly ITestOutputHelper output;
	public DodgeTests(ITestOutputHelper output) => this.output = output;

	private static readonly PhysicsParams P = PhysicsParams.Default;
	private const float Floor = 1000f;

	/// <summary>Player standing on the floor with its left edge at x.</summary>
	private static BodyState Standing(float x, float vx = 0f) =>
		new() { X = x, Y = Floor - P.Height, VX = vx, ReleaseJump = true };

	/// <summary>The Eye of Cthulhu (100 x 110) dashing at 6 px/tick from (fromX, fromY) at the player's centre.</summary>
	private static Threat EyeDash(BodyState s, float fromX, float fromY, float speed = 6f)
	{
		float tx = s.X + P.Width / 2f, ty = s.Y + P.Height / 2f;
		float dx = tx - fromX, dy = ty - fromY, d = MathF.Sqrt(dx * dx + dy * dy);
		return new Threat(fromX - 50f, fromY - 55f, 100, 110, dx / d * speed, dy / d * speed);
	}

	[Fact]
	public void NoThreats_KeepsThePreferredDirection()
	{
		DodgePlan best = Dodge.Best(Standing(0), P, Floor, -10000, 10000, Array.Empty<Threat>(), preferDir: -1);
		Assert.False(best.Hits);
		Assert.Equal(-1, best.Dir);
		Assert.Equal(0, best.JumpHold);
	}

	[Fact]
	public void LevelDash_IsAvoided()
	{
		// A low, flat threat along the floor from the right: standing still is hit; jumping over it or running
		// ahead of it (the Eye brakes after 40 ticks of a dash) is not, within the horizon.
		BodyState s = Standing(0);
		var dash = new Threat(260f, Floor - 40f, 100, 30, -6f, 0f); // a flat, low threat (a servant-sized box)
		DodgePlan stand = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { dash }, 0, 0);
		DodgePlan best = Dodge.Best(s, P, Floor, -10000, 10000, new[] { dash }, preferDir: 1);
		output.WriteLine($"stand {stand}, best {best}");
		Assert.True(stand.Hits);
		Assert.False(best.Hits, $"best plan {best} is still hit");
	}

	[Fact]
	public void DashFromAbove_RunningSidewaysClears()
	{
		// The Eye hovers ~200 px above and dashes straight down at a runner: carrying on running is enough.
		BodyState s = Standing(0, vx: 3f);
		Threat dash = EyeDash(s, s.X + 10f, s.Y - 220f);
		DodgePlan best = Dodge.Best(s, P, Floor, -10000, 10000, new[] { dash }, preferDir: 1);
		output.WriteLine(best.ToString());
		Assert.False(best.Hits);
		Assert.Equal(1, best.Dir);
	}

	[Fact]
	public void PhaseTwoDash_AtAStandingPlayer_CannotBeAvoided()
	{
		// Phase 2: the Eye hovers ~120 px above and to one side and dashes at 6.8 px/tick. A player caught standing
		// (just turned round) cannot get its 20 px out from under a 100 x 110 px eye in ~22 ticks: running only
		// reaches 0.08 px/tick² (≈ 20 px). Hence the kite never turns round while a dash could come (BossFightSkill).
		BodyState s = Standing(0);
		Threat dash = EyeDash(s, s.X - 160f, s.Y - 120f, speed: 6.8f);
		DodgePlan stand = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { dash }, 0, 0);
		DodgePlan best = Dodge.Best(s, P, Floor, -10000, 10000, new[] { dash }, preferDir: -1);
		output.WriteLine($"stand {stand}, best {best}");
		Assert.True(stand.Hits);
		Assert.True(best.Hits);
		Assert.True(best.HitTick >= stand.HitTick, "the planner should at least not make it worse");
	}

	[Fact]
	public void RunnerCaughtFromBehind_IsNotHitBecauseTheDashAimsWhereItWas()
	{
		// The dash is aimed at where the player is when it starts; a full-speed runner has moved on. Kiting works.
		BodyState s = Standing(0, vx: 3f);
		Threat dash = EyeDash(s, s.X - 200f, s.Y - 120f, speed: 6.8f);
		Assert.False(Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { dash }, 1, 0).Hits);
	}

	[Fact]
	public void WithRoomBothWays_DoesNotDodgeIntoTheWall()
	{
		// A slow threat from above, a wall 40 px to the left: running either way clears it, but left ends cornered.
		BodyState s = Standing(100f);
		var threat = new Threat(s.X - 40f, s.Y - 300f, 100, 110, 0f, 1f);
		DodgePlan best = Dodge.Best(s, P, Floor, 60f, 10000, new[] { threat }, preferDir: -1);
		output.WriteLine(best.ToString());
		Assert.False(best.Hits);
		Assert.NotEqual(-1, best.Dir);
	}

	[Fact]
	public void WallStopsTheRun()
	{
		// Pinned against a wall on the right, a dash from the left-above: running right gains nothing.
		BodyState s = Standing(100f);
		Threat dash = EyeDash(s, s.X - 150f, s.Y - 150f);
		DodgePlan right = Dodge.Evaluate(s, P, Floor, -10000, 100f, new[] { dash }, 1, 0);
		DodgePlan stand = Dodge.Evaluate(s, P, Floor, -10000, 100f, new[] { dash }, 0, 0);
		Assert.Equal(stand.HitTick, right.HitTick);
	}

	[Fact]
	public void InsideAThreat_WhileImmune_TheTouchIsNoHit_AndThePlanThatGetsOutWins()
	{
		// A body 40 px wide passing slowly right through the player, which was just hit: immune for 39 more ticks (Player.Hurt:
		// immuneTime 40). Without the immunity every plan is "hit in 1 tick" and the tie goes to the preferred way, right, with
		// the body; with it, the plan out to the left is not hit at all.
		BodyState s = Standing(100f);
		var body = new Threat(s.X - 10f, s.Y, 40, 40, 1f, 0f);
		DodgePlan blind = Dodge.Best(s, P, Floor, -10000, 10000, new[] { body }, preferDir: 1);
		DodgePlan immune = Dodge.Best(s, P, Floor, -10000, 10000, new[] { body }, preferDir: 1, immuneTicks: 39);
		output.WriteLine($"blind {blind}, immune {immune}");
		Assert.Equal(1, blind.HitTick);
		Assert.Equal(1, blind.Dir);
		Assert.False(immune.Hits);
		Assert.Equal(-1, immune.Dir);
		Assert.True(immune.ContactTicks > 0);
	}

	[Fact]
	public void OnTerrain_ThePlanJumpsOutOfAPit_WhereWallsAsHighAsTheSkyHoldIt()
	{
		// A pit two columns wide and three rows deep (columns 10-11, floor at row 13), a worm's body level with the player
		// streaming through it from the right, ten segments of 38 px. With the pit's sides as walls of the flat model no plan
		// gets out (a jump in place comes down into the stream); on the tiles a jump out lands on the ground beside the pit.
		var rows = new string[30];
		for (int y = 0; y < 30; y++)
			rows[y] = y < 10 ? new string('.', 40) : y <= 12 ? new string('#', 10) + ".." + new string('#', 28) : new string('#', 40);
		var g = Core.Grid.ArrayTileGrid.Parse(rows);
		var s = new BodyState { X = 10 * 16f + 6f, Y = 13 * 16f - P.Height, ReleaseJump = true };
		Threat[] stream = System.Linq.Enumerable.Range(0, 10).Select(k => new Threat(s.X + 120f + 38f * k, s.Y + 2f, 38, 38, -6f, 0f)).ToArray();
		DodgePlan flat = Dodge.Best(s, P, 13 * 16f, 10 * 16f, 12 * 16f - P.Width, stream, preferDir: 1);
		DodgePlan tiles = Dodge.Best(s, P, float.PositiveInfinity, -10000, 10000, stream, preferDir: 1, terrain: g);
		output.WriteLine($"flat {flat}, tiles {tiles}");
		Assert.True(flat.Hits);
		Assert.False(tiles.Hits);
		Assert.True(tiles.JumpHold > 0);
		Assert.True(tiles.EndBottom <= 10 * 16f + 0.5f, "on the ground beside the pit");
	}

	[Theory]
	[InlineData(0, 0, 10, 10, 20, 0, 10, 10, 10f)]   // 10 px apart horizontally
	[InlineData(0, 0, 10, 10, 5, 5, 10, 10, -5f)]    // overlapping
	[InlineData(0, 0, 10, 10, 0, 30, 10, 10, 20f)]   // 20 px below
	public void Gap_IsTheLargerAxisGap(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh, float expected)
	{
		Assert.Equal(expected, Dodge.Gap(ax, ay, aw, ah, bx, by, bw, bh), 3);
	}
}
