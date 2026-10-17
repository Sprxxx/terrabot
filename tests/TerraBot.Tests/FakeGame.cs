using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// A small fake Terraria for brain/skill tests, on top of <see cref="SimWorld"/>. Uses the vanilla item, tile and
/// recipe numbers the bot needs up to iron armour (recipes checked against the decompiled Recipe.SetupRecipes,
/// tML 2026.08), in an "iron world" (lead ore does not exist):
/// <list type="bullet">
/// <item>trees fall after 30 ticks of axe swings at their base and drop one wood per trunk tile around it;</item>
/// <item>mined stone/ore/dirt drops its item where the cell was; drops within 42 px are picked up; placing a block
/// (SimWorld) spends a carried stone or dirt block;</item>
/// <item>crafting checks ingredients (recipe groups included) and stations within 4 tiles of the body;</item>
/// <item>furniture has vanilla footprints (TileObjectData size and origin): work bench 2×1, furnace 3×2,
/// anvil 2×1, table 3×2, chair 1×2, door 1×3, torch 1×1. Floor furniture needs solid ground under it, a door
/// solid tiles above and below, a torch a wall or a solid side; nothing goes over the player except torches;</item>
/// <item>walls go into cells next to a tile or wall; the housing check is a simplified WorldGen.StartRoomCheck +
/// RoomNeeds (8-neighbour flood fill, ≥ 60 cells frame included, walls or solids within 2 cells, chair, table,
/// door and light source);</item>
/// <item>slimes (AI style 1) sit still, take 10 damage per in-reach swing and drop 2 gel.</item>
/// </list>
/// </summary>
public sealed class FakeGame : IWorldInfo, IInventory, ICrafter, IGameKnowledge
{
	// Vanilla IDs (ItemID / TileID).
	public const int Wood = 9, WorkBench = 36, WoodenSword = 24, Gel = 23, Torch = 8, StoneBlock = 3, DirtBlock = 2;
	public const int Furnace = 33, IronOre = 11, IronBar = 22, IronAnvil = 35, IronPickaxe = 1;
	public const int LeadOre = 700, LeadBar = 704, LeadAnvil = 716, LeadPickaxe = 3497, WoodPlatform = 94, IronFence = 2333;
	public const int WoodWall = 93, Door = 25, Chair = 34, Table = 32;
	public const int IronBroadsword = 4, IronHelmet = 90, IronChainmail = 81, IronGreaves = 77;
	public const int SilverOre = 14, SilverBar = 21, SilverHelmet = 92, SilverChainmail = 83, SilverGreaves = 79, TileSilver = 9;
	public const int TileDirt = 0, TileStone = 1, TileIron = 6, TileLead = 167, TileAnvil = 16, TileFurnace = 17, TileBench = 18;
	public const int TileTorch = 4, TileDoor = 10, TileTable = 14, TileChair = 15;
	public const int LifeCrystal = 29, TileHeart = 12;
	public const int IronBow = 99, WoodenArrow = 40, Lens = 38, SuspiciousEye = 43, TileDemonAltar = 26, EyeNpc = 4, ServantNpc = 5, DemonEyeAi = 2;
	public const int SlimeAi = 1;
	public const int RecallPotion = 2350;
	public const int Campfire = 966, HeartLantern = 1859, Chain = 85, TileCampfire = 215, TileLantern = 42;
	// Milestone 10 (docs M10_SPEC.md; ItemID, TileID, NPCID and the recipes checked against the decompiled tML 2026.08).
	public const int WormFood = 70, VilePowder = 67, VileMushroom = 60, RottenChunk = 68, WormTooth = 69, UnholyArrow = 47;
	public const int DemoniteOre = 56, DemoniteBar = 57, DemonBow = 44, Bottle = 31, Glass = 170, SandBlock = 169;
	public const int TileBottles = 13, TileDemonite = 22, TileCorruptGrass = 23, TileCorruptPlants = 24, TileEbonstone = 25, TileCorruptThorns = 32;
	public const int EowHead = 13, EowBody = 14, EowTail = 15, FlyingAi = 5, WormAi = 6, VileMushroomFrame = 144;
	/// <summary>The Corruption's trees' wood (in the "Wood" recipe group), and the Encumbering Stone (carried, no pickups).</summary>
	public const int Ebonwood = 619, EncumberingStone = 4346;
	// Milestone 11 (docs M11_SPEC.md; NPCID, TileID and ItemID checked against the decompiled tML 2026.08).
	public const int SkeletronHeadNpc = 35, SkeletronHandNpc = 36, OldManNpc = 37, GuardianNpc = 68;
	public const int TileBlueBrick = 41, TileJungleGrass = 60, TileSnow = 147, HealingPotion = 188;
	// Milestone 12 (docs M12_SPEC.md; NPCID, ItemID and TileID checked against the decompiled tML 2026.08).
	public const int GuideNpc = 22, GuideVoodooDoll = 267, WofNpc = 113, WofEyeNpc = 114, HungryNpc = 115, HungryIINpc = 116;
	public const int LeechHeadNpc = 117, LeechBodyNpc = 118, LeechTailNpc = 119, VoodooDemonNpc = 66;
	public const int TileHellforge = 77, TileHellstone = 58, TileAsh = 57, Hellstone = 174, Obsidian = 173, AshBlock = 172, ShadowScale = 86;

	public static readonly GameIds Ids = new() {
		Wood = Wood, WorkBenchItem = WorkBench, WorkBenchTile = TileBench, WoodenSword = WoodenSword, WoodenHammer = 196, Acorn = 27,
		Gel = Gel, Torch = Torch, FurnaceTile = TileFurnace, AnvilTile = TileAnvil, IronTierPickaxe = IronPickaxe,
		IronTierBroadsword = IronBroadsword, IronTierHelmet = IronHelmet, IronTierChainmail = IronChainmail, IronTierGreaves = IronGreaves,
		SilverTierHelmet = SilverHelmet, SilverTierChainmail = SilverChainmail, SilverTierGreaves = SilverGreaves,
		WoodWall = WoodWall, Door = Door, Chair = Chair, Table = Table, BuildingBlocks = new[] { StoneBlock, DirtBlock },
		LifeCrystal = LifeCrystal,
		IronTierBow = IronBow, WoodenArrow = WoodenArrow, Lens = Lens, SuspiciousEye = SuspiciousEye, DemonAltarTile = TileDemonAltar,
		EyeBoss = EyeNpc, EyeServant = ServantNpc,
		Campfire = Campfire, HeartLantern = HeartLantern, RecallPotion = RecallPotion, FloatingAccessories = new[] { InnerTube }, PickupBlockers = new[] { EncumberingStone },
		DesertTiles = new[] { TileSand }, // only cells a test marks with it (TileTypes) count
		EvilTiles = new[] { TileCorruptGrass, TileEbonstone }, // likewise
		CorruptionWeights = new Dictionary<int, int> { [TileCorruptGrass] = 1, [TileCorruptPlants] = 1, [TileEbonstone] = 1 },
		EvilBoss = EowHead, EvilBossBody = EowBody, EvilBossTail = EowTail, WormFood = WormFood, VilePowder = VilePowder,
		VileMushroom = VileMushroom, CorruptGrass = TileCorruptGrass, CorruptThorns = TileCorruptThorns, RottenChunk = RottenChunk, WormTooth = WormTooth, UnholyArrow = UnholyArrow,
		DemoniteOre = DemoniteOre, DemoniteBar = DemoniteBar, DemonBow = DemonBow, Bottle = Bottle, BottleTile = TileBottles,
		SkeletronHead = SkeletronHeadNpc, SkeletronHand = SkeletronHandNpc, OldMan = OldManNpc, DungeonGuardian = GuardianNpc,
		DungeonBrickTiles = new[] { TileBlueBrick }, SnowTiles = new[] { TileSnow }, JungleTiles = new[] { TileJungleGrass },
		HealingPotion = HealingPotion, LesserHealingPotion = LesserHealingPotion,
		Guide = GuideNpc, GuideVoodooDoll = GuideVoodooDoll, WallOfFlesh = WofNpc, WallOfFleshEye = WofEyeNpc, TheHungry = HungryNpc, TheHungryII = HungryIINpc,
		LeechHead = LeechHeadNpc, LeechBody = LeechBodyNpc, LeechTail = LeechTailNpc, VoodooDemon = VoodooDemonNpc,
		HellforgeTile = TileHellforge, HellstoneTile = TileHellstone, AshTile = TileAsh, Hellstone = Hellstone, Obsidian = Obsidian, AshBlock = AshBlock, ShadowScale = ShadowScale, Heart = 58,
		TileExplosives = new[] { new ExplosiveInfo(Bomb, "Bomb", 4, 64f, 100, 180, 5f, 25, 22, 0.97f, 0.4f),
			new ExplosiveInfo(Dynamite, "Dynamite", 7, 125f, 250, 300, 4f, 40, 10, 0.97f * 0.99f, 0.4f * 0.8f) },
	};

