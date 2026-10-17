using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Get <see cref="Amount"/> of an item by mining the tile type that drops it (stone, ores):
/// <list type="number">
/// <item>pick up matching drops lying around;</item>
/// <item>target the nearest matching tile within <see cref="SearchRadius"/> that is safe to break (same rules as
/// digging moves: no lava neighbour, no unsupported sand above), walk until it is in tool range
/// (<see cref="GoalReachTile"/>), and swing the pickaxe at it until it is gone;</item>
/// <item>with nothing in sight, dig down <see cref="ExploreDepth"/> rows (zig-zagging sideways) to look further.</item>
/// </list>
/// A block that is everywhere (<see cref="Bulk"/>: stone, dirt, ash) is quarried: every tile of it in the pickaxe's reach from
/// where the bot stands is mined first, one after another with no walk or pickup in between (<see cref="InReach"/>), and only
/// then are the drops collected and the next tile walked to.
/// Vanilla has no line-of-sight rule for pickaxes: any tile in range can be mined.
/// </summary>
public sealed class MineSkill : ISkill
{
	public const int SearchRadius = 40;
	/// <summary>
	/// Ore of the same kind this close (tiles) to the last one mined is mined too once the amount is in, up to
	/// <see cref="MaxVeinExtra"/> tiles: a player finishes the vein. Seen in game (user report, 2026-10-14): it mined
	/// what the first item needed and left the rest, and later explored elsewhere instead of coming back for it.
	/// </summary>
	public const int VeinRadius = 5;
	public const int MaxVeinExtra = 30;
	/// <summary>
	/// With nothing within <see cref="SearchRadius"/>: ore the bot has seen before (still on its map) this far away is
	/// gone back to before exploring new ground, as a player looks at the map.
	/// </summary>
	public const int MapSearchRadius = 200;
	public const int MaxMineTicksPerTile = 60 * 8;
	public const int ExploreDepth = 15;
	public const int ExploreSideways = 6;
	/// <summary>Explorations (each up to <see cref="ExploreSkill.MaxLegs"/> legs) before giving up.</summary>
	public const int MaxExplores = 3;
	public const float UnderFootPenalty = 30f;
	public const int CollectRadius = 12;
	/// <summary>Unreachable targets in a row before giving up (nearby tiles tend to be unreachable for the same reason).</summary>
	public const int MaxUnreachableInARow = 4;
	/// <summary>Same-type tiles this close to an unreachable target are skipped too.</summary>
	public const int UnreachableClusterRadius = 4;
	/// <summary>A search for a mining spot gives up sooner than a general walk: there are other tiles to try.</summary>
	/// <summary>
	/// 4,000, not 12,000: seen in game (measured run G2), 42 searches for spots 10-20 tiles off that could not be reached each
	/// ran all 12,000 nodes (over 1 s, the bot standing still), and the relaxed retry did it again. A player gives up on a
	/// dig spot sooner and tries another; farther targets are walked in segments (SearchLimits.SegmentAfter).
	/// </summary>
	public static readonly SearchLimits ReachLimits = new() { MaxExpansions = 4_000 };

	private readonly HashSet<TilePos> bad = new();
	private readonly HashSet<int> ignoredDrops = new();
	private ISkill sub;
	private TilePos? target;
	private int mineTicks;
	private int lastHave;
	private int unreachableInARow;
	private TilePos? lastMined;
	/// <summary>Vertical speed (px/tick) under which the body counts as held still for mining though not grounded.</summary>
	public const float HeldStillVY = 0.5f;
	private int veinExtra;
	/// <summary>
	/// The tile the vein step picked (the done check's own, within <see cref="VeinRadius"/> of the last one mined): only
	/// while that is the target is the done check skipped. A flag for "veining" skipped it for any target, and after each
	/// tile's drops were collected the next was picked by the usual search (any ore within <see cref="SearchRadius"/>), not by
	/// the vein step, so <see cref="MaxVeinExtra"/> was never counted past the first: unit tests with an Amount of 3 mined 48
	/// and 63 tiles, all the ore in sight.
	/// </summary>
	private TilePos? veinTarget;

