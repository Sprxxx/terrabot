namespace TerraBot.Core.Physics;

/// <summary>
/// Faithful replica of the parts of vanilla player movement that the planner relies on, without tile collision.
/// Per tick the game runs (Player.Update): HorizontalMovement() → JumpMovement() → gravity → StepUp/StepDown →
/// DryCollision (position += velocity). Mounts, wings, carts, wind, liquids and ice are not modelled.
/// </summary>
public static class PlayerPhysics
{
	/// <summary>Updates velocity and jump state exactly like the game does before collision. Does not move.</summary>
	public static void ApplyControls(ref BodyState s, in MoveInput c, PhysicsParams p)
	{
		Horizontal(ref s, c, p);
		JumpLogic(ref s, c, p);
		s.VY += p.Gravity;
		if (s.VY > p.MaxFallSpeed)
			s.VY = p.MaxFallSpeed;
	}

	/// <summary>One tick in empty space: controls, then position += velocity.</summary>
	public static void StepFreeSpace(ref BodyState s, in MoveInput c, PhysicsParams p)
	{
		ApplyControls(ref s, c, p);
		s.X += s.VX;
		s.Y += s.VY;
	}

	/// <summary>
	/// One tick in open water: controls with <paramref name="wet"/> (<see cref="PhysicsParams.InWater"/>), then half the velocity
	/// (<see cref="PhysicsParams.WaterMoveFactor"/>, Player.WaterCollision).
	/// </summary>
	public static void StepWater(ref BodyState s, in MoveInput c, PhysicsParams wet)
	{
		ApplyControls(ref s, c, wet);
		s.X += s.VX * PhysicsParams.WaterMoveFactor;
		s.Y += s.VY * PhysicsParams.WaterMoveFactor;
	}

	/// <summary>Player.HorizontalMovement, non-mounted, no wind/ice/carts. Branch order matters and matches vanilla.</summary>
	public static void Horizontal(ref BodyState s, in MoveInput c, PhysicsParams p)
	{
		bool grounded = s.VY == 0f;
		if (c.Left && s.VX > -p.MaxRunSpeed) {
			if (s.VX > p.RunSlowdown)
				s.VX -= p.RunSlowdown;
			s.VX -= p.RunAcceleration;
		}
		else if (c.Right && s.VX < p.MaxRunSpeed) {
			if (s.VX < -p.RunSlowdown)
				s.VX += p.RunSlowdown;
			s.VX += p.RunAcceleration;
		}
		else if (c.Left && s.VX > -p.AccRunSpeed) {
			// Running-boots band: only accelerates on the ground (or with wings, not modelled).
			if (grounded) {
				if (s.VX > p.RunSlowdown)
					s.VX -= p.RunSlowdown;
				s.VX -= p.RunAcceleration * 0.2f;
			}
		}
		else if (c.Right && s.VX < p.AccRunSpeed) {
			if (grounded) {
				if (s.VX < -p.RunSlowdown)
					s.VX += p.RunSlowdown;
				s.VX += p.RunAcceleration * 0.2f;
			}
		}
		else if (grounded) {
			if (s.VX > p.RunSlowdown)
				s.VX -= p.RunSlowdown;
			else if (s.VX < -p.RunSlowdown)
				s.VX += p.RunSlowdown;
			else
				s.VX = 0f;
		}
		else {
			float airSlow = p.RunSlowdown * 0.5f;
			if (s.VX > airSlow)
				s.VX -= airSlow;
			else if (s.VX < -airSlow)
				s.VX += airSlow;
			else
				s.VX = 0f;
		}
	}

	/// <summary>Player.JumpMovement without extra jumps, sliding, mounts or the portable stool.</summary>
	public static void JumpLogic(ref BodyState s, in MoveInput c, PhysicsParams p)
	{
		if (c.Jump) {
			if (s.Jump > 0) {
				if (s.VY == 0f) {
					s.Jump = 0; // bonked a ceiling
				}
				else {
					s.VY = -p.JumpSpeed;
					s.Jump--;
				}
			}
			else if (s.VY == 0f && s.ReleaseJump) {
				s.VY = -p.JumpSpeed;
				s.Jump = p.JumpHeight;
			}
			s.ReleaseJump = false;
		}
		else {
			s.Jump = 0;
			s.ReleaseJump = true;
		}
	}

	/// <summary>Highest rise (px above the start) of a jump from rest holding jump for <paramref name="holdTicks"/> ticks.</summary>
	public static float ApexRise(PhysicsParams p, int holdTicks)
	{
		var s = new BodyState { ReleaseJump = true };
		float minY = 0f;
		for (int t = 0; t < 400; t++) {
			var input = new MoveInput { Jump = t < holdTicks };
			StepFreeSpace(ref s, input, p);
			if (s.Y < minY)
				minY = s.Y;
			if (s.VY > 0f && t > 0)
				break;
		}
		return -minY;
	}

	/// <summary>Ticks to fall <paramref name="pixels"/> starting at rest, no input.</summary>
	public static int FallTicks(PhysicsParams p, float pixels)
	{
		var s = new BodyState { VY = 0.0001f, ReleaseJump = true };
		int t = 0;
		while (s.Y < pixels && t < 1000) {
			StepFreeSpace(ref s, MoveInput.None, p);
			t++;
		}
		return t;
	}
}