	// The Bomb and Dynamite (ItemID 166, 167) with the Game layer's numbers (TerrariaGameInfo.TileExplosiveInfos).
	public const int Bomb = 166, Dynamite = 167;
	public static ExplosiveInfo BombInfo => Ids.TileExplosives[0];
	public static ExplosiveInfo DynamiteInfo => Ids.TileExplosives[1];
	/// <summary>Solid cells a blast does not break (a dungeon brick, an altar); every other solid cell it does.</summary>
	public readonly HashSet<TilePos> NoExplode = new();
	/// <summary>Non-solid tile types (in <see cref="Furniture"/>) one pickaxe hit removes.</summary>
	public readonly HashSet<int> PickaxeRemoves = new();
	/// <summary>Explosives thrown and not yet gone off: where they came to rest (pixels) and the fuse left.</summary>
	public readonly List<(ExplosiveInfo Info, float X, float Y, int Fuse)> Lit = new();
	/// <summary>Blasts so far, and the ones whose box touched the player.</summary>
	public int Blasts, BlastHits;

	/// <summary>A desert tile type for biome tests: set on cells through <see cref="TileTypes"/>.</summary>
	public const int TileSand = 53;

	public readonly ArrayTileGrid Grid;
	public readonly TestBlockActions Blocks;
	public readonly SimWorld Sim;
	public readonly Dictionary<TilePos, int> Trees = new(); // base -> height
	/// <summary>The wood a tree drops (base -> item); Wood unless set.</summary>
	public readonly Dictionary<TilePos, int> TreeWoods = new();
	public readonly Dictionary<TilePos, int> TileTypes = new(); // solid cells that are not dirt
	public readonly Dictionary<TilePos, int> Furniture = new(); // cell -> tile type of placed furniture
	/// <summary>Background walls (the blocks' own set: a block in open air can be fixed to one, as in vanilla).</summary>
	public HashSet<TilePos> Walls => Blocks.Walls;
	public readonly HashSet<int> Equipped = new();
	public readonly Dictionary<int, int> Items = new();
	public readonly List<DropInfo> Drops = new();
	public readonly Dictionary<int, TilePos> Stations = new();
	/// <summary>How near (Chebyshev tiles) a station must be for a craft, like vanilla's AdjTiles box.</summary>
	public int StationRange = 4;
	/// <summary>How far (columns) trees are seen, like the game's 50-column scan; unlimited by default.</summary>
	public int TreeSight = int.MaxValue;
	public readonly List<(int id, float x, float y, int life)> Slimes = new();
	public bool IsNight;
	public int Crafts;
	private readonly Dictionary<TilePos, int> chopTicks = new();
	private int nextDropId = 1000;
	private int swingTimer;
	private int facing = 1;
	private int placedSeen;
	private int useTicks;
	public int LifeMax = 100;
	/// <summary>Current life; -1 = full.</summary>
	public int Life = -1;

	private readonly Dictionary<int, List<RecipeInfo>> recipes = new();

