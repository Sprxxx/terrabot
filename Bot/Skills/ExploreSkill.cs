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
/// <see cref="FallbackStep"/> tiles on in the heading for <see cref="ExploreBias.Sideways"/>, or the other way when that
/// is ground already seen (<see cref="PickDig"/>);</item>
/// <item>the heading is kept on <see cref="BotContext.LastExplore"/> for the next exploration.</item>
/// </list>
/// Fails after <see cref="MaxLegs"/> legs or <see cref="MaxTicks"/> ticks without finding it.
/// </summary>
public sealed class ExploreSkill : ISkill
{
	/// <summary>Underground only about 10 tiles round the bot get revealed, so legs must be allowed to be short.</summary>
	public const int MinLegTiles = 6, MaxLegTiles = 60;
	/// <summary>A frontier has an unseen cell within this many tiles.</summary>
	public const int FrontierReach = 3;
	/// <summary>About how far round the bot the map gets revealed underground (see <see cref="MinLegTiles"/>).</summary>
	public const int SightTiles = 10;
	public const int MaxLegs = 16;
	public const int MaxTicks = 60 * 60 * 5;
	public const int FallbackStep = 25, FallbackDepth = 20, FallbackSideways = 2;
	private bool lastFallbackFailed;
	private bool legWasFallback;
	private bool legDown;
	private int legStart;
	private TilePos legFrom;
	private int pauseUntil;
	private int instantFails;
	/// <summary>Legs in a row that fail at once before exploring gives up (<see cref="InstantFailTicks"/>).</summary>
	public const int MaxInstantFails = 10;
	/// <summary>A leg failing within this many ticks did not really start; wait <see cref="RetryPauseTicks"/> and try again.</summary>
	public const int InstantFailTicks = 10, RetryPauseTicks = 30;
	private bool downBlocked;
	/// <summary>Leg targets closer than this to an earlier one score <see cref="VisitedPenalty"/> worse (they were left out altogether).</summary>
	public const int VisitedRadius = 10;
	public const float VisitedPenalty = 30f;
	/// <summary>A leg target against the heading scores this much worse: keep on the way we were going (no zig-zag).</summary>
	public const float TurnPenalty = 40f;
	/// <summary>
	/// Digging on to ground with nothing unseen round it (seen already: going there shows nothing new) scores this much worse:
	/// more than turning round and an earlier leg's target together, so fresh ground the other way wins.
	/// </summary>
	public const float SeenPenalty = TurnPenalty + VisitedPenalty;
	/// <summary>The found check is a tile search: every this many ticks.</summary>
	public const int FoundCheckEvery = 15;
	/// <summary>Leg targets remembered in <see cref="BotContext.Explored"/>.</summary>

	private readonly Func<BotContext, bool> found;
	private WalkToSkill walk;
	private TilePos? legTarget;
	private int legs;
	private int ticks;
	private int heading;

	public string What { get; }
	/// <summary>
	/// The deepest row to explore down to, if not the gear's own (BotContext.DeepestRow): for a life crystal in a full set of
	/// armour, as deep as the crystal may be mined (MineSkill.ValuableDepthMargin). Seen in game (measured run U7): crystals
	/// were taken down to row 701 but explored for only down to 601; five days from full armour to max life 200.
	/// </summary>
	public int? DeepestRow { get; init; }
	private int Deepest(BotContext ctx) => DeepestRow ?? ctx.DeepestRow;
	/// <summary>
	/// The rows what is looked for lies in (life crystals: BotContext.CrystalBandTop to CrystalBandBottom), if any: frontiers
	/// and digs outside them cost <see cref="OutOfBandRowCost"/> a row more, a pull into the band, not a ban. Seen in game
	/// (measured run U9): 25% of the 288 crystal legs were above row 375, the top of the crystal band.
	/// </summary>
	public (int Top, int Bottom)? Band { get; init; }
	/// <summary>Per row outside <see cref="Band"/>: as much again as the downward pull per row (1.5), so above the band depth counts double.</summary>
	public const float OutOfBandRowCost = 1.5f;

