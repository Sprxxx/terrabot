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
		Coins = new int[] { ItemID.CopperCoin, ItemID.SilverCoin, ItemID.GoldCoin, ItemID.PlatinumCoin },
		PickupBlockers = new int[] { ItemID.EncumberingStone }, // Player.RefreshMechanicalAccsFromItemType: preventAllItemPickups
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
		// Milestone 10 (docs M10_SPEC.md): the Eater of Worlds and the Worm Food path.
		EvilBoss = NPCID.EaterofWorldsHead,
		EvilBossBody = NPCID.EaterofWorldsBody,
		EvilBossTail = NPCID.EaterofWorldsTail,
		WormFood = ItemID.WormFood,
		VilePowder = ItemID.VilePowder,
		VileMushroom = ItemID.VileMushroom,
		CorruptGrass = TileID.CorruptGrass,
		CorruptThorns = TileID.CorruptThorns,
		RottenChunk = ItemID.RottenChunk,
		WormTooth = ItemID.WormTooth,
		UnholyArrow = ItemID.UnholyArrow,
		DemoniteOre = ItemID.DemoniteOre,
		DemoniteBar = ItemID.DemoniteBar,
		DemonBow = ItemID.DemonBow,
		Bottle = ItemID.Bottle,
		BottleTile = TileID.Bottles,
		CorruptionWeights = CorruptionZoneWeights(),
		TileExplosives = TileExplosiveInfos(),
		// Milestone 11 (docs M11_SPEC.md): Skeletron, the Old Man, the Dungeon Guardian; the Dungeon's bricks, and the snow and
		// jungle surfaces that tell its side (WorldGen.cs:8209-8259).
		SkeletronHead = NPCID.SkeletronHead,
		SkeletronHand = NPCID.SkeletronHand,
		OldMan = NPCID.OldMan,
		DungeonGuardian = NPCID.DungeonGuardian,
		DungeonBrickTiles = new int[] { TileID.BlueDungeonBrick, TileID.GreenDungeonBrick, TileID.PinkDungeonBrick },
		SnowTiles = new int[] { TileID.SnowBlock, TileID.IceBlock },
		JungleTiles = new int[] { TileID.JungleGrass },
		HealingPotion = ItemID.HealingPotion,
		LesserHealingPotion = ItemID.LesserHealingPotion,
		// Milestone 12 (docs M12_SPEC.md): the Wall of Flesh, the Guide and his Voodoo Doll, the Voodoo Demon, the Underworld.
		Guide = NPCID.Guide,
		GuideVoodooDoll = ItemID.GuideVoodooDoll,
		WallOfFlesh = NPCID.WallofFlesh,
		WallOfFleshEye = NPCID.WallofFleshEye,
		TheHungry = NPCID.TheHungry,
		TheHungryII = NPCID.TheHungryII,
		LeechHead = NPCID.LeechHead,
		LeechBody = NPCID.LeechBody,
		LeechTail = NPCID.LeechTail,
		VoodooDemon = NPCID.VoodooDemon,
		HellforgeTile = TileID.Hellforge,
		HellstoneTile = TileID.Hellstone,
		AshTile = TileID.Ash,
		Hellstone = ItemID.Hellstone,
		Obsidian = ItemID.Obsidian,
		AshBlock = ItemID.AshBlock,
		ShadowScale = ItemID.ShadowScale,
		Heart = ItemID.Heart,
	};

	/// <summary>
	/// The Bomb (projectile 28) and Dynamite (29), the thrown explosives that break tiles; the Grenade's blast (30) only
	/// breaks cracked bricks (Projectile.Kill: ExplodeCrackedTiles for 30, ExplodeTiles for 28 radius 4 and 29 radius 7).
	/// Blast box and damage: Projectile.PrepareBombToBlow (Resize(128, 128), 100; Resize(250, 250), 250). Throw speed and
	/// swing from the item (Item.SetDefaults 166: shootSpeed 5, useAnimation 25; 167: 4, 40). Fuse: the wiki's 3 s and 5 s.
	/// </summary>
	private static ExplosiveInfo[] TileExplosiveInfos()
	{
		// The item's own numbers when the samples are there (ModPlayer.Initialize can run before they are), else the decompile's.
		ExplosiveInfo Of(int item, string name, int radius, float halfPx, int damage, int fuse, float speed, int use, int size, float friction, float bounce) =>
			ContentSamples.ItemsByType.TryGetValue(item, out Item it) && it.shoot > ProjectileID.None
				? new ExplosiveInfo(item, it.Name, radius, halfPx, damage, fuse, it.shootSpeed, it.useAnimation, size, friction, bounce)
				: new ExplosiveInfo(item, name, radius, halfPx, damage, fuse, speed, use, size, friction, bounce);
		return new[] {
			Of(ItemID.Bomb, "Bomb", 4, 64f, 100, 180, 5f, 25, 22, 0.97f, 0.4f),
			Of(ItemID.Dynamite, "Dynamite", 7, 125f, 250, 300, 4f, 40, 10, 0.97f * 0.99f, 0.4f * 0.8f),
		};
	}

	/// <summary>A projectile only for asking Projectile.CanExplodeTile, which reads the tile alone (and TileLoader.CanExplode).</summary>
	private static readonly Projectile explodeProbe = new();

	/// <summary>
	/// What each tile counts for Player.ZoneCorrupt, as tModLoader sums the scene scan (TileLoader.cs:1640-1652): the
	/// TileID.Sets.CorruptBiome weights (RemixCorruptBiome in a remix world), mods' tiles included.
	/// </summary>
	private static Dictionary<int, int> CorruptionZoneWeights()
	{
		int[] set = Main.remixWorld ? TileID.Sets.RemixCorruptBiome : TileID.Sets.CorruptBiome;
		var weights = new Dictionary<int, int>();
		for (int t = 0; t < set.Length; t++)
			if (set[t] != 0)
				weights[t] = set[t];
		return weights;
	}


	public void Bind(Player p) => bound = p;

	// ---------- IWorldInfo ----------

	public bool IsTreeTrunk(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 1))
			return false;
		Tile t = Main.tile[x, y];
		return t.HasTile && TileID.Sets.IsATreeTrunk[t.TileType];
	}

	public int TreeWood(int x, int y)
	{
		if (!IsTreeTrunk(x, y))
			return 0;
		// The trees that drop wood (WorldGen.KillTile_GetItemDrops: tiles 5, 596, 616, 634 go to KillTile_GetTreeDrops); gem
		// trees (583-589) drop stone and gems, mushroom trees (72) mushrooms.
		ushort type = Main.tile[x, y].TileType;
		if (type != TileID.Trees && type != TileID.VanityTreeSakura && type != TileID.VanityTreeYellowWillow && type != TileID.TreeAsh)
			return 0;
		// The ground: the tile under the base trunk tile (a base, as ScanTrees finds it, has no trunk under it), where
		// WorldGen.GetTreeBottom's walk down past the trunk ends.
		if (!WorldGen.InWorld(x, y + 1, 1) || !Main.tile[x, y + 1].HasTile)
			return ItemID.Wood;
		int ground = Main.tile[x, y + 1].TileType;
		// KillTile_GetTreeDrops's switch on the ground under the tree, then the mods' trees (TileLoader.DropTreeWood).
		int wood = ground switch {
			TileID.AshGrass => ItemID.AshWood,
			TileID.CorruptGrass or TileID.CorruptJungleGrass => ItemID.Ebonwood,
			TileID.CrimsonGrass or TileID.CrimsonJungleGrass => ItemID.Shadewood,
			TileID.JungleGrass => ItemID.RichMahogany,
			TileID.HallowedGrass or TileID.GolfGrassHallowed => ItemID.Pearlwood,
			TileID.SnowBlock => ItemID.BorealWood,
			TileID.MushroomGrass => 0, // a glowing mushroom half the time, else nothing
			_ => ItemID.Wood,
		};
		Terraria.ModLoader.TileLoader.DropTreeWood(ground, ref wood);
		return wood;
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
		// Ammo goes into the ammo slots first (Player.GetItem: FitsAmmoSlot, then FillAmmo, slots 54-57). Seen in game
		// (measured run M10f, world_eow): the 50 main slots full (the one KeepRoom freed refilled by a pickup at once),
		// "crafting 500 Wooden Arrow" refused as busy over and over for 20+ game hours, the arrows' own stack in an ammo slot.
		if (ContentSamples.ItemsByType.TryGetValue(itemType, out Item sample) && sample.FitsAmmoSlot())
			for (int i = 54; i < 58; i++) {
				Item it = player.inventory[i];
				if (it.IsAir || (it.type == itemType && it.stack < it.maxStack))
					return true;
			}
		return false;
	}

	public bool CanPickUp(int itemType)
	{
		if (!ContentSamples.ItemsByType.TryGetValue(itemType, out Item sample))
			return HasRoomFor(itemType);
		// The very checks Player.GrabItems makes before it pulls an item in: CanAcceptItemIntoInventory (an Encumbering Stone
		// carried lets only hearts and stars through) and ItemSpace (coin slots for coins, ammo slots for ammo). Seen in game
		// (measured run U28, world_eye): an Encumbering Stone looted at t465000 (chest at (2541,587)), and from then on not one
		// item was picked up for the 400000 ticks left: 101 of the run's 107 Collect heartbeats came after it, standing on
		// coins, dirt and arrows that never came.
		return player.CanAcceptItemIntoInventory(sample) && player.ItemSpace(sample).CanTakeItem;
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
		// Something left on the cursor blocks every craft (vanilla crafts onto the cursor): put it in the bag like a click on a
		// slot, or throw it out, as a player would. Seen in game (measured run M10g, world_eow): "crafting 1 Bottle (Glass
		// 1/1)" refused "crafting stayed blocked (cursor item / full inventory)" for 12 game hours with 1-2 slots free.
		if (!Main.mouseItem.IsAir) {
			string held = $"{Main.mouseItem.Name} x{Main.mouseItem.stack}";
			Main.mouseItem = player.GetItem(player.whoAmI, Main.mouseItem, GetItemSettings.InventoryUIToInventorySettings);
			if (!Main.mouseItem.IsAir) {
				player.QuickSpawnItem(player.GetSource_FromThis(), Main.mouseItem, Main.mouseItem.stack);
				Main.mouseItem.TurnToAir();
			}
			Terraria.ModLoader.ModContent.GetInstance<TerraBotMod>()?.Logger.Info($"Craft: {held} was on the cursor; put away before crafting");
		}
		bool room = HasRoomFor(itemType);
		// Vanilla refreshes nearby crafting stations (adjTile) only while the inventory screen is open, i.e. when
		// the crafting menu is visible. Do that same range check here before asking which recipes are available.
		player.AdjTiles();
		Recipe.FindRecipes();
		for (int i = 0; i < Main.numAvailableRecipes; i++) {
			Recipe r = Main.recipe[Main.availableRecipe[i]];
			if (r.createItem.type != itemType)
				continue;
			// No free slot: the craft still fits if it uses up an ingredient's whole stack (that slot comes free).
			if (!room && !r.requiredItem.Any(ing => !ing.IsAir && player.CountItem(ing.type, ing.stack + 1) <= ing.stack))
				return CraftResult.Busy;
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

	public int TileFrameX(int x, int y) => WorldGen.InWorld(x, y, 1) && Main.tile[x, y].HasTile ? Main.tile[x, y].TileFrameX : -1;

	/// <summary>
	/// Debug experiment only (self-test launch flag -terrabotxray): ore, crystals, stations and chests are found whether or
	/// not the map has revealed them, to measure how much time finding things costs. Never on in normal play.
	/// </summary>
	public static bool XRay { get; set; }

	public IReadOnlyList<TilePos> FindTiles(int tileType, TilePos around, int radius)
	{
		var found = new List<TilePos>();
		// Clamped to the world: a map-wide look passes a radius wider than the world.
		int x0 = Math.Max(2, around.X - radius), x1 = Math.Min(Main.maxTilesX - 3, around.X + radius);
		int y0 = Math.Max(2, around.Y - radius), y1 = Math.Min(Main.maxTilesY - 3, around.Y + radius);
		for (int x = x0; x <= x1; x++)
			for (int y = y0; y <= y1; y++)
				// Only what the player has seen: no x-ray. Exploring (and torchlight) reveals the map.
				if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == tileType && (XRay || Main.Map.IsRevealed(x, y)))
					found.Add(new TilePos(x, y));
		found.Sort((a, b) => a.ChebyshevTo(around).CompareTo(b.ChebyshevTo(around)));
		return found;
	}

	// ---------- IGameKnowledge (built once from the loaded recipes and items, mods included) ----------

	/// <summary>
	/// Natural tiles worth mining for their block/ore (the item that places the tile is what it drops). Sand for glass (2 Sand
	/// Block at a Furnace, any sand, Recipe.cs:3225), the glass for the Bottle the vile powder is made at (Recipe.cs:1687).
	/// </summary>
	private static readonly HashSet<int> MineableTiles = new() {
		TileID.Stone, TileID.Dirt, TileID.ClayBlock, TileID.Mud, TileID.SnowBlock, TileID.Granite, TileID.Marble,
		TileID.Copper, TileID.Tin, TileID.Iron, TileID.Lead, TileID.Silver, TileID.Tungsten, TileID.Gold, TileID.Platinum,
		TileID.Sand,
		// The Underworld's ash (Ash Block, 172): the hellbridge's blocks are got from it down there (Milestone 12).
		TileID.Ash,
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

	/// <summary>
	/// The altars' area, as WorldGen's "Altars" pass draws them (WorldGen.cs:11846-11898): x in 281..maxTilesX-284 but not over
	/// 0.45 to 0.55 of the width, y from (2 worldSurface + rockLayer) / 3 to (rockLayer + 2 (maxTilesY - 350)) / 3; a "don't dig
	/// up" world, rows 100 to 0.9 maxTilesY. The evil biome's own passes put a few more in it (WorldGen.cs:10689), wherever it is.
	/// </summary>
	public GenArea? GenerationArea(int tileType)
	{
		if (tileType != TileID.DemonAltar || Main.maxTilesX <= 0)
			return null;
		int w = Main.maxTilesX;
		int minY = Main.remixWorld ? 100 : (int)((Main.worldSurface * 2.0 + Main.rockLayer) / 3);
		int maxY = Main.remixWorld ? (int)(Main.maxTilesY * 0.9) : (int)(Main.rockLayer + (Main.maxTilesY - 350) * 2) / 3;
		return new GenArea(281, w - 284, (int)(w * 0.45), (int)System.Math.Ceiling(w * 0.55), minY, maxY);
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

	/// <summary>A corrupt plant (TileID.CorruptPlants) of this frame column is a vile mushroom and drops one (WorldGen.cs:59639-59641).</summary>
	public const int VileMushroomFrameX = 144;

	/// <summary>
	/// Items a tile drops although the item does not place that tile (KillTile_GetItemDrops special cases), with the frame
	/// column that drops it (-1: any), and whether only the evil biome has the tile.
	/// </summary>
	private static readonly Dictionary<int, (int Tile, int FrameX, bool Evil)> DroppedByTile = new() {
		[ItemID.LifeCrystal] = (TileID.Heart, -1, false),
		// On corrupt grass only (WorldGen.cs:54373: a plant growing there turns mushroom at 1/50 or 1/40); tileNoFail, so
		// one hit of any pickaxe breaks it (Main.cs:8037, Player.GetPickaxeDamage).
		[ItemID.VileMushroom] = (TileID.CorruptPlants, VileMushroomFrameX, true),
	};

	/// <summary>
	/// Items hunted from NPCs, by the NPCs' AI style (ItemDropDatabase: gel from slimes, lenses from demon eyes 1/3), and
	/// whether those NPCs live only in the evil biome.
	/// </summary>
	private static readonly Dictionary<int, (int Ai, bool Evil)> DroppedByNpcAi = new() {
		[ItemID.Lens] = (NPCAIStyleID.DemonEye, false),
		// Eaters of souls (NPC 6, aiStyle 5 flying, NPC.cs:3849): 1 in 3 (ItemDropDatabase.cs:1110). They spawn only while the
		// player is in the Corruption zone.
		[ItemID.RottenChunk] = (NPCAIStyleID.Flying, true),
		// Devourers (NPC 7-9, aiStyle 6 worm): 3-8 at 100% (ItemDropDatabase.cs:1113).
		[ItemID.WormTooth] = (NPCAIStyleID.Worm, true),
	};

	/// <summary>
	/// The woods trees drop (WorldGen.KillTile_GetTreeDrops by the ground under the tree: Wood, Ebonwood on corrupt grass,
	/// Shadewood on crimson grass, Rich Mahogany on jungle grass, Pearlwood on hallowed grass, Boreal Wood on snow, Ash Wood on
	/// ash grass; palm trees Palm Wood). All of them are in the "Wood" recipe group (Recipe.SetupRecipeGroups), so an
	/// any-wood ingredient (Wooden Arrow, Torch) takes whichever the trees near by give.
	/// </summary>
	private static readonly HashSet<int> TreeWoods = new() {
		ItemID.Wood, ItemID.Ebonwood, ItemID.Shadewood, ItemID.RichMahogany, ItemID.Pearlwood, ItemID.BorealWood, ItemID.PalmWood, ItemID.AshWood,
	};

	public ItemSource SourceOf(int itemType)
	{
		if (DroppedByTile.TryGetValue(itemType, out var drop))
			return new ItemSource(SourceKind.Mine, drop.Tile, FrameX: drop.FrameX, EvilBiome: drop.Evil);
		if (DroppedByNpcAi.TryGetValue(itemType, out var hunted))
			return new ItemSource(SourceKind.Hunt, NpcAiStyle: hunted.Ai, EvilBiome: hunted.Evil);
		if (TreeWoods.Contains(itemType))
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
	public int ItemValue(int itemType) => itemType > 0 && itemType < Terraria.ModLoader.ItemLoader.ItemCount ? ContentSamples.ItemsByType[itemType].value : 0;

	public string TileName(int tileType) => TileID.Search.TryGetName(tileType, out string name) ? name : $"tile {tileType}";

	// ---------- building (walls, footprints, housing) and armour ----------

	/// <summary>
	/// Life crystals are frame-important (2×2) but meant to be mined; any pickaxe breaks them. So are the plants that drop
	/// something (<see cref="DroppedByTile"/> with its frame: a vile mushroom), cut tiles a pickaxe breaks in one hit.
	/// </summary>
	public bool CanMineTarget(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 2) || !Main.tile[x, y].HasTile || !WorldGen.CanKillTile(x, y))
			return false;
		Tile t = Main.tile[x, y];
		if (t.TileType == TileID.Heart)
			return true;
		foreach (var drop in DroppedByTile.Values)
			if (drop.Tile == t.TileType && drop.FrameX >= 0 && drop.FrameX == t.TileFrameX && Main.tileCut[t.TileType])
				return true;
		return false;
	}

	/// <summary>Vanilla bosses' "downed" flags (the Eye of Cthulhu first; extend as bosses are added).</summary>
	/// <summary>Off the map counts as seen: there is nothing to explore past the world's edge.</summary>
	public bool IsRevealed(int x, int y) => !WorldGen.InWorld(x, y, 1) || Main.Map.IsRevealed(x, y);

	public float LightAt(int x, int y) => WorldGen.InWorld(x, y, 1) ? Lighting.Brightness(x, y) : 1f;

	/// <summary>Vanilla's own check, as a blast runs it per tile (Projectile.ExplodeTiles -> CanExplodeTile).</summary>
	public bool CanExplode(int x, int y) => WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && explodeProbe.CanExplodeTile(x, y);

	public bool BossDefeated(int npcType) => npcType switch {
		NPCID.EyeofCthulhu => NPC.downedBoss1,
		NPCID.KingSlime => NPC.downedSlimeKing,
		NPCID.EaterofWorldsHead or NPCID.BrainofCthulhu => NPC.downedBoss2,
		NPCID.SkeletronHead => NPC.downedBoss3,
		NPCID.QueenBee => NPC.downedQueenBee,
		// The Wall's death starts Hardmode (NPC.cs:85513, WorldGen.StartHardmode); there is no downed flag of its own.
		NPCID.WallofFlesh => Main.hardMode,
		_ => false,
	};

	/// <summary>An active town NPC of the type anywhere: its head is on the full-screen map (Main.cs:72101-72127), seen or not.</summary>
	public bool TownNpcAlive(int npcType) => NPC.AnyNPCs(npcType);

	/// <summary>
	/// The boss bar's life: for the Eater of Worlds the sum over every active head, body and tail, as its bar counts them
	/// (EaterOfWorldsProgressBar.cs:34-41, at any distance); for another boss the life of its active NPCs.
	/// </summary>
	public int BossBarLife(int npcType)
	{
		bool worm = npcType == NPCID.EaterofWorldsHead;
		int life = 0;
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (n.active && (worm ? n.type == NPCID.EaterofWorldsHead || n.type == NPCID.EaterofWorldsBody || n.type == NPCID.EaterofWorldsTail : n.type == npcType))
				life += n.life;
		}
		return life;
	}

	public bool HasWall(int x, int y) => WorldGen.InWorld(x, y, 1) && Main.tile[x, y].WallType > WallID.None;

	/// <summary>Main.wallDungeon (Main.cs:10586-10594) of the cell's wall, in a cell the map has revealed.</summary>
	public bool IsDungeonWall(int x, int y) =>
		WorldGen.InWorld(x, y, 1) && Main.wallDungeon[Main.tile[x, y].WallType] && (XRay || Main.Map.IsRevealed(x, y)); // paths ask it below the surface: the wall first

	/// <summary>
	/// A right-click on the NPC and a click on its first chat button, as the game runs them (a UI action: PLAN.md, "Actions that
	/// stand in for UI clicks"). The click is read only while the chat panel is drawn (Main.GUIChatDrawInner), which the fast
	/// self-test skips 15 frames in 16, so the handler's body is called with the same guards (docs M11_SPEC.md section 2c):
	/// <list type="number">
	/// <item>the chat would open: singleplayer, the local player alive and without a Wire Kite out, the NPC one that chats
	/// (NPCLoader.CanChat, town NPCs by default) and its hitbox in the 80 × 64 px box round the player's centre
	/// (Main.HoverOverNPCs, Main.cs:56045-56068);</item>
	/// <item>open it as the right-click does (Main.cs:56083-56096: SetTalkNPC, the inventory and chest closed, the recipes
	/// found again, the NPC's chat line);</item>
	/// <item>the button would be there: the Old Man's "Curse" (Lang.inter[50]) only when it is not day (Main.cs:51819-51825),
	/// as mods leave it (NPCLoader.SetChatButtons, Main.cs:52004);</item>
	/// <item>the first button's handler (Main.cs:52056-52060, 52113-52124): NPCLoader.PreChatButtonClicked and
	/// OnChatButtonClicked, then NPC.SpawnSkeletron (NPC.cs:91018), and the chat closed.</item>
	/// </list>
	/// Not with Skeletron up already (SpawnSkeletron would take the Old Man away and spawn nothing) or beaten.
	/// </summary>
	public string PressFirstChatButton(int npcId)
	{
		Player p = player;
		if (p.whoAmI != Main.myPlayer || Main.netMode != NetmodeID.SinglePlayer)
			return "not the local player in singleplayer";
		if (npcId < 0 || npcId >= Main.maxNPCs || !Main.npc[npcId].active)
			return $"no NPC #{npcId}";
		NPC npc = Main.npc[npcId];
		if (p.dead)
			return "dead";
		if (p.ownedProjectileCounts[ProjectileID.WireKite] > 0)
			return "a Wire Kite is out (no chat)";
		if (!(Terraria.ModLoader.NPCLoader.CanChat(npc) ?? npc.townNPC))
			return $"{npc.FullName} does not chat";
		var box = new Rectangle((int)(p.position.X + p.width / 2 - Player.tileRangeX * 16), (int)(p.position.Y + p.height / 2 - Player.tileRangeY * 16),
			Player.tileRangeX * 16 * 2, Player.tileRangeY * 16 * 2);
		if (!box.Intersects(npc.Hitbox))
			return $"{npc.FullName} is out of chat range";
		if (npc.type != NPCID.OldMan)
			return $"only the Old Man's first button is supported, not {npc.FullName}'s";
		if (NPC.downedBoss3)
			return "Skeletron is beaten already";
		if (NPC.AnyNPCs(NPCID.SkeletronHead))
			return "Skeletron is up already (a second Curse would only take the Old Man away)";
		p.SetTalkNPC(npcId);
		Main.playerInventory = false;
		p.chest = -1;
		Recipe.FindRecipes();
		Main.npcChatText = npc.GetChat();
		string button = Main.IsItDay() ? "" : Lang.inter[50].Value, button2 = "";
		Terraria.ModLoader.NPCLoader.SetChatButtons(ref button, ref button2);
		string refused = string.IsNullOrEmpty(button) ? (Main.IsItDay() ? "no Curse button by day" : "no first chat button (a mod took it away)")
			: !Terraria.ModLoader.NPCLoader.PreChatButtonClicked(firstButton: true) ? "a mod refused the click (PreChatButtonClicked)"
			: null;
		if (refused == null) {
			Terraria.ModLoader.NPCLoader.OnChatButtonClicked(firstButton: true);
			NPC.SpawnSkeletron(Main.myPlayer);
		}
		Main.npcChatText = "";
		p.SetTalkNPC(-1);
		return refused;
	}

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

	public int FreeSlots
	{
		get {
			int n = 0;
			for (int i = 0; i < MainInventorySlots; i++)
				if (player.inventory[i].IsAir)
					n++;
			return n;
		}
	}

	public int MaxStack(int itemType) =>
		itemType > 0 && itemType < Terraria.ModLoader.ItemLoader.ItemCount ? ContentSamples.ItemsByType[itemType].maxStack : 9999;

	public bool IsGear(int itemType)
	{
		if (itemType <= 0 || itemType >= Terraria.ModLoader.ItemLoader.ItemCount)
			return false;
		Item it = ContentSamples.ItemsByType[itemType];
		return it.pick > 0 || it.axe > 0 || it.hammer > 0 || it.damage > 0 || it.defense > 0 || it.accessory || it.healLife > 0 || it.ammo > 0
			|| it.headSlot >= 0 || it.bodySlot >= 0 || it.legSlot >= 0 || it.buffType > 0 || it.fishingPole > 0 || it.shoot > 0;
	}

	public IReadOnlyList<(int Type, int Stack)> Stacks()
	{
		var list = new List<(int, int)>();
		for (int i = 0; i < MainInventorySlots; i++)
			if (!player.inventory[i].IsAir)
				list.Add((player.inventory[i].type, player.inventory[i].stack));
		return list;
	}

	/// <summary>Ctrl-click quick trash (vanilla ItemSlot): the slot's item into the trash slot, the old trash gone.</summary>
	public bool TryTrash(int itemType)
	{
		if (player.whoAmI != Main.myPlayer)
			return false;
		int best = -1;
		for (int i = 0; i < MainInventorySlots; i++) {
			Item it = player.inventory[i];
			if (!it.IsAir && it.type == itemType && !it.favorited && i != player.selectedItem && (best < 0 || it.stack < player.inventory[best].stack))
				best = i;
		}
		if (best < 0)
			return false;
		player.trashItem = player.inventory[best].Clone();
		player.inventory[best].TurnToAir();
		Recipe.FindRecipes();
		return true;
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
