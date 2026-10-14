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
	/// <summary>Blocks in the way up a column that are mined through on the way (a ceiling over the water).</summary>
	public const int MaxCeilingBlocks = 3;
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
	/// 2 px above it), against any cell with that liquid (the liquid's level aside). The head cells of the node missed it
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
				if ((k & (CellKind.Water | CellKind.Honey)) != 0)
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
		if (column == null || ticks % 30 == 0)
			column = AirColumn(ctx);
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
					State = $"mining up through {above}";
					return SkillStatus.Running;
				}
			}
		}
		// Fallback: vanilla has no swimming up without flippers or a float (a new jump needs footing: Player.JumpMovement
		// wants velocity.Y == 0). Seen in game: holding jump in a flooded shaft moved the bot 6 tiles in 15 s and it
		// drowned. So pillar up like a player: jump off whatever is underfoot, a block under the feet on the way up.
		bool footing = Math.Abs(ctx.Body.VY) < 0.05f;
		if (footing) {
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
		if (column is int cx && cx != n.X)
			ctx.Output.Horizontal = Math.Sign(cx - n.X);
		State = column != null ? $"pillaring up towards column {column}" : "pillaring up";
		return SkillStatus.Running;
	}

	/// <summary>
	/// The nearest column (by sideways distance plus height to swim, plus blocks to mine) with an air cell above the water,
	/// through at most <see cref="MaxCeilingBlocks"/> blocks the pickaxe can break.
	/// </summary>
	private static int? AirColumn(BotContext ctx)
	{
		TilePos n = ctx.Node;
		int? best = null;
		int bestCost = int.MaxValue;
		for (int dx = 0; dx <= SearchColumns; dx++)
			foreach (int x in dx == 0 ? new[] { n.X } : new[] { n.X - dx, n.X + dx }) {
				int blocks = 0;
				for (int y = n.Y - 2; y >= n.Y - 2 - SearchUp; y--) {
					CellKind k = ctx.Grid.Get(x, y);
					if (k.IsSolid()) {
						if (++blocks > MaxCeilingBlocks || !Breakable(ctx, new TilePos(x, y)))
							break; // too thick, or nothing the pickaxe breaks: no air this way
						continue;
					}
					if (!k.IsLiquid()) {
						int cost = dx * 2 + (n.Y - y) + blocks * CeilingBlockCost;
						if (cost < bestCost) {
							bestCost = cost;
							best = x;
						}
						break;
					}
				}
			}
		return best;
	}

	/// <summary>A block mined on the way up weighs as this many tiles of swimming.</summary>
	public const int CeilingBlockCost = 4;

	private static bool Breakable(BotContext ctx, TilePos c) =>
		ctx.Grid.Get(c.X, c.Y).IsSolid() && ctx.Moves.Blocks is Core.Pathfinding.IBlockActions b && !float.IsInfinity(b.BreakTicks(c.X, c.Y));
}
