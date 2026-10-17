using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Bot;

/// <summary>Tile facts the skills need beyond the pathfinding grid (implemented by the game adapter).</summary>
/// <summary>A chest the player has seen: its top-left tile, and whether it is locked (gold chests in the dungeon...).</summary>
public readonly record struct ChestInfo(TilePos TopLeft, bool Locked);

public interface IWorldInfo
{
	/// <summary>Chests within <paramref name="radius"/> tiles that the player has seen (revealed on the map), nearest first.</summary>
	IReadOnlyList<ChestInfo> SeenChests(TilePos around, int radius);

	/// <summary>Is there a tree trunk tile (any tree kind) at this cell?</summary>
	bool IsTreeTrunk(int x, int y);

	/// <summary>Could the item (furniture, block) be placed with its placement tile at (x, y) right now, ignoring
	/// the player's own hitbox (the skill keeps clear of it)?</summary>
	bool CanPlaceItem(int itemType, int x, int y);

	/// <summary>Tile type at the cell, or -1 when there is no tile.</summary>
	int TileTypeAt(int x, int y);

	/// <summary>
	/// The tile's frame column (vanilla Tile.TileFrameX) at the cell, or -1 when there is no tile: which style of a tile
	/// type it is (a corrupt plant at frame 144 is a vile mushroom, WorldGen.KillTile_GetItemDrops).
	/// </summary>
	int TileFrameX(int x, int y);

	/// <summary>
	/// Cells holding <paramref name="tileType"/> within <paramref name="radius"/> tiles (Chebyshev) of <paramref name="around"/>,
	/// nearest first: only cells the player has seen (revealed on the map), as a player would know them.
	/// </summary>
	IReadOnlyList<TilePos> FindTiles(int tileType, TilePos around, int radius);

	/// <summary>Is there a background wall in the cell?</summary>
	bool HasWall(int x, int y);

	/// <summary>Could the wall item go into the cell now (vanilla: no wall yet, a tile or wall on a 4-neighbour)?</summary>
	bool CanPlaceWall(int wallItem, int x, int y);

	/// <summary>Tile footprint of a placeable item: size and the origin cell the cursor aims at (TileObjectData).</summary>
	(int Width, int Height, int OriginX, int OriginY) Footprint(int itemType);

	/// <summary>The game's housing query for the room containing the cell: null if a town NPC could live there, else why not.</summary>
	string CheckHousing(int x, int y);

	/// <summary>
	/// Can a mining skill break this tile on purpose although path moves never would? For multi-tile objects worth
	/// mining for their drop (a life crystal): the current pickaxe can break it and the game allows it.
	/// </summary>
	bool CanMineTarget(int x, int y);

	/// <summary>Has the world recorded this boss NPC type as defeated (NPC.downedBoss1 and friends)?</summary>
	bool BossDefeated(int npcType);

	/// <summary>Has the player seen this cell (revealed on the world map, vanilla Main.Map.IsRevealed)? Off the world: true.</summary>
	bool IsRevealed(int x, int y);

	/// <summary>Light level at the cell, 0 (dark) to about 1 (vanilla Lighting.Brightness).</summary>
	float LightAt(int x, int y);
}

/// <summary>Read-only view of the player's items.</summary>
public interface IInventory
{
	/// <summary>Total stack of this item type across the main inventory (hotbar included).</summary>
	int Count(int itemType);

	/// <summary>Is there a free main-inventory slot (or a stack this item can join)?</summary>
	bool HasRoomFor(int itemType);

	/// <summary>Is the item worn in an armour slot (head, body, legs) or an accessory slot?</summary>
	bool IsEquipped(int itemType);

	/// <summary>Accessories carried but not worn (no vanity), one entry per type.</summary>
	IReadOnlyList<int> UnwornAccessories();

	/// <summary>Empty main-inventory slots.</summary>
	int FreeSlots { get; }

	/// <summary>Every stack in the main inventory (hotbar included), slot by slot.</summary>
	IReadOnlyList<(int Type, int Stack)> Stacks();

