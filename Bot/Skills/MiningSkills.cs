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
/// Vanilla has no line-of-sight rule for pickaxes: any tile in range can be mined.
/// </summary>
public sealed class MineSkill : ISkill
{
	public const int SearchRadius = 40;
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
	public static readonly SearchLimits ReachLimits = new() { MaxExpansions = 12_000 };

	private readonly HashSet<TilePos> bad = new();
	private readonly HashSet<int> ignoredDrops = new();
	private ISkill sub;
	private TilePos? target;
	private int mineTicks;
	private int exploreDir;
	private int lastHave;
	private int unreachableInARow;

	public int ItemType { get; }
	public int TileType { get; }
	public int Amount { get; }
	public int Mined { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Mine";

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
		sub?.Stop(ctx);
		sub = null;
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
		if (have >= Amount && sub is not CollectDropsSkill) {
			ctx.Log.Info($"Mine done: {Label(ctx)} {have}/{Amount}, mined {Mined} tile(s)");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (ctx.PickPower <= 0)
			return Fail(ctx, "no pickaxe in the hotbar");

		if (target is TilePos t)
			return TickTarget(ctx, t);

		if (sub != null) {
			SkillStatus s = sub.Tick(ctx);
			if (s == SkillStatus.Running)
				return SkillStatus.Running;
			sub.Stop(ctx);
			sub = null;
		}

		if (CollectDropsSkill.AnyInRange(ctx, d => d.Type == ItemType && !ignoredDrops.Contains(d.Id), CollectRadius)) {
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, CollectRadius, ignoredDrops), "collecting");
			return SkillStatus.Running;
		}
		if (PickTarget(ctx) is TilePos next) {
			target = next;
			mineTicks = 0;
			arrivedOutOfReach = 0;
			ctx.Log.Debug($"Mine: target {next}");
			return SkillStatus.Running;
		}
		// Counted on the context per item: a fight pauses the skill and the planner starts it afresh, and a count of its
		// own went back to 0 each time (world_eye run 42: 12 game hours of the same first leg, never reaching the give-up).
		int explores = ctx.ExploresWithoutOre[ItemType] = ctx.ExploresWithoutOre.GetValueOrDefault(ItemType) + 1;
		if (explores > MaxExplores) {
			// Given up: the next job (after travelling to new ground) starts counting afresh. Kept, every later job failed at
			// once, each a "fruitless" mine, and the travel target went out 120 tiles further each time (1,900 in run 47).
			ctx.ExploresWithoutOre.Remove(ItemType);
			return Fail(ctx, $"no {Label(ctx)} found after {MaxExplores} explorations in a row");
		}
		// Standing somewhere off limits (a biome zone, the underground desert): no exploring here, but somewhere new. Seen in
		// game (world_eye run 59): explorations in the underground desert went on until antlion chargers killed the bot.
		if (ctx.OffLimits(ctx.Node)) {
			ctx.ExploresWithoutOre.Remove(ItemType);
			return Fail(ctx, $"no {Label(ctx)} seen, and the explorations from here would be off limits");
		}
		// Only tiles the player has seen count (no x-ray): go and look, down into caves and through the ground.
		ctx.Log.Info($"Mine: no {Label(ctx)} seen within {SearchRadius} tiles; exploring");
		// "Found" is what this skill would actually go for (not beside water, not given up on...): with "any seen", exploring
		// stopped at ore that mining then skipped, and mining explored again, round and round.
		// The first exploration of a job goes the safer way (BotContext.SaferSide), later ones alternate.
		exploreDir = exploreDir == 0 ? ctx.SaferSide(ctx.Node) : -exploreDir;
		Begin(ctx, new ExploreSkill(Label(ctx), c => PickTarget(c) != null, ExploreBias.Down, exploreDir), "exploring");
		return SkillStatus.Running;
	}

	private SkillStatus TickTarget(BotContext ctx, TilePos t)
	{
		if (ctx.World.TileTypeAt(t.X, t.Y) != TileType) {
			// Mined (or changed under us): the drop falls nearby; pick it up before the next tile.
			Mined++;
			unreachableInARow = 0;
			ctx.ExploresWithoutOre.Remove(ItemType); // exploring found ore that got mined: only fruitless explorations in a row count
			target = null;
			sub?.Stop(ctx);
			sub = null;
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, CollectRadius, ignoredDrops), "collecting");
			return SkillStatus.Running;
		}
		var reach = new GoalReachTile(t, ctx.Physics, ctx.PickRangeBoost);
		TilePos node = ctx.Node;
		if (ctx.Body.Grounded && reach.IsGoal(node)) {
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
			if (++unreachableInARow >= MaxUnreachableInARow)
				return Fail(ctx, $"{unreachableInARow} {Label(ctx)} tiles in a row unreachable" + (ctx.AllowBreak ? "" : " (digging is off)") + $"; last: {why}");
			// Tiles of the same kind right around it are almost surely behind the same obstacle.
			foreach (TilePos c in ctx.World.FindTiles(TileType, t, UnreachableClusterRadius))
				ctx.UnreachableTiles.Add(c);
			ctx.UnreachableTiles.Add(t);
			return GiveUp(ctx, t, "unreachable: " + why);
		}
		if (s == SkillStatus.Succeeded) {
			sub.Stop(ctx);
			sub = null;
			// "Arrived" but still out of reach: seen in the settle scenario, the walk's search started on a node beside the
			// bot that reaches the tile, said "done" at once, and the two went round in a loop for 50,000 ticks.
			if (ctx.Body.Grounded && ++arrivedOutOfReach >= MaxArrivedOutOfReach) {
				arrivedOutOfReach = 0;
				return GiveUp(ctx, t, $"walked there {MaxArrivedOutOfReach} times and still out of reach from {ctx.Node}");
			}
		}
		State = $"walking to {t}";
		return SkillStatus.Running;
	}

	private int arrivedOutOfReach;
	public const int MaxArrivedOutOfReach = 3;

	private SkillStatus GiveUp(BotContext ctx, TilePos t, string why)
	{
		ctx.Log.Info($"Mine: skipping {t}: {why}");
		bad.Add(t);
		target = null;
		sub?.Stop(ctx);
		sub = null;
		return SkillStatus.Running;
	}

	/// <summary>Nearest safe tile of the type, weighting depth (digging down is cheap, climbing back is not).</summary>
	private TilePos? PickTarget(BotContext ctx)
	{
		TilePos node = ctx.Node;
		MoveContext moves = ctx.Moves;
		TilePos? best = null;
		float bestScore = float.MaxValue;
		foreach (TilePos c in ctx.World.FindTiles(TileType, node, SearchRadius)) {
			if (bad.Contains(c) || ctx.UnreachableTiles.Contains(c) || !SafeToBreak(ctx, moves, c) || NextToLiquid(ctx.Grid, c))
				continue;
			if (c.Y > ctx.DeepestRow || ctx.OffLimits(c))
				continue; // deeper than the gear is fit for, or where the bot was killed lately
			float score = Math.Abs(c.X - node.X) + 1.5f * Math.Abs(c.Y - node.Y);
			// The row the bot stands on, under its feet and a step either side, last: seen in the settle scenario, it mined
			// the stone it stood on tile by tile, placing a block each time to keep its footing, and the count never rose.
			if (c.Y == node.Y + 1 && c.X >= node.X - 1 && c.X <= node.X + 2)
				score += UnderFootPenalty;
			if (score < bestScore) {
				bestScore = score;
				best = c;
			}
		}
		return best;
	}

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
