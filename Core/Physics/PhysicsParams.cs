using System;

namespace TerraBot.Core.Physics;

/// <summary>
/// The player movement stats that matter for planning. Defaults are vanilla values verified against the
/// decompiled Player.Update / HorizontalMovement / JumpMovement of tModLoader 1.4.4.9 (2026.08). In game the
/// live values are read every tick in ModPlayer.PostUpdateRunSpeeds (they change with boots, buffs, potions).
/// Record equality is used to detect changes and rebuild jump templates.
/// </summary>
public sealed record PhysicsParams
{
	/// <summary>Player.maxRunSpeed, px/tick (vanilla 3).</summary>
	public float MaxRunSpeed { get; init; } = 3f;
	/// <summary>Player.accRunSpeed, px/tick; above maxRunSpeed only with running boots (vanilla = maxRunSpeed).</summary>
	public float AccRunSpeed { get; init; } = 3f;
	/// <summary>Player.runAcceleration, px/tick² (vanilla 0.08).</summary>
	public float RunAcceleration { get; init; } = 0.08f;
	/// <summary>Player.runSlowdown, px/tick² (vanilla 0.2). Ground friction; half of it in the air.</summary>
	public float RunSlowdown { get; init; } = 0.2f;
	/// <summary>Player.gravity, px/tick² (vanilla Player.defaultGravity 0.4).</summary>
	public float Gravity { get; init; } = 0.4f;
	/// <summary>Player.maxFallSpeed, px/tick (vanilla 10).</summary>
	public float MaxFallSpeed { get; init; } = 10f;
	/// <summary>Player.jumpSpeed (static), px/tick, after UpdateJumpHeight (vanilla 5.01).</summary>
	public float JumpSpeed { get; init; } = 5.01f;
	/// <summary>Player.jumpHeight (static): ticks the jump impulse is sustained while held (vanilla 15).</summary>
	public int JumpHeight { get; init; } = 15;
	/// <summary>Hitbox width in px (Player.width = 20).</summary>
	public int Width { get; init; } = 20;
	/// <summary>Hitbox height in px (Player.height = 42).</summary>
	public int Height { get; init; } = 42;
	/// <summary>Tiles that can be fallen without damage: 25 + Player.extraFall (damage if strictly more).</summary>
	public int SafeFallTiles { get; init; } = 25;

	public static readonly PhysicsParams Default = new();

	/// <summary>Upper bound on horizontal speed (max-speed oscillation overshoots by one acceleration step).</summary>
	public float MaxHorizontalSpeed => Math.Max(MaxRunSpeed, AccRunSpeed) + RunAcceleration;

	/// <summary>Deceleration when pressing against the current motion (slowdown + acceleration, same in air).</summary>
	public float BrakeDecel => RunSlowdown + RunAcceleration;

	/// <summary>Relative change in run speed/acceleration/slowdown that a running plan tolerates (25%).</summary>
	public const float HorizontalTolerance = 0.25f;

	/// <summary>
	/// Would a plan made with <paramref name="planned"/> become unreliable under these params? Horizontal stats
	/// may drift by up to <see cref="HorizontalTolerance"/>: arrive steering is closed-loop and the closed-loop
	/// tests run jumps fine at ±25% speed. Anything that changes jump height, fall behaviour or the hitbox counts:
	/// those define which jumps and drops exist at all. Fall speed gets 10% (vanilla flickers 10 / 10.01).
	/// </summary>
	public bool MateriallyDiffers(PhysicsParams planned)
	{
		if (planned == null)
			return true;
		return JumpHeight != planned.JumpHeight || Width != planned.Width || Height != planned.Height
			|| SafeFallTiles != planned.SafeFallTiles
			|| Rel(JumpSpeed, planned.JumpSpeed) > 0.02f || Rel(Gravity, planned.Gravity) > 0.02f
			|| Rel(MaxFallSpeed, planned.MaxFallSpeed) > 0.10f
			|| Rel(MaxRunSpeed, planned.MaxRunSpeed) > HorizontalTolerance + 1e-4f
			|| Rel(AccRunSpeed, planned.AccRunSpeed) > HorizontalTolerance + 1e-4f
			|| Rel(RunAcceleration, planned.RunAcceleration) > HorizontalTolerance + 1e-4f
			|| Rel(RunSlowdown, planned.RunSlowdown) > HorizontalTolerance + 1e-4f;
	}

	/// <summary>Relative difference of a against the planned value b.</summary>
	private static float Rel(float a, float b) => b == 0f ? (a == 0f ? 0f : float.PositiveInfinity) : Math.Abs(a - b) / Math.Abs(b);

	public override string ToString() =>
		$"run {MaxRunSpeed:0.###}/{AccRunSpeed:0.###} acc {RunAcceleration:0.###} slow {RunSlowdown:0.###} " +
		$"grav {Gravity:0.###} fall {MaxFallSpeed:0.###} jump {JumpSpeed:0.###}x{JumpHeight} " +
		$"box {Width}x{Height} safeFall {SafeFallTiles}";
}
