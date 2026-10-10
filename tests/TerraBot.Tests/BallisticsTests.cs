using System;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

public class BallisticsTests
{
	private readonly ITestOutputHelper output;
	public BallisticsTests(ITestOutputHelper output) => this.output = output;

	/// <summary>A slow arrow with gravity after a few updates (numbers in the range of a wooden bow + wooden arrow).</summary>
	private static readonly ProjectileModel Arrow = new(Speed: 9.1f, GravityDelay: 15, Gravity: 0.1f, MaxFallSpeed: 16f);

	/// <summary>Fires along the solution and returns the closest approach to the moving target over the flight.</summary>
	private static float ClosestApproach(float ox, float oy, AimSolution s, float tx, float ty, float tvx, float tvy, ProjectileModel m)
	{
		float angle = MathF.Atan2(s.AimY - oy, s.AimX - ox);
		float best = float.MaxValue;
		for (int t = 1; t <= Ballistics.MaxTicks; t++) {
			var (x, y) = Ballistics.At(ox, oy, angle, m, t);
			float px = tx + tvx * t, py = ty + tvy * t;
			best = MathF.Min(best, MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
		}
		return best;
	}

	[Theory]
	[InlineData(400f, 0f)]    // level, to the right
	[InlineData(-400f, 0f)]   // level, to the left
	[InlineData(300f, -250f)] // up and right (a hovering boss)
	[InlineData(-200f, 150f)] // down and left
	public void HitsStationaryTargets_BothSides(float dx, float dy)
	{
		AimSolution s = Ballistics.Lead(0, 0, dx, dy, 0, 0, Arrow);
		float miss = ClosestApproach(0, 0, s, dx, dy, 0, 0, Arrow);
		output.WriteLine($"({dx},{dy}): hits {s.Hits} in {s.Ticks} ticks, miss {miss:0.0} px");
		Assert.True(s.Hits);
		Assert.True(miss <= 12f, $"missed by {miss:0.0} px");
	}

	[Fact]
	public void FarShots_AimAboveTheTarget()
	{
		// 600 px level: gravity after 15 updates means the shot must go up a little.
		AimSolution s = Ballistics.Lead(0, 0, 600, 0, 0, 0, Arrow);
		Assert.True(s.Hits);
		Assert.True(s.AimY < 0, "should aim above a level target that far away");
	}

	[Fact]
	public void LeadsAMovingTarget()
	{
		// Target 300 px right, moving down-left at 4 px/tick (a charging boss).
		AimSolution s = Ballistics.Lead(0, 0, 300, -200, -4, 3, Arrow);
		float miss = ClosestApproach(0, 0, s, 300, -200, -4, 3, Arrow);
		output.WriteLine($"hits {s.Hits} at t{s.Ticks}, miss {miss:0.0}");
		Assert.True(s.Hits);
		Assert.True(miss <= 12f);
	}

	[Fact]
	public void OutOfRange_SaysSo()
	{
		AimSolution s = Ballistics.Lead(0, 0, 4000, 0, 0, 0, Arrow);
		Assert.False(s.Hits);
	}
}
