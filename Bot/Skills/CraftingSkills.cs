using System;
using System.Linq;
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
	public string Key => $"Craft {ItemType} {Count}";

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
/// With no acceptable cell around, it makes one: a pocket beside the bot, on the floor it stands on, dug out to the
/// footprint (gaps in the floor under it filled with blocks), as a player does in a cave. Only when no pocket can be
/// made there does it walk to look elsewhere (±25, then ±50 tiles from where it started).
/// </summary>
public sealed class PlaceItemSkill : ISkill
{
	public const int MaxTicks = 60 * 60;
	public const int SearchX = 20;
	public const int SearchY = 3;
	/// <summary>Ticks of using the item on an in-range spot before giving up on that spot.</summary>
	public const int MaxUseTicksPerSpot = 60;
	public const int MaxBadSpots = 6;
	public const int MaxWanders = 4;
	public const int WanderTiles = 25;
	/// <summary>
	/// Pockets dug before walking off to look. Seen in game (measured run L1): underground on uneven cave floor, "no free
	/// floor within 20 tiles" 75 times, and the furnace went down 99,000 ticks (over a game day) later, after walking about.
	/// </summary>
	public const int MaxPockets = 3;
	/// <summary>Ticks on one cell of a pocket (mining it, or filling the floor under it) before that pocket is given up.</summary>
	public const int MaxTicksPerPocketCell = 180;
	private readonly System.Collections.Generic.List<TilePos> pocketWork = new();
	private readonly System.Collections.Generic.HashSet<TilePos> pocketFills = new();
	private readonly System.Collections.Generic.HashSet<TilePos> badPockets = new();
	private TilePos? pocketAim;
	private TilePos pocketFrom;
	private int pockets;
	private int pocketCellTicks;
	private WalkToSkill wander;
	private int wanders;
	private int wanderDir = 1;
	private TilePos? wanderOrigin;

	private readonly System.Collections.Generic.HashSet<TilePos> bad = new();
	private int startCount = -1;
	private int ticks;
	private int useTicks;
	private TilePos? spot;
	private WalkToSkill walk;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Place";
	public string Key => $"Place {ItemType}";
	public TilePos? PlacedAt { get; private set; }

