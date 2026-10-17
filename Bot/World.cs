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

	/// <summary>
	/// The wood the tree whose base trunk tile is at (x, y) drops when felled, by the ground it stands on (vanilla
	/// WorldGen.KillTile_GetTreeDrops: corrupt grass gives Ebonwood, crimson grass Shadewood, jungle grass Rich Mahogany...);
	/// 0 when it drops no wood (a gem tree) or there is no tree there.
	/// </summary>
	int TreeWood(int x, int y);

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

	/// <summary>
	/// Is the cell's background wall a dungeon wall (vanilla Main.wallDungeon: walls 7-9 and 94-99, Main.cs:10586-10594), in a
	/// cell the player has seen (revealed on the map)? A player standing in front of one below the surface is in the Dungeon
	/// (Player.ZoneDungeon, Player.cs:15312-15321).
	/// </summary>
	bool IsDungeonWall(int x, int y);

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

	/// <summary>
	/// Has the world recorded this boss NPC type as defeated (NPC.downedBoss1 and friends)? The Wall of Flesh (113): Main.hardMode,
	/// which its death starts (NPC.cs:85505-85525, WorldGen.StartHardmode).
	/// </summary>
	bool BossDefeated(int npcType);

	/// <summary>
	/// Is a town NPC of this type alive anywhere in the world? A player reads it off the full-screen map, which draws the head of
	/// every active town NPC with no exploration check (Main.cs:72101-72127): the Guide, whose Voodoo Doll is wasted without him
	/// (Item.CheckLavaDeath, Item.cs:50409-50425; docs M12_SPEC.md section 2b).
	/// </summary>
	bool TownNpcAlive(int npcType);

	/// <summary>
	/// The life the boss's health bar at the foot of the screen shows, 0 when there is none: what a player reads to know the
	/// boss is still up while none of it is in sight. The Eater of Worlds' bar sums the life of every active segment, wherever
	/// it is (vanilla Terraria.GameContent.UI.BigProgressBar.EaterOfWorldsProgressBar.ValidateAndCollectNecessaryInfo, lines
	/// 34-41): a worm burrowed out of sight still shows there.
	/// </summary>
	int BossBarLife(int npcType);

	/// <summary>Has the player seen this cell (revealed on the world map, vanilla Main.Map.IsRevealed)? Off the world: true.</summary>
	bool IsRevealed(int x, int y);

	/// <summary>Light level at the cell, 0 (dark) to about 1 (vanilla Lighting.Brightness).</summary>
	float LightAt(int x, int y);

	/// <summary>
	/// Would an explosive's blast break the tile in this cell (vanilla Projectile.CanExplodeTile: not dungeon bricks, chests,
	/// altars, lihzahrd bricks or the hardmode ores; hellstone and meteorite only in hardmode)? False when there is no tile.
	/// </summary>
	bool CanExplode(int x, int y);
}

/// <summary>Read-only view of the player's items.</summary>
public interface IInventory
{
	/// <summary>Total stack of this item type across the main inventory (hotbar included).</summary>
	int Count(int itemType);

	/// <summary>Is there a free main-inventory slot (or a stack this item can join)?</summary>
	bool HasRoomFor(int itemType);

	/// <summary>
	/// Would a drop of this item be picked up when touched: room for it (vanilla Player.ItemSpace: the main slots, the coin
	/// slots for coins, the ammo slots for ammo) and nothing carried that stops pickups (Player.CanAcceptItemIntoInventory:
	/// an Encumbering Stone anywhere in the inventory lets only hearts and mana stars through).
	/// </summary>
	bool CanPickUp(int itemType);

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

