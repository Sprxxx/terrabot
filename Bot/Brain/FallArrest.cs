using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Pathfinding.Moves;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// The one owner of a fall that would hurt: a reflex the brain runs after whatever skill has the keys. Paths never plan such a
/// fall (their drops stop at <see cref="MoveContext.MaxDrop"/>, two rows short of a safe fall), so one in progress is always
/// an accident: a hit's knockback over a lip, a walk's jump gone wrong. A player then steers to a ledge, or places a block
/// under the feet before the fall is too long; so does the bot:
/// <list type="number">
/// <item>where does the fall end if the keys stay as the skill set them (<see cref="Predict"/>)? On ground within a safe fall
/// of where it began, or in water (vanilla resets the fall in liquid): nothing to do;</item>
/// <item>else, if holding left, right or neither lands within a safe fall: that key (to the ledge);</item>
/// <item>else a block from the hotbar into the first cell under the body that vanilla lets it go into (a solid tile or a wall
/// beside it, within reach), and land on it. None yet: steer to the nearer wall, where there will be one.</item>
/// </list>
/// Vanilla (decompiled Player.cs): the fall counts from <c>fallStart</c>, the hitbox's top row while velocity.Y is 0 or a jump is
/// held (24331-24395); landing more than 25 rows (+ extraFall, <see cref="PhysicsParams.SafeFallTiles"/>) below it hurts 10 a
/// row. A block goes into an empty cell with a solid tile or a wall on a 4-neighbour, or a wall in the cell
/// (PlaceThing_Tiles_BlockPlacementForAssortedThings, 40494-40515), up to tileRangeY 4 rows past the hitbox's bottom less 2
/// (PlaceThing_Tiles, 38999), and not into the hitbox; item use comes after the tick's movement (ItemCheckWrapped after
/// DryCollision, 26983-27062).
/// Seen in game (measured run M10d, world_eow): thrown off a pillar top at (1703,267) by a Devourer ("knockback 4.5 dir -1"),
/// the bot fell down the shaft at x 1700-1701, 6.4 px east of a ledge at (1699,276), to (1701,361): 94 rows, 685 damage, dead;
/// it carried 1502 dirt blocks, and the fight retargeted the worm all the way down.
/// </summary>
public sealed class FallArrest
{
	/// <summary>Vanilla Player.tileRangeX / tileRangeY (Player.cs:2859-2862): how far from the hitbox a block may be placed.</summary>
	public const int TileRangeX = 5, TileRangeY = 4;

	/// <summary>
	/// Ticks a prediction looks ahead. A loop guard only: every prediction ends sooner, on landing or once the hitbox is more than
	/// a safe fall below the fall's start (from rest, 26 rows take 41 ticks; a hit's -3.5 px a tick adds 9 rising).
	/// </summary>
	public const int Horizon = 600;

	private int fallStart = int.MinValue;
	private long lastTick = long.MinValue;
	private bool active;
	private string mode = "";
	private TilePos? placing;

	/// <summary>The hitbox's top row the fall counts from (vanilla Player.fallStart).</summary>
	public int FallStart => fallStart;
	/// <summary>Steering or placing to stop a fall this tick.</summary>
	public bool Active => active;
	public string State => active ? mode : "";

	/// <summary>Where a fall ends, as far as the bot can tell.</summary>
	public readonly record struct Landing(bool Safe, int Rows, TilePos Where, string Why);

	public void Tick(BotContext ctx)
	{
		PhysicsParams p = ctx.Physics;
		BodyState s = ctx.Body;
		if (ctx.Grid == null || ctx.BodyIssue != null) { // dead, or the body not the bot's to move
			Stop();
			return;
		}
		Track(ctx, s);
		if (s.Grounded) {
			if (arrested)
				ctx.Log.Info($"Fall: landed at {ctx.Node}, {(int)(s.Y / Body.TileSize) - arrestFrom} rows below where the fall began (row {arrestFrom})");
			arrested = false;
			Stop();
			return;
		}
		Landing now = Predict(ctx.Grid, p, s, ctx.Output, fallStart);
		if (now.Safe || PlannedFallHolds(ctx, s)) {
			Stop();
			return;
		}
		// A key that lands it within a safe fall: a ledge within reach of the drift.
		Landing best = default;
		int bestKey = 0;
		bool found = false;
		foreach (int key in new[] { ctx.Output.Horizontal, -1, 1, 0 }) {
			MoveInput k = ctx.Output;
			k.Horizontal = key;
			Landing l = Predict(ctx.Grid, p, s, k, fallStart);
			if (l.Safe && (!found || l.Rows < best.Rows)) {
				best = l;
				bestKey = key;
				found = true;
			}
		}
		if (found) {
			ctx.Output.Horizontal = bestKey;
			Begin(ctx, now, Kind.Steer, $"steering {(bestKey < 0 ? "left" : bestKey > 0 ? "right" : "straight")} to land at {best.Where}, {best.Rows} rows down");
			return;
		}
		Place(ctx, s, now);
	}

