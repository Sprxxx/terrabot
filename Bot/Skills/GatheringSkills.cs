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

	public ChopTreeSkill(TilePos treeBase) => Base = treeBase;

	public string Describe() => $"ChopTree {Base} [{State}]";
	public void Start(BotContext ctx) => ctx.Log.Info($"ChopTree start: tree at {Base}, bot at {ctx.Node}");

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
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
		var w = new WalkToSkill(Base) { Quiet = true, Tolerance = 1 };
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
	private readonly HashSet<int> skip = new();
	private WalkToSkill walk;
	private int walkTarget = -1;
	private int standingTicks;
	private int ticks;

	public int Collected { get; private set; }
	public string State { get; private set; } = "new";
	public string Name => "Collect";
	public const int MaxTicks = 60 * 40;

	public CollectDropsSkill(Func<DropInfo, bool> want, int radiusTiles = 20)
	{
		this.want = want;
		radiusPx = radiusTiles * 16f;
	}

	public string Describe() => $"Collect [{State}] got {Collected}";
	public void Start(BotContext ctx) { }

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
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
			walkTarget = target.Id;
			standingTicks = 0;
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
	public const int MaxExplores = 6;

	private ISkill sub;
	private readonly HashSet<TilePos> badTrees = new();
	private int explores;
	private int exploreDir = 1;

	public int ItemType { get; }
	public int Amount { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "GatherWood";

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
			if (s == SkillStatus.Failed && sub is ChopTreeSkill chop) {
				badTrees.Add(chop.Base);
				if (chop.FailureReason.StartsWith("no axe"))
					return Fail(ctx, chop.FailureReason);
			}
			bool felled = s == SkillStatus.Succeeded && sub is ChopTreeSkill;
			sub.Stop(ctx);
			sub = null;
			if (felled) {
				sub = new CollectDropsSkill(d => d.Type == ItemType, 15);
				sub.Start(ctx);
				State = "collecting";
				return SkillStatus.Running;
			}
		}

		if (ctx.Drops.Any(d => d.Type == ItemType)) {
			sub = new CollectDropsSkill(d => d.Type == ItemType, 20);
			State = "collecting";
		}
		else if (NearestTree(ctx) is TilePos tree) {
			sub = new ChopTreeSkill(tree);
			State = "chopping";
		}
		else {
			if (++explores > MaxExplores)
				return Fail(ctx, $"no reachable trees found after {MaxExplores} explorations");
			TilePos here = ctx.Node;
			var goal = new TilePos(here.X + exploreDir * ExploreStepTiles, here.Y - 10);
			ctx.Log.Info($"GatherWood: no trees in sight, exploring towards {goal}");
			sub = new WalkToSkill(goal) { Quiet = true, Tolerance = 4 };
			exploreDir = -exploreDir;
			State = "exploring";
		}
		sub.Start(ctx);
		return SkillStatus.Running;
	}

	private TilePos? NearestTree(BotContext ctx)
	{
		TilePos node = ctx.Node;
		foreach (TilePos t in ctx.Seen.Trees)
			if (!badTrees.Contains(t) && Math.Abs(t.X - node.X) <= TreeSearchTiles && ctx.World.IsTreeTrunk(t.X, t.Y))
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
