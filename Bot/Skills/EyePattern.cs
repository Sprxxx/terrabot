using System;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The Eye of Cthulhu's attack cycle, read from its AI state (NPC.ai[0..3], AI style 4; decompiled NPC.AI of
/// tModLoader 1.4.4.9). ai[0] is the phase: 0 = first form, 1–2 = the spin into the second form, 3 = second form.
/// In a form, ai[1] is the step: 0 = hover (ai[2] counts up to the first dash), 1 = start a dash (sets the
/// velocity at the player), 2 = dashing (ai[2] counts the dash; ai[3] counts dashes, back to hovering after 3);
/// Expert's second form adds 3–5, a run of fast dashes.
/// <list type="table">
/// <item>first form: hover 600 ticks (Expert 210), dashes 150 apart (Expert 100);</item>
/// <item>spin: two steps of 100 ticks, no dashes;</item>
/// <item>second form: hover 200 ticks, dashes 130 apart (Expert 90).</item>
/// </list>
/// For the Eye, <see cref="BossFightSkill"/> uses this to turn round only when no dash can come in time.
/// </summary>
public static class EyePattern
{
	public const int EyeAiStyle = 4;
	/// <summary>Ticks after a dash starts during which it is still coming at us.</summary>
	public const int DashComingTicks = 40;

	/// <summary>Ticks until the next dash starts; 0 while one is under way and still coming.</summary>
	public static int TicksUntilDash(EntityInfo eye, bool expert)
	{
		int phase = (int)eye.Ai0, step = (int)eye.Ai1, t = (int)eye.Ai2, dashes = (int)eye.Ai3;
		if (phase == 1 || phase == 2) // spinning into the second form, then its hover
			return (phase == 1 ? 200 : 100) - (int)eye.Ai1 + 200;
		bool first = phase == 0;
		int hover = first ? (expert ? 210 : 600) : 200;
		int cycle = first ? (expert ? 100 : 150) : (expert ? 90 : 130);
		switch (step) {
			case 0:
				return Math.Max(0, hover - t);
			case 1:
				return 0;
			case 2:
				if (t < DashComingTicks)
					return 0;
				int left = Math.Max(0, cycle - t);
				return dashes + 1 >= 3 ? left + hover : left;
			default:
				return 0; // Expert's fast dashes
		}
	}

	/// <summary>
	/// The Eye as a <see cref="Threat"/> for the dodge planner: hovering (seek a point 200 px above the player in the
	/// first form, 120 px in the second), dashing (straight, braking after 40 ticks), and the next dash if it is due
	/// within the look-ahead. Classic and Expert numbers from NPC.AI; For the Worthy extras are not modelled.
	/// </summary>
	public static Threat Predict(EntityInfo eye, bool expert)
	{
		int phase = (int)eye.Ai0, step = (int)eye.Ai1, t = (int)eye.Ai2, dashes = (int)eye.Ai3;
		var linear = new Threat(eye.X, eye.Y, eye.Width, eye.Height, eye.VX, eye.VY);
		if (phase == 1 || phase == 2) // spinning: slows down (×0.98 per tick), no dashes
			return linear with { DashedFor = 0, BrakeAfter = 0, Brake = 0.98f };
		if (phase != 0 && phase != 3 || step > 2)
			return linear; // Expert's fast dashes: take them as they come
		bool first = phase == 0;
		float seekAccel = first ? (expert ? 0.15f : 0.04f) : 0.07f;
		float seekSpeed = first ? (expert ? 7f : 5f) : 6f;
		float dashSpeed = first ? (expert ? 7f : 6f) : 6.8f;
		float brake = first ? (expert ? 0.98f * 0.985f : 0.98f) : (expert ? 0.97f * 0.98f : 0.97f);
		int brakeAfter = first ? 40 : (expert ? 50 : 40);
		int hover = first ? (expert ? 210 : 600) : 200;
		int cycle = first ? (expert ? 100 : 150) : (expert ? 90 : 130);
		var model = linear with {
			SeekAccel = seekAccel, SeekSpeed = seekSpeed, SeekAbove = first ? 200f : 120f,
			DashSpeed = dashSpeed, Brake = brake, BrakeAfter = brakeAfter,
		};
		switch (step) {
			case 0: // hovering; the dash starts the tick after ai[2] reaches the hover time
				return model with { DashAt = Math.Max(1, hover - t + 1) };
			case 1: // the dash starts this tick
				return model with { DashAt = 1 };
			default: { // dashing
				int left = Math.Max(1, cycle - t);
				return dashes + 1 >= 3
					? model with { DashedFor = t, SeekFrom = left }
					: model with { DashedFor = t, DashAt = left + 1 };
			}
		}
	}
}
