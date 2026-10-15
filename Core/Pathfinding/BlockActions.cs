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
		bool unsafeCell = Unsafe(ctx.Grid, cell, sameMove);
		// Boxed in (MoveContext.DigAnyway): sand and water may be dug beside after all, at a price; lava never.
		if (unsafeCell && (!ctx.DigAnyway || NextToLava(ctx.Grid, cell)))
			return float.PositiveInfinity;

		float ticks = ctx.BreakTicks(cell.X, cell.Y);
		if (float.IsInfinity(ticks) || float.IsNaN(ticks))
			return float.PositiveInfinity;
		return ticks * ctx.BreakCostMultiplier + ctx.BreakOverheadTicks + (unsafeCell ? DigAnywayTicks : 0f)
			+ (NextToWater(ctx.Grid, cell) ? WetBreakTicks : 0f);
	}

	/// <summary>
	/// Extra cost of opening a cell beside water or honey: allowed (it flows in, the bot swims, surfaces for breath), but a
	/// dry way is still taken when there is one. It used to be forbidden, so ore and a life crystal beside water were
	/// never mined, and the bot ran past the crystal (user report, 2026-10-14: solve the problem, don't ban the place).
	/// </summary>
	public const float WetBreakTicks = 40f;

	public static bool NextToWater(ITileGrid g, TilePos cell) =>
		IsWater(g.Get(cell.X - 1, cell.Y)) || IsWater(g.Get(cell.X + 1, cell.Y)) || IsWater(g.Get(cell.X, cell.Y - 1)) || IsWater(g.Get(cell.X, cell.Y + 1));

	private static bool IsWater(CellKind k) => k.IsLiquid() && !k.HasLava();

	/// <summary>Extra cost of an unsafe break when digging anyway: only when there is nothing else.</summary>
	public const float DigAnywayTicks = 300f;

	/// <summary>
	/// Lava beside or above the cell, which would flow into it once opened. Not below: liquid never flows up, so a block
	/// over lava is opened safely (Baritone's rule, "always okay to break the block above liquid"); it used to count, and
	/// ore and crystals over lava were left alone. Lava beside or above still waits for a player's answer to it (a water
	/// bucket); vanilla refuses solid blocks placed into lava (Player.PlaceThing_Tiles_CheckLavaBlocking).
	/// </summary>
	public static bool NextToLava(ITileGrid g, TilePos cell) =>
		g.Get(cell.X - 1, cell.Y).HasLava() || g.Get(cell.X + 1, cell.Y).HasLava() || g.Get(cell.X, cell.Y - 1).HasLava();

	/// <summary>Opening this cell would flood lava in or drop an unsupported falling block (not removed by the same move).</summary>
	public static bool Unsafe(ITileGrid g, TilePos cell, IReadOnlyCollection<TilePos> sameMove)
	{
		if (NextToLava(g, cell))
			return true;
		// Not water: opening a block beside it floods the tunnel, and the bot deals with that (surfaces for breath; once a
		// shaft into a water pocket drowned it, before surfacing existed). It costs more instead (WetBreakTicks).
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
