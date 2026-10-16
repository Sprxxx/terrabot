using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Out of breath under water: pillar up (jump, block under the feet) towards the nearest column with air above the head, until
/// breath is back near full and the head is out. Seen in game: the bot chased a slime into a flooded cave and
/// drowned (vanilla takes 2 life per breath tick at 0 breath, without a hurt event: nothing showed in the log).
/// </summary>
public sealed class SurfaceSkill : ISkill
{
	/// <summary>
	/// Longer than a full breath (200 at one per 7 ticks): under water there is nothing better to do than keep going up.
	/// Seen in game (measured runs L1, M1, M2): every drowning was a surfacing that gave up after 900 ticks with breath
	/// at 21, then a second one that had no air left.
	/// </summary>
	public const int MaxTicks = 60 * 30;
	/// <summary>Rows of blocks in the way up a column that are mined through on the way (a ceiling over the water).</summary>
	public const int MaxCeilingBlocks = 4;
	public const int SearchColumns = 16, SearchUp = 25;

	private int ticks;
	private int? column;
	private bool jumpedLastTick;
	private WalkToSkill walk;
	private bool walkFailed;
	/// <summary>How far (cells, through water and air) to look for a dry place to stand.</summary>
	public const int AirSearchCells = 600;
	public const int MaxWalkTicks = 180;
	public const float GiveUpWalkBreath = 0.35f;
	private int walkStart;
	/// <summary>Rising straight up over the node's own column (mining through), after a stall heading elsewhere.</summary>
	private bool straightUp;
	private int progressTick = -1, progressRow, progressDist;
	private int? progressColumn;

	/// <summary>
	/// One held jump under water from standing until back where it began, by vanilla (Player.cs, wet without gear: jumpHeight
	/// 30, jumpSpeed 6.01, gravity 0.2, maxFallSpeed 5.01; WaterCollision moves the body half its velocity): 30 ticks at
	/// 3 px (90 px), 30 more slowing by 0.2 a tick (45 px: the 8 rows of a water jump), then the 135 px back down, 25 ticks
	/// gathering speed (31 px) and 42 at 2.5 px. About 127 ticks; a working pillar gains a row in each, landing on its block.
	/// </summary>
	public const int WaterJumpCycleTicks = 130;
	/// <summary>
	/// Pillaring that has neither gained a row nor got closer to its column in this long is stuck: two jump cycles, so one
	/// jump that placed nothing (the hitbox not yet clear of the cell) is not taken for it.
	/// </summary>
	public const int StallTicks = 2 * WaterJumpCycleTicks;
	/// <summary>How often the pillaring logs where it is (a second), so a drowning can be read from the log.</summary>
	public const int LogEveryTicks = 60;
	/// <summary>Rows over the head the pillaring log draws: U21's air was 5 rows up.</summary>
	public const int LogRowsAbove = 5;

	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Surface";

	public string Describe() => $"Surface [{State}]";

	public void Start(BotContext ctx) => ctx.Log.Info($"Surface start: breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} at {ctx.Node}");
	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	/// <summary>
	/// The nearest node (breadth-first through passable cells: water, air, slopes) where the body can stand with no part
	/// of it in liquid; null if none within <see cref="AirSearchCells"/>.
	/// </summary>
	private static TilePos? NearestAir(BotContext ctx)
	{
		TilePos start = ctx.Node;
		var seen = new System.Collections.Generic.HashSet<TilePos> { start };
		var queue = new System.Collections.Generic.Queue<TilePos>();
		queue.Enqueue(start);
		while (queue.Count > 0 && seen.Count < AirSearchCells) {
			TilePos c = queue.Dequeue();
			if (c != start && Core.Pathfinding.Body.IsStandable(ctx.Grid, c) && !Core.Pathfinding.Body.InLiquid(ctx.Grid, c))
				return c;
			foreach (TilePos d in new[] { new TilePos(0, -1), new TilePos(-1, 0), new TilePos(1, 0), new TilePos(0, 1) }) {
				TilePos nx = new(c.X + d.X, c.Y + d.Y);
				if (seen.Add(nx) && ctx.Grid.Get(nx.X, nx.Y).IsPassable())
					queue.Enqueue(nx);
			}
		}
		return null;
	}