	/// <summary>
	/// A fall the walk planned: the step in the air lands at <see cref="BotContext.PlannedLanding"/> (a jump's or a drop's
	/// landing node, an ArrestedDrop's, with its block <see cref="BotContext.PlannedCatch"/> taken as in), and the body steered
	/// there as the step steers it (towards the node's column, tick by tick) comes down within a safe fall: the step's fall, not
	/// the reflex's. The keys of this one tick held all the way down are not the step's: a jump presses towards its landing and
	/// lets go to land on it, and an ArrestedDrop's catch is not placed yet. Seen in game (self-test chasm_arrested_descent,
	/// 2026-10-17): "Jump (2052,167)->(2058,171)" from the arena wall's top down onto the chasm's lip, its first tick's right key
	/// held on carrying it over the shaft ("nothing to land on within a safe fall: ground at (2065,213), 46 rows down"), and the
	/// reflex took the keys ("steering straight"); the jump landed on the lip all the same. Not once the step has let the fall
	/// go (an ArrestedDrop's catch passed): then the reflex as for any fall.
	/// </summary>
	private bool PlannedFallHolds(BotContext ctx, in BodyState s)
	{
		if (ctx.PlannedLanding is not TilePos land)
			return false;
		ITileGrid g = ctx.Grid;
		if (ctx.PlannedCatch is TilePos c && !g.Get(c.X, c.Y).CanStandOn()) {
			caught.Reset(g);
			caught.Set(c.X, c.Y, CellKind.Solid);
			g = caught;
		}
		return Predict(g, ctx.Physics, s, ctx.Output, fallStart, Body.CanonicalLeft(land.X, ctx.Physics)).Safe;
	}

	private readonly OverlayGrid caught = new();

	/// <summary>The techniques, one at a time; a change of technique is logged.</summary>
	private enum Kind { None, Steer, Place, Hand, NoBlock }
	private Kind kind;
	/// <summary>The fall's start when the arrest began (fallStart is reset on landing, before the landing is logged).</summary>
	private int arrestFrom;
	/// <summary>This fall was steered or blocked at some tick (until it lands).</summary>
	private bool arrested;

	/// <summary>Vanilla's fallStart: the top row while standing (velocity.Y 0), jumping (jump > 0) or in liquid.</summary>
	private void Track(BotContext ctx, in BodyState s)
	{
		int row = (int)(s.Y / Body.TileSize);
		if (ctx.Tick != lastTick + 1 || fallStart == int.MinValue || s.VY == 0f || s.Jump > 0 || Body.InLiquid(ctx.Grid, Body.NodeOf(s, ctx.Physics)))
			fallStart = row;
		lastTick = ctx.Tick;
	}

	private void Begin(BotContext ctx, in Landing now, Kind k, string how)
	{
		if (!arrested)
			arrestFrom = fallStart;
		arrested = true;
		if (!active || k != kind)
			ctx.Log.Info($"Fall: from row {fallStart} at {ctx.Node} (vel {ctx.Body.VX:0.##},{ctx.Body.VY:0.##}), {now.Why}; {how}");
		active = true;
		kind = k;
		mode = how;
	}

	private void Stop()
	{
		active = false;
		kind = Kind.None;
		mode = "";
		placing = null;
	}

