using System;

namespace TerraBot.Core.Grid;

/// <summary>What the pathfinder needs to know about one tile cell. Flags so liquids can combine with blocks.</summary>
[Flags]
public enum CellKind : byte
{
	Empty = 0,
	/// <summary>Blocks movement from every side (Main.tileSolid and not solid-top). Out-of-world cells are Solid too.</summary>
	Solid = 1,
	/// <summary>Collides only from above (platforms, tables' top row...). Passable when moving up or sideways.</summary>
	Platform = 2,
	Water = 4,
	Lava = 8,
	Honey = 16,
}

public static class CellKindExtensions
{
	public static bool IsSolid(this CellKind k) => (k & CellKind.Solid) != 0;
	public static bool IsPlatform(this CellKind k) => (k & CellKind.Platform) != 0;

	/// <summary>Something the player can stand on.</summary>
	public static bool CanStandOn(this CellKind k) => (k & (CellKind.Solid | CellKind.Platform)) != 0;

	/// <summary>Can the player's body occupy this cell? Lava counts as impassable (avoid it entirely).</summary>
	public static bool IsPassable(this CellKind k) => (k & (CellKind.Solid | CellKind.Lava)) == 0;

	public static bool IsLiquid(this CellKind k) => (k & (CellKind.Water | CellKind.Lava | CellKind.Honey)) != 0;

	/// <summary>One-character legend used by ASCII grids, dumps and tests.</summary>
	public static char ToChar(this CellKind k)
	{
		if (k.IsSolid()) return '#';
		if (k.IsPlatform()) return '=';
		if ((k & CellKind.Lava) != 0) return '!';
		if ((k & CellKind.Honey) != 0) return 'h';
		if ((k & CellKind.Water) != 0) return '~';
		return '.';
	}

	public static CellKind FromChar(char c) => c switch {
		'#' => CellKind.Solid,
		'=' => CellKind.Platform,
		'~' => CellKind.Water,
		'!' => CellKind.Lava,
		'h' => CellKind.Honey,
		_ => CellKind.Empty,
	};
}
