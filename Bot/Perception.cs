using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot;

/// <summary>
/// An NPC as the bot perceives it (world pixels, top-left position like Entity.position). <see cref="NoTileCollide"/>: vanilla
/// NPC.noTileCollide, it moves through blocks (worms, ghosts). <see cref="DontTakeDamage"/>: vanilla NPC.dontTakeDamage, nothing
/// hurts it (the Brain of Cthulhu while a Creeper lives, NPC.cs:7592 and 33157); such an NPC is perceived only as a boss's part, for
/// its contact damage.
/// </summary>
public readonly record struct EntityInfo(
	int Id, int Type, string Name, float X, float Y, int Width, int Height, float VX, float VY,
	int Life, int LifeMax, int Damage, int AiStyle = 0, float Ai0 = 0, float Ai1 = 0, float Ai2 = 0, float Ai3 = 0, bool NoTileCollide = false,
	bool DontTakeDamage = false)
{
	public float CenterX => X + Width * 0.5f;
	public float CenterY => Y + Height * 0.5f;
	public float Bottom => Y + Height;

	/// <summary>Tile under the middle of the NPC's feet.</summary>
	public TilePos FeetTile => new((int)Math.Floor(CenterX / Body.TileSize), (int)Math.Floor((Bottom - 1f) / Body.TileSize));

	public float DistanceTo(float x, float y) => MathF.Sqrt((CenterX - x) * (CenterX - x) + (CenterY - y) * (CenterY - y));
}

/// <summary>
/// A hostile projectile in sight (world pixels, top-left like Entity.position): an eye laser of the Wall of Flesh (projectile 83,
/// NPC.cs:26668-26697), a Demon's scythe. <see cref="VX"/> and <see cref="VY"/> are its movement per tick, the velocity times
/// 1 + extraUpdates (Projectile.Update moves it that many times a tick: the laser's extraUpdates 2 makes 9 px/tick 27,
/// Projectile.cs:1817-1830). <see cref="TileCollide"/>: it stops on tiles.
/// </summary>
public readonly record struct ShotInfo(int Id, int Type, float X, float Y, int Width, int Height, float VX, float VY, int Damage, bool TileCollide = true)
{
	public float CenterX => X + Width * 0.5f;
	public float CenterY => Y + Height * 0.5f;
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
	/// <summary>
	/// The item in hand (Player.HeldItem.type, 0 for none): with <see cref="ItemAnimation"/> above 0 it is the one being used. A use
	/// shows by this even when the stack does not shrink: a mod's "researched consumables are infinite" (Upgraded Research's
	/// GlobalItem.ConsumeItem returning false, which Player.ItemCheck and PlaceThing respect) keeps the item, the effect still comes.
	/// </summary>
	public int HeldItemType { get; set; }
	/// <summary>Is there a healing potion that quick-heal (H) would use, with no potion sickness?</summary>
	public bool CanQuickHeal { get; set; }
	/// <summary>Ticks of potion sickness left (vanilla Player.potionDelay): above 0 just after a heal.</summary>
	public int PotionSickTicks { get; set; }
	/// <summary>Breath (Player.breath, 0..breathMax = 200): runs down under water, drowning at 0.</summary>
	public int Breath { get; set; } = 200;
	public int BreathMax { get; set; } = 200;
	/// <summary>Player.statDefense: armour and accessories.</summary>
	public int Defense { get; set; }
	/// <summary>
	/// Within a town NPC's range (vanilla Player.townNPCs > 0): the game spawns no hostile there on the surface
	/// (spawnInfo.PlayerSafe), so nothing to hunt.
	/// </summary>
	public bool InTown { get; set; }
	/// <summary>
	/// In the evil biome's zone (vanilla Player.ZoneCorrupt or ZoneCrimson: SceneMetrics.EvilTileCount at least 300 in the
	/// scan round the player), as a player knows it by the background and the music changing; null where nothing tells it
	/// (tests), and the bot goes by its own count of the tiles it has seen (<see cref="BotContext.CorruptionAround"/>). Worm
	/// Food works only in it (Player.cs:43462), and the Eater of Worlds dives and despawns once no player is in it
	/// (NPC.AI_006_Worms, NPC.cs:55436). The game updates it with its scene scan (LightingEngine's ExportMetrics, every
	/// fourth lighting pass), so it can lag the body by a few ticks; the worm reads the same flag.
	/// </summary>
	public bool? InEvilZone { get; set; }
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
