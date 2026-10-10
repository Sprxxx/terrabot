namespace TerraBot.Core.Physics;

/// <summary>Kinematic state of the player hitbox, in world pixels. (X, Y) is the top-left corner, like Entity.position.</summary>
public struct BodyState
{
	public float X;
	public float Y;
	public float VX;
	public float VY;
	/// <summary>Player.jump: remaining ticks of sustained jump impulse.</summary>
	public int Jump;
	/// <summary>Player.releaseJump: jump was not held last tick, so a new jump may start.</summary>
	public bool ReleaseJump;

	/// <summary>The game's own grounded test: velocity.Y == 0 (vanilla uses this exact compare).</summary>
	public readonly bool Grounded => VY == 0f;

	public readonly float Bottom(PhysicsParams p) => Y + p.Height;
	public readonly float CenterX(PhysicsParams p) => X + p.Width * 0.5f;

	public override readonly string ToString() =>
		$"pos ({X:0.##},{Y:0.##}) vel ({VX:0.###},{VY:0.###}) jump {Jump}{(ReleaseJump ? "" : " held")}";
}
