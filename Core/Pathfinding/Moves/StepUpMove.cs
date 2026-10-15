using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Walk onto a one-tile-high step. Mirrors Collision.StepUp: while walking on the ground the game lifts the
/// player ≤ 16.1 px when the tile in front at feet level is Solid (not solid-top), the three cells above it are
/// free and the cell above the leading body column is free. All of that equals "destination body clear".
/// Platforms are not auto-stepped (vanilla only does that while holding Up); those need a Jump.
/// </summary>
public sealed class StepUpMove : IMoveType
{
	/// <summary>Small penalty: step-ups cost a little speed and are slightly less reliable than flat walking.</summary>
	public const float Penalty = 2f;

	public MoveKind Kind => MoveKind.StepUp;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		for (int dir = -1; dir <= 1; dir += 2) {
			if (Check(ctx.Grid, from, dir, out TilePos to))
				output.Add(new PathStep(Kind, from, to, ctx.Adjust(ctx.WalkTileCost + Penalty, to), Source: this));
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) => Check(ctx.Grid, step.From, step.Dir, out _);

	private static bool Check(ITileGrid g, TilePos from, int dir, out TilePos to)
	{
		to = new TilePos(from.X + dir, from.Y - 1);
		int frontColumn = dir > 0 ? from.X + Body.WidthTiles : from.X - 1;
		// Right above the step it must be fully open: a slope or half block on top of the step block makes it more than the
		// one tile vanilla lifts the player (seen in game, world_eye run 51: pressing right into it, three times).
		// A half block in front lifts the walking body too (Collision.StepUp), onto the node a row up.
		CellKind front = g.Get(frontColumn, from.Y);
		return (front.IsSolid() || front.IsHalfBlock()) && g.Get(frontColumn, from.Y - 1).IsHeadroom() && Body.IsClear(g, to);
	}
}
