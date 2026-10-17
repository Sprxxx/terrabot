using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// Block actions with cells where an action kept failing in the world: no placing in <see cref="NoPlace"/>, no
/// breaking in <see cref="NoBreak"/>. The sets are live (filled while the bot plays), so plans made after a failure
/// route round it. Seen in game: a block that would not go in at one cell (something in the way the grid does not
/// show) and thirteen re-plans in a row producing the same plan.
/// </summary>
public sealed class VetoedBlocks : IBlockActions
{
	public IBlockActions Inner { get; }
	public IReadOnlySet<TilePos> NoPlace { get; }
	public IReadOnlySet<TilePos> NoBreak { get; }

	public VetoedBlocks(IBlockActions inner, IReadOnlySet<TilePos> noPlace, IReadOnlySet<TilePos> noBreak)
	{
		Inner = inner;
		NoPlace = noPlace;
		NoBreak = noBreak;
	}

	public float BreakTicks(int x, int y) => NoBreak.Contains(new TilePos(x, y)) ? float.PositiveInfinity : Inner.BreakTicks(x, y);
	public bool CanPlaceAt(int x, int y) => !NoPlace.Contains(new TilePos(x, y)) && Inner.CanPlaceAt(x, y);
	public int BlockCount => Inner.BlockCount;
	public float PlaceTicks => Inner.PlaceTicks;
	public bool HasWall(int x, int y) => Inner.HasWall(x, y);
}
