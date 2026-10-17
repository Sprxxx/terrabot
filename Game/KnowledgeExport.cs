using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// Writes the game's own data as tab-separated tables for the local knowledge base (Desktop\Projekt\terrabot\docs\knowledge\game):
/// items with their stats and tooltips, recipes, NPCs, drop rates (the bestiary's ItemDropDatabase), NPC shops (NPCShopDatabase),
/// buffs, projectiles, tiles and the armour set bonuses' texts. The game is the authority for all of it, so a question these
/// tables answer needs no wiki research. Run by <c>selftest.ps1 -Export &lt;dir&gt;</c> (launch flag <c>-terrabotexport</c>): the
/// game loads a world, writes the tables and exits. Text fields have tabs and line breaks replaced (" / " between lines).
/// </summary>
public static class KnowledgeExport
{
	public const string Flag = "-terrabotexport";

	public static void WriteAll(string dir, log4net.ILog log)
	{
		Directory.CreateDirectory(dir);
		var counts = new List<string>();
		void Table(string file, string header, IEnumerable<string> rows)
		{
			var sb = new StringBuilder();
			sb.Append(header).Append('\n');
			int n = 0;
			foreach (string row in rows) {
				sb.Append(row).Append('\n');
				n++;
			}
			File.WriteAllText(Path.Combine(dir, file), sb.ToString(), new UTF8Encoding(false));
			counts.Add($"{file}\t{n}");
			log.Info($"Knowledge export: {file}, {n} rows");
		}
		Table("items.tsv", Join("id", "internal", "name", "damage", "class", "use_time", "use_animation", "knockback", "crit", "auto_reuse", "mana",
			"shoot", "shoot_speed", "use_ammo", "ammo", "defense", "head", "body", "legs", "accessory", "vanity", "pick", "axe", "hammer",
			"heal_life", "heal_mana", "potion", "buff", "buff_seconds", "consumable", "max_stack", "value_copper", "rarity", "create_tile",
			"place_style", "tooltip"), Items());
		Table("recipes.tsv", Join("result_id", "result", "stack", "ingredients", "stations", "groups", "conditions"), Recipes());
		Table("npcs.tsv", Join("net_id", "internal", "name", "life", "damage", "defense", "knockback_resist", "ai_style", "boss", "town", "friendly",
			"value_copper", "width", "height", "no_gravity", "no_tile_collide", "lava_immune"), Npcs());
		Table("drops.tsv", Join("source_kind", "source_id", "source", "item_id", "item", "stack_min", "stack_max", "rate", "one_in", "conditions"), Drops());
		Table("shops.tsv", Join("shop", "npc_id", "npc", "item_id", "item", "price_copper", "conditions"), Shops());
		Table("buffs.tsv", Join("id", "internal", "name", "debuff", "pet", "description"), Buffs());
		Table("projectiles.tsv", Join("id", "internal", "name", "ai_style", "friendly", "hostile", "minion", "minion_slots", "sentry", "class",
			"penetrate", "time_left", "tile_collide", "width", "height"), Projectiles());
		Table("tiles.tsv", Join("id", "internal", "solid", "solid_top", "frame_important", "light", "spelunker", "drop_item_id_style0", "drop_item_style0", "placed_by"), Tiles());
		Table("set_bonuses.tsv", Join("key", "text"), SetBonuses());
		File.WriteAllText(Path.Combine(dir, "meta.txt"),
			$"tModLoader {BuildInfo.versionedName}; Terraria {Main.versionNumber}; exported {DateTime.Now:yyyy-MM-dd HH:mm}\n" + string.Join("\n", counts) + "\n",
			new UTF8Encoding(false));
	}

	private static string Join(params object[] fields) => string.Join("\t", fields.Select(Clean));

	private static string Clean(object o) => o switch {
		null => "",
		bool b => b ? "1" : "0",
		float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
		double d => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
		_ => (o.ToString() ?? "").Replace("\t", " ").Replace("\r\n", " / ").Replace("\n", " / ").Replace("\r", " / "),
	};

