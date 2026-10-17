using System.Collections.Generic;
using TerraBot.Bot;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Creative;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraBot.Game;

/// <summary>
/// Journey Mode's research and duplication for the local player, through the functions the Journey menu's own buttons run
/// (decompiled tML 2026.08; the user approved these UI-equivalent actions):
/// <list type="bullet">
/// <item>research: a stack into the research slot, as a shift-click on it does with the research menu open (ItemSlot.cs:640-647:
/// Main.CreativeMenu.SwapItem, then SacrificeItemInSacrificeSlot, whose button handler UICreativeInfiniteItemsDisplay.SacrificeWhatYouCan,
/// lines 521-543, calls CreativeUI.SacrificeItem(out int), CreativeUI.cs:293). That checks ItemLoader.CanResearch and the catalog,
/// takes what research still needs (at most the stack) and registers it (CreativeUI.cs:303-354). What it leaves is taken back to
/// the slot it came from, as a player drags it back. Never <c>CreativeUI.ResearchItem</c>, which researches from nothing;</item>
/// <item>duplication: a shift-click on a researched item in the duplication menu (UIDynamicItemCollection.cs:54-69 runs ItemSlot on
/// ContentSamples.ItemsByType[type] in context 29; ItemSlot.OverrideHover sets cursorOverride 7 when Player.ItemSpace allows it,
/// and OverrideLeftClick, ItemSlot.cs:618-625, clones it with stack = maxStack, OnCreated(JourneyDuplicationItemCreationContext)
/// and Player.GetItem with InventoryEntityToPlayerInventorySettings: a full stack straight into the bag).</item>
/// </list>
/// The menu's own guards hold: a Journey character (Player.difficulty 3, CreativeUI.Draw draws no menu otherwise) in a Journey
/// world, and not while a chest or an NPC's chat is open (CreativeUI.Blocked). Only bag stacks are offered (main inventory and
/// the ammo slots, never worn gear), never a favourited one (the shift-click skips those, ItemSlot.cs:415), never the selected
/// item while it is being used.
/// </summary>
public sealed class TerrariaJourney : IJourney
{
	/// <summary>Main inventory (0-49) and the ammo slots (54-57): where a stack can be taken into the research slot from.</summary>
	private static readonly int[] Slots = BuildSlots();

	private static int[] BuildSlots()
	{
		var s = new List<int>();
		for (int i = 0; i < TerrariaGameInfo.MainInventorySlots; i++)
			s.Add(i);
		for (int i = AmmoStart; i < AmmoEnd; i++)
			s.Add(i);
		return s.ToArray();
	}

	/// <summary>Player.inventory's ammo slots (Player.GetItem's FillAmmo, 54-57).</summary>
	private const int AmmoStart = 54, AmmoEnd = 58;

	private Player bound;
	private Player player => bound ?? Main.LocalPlayer;

	public void Bind(Player p) => bound = p;

	/// <summary>Off for a run that wants the Classic behaviour on a Journey character (the self-test's synthetic scenarios).</summary>
	public bool Allowed { get; set; } = true;

	public bool Enabled =>
		Allowed && !Main.gameMenu && player != null && player.whoAmI == Main.myPlayer && player.difficulty == PlayerDifficultyID.Creative
		&& Main.GameModeInfo.IsJourneyMode;

	public int ResearchNeeded(int itemType) =>
		itemType > 0 && CreativeItemSacrificesCatalog.Instance.TryGetSacrificeCountCapToUnlockInfiniteItems(itemType, out int n) ? n : 0;

	// ItemsSacrificedUnlocksTracker.GetSacrificeCount maps ContentSamples.CreativeResearchItemPersistentIdOverride itself.
	public int ResearchDone(int itemType) => itemType > 0 ? Main.LocalPlayerCreativeTracker.ItemSacrifices.GetSacrificeCount(itemType) : 0;

	// The duplication menu's own test (ItemsSacrificedUnlocksTracker.FillListOfItemsThatCanBeObtainedInfinitely).
	public bool IsResearched(int itemType) =>
		Enabled && itemType > 0 && Main.LocalPlayerCreativeTracker.ItemSacrifices.TryGetSacrificeNumbers(itemType, out int have, out int need) && have >= need;

