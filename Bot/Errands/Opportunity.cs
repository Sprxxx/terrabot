using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Bot.Errands;

public enum OpportunityKind
{
	Chest,
	LifeCrystal,
}

/// <summary>
/// Optional work seen in the world (docs ERRAND_DESIGN.md section 2): a chest, a life crystal. Offered to the errand that owns the
/// bot with the cost of the detour (ticks, <see cref="DetourTicks"/>); the errand accepts or declines by its policy. No other code
/// path leaves an errand for it.
/// </summary>
public readonly record struct Opportunity(OpportunityKind Kind, TilePos Where, float DetourTicks)
{
	/// <summary>The same thing seen again (the key the brain remembers a declined one by).</summary>
	public string Key => $"{Kind} {Where}";

	/// <summary>
	/// The detour's cost there and back, estimated as the paths' heuristic does (ticks to walk the columns at full speed and to
	/// fall or climb the rows, MoveContext.WalkTileCost; climbing is pillaring or digging up, slower than walking, so a row counts
	/// as <see cref="RowCostTiles"/> columns), plus the rock in the way: the mining time of every solid cell on the straight line
	/// there (once; the way back is dug). A bound, not a search; a policy estimate.
	/// </summary>
	public static float Detour(BotContext ctx, TilePos to)
	{
		MoveContext m = ctx.Moves;
		TilePos from = ctx.Node;
		int dx = Math.Abs(to.X - from.X), dy = Math.Abs(to.Y - from.Y);
		float ticks = 2f * m.WalkTileCost * (dx + RowCostTiles * dy);
		int steps = Math.Max(dx, dy);
		for (int i = 1; i <= steps; i++) {
			int x = from.X + (to.X - from.X) * i / steps, y = from.Y + (to.Y - from.Y) * i / steps;
			CellKind k = ctx.Grid.Get(x, y);
			if (!k.IsSolid())
				continue;
			float t = m.CanBreak ? m.BreakTicks(x, y) : float.PositiveInfinity;
			ticks += float.IsInfinity(t) || float.IsNaN(t) ? UnbreakableTicks : t;
		}
		return ticks;
	}

	public const int RowCostTiles = 4;
	/// <summary>A cell in the way that cannot be mined costs this much (a way round it is a search away): a policy estimate.</summary>
	public const float UnbreakableTicks = 2000f;
}
