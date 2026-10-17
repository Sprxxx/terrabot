using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Down a shaft deeper than a safe fall, as a player goes down a corruption chasm with blocks in the hotbar: walk off the lip
/// by one column (as <see cref="DropMove"/>), fall straight down, and place a block under the feet on the way down, landing on
/// it k rows lower (k ≤ <see cref="MoveContext.MaxDrop"/>, so the fall never hurts). Only where a plain drop finds nothing to
/// land on within a safe fall: everywhere else <see cref="DropMove"/> has it. The block must be one vanilla lets go into open
/// air: a solid tile on a 4-neighbour, or a wall in or beside the cell (<see cref="BlockRules.Fixable"/>); a corruption chasm
/// has its ebonstone wall behind it all the way down (WorldGen.ChasmRunner, decompiled WorldGen.cs 70191-70447, sets wall 3
/// over its carve below its first rows).
/// <para>The block goes under the trailing body column (the one over the side the step came from), so the next step on in
/// the same direction walks off it again; a step back over it is no fall (the search knows the blocks its path placed:
/// <see cref="MoveContext.PlacedOnPath"/>).</para>
/// <para>Seen in game (measured run U30, world_eye): with the Suspicious Looking Eye's lenses in at d6 +18h, the walks to the
/// DemonAltar at (1748,411) ended "no path" for 117,000 ticks at the corruption chasm's lip (1779,341): the shaft below it
/// open 55 rows, every 40,000-node search "start h 112, lowest h 112". The bot got down only when an eater of souls knocked
/// it in (t671000) and the fall arrest placed a block at (1776,370), mid-shaft, 28 rows down; the altar was reached from the
/// shaft's foot.</para>
/// </summary>
public sealed class ArrestedDropMove : IMoveType
{
	public MoveKind Kind => MoveKind.ArrestedDrop;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (!ctx.CanPlace)
			return;
		ITileGrid g = ctx.Grid;
		for (int dir = -1; dir <= 1; dir += 2) {
			int col = from.X + dir;
			if (!Body.IsClear(g, col, from.Y) || HasSupport(ctx, col, from.Y))
				continue; // blocked, or a plain walk (onto the ground, or a block this path placed)
			int deepest = DeepestOpen(ctx, col, from.Y);
			if (deepest < 0)
				continue; // ground within a safe fall: a plain drop
			for (int k = 1; k <= deepest; k++)
				if (Catch(ctx, col, from.Y + k, dir) is TilePos cell) {
					var to = new TilePos(col, from.Y + k);
					output.Add(new PathStep(Kind, from, to, Cost(ctx, k, to), Actions: new[] { new BlockAction(ActionKind.Place, cell) }, Source: this));
				}
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step)
	{
		if (!step.HasActions || step.Dir == 0)
			return false;
		ITileGrid g = ctx.Grid;
		TilePos cell = step.Actions[0].Cell;
		int col = step.To.X, k = step.To.Y - step.From.Y;
		if (k < 1 || k > ctx.MaxDrop || !Body.IsClear(g, col, step.From.Y) || Body.HasSupport(g, col, step.From.Y))
			return false;
		for (int j = 1; j <= k; j++) {
			if (!Body.IsClear(g, col, step.From.Y + j))
				return false;
			if (j < k && Body.HasSupport(g, col, step.From.Y + j))
				return false; // something to land on above the catch now: the fall would stop there
		}
		CellKind c = g.Get(cell.X, cell.Y);
		return c.CanStandOn() || ctx.CanPlace && Placeable(ctx, cell);
	}

	/// <summary>
	/// The deepest landing row offset (≤ <see cref="MoveContext.MaxDrop"/>) the body can fall to in columns
	/// <paramref name="col"/>..+1 from feet row <paramref name="y"/>, if nothing is there to land on within a safe fall
	/// (else -1: a plain drop, or a fall into a closed hole).
	/// </summary>
	private static int DeepestOpen(MoveContext ctx, int col, int y)
	{
		ITileGrid g = ctx.Grid;
		for (int k = 1; k <= ctx.MaxDrop; k++) {
			if (!Body.IsClear(g, col, y + k))
				return -1; // stopped by what is in the way (it is ground to the row above it: a plain drop's)
			if (HasSupport(ctx, col, y + k))
				return -1;
		}
		return ctx.MaxDrop;
	}

	/// <summary>The cell under the landing node (feet row <paramref name="y"/>) for the block: the trailing column first.</summary>
	private static TilePos? Catch(MoveContext ctx, int col, int y, int dir)
	{
		var trailing = new TilePos(dir < 0 ? col + 1 : col, y + 1);
		var leading = new TilePos(dir < 0 ? col : col + 1, y + 1);
		if (Placeable(ctx, trailing))
			return trailing;
		return Placeable(ctx, leading) ? leading : null;
	}

	private static bool Placeable(MoveContext ctx, TilePos cell)
	{
		CellKind k = ctx.Grid.Get(cell.X, cell.Y);
		return !k.CanStandOn() && !k.HasLava() && ctx.CanPlaceAt(cell.X, cell.Y) && BlockRules.Fixable(ctx, cell);
	}

	/// <summary>Support under the body: the grid's, or a block an earlier step of the path being searched places.</summary>
	private static bool HasSupport(MoveContext ctx, int x, int y) => ctx.HasSupport(x, y);

	/// <summary>
	/// A drop's (walk off, fall k rows) plus the swing and the block, plus <see cref="MoveContext.PrecariousTicks"/>: the body
	/// lands on one column over the shaft (<see cref="Body.IsPrecarious"/>'s footing, which the grid cannot see here: the
	/// block is not in it).
	/// </summary>
	private static float Cost(MoveContext ctx, int k, TilePos to) =>
		ctx.Adjust(ctx.WalkTileCost + ctx.FallTicks(k) + 1f + ctx.Blocks.PlaceTicks + ctx.PlacePenaltyTicks + ctx.PrecariousTicks, to);
}
