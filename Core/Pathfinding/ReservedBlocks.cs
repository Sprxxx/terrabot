using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// Block actions with some cells off limits to path moves: they are never mined (+∞) and never filled. Used
/// while building, so a tunnel or pillar on the way does not break the frame or block up the room. The skill
/// doing the building mines and places those cells itself.
/// </summary>
public sealed class ReservedBlocks : IBlockActions
{
	public IBlockActions Inner { get; }
	public IReadOnlySet<TilePos> Reserved { get; }

	public ReservedBlocks(IBlockActions inner, IReadOnlySet<TilePos> reserved)
	{
		Inner = inner;
		Reserved = reserved;
	}

	public float BreakTicks(int x, int y) => Reserved.Contains(new TilePos(x, y)) ? float.PositiveInfinity : Inner.BreakTicks(x, y);
	public bool CanPlaceAt(int x, int y) => !Reserved.Contains(new TilePos(x, y)) && Inner.CanPlaceAt(x, y);
	public int BlockCount => Inner.BlockCount;
	public float PlaceTicks => Inner.PlaceTicks;
}
