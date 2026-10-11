using System;
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
	public void WallStopsTheRun()
	{
		// Pinned against a wall on the right, a dash from the left-above: running right gains nothing.
		BodyState s = Standing(100f);
		Threat dash = EyeDash(s, s.X - 150f, s.Y - 150f);
		DodgePlan right = Dodge.Evaluate(s, P, Floor, -10000, 100f, new[] { dash }, 1, 0);
		DodgePlan stand = Dodge.Evaluate(s, P, Floor, -10000, 100f, new[] { dash }, 0, 0);
		Assert.Equal(stand.HitTick, right.HitTick);
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
