using TerraBot.Core.Physics;
using Terraria;

namespace TerraBot.Game;

public static class PhysicsReader
{
	/// <summary>
	/// Reads the player's movement stats. Call from ModPlayer.PostUpdateRunSpeeds: by then Player.Update has reset
	/// the defaults, applied equipment/buffs, and run UpdateJumpHeight (jumpHeight/jumpSpeed are static fields).
	/// </summary>
	public static PhysicsParams Read(Player p) => new() {
		MaxRunSpeed = p.maxRunSpeed,
		AccRunSpeed = p.accRunSpeed,
		RunAcceleration = p.runAcceleration,
		RunSlowdown = p.runSlowdown,
		Gravity = p.gravity,
		MaxFallSpeed = p.maxFallSpeed,
		JumpSpeed = Player.jumpSpeed,
		JumpHeight = Player.jumpHeight,
		Width = p.width,
		Height = p.height,
		SafeFallTiles = p.noFallDmg ? 60 : 25 + p.extraFall,
	};
}
