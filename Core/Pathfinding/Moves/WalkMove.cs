using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>Walk one tile left/right on the same feet row. The swept area is exactly From ∪ To bodies.</summary>
public sealed class WalkMove : IMoveType
{
	public MoveKind Kind => MoveKind.Walk;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		for (int dir = -1; dir <= 1; dir += 2) {
			var to = new TilePos(from.X + dir, from.Y);
			if (Body.IsStandable(ctx.Grid, to))
				output.Add(new PathStep(Kind, from, to, ctx.Adjust(ctx.WalkTileCost, to), Source: this));
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) => Body.IsStandable(ctx.Grid, step.To);
}