	private float BandCost(int y) =>
		Band is not (int top, int bottom) ? 0f : y < top ? (top - y) * OutOfBandRowCost : y > bottom ? (y - bottom) * OutOfBandRowCost : 0f;
	public ExploreBias Bias { get; }
	public int Legs => legs;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Explore";
	/// <summary>The heading it was started with (0: chosen by itself).</summary>
	public int Heading => startHeading;
	private readonly int startHeading;
	/// <summary>
	/// The job, if the caller names one (<see cref="ISkill.Key"/>): with a key, an exploration a fight paused goes on with
	/// what it had learnt instead of a fresh one from wherever the fight left the bot.
	/// </summary>
	public string JobKey { get; init; }
	public string Key => JobKey;

	public ExploreSkill(string what, Func<BotContext, bool> found, ExploreBias bias, int heading = 0)
	{
		What = what;
		this.found = found;
		Bias = bias;
		this.heading = heading;
		startHeading = heading;
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

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		walk?.Resume(ctx);
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
			// Afloat, the leg's walk swims for footing (it waited for footing that never came: measured run S2, 490,000 ticks).
			bool afloat = Body.InLiquid(ctx.Grid, ctx.Node);
			if (!ctx.Body.Grounded && !afloat || ticks < pauseUntil) {
				State = "waiting for footing";
				return SkillStatus.Running;
			}
			if (legs >= MaxLegs)
				return Fail(ctx, $"no {What} after {legs} legs of exploring");
			TilePos node = ctx.Node;
			// Keep the heading: a frontier that way, else dig on that way, and only then a frontier anywhere (turn round).
			// Then a frontier anywhere in view (turning round costs it more): unexplored cave first, digging blind last, as a
			// player. Seen in game (measured run S1): 287 blind digging legs against 141 into caves, ore found in 12 of 70.
			TilePos? frontier = PickFrontier(ctx, node, aheadOnly: true) ?? PickFrontier(ctx, node, aheadOnly: false);
			legWasFallback = frontier == null;
			// Digging on: straight down for ores and caves (2 blocks a tile; a diagonal through stone was 60-70 blocks a
			// leg in game), a level tunnel along the surface.
			// Not deeper than the gear allows (BotContext.DeepestRow): sideways once at the floor.
			bool down = Bias == ExploreBias.Down && !downBlocked && node.Y + FallbackDepth <= Deepest(ctx);
			legDown = frontier == null && down;
			// Below the depth the gear allows (fell in, chased, knocked down): dig back up first. Seen in game: at row 480
			// with a floor of 441 every leg was a level tunnel at that depth, and skeletons killed the bot there. A full leg
			// up, not just to the floor: 4 rows below it, "up to the floor" was within the walk's tolerance, and 14 legs in
			// a row ended at once without a step (world_eye run 36).
			bool tooDeep = node.Y > Deepest(ctx);
			TilePos t;
			if (frontier is TilePos f)
				t = f;
			else {
				int dy = tooDeep ? -FallbackDepth : down ? FallbackDepth : 0;
				var ahead = new TilePos(node.X + heading * (dy != 0 ? FallbackSideways : FallbackStep), node.Y + dy);
				TilePos? open = PickDig(ctx, node, ahead, tooDeep);
				// Every way off limits (inside a biome zone): give up rather than dig on into it. Seen in game (world_eye run
				// 59): legs went on through the underground desert after an antlion charger was seen, and the bot died there.
				// Not for a sighting's zone in full armour: that is a cost (BotContext.OffLimitsOutright).
				if (open == null && ctx.OffLimitsOutright(ahead))
					return Fail(ctx, "every way from here is off limits");
				t = open ?? ahead;
			}
			legs++;
			legTarget = t;
			ctx.Explored.Add(t);
			if (t.X != node.X)
				heading = Math.Sign(t.X - node.X);
			ctx.LastExplore = (node, heading); // the next exploration from near here carries on this way (BotContext.ExploreSide)
			ctx.Log.Info($"Explore: leg {legs} to {t} ({(frontier != null ? "frontier" : "digging on")}) from {node}");
			walk = new WalkToSkill(t) { Quiet = true, Tolerance = 3, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
			walk.Start(ctx);
			legStart = ticks;
			legFrom = node;
		}
		SkillStatus s = walk.Tick(ctx);
		State = $"leg {legs} to {legTarget}";
		if (s == SkillStatus.Running)
			return SkillStatus.Running;
		if (s == SkillStatus.Failed && ticks - legStart < InstantFailTicks) {
			// Failed before it got going (no footing, start not standable): not a real leg; try again shortly. But not
			// for ever: seen in game, no move at all led out of the spot (search expanded 1 node) and the bot retried the
			// same leg for 9 game minutes. Then fail, so the brain's rest clears the vetoes that may box it in.
			if (++instantFails > MaxInstantFails) {
				TilePos here = ctx.Node;
				ctx.Log.Info($"Explore: {MaxInstantFails} legs in a row failed at once from {here} ({walk.FailureReason}); around it:\n{WalkToSkill.Around(ctx, here, legTarget is TilePos lt && lt.ChebyshevTo(here) <= 4 ? lt : here)}");
				walk.Stop(ctx);
				walk = null;
				return Fail(ctx, $"no way on from {here}: {MaxInstantFails} legs failed at once");
			}
			legs--;
			pauseUntil = ticks + RetryPauseTicks;
			walk.Stop(ctx);
			walk = null;
			return SkillStatus.Running;
		}
		// A leg that ended where it began (the target already within reach) went nowhere: count it like a failed one, so
		// the next pick turns or looks for a frontier anywhere, and the same leg is not taken again and again.
		bool wentNowhere = s == SkillStatus.Succeeded && ctx.Node.ChebyshevTo(legFrom) <= 1;
		if (wentNowhere && ++instantFails > MaxInstantFails) {
			walk.Stop(ctx);
			walk = null;
			return Fail(ctx, $"no way on from {ctx.Node}: {MaxInstantFails} legs went nowhere");
		}
		if (!wentNowhere)
			instantFails = 0;
		if (wentNowhere) {
			ctx.Log.Info($"Explore: leg {legs} to {legTarget} went nowhere; turning round");
			heading = -heading;
		}
		if (s == SkillStatus.Failed && walk.FailureReason.StartsWith("no path") && legTarget is TilePos lt0)
			ctx.UnreachableTiles.Add(lt0); // not again until the brain's rest forgets the unreachable
		if (s == SkillStatus.Failed) {
			// Down blocked (the bottom, lava): tunnel sideways from now on; a sideways tunnel blocked: turn round.
			ctx.Log.Info($"Explore: leg {legs} to {legTarget} failed ({walk.FailureReason})"
				+ (legDown ? "; going sideways instead" : legWasFallback ? "; turning round" : ""));
			if (legDown)
				downBlocked = true;
			else if (legWasFallback)
				heading = -heading;
		}
		lastFallbackFailed = legWasFallback && !legDown && (s == SkillStatus.Failed || wentNowhere);
		ctx.LastExplore = (ctx.Node, heading); // turned round included
		ctx.LookedFrom.Add(ctx.Node);
		walk.Stop(ctx);
		walk = null;
		return SkillStatus.Running;
	}

