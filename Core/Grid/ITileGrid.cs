namespace TerraBot.Core.Grid;

/// <summary>
/// The pathfinder's only view of the world. Implementations: TerrariaTileGrid (live game) and
/// ArrayTileGrid (tests). Must return <see cref="CellKind.Solid"/> for anything outside the world.
/// </summary>
public interface ITileGrid
{
	CellKind Get(int x, int y);
}
