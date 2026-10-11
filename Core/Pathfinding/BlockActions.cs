using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// What the planner may know about changing the world, answered by the game adapter from the player's
/// hotbar (TerrariaBlockActions) or by tests. Implementations must not mutate anything.
/// </summary>
public interface IBlockActions
{
	/// <summary>Ticks to mine the (solid) cell with the best usable pickaxe; +∞ if it cannot or must not be mined.</summary>
	float BreakTicks(int x, int y);

	/// <summary>
	/// Can a solid block be placed into this cell as far as its contents go (empty, or a plant that gets cut)?
	/// Adjacency is guaranteed by the moves themselves; player/NPC overlap is handled by the executor's timing.
	/// </summary>
	bool CanPlaceAt(int x, int y);

	/// <summary>Placeable blocks available right now (0 disables placing moves).</summary>
	int BlockCount { get; }

	/// <summary>Ticks for one placement swing.</summary>
	float PlaceTicks { get; }
}

public enum ActionKind
{
	Break,
	Place,
}

/// <summary>A world change the executor performs as part of a step.</summary>
public readonly record struct BlockAction(ActionKind Kind, TilePos Cell)
{
	public override string ToString() => $"{(Kind == ActionKind.Break ? "break" : "place")}{Cell}";
}

/// <summary>Planner-side rules for breaking cells safely, shared by all digging moves.</summary>
public static class BlockRules
{
	/// <summary>
	/// Planner cost (ticks) of breaking <paramref name="cell"/> as part of a move that also breaks
	/// <paramref name="sameMove"/>; +∞ when not allowed. Rules: never open a cell next to lava (it would flood);
	/// never undermine a falling block (sand) unless that block is removed by the same move.
	/// </summary>
	public static float BreakCost(MoveContext ctx, TilePos cell, IReadOnlyCollection<TilePos> sameMove)
	{
		if (Unsafe(ctx.Grid, cell, sameMove))
			return float.PositiveInfinity;

		float ticks = ctx.Blocks.BreakTicks(cell.X, cell.Y);
		if (float.IsInfinity(ticks) || float.IsNaN(ticks))
			return float.PositiveInfinity;
		return ticks * ctx.BreakCostMultiplier + ctx.BreakOverheadTicks;
	}

	/// <summary>Opening this cell would flood lava in or drop an unsupported falling block (not removed by the same move).</summary>
	public static bool Unsafe(ITileGrid g, TilePos cell, IReadOnlyCollection<TilePos> sameMove)
	{
		if (g.Get(cell.X - 1, cell.Y).HasLava() || g.Get(cell.X + 1, cell.Y).HasLava()
			|| g.Get(cell.X, cell.Y - 1).HasLava() || g.Get(cell.X, cell.Y + 1).HasLava())
			return true;
		// Water too: opening a block next to it floods the tunnel (seen in game: a shaft dug down into a water pocket
		// filled up, and the bot drowned in it).
		if (g.Get(cell.X - 1, cell.Y).IsLiquid() || g.Get(cell.X + 1, cell.Y).IsLiquid()
			|| g.Get(cell.X, cell.Y - 1).IsLiquid() || g.Get(cell.X, cell.Y + 1).IsLiquid())
			return true;
		var above = new TilePos(cell.X, cell.Y - 1);
		return g.Get(above.X, above.Y).IsFalling() && !Contains(sameMove, above);
	}

	private static bool Contains(IReadOnlyCollection<TilePos> cells, TilePos c)
	{
		foreach (TilePos p in cells)
			if (p == c)
				return true;
		return false;
	}
}
