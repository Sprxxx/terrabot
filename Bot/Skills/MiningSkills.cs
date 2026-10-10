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
	public const int MaxExplores = 8;
	public const int CollectRadius = 12;
	/// <summary>Unreachable targets in a row before giving up (nearby tiles tend to be unreachable for the same reason).</summary>
	public const int MaxUnreachableInARow = 4;

	private readonly HashSet<TilePos> bad = new();
	private ISkill sub;
	private TilePos? target;
	private int mineTicks;
	private int explores;
	private int exploreDir = 1;
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

	public void Start(BotContext ctx) =>
		ctx.Log.Info($"Mine start: {Label(ctx)} x{Amount} (have {ctx.Inventory.Count(ItemType)}), pick power {ctx.PickPower}");

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

		if (ctx.Drops.Any(d => d.Type == ItemType && Near(ctx, d))) {
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, CollectRadius), "collecting");
			return SkillStatus.Running;
		}
		if (PickTarget(ctx) is TilePos next) {
			target = next;
			mineTicks = 0;
			ctx.Log.Debug($"Mine: target {next}");
			return SkillStatus.Running;
		}
		if (++explores > MaxExplores)
			return Fail(ctx, $"no {Label(ctx)} found after {MaxExplores} explorations");
		TilePos node = ctx.Node;
		var goal = new TilePos(node.X + exploreDir * ExploreSideways, node.Y + ExploreDepth);
		exploreDir = -exploreDir;
		ctx.Log.Info($"Mine: no {Label(ctx)} within {SearchRadius} tiles, digging down towards {goal}");
		Begin(ctx, new WalkToSkill(goal) { Quiet = true, Tolerance = 3 }, "exploring");
		return SkillStatus.Running;
	}

	private SkillStatus TickTarget(BotContext ctx, TilePos t)
	{
		if (ctx.World.TileTypeAt(t.X, t.Y) != TileType) {
			// Mined (or changed under us): the drop falls nearby; pick it up before the next tile.
			Mined++;
			unreachableInARow = 0;
			target = null;
			sub?.Stop(ctx);
			sub = null;
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, CollectRadius), "collecting");
			return SkillStatus.Running;
		}
		var reach = new GoalReachTile(t, ctx.Physics, ctx.PickRangeBoost);
		TilePos node = ctx.Node;
		if (ctx.Body.Grounded && reach.IsGoal(node)) {
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
			sub = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 1 };
			sub.Start(ctx);
		}
		SkillStatus s = sub.Tick(ctx);
		if (s == SkillStatus.Failed) {
			string why = ((WalkToSkill)sub).FailureReason;
			if (++unreachableInARow >= MaxUnreachableInARow)
				return Fail(ctx, $"{unreachableInARow} {Label(ctx)} tiles in a row unreachable" + (ctx.AllowBreak ? "" : " (digging is off)") + $"; last: {why}");
			return GiveUp(ctx, t, "unreachable: " + why);
		}
		if (s == SkillStatus.Succeeded) {
			sub.Stop(ctx);
			sub = null;
		}
		State = $"walking to {t}";
		return SkillStatus.Running;
	}

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
			if (bad.Contains(c) || !SafeToBreak(moves, c))
				continue;
			float score = Math.Abs(c.X - node.X) + 1.5f * Math.Abs(c.Y - node.Y);
			if (score < bestScore) {
				bestScore = score;
				best = c;
			}
		}
		return best;
	}

	public static bool SafeToBreak(MoveContext moves, TilePos c) =>
		moves.Blocks != null && !float.IsInfinity(BlockRules.BreakCost(moves, c, Array.Empty<TilePos>()));

	private static bool Near(BotContext ctx, DropInfo d)
	{
		PhysicsParams p = ctx.Physics;
		float dx = d.CenterX - ctx.Body.CenterX(p), dy = d.CenterY - ctx.Body.CenterY(p);
		return dx * dx + dy * dy <= CollectRadius * 16f * CollectRadius * 16f;
	}

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
