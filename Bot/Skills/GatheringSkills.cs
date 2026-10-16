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
/// Player.defaultItemGrabRange = 42 px, so standing a tile away is enough). Unreachable drops are skipped.
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
		var candidates = ctx.Drops.Where(d => want(d) && !skip.Contains(d.Id)
			&& Dist(d, cx, cy) <= radiusPx).OrderBy(d => Dist(d, cx, cy)).ToList();
		if (candidates.Count == 0 || ++ticks > MaxTicks) {
			State = "done";
			return SkillStatus.Succeeded;
		}
		DropInfo target = candidates[0];
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
			if (++standingTicks > 30)
				skip.Add(target.Id);
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
		return ctx.Drops.Any(d => want(d) && Dist(d, cx, cy) <= radiusTiles * 16f);
	}

	private static float Dist(DropInfo d, float x, float y) => MathF.Sqrt((d.CenterX - x) * (d.CenterX - x) + (d.CenterY - y) * (d.CenterY - y));
}

/// <summary>
/// Get <see cref="Amount"/> of an item that trees drop (wood): pick up matching drops, chop the nearest tree,
/// pick up its wood; with no tree around, explore along the surface (alternating directions).
/// </summary>
public sealed class GatherWoodSkill : ISkill
{
	public const int TreeSearchTiles = 60;
	public const int ExploreStepTiles = 40;
	/// <summary>Deeper than this below the surface's height, no tree can be in sight: the search starts on the surface.</summary>
	public const int UndergroundRows = 12;
	public const int MaxExplores = 6;
	public const int CollectRadius = 20;

	private ISkill sub;
	private readonly HashSet<TilePos> badTrees = new();
	private readonly HashSet<int> ignoredDrops = new();
	private int explores;
	private TilePos? exploreFrom;

	public int ItemType { get; }
	public int Amount { get; }
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

	public void Start(BotContext ctx) => ctx.Log.Info($"GatherWood start: have {ctx.Inventory.Count(ItemType)}, want {Amount}, {ctx.Seen.Trees.Count} tree(s) seen");

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
		int have = ctx.Inventory.Count(ItemType);
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
				// Arrived (the tree is in sight now, or it is gone: the scan forgets it), or no way there.
				if (s == SkillStatus.Failed)
					badTrees.Add(w);
				walkingTo = null;
			}
			if (s == SkillStatus.Failed && sub is ChopTreeSkill chop) {
				badTrees.Add(chop.Base);
				ctx.UnreachableTiles.Add(chop.Base);
				if (chop.FailureReason.StartsWith("no axe"))
					return Fail(ctx, chop.FailureReason);
			}
			bool felled = s == SkillStatus.Succeeded && sub is ChopTreeSkill;
			sub.Stop(ctx);
			sub = null;
			if (felled) {
				sub = new CollectDropsSkill(d => d.Type == ItemType, 15, ignoredDrops);
				sub.Start(ctx);
				State = "collecting";
				return SkillStatus.Running;
			}
		}

		if (CollectDropsSkill.AnyInRange(ctx, d => d.Type == ItemType && !ignoredDrops.Contains(d.Id), CollectRadius)) {
			sub = new CollectDropsSkill(d => d.Type == ItemType, CollectRadius, ignoredDrops);
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
			if (d < bestDist && !badTrees.Contains(t) && !ctx.UnreachableTiles.Contains(t)) {
				best = t;
				bestDist = d;
			}
		}
		return best;
	}

	private TilePos? NearestTree(BotContext ctx)
	{
		TilePos node = ctx.Node;
		foreach (TilePos t in ctx.Seen.Trees)
			if (!badTrees.Contains(t) && !ctx.UnreachableTiles.Contains(t) && Math.Abs(t.X - node.X) <= TreeSearchTiles && ctx.World.IsTreeTrunk(t.X, t.Y))
				return t;
		return null;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"GatherWood failed: {reason}");
		return SkillStatus.Failed;
	}
}
