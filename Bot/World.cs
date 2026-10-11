using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Bot;

/// <summary>Tile facts the skills need beyond the pathfinding grid (implemented by the game adapter).</summary>
public interface IWorldInfo
{
	/// <summary>Is there a tree trunk tile (any tree kind) at this cell?</summary>
	bool IsTreeTrunk(int x, int y);

	/// <summary>Could the item (furniture, block) be placed with its placement tile at (x, y) right now, ignoring
	/// the player's own hitbox (the skill keeps clear of it)?</summary>
	bool CanPlaceItem(int itemType, int x, int y);

	/// <summary>Tile type at the cell, or -1 when there is no tile.</summary>
	int TileTypeAt(int x, int y);

	/// <summary>Cells holding <paramref name="tileType"/> within <paramref name="radius"/> tiles (Chebyshev) of <paramref name="around"/>, nearest first.</summary>
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
}

/// <summary>Read-only view of the player's items.</summary>
public interface IInventory
{
	/// <summary>Total stack of this item type across the main inventory (hotbar included).</summary>
	int Count(int itemType);

	/// <summary>Is there a free main-inventory slot (or a stack this item can join)?</summary>
	bool HasRoomFor(int itemType);

	/// <summary>Is the item worn in an armour slot (head, body, legs)?</summary>
	bool IsEquipped(int itemType);
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

	/// <summary>Put a carried armour piece on, like right-clicking it (vanilla ItemSlot.SwapEquip). False if it could not.</summary>
	bool TryEquip(int itemType);

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
	/// <summary>Furniture and walls for a house.</summary>
	public int WoodWall { get; init; }
	public int Door { get; init; }
	public int Chair { get; init; }
	public int Table { get; init; }
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

	public static readonly GameIds None = new();
}

/// <summary>Things the bot noticed in the world this tick, beyond NPCs and drops.</summary>
public sealed class Surroundings
{
	/// <summary>Base trunk tile of each tree near the player (nearest first).</summary>
	public IReadOnlyList<TilePos> Trees { get; set; } = System.Array.Empty<TilePos>();

	/// <summary>Placed crafting-station tiles near the player, by tile type (top-left tile of each).</summary>
	public IReadOnlyDictionary<int, TilePos> Stations { get; set; } = new Dictionary<int, TilePos>();
}