	public IReadOnlyList<(int Type, int Carried)> Researchable()
	{
		var list = new List<(int, int)>();
		if (!Enabled)
			return list;
		var carried = new Dictionary<int, int>();
		foreach (int i in Slots) {
			Item it = player.inventory[i];
			if (it.IsAir || it.favorited)
				continue;
			carried[it.type] = carried.GetValueOrDefault(it.type) + it.stack;
		}
		foreach (var kv in carried) {
			int need = ResearchNeeded(kv.Key);
			if (need > 0 && ResearchDone(kv.Key) < need && ItemLoader.CanResearch(ContentSamples.ItemsByType[kv.Key]))
				list.Add((kv.Key, kv.Value));
		}
		return list;
	}

	public int Research(int itemType)
	{
		// Not dead: the menu does not open then (Player.ToggleCreativeMenu, Player.cs:5765-5770).
		if (!Enabled || player.dead || Main.CreativeMenu.Blocked || IsResearched(itemType))
			return 0;
		int left = ResearchNeeded(itemType) - ResearchDone(itemType);
		// The stack: the smallest that finishes the research in one go, else the biggest (the most progress for the click).
		int slot = -1;
		foreach (int i in Slots) {
			Item it = player.inventory[i];
			if (it.IsAir || it.type != itemType || it.favorited || i == player.selectedItem && player.itemAnimation > 0)
				continue;
			if (slot < 0) {
				slot = i;
				continue;
			}
			int best = player.inventory[slot].stack;
			bool fits = it.stack >= left, bestFits = best >= left;
			if (fits && (!bestFits || it.stack < best) || !fits && !bestFits && it.stack > best)
				slot = i;
		}
		if (slot < 0)
			return 0;
		// Whatever a player left in the research slot goes back to the bag first, as closing the menu does (CreativeUI.CloseMenu).
		Item waiting = Main.CreativeMenu.GetItemByIndex(0);
		if (waiting != null && !waiting.IsAir) {
			Main.CreativeMenu.SetItembyIndex(player.GetItem(Main.myPlayer, waiting, GetItemSettings.InventoryUIToInventorySettings), 0);
			if (!Main.CreativeMenu.GetItemByIndex(0).IsAir)
				return 0;
		}
		Main.CreativeMenu.SwapItem(ref player.inventory[slot]);
		Main.CreativeMenu.SacrificeItem(out int used);
		// The rest (or nothing) back where it came from; the slot then holds air, as the research slot did.
		Main.CreativeMenu.SwapItem(ref player.inventory[slot]);
		Recipe.FindRecipes();
		return used;
	}

	// Upgraded Research, when loaded (UpgradedResearchCompat): its settings as they are now; the vanilla answers without it.
	public bool DuplicationAllowed => UpgradedResearchCompat.DuplicationAllowed;
	// And TerraBot's own (ResearchFeatures, the user's TerraBot settings): a picked-up drop deleted, researched stations in range.
	public bool PickupVanishes(int itemType) => Enabled && (UpgradedResearchCompat.PickupVanishes(player, itemType, IsResearched(itemType))
		|| ResearchFeatures.PickupTrashed(player, itemType, 0));
	public bool StationAtHand(int tileType) => Enabled && (UpgradedResearchCompat.StationAtHand(player, tileType)
		|| ResearchFeatures.Config.CraftFromResearchedStations && System.Array.IndexOf(ResearchFeatures.ResearchedStations(), tileType) >= 0);

	public int Duplicate(int itemType)
	{
		if (!Enabled || !DuplicationAllowed || player.dead || Main.CreativeMenu.Blocked || !IsResearched(itemType) || itemType >= ItemLoader.ItemCount)
			return 0;
		Item sample = ContentSamples.ItemsByType[itemType];
		// OverrideHover's condition for the shift-click's cursorOverride 7 in context 29: the bag can take it.
		if (!player.ItemSpace(sample).CanTakeItemToPersonalInventory)
			return 0;
		Item[] inv = { sample };
		if (PlayerLoader.ShiftClickSlot(player, inv, ItemSlot.Context.CreativeInfinite, 0))
			return 0; // a mod took the shift-click (OverrideLeftClick returns there too)
		int before = Count(itemType);
		Item copy = sample.Clone();
		copy.stack = copy.maxStack;
		copy.OnCreated(new JourneyDuplicationItemCreationContext());
		player.GetItem(Main.myPlayer, copy, GetItemSettings.InventoryEntityToPlayerInventorySettings);
		Recipe.FindRecipes();
		return Count(itemType) - before;
	}

	private int Count(int itemType)
	{
		int n = 0;
		for (int i = 0; i < AmmoEnd; i++)
			if (player.inventory[i].type == itemType)
				n += player.inventory[i].stack;
		return n;
	}
}
