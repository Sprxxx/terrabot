using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// Block actions where some cells are blocks a build in progress puts back if a path move takes them out: mining one costs
/// its mining plus placing it again, and placing one is what the build wants there anyway. A cost, not a ban
/// (<see cref="ReservedBlocks"/> bans): a walk takes one out only when that beats every way round, as a player under the
/// sky bridge they are building mines a block of it to get on top and puts it back.
/// </summary>
public sealed class RebuiltBlocks : IBlockActions
{
	public IBlockActions Inner { get; }
	public IReadOnlySet<TilePos> Rebuilt { get; }

	public RebuiltBlocks(IBlockActions inner, IReadOnlySet<TilePos> rebuilt)
	{
		Inner = inner;
		Rebuilt = rebuilt;
	}

	public float BreakTicks(int x, int y)
	{
		float t = Inner.BreakTicks(x, y);
		return Rebuilt.Contains(new TilePos(x, y)) ? t + Inner.PlaceTicks : t;
	}

	public bool CanPlaceAt(int x, int y) => Inner.CanPlaceAt(x, y);
	public int BlockCount => Inner.BlockCount;
	public float PlaceTicks => Inner.PlaceTicks;
	public bool HasWall(int x, int y) => Inner.HasWall(x, y);
}
