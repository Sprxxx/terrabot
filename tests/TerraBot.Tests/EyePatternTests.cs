using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Physics;
using Xunit;

namespace TerraBot.Tests;

public class EyePatternTests
{
	private static EntityInfo Eye(float ai0, float ai1, float ai2, float ai3) =>
		new(0, 4, "Eye of Cthulhu", 0, 0, 100, 110, 0, 0, 2800, 2800, 15, EyePattern.EyeAiStyle, ai0, ai1, ai2, ai3);

	[Theory]
	[InlineData(0, 0, 100, 0, false, 500)]  // first form hovering: dash at ai[2] = 600
	[InlineData(0, 0, 100, 0, true, 110)]   // Expert hovers 210
	[InlineData(0, 1, 0, 0, false, 0)]      // a dash is starting
	[InlineData(0, 2, 10, 0, false, 0)]     // dashing at us
	[InlineData(0, 2, 60, 0, false, 90)]    // dash 1 went by: dash 2 at 150
	[InlineData(0, 2, 60, 2, false, 690)]   // dash 3 went by: back to hovering for 600
	[InlineData(1, 50, 0, 0, false, 350)]   // spinning: no dashes for a while
	[InlineData(3, 0, 150, 0, false, 50)]   // second form hover: dash at 200
	[InlineData(3, 2, 100, 1, false, 30)]   // second form: dashes 130 apart
	[InlineData(3, 2, 100, 1, true, 0)]     // Expert: 90 apart, already due
	public void TicksUntilDash(float ai0, float ai1, float ai2, float ai3, bool expert, int expected)
	{
		Assert.Equal(expected, EyePattern.TicksUntilDash(Eye(ai0, ai1, ai2, ai3), expert));
	}

	private static readonly PhysicsParams P = PhysicsParams.Default;
	private const float Floor = 1000f;

	/// <summary>An Eye with its centre at (cx, cy) moving at (vx, vy), in the given AI state.</summary>
	private static EntityInfo EyeAt(float cx, float cy, float vx, float vy, float ai0, float ai1, float ai2, float ai3) =>
		new(0, 4, "Eye of Cthulhu", cx - 50, cy - 55, 100, 110, vx, vy, 2800, 2800, 15, EyePattern.EyeAiStyle, ai0, ai1, ai2, ai3);

	[Fact]
	public void SecondFormHover_JumpingIntoItIsPredicted()
	{
		// Hovering 120 px above the player's centre: a full jump rises ~100 px into it. A straight-line model saw
		// nothing (the Eye barely moves); the hover model follows the player and sees the jump hit.
		var s = new BodyState { X = 0, Y = Floor - P.Height, ReleaseJump = true };
		float pcx = s.X + P.Width / 2f, pcy = s.Y + P.Height / 2f;
		Threat eye = EyePattern.Predict(EyeAt(pcx, pcy - 120, 0, 0, 3, 0, 20, 0), expert: false);
		DodgePlan jump = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { eye }, 0, 15);
		DodgePlan stay = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { eye }, 0, 0);
		Assert.True(jump.Hits);
		Assert.False(stay.Hits);
	}

	[Fact]
	public void DashDueSoon_StandingIsHit_RunningIsNot()
	{
		// First form, 10 ticks before a dash, hovering up and to the left: the dash will aim at the player.
		var s = new BodyState { X = 0, Y = Floor - P.Height, VX = 3f, ReleaseJump = true };
		float pcx = s.X + P.Width / 2f, pcy = s.Y + P.Height / 2f;
		Threat eye = EyePattern.Predict(EyeAt(pcx - 60, pcy - 200, 2f, 0, 0, 0, 590, 0), expert: false);
		Assert.Equal(11, eye.DashAt);
		DodgePlan run = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { eye }, 1, 0);
		DodgePlan turn = Dodge.Evaluate(s, P, Floor, -10000, 10000, new[] { eye }, -1, 0);
		Assert.False(run.Hits, $"running on: {run}");
		Assert.True(turn.Hits, $"turning round: {turn}");
	}
}
