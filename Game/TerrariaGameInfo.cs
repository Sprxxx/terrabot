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
public sealed class TerrariaGameInfo : IWorldInfo, IInventory, ICrafter, IGameKnowledge
{
	public const int MainInventorySlots = 50;
	public const int TreeScanX = 50, TreeScanUp = 25, TreeScanDown = 15;
	public const int StationScan = 25;

	private Player bound;
	/// <summary>The bound player, or the local player before the first Bind (skills may start before the bot ticks).</summary>
	private Player player => bound ?? Main.LocalPlayer;

	/// <summary>IDs for the current world: the iron tier (iron or lead) is chosen at world generation.</summary>
	public static GameIds CreateIds() => new() {
		Wood = ItemID.Wood,
		WorkBenchItem = ItemID.WorkBench,
		WorkBenchTile = TileID.WorkBenches,
		WoodenSword = ItemID.WoodenSword,
		WoodenHammer = ItemID.WoodenHammer,
		Acorn = ItemID.Acorn,
		Gel = ItemID.Gel,
		Torch = ItemID.Torch,
		FurnaceTile = TileID.Furnaces,
		AnvilTile = TileID.Anvils,
		IronTierPickaxe = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadPickaxe : ItemID.IronPickaxe,
		IronTierBroadsword = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadBroadsword : ItemID.IronBroadsword,
		IronTierHelmet = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadHelmet : ItemID.IronHelmet,
		IronTierChainmail = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadChainmail : ItemID.IronChainmail,
		IronTierGreaves = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadGreaves : ItemID.IronGreaves,
		SilverTierHelmet = WorldGen.SavedOreTiers.Silver == TileID.Tungsten ? ItemID.TungstenHelmet : ItemID.SilverHelmet,
		SilverTierPickaxe = WorldGen.SavedOreTiers.Silver == TileID.Tungsten ? ItemID.TungstenPickaxe : ItemID.SilverPickaxe,
		SilverTierBroadsword = WorldGen.SavedOreTiers.Silver == TileID.Tungsten ? ItemID.TungstenBroadsword : ItemID.SilverBroadsword,
		SilverTierChainmail = WorldGen.SavedOreTiers.Silver == TileID.Tungsten ? ItemID.TungstenChainmail : ItemID.SilverChainmail,
		SilverTierGreaves = WorldGen.SavedOreTiers.Silver == TileID.Tungsten ? ItemID.TungstenGreaves : ItemID.SilverGreaves,
		WoodWall = ItemID.WoodWall,
		Door = ItemID.WoodenDoor,
		Chair = ItemID.WoodenChair,
		Table = ItemID.WoodenTable,
		BuildingBlocks = new int[] { ItemID.StoneBlock, ItemID.DirtBlock },
		DesertTiles = new int[] { TileID.Sand, TileID.HardenedSand, TileID.Sandstone, TileID.DesertFossil },
		EvilTiles = new int[] { TileID.Ebonstone, TileID.Crimstone, TileID.CorruptGrass, TileID.CrimsonGrass, TileID.Ebonsand, TileID.Crimsand,
			TileID.CorruptHardenedSand, TileID.CrimsonHardenedSand, TileID.CorruptSandstone, TileID.CrimsonSandstone, TileID.CorruptIce, TileID.FleshIce },
		EvilEnemies = new int[] { NPCID.EaterofSouls, NPCID.DevourerHead, NPCID.DevourerBody, NPCID.DevourerTail, NPCID.Crimera,
			NPCID.FaceMonster, NPCID.BloodCrawler, NPCID.BloodCrawlerWall },
		DesertEnemies = new int[] { NPCID.Antlion, NPCID.WalkingAntlion, NPCID.FlyingAntlion, NPCID.GiantWalkingAntlion, NPCID.GiantFlyingAntlion,
			NPCID.LarvaeAntlion, NPCID.TombCrawlerHead, NPCID.TombCrawlerBody, NPCID.TombCrawlerTail },
		LifeCrystal = ItemID.LifeCrystal,
		FloatingAccessories = new int[] { ItemID.FloatingTube }, // Player.ApplyEquipFunctional: canFloatInWater
		IronTierBow = WorldGen.SavedOreTiers.Iron == TileID.Lead ? ItemID.LeadBow : ItemID.IronBow,
		WoodenArrow = ItemID.WoodenArrow,
		Lens = ItemID.Lens,
		SuspiciousEye = ItemID.SuspiciousLookingEye,
		DemonAltarTile = TileID.DemonAltar,
		EyeBoss = NPCID.EyeofCthulhu,
		EyeServant = NPCID.ServantofCthulhu,
		Campfire = ItemID.Campfire,
		HeartLantern = ItemID.HeartLantern,
		RecallPotion = ItemID.RecallPotion,
	};


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
		IReadOnlyList<RecipeInfo> all = RecipesFor(itemType);
		if (all.Count == 0)
			return CraftResult.NoRecipe;
		// Not craftable now: if any recipe's ingredients are all carried, what is missing is a station in range.
		bool haveAll = all.Any(r => r.Ingredients.All(i => this.CountAny(i.Accepts) >= i.Stack));
		return haveAll ? CraftResult.NeedsStation : CraftResult.Missing;
	}

	/// <summary>All (unconditional) recipes for the item, e.g. "3 Iron Ore @ Furnaces | 4 Iron Fence by hand".</summary>
	public string Describe(int itemType)
	{
		IReadOnlyList<RecipeInfo> all = RecipesFor(itemType);
		if (all.Count == 0)
			return "no recipe";
		return string.Join(" | ", all.Take(4).Select(r => {
			string items = string.Join(", ", r.Ingredients.Select(i => $"{i.Stack} {ItemName(i.Item)}"));
			string tiles = r.Stations.Count == 0 ? "by hand" : "@ " + string.Join(", ", r.Stations.Select(TileName));
			return $"{items} {tiles}";
		})) + (all.Count > 4 ? $" | ... ({all.Count} recipes)" : "");
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

	// ---------- IWorldInfo: tile queries ----------

	public int TileTypeAt(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 1))
			return -1;
		Tile t = Main.tile[x, y];
		if (!t.HasTile)
			return -1;
		// A door the bot walked through is left open (auto-open): still the house's door. Read as open, the house's
		// blueprint lacked its door and the planner crafted and placed a new one every night (world_eye runs 51 and 55).
		return t.TileType == TileID.OpenDoor ? TileID.ClosedDoor : t.TileType;
	}

	/// <summary>
	/// Debug experiment only (self-test launch flag -terrabotxray): ore, crystals, stations and chests are found whether or
	/// not the map has revealed them, to measure how much time finding things costs. Never on in normal play.
	/// </summary>
	public static bool XRay { get; set; }

	public IReadOnlyList<TilePos> FindTiles(int tileType, TilePos around, int radius)
	{
		var found = new List<TilePos>();
		for (int x = around.X - radius; x <= around.X + radius; x++)
			for (int y = around.Y - radius; y <= around.Y + radius; y++)
				// Only what the player has seen: no x-ray. Exploring (and torchlight) reveals the map.
				if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == tileType && (XRay || Main.Map.IsRevealed(x, y)))
					found.Add(new TilePos(x, y));
		found.Sort((a, b) => a.ChebyshevTo(around).CompareTo(b.ChebyshevTo(around)));
		return found;
	}

	// ---------- IGameKnowledge (built once from the loaded recipes and items, mods included) ----------

	/// <summary>Natural tiles worth mining for their block/ore (the item that places the tile is what it drops).</summary>
	private static readonly HashSet<int> MineableTiles = new() {
		TileID.Stone, TileID.Dirt, TileID.ClayBlock, TileID.Mud, TileID.SnowBlock, TileID.Granite, TileID.Marble,
		TileID.Copper, TileID.Tin, TileID.Iron, TileID.Lead, TileID.Silver, TileID.Tungsten, TileID.Gold, TileID.Platinum,
	};

	/// <summary>Ore pairs: a world generates only one ore of each tier (WorldGen.SavedOreTiers).</summary>
	private static readonly (int a, int b, Func<int> chosen)[] OreTiers = {
		(TileID.Copper, TileID.Tin, () => WorldGen.SavedOreTiers.Copper),
		(TileID.Iron, TileID.Lead, () => WorldGen.SavedOreTiers.Iron),
		(TileID.Silver, TileID.Tungsten, () => WorldGen.SavedOreTiers.Silver),
		(TileID.Gold, TileID.Platinum, () => WorldGen.SavedOreTiers.Gold),
	};

	private static Dictionary<int, List<RecipeInfo>> recipeIndex;
	private static Dictionary<int, List<int>> placingIndex;
	private static HashSet<int> stationTiles;

	/// <summary>Every tile type some recipe needs as a station (looked for in the surroundings).</summary>
	private static HashSet<int> StationTiles
	{
		get {
			BuildIndex();
			return stationTiles;
		}
	}

	private static void BuildIndex()
	{
		if (recipeIndex != null)
			return;
		recipeIndex = new Dictionary<int, List<RecipeInfo>>();
		stationTiles = new HashSet<int>();
		for (int i = 0; i < Recipe.numRecipes; i++) {
			Recipe r = Main.recipe[i];
			if (r == null || r.Disabled || r.createItem.IsAir || r.Conditions.Count > 0)
				continue; // conditional recipes (near water, biome, ...) are left out for now
			var ingredients = new List<Ingredient>();
			foreach (Item req in r.requiredItem) {
				var accepts = new List<int> { req.type };
				foreach (int g in r.acceptedGroups)
					if (RecipeGroup.recipeGroups.TryGetValue(g, out RecipeGroup group) && group.ContainsItem(req.type))
						accepts.AddRange(group.ValidItems.Where(t => t != req.type).OrderBy(t => t));
				ingredients.Add(new Ingredient(req.stack, accepts.ToArray()));
			}
			var info = new RecipeInfo {
				Result = r.createItem.type,
				ResultStack = Math.Max(1, r.createItem.stack),
				Ingredients = ingredients,
				Stations = r.requiredTile.ToArray(),
			};
			if (!recipeIndex.TryGetValue(info.Result, out List<RecipeInfo> list))
				recipeIndex[info.Result] = list = new List<RecipeInfo>();
			list.Add(info);
			foreach (int t in r.requiredTile)
				stationTiles.Add(t);
		}
		placingIndex = new Dictionary<int, List<int>>();
		foreach (var (type, item) in ContentSamples.ItemsByType) {
			if (item == null || item.createTile <= -1)
				continue;
			if (!placingIndex.TryGetValue(item.createTile, out List<int> list))
				placingIndex[item.createTile] = list = new List<int>();
			list.Add(type);
		}
		foreach (List<int> list in placingIndex.Values)
			list.Sort();
	}

	public IReadOnlyList<RecipeInfo> RecipesFor(int itemType)
	{
		BuildIndex();
		return recipeIndex.TryGetValue(itemType, out List<RecipeInfo> list) ? list : Array.Empty<RecipeInfo>();
	}

	public IReadOnlyList<int> ItemsPlacing(int tileType)
	{
		BuildIndex();
		return placingIndex.TryGetValue(tileType, out List<int> list) ? list : Array.Empty<int>();
	}

	public int TilePlacedBy(int itemType) =>
		ContentSamples.ItemsByType.TryGetValue(itemType, out Item it) && it.createTile > -1 ? it.createTile : -1;

	/// <summary>Items a tile drops although the item does not place that tile (KillTile_GetItemDrops special cases).</summary>
	private static readonly Dictionary<int, int> DroppedByTile = new() {
		[ItemID.LifeCrystal] = TileID.Heart,
	};

	/// <summary>Items hunted from NPCs, by the NPCs' AI style (ItemDropDatabase: gel from slimes, lenses from demon eyes 1/3).</summary>
	private static readonly Dictionary<int, int> DroppedByNpcAi = new() {
		[ItemID.Lens] = NPCAIStyleID.DemonEye,
	};

	public ItemSource SourceOf(int itemType)
	{
		if (DroppedByTile.TryGetValue(itemType, out int dropTile))
			return new ItemSource(SourceKind.Mine, dropTile);
		if (DroppedByNpcAi.TryGetValue(itemType, out int ai))
			return new ItemSource(SourceKind.Hunt, NpcAiStyle: ai);
		if (itemType == ItemID.Wood)
			return new ItemSource(SourceKind.Tree);
		if (itemType == ItemID.Gel)
			return new ItemSource(SourceKind.Hunt, NpcAiStyle: NPCAIStyleID.Slime);
		int tile = TilePlacedBy(itemType);
		if (tile < 0 || !(MineableTiles.Contains(tile) || TileID.Sets.Ore[tile]))
			return ItemSource.None;
		foreach (var (a, b, chosen) in OreTiers)
			if ((tile == a || tile == b) && tile != chosen())
				return ItemSource.None; // the other ore of the pair: not in this world
		return new ItemSource(SourceKind.Mine, tile);
	}

	public string ItemName(int itemType) => Lang.GetItemNameValue(itemType);

	public string TileName(int tileType) => TileID.Search.TryGetName(tileType, out string name) ? name : $"tile {tileType}";

	// ---------- building (walls, footprints, housing) and armour ----------

	/// <summary>Life crystals are frame-important (2×2) but meant to be mined; any pickaxe breaks them.</summary>
	public bool CanMineTarget(int x, int y) =>
		WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.Heart && WorldGen.CanKillTile(x, y);

	/// <summary>Vanilla bosses' "downed" flags (the Eye of Cthulhu first; extend as bosses are added).</summary>
	/// <summary>Off the map counts as seen: there is nothing to explore past the world's edge.</summary>
	public bool IsRevealed(int x, int y) => !WorldGen.InWorld(x, y, 1) || Main.Map.IsRevealed(x, y);

	public float LightAt(int x, int y) => WorldGen.InWorld(x, y, 1) ? Lighting.Brightness(x, y) : 1f;

	public bool BossDefeated(int npcType) => npcType switch {
		NPCID.EyeofCthulhu => NPC.downedBoss1,
		NPCID.KingSlime => NPC.downedSlimeKing,
		NPCID.EaterofWorldsHead or NPCID.BrainofCthulhu => NPC.downedBoss2,
		NPCID.SkeletronHead => NPC.downedBoss3,
		NPCID.QueenBee => NPC.downedQueenBee,
		_ => false,
	};

	public bool HasWall(int x, int y) => WorldGen.InWorld(x, y, 1) && Main.tile[x, y].WallType > WallID.None;

	/// <summary>Mirror of the conditions in Player.PlaceThing_Walls (range aside): no wall yet, a tile or wall on a 4-neighbour.</summary>
	public bool CanPlaceWall(int wallItem, int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 2) || !ContentSamples.ItemsByType.TryGetValue(wallItem, out Item item) || item.createWall <= 0)
			return false;
		if (Main.tile[x, y].WallType == item.createWall)
			return false;
		static bool Anchors(int i, int j) => Main.tile[i, j].HasTile || Main.tile[i, j].WallType > WallID.None;
		return Anchors(x + 1, y) || Anchors(x - 1, y) || Anchors(x, y + 1) || Anchors(x, y - 1);
	}

	public (int Width, int Height, int OriginX, int OriginY) Footprint(int itemType)
	{
		if (!ContentSamples.ItemsByType.TryGetValue(itemType, out Item item) || item.createTile <= -1)
			return (1, 1, 0, 0);
		TileObjectData data = TileObjectData.GetTileData(item.createTile, item.placeStyle);
		return data == null ? (1, 1, 0, 0) : (data.Width, data.Height, data.Origin.X, data.Origin.Y);
	}

	/// <summary>
	/// The housing query (WorldGen.MoveTownNPC without moving anyone): StartRoomCheck, RoomNeeds and ScoreRoom.
	/// A room someone already lives in counts as a house (ScoreRoom reports that as hiScore -1).
	/// </summary>
	public string CheckHousing(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 10))
			return "outside the world";
		if (!WorldGen.StartRoomCheck(x, y))
			return $"room: {WorldGen.roomCheckFailureReason}";
		if (!WorldGen.RoomNeeds(NPCID.Guide)) {
			var missing = new List<string>();
			// Same sets RoomNeeds reads (its own flags are private); houseTile is filled by StartRoomCheck.
			static bool Has(int[] types) => types.Any(t => t < WorldGen.houseTile.Length && WorldGen.houseTile[t]);
			if (!Has(TileID.Sets.RoomNeeds.CountsAsChair)) missing.Add("chair");
			if (!Has(TileID.Sets.RoomNeeds.CountsAsTable)) missing.Add("table");
			if (!Has(TileID.Sets.RoomNeeds.CountsAsDoor)) missing.Add("door");
			if (!Has(TileID.Sets.RoomNeeds.CountsAsTorch)) missing.Add("light source");
			return "missing " + string.Join(", ", missing);
		}
		WorldGen.ScoreRoom(-1, NPCID.Guide);
		if (WorldGen.hiScore == -1)
			return null; // occupied: it is a house, someone lives there
		return WorldGen.hiScore > 0 ? null : WorldGen.hiScore <= -250 ? "too much corruption/crimson around" : "no spot an NPC can stand on";
	}

	public bool IsEquipped(int itemType)
	{
		for (int i = 0; i < AccessoryEnd; i++)
			if (player.armor[i].type == itemType && !player.armor[i].IsAir)
				return true;
		return false;
	}

	/// <summary>armor[0..2] are head, body, legs; armor[3..9] the accessory slots (some only in Expert/Master).</summary>
	private const int AccessoryStart = 3, AccessoryEnd = 10;

	private bool FreeAccessorySlot()
	{
		for (int i = AccessoryStart; i < AccessoryEnd; i++)
			if (player.IsItemSlotUnlockedAndUsable(i) && player.armor[i].IsAir)
				return true;
		return false;
	}

	public IReadOnlyList<int> UnwornAccessories()
	{
		var list = new List<int>();
		for (int i = 0; i < 50; i++) {
			Item it = player.inventory[i];
			if (!it.IsAir && it.accessory && !it.vanity && !list.Contains(it.type) && !IsEquipped(it.type))
				list.Add(it.type);
		}
		return list;
	}

	public IReadOnlyList<ChestInfo> SeenChests(TilePos around, int radius)
	{
		var list = new List<ChestInfo>();
		for (int i = 0; i < Main.maxChests; i++) {
			Chest c = Main.chest[i];
			if (c == null || Math.Abs(c.x - around.X) > radius || Math.Abs(c.y - around.Y) > radius || !XRay && !Main.Map.IsRevealed(c.x, c.y))
				continue;
			if (!Main.tile[c.x, c.y].HasTile || !TileID.Sets.BasicChest[Main.tile[c.x, c.y].TileType])
				continue; // dressers and other containers are left alone
			list.Add(new ChestInfo(new TilePos(c.x, c.y), Chest.IsLocked(c.x, c.y)));
		}
		list.Sort((a, b) => a.TopLeft.ChebyshevTo(around).CompareTo(b.TopLeft.ChebyshevTo(around)));
		return list;
	}

	public string LootChest(TilePos topLeft)
	{
		if (player.whoAmI != Main.myPlayer || Main.netMode == NetmodeID.MultiplayerClient)
			return null;
		int x = topLeft.X, y = topLeft.Y;
		if (!player.IsInTileInteractionRange(x, y, Terraria.DataStructures.TileReachCheckSettings.Simple) || Chest.IsLocked(x, y))
			return null;
		int index = Chest.FindChest(x, y);
		if (index < 0)
			return null;
		Chest chest = Main.chest[index];
		var before = chest.item.Where(i => i != null && !i.IsAir).Select(i => (i.Name, i.stack)).ToList();
		player.OpenChest(x, y, index);
		Terraria.UI.ChestUI.LootAll();
		player.chest = -1;
		Main.playerInventory = false;
		Recipe.FindRecipes();
		var left = chest.item.Where(i => i != null && !i.IsAir).Select(i => i.Name).ToHashSet();
		return string.Join(", ", before.Where(b => !left.Contains(b.Name)).Select(b => $"{b.Name} x{b.stack}"))
			+ (left.Count > 0 ? $" (left behind: {string.Join(", ", left)}; inventory full?)" : "");
	}

	/// <summary>Right-click equip (ItemSlot.SwapEquip on the inventory slot), only for the local player.</summary>
	public bool TryEquip(int itemType)
	{
		Item item = FindItem(itemType, out int slot);
		if (player.whoAmI != Main.myPlayer || item == null)
			return false;
		if (item.accessory && (!FreeAccessorySlot() || IsEquipped(itemType)))
			return false; // never swap a worn accessory out; the same one twice is not allowed anyway
		Terraria.UI.ItemSlot.SwapEquip(player.inventory, Terraria.UI.ItemSlot.Context.InventoryItem, slot);
		return IsEquipped(itemType);
	}

	/// <summary>Shift-click a worn accessory back into the first free inventory slot (a plain slot swap).</summary>
	public bool TryTakeOff(int itemType)
	{
		if (player.whoAmI != Main.myPlayer)
			return false;
		for (int a = AccessoryStart; a < AccessoryEnd; a++) {
			if (player.armor[a].IsAir || player.armor[a].type != itemType)
				continue;
			for (int i = 0; i < 50; i++)
				if (player.inventory[i].IsAir) {
					(player.inventory[i], player.armor[a]) = (player.armor[a], player.inventory[i]);
					return !IsEquipped(itemType);
				}
			return false; // nowhere to put it
		}
		return false;
	}
}
