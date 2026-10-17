using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent.Creative;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// TerraBot's own Journey conveniences, the ones the user had from Upgraded Research (2026-10-17: "i dont want the upgraded
/// research mod anymore, as long as we have the same functionality"), each a setting in <see cref="TerraBotConfig"/>, for the
/// local Journey character in a Journey world only, the bot on or off:
/// <list type="bullet">
/// <item>trash researched pickups: a picked-up item already researched is deleted (GlobalItem.OnPickup false, which
/// Player.GrabItems turns into removing the world item), as Upgraded Research's auto-trash; with the bot in auto mode only what
/// it has no use for (<see cref="Bot.Brain.ResearchHabit.Trashable"/>), and never coins nor an item with a prefix (a
/// duplicate has none);</item>
/// <item>infinite researched consumables and ammo: GlobalItem.ConsumeItem and CanBeConsumedAsAmmo keep a researched stack
/// (crafting is not affected: it consumes through RecipeLoader, not ConsumeItem);</item>
/// <item>craft from researched stations: GlobalTile.AdjTiles adds every crafting station a researched item places (with what it
/// counts as, Player.AdjTiles' own list: a Hellforge is a Furnace) wherever a tile is in range;</item>
/// <item>the research still to give in the tooltip; a shift-click on a researched item with the research menu open trashes it
/// (<see cref="TerraBotPlayer.ShiftClickSlot"/>).</item>
/// </list>
/// </summary>
public static class ResearchFeatures
{
	public static TerraBotConfig Config => ModContent.GetInstance<TerraBotConfig>();

	/// <summary>The local player, a Journey character in a Journey world (the research menu's own condition).</summary>
	public static bool JourneyLocal(Player p) =>
		p != null && p.whoAmI == Main.myPlayer && Main.netMode != NetmodeID.Server && !Main.gameMenu && p.difficulty == PlayerDifficultyID.Creative
		&& Main.GameModeInfo.IsJourneyMode;

	/// <summary>Researched to its cap (the duplication menu's own test).</summary>
	public static bool Researched(int itemType) =>
		itemType > 0 && Main.LocalPlayerCreativeTracker.ItemSacrifices.TryGetSacrificeNumbers(itemType, out int have, out int need) && need > 0 && have >= need;

	/// <summary>
	/// A pickup of <paramref name="item"/> by <paramref name="p"/> is deleted: the setting on, duplication there to get it back
	/// (not Upgraded Research's checklist mode), the item researched, not money, not prefixed. The bot in auto mode: unless it
	/// keeps it. A player: unless a carried stack of it has room (it merges, as Upgraded Research has it).
	/// </summary>
	public static bool PickupTrashed(Player p, int itemType, int prefix)
	{
		if (!Config.TrashResearchedItems || !JourneyLocal(p) || !UpgradedResearchCompat.DuplicationAllowed || prefix != 0
			|| itemType is >= ItemID.CopperCoin and <= ItemID.PlatinumCoin || !Researched(itemType))
			return false;
		TerraBotPlayer bot = p.GetModPlayer<TerraBotPlayer>();
		if (bot.Brain.Auto)
			return Bot.Brain.ResearchHabit.Trashable(bot.Context, itemType);
		foreach (Item it in p.inventory)
			if (it.type == itemType && it.stack < it.maxStack)
				return false;
		return true;
	}

	private static object trackerSeen;
	private static int editSeen = -1;
	private static int[] stations = Array.Empty<int>();
	private static Dictionary<int, List<int>> placers;

	/// <summary>
	/// Crafting stations a researched item places, with what each counts as (Player.AdjTiles: Hellforge and Glass Kiln a Furnace,
	/// Adamantite Forge a Furnace and Hellforge, Mythril Anvil an Anvil, Bewitching Table, Alchemy Table and the rest a Table, the
	/// Alchemy Table a Bottle too; a mod tile's AdjTiles). Worked out again whenever research changes (LastEditId).
	/// </summary>
	public static int[] ResearchedStations()
	{
		var tracker = Main.LocalPlayerCreativeTracker.ItemSacrifices;
		if (ReferenceEquals(tracker, trackerSeen) && tracker.LastEditId == editSeen && placers != null)
			return stations;
		trackerSeen = tracker;
		editSeen = tracker.LastEditId;
		placers ??= BuildPlacers();
		var set = new HashSet<int>();
		foreach (var kv in placers)
			if (kv.Value.Any(Researched))
				AddStation(set, kv.Key);
		stations = set.ToArray();
		return stations;
	}

