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

	public override string ToString() =>
		$"run {MaxRunSpeed:0.###}/{AccRunSpeed:0.###} acc {RunAcceleration:0.###} slow {RunSlowdown:0.###} " +
		$"grav {Gravity:0.###} fall {MaxFallSpeed:0.###} jump {JumpSpeed:0.###}x{JumpHeight} " +
		$"box {Width}x{Height} safeFall {SafeFallTiles}";
}
