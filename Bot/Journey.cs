using System.Collections.Generic;

namespace TerraBot.Bot;

/// <summary>
/// Journey Mode's research and duplication, as the Journey menu offers them (implemented by the game adapter). Research is per
/// character (vanilla Player.creativeTracker, Main.LocalPlayerCreativeTracker, Main.cs:3305): an item researched to its cap
/// (CreativeItemSacrificesCatalog, from the game's Sacrifices.tsv) can be duplicated for ever after, a full stack a click.
/// With Journey off (<see cref="Enabled"/> false) every call does nothing and the bot plays as on Classic.
/// </summary>
public interface IJourney
{
	/// <summary>A Journey character (Player.difficulty 3) in a Journey world (Main.GameModeInfo.IsJourneyMode), research allowed.</summary>
	bool Enabled { get; }

	/// <summary>How many of the item research takes in all (its cap), or 0 when it can never be researched.</summary>
	int ResearchNeeded(int itemType);

	/// <summary>How many of the item have been researched so far.</summary>
	int ResearchDone(int itemType);

	/// <summary>Researched to its cap: the duplication menu lists it (partly researched items are not).</summary>
	bool IsResearched(int itemType);

	/// <summary>
	/// The carried items research would still take (not yet researched, researchable), with how many are carried (main
	/// inventory and ammo slots, the slots a stack can be taken from into the research slot).
	/// </summary>
	IReadOnlyList<(int Type, int Carried)> Researchable();

	/// <summary>
	/// One stack of the item into the research slot and the research button pressed, as the Journey menu does (the game's own
	/// checks: researchable, not favourited, not the item in use). What research does not take goes back where it came from.
	/// Returns how many were used (0: refused or nothing to give).
	/// </summary>
	int Research(int itemType);

	/// <summary>
	/// One click on a researched item in the duplication menu (shift-click: a full stack straight into the bag, as the game
	/// gives it). Returns how many came into the bag (0: not researched, or no room).
	/// </summary>
	int Duplicate(int itemType);

	/// <summary>
	/// The duplication menu works. False with a research mod that turns it into a checklist (Upgraded Research's "Use Creative
	/// Panel as Item Checklist"): research still counts, nothing comes out of the menu, so the bot gathers as on Classic.
	/// </summary>
	bool DuplicationAllowed => true;

	/// <summary>Every item researched to its cap (what the duplication menu lists), refreshed every few seconds by the game.</summary>
	IReadOnlyList<int> ResearchedItems() => System.Array.Empty<int>();

	/// <summary>
	/// A pickup of the item would be deleted, not put in the bag (Upgraded Research's "Auto-Trash picked up Researched items"):
	/// a drop not worth walking to.
	/// </summary>
	bool PickupVanishes(int itemType) => false;

	/// <summary>
	/// The station counts as in range wherever the bot is (Upgraded Research's "Craft from Researched stations", for a researched
	/// station): a craft at it needs no walk and no station placed.
	/// </summary>
	bool StationAtHand(int tileType) => false;
}

/// <summary>Classic (or Expert, Master): no research, no duplication.</summary>
public sealed class NoJourney : IJourney
{
	public static readonly NoJourney Instance = new();
	public bool Enabled => false;
	public int ResearchNeeded(int itemType) => 0;
	public int ResearchDone(int itemType) => 0;
	public bool IsResearched(int itemType) => false;
	public IReadOnlyList<(int Type, int Carried)> Researchable() => System.Array.Empty<(int, int)>();
	public int Research(int itemType) => 0;
	public int Duplicate(int itemType) => 0;
}

public static class JourneyExtensions
{
	/// <summary>Research still to give before the item is researched (0 when done, or never researchable, or Journey is off).</summary>
	public static int ResearchLeft(this IJourney j, int itemType)
	{
		if (j == null || !j.Enabled)
			return 0;
		int cap = j.ResearchNeeded(itemType);
		return cap <= 0 ? 0 : System.Math.Max(0, cap - j.ResearchDone(itemType));
	}
}