	private static void AddStation(HashSet<int> set, int tile)
	{
		if (!set.Add(tile))
			return;
		switch (tile) {
			case TileID.Hellforge:
			case TileID.GlassKiln:
				set.Add(TileID.Furnaces);
				break;
			case TileID.AdamantiteForge:
				set.Add(TileID.Furnaces);
				set.Add(TileID.Hellforge);
				break;
			case TileID.MythrilAnvil:
				set.Add(TileID.Anvils);
				break;
			case TileID.BewitchingTable:
			case TileID.Tables2:
			case TileID.PicnicTable:
				set.Add(TileID.Tables);
				break;
			case TileID.AlchemyTable:
				set.Add(TileID.Bottles);
				set.Add(TileID.Tables);
				break;
		}
		if (TileLoader.GetTile(tile) is ModTile mt)
			foreach (int a in mt.AdjTiles)
				set.Add(a);
	}

	/// <summary>Station tile (any recipe's required tile) to the items that place it.</summary>
	private static Dictionary<int, List<int>> BuildPlacers()
	{
		var required = new HashSet<int>();
		for (int i = 0; i < Recipe.numRecipes; i++)
			foreach (int t in Main.recipe[i].requiredTile)
				if (t >= 0)
					required.Add(t);
		var map = new Dictionary<int, List<int>>();
		foreach (var kv in ContentSamples.ItemsByType) {
			int ct = kv.Value.createTile;
			if (ct >= 0 && required.Contains(ct))
				(map.TryGetValue(ct, out var l) ? l : map[ct] = new List<int>()).Add(kv.Key);
		}
		return map;
	}
}

/// <summary>The item side of <see cref="ResearchFeatures"/>.</summary>
public sealed class ResearchGlobalItem : GlobalItem
{
	public override bool OnPickup(Item item, Player player)
	{
		if (ResearchFeatures.PickupTrashed(player, item.type, item.prefix)) {
			if (player.GetModPlayer<TerraBotPlayer>().Brain.Auto)
				Mod.Logger.Debug($"Journey: a picked-up {item.Name} x{item.stack} deleted (researched, nothing needs it)");
			return false;
		}
		return true;
	}

	public override bool ConsumeItem(Item item, Player player) =>
		!(ResearchFeatures.Config.InfiniteResearchedConsumables && ResearchFeatures.JourneyLocal(player) && ResearchFeatures.Researched(item.type));

	public override bool CanBeConsumedAsAmmo(Item ammo, Item weapon, Player player) =>
		!(ResearchFeatures.Config.InfiniteResearchedAmmo && ResearchFeatures.JourneyLocal(player) && ResearchFeatures.Researched(ammo.type));

	public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
	{
		if (!ResearchFeatures.Config.ShowResearchLeft || !ResearchFeatures.JourneyLocal(Main.LocalPlayer))
			return;
		int? left = CreativeUI.GetSacrificesRemaining(item.type);
		if (left is int n && n > 0)
			tooltips.Add(new TooltipLine(Mod, "ResearchLeft", Mod.GetLocalization("ResearchLeft").Format(n)));
	}
}

/// <summary>The station side of <see cref="ResearchFeatures"/>: researched stations count as in range.</summary>
public sealed class ResearchGlobalTile : GlobalTile
{
	public override int[] AdjTiles(int type) =>
		ResearchFeatures.Config.CraftFromResearchedStations && ResearchFeatures.JourneyLocal(Main.LocalPlayer) ? ResearchFeatures.ResearchedStations() : base.AdjTiles(type);
}
