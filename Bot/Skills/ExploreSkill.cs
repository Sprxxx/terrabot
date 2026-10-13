using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>Which way exploring leans.</summary>
public enum ExploreBias
{
	/// <summary>Along the surface, left and right.</summary>
	Sideways,
	/// <summary>Down into caves and through the ground (ores, life crystals, chests).</summary>
	Down,
}

/// <summary>
/// Explore until <see cref="Found"/> holds, as a player does: walk to a frontier, a spot the player has seen
/// (revealed on the map) and could stand at, with unseen cells close by. The walk itself reveals more (the map
/// fills in around the player and where torches light it). One leg after another:
/// <list type="bullet">
/// <item>frontiers <see cref="MinLegTiles"/>–<see cref="MaxLegTiles"/> tiles away are scored by distance, keeping on in
/// the same heading (no zig-zag), and the bias (deeper for <see cref="ExploreBias.Down"/>);</item>
/// <item>spots near earlier legs' targets are skipped;</item>
/// <item>with no frontier in view, dig: <see cref="FallbackDepth"/> tiles straight down for <see cref="ExploreBias.Down"/>,
/// <see cref="FallbackStep"/> tiles on in the heading for <see cref="ExploreBias.Sideways"/>.</item>
/// </list>
/// Fails after <see cref="MaxLegs"/> legs or <see cref="MaxTicks"/> ticks without finding it.
/// </summary>
public sealed class ExploreSkill : ISkill
{
	/// <summary>Underground only about 10 tiles round the bot get revealed, so legs must be allowed to be short.</summary>
	public const int MinLegTiles = 6, MaxLegTiles = 60;
	/// <summary>A frontier has an unseen cell within this many tiles.</summary>
	public const int FrontierReach = 3;
	public const int MaxLegs = 16;
	public const int MaxTicks = 60 * 60 * 5;
	public const int FallbackStep = 25, FallbackDepth = 20, FallbackSideways = 2;
	private bool lastFallbackFailed;
	private bool legWasFallback;
	private bool legDown;
	private int legStart;
	private int pauseUntil;
	/// <summary>A leg failing within this many ticks did not really start; wait <see cref="RetryPauseTicks"/> and try again.</summary>
	public const int InstantFailTicks = 10, RetryPauseTicks = 30;
	private bool downBlocked;
	/// <summary>Leg targets closer than this to an earlier one are not picked again.</summary>
	public const int VisitedRadius = 10;
	/// <summary>The found check is a tile search: every this many ticks.</summary>
	public const int FoundCheckEvery = 15;
	/// <summary>Leg targets remembered in <see cref="BotContext.Explored"/>.</summary>
	public const int MaxRemembered = 300;

	private readonly Func<BotContext, bool> found;
	private WalkToSkill walk;
	private TilePos? legTarget;
	private int legs;
	private int ticks;
	private int heading;

	public string What { get; }
	public ExploreBias Bias { get; }
	public int Legs => legs;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Explore";

	public ExploreSkill(string what, Func<BotContext, bool> found, ExploreBias bias, int heading = 0)
	{
		What = what;
		this.found = found;
		Bias = bias;
		this.heading = heading;
	}

	public bool Found(BotContext ctx) => found(ctx);

