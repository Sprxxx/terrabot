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
/// is ground already seen (<see cref="PickDig"/>); short of off-limits ground, and up a dig when it cannot go deeper;</item>
/// <item>the heading is kept on <see cref="BotContext.LastExplore"/> for the next exploration.</item>
/// </list>
/// Fails after <see cref="MaxLegs"/> legs or <see cref="MaxTicks"/> ticks without finding it, and sooner when every way to
/// dig on is barred or (exploring down) the best of them goes back to ground explored and seen already.
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

	/// <summary>Exploring down, each row deeper scores this much better (<see cref="DepthScore"/>).</summary>
	public const float DownPullPerRow = 1.5f;

	/// <summary>
	/// Each row below the gear's own floor (BotContext.DeepestRow) a leg would end at costs this much: as much as the downward
	/// pull per row, the other way. A cost, not a ban: exploring a <see cref="Band"/> with a deeper floor of its own
	/// (<see cref="DeepestRow"/>: a crystal's, MineSkill.ValuableDepthMargin), those rows are still explored when nothing
	/// above is left, and a crystal there is still mined. Seen in game (measured run U26, the gear's floor row 626, a
	/// crystal's 726): in the crystal phase 20 of 45 legs went below row 626, and there came 73 of its 81 hurts and all 4
	/// deaths (rows 720-772, poison spit and a spider cave), but none of the 4 crystals mined (rows 449-567).
	/// </summary>
	public const float BelowGearFloorRowCost = DownPullPerRow;

	/// <summary>
	/// The depth part of a leg's score ending at row <paramref name="y"/> from row <paramref name="fromY"/>, exploring down:
	/// deeper is better (<see cref="DownPullPerRow"/>) down to the gear's floor, and with a <see cref="Band"/> only down to
	/// its top: in it what is looked for is as likely at every row (vanilla WorldGen "Life Crystals": each crystal's row drawn
	/// evenly from the band, AddLifeCrystal setting it on the first solid tile below), so depth there buys nothing, while the
	/// monsters get worse. Below the gear's own floor each row costs (<see cref="BelowGearFloorRowCost"/>). Seen in game
	/// (measured run U26): from (2166,567) all 15 legs of one crystal exploration went down, to row 724, and the four deaths
	/// were at rows 720-772.
	/// </summary>
	private float DepthScore(BotContext ctx, int fromY, int y)
	{
		int floor = ctx.DeepestRow;
		int pullTo = Band is (int top, _) ? Math.Min(top, floor) : floor;
		float score = -(Math.Min(y, pullTo) - Math.Min(fromY, pullTo)) * DownPullPerRow;
		if (y > floor)
			score += (y - floor) * BelowGearFloorRowCost;
		return score;
	}

	/// <summary>
	/// The row a leg to <paramref name="t"/> ends at: the walk stands on the first floor below a target in the open
	/// (WalkToSkill, GoalResolver.StandNear), so digging on into a cave is a drop to its floor. Seen in game (measured run
	/// U26): "leg 2 to (2101,726) (digging on)" from (2103,706) was a walk to StandAt(2101,760) in a spider cave, 34 rows below
	/// a crystal's floor (726) and 134 below the gear's (626); wall creepers hurt the bot there 9 times and killed it at
	/// (2138,748). Likewise (2102,739) to row 760 and (2083,706) to row 729.
	/// </summary>
	private static int LegEndRow(BotContext ctx, TilePos t) => GoalResolver.StandNear(ctx.Grid, t, ctx.Physics, ctx.Moves.CanBreak).Feet.Y;
	public ExploreBias Bias { get; }
	public int Legs => legs;
	/// <summary>Legs whose walk ended away from where it began (not failed at once, not gone nowhere): ground looked at.</summary>
	public int LegsWalked { get; private set; }
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
			TilePos? frontier = PickFrontier(ctx, node, aheadOnly: true, out float frontierScore);
			if (frontier == null)
				frontier = PickFrontier(ctx, node, aheadOnly: false, out frontierScore);
			// Digging on: straight down for ores and caves (2 blocks a tile; a diagonal through stone was 60-70 blocks a
			// leg in game), a level tunnel along the surface.
			// Not deeper than the gear allows (BotContext.DeepestRow): sideways once at the floor.
			bool down = Bias == ExploreBias.Down && !downBlocked && node.Y + FallbackDepth <= Deepest(ctx);
			// Below the depth the gear allows (fell in, chased, knocked down): dig back up first. Seen in game: at row 480
			// with a floor of 441 every leg was a level tunnel at that depth, and skeletons killed the bot there. A full leg
			// up, not just to the floor: 4 rows below it, "up to the floor" was within the walk's tolerance, and 14 legs in
			// a row ended at once without a step (world_eye run 36).
			bool tooDeep = node.Y > Deepest(ctx);
			if (frontier != null && DigIntoBandFirst(ctx, node, down, tooDeep, frontierScore))
				frontier = null;
			legWasFallback = frontier == null;
			legDown = frontier == null && down;
			TilePos t;
			if (frontier is TilePos f)
				t = f;
			else {
				TilePos ahead = DigAhead(node, down, tooDeep);
				DigPick? open = PickDig(ctx, node, ahead, tooDeep, out _);
				// Every way off limits (inside a biome zone): give up rather than dig on into it. Seen in game (world_eye run
				// 59): legs went on through the underground desert after an antlion charger was seen, and the bot died there.
				// Not for a sighting's zone in full armour: that is a cost (BotContext.OffLimitsOutright).
				if (open == null && ctx.OffLimitsOutright(ahead))
					return Fail(ctx, "every way from here is off limits");
				// Every way barred, some of it found unreachable: this way is done, and no leg goes to a barred target after
				// all. Seen in game (measured run U22): from (2155,425), with (2180,425) found unreachable and the other way in
				// a crowd's zone, the unreachable one was taken anyway, 9 times across 4 explorations, each "no path" after
				// 4001 nodes; those explorations used up all 16 legs.
				if (open is not DigPick pick) {
					ctx.Log.Info($"Explore: every way to dig on from {node} is barred (off limits, or found unreachable); nothing left this way after {legs} leg(s)");
					return Fail(ctx, $"no {What}: every way to dig on from {node} is barred");
				}
				// The best way on is back to ground that legs went to and that has nothing unseen round it: going there shows
				// nothing new, nor do the ways that score worse. End now as fruitless, so the caller (MineSkill's count of
				// explorations, the planner's travel and depth steps) moves on, rather than using up the legs. Not when too
				// deep (that leg up is for safety), and only exploring down: along the surface, seen ground is the way across.
				// Seen in game (measured run U22): 7 explorations in a row bounced 16 legs each between (2004,472) and
				// (2033,472), 0.7 game hours each; 126 of 395 legs went back within 5 tiles of the leg before last (U19: 13%).
				// Nor when the best way is down through such ground, with room below it within the gear's own floor: that way leads
				// on to fresh ground under it (PickDig carries a way down on through it within a leg; past a leg, this goes a dig at
				// a time). Seen in game (measured run U27, d4 +0.5h, full armour, the gear's floor row 601): from the spawn, "the best
				// way on from (2058,346), (2056,366), goes back to ground explored and seen already", three crystal explorations
				// ended after 2, 0 and 0 legs, within 1.4 seconds, and the bot went to new ground at (1975,280); the ore phase had
				// explored round the spawn down to about row 450, and six crystals lay under it at rows 525-602 (U24-U26).
				if (pick.SeenAndVisited && Bias == ExploreBias.Down && !tooDeep && !DownWithRoomBelow(ctx, node, pick.At)) {
					ctx.Log.Info($"Explore: the best way on from {node}, {pick.At}, goes back to ground explored and seen already; nothing new this way: ending after {legs} leg(s)");
					return Fail(ctx, $"no {What}: every way on from {node} is over ground explored already");
				}
				t = pick.At;
			}
			legs++;
			legTarget = t;
			ctx.Explored.Add(t);
			if (t.X != node.X)
				heading = Math.Sign(t.X - node.X);
			ctx.LastExplore = (node, heading); // the next exploration from near here carries on this way (BotContext.ExploreSide)
			ctx.Log.Info($"Explore: leg {legs} to {t} ({(frontier != null ? "frontier" : "digging on")}) from {node}");
			walk = new WalkToSkill(t) { Quiet = true, Tolerance = LegTolerance, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
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
		if (ctx.Node.ChebyshevTo(legFrom) > 1)
			LegsWalked++;
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

	/// <summary>
	/// A leg's walk ends within this many tiles of its target (<see cref="WalkToSkill.Tolerance"/>), so a dig target this
	/// close to one found unreachable is the same target. Seen in game (measured run U22): leg 1 to (2181,425) ended "no
	/// path", and leg 5 of the same exploration went to (2180,425), one column off, and ended the same way.
	/// </summary>
	public const int LegTolerance = 3;

	/// <summary>Where digging on goes first: up when too deep, down when it may, else level, a step aside in the heading.</summary>
	private TilePos DigAhead(TilePos node, bool down, bool tooDeep)
	{
		int dy = tooDeep ? -FallbackDepth : down ? FallbackDepth : 0;
		return new TilePos(node.X + heading * (dy != 0 ? FallbackSideways : FallbackStep), node.Y + dy);
	}

	/// <summary>
	/// Above the <see cref="Band"/> (exploring down, room to dig): does digging on down score better than the best frontier
	/// in view (<paramref name="frontierScore"/>, the same terms)? Elsewhere a frontier comes before digging blind (caves
	/// first, measured run S1), but above the band its rows are what is not looked for, and a frontier along the surface
	/// only leads along it. Seen in game (measured run U28): 88 of 133 crystal legs ended at rows 300-349, above the band's
	/// top (row 375), most of them frontiers along the surface, 16 legs from (2346,298) to (2683,348); U27 54 of 145; U25,
	/// whose search began in the band, 1 of 36. A cave frontier going down still wins (its depth pulls as a dig's does).
	/// </summary>
	private bool DigIntoBandFirst(BotContext ctx, TilePos node, bool down, bool tooDeep, float frontierScore)
	{
		if (!down || tooDeep || Band is not (int top, _) || node.Y >= top)
			return false;
		return PickDig(ctx, node, DigAhead(node, down, tooDeep), tooDeep, out float digScore) is DigPick dig
			&& !dig.SeenAndVisited && dig.At.Y > node.Y && digScore < frontierScore;
	}

	/// <summary>The deepest row a way down may go through ground explored already: the exploration's floor or the gear's own, the higher.</summary>
	private int ThroughFloor(BotContext ctx) => Math.Min(Deepest(ctx), ctx.DeepestRow);

	/// <summary>Is <paramref name="t"/> a way down from <paramref name="n"/> that ends within the gear's own floor, with a dig's depth more below it?</summary>
	private bool DownWithRoomBelow(BotContext ctx, TilePos n, TilePos t) =>
		t.Y > n.Y && LegEndRow(ctx, t) + FallbackDepth <= ThroughFloor(ctx);

	/// <summary>
	/// A way down <paramref name="w"/> over ground explored and seen already (near an earlier leg's target, nothing unseen
	/// round it), carried on down a dig's depth at a time to the first spot that is not, within a leg of
	/// <paramref name="n"/> (<see cref="MaxLegTiles"/>) and the gear's own floor (<see cref="ThroughFloor"/>; below it, as
	/// in measured run U26's deaths at rows 720-772, ground explored already still ends the exploration). Null if none.
	/// </summary>
	private TilePos? ThroughExplored(BotContext ctx, TilePos n, TilePos w)
	{
		if (w.Y <= n.Y || !NearVisited(ctx, w.X, w.Y) || NearUnseen(ctx.World, w.X, w.Y))
			return null;
		int floor = ThroughFloor(ctx);
		for (int y = w.Y + FallbackDepth; y - n.Y <= MaxLegTiles && y <= floor; y += FallbackDepth) {
			var c = new TilePos(w.X, y);
			if (Barred(ctx, c) || LegEndRow(ctx, c) > floor)
				return null;
			if (!NearVisited(ctx, c.X, c.Y) || NearUnseen(ctx.World, c.X, c.Y))
				return c;
		}
		return null;
	}

	private static bool Barred(BotContext ctx, TilePos t) => ctx.OffLimitsOutright(t) || UnreachableNear(ctx, t);

	private static bool UnreachableNear(BotContext ctx, TilePos t)
	{
		for (int dx = -LegTolerance; dx <= LegTolerance; dx++)
			for (int dy = -LegTolerance; dy <= LegTolerance; dy++)
				if (ctx.UnreachableTiles.Contains(new TilePos(t.X + dx, t.Y + dy)))
					return true;
		return false;
	}

	/// <summary>
	/// The way from <paramref name="n"/> towards <paramref name="o"/> as far as the edge of the off-limits ground it runs
	/// into, if that is a leg's worth (<see cref="MinLegTiles"/>, as for frontiers) and not barred itself; else null.
	/// Seen in game (measured run U22): at (2029,472) the dig on, (2054,472), was in the zone of a death at (2073,487), which
	/// starts at column 2043; with no step at all that way, every leg went back west through the 30-column tunnel.
	/// </summary>
	private static TilePos? ToZoneEdge(BotContext ctx, TilePos n, TilePos o)
	{
		int d = n.ChebyshevTo(o);
		for (int k = 1; k <= d; k++) {
			var p = new TilePos(n.X + (o.X - n.X) * k / d, n.Y + (o.Y - n.Y) * k / d);
			if (!ctx.OffLimitsOutright(p))
				continue;
			if (k - 1 < MinLegTiles)
				return null;
			var edge = new TilePos(n.X + (o.X - n.X) * (k - 1) / d, n.Y + (o.Y - n.Y) * (k - 1) / d);
			return Barred(ctx, edge) ? null : edge;
		}
		return null;
	}

	/// <summary>A dig target, and whether it is both near an earlier leg's target and has nothing unseen round it.</summary>
	private readonly record struct DigPick(TilePos At, bool SeenAndVisited);

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
	/// <para>A way into off-limits ground is cut short at its edge (<see cref="ToZoneEdge"/>). Exploring down and unable to
	/// go deeper (the gear's depth floor, or down found blocked), up a dig is a way too, at the cost of the downward pull per
	/// row: fresh ground level still wins, but ground dug and seen already loses to it. Seen in game (measured run U22): 89%
	/// of the 395 legs were at rows 400-499, 0.39 ore targets a leg, while rows 325-399 had 5.6% of them and 2.6 a leg (58 of
	/// 204); at the floor the bot tunnelled to and fro at row 472 for 7 explorations with no way up offered.</para>
	/// </summary>
	private DigPick? PickDig(BotContext ctx, TilePos n, TilePos ahead, bool tooDeep, out float score0)
	{
		var ways = new List<TilePos> {
			ahead,
			new TilePos(n.X - (ahead.X - n.X), ahead.Y),
			new TilePos(n.X + heading * FallbackStep, n.Y),
			new TilePos(n.X - heading * FallbackStep, n.Y),
		};
		if (Bias == ExploreBias.Down && !tooDeep && ahead.Y == n.Y) {
			ways.Add(new TilePos(n.X + heading * FallbackSideways, n.Y - FallbackDepth));
			ways.Add(new TilePos(n.X - heading * FallbackSideways, n.Y - FallbackDepth));
		}
		// Each way followed by the same cut short at off-limits ground's edge, so too deep, a shortened way up still comes
		// before the level ones.
		var options = new List<TilePos>();
		foreach (TilePos w in ways) {
			if (options.Contains(w))
				continue;
			options.Add(w);
			if (ctx.OffLimitsOutright(w) && ToZoneEdge(ctx, n, w) is TilePos edge && !options.Contains(edge))
				options.Add(edge);
			// A way down over ground explored already: on through it to the fresh ground below (ThroughExplored).
			if (Bias == ExploreBias.Down && !tooDeep && ThroughExplored(ctx, n, w) is TilePos deeper && !options.Contains(deeper))
				options.Add(deeper);
		}
		DigPick? best = null;
		float bestScore = float.MaxValue;
		foreach (TilePos o in options) {
			// Too deep: up first, level only with both ways up barred (skeletons killed the bot tunnelling below the floor).
			if (Barred(ctx, o) || tooDeep && o.Y == n.Y && best != null)
				continue;
			float score = o.ChebyshevTo(n) + ctx.SightingDepth(o) * SightingPenalty;
			bool visited = NearVisited(ctx, o.X, o.Y), seen = !NearUnseen(ctx.World, o.X, o.Y);
			if (visited)
				score += VisitedPenalty;
			if (seen)
				score += SeenPenalty;
			if (Math.Sign(o.X - n.X) != heading)
				score += TurnPenalty;
			// The depth terms by where the leg ends: in the open, on the floor below (LegEndRow).
			int endY = LegEndRow(ctx, o);
			score += BandCost(endY);
			if (tooDeep)
				score -= (n.Y - endY) * DownPullPerRow; // higher is better
			else if (Bias == ExploreBias.Down)
				score += DepthScore(ctx, n.Y, endY); // as for frontiers
			if (score < bestScore) {
				bestScore = score;
				best = new DigPick(o, visited && seen);
			}
		}
		score0 = bestScore;
		return best;
	}

	/// <summary>The best frontier in view, or null; <paramref name="bestScore"/> its score (float.MaxValue if none).</summary>
	private TilePos? PickFrontier(BotContext ctx, TilePos n, bool aheadOnly, out float bestScore)
	{
		IWorldInfo w = ctx.World;
		ITileGrid g = ctx.Grid;
		TilePos? best = null;
		bestScore = float.MaxValue;
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
					score += DepthScore(ctx, n.Y, y); // deeper is better, down to the band (DepthScore)
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
