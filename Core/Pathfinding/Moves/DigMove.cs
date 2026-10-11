using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Moves that mine blocks out of the way, then reuse an ordinary movement (Baritone's breaking traverse,
/// ascend, descend and downward moves):
/// <list type="bullet">
/// <item>Tunnel: Walk ±1 after breaking solid cells of the destination body.</item>
/// <item>TunnelUp: StepUp ±1/−1 onto a solid step after clearing the cells above it.</item>
/// <item>TunnelDown: one-tile Drop ±1/+1 after clearing the destination body.</item>
/// <item>DigDown: mine the two cells under the feet and drop one row.</item>
/// </list>
/// Each requires the destination to be standable once the listed cells are gone, and every break to be
/// allowed by <see cref="BlockRules.BreakCost"/>.
/// </summary>
public sealed class DigMove : IMoveType
{
	private enum Variant { Tunnel, TunnelUp, TunnelDown, DigDown }

	/// <summary>Family marker only; steps carry their real movement kind.</summary>
	public MoveKind Kind => MoveKind.Walk;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (!ctx.CanBreak)
			return;
		for (int dir = -1; dir <= 1; dir += 2) {
			Add(ctx, from, Variant.Tunnel, dir, output);
			Add(ctx, from, Variant.TunnelUp, dir, output);
			Add(ctx, from, Variant.TunnelDown, dir, output);
		}
		Add(ctx, from, Variant.DigDown, 0, output);
	}

	public bool IsValid(MoveContext ctx, in PathStep step)
	{
		if (!ctx.CanBreak)
			return false;
		Variant v = step.Kind switch {
			MoveKind.StepUp => Variant.TunnelUp,
			MoveKind.Drop => step.Dir == 0 ? Variant.DigDown : Variant.TunnelDown,
			_ => Variant.Tunnel,
		};
		// Lenient: cells already mined since planning are fine (the step simply has less to do).
		return TryBuild(ctx, step.From, v, step.Dir, requireBreak: false, out PathStep s) && s.To == step.To;
	}

	private void Add(MoveContext ctx, TilePos from, Variant v, int dir, List<PathStep> output)
	{
		if (TryBuild(ctx, from, v, dir, requireBreak: true, out PathStep step))
			output.Add(step);
	}

	private bool TryBuild(MoveContext ctx, TilePos from, Variant v, int dir, bool requireBreak, out PathStep step)
	{
		ITileGrid g = ctx.Grid;
		step = default;
		TilePos to;
		MoveKind kind;
		float baseCost;
		var cells = new List<TilePos>(6);

		switch (v) {
			case Variant.Tunnel:
				to = new TilePos(from.X + dir, from.Y);
				if (!Body.HasSupport(g, to.X, to.Y))
					return false;
				kind = MoveKind.Walk;
				baseCost = ctx.WalkTileCost;
				AddBody(cells, to);
				break;
			case Variant.TunnelUp: {
				to = new TilePos(from.X + dir, from.Y - 1);
				int front = dir > 0 ? from.X + Body.WidthTiles : from.X - 1;
				if (!g.Get(front, from.Y).IsSolid()) // the step we climb onto stays
					return false;
				kind = MoveKind.StepUp;
				baseCost = ctx.WalkTileCost + StepUpMove.Penalty;
				AddBody(cells, to);
				break;
			}
			case Variant.TunnelDown:
				to = new TilePos(from.X + dir, from.Y + 1);
				if (!Body.HasSupport(g, to.X, to.Y))
					return false;
				kind = MoveKind.Drop;
				baseCost = ctx.WalkTileCost + ctx.FallTicks(1) + 1f;
				AddBody(cells, to);
				break;
			default: { // DigDown
				if (!FindDigLanding(ctx, from, out int k))
					return false;
				to = new TilePos(from.X, from.Y + k);
				kind = MoveKind.Drop;
				baseCost = ctx.FallTicks(k) + 2f;
				cells.Add(new TilePos(from.X, from.Y + 1));
				cells.Add(new TilePos(from.X + 1, from.Y + 1));
				break;
			}
		}

		var breaks = new List<TilePos>(cells.Count);
		bool dugEarlier = false;
		foreach (TilePos c in cells) {
			CellKind k = g.Get(c.X, c.Y);
			if (InBody(c, from)) {
				// We are standing in it, so it is clear: dug by an earlier step of this plan if the grid says solid.
				dugEarlier |= k.IsSolid();
				continue;
			}
			if (k.HasLava())
				return false;
			if (k.IsSolid() || v == Variant.DigDown && k.IsShaped())
				breaks.Add(c); // digging down through a slope or half block mines it like any block
			else if (v == Variant.DigDown && k.IsPlatform())
				return false; // standing on a platform: would need Down to fall through (not yet supported)
		}
		// No breaks and nothing dug earlier = plain movement, which the ordinary move types already cover.
		// No breaks but cells dug earlier = a plain move the planner's (unedited) grid would wrongly reject.
		if (requireBreak && breaks.Count == 0 && !dugEarlier)
			return false;

		float cost = baseCost;
		foreach (TilePos c in breaks) {
			float b = BlockRules.BreakCost(ctx, c, breaks);
			if (float.IsPositiveInfinity(b))
				return false;
			cost += b;
		}

		step = new PathStep(kind, from, to, ctx.Adjust(cost, to), Actions: Order(breaks, from), Source: this);
		return true;
	}

	/// <summary>
	/// The planner does not track its own edits, so cells inside the current body count as clear, which is the
	/// only way chained digging (a shaft, a long tunnel) can be planned at all.
	/// </summary>
	private static bool InBody(TilePos c, TilePos node) =>
		c.X >= node.X && c.X < node.X + Body.WidthTiles && c.Y > node.Y - Body.HeightTiles && c.Y <= node.Y;

	/// <summary>
	/// After mining the two cells under the feet, how far does the body fall? Rows of the current body and the
	/// mined row count as clear; below that the live grid decides. Landing must be within the safe drop.
	/// </summary>
	private static bool FindDigLanding(MoveContext ctx, TilePos from, out int k)
	{
		ITileGrid g = ctx.Grid;
		for (k = 1; k <= ctx.MaxDrop; k++) {
			int feet = from.Y + k;
			for (int y = feet - Body.HeightTiles + 1; y <= feet; y++) {
				if (y <= from.Y + 1)
					continue; // current body rows and the row being mined
				if (!g.Get(from.X, y).IsPassable() || !g.Get(from.X + 1, y).IsPassable())
					return false;
			}
			if (g.Get(from.X, feet + 1).CanStandOn() || g.Get(from.X + 1, feet + 1).CanStandOn())
				return feet + 1 > from.Y + 1; // never "land" on the row we are mining
		}
		return false;
	}

	private static void AddBody(List<TilePos> cells, TilePos node)
	{
		for (int dy = -Body.HeightTiles + 1; dy <= 0; dy++)
			for (int dx = 0; dx < Body.WidthTiles; dx++)
				cells.Add(new TilePos(node.X + dx, node.Y + dy));
	}

	/// <summary>Top to bottom, and anything under the current feet last so the bot keeps its footing while mining.</summary>
	private static BlockAction[] Order(List<TilePos> breaks, TilePos from)
	{
		breaks.Sort((a, b) => {
			bool au = a.Y == from.Y + 1 && (a.X == from.X || a.X == from.X + 1);
			bool bu = b.Y == from.Y + 1 && (b.X == from.X || b.X == from.X + 1);
			if (au != bu)
				return au ? 1 : -1;
			return a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
		});
		var actions = new BlockAction[breaks.Count];
		for (int i = 0; i < breaks.Count; i++)
			actions[i] = new BlockAction(ActionKind.Break, breaks[i]);
		return actions;
	}
}
