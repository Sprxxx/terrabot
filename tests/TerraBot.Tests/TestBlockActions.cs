using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Tests;

/// <summary>Fixed-cost mining/placing for tests. '@' cells in the source rows are unbreakable.</summary>
public sealed class TestBlockActions : IBlockActions
{
	private readonly ITileGrid grid;

	public HashSet<TilePos> Unbreakable { get; } = new();
	public float TicksPerBlock { get; set; } = 20f;
	public int BlockCount { get; set; } = 50;
	/// <summary>Matches the in-game value logged for wood/dirt blocks (useTime 10 at tileSpeed 1).</summary>
	public float PlaceTicks => 10f;

	public TestBlockActions(ITileGrid grid) => this.grid = grid;

	/// <summary>Out-of-bounds cells are unbreakable, like out-of-world tiles in the game.</summary>
	public float BreakTicks(int x, int y) =>
		Unbreakable.Contains(new TilePos(x, y)) || (grid is ArrayTileGrid a && !a.InBounds(x, y))
			? float.PositiveInfinity
			: TicksPerBlock;

	/// <summary>Open cells where no block may go (',' in game dumps).</summary>
	public HashSet<TilePos> NoPlace { get; } = new();

	public bool CanPlaceAt(int x, int y) => !grid.Get(x, y).CanStandOn() && !NoPlace.Contains(new TilePos(x, y));

	/// <summary>Cells with a background wall (vanilla Tile.wall &gt; 0): a block placed into open air can be fixed to one.</summary>
	public HashSet<TilePos> Walls { get; } = new();

	public bool HasWall(int x, int y) => Walls.Contains(new TilePos(x, y));

	public static (ArrayTileGrid grid, TestBlockActions blocks) Parse(params string[] rows)
	{
		ArrayTileGrid g = ArrayTileGrid.Parse(rows);
		var b = new TestBlockActions(g);
		for (int y = 0; y < rows.Length; y++)
			for (int x = 0; x < rows[y].Length; x++)
				if (rows[y][x] == '@')
					b.Unbreakable.Add(new TilePos(x, y));
		return (g, b);
	}
}
