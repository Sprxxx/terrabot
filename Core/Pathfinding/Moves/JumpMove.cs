using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Standing and running jumps to any entry in the <see cref="JumpLibrary"/>. For each standable destination the first
/// (cheapest) template variant whose swept cells are free is used.
/// </summary>
public sealed class JumpMove : IMoveType
{
	public MoveKind Kind => MoveKind.Jump;

	[System.ThreadStatic] private static ulong[] standRowsBuffer;
	private static ulong[] standRows => standRowsBuffer ??= new ulong[JumpLibrary.MaxUp + JumpLibrary.MaxDown + 1];

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		// Row masks of the grid, kept for the search (MoveContext.Mask): a template is tested a row at a time.
		if (Body.Bobbing(ctx.Grid, from))
			return; // no take-off while bobbing half over a flooded hole
		GridMask mask = ctx.Mask;
		if (!ctx.InSearch)
			mask.Reset(ctx.Grid, from); // asked outside a search: read afresh
		ITileGrid g = ctx.Grid;
		// Every landing spot's standability at once, a row at a time: bit x = body standable with its left column at
		// left + x (blocked when either column is; held up when either column has a floor).
		int reach = ctx.Jumps.MaxReach;
		int left = from.X - reach;
		int width = 2 * reach + Body.WidthTiles + 1;
		for (int dy = -JumpLibrary.MaxUp; dy <= JumpLibrary.MaxDown; dy++) {
			int y = from.Y + dy;
			ulong blocked = mask.Bits(GridMask.Kind.NotPassable, left, y, width) | mask.Bits(GridMask.Kind.NotHeadroom, left, y - 1, width)
				| mask.Bits(GridMask.Kind.NotHeadroom, left, y - 2, width);
			ulong floor = mask.Bits(GridMask.Kind.StandOn, left, y + 1, width);
			standRows[dy + JumpLibrary.MaxUp] = ~(blocked | blocked >> 1) & (floor | floor >> 1);
		}
		foreach (JumpLibrary.Entry e in ctx.Jumps.Entries) {
			if ((standRows[e.Dy + JumpLibrary.MaxUp] >> (e.Dx + reach) & 1) == 0)
				continue;
			if (Walkable(e, reach))
				continue;
			var to = new TilePos(from.X + e.Dx, from.Y + e.Dy);
			if (LandsInASlope(g, to))
				continue;
			foreach (JumpTemplate v in e.Variants) {
				if (v.Fits(mask, from)) {
					output.Add(new PathStep(Kind, from, to, ctx.Adjust(v.Cost, to), v, Source: this));
					break;
				}
			}
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) =>
		step.Template != null && Body.IsStandable(ctx.Grid, step.To) && step.Template.Fits(ctx.Grid, step.From);

	/// <summary>
	/// A jump at most one row up or down to a spot that walking gets to anyway: every column on the way has somewhere to
	/// stand within a row of the start's (walks, step-ups and one-row drops cover it, and cost less than the jump). The
	/// search never takes such a jump, and generating them was most of the jump move's time on open ground.
	/// </summary>
	private static bool Walkable(JumpLibrary.Entry e, int reach)
	{
		if (e.Dy < -1 || e.Dy > 1)
			return false;
		ulong near = standRows[JumpLibrary.MaxUp - 1] | standRows[JumpLibrary.MaxUp] | standRows[JumpLibrary.MaxUp + 1];
		int dir = System.Math.Sign(e.Dx);
		for (int dx = dir; dx != e.Dx; dx += dir)
			if ((near >> (dx + reach) & 1) == 0)
				return false;
		return true;
	}

	/// <summary>
	/// Feet would end up inside a slope or half block: the templates assume a flat landing, and the slope's surface is
	/// lower or higher than that. Seen in game: two such jumps failed 3 times each. Walking and step-ups handle slopes.
	/// </summary>
	private static bool LandsInASlope(ITileGrid g, TilePos to) => g.Get(to.X, to.Y).IsShaped() || g.Get(to.X + 1, to.Y).IsShaped();
}
