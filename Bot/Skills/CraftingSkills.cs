using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Craft until the inventory holds <see cref="Count"/> of an item, one recipe click every
/// <see cref="ClickInterval"/> ticks (the crafting menu's own pace is about that). Fails with the crafter's
/// reason (missing ingredients, station out of range...), which the brain uses to decide what to do first.
/// </summary>
public sealed class CraftSkill : ISkill
{
	public const int ClickInterval = 10;
	public const int MaxBusyTicks = 120;

	private int cooldown;
	private int busy;

	public int ItemType { get; }
	public int Count { get; }
	public CraftResult LastResult { get; private set; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Craft";

	public CraftSkill(int itemType, int count = 1)
	{
		ItemType = itemType;
		Count = count;
	}

	public string Describe() => $"Craft item {ItemType} x{Count} [{LastResult}]";
	public void Start(BotContext ctx) => ctx.Log.Info($"Craft start: item {ItemType} x{Count}, needs {ctx.Crafter.Describe(ItemType)}");
	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.Inventory.Count(ItemType) >= Count) {
			ctx.Log.Info($"Craft done: item {ItemType} x{ctx.Inventory.Count(ItemType)}");
			return SkillStatus.Succeeded;
		}
		if (cooldown > 0) {
			cooldown--;
			return SkillStatus.Running;
		}
		LastResult = ctx.Crafter.TryCraft(ItemType);
		switch (LastResult) {
			case CraftResult.Crafted:
				cooldown = ClickInterval;
				return SkillStatus.Running;
			case CraftResult.Busy:
				if (++busy > MaxBusyTicks)
					return Fail(ctx, "crafting stayed blocked (cursor item / full inventory)");
				return SkillStatus.Running;
			default:
				return Fail(ctx, $"{LastResult}: needs {ctx.Crafter.Describe(ItemType)}");
		}
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Craft item {ItemType} failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>
/// Place one item (work bench, furnace, anvil...) near the bot: find the nearest cell within
/// <see cref="SearchX"/> columns and <see cref="SearchY"/> rows that the game accepts, walk until it is in tool
/// range if needed (<see cref="GoalReachTile"/>), aim at it and use the item until the stack shrinks. Cells the
/// bot could not reach, or that stayed refused once in range, are skipped. The game check also refuses cells
/// whose footprint overlaps the player, so a spot is re-picked when the bot ends up standing on it.
/// </summary>
public sealed class PlaceItemSkill : ISkill
{
	public const int MaxTicks = 60 * 20;
	public const int SearchX = 10;
	public const int SearchY = 3;
	/// <summary>Ticks of using the item on an in-range spot before giving up on that spot.</summary>
	public const int MaxUseTicksPerSpot = 60;
	public const int MaxBadSpots = 6;

	private readonly System.Collections.Generic.HashSet<TilePos> bad = new();
	private int startCount = -1;
	private int ticks;
	private int useTicks;
	private TilePos? spot;
	private WalkToSkill walk;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Place";
	public TilePos? PlacedAt { get; private set; }

	public PlaceItemSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Place item {ItemType} at {spot?.ToString() ?? "?"}" + (walk != null ? " > " + walk.Describe() : "");
	public void Start(BotContext ctx) => startCount = ctx.Inventory.Count(ItemType);

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (startCount < 0)
			startCount = ctx.Inventory.Count(ItemType);
		if (ctx.Inventory.Count(ItemType) < startCount) {
			PlacedAt = spot;
			ctx.Log.Info($"Placed item {ItemType} at {spot}");
			return SkillStatus.Succeeded;
		}
		if (startCount == 0)
			return Fail(ctx, "no such item in the inventory");
		if (++ticks > MaxTicks)
			return Fail(ctx, $"could not place it (last spot {spot?.ToString() ?? "none"})");
		if (bad.Count >= MaxBadSpots)
			return Fail(ctx, $"{bad.Count} spots tried, none worked");

		TilePos node = ctx.Node;
		if (spot == null) {
			spot = FindSpot(ctx, node);
			useTicks = 0;
			if (spot == null)
				return ticks > 30 ? Fail(ctx, $"no free floor within {SearchX} tiles") : SkillStatus.Running;
		}
		TilePos s = spot.Value;
		var reach = new GoalReachTile(s, ctx.Physics);
		if (walk == null && ctx.Body.Grounded && reach.InRange(node)) {
			if (!ctx.World.CanPlaceItem(ItemType, s.X, s.Y)) {
				// Refused from here (e.g. we stand where it would go): pick another spot.
				bad.Add(s);
				spot = null;
				return SkillStatus.Running;
			}
			if (++useTicks > MaxUseTicksPerSpot) {
				ctx.Log.Info($"Place: {s} accepted by the check but nothing was placed; trying elsewhere");
				bad.Add(s);
				spot = null;
				return SkillStatus.Running;
			}
			ctx.Output.Tool = ToolRequest.Item;
			ctx.Output.ToolItem = ItemType;
			ctx.Output.UseItem = true;
			ctx.Output.Aim(s.X, s.Y);
			return SkillStatus.Running;
		}
		if (walk == null) {
			walk = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 1 };
			walk.Start(ctx);
		}
		SkillStatus w = walk.Tick(ctx);
		if (w != SkillStatus.Running) {
			walk.Stop(ctx);
			walk = null;
			if (w == SkillStatus.Failed) {
				bad.Add(s);
				spot = null;
			}
		}
		return SkillStatus.Running;
	}

	/// <summary>Nearest acceptable cell (same row first, then rows above/below), skipping spots that failed.</summary>
	private TilePos? FindSpot(BotContext ctx, TilePos node)
	{
		TilePos? best = null;
		int bestScore = int.MaxValue;
		for (int dy = -SearchY; dy <= SearchY; dy++) {
			for (int dx = -SearchX; dx <= SearchX; dx++) {
				var c = new TilePos(node.X + dx, node.Y + dy);
				if (bad.Contains(c) || !ctx.World.CanPlaceItem(ItemType, c.X, c.Y))
					continue;
				int score = Math.Abs(dx) + 3 * Math.Abs(dy);
				if (score < bestScore) {
					bestScore = score;
					best = c;
				}
			}
		}
		return best;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Place item {ItemType} failed: {reason}");
		return SkillStatus.Failed;
	}
}
