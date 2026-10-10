using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerraBot.Bot;
using TerraBot.Core.Grid;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerraBot.Game;

/// <summary>
/// Game-side implementation of the skills' world, inventory and crafting views for the local player.
///
/// Crafting goes through <see cref="Main.CraftItem"/>, the very function the crafting menu runs when you click a
/// recipe: it only offers recipes vanilla's <see cref="Recipe.FindRecipes"/> lists as available (ingredients in
/// the inventory, required stations in range), consumes the ingredients and puts the result on the cursor, which
/// is then moved into the inventory like a click on an empty slot does.
/// </summary>
public sealed class TerrariaGameInfo : IWorldInfo, IInventory, ICrafter
{
	public const int MainInventorySlots = 50;
	public const int TreeScanX = 50, TreeScanUp = 25, TreeScanDown = 15;
	public const int StationScan = 25;

	private Player bound;
	/// <summary>The bound player, or the local player before the first Bind (skills may start before the bot ticks).</summary>
	private Player player => bound ?? Main.LocalPlayer;

	public static readonly GameIds Ids = new() {
		Wood = ItemID.Wood,
		WorkBenchItem = ItemID.WorkBench,
		WorkBenchTile = TileID.WorkBenches,
		WoodenSword = ItemID.WoodenSword,
		WoodenHammer = ItemID.WoodenHammer,
		Acorn = ItemID.Acorn,
	};

	/// <summary>Station tile types the planner may ask about.</summary>
	private static readonly int[] StationTiles = { TileID.WorkBenches };

	public void Bind(Player p) => bound = p;

	// ---------- IWorldInfo ----------

	public bool IsTreeTrunk(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 1))
			return false;
		Tile t = Main.tile[x, y];
		return t.HasTile && TileID.Sets.IsATreeTrunk[t.TileType];
	}

	public bool CanPlaceItem(int itemType, int x, int y)
	{
		Item item = FindItem(itemType, out _);
		if (item == null || item.createTile <= -1 || !WorldGen.InWorld(x, y, 2)) // -1 = places nothing
			return false;
		TileObjectData data = TileObjectData.GetTileData(item.createTile, item.placeStyle);
		int w = data?.Width ?? 1, h = data?.Height ?? 1;
		int ox = data?.Origin.X ?? 0, oy = data?.Origin.Y ?? 0;
		var footprint = new Rectangle((x - ox) * 16, (y - oy) * 16, w * 16, h * 16);
		if (footprint.Intersects(player.Hitbox))
			return false; // vanilla refuses to place furniture over players; keep clear of our own body
		if (data == null)
			return !Main.tile[x, y].HasTile;
		return TileObject.CanPlace(x, y, item.createTile, item.placeStyle, player.direction, out _, onlyCheck: true);
	}

	// ---------- IInventory ----------

	public int Count(int itemType)
	{
		int n = 0;
		for (int i = 0; i < 58; i++)
			if (player.inventory[i].type == itemType)
				n += player.inventory[i].stack;
		return n;
	}

	public bool HasRoomFor(int itemType)
	{
		for (int i = 0; i < MainInventorySlots; i++) {
			Item it = player.inventory[i];
			if (it.IsAir || (it.type == itemType && it.stack < it.maxStack))
				return true;
		}
		return false;
	}

	/// <summary>The first stack of this item in the main inventory (hotbar first).</summary>
	public Item FindItem(int itemType, out int slot)
	{
		for (slot = 0; slot < MainInventorySlots; slot++)
			if (player.inventory[slot].type == itemType && player.inventory[slot].stack > 0)
				return player.inventory[slot];
		slot = -1;
		return null;
	}

	// ---------- ICrafter ----------

	public CraftResult TryCraft(int itemType)
	{
		if (!Main.mouseItem.IsAir || !HasRoomFor(itemType))
			return CraftResult.Busy;
		// Vanilla refreshes nearby crafting stations (adjTile) only while the inventory screen is open, i.e. when
		// the crafting menu is visible. Do that same range check here before asking which recipes are available.
		player.AdjTiles();
		Recipe.FindRecipes();
		for (int i = 0; i < Main.numAvailableRecipes; i++) {
			Recipe r = Main.recipe[Main.availableRecipe[i]];
			if (r.createItem.type != itemType)
				continue;
			Main.CraftItem(r);
			// The craft lands on the cursor; put it into the inventory like clicking an empty slot would.
			Main.mouseItem = player.GetItem(player.whoAmI, Main.mouseItem, GetItemSettings.InventoryUIToInventorySettings);
			if (!Main.mouseItem.IsAir) {
				player.QuickSpawnItem(player.GetSource_FromThis(), Main.mouseItem, Main.mouseItem.stack);
				Main.mouseItem.TurnToAir();
			}
			Recipe.FindRecipes();
			return CraftResult.Crafted;
		}
		Recipe recipe = RecipeFor(itemType);
		if (recipe == null)
			return CraftResult.NoRecipe;
		bool haveAll = recipe.requiredItem.All(req => Count(req.type) >= req.stack);
		return haveAll ? CraftResult.NeedsStation : CraftResult.Missing;
	}

	public string Describe(int itemType)
	{
		Recipe r = RecipeFor(itemType);
		if (r == null)
			return "no recipe";
		string items = string.Join(", ", r.requiredItem.Select(i => $"{i.stack} {i.Name}"));
		string tiles = r.requiredTile.Count == 0 ? "by hand" : "@ " + string.Join(", ", r.requiredTile.Select(t => TileID.Search.GetName(t)));
		return $"{items} {tiles}";
	}

	private static Recipe RecipeFor(int itemType)
	{
		for (int i = 0; i < Recipe.numRecipes; i++)
			if (Main.recipe[i].createItem.type == itemType && !Main.recipe[i].Disabled)
				return Main.recipe[i];
		return null;
	}

	// ---------- Surroundings ----------

	/// <summary>Base trunk tiles (a trunk tile with trunk above and none below: excludes roots and branches).</summary>
	public List<TilePos> ScanTrees(TilePos around)
	{
		var trees = new List<TilePos>();
		for (int x = around.X - TreeScanX; x <= around.X + TreeScanX; x++) {
			for (int y = around.Y - TreeScanUp; y <= around.Y + TreeScanDown; y++) {
				if (IsTreeTrunk(x, y) && IsTreeTrunk(x, y - 1) && !IsTreeTrunk(x, y + 1))
					trees.Add(new TilePos(x, y));
			}
		}
		trees.Sort((a, b) => (Math.Abs(a.X - around.X) + Math.Abs(a.Y - around.Y)).CompareTo(Math.Abs(b.X - around.X) + Math.Abs(b.Y - around.Y)));
		return trees;
	}

	public Dictionary<int, TilePos> ScanStations(TilePos around)
	{
		var found = new Dictionary<int, TilePos>();
		for (int x = around.X - StationScan; x <= around.X + StationScan; x++) {
			for (int y = around.Y - StationScan; y <= around.Y + StationScan; y++) {
				if (!WorldGen.InWorld(x, y, 1))
					continue;
				Tile t = Main.tile[x, y];
				if (!t.HasTile || !StationTiles.Contains(t.TileType))
					continue;
				var pos = new TilePos(x, y);
				if (!found.TryGetValue(t.TileType, out TilePos old) || pos.ChebyshevTo(around) < old.ChebyshevTo(around))
					found[t.TileType] = pos;
			}
		}
		return found;
	}
}