	public int ItemType { get; }
	public int TileType { get; }
	public int Amount { get; }
	/// <summary>
	/// Other ores that would do as well (another tier's gear): with none of this ore in sight but one of these, its vein is
	/// mined and the job ends, so the planner picks again with it in the bag (see ProgressionPlanner.AltOres).
	/// </summary>
	public IReadOnlyList<(int Item, int Tile)> Also { get; init; } = Array.Empty<(int, int)>();
	/// <summary>
	/// Only tiles of this frame column drop the item (ItemSource.FrameX; -1: any). A corrupt plant (tile 24) drops a vile
	/// mushroom only at frame 144 (WorldGen.cs:59639); the other plants on the grass beside it drop nothing.
	/// </summary>
	public int FrameX { get; init; } = -1;
	/// <summary>
	/// Which way exploring goes when none is in sight: down into the ground for ore and crystals; along the surface for
	/// what grows on it (vile mushrooms on corrupt grass).
	/// </summary>
	public ExploreBias Bias { get; init; } = ExploreBias.Down;
	/// <summary>
	/// What grows back on the evil biome's surface (vile mushrooms on corrupt grass): with none in sight or on the map, the
	/// job cuts the other plants of <see cref="TileType"/> and the corrupt thorns on the grass the map shows, where they are
	/// (<see cref="CutPlantsSkill"/>), so their grass tops grow again; with none to cut, it walks the known stretch of that
	/// surface end to end and back (<see cref="PatrolSkill"/>) for up to <see cref="MaxPatrolPasses"/> passes, before
	/// exploring new ground. Seen in game (measured run M10b): the mushrooms the bot got were ones grown back in the corruption it had
	/// seen ((1931,244), (1914,244), "on the map"), while its "Sideways" explorations went out to (1625,268), 140 tiles
	/// west of the corruption (x 1765-1960), and found none: 2 of 6 at day 2 +20h, 4 at day 5 +4h. In measured run M10a,
	/// 222 such explorations, and the sixth mushroom 49 game hours after the rotten chunks (day 5 +10h).
	/// </summary>
	public bool PatrolEvilSurface { get; init; }
	/// <summary>
	/// Passes along the known evil surface with none found before the job explores instead (BotContext.PatrolPasses).
	/// An 80-tile stretch with its plants cut grows about 0.13 mushrooms a game hour (PatrolSkill's remarks), and a pass
	/// of it takes about 0.47 game hours at the 21 ticks a tile the bot walked the corruption in measured run M10b
	/// ((1875,291) at tick 100800 to (1788,298) at 102600, 87 tiles in 1800 ticks): 0.06 a pass. 48 passes are 2.9
	/// mushrooms expected, so none in them (e^-2.9, 6%) says nothing is growing back there.
	/// </summary>
	public const int MaxPatrolPasses = 48;
	/// <summary>Passes in a row that could not walk to their end before the job explores instead.</summary>
	public const int MaxPatrolFails = 2;
	private int patrolFails;
	private TilePos? patrolFailedAt;
	/// <summary>The ore being mined when it is one of <see cref="Also"/>; null for this job's own.</summary>
	private (int Item, int Tile)? alt;
	private bool tookAlt;
	/// <summary>
	/// The <see cref="Also"/> ore this job mined instead of its own (null if none), and the tiles of its own it mined: the
	/// planner clears the fruitless count of the ore actually found (ProgressionPlanner.OnSkillFinished).
	/// </summary>
	public int? OtherOreTaken { get; private set; }
	public int MinedOwn { get; private set; }
	/// <summary>
	/// The ore and its <see cref="Also"/> ores: the tier group. An exploration's "found" check is <see cref="PickTarget"/>,
	/// which takes any of them, so an exploration that found none found none of the group: it counts against each
	/// (<see cref="BotContext.ExploresWithoutOre"/>, and the planner's fruitless count), and ore found clears them all.
	/// Seen in game (measured run U22): counted per ore, silver's fruitless count (from a silver job's failure at t235652)
	/// stayed while iron's was cleared, so silver ore cost twice iron's, and at d4 +5.5h the plan dropped "Silver Ore x68
	/// (have 28)", 40 short, for "Iron Ore x60 (have 3)", 57 short; 15 such switches over 47 game hours, the stock split at
	/// iron 30/60 and silver 34/68 for 31 hours, and 66 ore left over at full armour.
	/// </summary>
	public IEnumerable<int> TierGroup => Also.Select(a => a.Item).Prepend(ItemType).Distinct();
	/// <summary>
	/// Failed for want of ore after explorations that ran at the gear's depth floor (the deepest row they reached within
	/// <see cref="ExploreSkill.FallbackDepth"/> of <see cref="BotContext.DeepestRow"/>, where they dig no deeper): the ground
	/// within the floor is looked through, and the planner goes a step deeper before a trip to new ground
	/// (ProgressionPlanner.TravelForOre). Ore only, not a life crystal (its own floor, ValuableDepthMargin deeper).
	/// </summary>
	public bool ExploredToFloor { get; private set; }
	/// <summary>The deepest row this job's explorations took the bot to (int.MinValue: none yet).</summary>
	private int deepestExplored = int.MinValue;
	public int Mined { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Mine";
	public string Key => $"Mine {ItemType} {TileType} {Amount}";

	public MineSkill(int itemType, int tileType, int amount)
	{
		ItemType = itemType;
		TileType = tileType;
		Amount = amount;
	}

	public string Describe() => $"Mine tile {TileType} for item {ItemType} {lastHave}/{Amount} [{State}]" + (sub != null ? " > " + sub.Describe() : "");

	public void Start(BotContext ctx)
	{
		ctx.Log.Info($"Mine start: {Label(ctx)} x{Amount} (have {ctx.Inventory.Count(ItemType)}), pick power {ctx.PickPower}");
	}

	public void Stop(BotContext ctx)
	{
		if (sub is ExploreSkill cut)
			NoteCutExploration(ctx, cut.LegsWalked);
		sub?.Stop(ctx);
		sub = null;
	}

	/// <summary>
	/// The job is stopped in the middle of an exploration (something else came first: a chest, a recall, the plan after a
	/// fight): one that walked legs to new ground has not looked through its ground, and the next job's exploration goes on
	/// from where it left off, so it is taken off the count of fruitless ones (BotContext.ExploresWithoutOre) while the legs
	/// of such explorations add up to less than one whole (ExploreSkill.MaxLegs; BotContext.ExploreLegsCut), so the give-up
	/// still comes after as many legs. One that walked none still counts (world_eye run 42: a fight every few seconds
	/// restarted the job and with it the same first leg for 12 game hours). Seen in game (measured run U29, d3 +3.7h to
	/// +13.5h): the crystal search near the spawn walked 10 legs into the band before a recall cut it off and 4 more before a
	/// chest did, both counted whole, and the next job failed "after 3 explorations in a row" and went to new ground at
	/// (1735,269), off limits there; U28 (d3 +16.1h) likewise after 3 legs cut off by a chest.
	/// </summary>
	private void NoteCutExploration(BotContext ctx, int legsWalked)
	{
		if (legsWalked <= 0)
			return;
		int cut = TierGroup.Max(i => ctx.ExploreLegsCut.GetValueOrDefault(i)) + legsWalked;
		bool whole = cut >= ExploreSkill.MaxLegs;
		foreach (int i in TierGroup) {
			ctx.ExploreLegsCut[i] = whole ? cut - ExploreSkill.MaxLegs : cut;
			if (!whole && ctx.ExploresWithoutOre.TryGetValue(i, out int n)) {
				if (n <= 1)
					ctx.ExploresWithoutOre.Remove(i);
				else
					ctx.ExploresWithoutOre[i] = n - 1;
			}
		}
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		sub?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		sub?.Resume(ctx);
	}

	private string Label(BotContext ctx) => ctx.Knowledge?.ItemName(ItemType) ?? $"item {ItemType}";

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		int have = ctx.Inventory.Count(ItemType);
		lastHave = have;
		// Done once the amount is in, unless finishing the vein (the vein step's target set: carry on with that one; any
		// other target is dropped for the vein step's, so each tile past the amount counts against MaxVeinExtra). Ore only: a
		// "vein" of stone or dirt is the whole world (a settle scenario mined 776 blocks and never finished).
		if (have >= Amount && sub is not CollectDropsSkill && !(target != null && target == veinTarget)) {
			bool ore = !Bulk(ctx);
			if (ore && veinExtra < MaxVeinExtra && lastMined is TilePos last && Nearest(ctx, last, VeinRadius) is TilePos more) {
				if (veinExtra == 0)
					ctx.Log.Info($"Mine: {Label(ctx)} {have}/{Amount}; finishing the vein first");
				veinExtra++;
				sub?.Stop(ctx);
				sub = null;
				target = veinTarget = more;
				mineTicks = 0;
				arrivedOutOfReach = 0;
				State = "finishing the vein";
				return SkillStatus.Running;
			}
			sub?.Stop(ctx);
			sub = null;
			// The amount is in: no fruitless explorations carried to the next job. Mining a tile clears them, but the drop
			// reaches the bag a tick or more after it leaves the world, and in that gap the job, its drop collected and none
			// in sight, counted one more and began exploring (measured runs U25-U29: "Mine: no Life Crystal seen within 40
			// tiles; exploring" between the pickup and "Mine done" after every crystal, U25 02:54:54.344); the next crystal
			// job began at 1 of its MaxExplores.
			ClearExplores(ctx);
			ctx.Log.Info($"Mine done: {Label(ctx)} {have}/{Amount}, mined {Mined} tile(s)");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (ctx.PickPower <= 0)
			return Fail(ctx, "no pickaxe in the hotbar");

		if (target is TilePos t)
			return TickTarget(ctx, t);

		// On the way back to where the crystal search left off, a crystal coming into sight is gone for at once.
		if (sub != null && ReferenceEquals(sub, resumeWalk) && ctx.Tick % ExploreSkill.FoundCheckEvery == 0 && PickTarget(ctx) != null) {
			sub.Stop(ctx);
			sub = null;
			resumeWalk = null;
		}
		if (sub != null) {
			SkillStatus s = sub.Tick(ctx);
			if (sub is ExploreSkill)
				deepestExplored = Math.Max(deepestExplored, ctx.Node.Y);
			if (Valuable(ctx) && sub is ExploreSkill)
				NoteCrystalSearch(ctx);
			if (s == SkillStatus.Running)
				return SkillStatus.Running;
			if (ReferenceEquals(sub, resumeWalk)) {
				// No way back there (not a death on the way): not that spot again; the search goes on from here.
				if (s == SkillStatus.Failed && !string.IsNullOrEmpty(sub.FailureReason) && !sub.FailureReason.Contains("died")) {
					ctx.CrystalSearch.RemoveAll(c => c.ChebyshevTo(resumeFrom) <= ExploreSkill.FrontierReach);
					ctx.Log.Info($"Mine: no way back to {resumeFrom}, where the crystal search left off ({sub.FailureReason}); exploring from here");
				}
				resumeWalk = null;
			}
			if (sub is PatrolSkill patrol) {
				if (s == SkillStatus.Failed) {
					patrolFailedAt = patrol.End;
					ctx.Log.Info($"Mine: patrol for {Label(ctx)} failed ({patrol.FailureReason})");
					if (++patrolFails >= MaxPatrolFails)
						ctx.PatrolPasses[ItemType] = MaxPatrolPasses; // no way along it: explore instead
				}
				else
					patrolFails = 0;
			}
			sub.Stop(ctx);
			sub = null;
		}

		// A block that is everywhere: what is in reach from here first, before the drops and before any walk (InReach).
		if (InReach(ctx, out int inReach) is TilePos near) {
			ctx.Log.Debug($"Mine: {inReach} {Label(ctx)} in reach from {ctx.Node}; mining them from here, nearest first ({near})");
			target = near;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			State = $"mining {near}";
			return SkillStatus.Running;
		}
		if (CollectDropsSkill.AnyInRange(ctx, d => d.Type == ItemType && !ignoredDrops.Contains(d.Id), CollectRadius)) {
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, CollectRadius, ignoredDrops), "collecting");
			return SkillStatus.Running;
		}
		// An other tier's vein mined (Also): finish it, then hand back to the planner, which picks the tier again.
		if (tookAlt && target == null && alt is (int altItem, int altTile)) {
			if (veinExtra < MaxVeinExtra && lastMined is TilePos lm && NearestOf(ctx, altTile, lm, VeinRadius, out _) is TilePos moreAlt) {
				veinExtra++;
				target = moreAlt;
				mineTicks = 0;
				arrivedOutOfReach = 0;
				State = "finishing the vein";
				return SkillStatus.Running;
			}
			ctx.Log.Info($"Mine done: took {ctx.Knowledge?.ItemName(altItem)} instead of {Label(ctx)} (mined {Mined} tile(s)); the plan picks the tier again");
			State = "done (other ore)";
			return SkillStatus.Succeeded;
		}
		if (PickTarget(ctx) is TilePos next) {
			target = next;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			ctx.Log.Debug($"Mine: target {next}");
			return SkillStatus.Running;
		}
		// A life crystal remembered, however far (BotContext.KnownCrystals): back for it before any new ground, and before the
		// count of fruitless explorations (going for a crystal known is not one).
		if (Valuable(ctx) && RememberedCrystal(ctx, TileType, ctx.Node, bad.Contains) is TilePos remembered) {
			target = remembered;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			ctx.Log.Info($"Mine: no {Label(ctx)} within {SearchRadius} tiles, but one remembered at {remembered} ({remembered.ChebyshevTo(ctx.Node)} tiles away); going back for it");
			return SkillStatus.Running;
		}
		// A crystal search that got into the band before (BotContext.CrystalSearch): back there first, once a job, not looking
		// again from the spawn and the rows above the band; not counted as an exploration.
		if (Valuable(ctx) && !walkedBack && SearchToResume(ctx) is TilePos from) {
			walkedBack = true;
			resumeFrom = from;
			resumeWalk = new WalkToSkill(new GoalNearTile(from, ExploreSkill.FrontierReach, ctx.Physics)) { Quiet = true };
			ctx.Log.Info($"Mine: no {Label(ctx)} in sight; back to {from} ({from.ChebyshevTo(ctx.Node)} tiles away), where the crystal search left off in the crystal band, before exploring");
			Begin(ctx, resumeWalk, $"back to {from}, where the crystal search left off");
			return SkillStatus.Running;
		}
		// What grows back on the evil surface: the stretch of it the map shows is walked again, not new ground (PatrolEvilSurface).
		if (PatrolEvilSurface && Patrol(ctx))
			return SkillStatus.Running;
		// Counted on the context per item: a fight pauses the skill and the planner starts it afresh, and a count of its
		// own went back to 0 each time (world_eye run 42: 12 game hours of the same first leg, never reaching the give-up).
		// Against every ore of the tier group (TierGroup): the exploration looks for all of them.
		int explores = 1 + TierGroup.Max(i => ctx.ExploresWithoutOre.GetValueOrDefault(i));
		foreach (int i in TierGroup)
			ctx.ExploresWithoutOre[i] = explores;
		if (explores > MaxExplores) {
			// Given up: the next job (after travelling to new ground) starts counting afresh. Kept, every later job failed at
			// once, each a "fruitless" mine, and the travel target went out 120 tiles further each time (1,900 in run 47).
			ClearExplores(ctx);
			ctx.PatrolPasses.Remove(ItemType); // and the next job walks the evil surface again first
			int floor = ctx.DeepestRow;
			ExploredToFloor = !Valuable(ctx) && Bias == ExploreBias.Down && floor < int.MaxValue
				&& Math.Max(deepestExplored, ctx.Node.Y) + ExploreSkill.FallbackDepth > floor;
			return Fail(ctx, $"no {Label(ctx)} found after {MaxExplores} explorations in a row" + (ExploredToFloor ? $", down to the depth floor (row {floor})" : ""));
		}
		// Standing somewhere off limits (a biome zone, the underground desert): no exploring here, but somewhere new. Seen in
		// game (world_eye run 59): explorations in the underground desert went on until antlion chargers killed the bot.
		// The planner walks the bot out (ProgressionPlanner.LeaveOffLimits).
		if (ctx.OffLimitsOutright(ctx.Node)) {
			ClearExplores(ctx);
			return Fail(ctx, $"no {Label(ctx)} seen, and the explorations from here would be off limits");
		}
		// In a zone where monsters were seen, in full armour: a cost, not a ban, so the exploring weighs its way out (leg
		// targets score worse towards the zone's centre, ExploreSkill.SightingPenalty). Seen in game (measured run U9): 17
		// crystal jobs failed here at once in sighting zones the bot could have walked out of.
		if (ctx.OffLimits(ctx.Node))
			ctx.Log.Info($"Mine: in a zone where monsters were seen at {ctx.Node}; exploring out of it in full armour");
		// Ore seen before and still on the map: back for it before new ground (a player looks at the map). Not stone or dirt:
		// those are everywhere. Ore of the tier group too (Also): the explorations count against it as well (TierGroup), so
		// an iron job leaving silver on the map would send the plan on a trip past it.
		if (!Bulk(ctx) && OnTheMap(ctx, out (int Item, int Tile)? other) is TilePos known) {
			target = known;
			alt = other;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			string some = other is (int otherItem, _) ? $"{ctx.Knowledge?.ItemName(otherItem) ?? $"item {otherItem}"}, which does as well," : "some";
			string depth = known.Y > ctx.DeepestRow ? $", {known.Y - ctx.DeepestRow} rows below the depth floor (row {ctx.DeepestRow})" : "";
			ctx.Log.Info($"Mine: no {Label(ctx)} within {SearchRadius} tiles, but {some} at {known} on the map ({known.ChebyshevTo(ctx.Node)} tiles away{depth}); going back for it");
			return SkillStatus.Running;
		}
		// Only tiles the player has seen count (no x-ray): go and look, down into caves and through the ground.
		ctx.Log.Info($"Mine: no {Label(ctx)} seen within {SearchRadius} tiles; exploring");
		// "Found" is what this skill would actually go for (not beside water, not given up on...): with "any seen", exploring
		// stopped at ore that mining then skipped, and mining explored again, round and round.
		// The first exploration goes the safer way (BotContext.SaferSide); later ones, in this job or the next, carry on the
		// way the last one was going (it turns round itself when blocked). Alternating, each walked back over the one before.
		int exploreDir = ctx.ExploreSide(ctx.Node);
		bool valuable = Valuable(ctx);
		bool deeper = valuable && ctx.Combat.FullArmour && ctx.DeepestRow < int.MaxValue - ValuableDepthMargin;
		Begin(ctx, new ExploreSkill(Label(ctx), c => PickTarget(c) != null, Bias, exploreDir) {
			DeepestRow = deeper ? ctx.DeepestRow + ValuableDepthMargin : null,
			Band = valuable && ctx.CrystalBandTop > 0 ? (ctx.CrystalBandTop, ctx.CrystalBandBottom) : null,
		}, "exploring");
		return SkillStatus.Running;
	}

	/// <summary>
	/// <see cref="PatrolEvilSurface"/>: one seen on the map (within <see cref="MapSearchRadius"/>) is gone for; else the
	/// plants and thorns on the corrupt grass the map shows are cut (CutPlantsSkill), however many passes there were; else
	/// (none to cut) a pass to the far end of the known evil surface round the bot (PatrolSkill.EvilSurfaceSpan), the other
	/// end if the last pass could not get to that one, up to <see cref="MaxPatrolPasses"/>. False when the known stretch is
	/// too small to wait on, or has no surface to stand on at its ends, or the passes are used up: the job explores instead.
	/// </summary>
	private bool Patrol(BotContext ctx)
	{
		if (Nearest(ctx, ctx.Node, MapSearchRadius) is TilePos known) {
			target = known;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			ctx.Log.Info($"Mine: no {Label(ctx)} within {SearchRadius} tiles, but some at {known} on the map ({known.ChebyshevTo(ctx.Node)} tiles away); going back for it");
			return true;
		}
		// The plants on the corrupt grass the map shows, cut where they are (CutPlantsSkill): the grass tops kept free are what
		// grows mushrooms, not the walking. The stretch is walked only with none to cut.
		if (ctx.AllowBreak && CutPlantsSkill.Next(ctx, ctx.Node, TileType, FrameX) is TilePos first) {
			var (toCut, mushrooms, free) = CutPlantsSkill.Survey(ctx, ctx.Node, TileType, FrameX);
			int growing = toCut + mushrooms;
			double perHour = CutPlantsSkill.MushroomsPerCellHour;
			ctx.Log.Info($"Mine: no {Label(ctx)} seen; cutting the {toCut} plant(s) and thorns on the corrupt grass the map shows within "
				+ $"{CutPlantsSkill.Radius} tiles, nearest first ({first}): kept cut, its {growing} grass tops that grow things (and {free} free ones, "
				+ $"some sloped or walled) give {growing * perHour * 24:0.0}-{(growing + free) * perHour * 24:0.0} {Label(ctx)} a game day "
				+ $"({perHour:0.0000} a grass top an hour, vanilla rates; the map looked at every {MapLookEvery * ExploreSkill.FoundCheckEvery} ticks)");
			// Found: one in sight, or (every MapLookEvery-th look) one grown anywhere the map shows: a round can take game hours.
			int looks = 0;
			bool found(BotContext c) => PickTarget(c) != null || ++looks % MapLookEvery == 0 && Nearest(c, c.Node, MapSearchRadius) != null;
			Begin(ctx, new CutPlantsSkill(Label(ctx), found) { PlantTile = TileType, KeepFrameX = FrameX }, "cutting plants");
			return true;
		}
		if (ctx.PatrolPasses.GetValueOrDefault(ItemType) >= MaxPatrolPasses || PatrolSkill.EvilSurfaceSpan(ctx, ctx.Node) is not (int x0, int x1))
			return false;
		TilePos? west = PatrolSkill.EndSpot(ctx, x0, 1), east = PatrolSkill.EndSpot(ctx, x1, -1);
		if (west is not TilePos w || east is not TilePos e)
			return false;
		TilePos far = Math.Abs(w.X - ctx.Node.X) >= Math.Abs(e.X - ctx.Node.X) ? w : e;
		if (patrolFailedAt == far)
			far = far == w ? e : w;
		patrolFailedAt = null;
		int pass = ctx.PatrolPasses[ItemType] = ctx.PatrolPasses.GetValueOrDefault(ItemType) + 1;
		ctx.Log.Info($"Mine: no {Label(ctx)} seen; along the evil surface x {x0}-{x1} to {far}, pass {pass}/{MaxPatrolPasses}, cutting its plants so new ones grow");
		Begin(ctx, new PatrolSkill(Label(ctx), far, c => PickTarget(c) != null) { CutTile = TileType, KeepFrameX = FrameX }, $"patrolling, pass {pass}");
		return true;
	}

	/// <summary>
	/// A cutting round looks at the whole map (<see cref="MapSearchRadius"/>) for the job's own on every this-many-th of its
	/// looks in sight (every ExploreSkill.FoundCheckEvery ticks): every 300 ticks, 5 seconds, against the 0.0016 mushrooms a
	/// grass top grows each game hour (CutPlantsSkill.MushroomsPerCellHour), one in 6 game hours for 100 grass tops.
	/// </summary>
	public const int MapLookEvery = 20;

	private bool walkedBack;
	private WalkToSkill resumeWalk;
	private TilePos resumeFrom;

	/// <summary>Farther than one leg's reach from where the crystal search left off, it is walked back to first (nearer, the legs get there).</summary>
	public const int ResumeBeyondTiles = ExploreSkill.MaxLegTiles;

	/// <summary>
	/// The exploration stood somewhere new in the crystal band: kept (BotContext.CrystalSearch), the oldest dropped past
	/// BotContext.CrystalSearchKept.
	/// </summary>
	private static void NoteCrystalSearch(BotContext ctx)
	{
		if (ctx.LastExplore is not { } last || !ctx.InCrystalBand(last.At.Y))
			return;
		List<TilePos> kept = ctx.CrystalSearch;
		if (kept.Count > 0 && kept[^1] == last.At)
			return;
		kept.Add(last.At);
		if (kept.Count > BotContext.CrystalSearchKept)
			kept.RemoveAt(0);
	}

	/// <summary>
	/// Where to walk back to before exploring for a crystal: the latest spot the crystal search stood at
	/// (BotContext.CrystalSearch) that is not deeper than a crystal is gone for, not off limits, not found unreachable and not
	/// within a leg (ExploreSkill.MaxLegTiles) of where the bot was killed lately (BotContext.KilledWithin); of those, one
	/// at or above the gear's own floor (BotContext.DeepestRow) before one below it, the fewest rows below it first (a cost,
	/// not a ban). Not the deepest: a crystal is as likely at any row of the band (ExploreSkill.DepthScore). Null if none, or
	/// if it is within <see cref="ResumeBeyondTiles"/>: the search starts afresh from where the bot is.
	/// <para>Seen in game (measured run U26, the gear's floor row 626): after each of four deaths at rows 720-772 the bot
	/// walked back 396-424 tiles to the deepest spot, (2113,682), (2103,706), (2106,706), (2114,683), 39-42 tiles from the
	/// latest death (outside its 30-tile zone, within one leg of it), and the first legs from there met the same wall
	/// creepers again.</para>
	/// </summary>
	public static TilePos? SearchToResume(BotContext ctx)
	{
		int deepest = ctx.Combat.FullArmour && ctx.DeepestRow < int.MaxValue - ValuableDepthMargin ? ctx.DeepestRow + ValuableDepthMargin : ctx.DeepestRow;
		TilePos? best = null;
		int bestBelow = int.MaxValue;
		foreach (TilePos c in ctx.CrystalSearch) { // oldest first: the latest of equals wins
			if (c.Y > deepest || ctx.OffLimits(c) || ctx.UnreachableTiles.Contains(c) || ctx.KilledWithin(c, ExploreSkill.MaxLegTiles))
				continue;
			int below = c.Y > ctx.DeepestRow ? c.Y - ctx.DeepestRow : 0;
			if (below <= bestBelow) {
				best = c;
				bestBelow = below;
			}
		}
		return best is TilePos b && b.ChebyshevTo(ctx.Node) > ResumeBeyondTiles ? best : null;
	}

	private SkillStatus TickTarget(BotContext ctx, TilePos t)
	{
		if (!StillThere(ctx, t)) {
			// Gone before the pickaxe touched it, out of collecting's reach: its drop, if it left one, lies where it was. A plant
			// is cut by any friendly projectile through it (Projectile.CutTilesAt), the bot's own missed arrows too, and drops its
			// item there. Seen in game (measured run M10p): four vile mushrooms on the map gone each time the bot neared them,
			// none picked in 1.75 game days; diagnostic run D1mush: "Vile Mushroom at (1814,286) destroyed by the bot's
			// projectile Wooden Arrow; the bot -66,-3 tiles off". So the walk goes on to where it was, and the drop there is
			// in collecting's reach (a player sees it lying there on arrival).
			if (mineTicks == 0 && t.ChebyshevTo(ctx.Node) > CollectRadius / 2) {
				ctx.Log.Info($"Mine: the {Label(ctx)} at {t} is gone before we got to it (now tile {ctx.World.TileTypeAt(t.X, t.Y)}), "
					+ $"{t.ChebyshevTo(ctx.Node)} tiles off; going on there for its drop");
				target = null;
				sub?.Stop(ctx);
				Begin(ctx, new WalkToSkill(new GoalNearTile(t, CollectRadius / 2, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 1, Limits = ReachLimits },
					$"to where the {Label(ctx)} at {t} was");
				return SkillStatus.Running;
			}
			// Mined (or changed under us): the drop falls nearby; pick it up before the next tile.
			Mined++;
			lastMined = t;
			unreachableInARow = 0;
			ClearExplores(ctx); // exploring found ore that got mined: only fruitless explorations in a row count
			ctx.PatrolPasses.Remove(ItemType); // likewise passes along the evil surface
			target = null;
			sub?.Stop(ctx);
			sub = null;
			if (alt is (int altItem, _)) {
				tookAlt = true;
				OtherOreTaken = altItem;
			}
			else
				MinedOwn++;
			// A block that is everywhere: the next one in reach at once, its drop and this one's pulled in meanwhile (InReach).
			if (InReach(ctx, out _) is TilePos next) {
				target = next;
				mineTicks = 0;
				arrivedOutOfReach = 0;
				return SkillStatus.Running;
			}
			int altDrop = alt?.Item ?? -1;
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType || d.Type == altDrop, CollectRadius, ignoredDrops), "collecting");
			return SkillStatus.Running;
		}
		var reach = new GoalReachTile(t, ctx.Physics, ctx.PickRangeBoost);
		TilePos node = ctx.Node;
		// Or held still off the ground (a ledge corner, a web): a player mines from there too. Seen in game (measured run P2):
		// in reach but never grounded, the walk said "done" and mining waited for footing, round and round for 6 game days.
		if ((ctx.Body.Grounded || System.Math.Abs(ctx.Body.VY) < HeldStillVY) && reach.IsGoal(node)) {
			arrivedOutOfReach = 0;
			sub?.Stop(ctx);
			sub = null;
			if (++mineTicks > MaxMineTicksPerTile)
				return GiveUp(ctx, t, $"still there after {mineTicks} ticks");
			ctx.Output.Tool = ToolRequest.Pickaxe;
			ctx.Output.UseItem = true;
			ctx.Output.Aim(t.X, t.Y);
			State = $"mining {t}";
			return SkillStatus.Running;
		}
		if (sub is not WalkToSkill) {
			sub?.Stop(ctx);
			sub = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 1, Limits = ReachLimits };
			sub.Start(ctx);
		}
		SkillStatus s = sub.Tick(ctx);
		if (s == SkillStatus.Failed) {
			string why = ((WalkToSkill)sub).FailureReason;
			// Tiles of the same kind right around it are almost surely behind the same obstacle: the kind being mined, the
			// other tier's ore when that was taken (Also). And the last of the run too, before the job fails: the next job
			// shares the set. Seen in game (measured run U10): with this job's own ore marked instead, one iron vein at
			// (2029-2033,355-361) was tried tile by tile, 142 "skipping ... unreachable" lines and 44 jobs failed ("4 Silver Ore
			// tiles in a row unreachable"), each next job starting on the tile the last one failed at, about 11 game hours.
			(int Item, int Tile)? taking = alt;
			foreach (TilePos c in ctx.World.FindTiles(taking?.Tile ?? TileType, t, UnreachableClusterRadius))
				ctx.UnreachableTiles.Add(c);
			ctx.UnreachableTiles.Add(t);
			GiveUp(ctx, t, "unreachable: " + why);
			if (++unreachableInARow >= MaxUnreachableInARow) {
				string ore = taking is (int altItem, _) ? ctx.Knowledge?.ItemName(altItem) ?? $"item {altItem}" : Label(ctx);
				return Fail(ctx, $"{unreachableInARow} {ore} tiles in a row unreachable" + (ctx.AllowBreak ? "" : " (digging is off)") + $"; last: {why}");
			}
			return SkillStatus.Running;
		}
		if (s == SkillStatus.Succeeded) {
			sub.Stop(ctx);
			sub = null;
			// "Arrived" but still out of reach: seen in the settle scenario, the walk's search started on a node beside the
			// bot that reaches the tile, said "done" at once, and the two went round in a loop for 50,000 ticks.
			if (++arrivedOutOfReach >= MaxArrivedOutOfReach) {
				arrivedOutOfReach = 0;
				return GiveUp(ctx, t, $"walked there {MaxArrivedOutOfReach} times and still out of reach from {ctx.Node}");
			}
		}
		State = $"walking to {t}";
		return SkillStatus.Running;
	}

	private int arrivedOutOfReach;
	public const int MaxArrivedOutOfReach = 3;

	/// <summary>
	/// Skip the tile for the rest of this job, and remember the failure on the context (BotContext.MineFailures) so later
	/// jobs go for other tiles first. A life crystal is one object of 2×2 tiles: the failure is noted on all of them.
	/// </summary>
	private SkillStatus GiveUp(BotContext ctx, TilePos t, string why)
	{
		int type = alt?.Tile ?? TileType;
		int n = ctx.NoteMineFailure(t);
		if (ctx.World.CanMineTarget(t.X, t.Y))
			foreach (TilePos c in ctx.World.FindTiles(type, t, 1))
				if (c != t)
					ctx.NoteMineFailure(c);
		ctx.Log.Info($"Mine: skipping {t}: {why}" + (n > 1 ? $" ({n} times lately)" : ""));
		bad.Add(t);
		target = null;
		sub?.Stop(ctx);
		sub = null;
		return SkillStatus.Running;
	}

	/// <summary>The nearest tile of this kind within <paramref name="radius"/> of <paramref name="around"/> that mining would take on.</summary>
	private TilePos? Nearest(BotContext ctx, TilePos around, int radius) => NearestOf(ctx, TileType, around, radius, out _);

	/// <summary>The fruitless explorations of this job's tier group are forgotten (ore found, or given up).</summary>
	private void ClearExplores(BotContext ctx)
	{
		foreach (int i in TierGroup) {
			ctx.ExploresWithoutOre.Remove(i);
			ctx.ExploreLegsCut.Remove(i);
		}
	}

	/// <summary>
	/// The ore on the map (within <see cref="MapSearchRadius"/>) to go back for: this job's own or one of <see cref="Also"/>,
	/// whichever costs least (<see cref="NearestOf"/>); <paramref name="other"/> is the Also ore taken, null for its own.
	/// </summary>
	private TilePos? OnTheMap(BotContext ctx, out (int Item, int Tile)? other)
	{
		other = null;
		TilePos? best = NearestOf(ctx, TileType, ctx.Node, MapSearchRadius, out float bestCost);
		foreach (var a in Also)
			if (NearestOf(ctx, a.Tile, ctx.Node, MapSearchRadius, out float c) is TilePos o && c < bestCost) {
				best = o;
				bestCost = c;
				other = a;
			}
		return best;
	}

	/// <summary>
	/// Rows below the depth floor (<see cref="BotContext.DeepestRow"/>) that ore (in sight, or on the map) is still gone for,
	/// while the floor has a step to go (<see cref="BotContext.MaxDepthBonus"/>): the next step's worth
	/// (<see cref="BotContext.DepthBonusStep"/>), weighed as a cost (<see cref="BelowFloorRowTiles"/>), not banned. Seen in game
	/// (measured run U22): with defence under 8 the floor stayed at row 436 through the armour phase, and the iron seen at
	/// (2029,441) and (2122,443), 5-7 rows under it, sat on the map while the bot explored at the floor and went on a trip
	/// west and back (10.2 game hours) that found no ore; after the +25 step it went back for them and had 33 iron in about
	/// 2.5 game hours. Exploring itself keeps to the floor (ExploreSkill); the planner takes the step (ProgressionPlanner).
	/// </summary>
	public static int BelowFloorAllowance(BotContext ctx) =>
		ctx.DeepestRow < int.MaxValue - BotContext.DepthBonusStep && ctx.DepthBonus < BotContext.MaxDepthBonus ? BotContext.DepthBonusStep : 0;

	/// <summary>
	/// Each row below the floor counts as this many tiles of distance: a full step down (<see cref="BotContext.DepthBonusStep"/>)
	/// weighs as much as the map lookup's whole reach (<see cref="MapSearchRadius"/>), so ore within the floor anywhere on the
	/// map comes first, and ore below it before new ground.
	/// </summary>
	public const int BelowFloorRowTiles = MapSearchRadius / BotContext.DepthBonusStep;

	/// <summary>
	/// <see cref="BelowFloorAllowance"/> for this job: ore only (a life crystal has its own margin, ValuableDepthMargin; stone
	/// and dirt are everywhere). In sight as on the map, so the found check of an exploration at the floor sees that ore too.
	/// </summary>
	private int StepBelow(BotContext ctx) => Valuable(ctx) || Bulk(ctx) ? 0 : BelowFloorAllowance(ctx);

	private static float BelowFloorCost(TilePos c, int floor, int below) => below > 0 && c.Y > floor ? (c.Y - floor) * (float)BelowFloorRowTiles : 0f;

	/// <summary>The tile of this ore to go for, else (none in sight) one of an <see cref="Also"/> ore; sets <see cref="alt"/>.</summary>
	private TilePos? PickTarget(BotContext ctx)
	{
		if (tookAlt && alt is (int, int tile))
			return BestOf(ctx, tile); // finishing the other tier's vein: that ore only
		if (BestOf(ctx, TileType) is TilePos own) {
			alt = null;
			return own;
		}
		foreach (var a in Also)
			if (BestOf(ctx, a.Tile) is TilePos other) {
				if (alt?.Tile != a.Tile)
					ctx.Log.Info($"Mine: no {Label(ctx)} in sight, but {ctx.Knowledge?.ItemName(a.Item)} at {other}, which does as well; taking that");
				alt = a;
				return other;
			}
		return null;
	}

	/// <summary>
	/// The nearest tile of the type within <paramref name="radius"/> that mining would take on, failures lately (on this job
	/// or an earlier one: BotContext.MineFailures) counted as distance.
	/// </summary>
	private TilePos? NearestOf(BotContext ctx, int tile, TilePos around, int radius, out float cost)
	{
		TilePos? best = null;
		float bestDist = float.MaxValue;
		int floor = ctx.DeepestRow, below = StepBelow(ctx);
		foreach (TilePos c in Find(ctx, tile, around, radius)) {
			int d = c.ChebyshevTo(around);
			if (d >= bestDist)
				break; // nearest first: none further on can beat it
			if (bad.Contains(c) || !ctx.InMiningReach(c, around, radius) || WhyNotMine(ctx, ctx.Moves, c, Valuable(ctx), below) != null)
				continue;
			float dist = d + ctx.MineFailureCost(c, radius) + BelowFloorCost(c, floor, below);
			if (dist < bestDist) {
				bestDist = dist;
				best = c;
			}
		}
		cost = bestDist;
		return best;
	}

	/// <summary>The tiles of the type the map shows round <paramref name="around"/>; life crystals among them are remembered (BotContext.SeeCrystals).</summary>
	private IReadOnlyList<TilePos> Find(BotContext ctx, int tile, TilePos around, int radius)
	{
		if (Valuable(ctx) && tile == TileType)
			return ctx.SeeCrystals(tile, around, radius);
		IReadOnlyList<TilePos> found = ctx.World.FindTiles(tile, around, radius);
		return FrameX < 0 || tile != TileType ? found : found.Where(c => ctx.World.TileFrameX(c.X, c.Y) == FrameX).ToList();
	}

	/// <summary>The tile is still the one this job is after (its type, and its frame if <see cref="FrameX"/> is set).</summary>
	private bool StillThere(BotContext ctx, TilePos t)
	{
		int tile = alt?.Tile ?? TileType;
		return ctx.World.TileTypeAt(t.X, t.Y) == tile && (alt != null || FrameX < 0 || ctx.World.TileFrameX(t.X, t.Y) == FrameX);
	}

	/// <summary>
	/// The nearest remembered life crystal cell (BotContext.KnownCrystals), with no radius cap, that mining would take on
	/// (<see cref="WhyNotMine"/> for a valuable: the gear's depth for crystals, lava, biomes), not <paramref name="skip"/>ped.
	/// Its give-ups lately (BotContext.MineFailures) count as distance, half a map lookup's reach (<see cref="MapSearchRadius"/>)
	/// each, and at BotContext.MineFailuresToForgo it waits until they fade, as for crystals in sight. A remembered cell the map
	/// no longer shows (mined) is forgotten. Seen in game (measured run U9): a crystal 325 rows from the respawn, beyond the
	/// 200-tile map lookup, was found again 23 game hours later.
	/// </summary>
	public static TilePos? RememberedCrystal(BotContext ctx, int crystalTile, TilePos from, Func<TilePos, bool> skip = null)
	{
		if (ctx.KnownCrystals.Count == 0 || ctx.Moves.Blocks == null)
			return null;
		TilePos? best = null;
		float bestDist = float.MaxValue;
		List<TilePos> gone = null;
		foreach (TilePos c in ctx.KnownCrystals.All) {
			if (ctx.World.TileTypeAt(c.X, c.Y) != crystalTile) {
				(gone ??= new List<TilePos>()).Add(c);
				continue;
			}
			int d = c.ChebyshevTo(from);
			if (d >= bestDist || skip != null && skip(c) || ctx.MineFailuresLately(c) >= BotContext.MineFailuresToForgo)
				continue;
			if (!ctx.World.CanMineTarget(c.X, c.Y) || WhyNotMine(ctx, ctx.Moves, c, valuable: true) != null)
				continue;
			float dist = d + ctx.MineFailureCost(c, MapSearchRadius);
			if (dist < bestDist) {
				bestDist = dist;
				best = c;
			}
		}
		if (gone != null)
			foreach (TilePos c in gone)
				ctx.KnownCrystals.Remove(c);
		return best;
	}

	private TilePos? BestOf(BotContext ctx, int tileType)
	{
		TilePos node = ctx.Node;
		MoveContext moves = ctx.Moves;
		TilePos? best = null;
		float bestScore = float.MaxValue;
		int floor = ctx.DeepestRow, below = StepBelow(ctx);
		bool bulk = Bulk(ctx);
		foreach (TilePos c in Find(ctx, tileType, node, SearchRadius)) {
			if (bad.Contains(c) || !ctx.InMiningReach(c, node, SearchRadius) || WhyNotMine(ctx, moves, c, Valuable(ctx), below) != null)
				continue;
			// Given up on lately, by this job or an earlier one: after the tiles not tried yet. Below the floor (StepBelow):
			// after the tiles within it.
			float score = Math.Abs(c.X - node.X) + 1.5f * Math.Abs(c.Y - node.Y) + ctx.MineFailureCost(c, SearchRadius) + BelowFloorCost(c, floor, below);
			// The row the bot stands on, under its feet and a step either side, last: seen in the settle scenario, it mined
			// the stone it stood on tile by tile, placing a block each time to keep its footing, and the count never rose.
			if (c.Y == node.Y + 1 && c.X >= node.X - 1 && c.X <= node.X + 2)
				score += UnderFootPenalty;
			if (score < bestScore && !(bulk && WhyNotQuarry(ctx, moves, c) != null)) {
				bestScore = score;
				best = c;
			}
		}
		return best;
	}

	/// <summary>
	/// A block that is everywhere, wanted by the hundred (stone, dirt, the Underworld's ash: the hellbridge's thousands), not an
	/// ore vein: mined from where the bot stands as far as the pickaxe reaches (<see cref="InReach"/>), with no vein to finish,
	/// no map lookup and no step below the depth floor.
	/// </summary>
	public static bool Bulk(GameIds ids, int itemType) => ids.BuildingBlocks.Contains(itemType) || itemType > 0 && itemType == ids.AshBlock;

	private bool Bulk(BotContext ctx) => Bulk(ctx.Ids, ItemType);

	/// <summary>
	/// Why a <see cref="Bulk"/> job leaves this tile be though mining would take it on (<see cref="WhyNotMine"/>), or null:
	/// <list type="bullet">
	/// <item>it has no floor under it within a fall the body takes unhurt (<see cref="OverAFall"/>): a bridge or a stair over a
	/// drop, not ground. Mining it opens a hole into that fall, and getting to it takes blocks placed to stand on. Seen in game
	/// (measured run world_wof M12d, t129600-t151200): the stone job began on the stair the bot had just built up out of a
	/// valley ("place(2399,258) ... place(2418,247)", up to 48 rows over the ground at (2416,304)) and mined it block by block,
	/// placing blocks to reach the next ((2401,258) and (2399,261) mined twice): 40 Stone Blocks in 21,600 ticks;</item>
	/// <item>it is a block of a build in progress (BotContext.Rebuilt: the arena's floor), which path moves may take out at the
	/// price of putting it back, but not a quarry.</item>
	/// </list>
	/// </summary>
	private string WhyNotQuarry(BotContext ctx, MoveContext moves, TilePos c)
	{
		if (OverAFall(moves.Grid, c, ctx.Physics.SafeFallTiles))
			return "over a fall";
		if (ctx.Rebuilt != null && ctx.Rebuilt.Contains(c))
			return "part of a build in progress";
		return null;
	}

	/// <summary>No floor under the tile within <paramref name="safeFallTiles"/> rows (<see cref="PhysicsParams.SafeFallTiles"/>).</summary>
	public static bool OverAFall(ITileGrid g, TilePos c, int safeFallTiles)
	{
		for (int dy = 1; dy <= safeFallTiles; dy++)
			if (g.Get(c.X, c.Y + dy).CanStandOn())
				return false;
		return true;
	}

	/// <summary>
	/// <see cref="Bulk"/>: the nearest tile of the job's own in the pickaxe's reach from where the body is held still
	/// (<see cref="GoalReachTile.IsGoal"/>: Player.IsTargetTileInItemRange with tileRangeX 5, tileRangeY 4 and the pickaxe's
	/// tileBoost, less a tile of slack, and never one of the two cells under the feet), that mining would take on
	/// (<see cref="WhyNotMine"/>: no lava let in, no sand let fall, no protected cell) and a quarry would
	/// (<see cref="WhyNotQuarry"/>), and not given up on lately (BotContext.MineFailures: the search's cost of it,
	/// <see cref="BestOf"/>, decides when it is tried again, not its being in reach); <paramref name="count"/> is how many such
	/// tiles there are. Mined one after another with no
	/// walk and no pickup walk in between: the drops of those beside the body come in on their own (Player.GrabItems pulls from
	/// 42 px round the hitbox), the rest are collected once nothing is left in reach, and only then is the next tile walked to
	/// (<see cref="BestOf"/>, the nearest: the quarry's edge, where the next reach is full of them). Null when nothing is in
	/// reach, when the bag and the drops lying round already make <see cref="Amount"/>, or while the body is moving.
	/// <para>Seen in game (measured run world_wof M12d, Silver Pickaxe, pick 45 at 11 ticks a hit, 23 ticks a stone): 735 Stone
	/// Blocks in 72,000 ticks, each a target of its own picked by the search (|dx| + 1.5|dy| from the node), in scattered order
	/// ((2401,259), (2409,253), (2403,258), (2402,258), (2410,253)), each followed by a walk to its reach and a walk to its drop;
	/// 49 ticks a block over the stretch with no fight, chest or stair in it (t172800-t199800: 1,773 to 2,324).</para>
	/// </summary>
	private TilePos? InReach(BotContext ctx, out int count)
	{
		count = 0;
		if (!Bulk(ctx) || alt != null || target != null)
			return null;
		if (!(ctx.Body.Grounded || Math.Abs(ctx.Body.VY) < HeldStillVY))
			return null;
		if (ctx.Inventory.Count(ItemType) + Lying(ctx) >= Amount)
			return null;
		TilePos node = ctx.Node;
		MoveContext moves = ctx.Moves;
		int boost = ctx.PickRangeBoost;
		TilePos? best = null;
		float bestDist = float.MaxValue;
		foreach (TilePos c in Find(ctx, TileType, node, ReachRadius + boost)) {
			if (bad.Contains(c) || !new GoalReachTile(c, ctx.Physics, boost).IsGoal(node) || ctx.MineFailuresLately(c) > 0
				|| WhyNotMine(ctx, moves, c) != null || WhyNotQuarry(ctx, moves, c) != null)
				continue;
			count++;
			// From the body's middle (columns x and x + 1, rows y - 2 to y): the tiles beside it first.
			float dx = c.X - node.X - 0.5f, dy = c.Y - node.Y + 1f, dist = dx * dx + dy * dy;
			if (dist < bestDist) {
				bestDist = dist;
				best = c;
			}
		}
		return best;
	}

	/// <summary>
	/// How far round the node (Chebyshev) the tiles in reach are looked for, before the pickaxe's tileBoost: GoalReachTile's
	/// widest, dx up to 5 and dy up to -5 (Player.IsTargetTileInItemRange), its slack aside.
	/// </summary>
	public const int ReachRadius = 5;

	/// <summary>The job's item lying round in collecting's reach that collecting would go for: counted towards the amount before mining more.</summary>
	private int Lying(BotContext ctx)
	{
		int n = 0;
		if (ctx.Drops == null)
			return 0;
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p), r = CollectRadius * 16f;
		foreach (DropInfo d in ctx.Drops)
			if (d.Type == ItemType && !ignoredDrops.Contains(d.Id) && (d.CenterX - cx) * (d.CenterX - cx) + (d.CenterY - cy) * (d.CenterY - cy) <= r * r
				&& !CollectDropsSkill.TooDeep(ctx, d))
				n += d.Stack;
		return n;
	}

	/// <summary>
	/// Would mining go for this tile? Known reachable, safe to break (lava and undermined sand aside: water beside it is
	/// fine, as for a player), not deeper than the gear is fit for, not off limits. Shared with the planner, which picks
	/// the job: seen in game, the planner chose "mine the life crystal" for a crystal beside water that mining then
	/// skipped, so the bot explored away from it and was sent back, round and round, running past it (user report,
	/// 2026-10-14).
	/// </summary>
	public static bool WouldMine(BotContext ctx, MoveContext moves, TilePos c, bool valuable = false) => WhyNotMine(ctx, moves, c, valuable) == null;

	/// <summary>
	/// Why mining would leave this tile alone, or null if it would go for it. <paramref name="valuable"/> (a life crystal):
	/// worth what a player risks for one, so a danger zone round an old death and sand on top do not stop it; lava beside
	/// it, the gear's depth and the biomes the gear is too weak for still do. Seen in game: a crystal passed by with no
	/// reason given (user report, 2026-10-15); every skip now has one.
	/// </summary>
	/// <remarks><paramref name="belowFloor"/>: rows below the gear's depth floor still taken (<see cref="BelowFloorAllowance"/>).</remarks>
	public static string WhyNotMine(BotContext ctx, MoveContext moves, TilePos c, bool valuable = false, int belowFloor = 0)
	{
		if (ctx.UnreachableTiles.Contains(c))
			return "could not get to it lately";
		if (moves.Blocks == null)
			return "no pickaxe";
		if (BlockRules.NextToLava(moves.Grid, c))
			return "lava beside or above it";
		if (!valuable && BlockRules.Unsafe(moves.Grid, c, Array.Empty<TilePos>()))
			return "sand or silt on top would fall";
		if (float.IsInfinity(moves.Blocks.BreakTicks(c.X, c.Y)) && !ctx.World.CanMineTarget(c.X, c.Y))
			return "the pickaxe cannot break it";
		// A crystal a little deeper than ore, in a full set of armour (measured run S4: four crystals seen below row 701, while
		// the bot explored six game days for others and found one).
		int deepest = valuable && ctx.Combat.FullArmour && ctx.DeepestRow < int.MaxValue - ValuableDepthMargin ? ctx.DeepestRow + ValuableDepthMargin : ctx.DeepestRow;
		if (belowFloor > 0 && deepest < int.MaxValue - belowFloor)
			deepest += belowFloor;
		if (c.Y > deepest)
			return $"below row {deepest}, deeper than the gear allows";
		// A crystal in a biome: worth going in for in a full set of armour (defense 8+, CavernDefense), as a player does; the
		// biome's own gate is defense 16, more than full iron or silver gives (measured run S1: two crystals in the underground
		// desert left alone while the bot explored for others for three game days). Ore in a zone where monsters were seen, in
		// full armour, is mined too: the exploring goes in there now, the zone a cost (BotContext.OffLimitsOutright).
		if (valuable ? (!ctx.Combat.FullArmour || ctx.EvilStoneTooHard && ctx.InEvilBiome(c)) && ctx.BiomeOffLimits(c) : ctx.OffLimitsOutright(c))
			return valuable || !ctx.InDangerZone(c) ? "in a biome the gear is too weak for" : "in a danger zone";
		return null;
	}

	private bool Valuable(BotContext ctx) => ItemType == ctx.Ids.LifeCrystal;

	/// <summary>Rows below the gear's depth limit a life crystal is still gone for, in a full set of armour.</summary>
	public const int ValuableDepthMargin = 100;

	/// <summary>Liquid on a side or corner: mining it means swimming (or flooding the tunnel). Players leave such ore without gear to breathe.</summary>
	public static bool NextToLiquid(ITileGrid g, TilePos c)
	{
		for (int dx = -1; dx <= 1; dx++)
			for (int dy = -1; dy <= 1; dy++)
				if (g.Get(c.X + dx, c.Y + dy).IsLiquid())
					return true;
		return false;
	}

	public static bool SafeToBreak(BotContext ctx, MoveContext moves, TilePos c) =>
		moves.Blocks != null && !BlockRules.Unsafe(moves.Grid, c, Array.Empty<TilePos>())
		&& (!float.IsInfinity(moves.Blocks.BreakTicks(c.X, c.Y)) || ctx.World.CanMineTarget(c.X, c.Y));

	private void Begin(BotContext ctx, ISkill skill, string state)
	{
		sub = skill;
		State = state;
		skill.Start(ctx);
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Mine {Label(ctx)} failed: {reason}");
		return SkillStatus.Failed;
	}
}
