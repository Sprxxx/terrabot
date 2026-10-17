using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Walk next to a tree and swing the axe at its base trunk tile until the tree falls (vanilla: axe damage
/// axe × 1.2 per hit, the tree breaks at 100 and everything above the hit tile falls and drops wood).
/// </summary>
public sealed class ChopTreeSkill : ISkill
{
	public const int MaxChopTicks = 60 * 20;

	private WalkToSkill walk;
	private int chopTicks;

	public TilePos Base { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "ChopTree";
	public string Key => $"ChopTree {Base}";

	public ChopTreeSkill(TilePos treeBase) => Base = treeBase;

	public string Describe() => $"ChopTree {Base} [{State}]";
	public void Start(BotContext ctx) => ctx.Log.Info($"ChopTree start: tree at {Base}, bot at {ctx.Node}");

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
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		if (ctx.AxePower <= 0)
			return Fail(ctx, "no axe in the hotbar");
		if (!ctx.World.IsTreeTrunk(Base.X, Base.Y)) {
			ctx.Log.Info($"ChopTree: tree at {Base} is down after {chopTicks} ticks of chopping");
			State = "felled";
			return SkillStatus.Succeeded;
		}

		TilePos node = ctx.Node;
		bool inReach = ctx.Body.Grounded && Math.Abs(node.X + 1 - Base.X) <= 3 && Math.Abs(node.Y - Base.Y) <= 2;
		if (!inReach) {
			walk ??= StartWalk(ctx);
			SkillStatus s = walk.Tick(ctx);
			if (s == SkillStatus.Failed)
				return Fail(ctx, "cannot reach the tree: " + walk.FailureReason);
			if (s == SkillStatus.Succeeded)
				walk = null;
			State = "walking";
			return SkillStatus.Running;
		}
		walk = null;
		if (++chopTicks > MaxChopTicks)
			return Fail(ctx, $"tree did not fall after {chopTicks} ticks");
		ctx.Output.Tool = ToolRequest.Axe;
		ctx.Output.UseItem = true;
		ctx.Output.Aim(Base.X, Base.Y);
		State = "chopping";
		return SkillStatus.Running;
	}

	private WalkToSkill StartWalk(BotContext ctx)
	{
		var w = new WalkToSkill(Base) { Quiet = true, Tolerance = 1, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
		w.Start(ctx);
		return w;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"ChopTree {Base} failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>
/// Walk over dropped items matching a filter until none are left in range (vanilla picks items up within
/// Player.defaultItemGrabRange = 42 px, so standing a tile away is enough). Unreachable drops are skipped, and so are
/// drops lying deeper under the feet than a fall the body takes unhurt (<see cref="TooDeep"/>).
/// </summary>
public sealed class CollectDropsSkill : ISkill
{
	private readonly Func<DropInfo, bool> want;
	private readonly float radiusPx;
	/// <summary>Drops given up on (unreachable, or not picked up while standing on them). Callers that restart this
	/// skill pass their own set, so the same unreachable drop is not tried again and again.</summary>
	private readonly HashSet<int> skip;
	private WalkToSkill walk;
	private int walkTarget = -1;
	private int standingTicks;
	private int ticks;

	public int Collected { get; private set; }
	public string State { get; private set; } = "new";
	public string Name => "Collect";
	public const int MaxTicks = 60 * 40;
	/// <summary>
	/// Ticks standing where the walk counts as arrived before a drop that did not come is given up on: half a second. A drop
	/// in grab range is pulled in at once (Player.GrabItems runs every tick: PullItem_Common moves it towards the player, and
	/// it is picked up when the hitboxes touch), so one that has not come in that time is not coming.
	/// </summary>
	public const int StandGiveUpTicks = 30;

	public CollectDropsSkill(Func<DropInfo, bool> want, int radiusTiles = 20, HashSet<int> skip = null)
	{
		this.want = want;
		this.skip = skip ?? new HashSet<int>();
		radiusPx = radiusTiles * 16f;
	}

	public string Describe() => $"Collect [{State}] got {Collected}";
	public void Start(BotContext ctx) { }

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
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		if (walkTarget >= 0 && !ctx.Drops.Any(d => d.Id == walkTarget)) {
			Collected++;
			walk = null;
			walkTarget = -1;
		}
		foreach (DropInfo d in ctx.Drops)
			if (!skip.Contains(d.Id) && want(d) && Dist(d, cx, cy) <= radiusPx && TooDeep(ctx, d)) {
				skip.Add(d.Id);
				ctx.Log.Info($"Collect: leaving {d.Name} x{d.Stack} at {d.Tile}, {d.Tile.Y - ctx.Node.Y} rows down (deeper than a safe fall, {p.SafeFallTiles})");
			}
		var candidates = ctx.Drops.Where(d => want(d) && !skip.Contains(d.Id) && Fits(ctx, d)
			&& Dist(d, cx, cy) <= radiusPx).OrderBy(d => Dist(d, cx, cy)).ToList();
		if (candidates.Count == 0) {
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (++ticks > MaxTicks) {
			// Out of time on the drop it was after: given up on (logged), so a caller that starts a fresh collect with the same
			// skip set does not set off for it again.
			int at = candidates.FindIndex(d => d.Id == walkTarget);
			if (at >= 0 && skip.Add(walkTarget))
				ctx.Log.Info($"Collect: giving up on {candidates[at].Name} x{candidates[at].Stack} at {candidates[at].Tile}: not picked up in {MaxTicks / 60} s");
			State = "done";
			return SkillStatus.Succeeded;
		}
		// Committed to the drop it set off for while that drop lasts (picked up, given up on, or out of range ends it), not the
		// nearest of each tick. Seen in game (measured run M10d, world_eow, t73800-81000): fetching coins at (1831,305), the
		// walk's first jump took the body up to row 300, where coins at (1826,290) were nearer; the walk there began, a step
		// down made (1831,305) the nearer again, and so on: "Collect [fetching Copper Coin x87] got 0" for 2 game hours, and
		// 10 such stretches of 3 heartbeats or more in the run (47 Collect heartbeats of 240).
		int kept = candidates.FindIndex(d => d.Id == walkTarget);
		DropInfo target = candidates[kept >= 0 ? kept : 0];
		if (walkTarget != target.Id || walk == null) {
			// The give-up count belongs to the drop, not the walk: reset per walk, it never reached 30 (seen in game:
			// a drop on a ledge out of grab range, the walk "arriving" every tick).
			if (walkTarget != target.Id)
				standingTicks = 0;
			walkTarget = target.Id;
			walk = new WalkToSkill(target.Tile) { Quiet = true, Tolerance = 1 };
			walk.Start(ctx);
		}
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Failed) {
			skip.Add(target.Id);
			walk = null;
		}
		else if (s == SkillStatus.Succeeded) {
			// Standing on it but it did not come to us (inventory full, or it is out of grab range).
			walk = null;
			if (++standingTicks > StandGiveUpTicks && skip.Add(target.Id))
				ctx.Log.Info($"Collect: giving up on {target.Name} x{target.Stack} at {target.Tile}: stood by it {StandGiveUpTicks} ticks at {ctx.Node} and it was not picked up");
		}
		State = $"fetching {target.Name} x{target.Stack}";
		return SkillStatus.Running;
	}

	/// <summary>
	/// Is there a matching drop this skill would actually go for? Callers must use this rather than "any drop anywhere":
	/// a far drop makes the skill finish at once, and a caller that keeps restarting it never gets anywhere.
	/// </summary>
	public static bool AnyInRange(BotContext ctx, Func<DropInfo, bool> want, int radiusTiles)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		return ctx.Drops.Any(d => want(d) && Dist(d, cx, cy) <= radiusTiles * 16f && !TooDeep(ctx, d) && Fits(ctx, d));
	}

	/// <summary>
	/// The bag has room for it (a free slot, or a stack it joins): a drop that does not fit is not walked to, as a player with a
	/// full bag does not go for it. Seen in game (measured run M10f, world_eow): 50 of 240 heartbeats Collect, "fetching
	/// Goodie Bag x1 got 0" and "fetching Ancient Shadow Greaves x1 got 0" over and over beside a full bag. And nothing is
	/// walked to while the game would not pick it up at all (<see cref="IInventory.CanPickUp"/>: an Encumbering Stone
	/// carried). Seen in game (measured run U28, world_eye): with one in the bag from t465000, 101 Collect heartbeats standing
	/// on coins, dirt and arrows ("WalkTo reached StandAt(1805,286) at (1805,286)" 62 times in a row after one fight).
	/// </summary>
	private static bool Fits(BotContext ctx, DropInfo d) => ctx.Inventory == null || ctx.Inventory.CanPickUp(d.Type);

	/// <summary>
	/// A drop more rows under the feet than a fall the body takes unhurt (<see cref="PhysicsParams.SafeFallTiles"/>): at a
	/// chasm's floor, or down a shaft. Left where it lies: the paths drop no farther than a safe fall, so the way there is a
	/// long dig down into what the chasm holds, for a few coins. Seen in game (measured run M10c, world_eow): the fight's loot
	/// had fallen to a corruption chasm's floor, "Collect [fetching Copper Coin x11]" at (1700,361), some 90 rows under the
	/// surface the fight was on (rows 269-273); that time the bot was already falling there, knocked off the lip ("Died (665
	/// damage" on landing), but nothing kept a collect from going down for it, and the boss's loot is looked for 40 tiles round.
	/// </summary>
	public static bool TooDeep(BotContext ctx, DropInfo d) => d.Tile.Y - ctx.Node.Y > ctx.Physics.SafeFallTiles;

	private static float Dist(DropInfo d, float x, float y) => MathF.Sqrt((d.CenterX - x) * (d.CenterX - x) + (d.CenterY - y) * (d.CenterY - y));
}

/// <summary>
/// Get <see cref="Amount"/> of an item that trees drop (wood), counting <see cref="Also"/> (the other woods the need
/// accepts) with it: pick up matching drops, chop the nearest tree whose wood counts, pick up its wood; with no tree
/// around, explore along the surface (alternating directions).
/// </summary>
public sealed class GatherWoodSkill : ISkill
{
	public const int TreeSearchTiles = 60;
	public const int ExploreStepTiles = 40;
	/// <summary>Deeper than this below the surface's height, no tree can be in sight: the search starts on the surface.</summary>
	public const int UndergroundRows = 12;
	public const int MaxExplores = 6;
	public const int CollectRadius = 20;
	/// <summary>Failed walks up to the surface before the skill gives up: the second one, from where the first left it, is the retry.</summary>
	public const int MaxFailedWalksUp = 2;

	private ISkill sub;
	private int failedWalksUp;
	private readonly HashSet<TilePos> badTrees = new();
	private readonly HashSet<int> ignoredDrops = new();
	private int explores;
	private TilePos? exploreFrom;

	public int ItemType { get; }
	public int Amount { get; }
	/// <summary>
	/// Other woods that count towards <see cref="Amount"/>: what the need's recipe takes besides (vanilla's "Wood" recipe
	/// group: Ebonwood, Shadewood, Boreal Wood...). Counted, picked up, and their trees chopped. Seen in game (measured run
	/// U28, world_eye): for Wooden Arrows (any wood), the trees near by were the Corruption's (x 1765-1960), which drop Ebonwood;
	/// only Wood counted and only Wood drops were collected, so "GatherWood start: have 4" every time for the last 30 game
	/// hours of the run.
	/// </summary>
	public IReadOnlyList<int> Also { get; init; } = Array.Empty<int>();
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "GatherWood";
	public string Key => $"GatherWood {ItemType} {Amount}";

	public GatherWoodSkill(int itemType, int amount)
	{
		ItemType = itemType;
		Amount = amount;
	}

	private int lastHave;

	public string Describe() => $"GatherWood {lastHave}/{Amount} [{State}]" + (sub != null ? " > " + sub.Describe() : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"GatherWood start: have {Have(ctx)}, want {Amount}, {ctx.Seen.Trees.Count} tree(s) seen"
		+ (Also.Count > 0 && ctx.Knowledge != null ? $" (counting {string.Join(", ", Also.Select(ctx.Knowledge.ItemName))} too)" : ""));

	/// <summary>Does this wood count towards the amount?</summary>
	public bool Counts(int itemType) => itemType == ItemType || Also.Contains(itemType);

	private int Have(BotContext ctx) => ctx.Inventory.Count(ItemType) + Also.Where(a => a != ItemType).Sum(ctx.Inventory.Count);

	public void Stop(BotContext ctx)
	{
		sub?.Stop(ctx);
		sub = null;
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

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		int have = Have(ctx);
		lastHave = have;
		if (have >= Amount && sub is not ChopTreeSkill) {
			ctx.Log.Info($"GatherWood done: {have}/{Amount}");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (sub != null) {
			SkillStatus s = sub.Tick(ctx);
			if (s == SkillStatus.Running)
				return SkillStatus.Running;
			if (walkingTo is TilePos w) {
				// Arrived (the tree is in sight now, or it is gone: the scan forgets it), or no way there: then not again, nor
				// by a fresh GatherWood (as for a tree ChopTree could not get at).
				if (s == SkillStatus.Failed) {
					badTrees.Add(w);
					ctx.UnreachableTiles.Add(w);
				}
				// There, and no tree in sight: forgotten (a scan that sees it again remembers it). Seen in game (measured run
				// U9): just respawned on the spot of a remembered tree, the walk "arrived" each tick, 19 ticks running.
				else if (NearestTree(ctx) == null)
					ctx.KnownTrees.Remove(w);
				walkingTo = null;
			}
			if (s == SkillStatus.Failed && sub is ChopTreeSkill chop) {
				badTrees.Add(chop.Base);
				ctx.UnreachableTiles.Add(chop.Base);
				if (chop.FailureReason.StartsWith("no axe"))
					return Fail(ctx, chop.FailureReason);
			}
			// No way up: the next tick would build the same walk again, without end.
			if (s == SkillStatus.Failed && sub is WalkToSkill { ToSurface: true } && ++failedWalksUp >= MaxFailedWalksUp)
				return Fail(ctx, $"no way up to the surface after {failedWalksUp} walks");
			bool felled = s == SkillStatus.Succeeded && sub is ChopTreeSkill;
			sub.Stop(ctx);
			sub = null;
			if (felled) {
				sub = new CollectDropsSkill(d => Counts(d.Type), 15, ignoredDrops);
				sub.Start(ctx);
				State = "collecting";
				return SkillStatus.Running;
			}
		}

		if (CollectDropsSkill.AnyInRange(ctx, d => Counts(d.Type) && !ignoredDrops.Contains(d.Id), CollectRadius)) {
			sub = new CollectDropsSkill(d => Counts(d.Type), CollectRadius, ignoredDrops);
			State = "collecting";
		}
		else if (NearestTree(ctx) is TilePos tree) {
			sub = new ChopTreeSkill(tree);
			State = "chopping";
		}
		else if (NearestKnownTree(ctx) is TilePos known) {
			// Back to a tree seen earlier before exploring for new ones (Baritone and AltoClef go to remembered blocks first).
			ctx.Log.Info($"GatherWood: no trees in sight; back to one seen at {known}");
			var walkBack = new WalkToSkill(known) { Quiet = true, Tolerance = 4 };
			walkingTo = known;
			sub = walkBack;
			State = $"walking to a tree seen at {known}";
		}
		else {
			TilePos here = ctx.Node;
			// Trees grow on the surface: from underground, up first (seen in game, measured run R1: "exploring" for trees at
			// row 460 for most of a game day, back and forth along the caves).
			if (ctx.SurfaceY > 0 && here.Y > ctx.SurfaceY + UndergroundRows) {
				ctx.Log.Info($"GatherWood: no trees down here (row {here.Y}); up to the surface first");
				sub = new WalkToSkill(new Core.Pathfinding.GoalRow(ctx.SurfaceY + 2, here.X, ctx.Physics)) { Quiet = true, ToSurface = true };
				State = "going up";
				sub.Start(ctx);
				return SkillStatus.Running;
			}
			if (++explores > MaxExplores)
				return Fail(ctx, $"no reachable trees found after {MaxExplores} explorations");
			// Outward from where the search began, the two sides in turn and further each time: +40, -40, +80, -80...
			// Seen in game (measured run U8): stepping 40 tiles alternately from where it stood went to and fro between
			// the same two places (2320 and 2375) for 19 game hours.
			TilePos from = exploreFrom ??= here;
			int dir = explores % 2 == 1 ? 1 : -1;
			var goal = new TilePos(from.X + dir * ExploreStepTiles * ((explores + 1) / 2), from.Y - 10);
			ctx.Log.Info($"GatherWood: no trees in sight, exploring towards {goal}");
			sub = new WalkToSkill(goal) { Quiet = true, Tolerance = 4 };
			State = "exploring";
		}
		sub.Start(ctx);
		return SkillStatus.Running;
	}

	private TilePos? walkingTo;

	private TilePos? NearestKnownTree(BotContext ctx)
	{
		TilePos node = ctx.Node;
		TilePos? best = null;
		int bestDist = int.MaxValue;
		foreach (TilePos t in ctx.KnownTrees) {
			int d = Math.Abs(t.X - node.X) + Math.Abs(t.Y - node.Y);
			// A tree remembered with its ground (it was seen): one whose wood does not count is no use to go back to. Gone (0):
			// going there forgets it.
			int wood = ctx.World.TreeWood(t.X, t.Y);
			if (d < bestDist && !badTrees.Contains(t) && !ctx.UnreachableTiles.Contains(t) && (wood == 0 || Counts(wood))) {
				best = t;
				bestDist = d;
			}
		}
		return best;
	}

	/// <summary>
	/// The nearest tree in sight whose wood counts (<see cref="Counts"/>, by the ground it stands on: <see
	/// cref="IWorldInfo.TreeWood"/>). Trees of another wood are passed by, as a player after Wood walks past the Corruption's
	/// Ebonwood trees; logged once per wood when that leaves none.
	/// </summary>
	private TilePos? NearestTree(BotContext ctx)
	{
		TilePos node = ctx.Node;
		int otherWood = 0;
		foreach (TilePos t in ctx.Seen.Trees) {
			if (badTrees.Contains(t) || ctx.UnreachableTiles.Contains(t) || Math.Abs(t.X - node.X) > TreeSearchTiles || !ctx.World.IsTreeTrunk(t.X, t.Y))
				continue;
			int wood = ctx.World.TreeWood(t.X, t.Y);
			if (Counts(wood))
				return t;
			otherWood = wood;
		}
		if (otherWood != 0 && notedWoods.Add(otherWood) && ctx.Knowledge != null)
			ctx.Log.Info($"GatherWood: the trees in sight give {ctx.Knowledge.ItemName(otherWood)}, which does not count for {ctx.Knowledge.ItemName(ItemType)}; looking further");
		return null;
	}

	private readonly HashSet<int> notedWoods = new();

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"GatherWood failed: {reason}");
		return SkillStatus.Failed;
	}
}
