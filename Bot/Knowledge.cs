using System.Collections.Generic;

namespace TerraBot.Bot;

/// <summary>One ingredient line of a recipe: <see cref="Stack"/> of any item in <see cref="Accepts"/>.</summary>
public sealed class Ingredient
{
	/// <summary>Accepted item types; [0] is the item the recipe lists (the rest come from a recipe group, e.g. any wood).</summary>
	public IReadOnlyList<int> Accepts { get; }
	public int Stack { get; }
	public int Item => Accepts[0];

	public Ingredient(int stack, params int[] accepts)
	{
		Stack = stack;
		Accepts = accepts;
	}
}

/// <summary>A recipe as the planner sees it (game-independent copy of a Terraria Recipe).</summary>
public sealed class RecipeInfo
{
	public int Result { get; init; }
	public int ResultStack { get; init; } = 1;
	public IReadOnlyList<Ingredient> Ingredients { get; init; } = System.Array.Empty<Ingredient>();
	/// <summary>Required crafting-station tile types (all of them, like vanilla's requiredTile).</summary>
	public IReadOnlyList<int> Stations { get; init; } = System.Array.Empty<int>();
}

public enum SourceKind
{
	/// <summary>Not gathered: crafted (if a recipe exists) or unobtainable.</summary>
	None,
	/// <summary>Chop trees (wood).</summary>
	Tree,
	/// <summary>Mine tiles of <see cref="ItemSource.TileType"/> with a pickaxe (stone, ores).</summary>
	Mine,
	/// <summary>Kill NPCs with <see cref="ItemSource.NpcAiStyle"/> and pick up the drop (gel from slimes).</summary>
	Hunt,
}

/// <summary>
/// Where a raw material comes from. <see cref="FrameX"/>: only tiles of that frame column drop it (-1: any), as a corrupt
/// plant drops a vile mushroom only at frame 144 (WorldGen.cs:59639). <see cref="EvilBiome"/>: it is had only in the evil
/// biome (rotten chunks from eaters of souls, vile mushrooms on corrupt grass), so getting it is an errand there
/// (BotContext.EvilErrand).
/// </summary>
public readonly record struct ItemSource(SourceKind Kind, int TileType = -1, int NpcAiStyle = -1, int FrameX = -1, bool EvilBiome = false)
{
	public static readonly ItemSource None = new(SourceKind.None);
}

/// <summary>
/// What the bot knows about items: recipes, raw-material sources and which item places which station. In game
/// this is read from the loaded recipes and item data (mods included); tests supply a small table.
/// </summary>
public interface IGameKnowledge
{
	IReadOnlyList<RecipeInfo> RecipesFor(int itemType);

	/// <summary>How to gather the item directly; <see cref="SourceKind.None"/> if it must be crafted (or cannot be had).</summary>
	ItemSource SourceOf(int itemType);

	/// <summary>Item types that place this tile type (e.g. Iron Anvil and Lead Anvil both place an anvil).</summary>
	IReadOnlyList<int> ItemsPlacing(int tileType);

	/// <summary>The tile type this item places, or -1.</summary>
	int TilePlacedBy(int itemType);

	string ItemName(int itemType);
	string TileName(int tileType);

	/// <summary>What a shop pays for the item (vanilla Item.value, in copper); 0: worth nothing.</summary>
	int ItemValue(int itemType);

	/// <summary>
	/// Where world generation puts the tile, for one looked for in the world rather than placed (a demon or crimson altar):
	/// what a player knows of where to look. Null when generation has no such rule for it.
	/// </summary>
	GenArea? GenerationArea(int tileType) => null;

	/// <summary>Armour pieces (head, body, legs; no vanity) some recipe makes: the candidates for an armour upgrade.</summary>
	IReadOnlyList<int> CraftableArmor() => System.Array.Empty<int>();
}

/// <summary>
/// The tiles world generation puts a tile in: columns <see cref="MinX"/>..<see cref="MaxX"/> but not
/// <see cref="SkipFromX"/>..<see cref="SkipToX"/>, rows <see cref="MinY"/>..<see cref="MaxY"/>. For the altars (WorldGen's
/// "Altars" pass, WorldGen.cs:11846-11898): never in the middle tenth of the world's width, where the spawn is.
/// </summary>
public readonly record struct GenArea(int MinX, int MaxX, int SkipFromX, int SkipToX, int MinY, int MaxY)
{
	public bool HoldsColumn(int x) => x >= MinX && x <= MaxX && (x < SkipFromX || x > SkipToX);

	/// <summary>Columns it holds from <paramref name="x"/> on in direction <paramref name="dir"/> before an edge or the skipped middle (0 when x is not in it).</summary>
	public int RoomFrom(int x, int dir)
	{
		if (!HoldsColumn(x))
			return 0;
		int end = dir > 0 ? (x < SkipFromX ? SkipFromX - 1 : MaxX) : (x > SkipToX ? SkipToX + 1 : MinX);
		return System.Math.Abs(end - x);
	}
}

public static class InventoryExtensions
{

	/// <summary>Total of all accepted item types.</summary>
	public static int CountAny(this IInventory inv, IReadOnlyList<int> types)
	{
		int n = 0;
		foreach (int t in types)
			n += inv.Count(t);
		return n;
	}
}