	/// <summary>Gear a player keeps: a tool, weapon, armour, accessory, healing item or ammo (by the item's own stats).</summary>
	bool IsGear(int itemType);
}

public enum CraftResult
{
	Crafted,
	/// <summary>Not enough ingredients.</summary>
	Missing,
	/// <summary>Ingredients are there, but a required crafting station is not in range.</summary>
	NeedsStation,
	/// <summary>No recipe makes this item.</summary>
	NoRecipe,
	/// <summary>Cannot right now (cursor holds an item, inventory full...).</summary>
	Busy,
}

/// <summary>Crafting the way the crafting menu does it (one recipe click per call).</summary>
public interface ICrafter
{
	CraftResult TryCraft(int itemType);

	/// <summary>
	/// Put a carried armour piece or accessory on, like right-clicking it (vanilla ItemSlot.SwapEquip). Accessories
	/// only into an empty slot (never swapping one out). False if it could not.
	/// </summary>
	bool TryEquip(int itemType);

	/// <summary>Take a worn accessory off into a free inventory slot, like shift-clicking it. False if it could not.</summary>
	bool TryTakeOff(int itemType);

	/// <summary>Trash the smallest stack of this item, like ctrl-clicking it (vanilla ItemSlot quick trash). False if none.</summary>
	bool TryTrash(int itemType);

	/// <summary>
	/// Open the chest at <paramref name="topLeft"/> and take everything, like right-clicking it and pressing Loot All
	/// (vanilla Player.OpenChest, ChestUI.LootAll), then close it. Only within the game's interaction range and only
	/// unlocked chests. Returns what was taken ("" if it was empty), or null if it could not be opened.
	/// </summary>
	string LootChest(TilePos topLeft);

	/// <summary>Human-readable recipe needs for logs ("10 Wood @ Work Bench").</summary>
	string Describe(int itemType);
}