	/// <summary>
	/// Talk to the town NPC <paramref name="npcId"/> (whoAmI) and press its first chat button, as a right-click on it and a click
	/// on the button do (vanilla Main.HoverOverNPCs opening the chat, Main.cs:56077-56096, and Main.GUIChatDrawInner's
	/// first-button handler, Main.cs:52056-52124), with the game's own guards: in chat range, the button there (the Old Man's
	/// "Curse" only at night, Main.cs:51819-51825, and as mods leave it, NPCLoader.SetChatButtons). Returns null when it was
	/// pressed, else why not. Only the Old Man's button (summon Skeletron) is supported.
	/// </summary>
	string PressFirstChatButton(int npcId);
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
	/// <summary>
	/// Items that, carried anywhere in the inventory, stop every pickup but hearts and mana stars (the Encumbering Stone:
	/// Player.RefreshMechanicalAccsFromItemType sets preventAllItemPickups for it over all 58 slots, Player.UpdateEquips).
	/// </summary>
	public int[] PickupBlockers { get; init; } = System.Array.Empty<int>();
	/// <summary>
	/// Skeletron (Milestone 11, docs M11_SPEC.md): the head and its hands (NPC 35, 36, NPC.cs:4270-4300), the Old Man who curses
	/// the player to summon it (37, a town NPC at the Dungeon's entrance, NPC.cs:4301-4314) and the Dungeon Guardian (68, every
	/// spawn in the Dungeon before Skeletron is beaten, NPC.cs:88648-88673). 0 = none.
	/// </summary>
	public int SkeletronHead { get; init; }
	public int SkeletronHand { get; init; }
	public int OldMan { get; init; }
	public int DungeonGuardian { get; init; }
	/// <summary>The Dungeon's bricks (TileID.BlueDungeonBrick, GreenDungeonBrick, PinkDungeonBrick): seen on the surface, the Dungeon is there.</summary>
	public int[] DungeonBrickTiles { get; init; } = System.Array.Empty<int>();
	/// <summary>
	/// The snow biome's surface tiles (snow, ice) and the jungle's (jungle grass): world generation puts the snow on the
	/// Dungeon's side of the world and the jungle on the other (WorldGen.cs:8209-8259), which way to look for the Dungeon.
	/// </summary>
	public int[] SnowTiles { get; init; } = System.Array.Empty<int>();
	public int[] JungleTiles { get; init; } = System.Array.Empty<int>();
	/// <summary>Healing Potion (heals 100, Item.cs:5997) and Lesser Healing Potion (50): carried into the Skeletron fight.</summary>
	public int HealingPotion { get; init; }
	public int LesserHealingPotion { get; init; }
	/// <summary>Thrown explosives whose blast breaks tiles (the Bomb, Dynamite), smallest blast first; empty = none known.</summary>
	public ExplosiveInfo[] TileExplosives { get; init; } = System.Array.Empty<ExplosiveInfo>();
	/// <summary>
	/// The Wall of Flesh (Milestone 12, docs M12_SPEC.md): the Guide (NPC 22), whose Voodoo Doll (item 267) dropped into lava
	/// kills him and summons the Wall (Item.CheckLavaDeath, Item.cs:50396-50425); the Wall's mouth (113), its eyes (114, sharing
	/// the mouth's life), The Hungry (115) and The Hungry II (116), the leeches (117-119: head, body, tail; NPC.cs:5483-5600);
	/// the Voodoo Demon (66), the doll's only source (ItemDropDatabase.cs:1097). 0 = none.
	/// </summary>
	public int Guide { get; init; }
	public int GuideVoodooDoll { get; init; }
	public int WallOfFlesh { get; init; }
	public int WallOfFleshEye { get; init; }
	public int TheHungry { get; init; }
	public int TheHungryII { get; init; }
	public int LeechHead { get; init; }
	public int LeechBody { get; init; }
	public int LeechTail { get; init; }
	public int VoodooDemon { get; init; }
	/// <summary>
	/// The Underworld's tiles: the Hellforge (TileID 77), Hellstone (58; pick 65, Player.cs:55038-55041) and Ash (57), and the
	/// items Hellstone (174), Obsidian (173) and the Ash Block (172), the block the hellbridge's digging gives. 0 = none.
	/// </summary>
	public int HellforgeTile { get; init; }
	public int HellstoneTile { get; init; }
	public int AshTile { get; init; }
	public int Hellstone { get; init; }
	public int Obsidian { get; init; }
	public int AshBlock { get; init; }
	/// <summary>Shadow Scales (the Eater of Worlds' drop, item 86): kept for the Shadow armour of the spec's S1u (KeepRoom, G10).</summary>
	public int ShadowScale { get; init; }

	/// <summary>The Wall of Flesh's servants and other parts (114-119), the minions BossFightSkill fights with the mouth (113).</summary>
	public int[] WallOfFleshMinions => new[] { WallOfFleshEye, TheHungry, TheHungryII, LeechHead, LeechBody, LeechTail };

	public static readonly GameIds None = new();
}

/// <summary>
/// A thrown explosive that breaks tiles, with the vanilla numbers a blast is planned by. The Game layer fills them in from
/// the decompiled Projectile code (tML 2026.08): <see cref="TileRadius"/> is Projectile.Kill's ExplodeTiles radius (Bomb 4,
/// Dynamite 7), <see cref="BlastHalfPx"/> and <see cref="Damage"/> come from Projectile.PrepareBombToBlow (Resize to 128 /
/// 250 px, 100 / 250 damage, the thrower included), <see cref="ShootSpeed"/> and <see cref="UseTicks"/> from the item
/// (Item.shootSpeed, useAnimation), <see cref="SizePx"/> from Projectile.SetDefaults (28: 22, 29: 10), the roll and bounce from the
/// aiStyle 16 code (ground: velocity.X *= 0.97, Dynamite also *= 0.99; off a block: -0.4, Dynamite also *0.8), and the fuse from the wiki (Bomb 3 s, Dynamite 5 s: terraria.wiki.gg/wiki/Bomb and
/// /wiki/Dynamite; the decompile sets no timeLeft for projectiles 28 and 29 that could be found).
/// </summary>
public sealed record ExplosiveInfo(int ItemType, string Name, int TileRadius, float BlastHalfPx, int Damage, int FuseTicks, float ShootSpeed, int UseTicks,
	int SizePx, float GroundFriction, float Bounce);

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
