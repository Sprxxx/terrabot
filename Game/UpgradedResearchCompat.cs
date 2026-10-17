using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraBot.Game;

/// <summary>
/// Upgraded Research (Steam workshop 2710510282, mod "UpgradedResearch", decompiled v0.9.3), when it is loaded: its settings as they
/// are now, read by reflection from its live ModConfig instances (ConfigManager.Configs), so TerraBot builds and runs without it
/// (every answer is the vanilla one then). What of it the bot has to know, as its code does it:
/// <list type="bullet">
/// <item>"Use Creative Panel as Item Checklist" (SacrificeCountConfig.asItemChecklist): the duplication menu's clicks do nothing
/// (CreativeMenuGlobalItem hooks ItemSlot.LeftClick/RightClick for context 29), so the bot duplicates nothing either;</item>
/// <item>"Auto-Trash picked up Researched items" (TrashConfig.autoTrashResearched, off in checklist mode): ResearchGlobalItem.OnPickup
/// deletes a researched item as it is picked up, unless it is a coin (71-74), a Defender Medal (3822), on its blacklist, or a
/// carried stack of it has room for the pickup (isItemNotTrasheable);</item>
/// <item>"Craft from Researched stations" (Config.allowCraftFromResearch): RecipeGlobalTile.AdjTiles adds the stations in
/// ResearchPlayer.adj (tiles placed by fully researched items, with what they count as: a Hellforge is a Furnace) to
/// Player.adjTile wherever there is a tile in range, so a recipe needs no walk to them. The list is the mod's own: it fills only
/// once something has been researched since the world was entered (ResearchPlayer.resetItems clears it there);</item>
/// <item>"Researched Consumables/Ammo are infinite" (Config.infiniteItems, infiniteAmmo): GlobalItem.ConsumeItem and
/// CanBeConsumedAsAmmo keep a researched stack. The skills see a use by its swing or its tile, not by the stack, so nothing
/// here is needed for it; it is read for the log.</item>
/// </list>
/// Its research on its own (auto-research of 1-research pickups, of craftables, of recipe groups) and its changed research caps
/// (CreativeItemSacrificesCatalog, changed in place) need nothing: the bot reads the research state and the caps live.
/// </summary>
public static class UpgradedResearchCompat
{
	public const string ModName = "UpgradedResearch";

	private static bool looked;
	private static Mod mod;
	private static ModPlayer researchPlayerTemplate;
	private static FieldInfo configsField;

	/// <summary>The mod, or null when it is not loaded.</summary>
	public static Mod Mod
	{
		get {
			if (!looked) {
				looked = true;
				if (ModLoader.TryGetMod(ModName, out Mod m)) {
					mod = m;
					m.TryFind("ResearchPlayer", out researchPlayerTemplate);
				}
			}
			return mod;
		}
	}

	public static bool Loaded => Mod != null;

	/// <summary>Duplication works: false in checklist mode.</summary>
	public static bool DuplicationAllowed => !Loaded || !Bool("SacrificeCountConfig", "asItemChecklist", false);

	/// <summary>Picked-up researched items are deleted (the mod's own condition, OnPickup).</summary>
	public static bool AutoTrash => Loaded && Bool("TrashConfig", "autoTrashResearched", false) && !Bool("SacrificeCountConfig", "asItemChecklist", false);

	public static bool CraftFromResearch => Loaded && Bool("Config", "allowCraftFromResearch", false);
	public static bool InfiniteItems => Loaded && Bool("Config", "infiniteItems", false);
	public static bool InfiniteAmmo => Loaded && Bool("Config", "infiniteAmmo", false);

	/// <summary>
	/// A pickup of the item by the player would be deleted (ResearchGlobalItem.OnPickup with its isItemNotTrasheable), the item
	/// researched to its cap (<paramref name="researched"/>, the caller's check).
	/// </summary>
	public static bool PickupVanishes(Player p, int itemType, bool researched)
	{
		if (!researched || !AutoTrash || itemType is >= 71 and <= 74 or 3822)
			return false;
		if (Mod.TryFind("ResearchSharingBook", out ModItem book) && book.Type == itemType)
			return false;
		foreach (Item it in p.inventory)
			if (it.type == itemType && it.stack < it.maxStack)
				return false; // a pickup merges into it (the mod checks stack + the pickup's stack; one more is the bot's case)
		foreach (string ex in Field<List<string>>("TrashConfig", "exceptionList") ?? new List<string>()) {
			string s = ex?.Trim() ?? "";
			if (int.TryParse(s, out int id) ? id == itemType
				: ItemLoader.GetItem(itemType) is ModItem mi && mi.FullName.Equals(s, StringComparison.CurrentCultureIgnoreCase))
				return false;
		}
		return true;
	}

	/// <summary>The station tile is in range anywhere: "Craft from Researched stations" on and the tile in the mod's own list.</summary>
	public static bool StationAtHand(Player p, int tileType)
	{
		if (!CraftFromResearch || researchPlayerTemplate == null || p == null || !p.active)
			return false;
		ModPlayer rp = p.GetModPlayer(researchPlayerTemplate);
		return rp?.GetType().GetField("adj", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rp) is List<int> adj && adj.Contains(tileType);
	}

	/// <summary>The settings that change what the bot does, for the log on entering a world.</summary>
	public static string Describe() => !Loaded ? "not loaded"
		: $"loaded: duplication {(DuplicationAllowed ? "on" : "off (item checklist)")}, auto-trash researched pickups {(AutoTrash ? "on" : "off")}, "
		+ $"craft from researched stations {(CraftFromResearch ? "on" : "off")}, researched consumables {(InfiniteItems ? "infinite" : "used up")}, "
		+ $"researched ammo {(InfiniteAmmo ? "infinite" : "used up")}";

	private static ModConfig Config(string typeName)
	{
		if (Mod == null)
			return null;
		configsField ??= typeof(ConfigManager).GetField("Configs", BindingFlags.NonPublic | BindingFlags.Static);
		return configsField?.GetValue(null) is IDictionary<Mod, List<ModConfig>> all && all.TryGetValue(Mod, out List<ModConfig> list)
			? list.FirstOrDefault(c => c.GetType().Name == typeName) : null;
	}

	private static T Field<T>(string config, string field) where T : class =>
		Config(config) is ModConfig c ? c.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance)?.GetValue(c) as T : null;

	private static bool Bool(string config, string field, bool fallback) =>
		Config(config) is ModConfig c && c.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance)?.GetValue(c) is bool b ? b : fallback;
}