	/// <summary>
	/// A block under the feet: into the first cell below the hitbox (after this tick's movement) that it may go into, in one of
	/// the hitbox's columns; none: the block in hand, steering to the nearer wall within <see cref="TileRangeX"/>.
	/// </summary>
	private void Place(BotContext ctx, in BodyState s, in Landing now)
	{
		PhysicsParams p = ctx.Physics;
		ITileGrid g = ctx.Grid;
		if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0) {
			Begin(ctx, now, Kind.NoBlock, "no block to place under the feet");
			return;
		}
		bool heal = ctx.Output.QuickHeal;
		ctx.Output = new MoveInput { Tool = ToolRequest.Block, QuickHeal = heal };
		BodyState next = s;
		PlayerPhysics.ApplyControls(ref next, ctx.Output, p);
		next.X += next.VX;
		next.Y += next.VY;
		float bottom = MathF.Floor(next.Y) + p.Height;
		int c0 = Floor(next.X / Body.TileSize), c1 = Floor((next.X + p.Width - 0.001f) / Body.TileSize);
		int rFirst = (int)MathF.Ceiling((bottom + BuildMove.PillarClearance) / Body.TileSize);
		int rLast = Floor((next.Y + p.Height) / Body.TileSize + TileRangeY - 2);
		// The column under more of the hitbox first: the body lands on what is under it.
		float mid = next.X + p.Width * 0.5f;
		int first = Floor(mid / Body.TileSize), other = first == c0 ? c1 : c0;
		for (int r = rFirst; r <= rLast; r++)
			foreach (int c in first == other ? new[] { first } : new[] { first, other }) {
				if (!CanPlace(ctx, c, r))
					continue;
				var cell = new TilePos(c, r);
				ctx.Output.UseItem = true;
				ctx.Output.Aim(c, r);
				ctx.Output.Horizontal = Arrive.Steer(s.X, s.VX, c * Body.TileSize + Body.TileSize * 0.5f - p.Width * 0.5f, p, Body.TileSize * 0.5f);
				Begin(ctx, now, Kind.Place, $"placing a block at {cell} under the feet");
				if (placing != cell) {
					placing = cell;
					int rows = (int)((r * Body.TileSize - p.Height) / (float)Body.TileSize) - fallStart;
					ctx.Log.Info($"Fall: placing a block at {cell} to land on, {rows} rows below where the fall began");
				}
				return;
			}
		// Nothing to fix one to under the body yet: towards the nearer wall (beside the rows the body is about to pass).
		int dir = 0, bestK = int.MaxValue;
		for (int side = -1; side <= 1; side += 2)
			for (int k = 1; k <= TileRangeX && k < bestK; k++) {
				int x = side < 0 ? c0 - k : c1 + k;
				bool wall = false;
				for (int r = Floor(next.Y / Body.TileSize); r <= rLast && !wall; r++)
					wall = g.Get(x, r).IsSolid() || ctx.World?.HasWall(x, r) == true;
				if (wall) {
					bestK = k;
					dir = side;
				}
			}
		ctx.Output.Horizontal = dir;
		Begin(ctx, now, Kind.Hand, dir != 0 ? $"a block in hand, steering {(dir < 0 ? "left" : "right")} to the wall {bestK} column(s) off" : "a block in hand, no wall within reach");
	}

	/// <summary>
	/// Vanilla's rule for a block: an empty cell (no lava: PlaceThing_Tiles_CheckLavaBlocking) with a solid tile on a 4-neighbour,
	/// or a wall in it or beside it; what the planner may place into (<see cref="IBlockActions.CanPlaceAt"/>).
	/// </summary>
	public static bool CanPlace(BotContext ctx, int x, int y)
	{
		ITileGrid g = ctx.Grid;
		CellKind k = g.Get(x, y);
		if (k.IsSolid() || k.CanStandOn() || k.HasLava() || !ctx.Blocks.CanPlaceAt(x, y))
			return false;
		return Holds(g.Get(x - 1, y)) || Holds(g.Get(x + 1, y)) || Holds(g.Get(x, y - 1)) || Holds(g.Get(x, y + 1))
			|| ctx.World != null && (ctx.World.HasWall(x, y) || ctx.World.HasWall(x - 1, y) || ctx.World.HasWall(x + 1, y)
				|| ctx.World.HasWall(x, y - 1) || ctx.World.HasWall(x, y + 1));
	}

	/// <summary>A tile a block can be fixed to: Main.tileSolid, which platforms and slopes are too.</summary>
	private static bool Holds(CellKind k) => k.IsSolid() || k.IsPlatform();

	/// <summary>
	/// Where the body comes down holding <paramref name="input"/>: free flight (<see cref="PlayerPhysics.ApplyControls"/>) with
	/// walls stopping it sideways, a ceiling stopping the rise, and the first solid tile (or platform, from above) under its
	/// columns ending it. Safe if that is no more than <see cref="PhysicsParams.SafeFallTiles"/> rows below
	/// <paramref name="fallStart"/>, or the body reaches water or honey first.
	/// </summary>
	/// <param name="steerToX">Steer the hitbox's left edge to this x (px) each tick, as a step's executor does (Arrive.Steer),
	/// instead of holding <paramref name="input"/>'s horizontal key.</param>
	public static Landing Predict(ITileGrid g, PhysicsParams p, in BodyState from, in MoveInput input, int fallStart, float? steerToX = null)
	{
		const int T = Body.TileSize;
		BodyState b = from;
		MoveInput keys = input;
		for (int t = 0; t < Horizon; t++) {
			if (steerToX is float sx)
				keys.Horizontal = Arrive.Steer(b.X, b.VX, sx, p, 2f);
			PlayerPhysics.ApplyControls(ref b, keys, p);
			float nx = b.X + b.VX;
			if (b.VX != 0f) {
				int col = b.VX > 0f ? Floor((nx + p.Width - 0.001f) / T) : Floor(nx / T);
				bool blocked = false;
				for (int r = Floor(b.Y / T); r <= Floor((b.Y + p.Height - 0.001f) / T) && !blocked; r++)
					blocked = g.Get(col, r).IsSolid();
				if (blocked)
					b.VX = 0f;
				else
					b.X = nx;
			}
			int c0 = Floor(b.X / T), c1 = Floor((b.X + p.Width - 0.001f) / T);
			if (b.VY > 0f) {
				float oldBottom = b.Y + p.Height, newBottom = oldBottom + b.VY;
				for (int r = Floor((oldBottom - 0.001f) / T) + 1; r <= Floor((newBottom - 0.001f) / T); r++)
					for (int c = c0; c <= c1; c++) {
						CellKind k = g.Get(c, r);
						if (k.IsSolid() || k.IsPlatform() && !keys.Down && oldBottom <= r * T + 0.001f) { // Down falls through platforms
							int rows = (int)((r * T - p.Height) / (float)T) - fallStart;
							var at = new TilePos(c, r - 1);
							return rows <= p.SafeFallTiles
								? new Landing(true, rows, at, $"lands at {at}, {rows} rows down")
								: new Landing(false, rows, at, $"would land at {at}, {rows} rows down (more than a safe fall, {p.SafeFallTiles})");
						}
					}
				b.Y += b.VY;
			}
			else if (b.VY < 0f) {
				int head = Floor((b.Y + b.VY) / T);
				bool ceiling = false;
				for (int c = c0; c <= c1 && !ceiling; c++)
					ceiling = g.Get(c, head).IsSolid();
				if (ceiling)
					b.VY = 0f;
				else
					b.Y += b.VY;
			}
			for (int r = Floor(b.Y / T); r <= Floor((b.Y + p.Height - 0.001f) / T); r++)
				for (int c = c0; c <= c1; c++) {
					CellKind k = g.Get(c, r);
					if (k.HasLava())
						return new Landing(false, (int)(b.Y / T) - fallStart, new TilePos(c, r), $"falls into lava at {new TilePos(c, r)}");
					if (k.IsOpenLiquid())
						return new Landing(true, (int)(b.Y / T) - fallStart, new TilePos(c, r), $"falls into liquid at {new TilePos(c, r)}");
				}
			if ((int)(b.Y / T) - fallStart > p.SafeFallTiles)
				return Below(g, p, b, c0, c1, fallStart);
		}
		return new Landing(true, 0, Body.NodeOf(b, p), "still in the air");
	}

	/// <summary>
	/// The body is more than a safe fall down already: only liquid under it can still make the fall harmless. Straight down its
	/// columns to the first thing to stand on or the first liquid (cells off the world read as solid, so the scan ends there).
	/// </summary>
	private static Landing Below(ITileGrid g, PhysicsParams p, in BodyState b, int c0, int c1, int fallStart)
	{
		const int T = Body.TileSize;
		for (int r = Floor((b.Y + p.Height - 0.001f) / T) + 1; r < Floor(b.Y / T) + MaxWorldRows; r++)
			for (int c = c0; c <= c1; c++) {
				CellKind k = g.Get(c, r);
				if (k.IsOpenLiquid() && !k.HasLava())
					return new Landing(true, r - fallStart, new TilePos(c, r), $"falls into liquid at {new TilePos(c, r)}");
				if (k.CanStandOn() || k.HasLava()) {
					int rows = (int)((r * T - p.Height) / (float)T) - fallStart;
					var at = new TilePos(c, r - 1);
					return new Landing(false, rows, at, $"nothing to land on within a safe fall ({p.SafeFallTiles} rows): {(k.HasLava() ? "lava" : "ground")} at {at}, {rows} rows down");
				}
			}
		return new Landing(false, MaxWorldRows, Body.NodeOf(b, p), $"nothing to land on within a safe fall ({p.SafeFallTiles} rows)");
	}

	/// <summary>Rows <see cref="Below"/> scans at most: more than the deepest world (a large world is 2400 rows, Main.maxTilesY).</summary>
	private const int MaxWorldRows = 2400;

	private static int Floor(float v) => (int)MathF.Floor(v);
}