	public PlaceItemSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Place item {ItemType} at {spot?.ToString() ?? "?"}" + (walk != null ? " > " + walk.Describe() : "");
	public void Start(BotContext ctx) => startCount = ctx.Inventory.Count(ItemType);

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		wander?.Stop(ctx);
		wander = null;
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
		wander?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		walk?.Resume(ctx);
		wander?.Resume(ctx);
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
		if (wander != null) {
			if (wander.Tick(ctx) == SkillStatus.Running)
				return SkillStatus.Running;
			wander.Stop(ctx);
			wander = null;
		}
		if (pocketAim != null && WorkPocket(ctx, node))
			return SkillStatus.Running;
		if (spot == null) {
			spot = FindSpot(ctx, node);
			useTicks = 0;
			if (spot == null) {
				if (ticks <= 30)
					return SkillStatus.Running; // give perception a moment (just landed, a drop being picked up...)
				if (pockets < MaxPockets && ctx.Body.Grounded && StartPocket(ctx, node))
					return SkillStatus.Running;
				if (++wanders > MaxWanders)
					return Fail(ctx, $"no free floor within {SearchX} tiles, here or {MaxWanders} places around");
				// Uneven or dug-up ground: walk a bit (alternating sides) and look again.
				wanderOrigin ??= node;
				int far = WanderTiles * ((wanders + 1) / 2); // +25, -25, +50, -50 from where the search started
				var goal = new TilePos(wanderOrigin.Value.X + wanderDir * far, node.Y - 5);
				wanderDir = -wanderDir;
				ctx.Log.Info($"Place: no free floor within {SearchX} tiles of {node}; looking towards {goal}");
				wander = new WalkToSkill(goal) { Quiet = true, Tolerance = 3 };
				wander.Start(ctx);
				return SkillStatus.Running;
			}
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

	/// <summary>
	/// Choose a pocket beside the bot for the footprint, its bottom row on the floor the bot stands on: every cell in it
	/// empty or a block it may mine, the floor under it solid or a gap a block can fill, all in reach from here, no
	/// liquid. The fewest cells to change wins.
	/// </summary>
	private bool StartPocket(BotContext ctx, TilePos node)
	{
		var (w, h, ox, oy) = ctx.World.Footprint(ItemType);
		if (w <= 0 || h <= 0 || ctx.Blocks == null)
			return false;
		ITileGrid g = ctx.Grid;
		System.Collections.Generic.List<TilePos> bestWork = null;
		System.Collections.Generic.HashSet<TilePos> bestFills = null;
		TilePos bestAim = default;
		int bestCost = int.MaxValue;
		foreach (int side in new[] { 1, -1 }) {
			for (int gap = 0; gap <= 1; gap++) {
				int x0 = side > 0 ? node.X + Body.WidthTiles + gap : node.X - w - gap;
				int y0 = node.Y - h + 1;
				var aim = new TilePos(x0 + ox, y0 + oy);
				if (badPockets.Contains(aim) || bad.Contains(aim))
					continue;
				var fills = new System.Collections.Generic.List<TilePos>();
				var digs = new System.Collections.Generic.List<TilePos>();
				bool ok = true;
				for (int cx = x0; cx < x0 + w && ok; cx++) {
					var floor = new TilePos(cx, node.Y + 1);
					CellKind f = g.Get(floor.X, floor.Y);
					if (f.IsLiquid() || !InReach(ctx, node, floor))
						ok = false;
					else if (!f.IsSolid()) {
						if (f.IsBlockTile() || !ctx.Blocks.CanPlaceAt(floor.X, floor.Y))
							ok = false; // a slope or half block: not a floor for furniture, and not a cell a block goes into
						else
							fills.Add(floor);
					}
					for (int cy = y0; cy <= node.Y && ok; cy++) {
						var c = new TilePos(cx, cy);
						CellKind k = g.Get(c.X, c.Y);
						if (k.IsLiquid() || !InReach(ctx, node, c))
							ok = false;
						else if (k.IsBlockTile()) {
							if (!MineSkill.SafeToBreak(ctx, ctx.Moves, c))
								ok = false;
							else
								digs.Add(c);
						}
						else if (k.IsPlatform())
							ok = false;
					}
				}
				if (!ok || fills.Count > ctx.Blocks.BlockCount)
					continue;
				int cost = digs.Count + 2 * fills.Count + gap;
				if (cost < bestCost) {
					bestCost = cost;
					bestAim = aim;
					bestFills = new System.Collections.Generic.HashSet<TilePos>(fills);
					digs.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)); // top down: nothing left hanging over the bot
					bestWork = fills.Concat(digs).ToList();
				}
			}
		}
		if (bestWork == null)
			return false;
		pockets++;
		pocketAim = bestAim;
		pocketFrom = node;
		pocketCellTicks = 0;
		pocketWork.Clear();
		pocketWork.AddRange(bestWork);
		pocketFills.Clear();
		pocketFills.UnionWith(bestFills);
		ctx.Log.Info($"Place: no free floor within {SearchX} tiles of {node}; making room at {bestAim} ({pocketWork.Count - pocketFills.Count} to mine, {pocketFills.Count} to fill)");
		return true;
	}

	private static bool InReach(BotContext ctx, TilePos node, TilePos c) => new GoalReachTile(c, ctx.Physics).InRange(node);

	/// <summary>One tick of digging the pocket; false once it is done (the spot is then its aim) or given up.</summary>
	private bool WorkPocket(BotContext ctx, TilePos node)
	{
		TilePos aim = pocketAim.Value;
		if (node != pocketFrom) {
			// Knocked off the spot (a fight, a fall): choose again from where the bot is now.
			pocketAim = null;
			return false;
		}
		while (pocketWork.Count > 0 && PocketCellDone(ctx, pocketWork[0])) {
			pocketWork.RemoveAt(0);
			pocketCellTicks = 0;
		}
		if (pocketWork.Count == 0) {
			pocketAim = null;
			spot = aim;
			useTicks = 0;
			return false;
		}
		TilePos c = pocketWork[0];
		if (++pocketCellTicks > MaxTicksPerPocketCell) {
			ctx.Log.Info($"Place: could not {(pocketFills.Contains(c) ? "fill" : "mine")} {c} for the pocket at {aim}; trying elsewhere");
			badPockets.Add(aim);
			pocketAim = null;
			return false;
		}
		if (!ctx.Body.Grounded)
			return true;
		ctx.Output.UseItem = true;
		ctx.Output.Aim(c.X, c.Y);
		ctx.Output.Tool = pocketFills.Contains(c) ? ToolRequest.Block : ToolRequest.Pickaxe;
		return true;
	}

	private bool PocketCellDone(BotContext ctx, TilePos c) =>
		pocketFills.Contains(c) ? ctx.Grid.Get(c.X, c.Y).IsSolid() : !ctx.Grid.Get(c.X, c.Y).IsBlockTile();

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