	private static string ItemName(int type) => type <= 0 ? "" : Lang.GetItemNameValue(type);
	private static string ItemInternal(int type) => ItemID.Search.TryGetName(type, out string n) ? n : type.ToString();
	private static string TileInternal(int type) => type < 0 ? "" : TileID.Search.TryGetName(type, out string n) ? n : type.ToString();
	private static string ProjectileInternal(int type) => type <= 0 ? "" : ProjectileID.Search.TryGetName(type, out string n) ? n : type.ToString();
	private static string BuffInternal(int type) => type <= 0 ? "" : BuffID.Search.TryGetName(type, out string n) ? n : type.ToString();
	private static string ClassName(DamageClass c) => c == null ? "" : c.Name.EndsWith("DamageClass") ? c.Name[..^"DamageClass".Length] : c.Name;

	private static IEnumerable<string> Items()
	{
		for (int type = 1; type < ItemLoader.ItemCount; type++) {
			if (!ContentSamples.ItemsByType.TryGetValue(type, out Item it))
				continue;
			var tip = Lang.GetTooltip(type);
			var lines = new List<string>();
			for (int i = 0; tip != null && i < tip.Lines; i++)
				lines.Add(tip.GetLine(i));
			yield return Join(type, ItemInternal(type), ItemName(type), it.damage, ClassName(it.DamageType), it.useTime, it.useAnimation, it.knockBack, it.crit,
				it.autoReuse, it.mana, ProjectileInternal(it.shoot), it.shootSpeed, it.useAmmo > 0 ? ItemInternal(it.useAmmo) : "", it.ammo > 0 ? ItemInternal(it.ammo) : "",
				it.defense, it.headSlot >= 0, it.bodySlot >= 0, it.legSlot >= 0, it.accessory, it.vanity, it.pick, it.axe, it.hammer, it.healLife, it.healMana,
				it.potion, BuffInternal(it.buffType), it.buffTime / 60f, it.consumable, it.maxStack, it.value, it.rare, it.createTile >= TileID.Dirt ? TileInternal(it.createTile) : "",
				it.createTile >= TileID.Dirt ? it.placeStyle : "", string.Join(" / ", lines));
		}
	}

	private static IEnumerable<string> Recipes()
	{
		for (int i = 0; i < Recipe.numRecipes; i++) {
			Recipe r = Main.recipe[i];
			if (r == null || r.Disabled || r.createItem == null || r.createItem.type <= ItemID.None)
				continue;
			string ingredients = string.Join("; ", r.requiredItem.Where(x => x.type > ItemID.None).Select(x => $"{ItemName(x.type)} x{x.stack}"));
			string stations = string.Join("; ", r.requiredTile.Where(t => t >= 0).Select(TileInternal));
			string groups = string.Join("; ", r.acceptedGroups.Select(g => RecipeGroup.recipeGroups.TryGetValue(g, out RecipeGroup rg) ? rg.GetText?.Invoke() ?? g.ToString() : g.ToString()));
			string conditions = string.Join("; ", r.Conditions.Select(c => c.Description?.Value ?? ""));
			yield return Join(r.createItem.type, ItemName(r.createItem.type), r.createItem.stack, ingredients, stations, groups, conditions);
		}
	}

	private static IEnumerable<string> Npcs()
	{
		foreach (var kv in ContentSamples.NpcsByNetId.OrderBy(kv => kv.Key)) {
			NPC n = kv.Value;
			if (n == null || kv.Key == 0)
				continue;
			string internalName = NPCID.Search.TryGetName(kv.Key, out string s) ? s : kv.Key.ToString();
			yield return Join(kv.Key, internalName, Lang.GetNPCNameValue(kv.Key), n.lifeMax, n.damage, n.defense, n.knockBackResist, n.aiStyle, n.boss, n.townNPC,
				n.friendly, n.value, n.width, n.height, n.noGravity, n.noTileCollide, n.lavaImmune);
		}
	}

	private static IEnumerable<string> Drops()
	{
		ItemDropDatabase db = Main.ItemDropsDB;
		var globals = new HashSet<IItemDropRule>();
		foreach (var kv in ContentSamples.NpcsByNetId.OrderBy(kv => kv.Key)) {
			if (kv.Key == 0)
				continue;
			List<IItemDropRule> local = db.GetRulesForNPCID(kv.Key, includeGlobalDrops: false);
			foreach (IItemDropRule g in db.GetRulesForNPCID(kv.Key, includeGlobalDrops: true).Except(local))
				globals.Add(g);
			foreach (string row in Report("npc", kv.Key, Lang.GetNPCNameValue(kv.Key), local))
				yield return row;
		}
		foreach (string row in Report("global (any NPC)", 0, "", globals.ToList()))
			yield return row;
		for (int type = 1; type < ItemLoader.ItemCount; type++) {
			List<IItemDropRule> rules = db.GetRulesForItemID(type);
			if (rules.Count > 0)
				foreach (string row in Report("item (opened)", type, ItemName(type), rules))
					yield return row;
		}
	}

