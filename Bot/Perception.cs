using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot;

/// <summary>An NPC as the bot perceives it (world pixels, top-left position like Entity.position).</summary>
public readonly record struct EntityInfo(
	int Id, int Type, string Name, float X, float Y, int Width, int Height, float VX, float VY,
	int Life, int LifeMax, int Damage, int AiStyle = 0)
{
	public float CenterX => X + Width * 0.5f;
	public float CenterY => Y + Height * 0.5f;
	public float Bottom => Y + Height;

	/// <summary>Tile under the middle of the NPC's feet.</summary>
	public TilePos FeetTile => new((int)Math.Floor(CenterX / Body.TileSize), (int)Math.Floor((Bottom - 1f) / Body.TileSize));

	public float DistanceTo(float x, float y) => MathF.Sqrt((CenterX - x) * (CenterX - x) + (CenterY - y) * (CenterY - y));
}

/// <summary>An item lying in the world.</summary>
public readonly record struct DropInfo(int Id, int Type, string Name, int Stack, float X, float Y, int Width, int Height)
{
	public float CenterX => X + Width * 0.5f;
	public float CenterY => Y + Height * 0.5f;
	public TilePos Tile => new((int)Math.Floor(CenterX / Body.TileSize), (int)Math.Floor(CenterY / Body.TileSize));
}

/// <summary>Combat-relevant state of the player and the best weapon in the hotbar.</summary>
public sealed class CombatInfo
{
	public int Life { get; set; } = 100;
	public int LifeMax { get; set; } = 100;
	/// <summary>Entity.direction: -1 left, +1 right.</summary>
	public int Facing { get; set; } = 1;
	/// <summary>Player.itemAnimation: 0 = free to turn and start a new swing.</summary>
	public int ItemAnimation { get; set; }
	/// <summary>Is there a healing potion that quick-heal (H) would use, with no potion sickness?</summary>
	public bool CanQuickHeal { get; set; }
	/// <summary>How far (px) in front of the body's edge the best melee weapon reaches; 0 = no weapon.</summary>
	public float WeaponReach { get; set; }
	public string WeaponName { get; set; } = "";

	public bool HasWeapon => WeaponReach > 0f;
	public float LifeFraction => LifeMax <= 0 ? 0f : (float)Life / LifeMax;
}

public static class BodyStateExtensions
{
	public static float CenterY(this in BodyState s, PhysicsParams p) => s.Y + p.Height * 0.5f;
}
