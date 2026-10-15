using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Moves that place blocks:
/// <list type="bullet">
/// <item>Bridge: the destination ±1 has no floor. Place a block under the leading body column (it touches the
/// floor we stand on, which satisfies vanilla's adjacency rule), then walk onto it. Chains across gaps.</item>
/// <item>Pillar: jump, place a block under the feet once the hitbox has cleared the cell (vanilla refuses to
/// place into a player's hitbox), land on it one row higher. Chains straight up.</item>
/// </list>
/// </summary>
public sealed class BuildMove : IMoveType
{
	/// <summary>Extra rise above the cell top before placing, so the hitbox is clear despite int truncation.</summary>
	public const float PillarClearance = 2f;

	/// <summary>Family marker only.</summary>
	public MoveKind Kind => MoveKind.Pillar;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (!ctx.CanPlace)
			return;
		for (int dir = -1; dir <= 1; dir += 2)
			if (TryBridge(ctx, from, dir, out PathStep s))
				output.Add(s);
		if (TryPillar(ctx, from, out PathStep p))
			output.Add(p);
	}

	public bool IsValid(MoveContext ctx, in PathStep step)
	{
		if (step.Kind == MoveKind.Pillar) {
			// Fine if the block is already under us, otherwise we must still be able to place one.
			ITileGrid g = ctx.Grid;
			bool placed = g.Get(step.From.X, step.From.Y).IsSolid() || g.Get(step.From.X + 1, step.From.Y).IsSolid();
			TilePos pillarCell = PillarCell(g, step.From);
			bool headroom = PillarHeadroomClear(g, step.From) || ctx.CanBreak && step.CountActions(ActionKind.Break) > 0;
			return headroom && (placed || (ctx.CanPlace && ctx.CanPlaceAt(pillarCell.X, pillarCell.Y)));
		}
		// Bridge: fine if the block is already there (placed by us) or can still be placed.
		TilePos cell = BridgeCell(step.From, step.Dir);
		return Body.IsClear(ctx.Grid, step.To)
			&& (ctx.Grid.Get(cell.X, cell.Y).CanStandOn() || (ctx.CanPlace && ctx.CanPlaceAt(cell.X, cell.Y)));
	}

	/// <summary>The cell under the leading body column when bridging in direction <paramref name="dir"/>.</summary>
	public static TilePos BridgeCell(TilePos from, int dir) =>
		dir > 0 ? new TilePos(from.X + 1, from.Y + 1) : new TilePos(from.X, from.Y + 1);

	/// <summary>
	/// Column for a pillar block under node <paramref name="from"/>: above a cell that can carry it (vanilla
	/// needs a solid neighbour). Decided again at execution time from the live world.
	/// </summary>
	public static TilePos PillarCell(ITileGrid g, TilePos from) =>
		g.Get(from.X, from.Y + 1).CanStandOn() || !g.Get(from.X + 1, from.Y + 1).CanStandOn()
			? new TilePos(from.X, from.Y)
			: new TilePos(from.X + 1, from.Y);

	/// <summary>Shortest jump hold whose apex clears one tile plus <see cref="PillarClearance"/> with margin.</summary>
	public static int PillarHoldTicks(PhysicsParams p)
	{
		for (int h = 1; h <= p.JumpHeight + 1; h++)
			if (PlayerPhysics.ApexRise(p, h) >= Body.TileSize + PillarClearance + 6f)
				return h;
		return p.JumpHeight + 1;
	}

	private bool TryBridge(MoveContext ctx, TilePos from, int dir, out PathStep step)
	{
		ITileGrid g = ctx.Grid;
		step = default;
		var to = new TilePos(from.X + dir, from.Y);
		if (!Body.IsClear(g, to) || Body.HasSupport(g, to.X, to.Y))
			return false; // blocked, or a plain walk
		TilePos cell = BridgeCell(from, dir);
		CellKind k = g.Get(cell.X, cell.Y);
		if (k.CanStandOn() || k.HasLava() || !ctx.CanPlaceAt(cell.X, cell.Y))
			return false;
		float cost = ctx.WalkTileCost + ctx.Blocks.PlaceTicks + ctx.PlacePenaltyTicks;
		step = new PathStep(MoveKind.Walk, from, to, ctx.Adjust(cost, to),
			Actions: new[] { new BlockAction(ActionKind.Place, cell) }, Source: this);
		return true;
	}

	private bool TryPillar(MoveContext ctx, TilePos from, out PathStep step)
	{
		ITileGrid g = ctx.Grid;
		step = default;
		// The row above the head: free, or (digging up) blocks to mine first from where the bot stands. Seen in game
		// (world_eye run 49): a pocket whose only way on was straight up through its ceiling had no move out at all.
		var breaks = new List<TilePos>(2);
		foreach (TilePos h in new[] { new TilePos(from.X, from.Y - Body.HeightTiles), new TilePos(from.X + 1, from.Y - Body.HeightTiles) }) {
			CellKind hk = g.Get(h.X, h.Y);
			if (hk.IsHeadroom())
				continue;
			if (!ctx.CanBreak || hk.HasLava() || !hk.IsBlockTile())
				return false;
			breaks.Add(h);
		}
		TilePos cell = PillarCell(g, from);
		CellKind k = g.Get(cell.X, cell.Y);
		// The block goes into the feet cell, inside the body: solid on the (unedited) grid only if an earlier step of the
		// plan mined it (digging up a shaft), so then it is open by the time this step runs (as DigMove's InBody).
		bool dugEarlier = k.IsSolid();
		if (!dugEarlier && (k.CanStandOn() || k.HasLava() || !ctx.CanPlaceAt(cell.X, cell.Y)))
			return false;
		var to = new TilePos(from.X, from.Y - 1);
		float cost = ctx.PillarAirTicks + ctx.Blocks.PlaceTicks + ctx.PlacePenaltyTicks + ctx.PillarRiskTicks;
		foreach (TilePos b in breaks) {
			float bc = BlockRules.BreakCost(ctx, b, breaks);
			if (float.IsPositiveInfinity(bc))
				return false;
			cost += bc;
		}
		var actions = new List<BlockAction>(3);
		foreach (TilePos b in breaks)
			actions.Add(new BlockAction(ActionKind.Break, b));
		actions.Add(new BlockAction(ActionKind.Place, cell));
		step = new PathStep(MoveKind.Pillar, from, to, ctx.Adjust(cost, to), Actions: actions.ToArray(), Source: this);
		return true;
	}

	/// <summary>The row above the head must be free (the body ends one row higher).</summary>
	private static bool PillarHeadroomClear(ITileGrid g, TilePos from) =>
		g.Get(from.X, from.Y - Body.HeightTiles).IsHeadroom() && g.Get(from.X + 1, from.Y - Body.HeightTiles).IsHeadroom();
}
