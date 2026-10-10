using System;

namespace TerraBot.Core.Grid;

/// <summary>Tile coordinate. X grows right, Y grows DOWN (Terraria convention).</summary>
public readonly record struct TilePos(int X, int Y)
{
	public static TilePos operator +(TilePos a, TilePos b) => new(a.X + b.X, a.Y + b.Y);
	public static TilePos operator -(TilePos a, TilePos b) => new(a.X - b.X, a.Y - b.Y);

	/// <summary>Packs into a dictionary key. Round-trips negative coordinates.</summary>
	public long Key => ((long)X << 32) | (uint)Y;

	public static TilePos FromKey(long key) => new((int)(key >> 32), (int)(uint)(key & 0xFFFFFFFFL));

	/// <summary>Chebyshev (king-move) distance.</summary>
	public int ChebyshevTo(TilePos o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Y - o.Y));

	public override string ToString() => $"({X},{Y})";
}
