using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Walk off a ledge by one column and fall straight down k tiles (1 ≤ k ≤ MaxDrop, below the fall-damage limit).
/// The executor approaches the edge slowly (arrive steering), so the falling body stays inside the destination
/// columns; the swept area is the destination column pair from the start row down to the landing row.
/// k = 1 is vanilla's StepDown (snaps smoothly while walking).
/// </summary>
public sealed class DropMove : IMoveType
{
	public MoveKind Kind => MoveKind.Drop;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		for (int dir = -1; dir <= 1; dir += 2) {
			if (Find(ctx, from, dir, out TilePos to, out int k))
				output.Add(new PathStep(Kind, from, to, Cost(ctx, k, to)));
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step)
	{
		return Find(ctx, step.From, step.Dir, out TilePos to, out _) && to == step.To;
	}

	private static float Cost(MoveContext ctx, int k, TilePos to) =>
		ctx.Adjust(ctx.WalkTileCost + ctx.FallTicks(k) + 1f, to);

	private static bool Find(MoveContext ctx, TilePos from, int dir, out TilePos to, out int k)
	{
		ITileGrid g = ctx.Grid;
		int col = from.X + dir;
		to = default;
		k = 0;
		if (!Body.IsClear(g, col, from.Y) || Body.HasSupport(g, col, from.Y))
			return false; // blocked, or it is a plain walk

		for (k = 1; k <= ctx.MaxDrop; k++) {
			if (!Body.IsClear(g, col, from.Y + k))
				return false;
			if (Body.HasSupport(g, col, from.Y + k)) {
				to = new TilePos(col, from.Y + k);
				return true;
			}
		}
		return false; // too deep to fall safely
	}
}