	private static bool Barred(BotContext ctx, TilePos t) => ctx.OffLimitsOutright(t) || ctx.UnreachableTiles.Contains(t);

	/// <summary>
	/// A leg target at the centre of a zone where monsters were seen, in full armour, scores this much worse, less towards
	/// its edge (<see cref="BotContext.SightingDepth"/>): as much as ground already seen, so fresh ground round the zone
	/// wins, but a leg into it still beats none. A cost, not a ban, like Baritone's mob avoidance. Seen in game (measured
	/// run U9): with such zones skipped outright, 151 of 204 legs while they stood stayed at row 412 or above, and only 6.6%
	/// of the 288 legs for life crystals (rows 375-900) reached row 600.
	/// </summary>
	public const float SightingPenalty = SeenPenalty;

	/// <summary>
	/// Where to dig on with no frontier in view: <paramref name="ahead"/> (on in the heading: down, up or level, as the leg
	/// calls for), the same the other way, or level either way, scored as frontiers are (<see cref="PickFrontier"/>): near
	/// an earlier leg's target costs <see cref="VisitedPenalty"/>, nothing unseen round it <see cref="SeenPenalty"/>,
	/// turning round <see cref="TurnPenalty"/>. Costs, not bans: dug-over ground is still gone through when every other way
	/// is worse.
	/// Seen in game (10-day world_eye runs, audit of 2026-10-16): at the gear's depth floor, "digging on" was 25 tiles on
	/// from where the bot stood whatever lay there, and from a tunnel's far end facing back it went back through the open
	/// tunnel, leg after leg. Not barred ground (off limits, or found unreachable: world_eye run 42, a fight every few
	/// seconds restarted mining and with it the same unreachable first leg for 12 game hours). Null if every way is barred.
	/// </summary>
	private TilePos? PickDig(BotContext ctx, TilePos n, TilePos ahead, bool tooDeep)
	{
		var options = new[] {
			ahead,
			new TilePos(n.X - (ahead.X - n.X), ahead.Y),
			new TilePos(n.X + heading * FallbackStep, n.Y),
			new TilePos(n.X - heading * FallbackStep, n.Y),
		};
		TilePos? best = null;
		float bestScore = float.MaxValue;
		foreach (TilePos o in options) {
			// Too deep: up first, level only with both ways up barred (skeletons killed the bot tunnelling below the floor).
			if (Barred(ctx, o) || tooDeep && o.Y == n.Y && best != null)
				continue;
			float score = o.ChebyshevTo(n) + ctx.SightingDepth(o) * SightingPenalty;
			if (NearVisited(ctx, o.X, o.Y))
				score += VisitedPenalty;
			if (!NearUnseen(ctx.World, o.X, o.Y))
				score += SeenPenalty;
			if (Math.Sign(o.X - n.X) != heading)
				score += TurnPenalty;
			score += BandCost(o.Y);
			if (tooDeep)
				score -= (n.Y - o.Y) * 1.5f; // higher is better
			else if (Bias == ExploreBias.Down)
				score -= (o.Y - n.Y) * 1.5f; // deeper is better, as for frontiers
			if (score < bestScore) {
				bestScore = score;
				best = o;
			}
		}
		return best;
	}

