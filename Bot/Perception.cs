using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot;

/// <summary>An NPC as the bot perceives it (world pixels, top-left position like Entity.position).</summary>
public readonly record struct EntityInfo(
	int Id, int Type, string Name, float X, float Y, int Width, int Height, float VX, float VY,
	int Life, int LifeMax, int Damage, int AiStyle = 0, float Ai0 = 0, float Ai1 = 0, float Ai2 = 0, float Ai3 = 0)
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
	/// <summary>Player.statLifeMax: max life from life crystals and fruit, without buffs or accessories.</summary>
	public int LifeMaxBase { get; set; } = 100;
	/// <summary>Entity.direction: -1 left, +1 right.</summary>
	public int Facing { get; set; } = 1;
	/// <summary>Player.itemAnimation: 0 = free to turn and start a new swing.</summary>
	public int ItemAnimation { get; set; }
	/// <summary>Is there a healing potion that quick-heal (H) would use, with no potion sickness?</summary>
	public bool CanQuickHeal { get; set; }
	/// <summary>Breath (Player.breath, 0..breathMax = 200): runs down under water, drowning at 0.</summary>
	public int Breath { get; set; } = 200;
	public int BreathMax { get; set; } = 200;
	/// <summary>Player.statDefense: armour and accessories.</summary>
	public int Defense { get; set; }
	/// <summary>Armour pieces worn (head, body, legs: 0-3).</summary>
	public int ArmourPieces { get; set; }

	/// <summary>
	/// A full set of armour (all three pieces), whatever its tiers: a mixed silver and iron set is 7 defense (no set bonus),
	/// under the defense 8 that "full armour" was read from before. Seen in game (measured run U2): in a silver helmet and
	/// iron chainmail and greaves, four life crystals at rows 525-534 were left "below row 511, deeper than the gear
	/// allows", and the heart lantern's crystal was looked for from d5 +10h to the end of the run.
	/// </summary>
	public bool FullArmour => ArmourPieces >= 3 || Defense >= BotContext.CavernDefense;
	/// <summary>How far (px) in front of the body's edge the best melee weapon reaches; 0 = no weapon.</summary>
	public float WeaponReach { get; set; }
	public string WeaponName { get; set; } = "";
	/// <summary>Base damage of the melee weapon in use (0 = none).</summary>
	public int WeaponDamage { get; set; }

	/// <summary>Best bow in the hotbar and whether arrows are carried; the shot as the game will fire it.</summary>
	public bool HasRanged { get; set; }
	public string RangedName { get; set; } = "";
	public int AmmoCount { get; set; }
	public ProjectileModel Projectile { get; set; } = new(9f, 15, 0.1f, 16f);

	public bool HasWeapon => WeaponReach > 0f;
	public float LifeFraction => LifeMax <= 0 ? 0f : (float)Life / LifeMax;
}

public static class BodyStateExtensions
{
	public static float CenterY(this in BodyState s, PhysicsParams p) => s.Y + p.Height * 0.5f;
}
