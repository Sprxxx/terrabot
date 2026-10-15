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

/// <summary>Where a raw material comes from.</summary>
public readonly record struct ItemSource(SourceKind Kind, int TileType = -1, int NpcAiStyle = -1)
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