	/// <summary>
	/// Is the head in water or honey? As vanilla's Collision.DrownCollision: a 10x12 px box at the top of the hitbox (from
	/// 2 px above it), against any cell with that liquid (the liquid's level aside) but a solid block's own, which vanilla
	/// skips too (Tile.active and Main.tileSolid). The head cells of the node missed it
	/// when the bot stood a few pixels low (on a slope): breath ran down from 95 to 18 while they read empty (world_eye run 59).
	/// </summary>
	public static bool HeadUnder(BotContext ctx) => HeadUnder(ctx.Grid, ctx.Body.X, ctx.Body.Y, ctx.Physics);

	public const int HeadBoxWidth = 10, HeadBoxHeight = 12, HeadBoxAbove = 2;

	public static bool HeadUnder(ITileGrid g, float left, float top, PhysicsParams p)
	{
		float x0 = left + p.Width / 2 - HeadBoxWidth / 2, y0 = top - HeadBoxAbove;
		int cx0 = (int)Math.Floor(x0 / 16f), cx1 = (int)Math.Floor((x0 + HeadBoxWidth - 0.01f) / 16f);
		int cy0 = (int)Math.Floor(y0 / 16f), cy1 = (int)Math.Floor((y0 + HeadBoxHeight - 0.01f) / 16f);
		for (int x = cx0; x <= cx1; x++)
			for (int y = cy0; y <= cy1; y++) {
				CellKind k = g.Get(x, y);
				if ((k & (CellKind.Water | CellKind.Honey)) != 0 && !k.IsSolid())
					return true;
			}
		return false;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (!HeadUnder(ctx) && ctx.Combat.Breath >= ctx.Combat.BreathMax * 0.9f) {
			ctx.Log.Info($"Surface: breathing again at {ctx.Node}");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (++ticks > MaxTicks) {
			FailureReason = $"no air reached in {MaxTicks} ticks";
			ctx.Log.Warn("Surface failed: " + FailureReason);
			return SkillStatus.Failed;
		}
		if (!HeadUnder(ctx)) {
			State = "catching breath";
			return SkillStatus.Running; // head out: just wait here for breath to come back
		}
		TilePos n = ctx.Node;
		if (straightUp)
			column = n.X;
		else if (column == null || ticks % 30 == 0)
			column = EscapeUp(ctx).Column;
		// Straight up is the way out (no sideways swim): no walk. Seen in game (measured run O1): the walk to air 13 tiles off
		// sideways, then a swim that way, covered 9 tiles in a whole breath; rising is about 20 ticks a row, mining included.
		if (column == n.X)
			walkFailed = true;
		// First choice: walk (the pathfinder digs through a ceiling and pillars) to the nearest spot where the head is out.
		if (walk == null && !walkFailed && ctx.Body.Grounded) {
			if (NearestAir(ctx) is TilePos air) {
				ctx.Log.Info($"Surface: heading for air at {air}");
				// The node itself: a plain tile target means "stand on the ground below it", which here was the bot's own spot
				// under the water, so the walk "arrived" at once, every tick (world_eye run 54).
				walk = new WalkToSkill(new Core.Pathfinding.GoalNearTile(air, 1, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
				walk.Start(ctx);
				walkStart = ticks;
			}
			else
				walkFailed = true;
		}
		// A walk that is not getting there gives way to pillaring up: seen in game (world_eye run 49), a walk to air 9 tiles
		// off, placing blocks under water, used all 900 ticks and the bot drowned with the pillar never tried.
		if (walk != null && (ticks - walkStart > MaxWalkTicks || ctx.Combat.Breath < ctx.Combat.BreathMax * GiveUpWalkBreath)) {
			ctx.Log.Info($"Surface: the walk to air is too slow (breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax}); pillaring up instead");
			walk.Stop(ctx);
			walk = null;
			walkFailed = true;
		}
		if (walk != null) {
			SkillStatus ws = walk.Tick(ctx);
			State = "walking to air";
			if (ws == SkillStatus.Running)
				return SkillStatus.Running;
			walk.Stop(ctx);
			walk = null;
			walkFailed = ws == SkillStatus.Failed;
			return SkillStatus.Running;
		}
		CheckProgress(ctx, n);
		// A ceiling over the head in this column: mine it, as a player digs up out of a flooded cave. Seen in game (measured
		// runs L1, M1, M2): air 3-11 rows up, through a block or two; the old column search skipped any column with a block
		// in it, so the bot drifted sideways under the ceiling, never rose, and drowned (5 of 17 deaths).
		if (column == null || column == n.X) {
			foreach (int dx in new[] { 0, 1 }) {
				var above = new TilePos(n.X + dx, n.Y - Core.Pathfinding.Body.HeightTiles);
				if (Breakable(ctx, above)) {
					ctx.Output.Tool = ToolRequest.Pickaxe;
					ctx.Output.UseItem = true;
					ctx.Output.Aim(above.X, above.Y);
					ctx.Output.Jump = false;
					jumpedLastTick = false;
					progressTick = ticks; // mining is the way up too: not a stall
					State = $"mining up through {above}";
					return SkillStatus.Running;
				}
			}
		}
		// Fallback: vanilla has no swimming up without flippers or a float (a new jump needs footing: Player.JumpMovement
		// wants velocity.Y == 0). Seen in game: holding jump in a flooded shaft moved the bot 6 tiles in 15 s and it
		// drowned. So pillar up like a player: jump off whatever is underfoot, a block under the feet on the way up.
		bool footing = Math.Abs(ctx.Body.VY) < 0.05f;
		// Over the column's two cells first: the hitbox (20 px) reaches into a third column when more than 6 px off centre,
		// and a jump from there hits whatever is above that column (Collision.up ends it: jump = 0), which the search and
		// the mining above never look at. Seen in game (measured runs U6, U8): the node on the column, the hitbox 0.3-1.5 px
		// into the next one under a block, jumping in place until it drowned; 12 of 26 drownings in U1-U8 had the hitbox across
		// three columns.
		if (footing && column == n.X && OffColumns(ctx.Body.X, n.X, ctx.Physics)) {
			ctx.Output.Jump = false;
			jumpedLastTick = false;
		}
		else if (footing) {
			ctx.Output.Jump = !jumpedLastTick; // a fresh press each time (releaseJump)
			jumpedLastTick = ctx.Output.Jump;
		}
		else {
			ctx.Output.Jump = ctx.Body.VY < 0f; // keep the jump going while rising
			jumpedLastTick = ctx.Output.Jump;
			var below = new TilePos(n.X, n.Y + 1);
			bool clearOfIt = ctx.Body.Y + ctx.Physics.Height <= below.Y * 16f - 1f;
			if (ctx.Body.VY < 0f && clearOfIt && !ctx.Grid.Get(below.X, below.Y).IsSolid() && ctx.Blocks != null && ctx.Blocks.BlockCount > 0) {
				ctx.Output.Tool = ToolRequest.Block;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(below.X, below.Y);
			}
		}
		// Steer onto the column's centre, not just until the node is on it: pressing on until the node changed, then letting
		// go, coasted the hitbox past the centre into the next column (the third-column jumps above).
		if (column is int cx) {
			PhysicsParams p = ctx.Physics;
			ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Core.Pathfinding.Body.CanonicalLeft(cx, p), p, CentreTolerance(p));
		}
		State = column != null ? $"pillaring up towards column {column}" : "pillaring up";
		if (ticks % LogEveryTicks == 0)
			ctx.Log.Info($"Surface: pillaring at {n} towards column {column?.ToString() ?? "-"}{(straightUp ? " (straight up)" : "")}, footing {footing}, vy {ctx.Body.VY:0.00}, "
				+ $"breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax}, cells {Cells(ctx.Grid, n, column ?? n.X)}");
		return SkillStatus.Running;
	}

	/// <summary>
	/// Pillaring that has gained no row and got no closer to its column in <see cref="StallTicks"/> switches between the
	/// column search and straight up over its own column, mining through (the search cannot see every way a column is
	/// shut; straight up, the mining over the head takes whatever ends the jumps). Seen in game (measured runs U20, U21):
	/// heading for the column to the left, the bot jumped in place for some 1200 ticks, 5 rows under air, and drowned.
	/// </summary>
	private void CheckProgress(BotContext ctx, TilePos n)
	{
		int dist = column is int c ? Math.Abs(c - n.X) : 0;
		if (progressTick < 0 || column != progressColumn) {
			if (progressTick < 0) {
				progressTick = ticks;
				progressRow = n.Y;
			}
			progressColumn = column;
			progressDist = dist;
		}
		if (n.Y < progressRow || dist < progressDist) {
			progressTick = ticks;
			progressRow = Math.Min(progressRow, n.Y);
			progressDist = dist;
			return;
		}
		if (ticks - progressTick < StallTicks)
			return;
		straightUp = !straightUp;
		ctx.Log.Info($"Surface: no row gained in {StallTicks} ticks at {n} towards column {column?.ToString() ?? "-"}; "
			+ (straightUp ? "going straight up, mining through" : "searching for a way up again") + $" (cells {Cells(ctx.Grid, n, column ?? n.X)})");
		column = straightUp ? n.X : EscapeUp(ctx).Column;
		progressTick = ticks;
		progressRow = n.Y;
		progressColumn = column;
		progressDist = column is int c2 ? Math.Abs(c2 - n.X) : 0;
	}

	/// <summary>
	/// The cells from <see cref="LogRowsAbove"/> rows over the head down to the feet, across the node's two columns and
	/// <paramref name="x"/>'s, top row first, as the dumps draw them.
	/// </summary>
	private static string Cells(ITileGrid g, TilePos n, int x)
	{
		int x0 = Math.Min(n.X, x), x1 = Math.Max(n.X, x) + Core.Pathfinding.Body.WidthTiles - 1;
		int y0 = n.Y - Core.Pathfinding.Body.HeightTiles + 1 - LogRowsAbove;
		var sb = new System.Text.StringBuilder($"x {x0}-{x1} y {y0}-{n.Y} ");
		for (int y = y0; y <= n.Y; y++) {
			if (y > y0)
				sb.Append('|');
			for (int cx = x0; cx <= x1; cx++)
				sb.Append(g.Get(cx, y).ToChar());
		}
		return sb.ToString();
	}

	/// <summary>
	/// Can the body swim from node <paramref name="n"/> over to column <paramref name="x"/> at its own rows: no solid cell in
	/// the columns it moves into? The way-up search starts at the head row, so it never saw a wall beside the lower two.
	/// Seen in game (measured runs U20, U21): column n-1 open over the head but solid beside the body's lower rows; the bot
	/// pressed left against it, the mining (over its own column only) never took the ceiling over the head out, and every
	/// jump ended at once until it drowned.
	/// </summary>
	public static bool SidewaysOpen(ITileGrid g, TilePos n, int x)
	{
		int from = x < n.X ? x : n.X + Core.Pathfinding.Body.WidthTiles, to = x < n.X ? n.X - 1 : x + Core.Pathfinding.Body.WidthTiles - 1;
		for (int cx = from; cx <= to; cx++)
			for (int y = n.Y - Core.Pathfinding.Body.HeightTiles + 1; y <= n.Y; y++)
				if (g.Get(cx, y).IsSolid())
					return false;
		return true;
	}

	/// <summary>
	/// Does a hitbox with its left edge at <paramref name="left"/> reach outside columns <paramref name="x"/>..x+1? Vanilla's
	/// Collision.TileCollision collides with any tile the box overlaps by more than nothing (strict compares).
	/// </summary>
	public static bool OffColumns(float left, int x, PhysicsParams p) =>
		left < x * Core.Pathfinding.Body.TileSize || left + p.Width > (x + Core.Pathfinding.Body.WidthTiles) * Core.Pathfinding.Body.TileSize;

	/// <summary>
	/// How far off the column's centre the steering lets the hitbox rest: half the slack either side (3 px of 6 for a 20 px
	/// body), so the coast after letting go stays inside the two columns.
	/// </summary>
	public static float CentreTolerance(PhysicsParams p) => Core.Pathfinding.Body.CenterOffset(p) / 2f;

	/// <summary>Estimated ticks per row risen under water (jump, a block underfoot), per tile swum sideways, per block mined.</summary>
	public const int RiseTicksPerRow = 25, SidewaysTicksPerTile = 60, MineTicksPerBlock = 25;
	/// <summary>Breath lost per tick under water: one every 7 ticks (vanilla Player.breathCDMax without gear).</summary>
	public const int TicksPerBreath = 7;

	/// <summary>
	/// The way up out of the water: the column (the body's left one), reachable sideways at the body's rows
	/// (<see cref="SidewaysOpen"/>), with air above both body columns that is quickest to reach by the estimate (sideways swimming is slow, rising fast, blocks in either column mined on the way, at most
	/// <see cref="MaxCeilingBlocks"/> rows of them), and the ticks it takes. No column: (null, int.MaxValue).
	/// </summary>
	public static (int? Column, int Ticks) EscapeUp(BotContext ctx)
	{
		TilePos n = ctx.Node;
		int? best = null;
		int bestTicks = int.MaxValue;
		for (int dx = 0; dx <= SearchColumns; dx++)
			foreach (int x in dx == 0 ? new[] { n.X } : new[] { n.X - dx, n.X + dx }) {
				int sideways = dx * SidewaysTicksPerTile;
				if (sideways >= bestTicks || !SidewaysOpen(ctx.Grid, n, x))
					continue;
				int blockRows = 0, blocks = 0;
				for (int y = n.Y - 2; y >= n.Y - 2 - SearchUp; y--) {
					CellKind a = ctx.Grid.Get(x, y), b = ctx.Grid.Get(x + 1, y);
					if (a.IsBlockTile() || b.IsBlockTile()) {
						if (a.IsBlockTile() && !Breakable(ctx, new TilePos(x, y)) || b.IsBlockTile() && !Breakable(ctx, new TilePos(x + 1, y)) || ++blockRows > MaxCeilingBlocks)
							break; // too thick, or nothing the pickaxe breaks: no air this way
						blocks += (a.IsBlockTile() ? 1 : 0) + (b.IsBlockTile() ? 1 : 0);
						continue;
					}
					if (!a.IsLiquid() && !b.IsLiquid()) {
						int t = sideways + (n.Y - 2 - y) * RiseTicksPerRow + blocks * MineTicksPerBlock;
						if (t < bestTicks) {
							bestTicks = t;
							best = x;
						}
						break;
					}
				}
			}
		return (best, bestTicks);
	}

	/// <summary>
	/// A block the pickaxe takes out of the way up: solid, or a slope or half block. Those read as platforms (feet ride them)
	/// but a head rising into one collides with it (Collision.TileCollision: a half brick's lower half, a slope's whole
	/// cell), and the jump ends there. Seen in game (measured run U6): a slope over the head and a half block over the
	/// next column read as open water, never mined; the bot jumped under them until it drowned.
	/// </summary>
	private static bool Breakable(BotContext ctx, TilePos c) =>
		ctx.Grid.Get(c.X, c.Y).IsBlockTile() && ctx.Moves.Blocks is Core.Pathfinding.IBlockActions b && !float.IsInfinity(b.BreakTicks(c.X, c.Y));
}