	private static IEnumerable<string> Report(string kind, int id, string name, List<IItemDropRule> rules)
	{
		var infos = new List<DropRateInfo>();
		foreach (IItemDropRule rule in rules)
			rule.ReportDroprates(infos, new DropRateInfoChainFeed(1f));
		foreach (DropRateInfo d in infos) {
			string conditions = d.conditions == null ? "" : string.Join("; ", d.conditions.Select(c => c.GetConditionDescription() ?? c.GetType().Name));
			// The rate with all its digits (a Slime Staff is 1 in 10,000) and as "one in N".
			string rate = ((double)d.dropRate).ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
			string oneIn = d.dropRate > 0f ? Math.Round(1.0 / d.dropRate, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
			yield return Join(kind, id, name, d.itemId, ItemName(d.itemId), d.stackMin, d.stackMax, rate, oneIn, conditions);
		}
	}

	private static IEnumerable<string> Shops()
	{
		foreach (AbstractNPCShop shop in NPCShopDatabase.AllShops.OrderBy(s => s.FullName)) {
			IEnumerable<AbstractNPCShop.Entry> entries;
			try {
				entries = shop.ActiveEntries.ToList();
			}
			catch (Exception) {
				continue; // a shop that lists nothing outside a world (the Travelling Merchant's is rolled each visit)
			}
			foreach (AbstractNPCShop.Entry e in entries) {
				if (e.Item == null || e.Item.type <= ItemID.None)
					continue;
				yield return Join(shop.FullName, shop.NpcType, Lang.GetNPCNameValue(shop.NpcType), e.Item.type, ItemName(e.Item.type), e.Item.shopCustomPrice ?? e.Item.value,
					string.Join("; ", e.Conditions.Select(c => c.Description?.Value ?? "")));
			}
		}
	}

	private static IEnumerable<string> Buffs()
	{
		for (int id = 1; id < BuffLoader.BuffCount; id++)
			yield return Join(id, BuffInternal(id), Lang.GetBuffName(id), Main.debuff[id], Main.vanityPet[id] || Main.lightPet[id], Lang.GetBuffDescription(id));
	}

	private static IEnumerable<string> Projectiles()
	{
		for (int id = 1; id < ProjectileLoader.ProjectileCount; id++) {
			if (!ContentSamples.ProjectilesByType.TryGetValue(id, out Projectile p))
				continue;
			yield return Join(id, ProjectileInternal(id), Lang.GetProjectileName(id).Value, p.aiStyle, p.friendly, p.hostile, p.minion, p.minionSlots, p.sentry,
				ClassName(p.DamageType), p.penetrate, p.timeLeft, p.tileCollide, p.width, p.height);
		}
	}

	private static IEnumerable<string> Tiles()
	{
		var placedBy = new Dictionary<int, List<string>>();
		for (int type = 1; type < ItemLoader.ItemCount; type++)
			if (ContentSamples.ItemsByType.TryGetValue(type, out Item it) && it.createTile >= TileID.Dirt) {
				if (!placedBy.TryGetValue(it.createTile, out List<string> l))
					placedBy[it.createTile] = l = new List<string>();
				if (l.Count < 8)
					l.Add(ItemName(type));
			}
		for (int t = 0; t < TileLoader.TileCount; t++) {
			int drop = 0;
			try {
				drop = TileLoader.GetItemDropFromTypeAndStyle(t);
			}
			catch (Exception) {
				// a tile whose drop needs a placed instance: left blank
			}
			yield return Join(t, TileInternal(t), Main.tileSolid[t], Main.tileSolidTop[t], Main.tileFrameImportant[t], Main.tileLighted[t], Main.tileSpelunker[t],
				drop > 0 ? drop : "", ItemName(drop), placedBy.TryGetValue(t, out List<string> by) ? string.Join("; ", by) : "");
		}
	}

	private static IEnumerable<string> SetBonuses() =>
		Language.FindAll(Lang.CreateDialogFilter("ArmorSetBonus.")).OrderBy(t => t.Key).Select(t => Join(t.Key, t.Value));
}