/// <summary>
/// Game IDs the pure layers need, filled by the game adapter from ItemID/TileID so Bot/ never references
/// Terraria.
/// </summary>
public sealed class GameIds
{
	public int Wood { get; init; }
	public int WorkBenchItem { get; init; }
	public int WorkBenchTile { get; init; }
	public int WoodenSword { get; init; }
	public int WoodenHammer { get; init; }
	public int Acorn { get; init; }
	public int Gel { get; init; }
	public int Torch { get; init; }
	public int FurnaceTile { get; init; }
	public int AnvilTile { get; init; }
	/// <summary>The pickaxe of this world's iron tier (Iron or Lead Pickaxe, set per world).</summary>
	public int IronTierPickaxe { get; init; }
	public int IronTierBroadsword { get; init; }
	public int IronTierHelmet { get; init; }
	public int IronTierChainmail { get; init; }
	public int IronTierGreaves { get; init; }
	/// <summary>This world's silver-tier armour (Silver or Tungsten): another way to the armour when iron runs out. 0 = none.</summary>
	public int SilverTierHelmet { get; init; }
	public int SilverTierPickaxe { get; init; }
	public int SilverTierBroadsword { get; init; }
	public int SilverTierChainmail { get; init; }
	public int SilverTierGreaves { get; init; }
	/// <summary>Furniture and walls for a house.</summary>
	public int WoodWall { get; init; }
	public int Door { get; init; }
	public int Chair { get; init; }
	public int Table { get; init; }
	/// <summary>Tile types that make up a desert (sand, hardened sand, sandstone): the underground desert.</summary>
	public int[] DesertTiles { get; init; } = System.Array.Empty<int>();
	/// <summary>Tile types of the Corruption and the Crimson (their stone, grass, sand, ice).</summary>
	public int[] EvilTiles { get; init; } = System.Array.Empty<int>();
	/// <summary>NPC types of the Corruption and the Crimson (eaters of souls, devourers, crimeras...): seen, the biome is near.</summary>
	public int[] EvilEnemies { get; init; } = System.Array.Empty<int>();
	/// <summary>NPC types of the underground desert (antlions, tomb crawlers): seen, the desert is near.</summary>
	public int[] DesertEnemies { get; init; } = System.Array.Empty<int>();
	/// <summary>Blocks the bot builds frames from (any of them).</summary>
	public int[] BuildingBlocks { get; init; } = System.Array.Empty<int>();
	/// <summary>Boss preparation (Milestone 7).</summary>
	public int LifeCrystal { get; init; }
	public int IronTierBow { get; init; }
	public int WoodenArrow { get; init; }
	public int Lens { get; init; }
	public int SuspiciousEye { get; init; }
	public int DemonAltarTile { get; init; }
	/// <summary>Eye of Cthulhu and its Servants (NPC types).</summary>
	public int EyeBoss { get; init; }
	public int EyeServant { get; init; }
	/// <summary>Arena regeneration: a campfire (Cozy Fire) on the floor, a heart lantern (Heart Lamp) hung in the house.</summary>
	public int Campfire { get; init; }
	public int HeartLantern { get; init; }
	/// <summary>Recall Potion: drunk, it takes the player home to the spawn point.</summary>
	public int RecallPotion { get; init; }
	/// <summary>
	/// The evil-biome boss of a Corruption world (Milestone 10, docs M10_SPEC.md): the Eater of Worlds' head, body and tail
	/// (NPC 13, 14, 15), summoned with Worm Food (30 Vile Powder + 15 Rotten Chunk at a Demon Altar, Recipe.cs:14407).
	/// </summary>
	public int EvilBoss { get; init; }
	public int EvilBossBody { get; init; }
	public int EvilBossTail { get; init; }
	public int WormFood { get; init; }
	public int VilePowder { get; init; }
	public int VileMushroom { get; init; }
	/// <summary>
	/// Corrupt grass (TileID.CorruptGrass), the ground vile mushrooms grow on, and corrupt thorns (TileID.CorruptThorns),
	/// which grow there in place of a plant 1 in 13 (WorldGen.cs:54339) and block that grass top until cut. 0 = none.
	/// </summary>
	public int CorruptGrass { get; init; }
	public int CorruptThorns { get; init; }
	public int RottenChunk { get; init; }
	/// <summary>Worm Tooth (Devourers: 3-8 at 100%, ItemDropDatabase.cs:1113): with 5 wooden arrows, 5 Unholy Arrows.</summary>
	public int WormTooth { get; init; }
	public int UnholyArrow { get; init; }
	public int DemoniteOre { get; init; }
	public int DemoniteBar { get; init; }
	public int DemonBow { get; init; }
	/// <summary>The Bottle item and the Placed Bottle tile it makes (TileID.Bottles), the station Vile Powder is made at.</summary>
	public int Bottle { get; init; }
	public int BottleTile { get; init; }
	/// <summary>
	/// What each tile type counts for the game's Corruption zone (vanilla TileID.Sets.CorruptBiome, summed by
	/// TileLoader.cs:1642 over the scene scan: corrupt grass, ebonstone, ebonsand... 1 each, a sunflower -10). Worm Food
	/// works only while that sum is 300 or more (Player.ZoneCorrupt, SceneMetrics.CorruptionTileThreshold).
	/// </summary>
	public IReadOnlyDictionary<int, int> CorruptionWeights { get; init; } = new Dictionary<int, int>();
	/// <summary>
	/// Accessories that hold the body at the water's surface (the Inner Tube): with one on, nobody sinks to a flooded floor
	/// unless holding Down, and the paths walk flooded floors.
	/// </summary>
	public int[] FloatingAccessories { get; init; } = System.Array.Empty<int>();

	public static readonly GameIds None = new();
}

/// <summary>Things the bot noticed in the world this tick, beyond NPCs and drops.</summary>
public sealed class Surroundings
{
	/// <summary>Base trunk tile of each tree near the player (nearest first).</summary>
	public IReadOnlyList<TilePos> Trees { get; set; } = System.Array.Empty<TilePos>();

	/// <summary>Placed crafting-station tiles near the player, by tile type (top-left tile of each).</summary>
	public IReadOnlyDictionary<int, TilePos> Stations { get; set; } = new Dictionary<int, TilePos>();

	/// <summary>Where the scan was made (feet tile); null = unknown (treated as the bot's position).</summary>
	public TilePos? Origin { get; set; }
}
