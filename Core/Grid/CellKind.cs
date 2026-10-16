using System;

namespace TerraBot.Core.Grid;

/// <summary>What the pathfinder needs to know about one tile cell. Flags so liquids can combine with blocks.</summary>
[Flags]
public enum CellKind : ushort
{
	Empty = 0,
	/// <summary>Blocks movement from every side (Main.tileSolid and not solid-top). Out-of-world cells are Solid too.</summary>
	Solid = 1,
	/// <summary>Collides only from above (platforms, tables' top row...). Passable when moving up or sideways.</summary>
	Platform = 2,
	Water = 4,
	Lava = 8,
	Honey = 16,
	/// <summary>Set together with Solid for blocks that fall when unsupported (TileID.Sets.Falling: sand, silt, slush...).</summary>
	Falling = 32,
	/// <summary>
	/// Set together with Platform for slopes and half blocks: stood on and walked into like a platform, but a real
	/// block underneath (no dropping through it; digging breaks it).
	/// </summary>
	Shaped = 64,
	/// <summary>
	/// Hurts on touch (TileID.Sets.TouchDamageImmediate: thorns, cactus, spikes). Not passable; with Solid, not stood on
	/// either. Seen in game (world_eye run 38): crimson thorns read as open space, and the bot died in the same patch twice.
	/// </summary>
	Hazard = 128,
	/// <summary>
	/// With Platform | Shaped: a half block (not a slope). The body standing on it is a whole half tile up, never with its
	/// feet in the cell, so a node with one in its feet row is not a place to stand. Seen in game (measured run S1): plans
	/// stood the body "in" half blocks, then dug down from there while it stood on them, 71 drops waiting at the edge.
	/// </summary>
	HalfBlock = 256,
}

public static class CellKindExtensions
{
	public static bool IsSolid(this CellKind k) => (k & CellKind.Solid) != 0;
	public static bool IsPlatform(this CellKind k) => (k & CellKind.Platform) != 0;
	public static bool IsFalling(this CellKind k) => (k & CellKind.Falling) != 0;
	public static bool IsShaped(this CellKind k) => (k & CellKind.Shaped) != 0;
	public static bool IsHalfBlock(this CellKind k) => (k & CellKind.HalfBlock) != 0;
	/// <summary>
	/// A block of the world, whole or shaped (a slope, a half block: read as a platform so feet can ride it): what building
	/// counts as a block to keep in a floor or to mine out of a room.
	/// </summary>
	public static bool IsBlock(this CellKind k) => k.IsSolid() || k.IsShaped();
	/// <summary>Room for the body above the feet: passable and not a slope or half block (those only open at the feet).</summary>
	public static bool IsHeadroom(this CellKind k) => k.IsPassable() && !k.IsShaped();

	/// <summary>A tile that has to be mined to get rid of it: solid, or a slope / half block.</summary>
	public static bool IsBlockTile(this CellKind k) => (k & (CellKind.Solid | CellKind.Shaped)) != 0;
	public static bool HasLava(this CellKind k) => (k & CellKind.Lava) != 0;

	/// <summary>Something the player can stand on.</summary>
	public static bool CanStandOn(this CellKind k) => (k & (CellKind.Solid | CellKind.Platform)) != 0 && (k & CellKind.Hazard) == 0;

	/// <summary>Can the player's body occupy this cell? Lava counts as impassable (avoid it entirely).</summary>
	public static bool IsPassable(this CellKind k) => (k & (CellKind.Solid | CellKind.Lava | CellKind.Hazard)) == 0;

	public static bool IsLiquid(this CellKind k) => (k & (CellKind.Water | CellKind.Lava | CellKind.Honey)) != 0;

	/// <summary>
	/// Liquid round the body: in an open cell (or a slope's or half block's), not held in a solid block. A block keeps the
	/// liquid of its cell (vanilla Liquid.Update leaves a solid tile's liquid alone, and placing a block into water keeps
	/// it there) until it is mined. Seen in game (measured run U10): blocks placed into a flooded row read as water, mining
	/// one of them counted as the head under (200x), and ore 4-8 tiles off was "no path" for three stretches of 1.5 game hours.
	/// </summary>
	public static bool IsOpenLiquid(this CellKind k) => k.IsLiquid() && !k.IsSolid();

	/// <summary>One-character legend used by ASCII grids, dumps and tests.</summary>
	public static char ToChar(this CellKind k)
	{
		if ((k & CellKind.Hazard) != 0) return '^';
		if (k.IsSolid()) return k.IsFalling() ? '%' : (k & CellKind.Water) != 0 ? '&' : '#';
		if (k.IsHalfBlock()) return '-';
		if (k.IsShaped()) return '/';
		if (k.IsPlatform()) return '=';
		if ((k & CellKind.Lava) != 0) return '!';
		if ((k & CellKind.Honey) != 0) return 'h';
		if ((k & CellKind.Water) != 0) return '~';
		return '.';
	}

	/// <summary>
	/// Inverse of <see cref="ToChar"/>. '@' is also Solid (tests use it for unbreakable blocks). '&' is a block holding
	/// water (<see cref="IsOpenLiquid"/>; not a letter, which a parse takes for a marker): drawn as '#', the dumps of measured
	/// run U10 hid the cells that mattered.
	/// </summary>
	public static CellKind FromChar(char c) => c switch {
		'#' or '@' => CellKind.Solid,
		'%' => CellKind.Solid | CellKind.Falling,
		'&' => CellKind.Solid | CellKind.Water,
		'=' => CellKind.Platform,
		'/' => CellKind.Platform | CellKind.Shaped, // a slope, as TerrariaTileGrid reads them
		'-' => CellKind.Platform | CellKind.Shaped | CellKind.HalfBlock,
		'~' => CellKind.Water,
		'!' => CellKind.Lava,
		'h' => CellKind.Honey,
		'^' => CellKind.Hazard,
		_ => CellKind.Empty,
	};
}