	/// <summary>
	/// Rows like the unit-test grids plus 's' stone and 'i' iron ore (solid, typed) and 'T' tree base (a tree of
	/// height 8 standing on the cell below). 'S' is the start node.
	/// </summary>
	public FakeGame(params string[] rows)
	{
		var plain = new string[rows.Length];
		for (int y = 0; y < rows.Length; y++) {
			var chars = rows[y].ToCharArray();
			for (int x = 0; x < chars.Length; x++) {
				char c = chars[x];
				if (c is 's' or 'i') {
					TileTypes[new TilePos(x, y)] = c == 's' ? TileStone : TileIron;
					chars[x] = '#';
				}
				else if (c == 'T') {
					Trees[new TilePos(x, y)] = 8;
					chars[x] = '.';
				}
			}
			plain[y] = new string(chars);
		}
		(Grid, Blocks) = TestBlockActions.Parse(plain);
		Blocks.BlockCount = 0;
		Sim = new SimWorld(Grid, PhysicsParams.Default, Grid.Markers['S'], Blocks);
		Sim.Mined += OnMined;

		Add(new RecipeInfo { Result = WorkBench, Ingredients = new[] { new Ingredient(10, Wood) } });
		Add(new RecipeInfo { Result = WoodenSword, Ingredients = new[] { new Ingredient(7, Wood) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = Torch, ResultStack = 3, Ingredients = new[] { new Ingredient(1, Gel), new Ingredient(1, Wood) } });
		Add(new RecipeInfo { Result = Furnace, Ingredients = new[] { new Ingredient(20, StoneBlock), new Ingredient(4, Wood), new Ingredient(3, Torch) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = IronBar, Ingredients = new[] { new Ingredient(3, IronOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = LeadBar, Ingredients = new[] { new Ingredient(3, LeadOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = IronAnvil, Ingredients = new[] { new Ingredient(5, IronBar) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = LeadAnvil, Ingredients = new[] { new Ingredient(5, LeadBar) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = IronPickaxe, Ingredients = new[] { new Ingredient(10, IronBar), new Ingredient(3, Wood) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = LeadPickaxe, Ingredients = new[] { new Ingredient(10, LeadBar), new Ingredient(3, Wood) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = IronBow, Ingredients = new[] { new Ingredient(7, IronBar) }, Stations = new[] { TileAnvil } });
		// Any wood (vanilla: anyWood, the "Wood" recipe group).
		Add(new RecipeInfo { Result = WoodenArrow, ResultStack = 25, Ingredients = new[] { new Ingredient(1, Wood, Ebonwood), new Ingredient(1, StoneBlock) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = SuspiciousEye, Ingredients = new[] { new Ingredient(6, Lens) }, Stations = new[] { TileDemonAltar } });
		Add(new RecipeInfo { Result = IronBroadsword, Ingredients = new[] { new Ingredient(8, IronBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = IronHelmet, Ingredients = new[] { new Ingredient(15, IronBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = IronChainmail, Ingredients = new[] { new Ingredient(25, IronBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = IronGreaves, Ingredients = new[] { new Ingredient(20, IronBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = SilverBar, Ingredients = new[] { new Ingredient(4, SilverOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = SilverHelmet, Ingredients = new[] { new Ingredient(15, SilverBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = SilverChainmail, Ingredients = new[] { new Ingredient(25, SilverBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = SilverGreaves, Ingredients = new[] { new Ingredient(20, SilverBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = WoodWall, ResultStack = 4, Ingredients = new[] { new Ingredient(1, Wood) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = Door, Ingredients = new[] { new Ingredient(6, Wood) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = Chair, Ingredients = new[] { new Ingredient(4, Wood) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = Table, Ingredients = new[] { new Ingredient(8, Wood) }, Stations = new[] { TileBench } });
		// Vanilla (Recipe.cs): campfire 10 wood + 5 torches by hand; chain 1 iron bar -> 15 at an anvil; heart lantern by hand.
		Add(new RecipeInfo { Result = Campfire, Ingredients = new[] { new Ingredient(10, Wood), new Ingredient(5, Torch) } });
		Add(new RecipeInfo { Result = Chain, ResultStack = 15, Ingredients = new[] { new Ingredient(1, IronBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = HeartLantern, Ingredients = new[] { new Ingredient(1, LifeCrystal), new Ingredient(4, Chain) } });
		// A cycle like vanilla's: platforms from wood and wood back from platforms.
		Add(new RecipeInfo { Result = WoodPlatform, ResultStack = 2, Ingredients = new[] { new Ingredient(1, Wood) } });
		Add(new RecipeInfo { Result = Wood, Ingredients = new[] { new Ingredient(2, WoodPlatform) } });
		// And one between crafted items (tML's reverse wall recipes), listed before the ore recipe on purpose: iron fences
		// come from iron bars, so "bars from fences" is never a way to get bars.
		Add(new RecipeInfo { Result = IronFence, ResultStack = 4, Ingredients = new[] { new Ingredient(1, IronBar) }, Stations = new[] { TileAnvil } });
		recipes[IronBar].Insert(0, new RecipeInfo { Result = IronBar, Ingredients = new[] { new Ingredient(4, IronFence) } });
		// Milestone 10, vanilla Recipe.cs: demonite bar 3 ore at a furnace (10250), Demon Bow 8 bars at an anvil (10260), unholy
		// arrows 5 from 5 wooden arrows and a worm tooth at an anvil (2276), Worm Food 30 vile powder and 15 rotten chunks at a
		// demon altar (14407), vile powder 5 from a vile mushroom at a placed bottle (2507), 2 bottles from a glass at a furnace
		// (1687), glass from 2 sand at a furnace (3225).
		Add(new RecipeInfo { Result = DemoniteBar, Ingredients = new[] { new Ingredient(3, DemoniteOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = DemonBow, Ingredients = new[] { new Ingredient(8, DemoniteBar) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = UnholyArrow, ResultStack = 5, Ingredients = new[] { new Ingredient(5, WoodenArrow), new Ingredient(1, WormTooth) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = WormFood, Ingredients = new[] { new Ingredient(30, VilePowder), new Ingredient(15, RottenChunk) }, Stations = new[] { TileDemonAltar } });
		Add(new RecipeInfo { Result = VilePowder, ResultStack = 5, Ingredients = new[] { new Ingredient(1, VileMushroom) }, Stations = new[] { TileBottles } });
		Add(new RecipeInfo { Result = Bottle, ResultStack = 2, Ingredients = new[] { new Ingredient(1, Glass) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = Glass, Ingredients = new[] { new Ingredient(2, SandBlock) }, Stations = new[] { TileFurnace } });
	}

	public void Add(RecipeInfo r)
	{
		if (!recipes.TryGetValue(r.Result, out List<RecipeInfo> list))
			recipes[r.Result] = list = new List<RecipeInfo>();
		list.Add(r);
	}

	public BotContext CreateContext(ITestOutputHelper output, string plan = null)
	{
		var ctx = new BotContext {
			Grid = Grid, Log = new TestLog(output), World = this, Inventory = this, Crafter = this, Knowledge = this,
			Ids = Ids, AxePower = 35, Blocks = Blocks, Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "test sword" },
		};
		Refresh(ctx);
		return ctx;
	}

	// ---------- IGameKnowledge ----------

	public IReadOnlyList<RecipeInfo> RecipesFor(int itemType) => recipes.TryGetValue(itemType, out List<RecipeInfo> l) ? l : Array.Empty<RecipeInfo>();

	public ItemSource SourceOf(int itemType) => itemType switch {
		Wood or Ebonwood => new ItemSource(SourceKind.Tree),
		Gel => new ItemSource(SourceKind.Hunt, NpcAiStyle: SlimeAi),
		StoneBlock => new ItemSource(SourceKind.Mine, TileStone),
		DirtBlock => new ItemSource(SourceKind.Mine, TileDirt),
		IronOre => new ItemSource(SourceKind.Mine, TileIron),
		SilverOre => new ItemSource(SourceKind.Mine, TileSilver),
		LifeCrystal => new ItemSource(SourceKind.Mine, TileHeart),
		Lens => new ItemSource(SourceKind.Hunt, NpcAiStyle: DemonEyeAi),
		RottenChunk => new ItemSource(SourceKind.Hunt, NpcAiStyle: FlyingAi, EvilBiome: true),
		WormTooth => new ItemSource(SourceKind.Hunt, NpcAiStyle: WormAi, EvilBiome: true),
		VileMushroom => new ItemSource(SourceKind.Mine, TileCorruptPlants, FrameX: VileMushroomFrame, EvilBiome: true),
		DemoniteOre => new ItemSource(SourceKind.Mine, TileDemonite),
		SandBlock => new ItemSource(SourceKind.Mine, TileSand),
		_ => ItemSource.None, // lead ore: not in this world
	};

	public IReadOnlyList<int> ItemsPlacing(int tileType) => tileType switch {
		TileBench => new[] { WorkBench },
		TileFurnace => new[] { Furnace },
		TileAnvil => new[] { IronAnvil, LeadAnvil },
		TileTable => new[] { Table },
		TileChair => new[] { Chair },
		TileDoor => new[] { Door },
		TileTorch => new[] { Torch },
		TileCampfire => new[] { Campfire },
		TileLantern => new[] { HeartLantern },
		TileBottles => new[] { Bottle },
		_ => Array.Empty<int>(),
	};

	public int TilePlacedBy(int itemType) => itemType switch {
		WorkBench => TileBench, Furnace => TileFurnace, IronAnvil or LeadAnvil => TileAnvil,
		Table => TileTable, Chair => TileChair, Door => TileDoor, Torch => TileTorch,
		Campfire => TileCampfire, HeartLantern => TileLantern, Bottle => TileBottles, _ => -1,
	};

	public string ItemName(int itemType) => $"item{itemType}";
	/// <summary>Item values (copper) for inventory tests; unlisted items are worth 1.</summary>
	public readonly Dictionary<int, int> Values = new();
	public int ItemValue(int itemType) => Values.TryGetValue(itemType, out int v) ? v : 1;
	/// <summary>Main-inventory slots (stacks of 999); unlimited unless a test sets it.</summary>
	public int SlotLimit = int.MaxValue;
	public const int MaxStack = 999;
	/// <summary>Items that stack less than <see cref="MaxStack"/> (accessories, tools: 1).</summary>
	public readonly Dictionary<int, int> StackSize = new();
	private int StackOf(int type) => StackSize.TryGetValue(type, out int s) ? s : MaxStack;
	public int FreeSlots => SlotLimit == int.MaxValue ? 50 : SlotLimit - Stacks().Count;
	public IReadOnlyList<(int Type, int Stack)> Stacks()
	{
		var list = new List<(int, int)>();
		foreach (var (type, n) in Items)
			for (int left = n; left > 0; left -= StackOf(type))
				list.Add((type, Math.Min(left, StackOf(type))));
		return list;
	}
	public bool TryTrash(int itemType)
	{
		int n = Count(itemType);
		if (n <= 0)
			return false;
		int smallest = n % StackOf(itemType) == 0 ? StackOf(itemType) : n % StackOf(itemType);
		Items[itemType] = n - smallest;
		Trashed.Add(itemType);
		return true;
	}
	public readonly List<int> Trashed = new();
	/// <summary>Gear for inventory tests (tools, weapons, potions...).</summary>
	public readonly HashSet<int> Gear = new() { WoodenSword, IronPickaxe, IronBroadsword, IronBow };
	public bool IsGear(int itemType) => Gear.Contains(itemType);
	public string TileName(int tileType) => $"tile{tileType}";

	// ---------- IWorldInfo ----------

	public bool IsTreeTrunk(int x, int y) => Trees.Any(t => t.Key.X == x && y <= t.Key.Y && y > t.Key.Y - t.Value);
	public int TreeWood(int x, int y) => Trees.ContainsKey(new TilePos(x, y)) ? TreeWoods.GetValueOrDefault(new TilePos(x, y), Wood) : 0;

	public (int Width, int Height, int OriginX, int OriginY) Footprint(int itemType) => TilePlacedBy(itemType) switch {
		TileBench or TileAnvil => (2, 1, 0, 0),
		TileFurnace or TileTable or TileCampfire => (3, 2, 1, 1),
		TileLantern => (1, 2, 0, 0),
		TileChair => (1, 2, 0, 1),
		TileDoor => (1, 3, 0, 0),
		_ => (1, 1, 0, 0),
	};

	private bool Free(int x, int y) => Grid.Get(x, y) == CellKind.Empty && !Furniture.ContainsKey(new TilePos(x, y)) && !IsTreeTrunk(x, y);

	/// <summary>Items the game refuses to place anywhere (a test's stand-in for a spot the game will not take).</summary>
	public readonly HashSet<int> RefusedItems = new();

	public bool CanPlaceItem(int itemType, int x, int y)
	{
		int tile = TilePlacedBy(itemType);
		if (tile < 0 || RefusedItems.Contains(itemType))
			return false;
		var (w, h, ox, oy) = Footprint(itemType);
		int x0 = x - ox, y0 = y - oy;
		TilePos n = Sim.Node;
		for (int cx = x0; cx < x0 + w; cx++)
			for (int cy = y0; cy < y0 + h; cy++) {
				if (!Free(cx, cy))
					return false;
				bool inBody = cx >= n.X && cx <= n.X + 1 && cy >= n.Y - 2 && cy <= n.Y;
				if (inBody && tile is not (TileTorch or TileLantern))
					return false;
			}
		if (tile == TileTorch)
			return Walls.Contains(new TilePos(x, y)) || Grid.Get(x - 1, y).IsSolid() || Grid.Get(x + 1, y).IsSolid() || Grid.Get(x, y + 1).IsSolid();
		if (tile == TileDoor)
			return Grid.Get(x0, y0 - 1).IsSolid() && Grid.Get(x0, y0 + h).IsSolid();
		if (tile == TileLantern)
			return Grid.Get(x0, y0 - 1).IsSolid(); // hangs from a ceiling
		for (int cx = x0; cx < x0 + w; cx++)
			if (!Grid.Get(cx, y0 + h).IsSolid())
				return false;
		return true;
	}

	public int TileTypeAt(int x, int y)
	{
		if (!Grid.InBounds(x, y))
			return -1; // as the game: no tile outside the world
		if (Furniture.TryGetValue(new TilePos(x, y), out int f))
			return f;
		if (!Grid.Get(x, y).IsSolid())
			return -1;
		return TileTypes.TryGetValue(new TilePos(x, y), out int t) ? t : TileDirt;
	}

	/// <summary>Frame columns of cells (plants' styles); unlisted cells with a tile are frame 0.</summary>
	public readonly Dictionary<TilePos, int> FrameXs = new();
	public int TileFrameX(int x, int y) => TileTypeAt(x, y) < 0 ? -1 : FrameXs.TryGetValue(new TilePos(x, y), out int f) ? f : 0;

	public IReadOnlyList<TilePos> FindTiles(int tileType, TilePos around, int radius)
	{
		var found = new List<TilePos>();
		for (int x = System.Math.Max(0, around.X - radius); x <= System.Math.Min(Grid.Width - 1, around.X + radius); x++)
			for (int y = System.Math.Max(0, around.Y - radius); y <= System.Math.Min(Grid.Height - 1, around.Y + radius); y++)
				if (Grid.InBounds(x, y) && TileTypeAt(x, y) == tileType && !Blocks.Unbreakable.Contains(new TilePos(x, y)) && IsRevealed(x, y))
					found.Add(new TilePos(x, y));
		return found.OrderBy(c => c.ChebyshevTo(around)).ToList();
	}

	/// <summary>Life crystals (2×2 heart objects) are the only frame-important targets worth mining.</summary>
	public bool CanMineTarget(int x, int y) => TileTypeAt(x, y) == TileHeart || TileTypeAt(x, y) == TileCorruptPlants && TileFrameX(x, y) == VileMushroomFrame;

	/// <summary>A corrupt plant (a cut tile, open to walk through) at the cell, a vile mushroom if <paramref name="mushroom"/>.</summary>
	public void AddPlant(int x, int y, bool mushroom)
	{
		Grid.Set(x, y, CellKind.Empty);
		Furniture[new TilePos(x, y)] = TileCorruptPlants;
		FrameXs[new TilePos(x, y)] = mushroom ? VileMushroomFrame : 0;
	}

	/// <summary>Corrupt thorns at the cell (hurt on touch, not passable: CellKind.Hazard), cut by one hit of a pickaxe.</summary>
	public void AddThorn(int x, int y)
	{
		Grid.Set(x, y, CellKind.Hazard);
		Furniture[new TilePos(x, y)] = TileCorruptThorns;
	}
	/// <summary>Town NPC types alive somewhere in the world besides those in <see cref="TownNpcs"/> (the Guide at home, off screen).</summary>
	public readonly HashSet<int> AliveTownNpcs = new();
	public bool TownNpcAlive(int npcType) => AliveTownNpcs.Contains(npcType) || TownNpcs.Any(n => n.Type == npcType);
	public readonly System.Collections.Generic.HashSet<int> BossesDefeated = new();
	public bool BossDefeated(int npcType) => BossesDefeated.Contains(npcType);
	/// <summary>The life each boss's health bar shows (none: 0).</summary>
	public readonly Dictionary<int, int> BossBars = new();
	public int BossBarLife(int npcType) => BossBars.GetValueOrDefault(npcType);
	/// <summary>Light everywhere; tests set it lower for the torch habit.</summary>
	public float Light = 1f;
	public float LightAt(int x, int y) => Light;
	public bool CanExplode(int x, int y) => Grid.InBounds(x, y) && Grid.Get(x, y).IsSolid() && !NoExplode.Contains(new TilePos(x, y));
	/// <summary>Cells not seen yet; everything else is revealed.</summary>
	public readonly HashSet<TilePos> Unrevealed = new();
	public bool IsRevealed(int x, int y) => !Unrevealed.Contains(new TilePos(x, y)) && !NeverRevealed.Contains(new TilePos(x, y)); // off the grid counts as seen, like the game
	/// <summary>Cells no looking reveals (unlit, or deep in rock).</summary>
	public readonly HashSet<TilePos> NeverRevealed = new();
	/// <summary>Cells within this many tiles of the bot get revealed as it moves (the map filling in).</summary>
	public int RevealRadius = 10;

	/// <summary>Hide everything from column <paramref name="fromX"/> on (the far side of the world not seen yet).</summary>
	public void HideFrom(int fromX)
	{
		for (int x = fromX; x < Grid.Width; x++)
			for (int y = 0; y < Grid.Height; y++)
				Unrevealed.Add(new TilePos(x, y));
	}

	/// <summary>Puts a 2×2 life crystal with its top-left cell at (x, y) (cells must be open; it sits on the floor below).</summary>
	/// <summary>A 3×2 demon altar (nothing places one; it is found) with its top-left at (x, y).</summary>
	public void AddAltar(int x, int y)
	{
		for (int cx = x; cx <= x + 2; cx++)
			for (int cy = y; cy <= y + 1; cy++) {
				Grid.Set(cx, cy, cy == y ? CellKind.Platform : CellKind.Empty);
				Furniture[new TilePos(cx, cy)] = TileDemonAltar;
			}
		Stations[TileDemonAltar] = new TilePos(x, y);
	}

	public void AddHeart(int x, int y)
	{
		for (int cx = x; cx <= x + 1; cx++)
			for (int cy = y; cy <= y + 1; cy++) {
				Grid.Set(cx, cy, CellKind.Empty);
				Furniture[new TilePos(cx, cy)] = TileHeart;
			}
	}

	public bool HasWall(int x, int y) => Walls.Contains(new TilePos(x, y));

	/// <summary>Cells with a dungeon wall behind them (Main.wallDungeon); seen ones only count, as for the bot's other looks.</summary>
	public readonly HashSet<TilePos> DungeonWalls = new();
	public bool IsDungeonWall(int x, int y) => DungeonWalls.Contains(new TilePos(x, y)) && IsRevealed(x, y);

	/// <summary>Town NPCs in the world (the Old Man), as perceived: not moved by the fake.</summary>
	public readonly List<EntityInfo> TownNpcs = new();
	/// <summary>Chat buttons pressed (the Old Man's Curse that summoned Skeletron).</summary>
	public int ChatPresses;

	/// <summary>
	/// The Old Man's first button as the game has it (Main.cs:51819-51825, 52113-52124, NPC.SpawnSkeletron): only at night, and
	/// the head spawns at his centre (NPC 35, 80 × 102, AI style 11, hovering); he is gone after it. Others: not supported.
	/// </summary>
	public string PressFirstChatButton(int npcId)
	{
		int i = TownNpcs.FindIndex(n => n.Id == npcId);
		if (i < 0)
			return $"no NPC #{npcId}";
		EntityInfo om = TownNpcs[i];
		if (om.Type != OldManNpc)
			return "only the Old Man's button";
		if (!IsNight)
			return "no Curse button by day";
		if (ExtraHostiles.Any(h => h.Type == SkeletronHeadNpc))
			return "Skeletron is up already";
		TownNpcs.RemoveAt(i);
		ChatPresses++;
		ExtraHostiles.Add(new EntityInfo(900, SkeletronHeadNpc, "Skeletron", om.CenterX - 40, om.CenterY - 51, 80, 102, 0, 0, 4400, 4400, 32,
			Bot.Skills.SkeletronPattern.HeadAiStyle));
		return null;
	}

	public bool CanPlaceWall(int wallItem, int x, int y)
	{
		if (wallItem != WoodWall || Walls.Contains(new TilePos(x, y)) || !Grid.InBounds(x, y))
			return false;
		bool Anchor(int i, int j) => Grid.Get(i, j) != CellKind.Empty || Furniture.ContainsKey(new TilePos(i, j)) || Walls.Contains(new TilePos(i, j));
		return Anchor(x + 1, y) || Anchor(x - 1, y) || Anchor(x, y + 1) || Anchor(x, y - 1);
	}

	/// <summary>Simplified WorldGen.StartRoomCheck + RoomNeeds.</summary>
	public string CheckHousing(int x, int y)
	{
		bool Frame(int i, int j) => Grid.Get(i, j).IsSolid() || Furniture.TryGetValue(new TilePos(i, j), out int d) && d == TileDoor;
		if (Grid.Get(x, y).IsSolid())
			return "starts in a solid tile";
		var seen = new HashSet<TilePos>();
		var stack = new Stack<TilePos>();
		var types = new HashSet<int>();
		stack.Push(new TilePos(x, y));
		while (stack.Count > 0) {
			TilePos c = stack.Pop();
			if (!seen.Add(c))
				continue;
			if (seen.Count > 750)
				return "room too big";
			if (!Grid.InBounds(c.X, c.Y))
				return "open to the edge";
			if (Furniture.TryGetValue(c, out int f))
				types.Add(f);
			if (Frame(c.X, c.Y))
				continue; // frame (closed doors included)
			bool row = false, col = false;
			for (int k = -2; k <= 2; k++) {
				row |= Walls.Contains(new TilePos(c.X + k, c.Y)) || Frame(c.X + k, c.Y);
				col |= Walls.Contains(new TilePos(c.X, c.Y + k)) || Frame(c.X, c.Y + k);
			}
			if (!row || !col)
				return $"hole in the wall near {c}";
			for (int dx = -1; dx <= 1; dx++)
				for (int dy = -1; dy <= 1; dy++)
					if (dx != 0 || dy != 0)
						stack.Push(new TilePos(c.X + dx, c.Y + dy));
		}
		if (seen.Count < 60)
			return $"room too small ({seen.Count})";
		var missing = new List<string>();
		if (!types.Contains(TileChair)) missing.Add("chair");
		if (!types.Contains(TileTable)) missing.Add("table");
		if (!types.Contains(TileDoor)) missing.Add("door");
		if (!types.Contains(TileTorch)) missing.Add("light source");
		return missing.Count == 0 ? null : "missing " + string.Join(", ", missing);
	}

	// ---------- IInventory / ICrafter ----------

	public int Count(int itemType) => Items.TryGetValue(itemType, out int n) ? n : 0;
	public bool HasRoomFor(int itemType) => FreeSlots > 0 || Stacks().Any(s => s.Type == itemType && s.Stack < StackOf(itemType));
	/// <summary>Like Player.CanAcceptItemIntoInventory and ItemSpace: room for it, and no Encumbering Stone carried.</summary>
	public bool CanPickUp(int itemType) => Count(EncumberingStone) == 0 && HasRoomFor(itemType);
	public bool IsEquipped(int itemType) => Equipped.Contains(itemType);

	/// <summary>Chests by top-left tile, with their contents; looted ones are emptied.</summary>
	public readonly Dictionary<TilePos, List<(int Item, int Stack)>> Chests = new();
	public readonly HashSet<TilePos> LockedChests = new();
	/// <summary>Item types that are accessories (5 slots).</summary>
	public readonly HashSet<int> Accessories = new();
	public const int HermesBoots = 54, LesserHealingPotion = 28, InnerTube = 4404;

	public IReadOnlyList<ChestInfo> SeenChests(TilePos around, int radius) => Chests.Keys
		.Where(c => System.Math.Abs(c.X - around.X) <= radius && System.Math.Abs(c.Y - around.Y) <= radius && IsRevealed(c.X, c.Y))
		.OrderBy(c => c.ChebyshevTo(around)).Select(c => new ChestInfo(c, LockedChests.Contains(c))).ToList();

	/// <summary>Like vanilla: within about 5 × 4 tiles of the player, unlocked.</summary>
	public string LootChest(TilePos topLeft)
	{
		TilePos n = Sim.Node;
		if (!Chests.TryGetValue(topLeft, out var items) || LockedChests.Contains(topLeft) || System.Math.Abs(n.X - topLeft.X) > 5 || System.Math.Abs(n.Y - topLeft.Y) > 4)
			return null;
		foreach (var (item, stack) in items)
			Items[item] = Count(item) + stack;
		string got = string.Join(", ", items.Select(i => $"item{i.Item} x{i.Stack}"));
		items.Clear();
		return got;
	}

	public IReadOnlyList<int> UnwornAccessories() => Accessories.Where(a => Count(a) > 0 && !Equipped.Contains(a)).ToList();

	public bool TryEquip(int itemType)
	{
		if (Count(itemType) <= 0)
			return false;
		if (Accessories.Contains(itemType) && (Equipped.Contains(itemType) || Equipped.Count(Accessories.Contains) >= 5))
			return false;
		Items[itemType]--;
		Equipped.Add(itemType);
		return true;
	}

	public bool TryTakeOff(int itemType)
	{
		if (!Accessories.Contains(itemType) || !Equipped.Remove(itemType))
			return false;
		Items[itemType] = Count(itemType) + 1;
		return true;
	}

	public CraftResult TryCraft(int itemType)
	{
		// Like the crafting menu: any recipe for the item that is craftable right now.
		IReadOnlyList<RecipeInfo> all = RecipesFor(itemType);
		if (all.Count == 0)
			return CraftResult.NoRecipe;
		bool HasItems(RecipeInfo x) => x.Ingredients.All(i => this.CountAny(i.Accepts) >= i.Stack);
		bool HasStations(RecipeInfo x) => x.Stations.All(s => Stations.TryGetValue(s, out TilePos at) && at.ChebyshevTo(Sim.Node) <= StationRange);
		RecipeInfo r = all.FirstOrDefault(x => HasItems(x) && HasStations(x));
		if (r == null)
			return all.Any(HasItems) ? CraftResult.NeedsStation : CraftResult.Missing;
		// As the game: no room for the result (no free slot, no stack it joins, no ingredient stack used up) is busy.
		if (!HasRoomFor(itemType) && !r.Ingredients.Any(i => i.Accepts.Sum(a => Count(a)) <= i.Stack))
			return CraftResult.Busy;
		foreach (Ingredient ing in r.Ingredients) {
			int left = ing.Stack;
			foreach (int a in ing.Accepts) {
				int take = Math.Min(left, Count(a));
				Items[a] = Count(a) - take;
				left -= take;
			}
		}
		Items[itemType] = Count(itemType) + r.ResultStack;
		Crafts++;
		return CraftResult.Crafted;
	}

	public string Describe(int itemType) => RecipesFor(itemType).FirstOrDefault() is RecipeInfo r
		? string.Join(", ", r.Ingredients.Select(i => $"{i.Stack} {ItemName(i.Item)}")) + (r.Stations.Count > 0 ? " @ " + string.Join(", ", r.Stations.Select(TileName)) : "")
		: "no recipe";

	// ---------- simulation ----------

	private void OnMined(TilePos cell)
	{
		int tile = TileTypeAt(cell.X, cell.Y);
		TileTypes.Remove(cell);
		int item = tile switch { TileStone => StoneBlock, TileIron => IronOre, TileSilver => SilverOre, _ => DirtBlock };
		int y = FallRow(cell.X, cell.Y);
		Drops.Add(new DropInfo(nextDropId++, item, ItemName(item), 1, cell.X * 16f + 4, y * 16f + 4, 8, 8));
	}

	/// <summary>Items fall like in the game: down from (x, y) to the first row with a floor under it (the cell at y itself is being cleared).</summary>
	private int FallRow(int x, int y)
	{
		while (Grid.InBounds(x, y + 1) && !Grid.Get(x, y + 1).CanStandOn())
			y++;
		return y;
	}

	/// <summary>Applies this tick's controls to the fake world (axe, placing, swings, pickup), then moves the body.</summary>
	public void Step(BotContext ctx)
	{
		MoveInput o = ctx.Output;
		if (o.Tool == ToolRequest.Axe && o.UseItem && o.HasAim) {
			var aim = new TilePos(o.AimX, o.AimY);
			if (Trees.TryGetValue(aim, out int height) && Math.Abs(Sim.Node.X + 1 - aim.X) <= 4) {
				chopTicks[aim] = chopTicks.GetValueOrDefault(aim) + 1;
				if (chopTicks[aim] >= 30) {
					int wood = TreeWood(aim.X, aim.Y);
					Trees.Remove(aim);
					for (int i = 0; i < height; i++)
						Drops.Add(new DropInfo(nextDropId++, wood, ItemName(wood), 1, (aim.X + (i % 5) - 2) * 16f + 4, aim.Y * 16f + 4, 8, 8));
				}
			}
		}
		// A pickaxe on a heart: 3 hits (copper pickaxe power 35, 100 to break), then the whole 2×2 goes and drops a crystal.
		if (o.Tool == ToolRequest.Pickaxe && o.UseItem && o.HasAim && Furniture.TryGetValue(new TilePos(o.AimX, o.AimY), out int ft) && ft == TileHeart
			&& Sim.InToolRange(new TilePos(o.AimX, o.AimY))) {
			var key = new TilePos(o.AimX, o.AimY);
			chopTicks[key] = chopTicks.GetValueOrDefault(key) + 1;
			if (chopTicks[key] >= 45) {
				foreach (TilePos c in Furniture.Where(kv => kv.Value == TileHeart && Math.Abs(kv.Key.X - key.X) <= 1 && Math.Abs(kv.Key.Y - key.Y) <= 1).Select(kv => kv.Key).ToList())
					Furniture.Remove(c);
				Drops.Add(new DropInfo(nextDropId++, LifeCrystal, "Life Crystal", 1, key.X * 16f, FallRow(key.X, key.Y) * 16f + 4, 16, 16));
			}
		}
		// A pickaxe on a corrupt plant or corrupt thorns: one hit (tileNoFail, Main.cs:8037, 8066); a mushroom drops a vile mushroom.
		if (o.Tool == ToolRequest.Pickaxe && o.UseItem && o.HasAim && Furniture.TryGetValue(new TilePos(o.AimX, o.AimY), out int pt)
			&& (pt == TileCorruptPlants || pt == TileCorruptThorns) && Sim.InToolRange(new TilePos(o.AimX, o.AimY))) {
			var cell = new TilePos(o.AimX, o.AimY);
			Furniture.Remove(cell);
			if (pt == TileCorruptThorns)
				Grid.Set(cell.X, cell.Y, CellKind.Empty);
			if (FrameXs.Remove(cell, out int frame) && frame == VileMushroomFrame)
				Drops.Add(new DropInfo(nextDropId++, VileMushroom, "Vile Mushroom", 1, cell.X * 16f + 4, FallRow(cell.X, cell.Y) * 16f + 4, 8, 8));
		}
		// A pickaxe on a non-solid tile a test lets it take (a pot, a chain in the hellbridge's way): one hit removes it.
		if (o.Tool == ToolRequest.Pickaxe && o.UseItem && o.HasAim && Furniture.TryGetValue(new TilePos(o.AimX, o.AimY), out int any)
			&& PickaxeRemoves.Contains(any) && Sim.InToolRange(new TilePos(o.AimX, o.AimY)))
			Furniture.Remove(new TilePos(o.AimX, o.AimY));
		// Using a life crystal: +20 max life up to 400.
		if (o.Tool == ToolRequest.Item && o.ToolItem == LifeCrystal && o.UseItem && Count(LifeCrystal) > 0 && LifeMax < 400 && ++useTicks >= 30) {
			useTicks = 0;
			Items[LifeCrystal]--;
			LifeMax += 20;
		}
		if (o.Tool == ToolRequest.Item && o.UseItem && o.HasAim && Count(o.ToolItem) > 0 && Sim.InToolRange(new TilePos(o.AimX, o.AimY))) {
			if (o.ToolItem == WoodWall) {
				if (CanPlaceWall(WoodWall, o.AimX, o.AimY)) {
					Walls.Add(new TilePos(o.AimX, o.AimY));
					Items[WoodWall]--;
				}
			}
			else if (CanPlaceItem(o.ToolItem, o.AimX, o.AimY))
				PlaceFurniture(o.ToolItem, o.AimX, o.AimY);
		}
		PhysicsParams p = PhysicsParams.Default;
		float cx = Sim.State.CenterX(p), cy = Sim.State.CenterY(p);
		if (swingTimer == 0 && o.Horizontal != 0)
			facing = o.Horizontal;
		if (o.UseItem && o.Tool == ToolRequest.Weapon) {
			if (swingTimer == 0)
				swingTimer = 20;
			for (int i = 0; i < Slimes.Count; i++) {
				var s = Slimes[i];
				float dx = s.x + 12f - cx, dy = s.y + 12f - cy;
				if (Math.Abs(dx) <= 10f + 30f + 12f && Math.Abs(dy) <= 21f + 12f + 8f && Math.Sign(dx) == facing)
					Slimes[i] = (s.id, s.x, s.y, s.life - 10);
			}
			foreach (var dead in Slimes.Where(s => s.life <= 0).ToList()) {
				Slimes.Remove(dead);
				Drops.Add(new DropInfo(nextDropId++, Gel, "Gel", 2, dead.x + 8, dead.y + 8, 8, 8));
			}
		}
		ThrowAndBlast(o, cx, cy);
		ThrowKey(o, cx, cy);
		if (swingTimer > 0)
			swingTimer--;
		Sim.Step(o);
		if (Unrevealed.Count > 0) {
			TilePos n = Sim.Node;
			for (int x = n.X - RevealRadius; x <= n.X + RevealRadius; x++)
				for (int y = n.Y - RevealRadius; y <= n.Y + RevealRadius; y++)
					Unrevealed.Remove(new TilePos(x, y));
		}
		// A block placed by the sim comes out of the inventory (stone first, then dirt).
		for (; placedSeen < Sim.Placed; placedSeen++) {
			int item = Count(StoneBlock) > 0 ? StoneBlock : DirtBlock;
			Items[item] = Math.Max(0, Count(item) - 1);
		}
		cx = Sim.State.CenterX(p);
		cy = Sim.State.CenterY(p);
		foreach (DropInfo d in Drops.ToList())
			if (MathF.Abs(d.CenterX - cx) <= 42 && MathF.Abs(d.CenterY - cy) <= 42 && Count(EncumberingStone) == 0) {
				Items[d.Type] = Count(d.Type) + d.Stack;
				Drops.Remove(d);
			}
	}

	/// <summary>
	/// A Bomb or Dynamite thrown at the virtual mouse (one per swing) from the body's centre: where it comes to rest is the
	/// vanilla explosive AI as the bot models it (ExplosiveFlight; the grid does not change under it here); when the fuse runs
	/// out, every solid cell a blast breaks goes (ExplodeTiles: tile index to the spot, within the radius), and the player is
	/// hurt if the blast box touches the hitbox (PrepareBombToBlow's resize).
	/// </summary>
	private void ThrowAndBlast(in MoveInput o, float cx, float cy)
	{
		ExplosiveInfo e = o.ToolItem == Bomb ? BombInfo : o.ToolItem == Dynamite ? DynamiteInfo : null;
		if (e != null && o.Tool == ToolRequest.Item && o.UseItem && o.HasMouseAim && swingTimer == 0 && Count(e.ItemType) > 0) {
			Items[e.ItemType]--;
			swingTimer = e.UseTicks;
			var (rx, ry) = Bot.Skills.ExplosiveFlight.Rest(Grid, cx, cy, o.MouseX, o.MouseY, e, e.FuseTicks);
			Lit.Add((e, rx, ry, e.FuseTicks));
		}
		for (int i = Lit.Count - 1; i >= 0; i--) {
			var b = Lit[i];
			if (--b.Fuse > 0) {
				Lit[i] = b;
				continue;
			}
			Lit.RemoveAt(i);
			Blasts++;
			int bx = (int)MathF.Floor(b.X / 16f), by = (int)MathF.Floor(b.Y / 16f), r = b.Info.TileRadius + 1;
			for (int x = bx - r; x <= bx + r; x++)
				for (int y = by - r; y <= by + r; y++)
					if (Bot.Skills.ExplosiveFlight.InBlast(b.X, b.Y, b.Info, x, y) && CanExplode(x, y)) {
						Grid.Set(x, y, CellKind.Empty);
						TileTypes.Remove(new TilePos(x, y));
					}
			PhysicsParams p = PhysicsParams.Default;
			float left = Sim.State.X, top = Sim.State.Y, h = b.Info.BlastHalfPx;
			if (left + p.Width > b.X - h && left < b.X + h && top + p.Height > b.Y - h && top < b.Y + h) {
				BlastHits++;
				Life = Math.Max(0, (Life < 0 ? LifeMax : Life) - b.Info.Damage);
			}
		}
	}

	/// <summary>Items thrown with the Throw key and still in the air: top-left and velocity (px, px/tick).</summary>
	public readonly List<(int Item, float X, float Y, float VX, float VY, int Age)> Flying = new();
	/// <summary>
	/// How far (px, past the hitbox) the player pulls items in once their 100-tick no-grab delay is over, at 7 px/tick (Player.GrabItems:
	/// 42 by default; a Journey character's placement-range power adds 240). 0: no pull (the thrown item falls on).
	/// </summary>
	public float GrabRangePx;
	/// <summary>Throw presses that dropped an item, items lost in lava, and the Wall of Flesh's spawns (the doll in lava, the Guide alive).</summary>
	public int Throws, BurntItems, WofSpawns;
	/// <summary>Where the Wall spawned (its mouth's top-left, px) and the way it travels (-1 west, +1 east).</summary>
	public (float X, float Y, int Dir)? WofSpawn;
	/// <summary>The world's width in columns for the Wall's side rule (NPC.SpawnWOF: maxTilesX / 2); the grid's width unless set.</summary>
	public int WorldWidthTiles;
	private bool throwHeld;

	/// <summary>
	/// The Throw key as the game has it: pressed (and up the tick before) between swings with one item held, that one drops from
	/// the player's centre at 4 px/tick its way plus the body's speed and 2 px/tick up (Player.DropSelectedItem, Player.cs:6005-6006),
	/// then falls at 0.1 a tick up to 7, slowing 5 % a tick across (Item.cs:50626-50632). An item that meets lava burns; the Guide
	/// Voodoo Doll then kills the Guide and spawns the Wall of Flesh 1200 px from the player on the side of the nearer world edge
	/// (Item.CheckLavaDeath, NPC.SpawnWOF: NPC.cs:90924-91017), its mouth 100 x 100 and its eyes at the quarter points. One that
	/// lands on a solid cell or platform lies there (items rest on platforms, Item.cs:50828).
	/// </summary>
	private void ThrowKey(in MoveInput o, float cx, float cy)
	{
		bool press = o.Throw && !throwHeld;
		throwHeld = o.Throw;
		if (press && o.ThrowItem > 0 && swingTimer == 0 && Count(o.ThrowItem) > 0) {
			Items[o.ThrowItem]--;
			Throws++;
			Flying.Add((o.ThrowItem, cx - 7f, cy - 13f, 4f * facing + Sim.State.VX, -2f, 0));
		}
		for (int i = Flying.Count - 1; i >= 0; i--) {
			var (item, x, y, vx, vy, age) = Flying[i];
			age++;
			PhysicsParams pp = PhysicsParams.Default;
			float left = Sim.State.X - GrabRangePx, top = Sim.State.Y - GrabRangePx;
			if (GrabRangePx > 0f && age > 100 && x + 14f > left && x < left + pp.Width + 2 * GrabRangePx && y + 26f > top && y < top + pp.Height + 2 * GrabRangePx) {
				float dx = cx - (x + 7f), dy = cy - (y + 13f), d = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
				(vx, vy) = (dx / d * 7f, dy / d * 7f);
			}
			else {
				vy = Math.Min(vy + 0.1f, 7f);
				vx *= 0.95f;
				if (Math.Abs(vx) < 0.1f)
					vx = 0f;
			}
			x += vx;
			y += vy;
			int tx = (int)MathF.Floor((x + 7f) / 16f), ty = (int)MathF.Floor((y + 26f) / 16f);
			if (!Grid.InBounds(tx, ty)) {
				Flying.RemoveAt(i);
				continue;
			}
			CellKind k = Grid.Get(tx, ty);
			if (k.HasLava()) {
				Flying.RemoveAt(i);
				BurntItems++;
				if (item == GuideVoodooDoll && TownNpcAlive(GuideNpc))
					SpawnWof(x);
				continue;
			}
			if (k.CanStandOn() && vy > 0f) {
				Flying.RemoveAt(i);
				Drops.Add(new DropInfo(nextDropId++, item, ItemName(item), 1, x, ty * 16f - 26f, 14, 26));
				continue;
			}
			Flying[i] = (item, x, y, vx, vy, age);
		}
	}

	/// <summary>The Guide dies and the Wall spawns (NPC.SpawnWOF), its parts as hostiles the test moves on its own.</summary>
	private void SpawnWof(float dollX)
	{
		TownNpcs.RemoveAll(n => n.Type == GuideNpc);
		AliveTownNpcs.Remove(GuideNpc);
		if (ExtraHostiles.Any(h => h.Type == WofNpc))
			return;
		int width = WorldWidthTiles > 0 ? WorldWidthTiles : Grid.Width;
		int num = dollX / 16f <= width / 2 ? 1 : -1;
		float px = Sim.State.X, x = dollX;
		while (px > x - 1200f && px < x + 1200f)
			x -= num * 16f;
		int dir = x < px ? 1 : -1; // TargetClosest on its first tick: toward the player, and it never turns
		float mouthY = Sim.State.Y - 29f;
		WofSpawns++;
		WofSpawn = (x, mouthY, dir);
		ExtraHostiles.Add(new EntityInfo(950, WofNpc, "Wall of Flesh", x, mouthY, 100, 100, 1.5f * dir, 0f, 8000, 8000, 50, 27));
		ExtraHostiles.Add(new EntityInfo(951, WofEyeNpc, "Wall of Flesh", x, mouthY - 270f, 100, 100, 1.5f * dir, 0f, 8000, 8000, 50, 28, 1f));
		ExtraHostiles.Add(new EntityInfo(952, WofEyeNpc, "Wall of Flesh", x, mouthY + 270f, 100, 100, 1.5f * dir, 0f, 8000, 8000, 50, 28, -1f));
	}

	/// <summary>Stations and tables are solid-top (Platform), doors solid, chairs and torches not in the way.</summary>
	private void PlaceFurniture(int itemType, int x, int y)
	{
		int tile = TilePlacedBy(itemType);
		var (w, h, ox, oy) = Footprint(itemType);
		for (int cx = x - ox; cx < x - ox + w; cx++)
			for (int cy = y - oy; cy < y - oy + h; cy++) {
				Furniture[new TilePos(cx, cy)] = tile;
				// A closed door: open space for walking, as the game's auto-open makes it (TerrariaTileGrid); part of the
				// frame for the housing check.
				if (tile == TileDoor)
					Grid.Set(cx, cy, CellKind.Empty);
				else if (tile is not (TileChair or TileTorch or TileCampfire or TileLantern) && cy == y - oy)
					Grid.Set(cx, cy, CellKind.Platform);
			}
		if (tile is TileBench or TileFurnace or TileAnvil)
			Stations[tile] = new TilePos(x, y);
		Items[itemType]--;
	}

	/// <summary>Like the game adapter, stations are only rescanned every 20 ticks.</summary>
	private Dictionary<int, TilePos> seenStations = new();
	private int refreshes;

	public void Refresh(BotContext ctx)
	{
		ctx.Body = Sim.State;
		ctx.Tick = refreshes;
		ctx.IsNight = IsNight;
		ctx.PickPower = Count(IronPickaxe) > 0 ? 40 : 35;
		ctx.Combat.ItemAnimation = swingTimer;
		ctx.Combat.Facing = facing;
		ctx.Combat.LifeMax = LifeMax;
		ctx.Combat.LifeMaxBase = LifeMax;
		ctx.Combat.Life = Life < 0 ? LifeMax : Life;
		Blocks.BlockCount = Count(StoneBlock) + Count(DirtBlock);
		if (refreshes++ % 20 == 0)
			seenStations = new Dictionary<int, TilePos>(Stations);
		TilePos n = Sim.Node;
		ctx.Seen = new Surroundings {
			Trees = Trees.Keys.Where(t => Math.Abs(t.X - n.X) <= TreeSight).OrderBy(t => Math.Abs(t.X - n.X)).ToList(),
			Stations = seenStations,
			Origin = TreeSight < int.MaxValue ? n : null,
		};
		// Items in the air are in sight as drops are (Main.item holds both).
		ctx.Drops = Drops.Concat(Flying.Select((f, k) => new DropInfo(5000 + k, f.Item, ItemName(f.Item), 1, f.X, f.Y, 14, 26))).ToList();
		ctx.TownNpcs = TownNpcs.ToList();
		ctx.Shots = Shots.ToList();
		ctx.Hostiles = Slimes.Select(s => new EntityInfo(s.id, 1, "Slime", s.x, s.y, 24, 24, 0, 0, s.life, 40, 7, SlimeAi)).Concat(ExtraHostiles).ToList();
	}

	/// <summary>Adds a slime standing on the floor under tile (x, y).</summary>
	/// <summary>Hostiles other than slimes (a worm), as they are: not moved nor hurt by the fake.</summary>
	public readonly List<EntityInfo> ExtraHostiles = new();
	/// <summary>Hostile projectiles in sight (lasers), as they are: not moved by the fake.</summary>
	public readonly List<ShotInfo> Shots = new();
	public void AddSlime(int id, int x, int feetRow) => Slimes.Add((id, x * 16f, (feetRow + 1) * 16f - 24f, 40));
}

public sealed class TestLog : IBotLog
{
	private readonly ITestOutputHelper o;
	public readonly List<string> Lines = new();
	public TestLog(ITestOutputHelper o) => this.o = o;
	public void Info(string m) { Lines.Add(m); o?.WriteLine("INFO  " + m); }
	public void Warn(string m) { Lines.Add(m); o?.WriteLine("WARN  " + m); }
	public void Error(string m, Exception ex = null) { Lines.Add(m); o?.WriteLine("ERROR " + m); }
	public void Debug(string m) { }
}
