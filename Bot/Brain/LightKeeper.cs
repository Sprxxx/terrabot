using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Lights the way like a player does: when it is dark where the bot stands and no torch is within
/// <see cref="Spacing"/> tiles, slip a torch onto a spot next to the body between two swings of whatever skill is
/// running (movement is left alone). Also keeps a few torches crafted from gel and wood (by hand, like the crafting
/// menu). The bot itself does not need the light (it reads tiles directly); this is so a watching player can see.
/// </summary>
public sealed class LightKeeper
{
	/// <summary>Brightness (vanilla Lighting.Brightness, 0..1) below which the bot's spot counts as dark.</summary>
	public const float DarkBelow = 0.25f;
	/// <summary>No new torch while one is within this many tiles (Chebyshev).</summary>
	public const int Spacing = 9;
	/// <summary>Look at the light this often (ticks); the torch scan is 19 × 19 cells.</summary>
	public const int CheckEvery = 20;
	/// <summary>Ticks to keep trying one placement before giving up on that spot.</summary>
	public const int MaxPlaceTicks = 12;
	/// <summary>
	/// Ticks a placement waits for the swing in hand to end (a pickaxe's is 20 or so, Item.useAnimation): three swings, then
	/// the spot is given up.
	/// </summary>
	public const int MaxSwingWaitTicks = 60;
	/// <summary>Craft torches while fewer than this are carried and gel and wood allow.</summary>
	public const int Reserve = 20;
	/// <summary>
	/// Torches not placed for light: the plan needs some (furnace 3, campfire 5). Seen in game: the keeper lit a detour
	/// with the arena's torches, and boss_auto then hunted gel for an hour. But at 12 (with 15 wood kept back below) the
	/// bot walked the caves in the dark for long stretches, holding exactly 12 torches and 15 wood (user report,
	/// 2026-10-14): one wood and one gel make 3 torches, and the planner gathers wood when a recipe needs it.
	/// </summary>
	public const int KeepForCrafting = 5;
	/// <summary>Wood left alone for the plan: torches only from what is above this.</summary>
	public const int SpareWood = 5;
	public const int CraftEvery = 300;

	private int ticks;
	private TilePos? placing;
	private int placeTicks, swingWaitTicks;
	private TilePos? lastFailed;

	public int Placed { get; private set; }

	/// <summary>Runs after the skill's tick: may replace this tick's item use with placing a torch.</summary>
	public void Tick(BotContext ctx)
	{
		ticks++;
		int torch = ctx.Ids.Torch;
		if (torch <= 0 || ctx.World == null || ctx.Inventory == null || ctx.BodyIssue != null)
			return;
		if (ticks % CraftEvery == 0)
			KeepStock(ctx, torch);
		if (placing is TilePos cell) {
			int torchTile = ctx.Knowledge?.TilePlacedBy(torch) ?? -1;
			if (ctx.World.TileTypeAt(cell.X, cell.Y) == torchTile && torchTile >= 0) {
				Placed++;
				ctx.Log.Info($"Light: torch #{Placed} at {cell} (light {ctx.World.LightAt(cell.X, cell.Y):0.00})");
				placing = null;
				return;
			}
			// Still swinging something else (the dig's pickaxe): the torch comes into the hand when the swing ends, as a player's
			// hotbar switch waits for it; the placement's ticks count from then.
			bool waiting = ctx.Combat.ItemAnimation > 0 && ctx.Combat.HeldItemType != torch;
			if ((waiting ? ++swingWaitTicks > MaxSwingWaitTicks : ++placeTicks > MaxPlaceTicks) || ctx.Inventory.Count(torch) <= 0) {
				lastFailed = cell;
				placing = null;
				return;
			}
			Use(ctx, torch, cell);
			return;
		}
		// Between swings, or during a dig's: the pickaxe swings one swing after another while a dig goes on (autoReuse), with a
		// tick between them at most, so a check every CheckEvery ticks that wanted the hand free met it once in hundreds of ticks.
		// Seen in game (user report, Journey run J2): digging straight down "elevator-style", no torches. Not a weapon's swing:
		// a fight's next swing is not held up for light.
		if (ticks % CheckEvery != 0 || ctx.Inventory.Count(torch) <= KeepForCrafting || ctx.Combat.ItemAnimation > 0 && ctx.Output.Tool != ToolRequest.Pickaxe)
			return;
		TilePos n = ctx.Node;
		if (ctx.World.LightAt(n.X, n.Y - 1) >= DarkBelow || TorchNear(ctx, n))
			return;
		TilePos? spot = Spot(ctx, torch, n);
		if (spot == null)
			return;
		placing = spot;
		placeTicks = swingWaitTicks = 0;
		Use(ctx, torch, spot.Value);
	}

	private static void Use(BotContext ctx, int torch, TilePos cell)
	{
		// Only the item use is taken over; the skill's movement stays.
		ctx.Output.Tool = ToolRequest.Item;
		ctx.Output.ToolItem = torch;
		ctx.Output.UseItem = true;
		ctx.Output.Aim(cell.X, cell.Y);
		ctx.Output.HasMouseAim = false;
	}

	private bool TorchNear(BotContext ctx, TilePos n)
	{
		int torchTile = ctx.Knowledge?.TilePlacedBy(ctx.Ids.Torch) ?? -1;
		if (torchTile < 0)
			return true;
		for (int x = n.X - Spacing; x <= n.X + Spacing; x++)
			for (int y = n.Y - Spacing; y <= n.Y + Spacing; y++)
				if (ctx.World.TileTypeAt(x, y) == torchTile)
					return true;
		return false;
	}

	/// <summary>A cell beside or in the body where the game would take a torch: head height first, then the sides.</summary>
	private TilePos? Spot(BotContext ctx, int torch, TilePos n)
	{
		foreach (var (dx, dy) in new[] { (0, -2), (1, -2), (-1, -2), (2, -2), (-1, -1), (2, -1), (0, -1), (1, -1), (-1, 0), (2, 0) }) {
			var c = new TilePos(n.X + dx, n.Y + dy);
			if (c == lastFailed || ctx.Grid.Get(c.X, c.Y).IsSolid())
				continue;
			if (ctx.World.CanPlaceItem(torch, c.X, c.Y))
				return c;
		}
		return null;
	}

	private static void KeepStock(BotContext ctx, int torch)
	{
		// The last of the wood is the plan's: torches made from it left the boss scenario 4 wood short in an arena with no trees.
		// And an errand's reserved wood is its own (BotContext.Reservations): torches only from what is spare over it.
		if (ctx.Inventory.Count(torch) >= Reserve || ctx.Inventory.Count(ctx.Ids.Gel) <= 0 || ctx.Spare(ctx.Ids.Wood) <= SpareWood || ctx.Crafter == null)
			return;
		CraftResult r = ctx.Crafter.TryCraft(torch);
		if (r == CraftResult.Crafted)
			ctx.Log.Info($"Light: crafted torches (now {ctx.Inventory.Count(torch)})");
	}
}
