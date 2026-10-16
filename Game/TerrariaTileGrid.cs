using TerraBot.Core.Grid;
using Terraria;
using Terraria.ID;

namespace TerraBot.Game;

/// <summary>
/// Live <see cref="ITileGrid"/> over Main.tile. Mirrors Collision.TileCollision's notion of blocking:
/// a tile blocks if it is active, not actuated, and tileSolid; solid-top tiles (platforms, and the top row
/// (frameY 0) of tables etc.) only collide from above. Slopes and half blocks count as platforms: something to stand
/// on, but passable, as the player walks up them without jumping and stands partly inside their cell. As full blocks,
/// the bot on a slope stood a row lower than any plan could start from and re-planned the same step for minutes; as
/// empty space, standing on top of one had no support under it ("invalid start").
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
		if (t.HasUnactuatedTile && !(DoorsOpenThemselves && IsClosedDoor(t.TileType))) {
			ushort type = t.TileType;
			bool solid = Main.tileSolid[type];
			bool top = Main.tileSolidTop[type];
			bool shaped = t.IsHalfBlock || t.Slope != SlopeType.Solid;
			if (solid && !top && shaped)
				k = t.IsHalfBlock ? CellKind.Platform | CellKind.Shaped | CellKind.HalfBlock : CellKind.Platform | CellKind.Shaped;
			else if (solid && !top)
				k = TileID.Sets.Falling[type] ? CellKind.Solid | CellKind.Falling : CellKind.Solid;
			else if (top && (solid || t.TileFrameY == 0))
				k = CellKind.Platform;
			if (TileID.Sets.TouchDamageImmediate[type] > 0)
				k |= CellKind.Hazard;
			// A pressure plate (TileID.Sets.PressurePlate: 135, 428, 442) sets off whatever is wired to it when stepped on: not
			// to be walked on, jumped over like thorns. Seen in game (measured run U17): walking to look for floor for a bench at
			// (1929,425), an explosives trap went off (projectile 108, 425 damage).
			if (TileID.Sets.PressurePlate[type] != -1)
				k |= CellKind.Hazard;
			// A cobweb is not solid (a cut tile, as grass), but it holds the player nearly still: a block to cut through,
			// as a player swings at it. Seen in game (measured run I1): read as open air, every walk sank through webs at
			// 1.79 px a tick, "never landed", and the rests ran out until auto mode stopped.
			if (type == TileID.Cobweb)
				k = CellKind.Solid;
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

	/// <summary>
	/// With the "auto-open doors" setting on for everything, walking into a closed door at floor level opens it (and
	/// it closes again behind the player): DoorOpeningHelper, run from Player.Update before the movement. So for
	/// walking, a closed door is open space. Seen in game: as a solid tile, the bot outside its house at night found
	/// no path in. With the setting off the door stays solid (the bot never right-clicks doors).
	/// </summary>
	private static bool DoorsOpenThemselves =>
		Terraria.GameContent.DoorOpeningHelper.PreferenceSettings == Terraria.GameContent.DoorOpeningHelper.DoorAutoOpeningPreference.EnabledForEverything;

	private static bool IsClosedDoor(ushort type) =>
		type == TileID.ClosedDoor || type == TileID.TallGateClosed || TileID.Sets.OpenDoorID[type] > -1;
}
