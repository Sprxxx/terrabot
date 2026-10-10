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
/// Place one item (e.g. a work bench) on the floor next to the bot: pick the nearest cell on the feet row that
/// the game accepts and that does not overlap the body, aim at it and use the item until the stack shrinks.
/// </summary>
public sealed class PlaceItemSkill : ISkill
{
	public const int MaxTicks = 60 * 6;
	private static readonly int[] Offsets = { 2, -1, 3, -2, 4, -3, 5, -4 };

	private int startCount = -1;
	private int ticks;
	private TilePos? spot;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Place";
	public TilePos? PlacedAt { get; private set; }

	public PlaceItemSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Place item {ItemType} at {spot?.ToString() ?? "?"}";
	public void Start(BotContext ctx) => startCount = ctx.Inventory.Count(ItemType);
	public void Stop(BotContext ctx) { }

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
		if (!ctx.Body.Grounded)
			return SkillStatus.Running;

		TilePos node = ctx.Node;
		if (spot == null || !ctx.World.CanPlaceItem(ItemType, spot.Value.X, spot.Value.Y)) {
			spot = null;
			foreach (int dx in Offsets) {
				int x = dx > 0 ? node.X + Body.WidthTiles - 1 + dx : node.X + dx;
				if (ctx.World.CanPlaceItem(ItemType, x, node.Y)) {
					spot = new TilePos(x, node.Y);
					break;
				}
			}
			if (spot == null)
				return ticks > 30 ? Fail(ctx, "no free floor next to the bot") : SkillStatus.Running;
		}
		ctx.Output.Tool = ToolRequest.Item;
		ctx.Output.ToolItem = ItemType;
		ctx.Output.UseItem = true;
		ctx.Output.Aim(spot.Value.X, spot.Value.Y);
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Place item {ItemType} failed: {reason}");
		return SkillStatus.Failed;
	}
}
