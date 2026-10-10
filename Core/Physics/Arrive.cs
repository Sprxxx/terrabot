using System;

namespace TerraBot.Core.Physics;

/// <summary>
/// Bang-bang "arrive" steering: press toward the target, brake (press away) once inside stopping distance.
/// The same function drives the jump-template simulation and the live executor, so planned and executed
/// trajectories agree.
/// </summary>
public static class Arrive
{
	/// <returns>-1 press left, 0 nothing, +1 press right.</returns>
	public static int Steer(float x, float vx, float targetX, PhysicsParams p, float tolerance = 1f)
	{
		float d = targetX - x;
		float speed = Math.Abs(vx);
		int moving = Math.Sign(vx);
		float brake = p.BrakeDecel;

		if (Math.Abs(d) <= tolerance)
			return speed <= brake ? 0 : -moving;

		int dir = Math.Sign(d);
		if (moving == dir) {
			// Distance needed to stop by braking, plus half a tick of reaction.
			float stopDist = speed * speed / (2f * brake) + speed * 0.5f;
			if (Math.Abs(d) <= stopDist)
				return speed > brake ? -moving : 0;
		}
		return dir;
	}
}
