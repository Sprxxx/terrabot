using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Fall through the platform(s) under the feet by holding Down (vanilla: DryCollision passes fallThrough =
/// controlDown, and TileCollision ignores solid-top tiles while falling slower than 1 px/tick). Only when the
/// body stands on platforms alone; a solid support cell would hold it. The body falls straight down its own
/// columns to the first floor below, within the safe drop.
/// </summary>
public sealed class DropThroughMove : IMoveType
{
	public MoveKind Kind => MoveKind.DropThrough;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (Find(ctx, from, out TilePos to, out int k))
			output.Add(new PathStep(Kind, from, to, ctx.Adjust(ctx.FallTicks(k) + 4f, to), Source: this));
	}

	public bool IsValid(MoveContext ctx, in PathStep step) => Find(ctx, step.From, out TilePos to, out _) && to == step.To;

	private static bool Find(MoveContext ctx, TilePos from, out TilePos to, out int k)
	{
		ITileGrid g = ctx.Grid;
		to = default;
		k = 0;
		CellKind a = g.Get(from.X, from.Y + 1), b = g.Get(from.X + 1, from.Y + 1);
		// Only real platforms: a slope or half block is a block (seen in game: 14 re-plans holding Down on a slope).
		if (a.IsSolid() || b.IsSolid() || a.IsShaped() || b.IsShaped() || !(a.IsPlatform() || b.IsPlatform()))
			return false;
		// The platform row itself is passable; the first floor below it (rows > from.Y + 1) ends the fall.
		for (k = 1; k <= ctx.MaxDrop; k++) {
			int feet = from.Y + k;
			if (!Body.IsClear(g, from.X, feet))
				return false;
			if (Body.HasSupport(g, from.X, feet)) {
				to = new TilePos(from.X, feet);
				return true;
			}
		}
		return false;
	}
}