	/// <summary>The best frontier in view, or null.</summary>
	private TilePos? PickFrontier(BotContext ctx, TilePos n, bool aheadOnly)
	{
		IWorldInfo w = ctx.World;
		ITileGrid g = ctx.Grid;
		TilePos? best = null;
		float bestScore = float.MaxValue;
		int up = Bias == ExploreBias.Down ? 10 : 20, down = Bias == ExploreBias.Down ? MaxLegTiles : 20;
		if (n.Y > Deepest(ctx))
			up = Math.Min(MaxLegTiles, n.Y - Deepest(ctx) + 10); // too deep: look up to back above the floor
		for (int x = n.X - MaxLegTiles; x <= n.X + MaxLegTiles; x += 2) {
			for (int y = n.Y - up; y <= n.Y + down; y++) {
				int d = Math.Max(Math.Abs(x - n.X), Math.Abs(y - n.Y));
				if (d < MinLegTiles)
					continue;
				if (aheadOnly && Math.Sign(x - n.X) != heading)
					continue;
				if (y > Deepest(ctx) || ctx.OffLimitsOutright(new TilePos(x, y)))
					continue;
				if (!w.IsRevealed(x, y) || !Standable(g, x, y) || Body.InLiquid(g, new TilePos(x, y)) || !NearUnseen(w, x, y))
					continue;
				var cand = new TilePos(x, y);
				if (ctx.UnreachableTiles.Contains(cand) || LookedAt(ctx, cand))
					continue;
				float score = d + ctx.SightingDepth(cand) * SightingPenalty;
				// Near an earlier leg's target: still worth it if unseen cells are there (NearUnseen), but less so.
				if (NearVisited(ctx, x, y))
					score += VisitedPenalty;
				if (Math.Sign(x - n.X) != heading)
					score += TurnPenalty;
				score += BandCost(y);
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

	/// <summary>
	/// Its unseen cells were in sight from where a leg ended, and are unseen still: solid rock, or dark, not to be seen by
	/// going there (Baritone keeps the chunks it has been in as explored). Seen in game (measured run S7): legs 10, 12 and
	/// 14 of one exploration all went to the frontier at (2024,476), with "digging on" legs between, back and forth.
	/// </summary>
	private static bool LookedAt(BotContext ctx, TilePos c) =>
		ctx.LookedFrom.AnyWithin(c.X, c.Y, SightTiles - FrontierReach, SightTiles - FrontierReach);

	internal static bool NearVisited(BotContext ctx, int x, int y) => ctx.Explored.AnyWithin(x, y, VisitedRadius, VisitedRadius);

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Explore failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
