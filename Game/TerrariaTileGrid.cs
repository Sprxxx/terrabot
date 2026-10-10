using TerraBot.Core.Grid;
using Terraria;
using Terraria.ID;

namespace TerraBot.Game;

/// <summary>
/// Live <see cref="ITileGrid"/> over Main.tile. Mirrors Collision.TileCollision's notion of blocking:
/// a tile blocks if it is active, not actuated, and tileSolid; solid-top tiles (platforms, and the top row
/// (frameY 0) of tables etc.) only collide from above. Slopes and half blocks count as full blocks for now.
/// Note for multiplayer clients: unreceived world sections read as empty air.
/// </summary>
public sealed class TerrariaTileGrid : ITileGrid
{
	public static readonly TerrariaTileGrid Instance = new();

	public CellKind Get(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 1))
			return CellKind.Solid;

		Tile t = Main.tile[x, y];
		CellKind k = CellKind.Empty;
		if (t.HasUnactuatedTile) {
			ushort type = t.TileType;
			bool solid = Main.tileSolid[type];
			bool top = Main.tileSolidTop[type];
			if (solid && !top)
				k = CellKind.Solid;
			else if (top && (solid || t.TileFrameY == 0))
				k = CellKind.Platform;
		}

		if (t.LiquidAmount > 0) {
			int liquid = t.LiquidType;
			if (liquid == LiquidID.Lava)
				k |= CellKind.Lava;
			else if (liquid == LiquidID.Honey)
				k |= CellKind.Honey;
			else
				k |= CellKind.Water;
		}
		return k;
	}
}
