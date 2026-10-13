using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// Maps between pathfinding nodes and the 20x42 px player hitbox.
/// Node (x, y): body occupies columns x..x+1 and rows y-2..y; y is the FEET row (the row just above the ground).
/// </summary>
public static class Body
{
	public const int TileSize = 16;
	public const int WidthTiles = 2;
	public const int HeightTiles = 3;

	/// <summary>
	/// Can the body stand with its feet in row <paramref name="y"/>? Slopes and half blocks (Shaped) only count as open
	/// at the feet (the player steps up onto them and stands partly inside); at chest or head height they block like any
	/// block (seen in game: a walk into a half block at head height, failing over and over).
	/// </summary>
	public static bool IsClear(ITileGrid g, int x, int y)
	{
		for (int dy = -HeightTiles + 1; dy <= 0; dy++)
			for (int dx = 0; dx < WidthTiles; dx++) {
				CellKind k = g.Get(x + dx, y + dy);
				if (dy < 0 ? !k.IsHeadroom() : !k.IsPassable())
					return false;
			}
		return true;
	}

	public static bool IsClear(ITileGrid g, TilePos n) => IsClear(g, n.X, n.Y);

	public static bool HasSupport(ITileGrid g, int x, int y) =>
		g.Get(x, y + 1).CanStandOn() || g.Get(x + 1, y + 1).CanStandOn();

	/// <summary>
	/// Standing on one column only, with nothing to land on under the other for <paramref name="depth"/> tiles. The
	/// hitbox overlaps each column by 10 px; a few px of drift (knockback, a swing) slides it off. Seen in game: digging
	/// down beside a shaft, the bot slid in and fell 42 tiles (180 damage).
	/// </summary>
	public static bool IsPrecarious(ITileGrid g, TilePos n, int depth)
	{
		bool left = g.Get(n.X, n.Y + 1).CanStandOn(), right = g.Get(n.X + 1, n.Y + 1).CanStandOn();
		if (left == right)
			return false;
		int open = left ? n.X + 1 : n.X;
		for (int dy = 1; dy <= depth; dy++)
			if (g.Get(open, n.Y + dy).CanStandOn() || g.Get(open, n.Y + dy).IsLiquid())
				return false;
		return true;
	}

	public static bool IsStandable(ITileGrid g, int x, int y) => IsClear(g, x, y) && HasSupport(g, x, y);

	public static bool IsStandable(ITileGrid g, TilePos n) => IsStandable(g, n.X, n.Y);

	/// <summary>Does any body cell touch liquid (used for cost penalties).</summary>
	public static bool InLiquid(ITileGrid g, TilePos n)
	{
		for (int dy = -HeightTiles + 1; dy <= 0; dy++)
			for (int dx = 0; dx < WidthTiles; dx++)
				if (g.Get(n.X + dx, n.Y + dy).IsLiquid())
					return true;
		return false;
	}

	/// <summary>Does any body cell hold lava?</summary>
	public static bool TouchesLava(ITileGrid g, TilePos n)
	{
		for (int dy = -HeightTiles + 1; dy <= 0; dy++)
			for (int dx = 0; dx < WidthTiles; dx++)
				if (g.Get(n.X + dx, n.Y + dy).HasLava())
					return true;
		return false;
	}

	/// <summary>Is the top row of the body (the head) in liquid?</summary>
	public static bool HeadInLiquid(ITileGrid g, TilePos n)
	{
		int head = n.Y - HeightTiles + 1;
		return g.Get(n.X, head).IsLiquid() || g.Get(n.X + 1, head).IsLiquid();
	}

	/// <summary>Horizontal offset that centres the hitbox on its two columns (6 px for a 20 px wide body).</summary>
	public static float CenterOffset(PhysicsParams p) => (WidthTiles * TileSize - p.Width) * 0.5f;

	/// <summary>Hitbox left edge (px) when standing centred on node column <paramref name="nodeX"/>.</summary>
	public static float CanonicalLeft(int nodeX, PhysicsParams p) => nodeX * TileSize + CenterOffset(p);

	/// <summary>Hitbox top (px) when standing on the ground below feet row <paramref name="nodeY"/>.</summary>
	public static float StandingTop(int nodeY, PhysicsParams p) => (nodeY + 1) * TileSize - p.Height;

	/// <summary>
	/// The node a hitbox at (left, top) belongs to. X rounds to the nearest node column; the feet row rounds the
	/// bottom edge to the nearest tile boundary so standing on a half block or slope maps to the full-block node.
	/// </summary>
	public static TilePos NodeOf(float left, float top, PhysicsParams p)
	{
		int x = (int)Math.Floor((left - CenterOffset(p) + TileSize / 2f) / TileSize);
		int y = (int)Math.Floor((top + p.Height + TileSize / 2f - 1f) / TileSize) - 1;
		return new TilePos(x, y);
	}

	public static TilePos NodeOf(in BodyState s, PhysicsParams p) => NodeOf(s.X, s.Y, p);
}