	public string Describe() => $"Explore for {What} ({Bias}) leg {legs}/{MaxLegs} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		if (heading == 0)
			heading = (ctx.Node.X & 1) == 0 ? 1 : -1;
		ctx.Log.Info($"Explore start: looking for {What} ({Bias}), heading {(heading > 0 ? "right" : "left")} from {ctx.Node}");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ticks % FoundCheckEvery == 0 && found(ctx)) {
			ctx.Log.Info($"Explore: found {What} after {legs} leg(s), {ticks} ticks, at {ctx.Node}");
			State = "found";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		if (++ticks > MaxTicks)
			return Fail(ctx, $"no {What} after {ticks} ticks of exploring");
		if (walk == null) {
			// Only from solid footing, and not straight after a leg failed at once: in game, knocked about in a fight, every
			// leg failed instantly ("invalid start") and all 16 were gone in a third of a second.
			if (!ctx.Body.Grounded || ticks < pauseUntil) {
				State = "waiting for footing";
				return SkillStatus.Running;
			}
			if (legs >= MaxLegs)
				return Fail(ctx, $"no {What} after {legs} legs of exploring");
			TilePos node = ctx.Node;
			// Keep the heading: a frontier that way, else dig on that way, and only then a frontier anywhere (turn round).
			TilePos? frontier = PickFrontier(ctx, node, aheadOnly: true);
			if (frontier == null && lastFallbackFailed)
				frontier = PickFrontier(ctx, node, aheadOnly: false);
			legWasFallback = frontier == null;
			// Digging on: straight down for ores and caves (2 blocks a tile; a diagonal through stone was 60-70 blocks a
			// leg in game), a level tunnel along the surface.
			// Not deeper than the gear allows (BotContext.DeepestRow): sideways once at the floor.
			bool down = Bias == ExploreBias.Down && !downBlocked && node.Y + FallbackDepth <= ctx.DeepestRow;
			legDown = frontier == null && down;
			TilePos t = frontier ?? (down
				? new TilePos(node.X + heading * FallbackSideways, node.Y + FallbackDepth)
				: new TilePos(node.X + heading * FallbackStep, node.Y));
			legs++;
			legTarget = t;
			ctx.Explored.Add(t);
			if (ctx.Explored.Count > MaxRemembered)
				ctx.Explored.RemoveAt(0);
			if (t.X != node.X)
				heading = Math.Sign(t.X - node.X);
			ctx.Log.Info($"Explore: leg {legs} to {t} ({(frontier != null ? "frontier" : "digging on")}) from {node}");
			walk = new WalkToSkill(t) { Quiet = true, Tolerance = 3, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
			walk.Start(ctx);
			legStart = ticks;
		}
		SkillStatus s = walk.Tick(ctx);
		State = $"leg {legs} to {legTarget}";
		if (s == SkillStatus.Running)
			return SkillStatus.Running;
		if (s == SkillStatus.Failed && ticks - legStart < InstantFailTicks) {
			// Failed before it got going (no footing, start not standable): not a real leg; try again shortly.
			legs--;
			pauseUntil = ticks + RetryPauseTicks;
			walk.Stop(ctx);
			walk = null;
			return SkillStatus.Running;
		}
		if (s == SkillStatus.Failed) {
			// Down blocked (the bottom, lava): tunnel sideways from now on; a sideways tunnel blocked: turn round.
			ctx.Log.Info($"Explore: leg {legs} to {legTarget} failed ({walk.FailureReason})"
				+ (legDown ? "; going sideways instead" : legWasFallback ? "; turning round" : ""));
			if (legDown)
				downBlocked = true;
			else if (legWasFallback)
				heading = -heading;
		}
		lastFallbackFailed = legWasFallback && !legDown && s == SkillStatus.Failed;
		walk.Stop(ctx);
		walk = null;
		return SkillStatus.Running;
	}

	/// <summary>The best frontier in view, or null.</summary>
	private TilePos? PickFrontier(BotContext ctx, TilePos n, bool aheadOnly)
	{
		IWorldInfo w = ctx.World;
		ITileGrid g = ctx.Grid;
		TilePos? best = null;
		float bestScore = float.MaxValue;
		int up = Bias == ExploreBias.Down ? 10 : 20, down = Bias == ExploreBias.Down ? MaxLegTiles : 20;
		for (int x = n.X - MaxLegTiles; x <= n.X + MaxLegTiles; x += 2) {
			for (int y = n.Y - up; y <= n.Y + down; y++) {
				int d = Math.Max(Math.Abs(x - n.X), Math.Abs(y - n.Y));
				if (d < MinLegTiles)
					continue;
				if (aheadOnly && Math.Sign(x - n.X) != heading)
					continue;
				if (y > ctx.DeepestRow)
					continue;
				if (!w.IsRevealed(x, y) || !Standable(g, x, y) || Body.InLiquid(g, new TilePos(x, y)) || !NearUnseen(w, x, y) || NearVisited(ctx, x, y))
					continue;
				float score = d;
				if (Math.Sign(x - n.X) != heading)
					score += 40f; // keep on the way we were going
				if (Bias == ExploreBias.Down)
					score -= (y - n.Y) * 1.5f; // deeper is better
				else
					score += Math.Abs(y - n.Y) * 0.5f; // stay near the surface's height
				if (score < bestScore) {
					bestScore = score;
					best = new TilePos(x, y);
				}
			}
		}
		return best;
	}

	/// <summary>Feet cell and the two above passable, solid or platform below (the bot's 2×3 body fits on the left column).</summary>
	private static bool Standable(ITileGrid g, int x, int y) =>
		g.Get(x, y).IsPassable() && g.Get(x, y - 1).IsPassable() && g.Get(x, y - 2).IsPassable() && g.Get(x, y + 1).CanStandOn();

	private static bool NearUnseen(IWorldInfo w, int x, int y)
	{
		for (int dx = -FrontierReach; dx <= FrontierReach; dx += FrontierReach)
			for (int dy = -FrontierReach; dy <= FrontierReach; dy += FrontierReach)
				if ((dx != 0 || dy != 0) && !w.IsRevealed(x + dx, y + dy))
					return true;
		return false;
	}

	private static bool NearVisited(BotContext ctx, int x, int y)
	{
		foreach (TilePos v in ctx.Explored)
			if (Math.Abs(v.X - x) <= VisitedRadius && Math.Abs(v.Y - y) <= VisitedRadius)
				return true;
		return false;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Explore failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
