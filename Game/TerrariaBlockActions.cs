using System;
using TerraBot.Core.Pathfinding;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// Mining/placing capabilities of the local player, read from the hotbar (slots 0-9 only, like a player who
/// switches with number keys). Mining time mirrors vanilla Player.PickTile/GetPickaxeDamage and
/// ItemCheck_UseMiningTools: a hit every TotalUseTime(useTime * pickSpeed) ticks, tile breaks at 100 damage.
/// </summary>
public sealed class TerrariaBlockActions : IBlockActions
{
	public const int HotbarSlots = 10;

	public int PickSlot { get; private set; } = -1;
	public int PickPower { get; private set; }
	public float HitTicks { get; private set; } = 20f;
	public int BlockSlot { get; private set; } = -1;
	public string BlockName { get; private set; } = "";
	public int BlockCount { get; private set; }
	public float PlaceTicks { get; private set; } = 15f;
	/// <summary>Player.noBuilding (e.g. some events/areas forbid building): no mining or placing at all.</summary>
	public bool NoBuilding { get; private set; }

	/// <summary>Best melee weapon in the hotbar (swing or shortsword thrust), by damage per tick.</summary>
	public int WeaponSlot { get; private set; } = -1;
	public string WeaponName { get; private set; } = "";
	/// <summary>Estimated reach in px beyond the hitbox edge (swing arc ≈ item width × scale).</summary>
	public float WeaponReach { get; private set; }
	public int WeaponDamage { get; private set; }
	/// <summary>Best arrow-firing weapon in the hotbar (by damage per tick), the arrows carried, and the launch speed
	/// (weapon shootSpeed + the ammo vanilla would pick, Player.ChooseAmmo).</summary>
	public int RangedSlot { get; private set; } = -1;
	public string RangedName { get; private set; } = "";
	public int AmmoCount { get; private set; }
	public float ShotSpeed { get; private set; }
	public int AxeSlot { get; private set; } = -1;
	public int AxePower { get; private set; }

	/// <summary>Re-reads the hotbar. Cheap (10 slots); called every tick for the local player.</summary>
	public void Refresh(Player p)
	{
		PickSlot = -1;
		PickPower = 0;
		BlockSlot = -1;
		BlockCount = 0;
		BlockName = "";
		NoBuilding = p.noBuilding;
		WeaponSlot = -1;
		WeaponName = "";
		WeaponDamage = 0;
		WeaponReach = 0f;
		AxeSlot = -1;
		AxePower = 0;
		RangedSlot = -1;
		RangedName = "";
		AmmoCount = 0;
		ShotSpeed = 0f;
		float bestRanged = 0f;
		float bestWeapon = 0f;
		int bestBlockScore = int.MinValue;
		for (int i = 0; i < HotbarSlots; i++) {
			Item it = p.inventory[i];
			if (it == null || it.IsAir)
				continue;
			if (it.pick > PickPower) {
				PickPower = it.pick;
				PickSlot = i;
			}
			if (it.axe > AxePower) {
				AxePower = it.axe;
				AxeSlot = i;
			}
			if (IsMeleeWeapon(it)) {
				// As the hotbar stocking (defense taken off): by raw damage a tick the copper shortsword beat the wooden sword, and
				// the bot fought slime crowds with it, a point or two a hit (world_eye runs 61-64).
				float score = WeaponScore(it);
				if (score > bestWeapon) {
					bestWeapon = score;
					WeaponSlot = i;
					WeaponName = it.Name;
					WeaponDamage = it.damage;
					WeaponReach = it.useStyle == ItemUseStyleID.Rapier ? 24f : Math.Max(16f, it.width * it.scale);
				}
			}
			if (RangedScore(it) > bestRanged) {
				bestRanged = RangedScore(it);
				RangedSlot = i;
				RangedName = it.Name;
			}
			if (IsThrowawayBlock(it)) {
				int score = (IsCheapBlock(it.createTile) ? 1_000_000 : 0) + it.stack;
				if (score > bestBlockScore) {
					bestBlockScore = score;
					BlockSlot = i;
				}
			}
		}
		if (RangedSlot >= 0) {
			Item bow = p.inventory[RangedSlot];
			for (int i = 0; i < 58; i++)
				if (!p.inventory[i].IsAir && p.inventory[i].ammo == AmmoID.Arrow)
					AmmoCount += p.inventory[i].stack;
			Item ammo = AmmoCount > 0 ? p.ChooseAmmo(bow) : null;
			ShotSpeed = bow.shootSpeed + (ammo?.shootSpeed ?? 0f);
		}
		if (PickSlot >= 0) {
			Item pick = p.inventory[PickSlot];
			HitTicks = Math.Max(1, CombinedHooks.TotalUseTime(pick.useTime * p.pickSpeed, p, pick));
		}
		if (BlockSlot >= 0) {
			Item b = p.inventory[BlockSlot];
			BlockCount = b.stack;
			BlockName = b.Name;
			PlaceTicks = Math.Max(1, CombinedHooks.TotalUseTime(b.useTime * p.tileSpeed, p, b));
		}
		if (NoBuilding)
			BlockCount = 0;
	}

	public const int MainInventorySlots = 50;
	/// <summary>A block stack outside the hotbar must beat the hotbar's by this much to be worth swapping in.</summary>
	public const int BlockSwapMargin = 50;

	private static float PickScore(Item it) => it.pick;
	private static float AxeScore(Item it) => it.axe;
	/// <summary>
	/// Damage per second against a typical early enemy: vanilla takes half the defense off each hit (about 8 for
	/// slimes, zombies, skeletons). By raw damage per tick the copper shortsword (5 in 12 ticks) beat the wooden sword
	/// (7 in 25), but against a yellow slime it did a point or two a hit; the bot swung for minutes and died.
	/// </summary>
	private static float WeaponScore(Item it) => IsMeleeWeapon(it) ? Math.Max(1f, it.damage - TypicalDefense / 2f) * 60f / Math.Max(1, it.useAnimation) : 0f;
	private const int TypicalDefense = 8;
	private static float RangedScore(Item it) => IsArrowWeapon(it) ? it.damage * 60f / Math.Max(1, it.useAnimation) : 0f;
	private static float BlockScore(Item it) => IsThrowawayBlock(it) ? (IsCheapBlock(it.createTile) ? 1_000_000 : 0) + it.stack : 0f;

	private static readonly (string what, Func<Item, float> score, float margin)[] Kinds = {
		("pickaxe", PickScore, 0f), ("axe", AxeScore, 0f), ("weapon", WeaponScore, 0f), ("bow", RangedScore, 0f), ("blocks", BlockScore, BlockSwapMargin),
	};

	/// <summary>
	/// Hotbar housekeeping, like a player dragging a better tool onto the hotbar: if the best pickaxe, axe, melee
	/// weapon or block stack in the main inventory (slots 11-50) beats the hotbar's best of that kind, swap it
	/// into the hotbar. One swap per call; only between swings and with an empty cursor. Returns a log line or null.
	/// </summary>
	public string StockHotbar(Player p)
	{
		if (p.itemAnimation != 0 || !Main.mouseItem.IsAir)
			return null;
		foreach (var (what, score, margin) in Kinds) {
			int hotbarBest = BestSlot(p, score, 0, HotbarSlots);
			int invBest = BestSlot(p, score, HotbarSlots, MainInventorySlots);
			if (invBest < 0)
				continue;
			float have = hotbarBest >= 0 ? score(p.inventory[hotbarBest]) : 0f;
			if (score(p.inventory[invBest]) <= have + margin)
				continue;
			int to = HotbarSlotFor(p, superseded: hotbarBest);
			if (to < 0)
				return null;
			string moved = p.inventory[invBest].Name, swappedOut = p.inventory[to].IsAir ? "" : $" (swapped out {p.inventory[to].Name})";
			Utils.Swap(ref p.inventory[invBest], ref p.inventory[to]);
			Refresh(p);
			return $"Hotbar: moved {what} {moved} from inventory slot {invBest + 1} to hotbar slot {to + 1}{swappedOut}";
		}
		return null;
	}

	/// <summary>
	/// Hotbar slot to put an item from the inventory into: an empty one, else the item it supersedes, else the
	/// right-most slot that holds none of the tools the bot uses (best pickaxe, axe, weapon, block stack).
	/// </summary>
	public int HotbarSlotFor(Player p, int superseded = -1)
	{
		for (int i = 0; i < HotbarSlots; i++)
			if (p.inventory[i].IsAir)
				return i;
		if (superseded >= 0)
			return superseded;
		for (int i = HotbarSlots - 1; i >= 0; i--)
			if (i != PickSlot && i != AxeSlot && i != WeaponSlot && i != RangedSlot && i != BlockSlot && i != p.selectedItem)
				return i;
		return -1;
	}

	private static int BestSlot(Player p, Func<Item, float> score, int from, int to)
	{
		int best = -1;
		float bestScore = 0f;
		for (int i = from; i < to; i++) {
			Item it = p.inventory[i];
			if (it == null || it.IsAir)
				continue;
			float s = score(it);
			if (s > bestScore) {
				bestScore = s;
				best = i;
			}
		}
		return best;
	}

	/// <summary>Bows and repeaters: fire arrows from the ammo carried.</summary>
	public static bool IsArrowWeapon(Item it) => it.useAmmo == AmmoID.Arrow && it.damage > 0 && !it.consumable && it.ammo == AmmoID.None;

	/// <summary>Swords and shortswords: true melee damage, no ammo/mana, not a tool or a thrown consumable.</summary>
	public static bool IsMeleeWeapon(Item it) =>
		it.damage > 0 && it.pick == 0 && it.axe == 0 && it.hammer == 0 && !it.consumable && it.ammo == 0
		&& it.useAmmo == 0 && it.mana == 0 && !it.channel && it.CountsAsClass(DamageClass.Melee)
		&& (it.useStyle == ItemUseStyleID.Swing || it.useStyle == ItemUseStyleID.Rapier);

	public string Describe() =>
		(WeaponSlot >= 0 ? $"weapon {WeaponName} (slot {WeaponSlot + 1}) | " : "no weapon | ")
		+ (PickSlot >= 0 ? $"pick {PickPower} (slot {PickSlot + 1}, {HitTicks:0}t/hit)" : "no pickaxe in hotbar")
		+ " | " + (BlockSlot >= 0 ? $"{BlockCount} {BlockName} (slot {BlockSlot + 1})" : "no blocks in hotbar")
		+ (NoBuilding ? " | NO BUILDING here" : "");

	public float BreakTicks(int x, int y)
	{
		if (PickSlot < 0 || NoBuilding || !WorldGen.InWorld(x, y, 2))
			return float.PositiveInfinity;
		Tile t = Main.tile[x, y];
		if (!t.HasTile)
			return 0f;
		int type = t.TileType;
		// A minecart track: passable, but nothing goes into its cell; a pickaxe takes it up in a hit (it drops as an item).
		if (type == TileID.MinecartTrack)
			return 1f;
		// Furniture, doors, chests, trees, altars: never part of a path.
		if (Main.tileFrameImportant[type] || Main.tileContainer[type] || Main.tileAxe[type] || Main.tileHammer[type])
			return float.PositiveInfinity;
		// Explosives go off when mined: 542 damage, dead at once (world_eye run 49).
		if (type == TileID.Explosives)
			return float.PositiveInfinity;
		// Never undermine furniture: a work bench, furnace, anvil, chair or door standing on this tile would break
		// and drop as an item (seen in game: the bot mined out the floor under its own bench and anvil).
		// Not a plant (grass, flowers, mushrooms: cut by anything) or rubble (piles, stalactites: vanilla breaks them when
		// placing over them): seen in game (measured run S10), grass under a pile or a flower counted as unbreakable, and
		// ruled out 113 of 161 arena sites round the spawn. Nor a torch (it drops as an item): measured run S12, the bot's own
		// torches on the grass ruled out 43.
		Tile above = Main.tile[x, y - 1];
		if (above.HasTile && Main.tileFrameImportant[above.TileType] && !TileID.Sets.IsATreeTrunk[above.TileType] && !Main.tileCut[above.TileType]
			&& !TileID.Sets.BreakableWhenPlacing[above.TileType] && above.TileType != TileID.Torches)
			return float.PositiveInfinity;
		if (!WorldGen.CanKillTile(x, y))
			return float.PositiveInfinity;
		int damage = PickDamage(t, x, y, PickPower);
		if (damage <= 0)
			return float.PositiveInfinity;
		int hits = (100 + damage - 1) / damage;
		return (hits - 1) * HitTicks + 1f; // the first hit lands on the first tick of the swing
	}

	public bool CanPlaceAt(int x, int y)
	{
		if (BlockSlot < 0 || NoBuilding || !WorldGen.InWorld(x, y, 2))
			return false;
		Tile t = Main.tile[x, y];
		if (t.HasTile && !Main.tileCut[t.TileType] && !TileID.Sets.BreakableWhenPlacing[t.TileType])
			return false;
		return !(t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava);
	}

	/// <summary>Blocks the bot may spend: plain solid blocks, not ores/gems, not sand (falls), not seeds.</summary>
	public static bool IsThrowawayBlock(Item it)
	{
		int ct = it.createTile;
		if (ct < 0 || !it.consumable || it.stack <= 0)
			return false;
		if (!Main.tileSolid[ct] || Main.tileSolidTop[ct] || Main.tileFrameImportant[ct])
			return false;
		if (TileID.Sets.Falling[ct] || Main.tileSpelunker[ct] || TileID.Sets.Ore[ct] || Main.tileMoss[ct])
			return false;
		return ct is not (TileID.Grass or TileID.CorruptGrass or TileID.JungleGrass or TileID.MushroomGrass
			or TileID.HallowedGrass or TileID.CrimsonGrass or TileID.AshGrass or TileID.CorruptJungleGrass
			or TileID.CrimsonJungleGrass);
	}

	private static bool IsCheapBlock(int ct) =>
		ct is TileID.Dirt or TileID.Stone or TileID.Mud or TileID.Ash or TileID.ClayBlock or TileID.SnowBlock;

	/// <summary>Mirror of the private Player.GetPickaxeDamage plus PickTile's getGoodWorld doubling.</summary>
	public static int PickDamage(Tile tile, int x, int y, int pickPower)
	{
		int type = tile.TileType;
		int num = Main.tileNoFail[type] ? 100 : 0;
		ModTile modTile = TileLoader.GetTile(type);
		if (modTile != null)
			num += (int)(pickPower / modTile.MineResist);
		else if (Main.tileDungeon[type] || type == TileID.Ebonstone || type == TileID.Hellstone || type == TileID.Pearlstone || type == TileID.Crimstone)
			num += pickPower / 2;
		else if (type == TileID.Tombstones)
			num += Main.getGoodWorld ? pickPower / 4 : pickPower / 3;
		else if (type == TileID.Spikes || type == TileID.WoodenSpikes)
			num += pickPower * 2;
		else if (type == TileID.LihzahrdBrick)
			num += pickPower / 4;
		else if (type == TileID.Cobalt || type == TileID.Palladium)
			num += pickPower / 2;
		else if (type == TileID.Mythril || type == TileID.Orichalcum)
			num += pickPower / 3;
		else if (type == TileID.Adamantite || type == TileID.Titanium)
			num += pickPower / 4;
		else if (type == TileID.Chlorophyte)
			num += pickPower / 5;
		else
			num += pickPower;

		if (type == TileID.Chlorophyte && pickPower < 200)
			num = 0;
		if ((type == TileID.Ebonstone || type == TileID.Crimstone) && pickPower < 65)
			num = 0;
		else if (type == TileID.Pearlstone && pickPower < 65)
			num = 0;
		else if (type == TileID.Meteorite && pickPower < 50)
			num = 0;
		else if ((type == TileID.Demonite || type == TileID.Crimtane) && y > Main.worldSurface && pickPower < 55)
			num = 0;
		else if (type == TileID.Obsidian && pickPower < 55)
			num = 0;
		else if (type == TileID.Hellforge && pickPower < 65 && y >= Main.UnderworldLayer)
			num = 0;
		else if (type == TileID.Hellstone && pickPower < 65)
			num = 0;
		else if ((type == TileID.LihzahrdBrick || type == TileID.LihzahrdAltar) && pickPower < 210)
			num = 0;
		else if (type == TileID.Traps && pickPower < 210)
			num = 0; // vanilla only protects some frames; stay away from all traps
		else if (Main.tileDungeon[type] && pickPower < 100 && y > Main.worldSurface) {
			if (x < Main.maxTilesX * 0.35 || x > Main.maxTilesX * 0.65)
				num = 0;
		}
		else if (type == TileID.Cobalt && pickPower < 100)
			num = 0;
		else if (type == TileID.Mythril && pickPower < 110)
			num = 0;
		else if (type == TileID.Adamantite && pickPower < 150)
			num = 0;
		else if (type == TileID.Palladium && pickPower < 100)
			num = 0;
		else if (type == TileID.Orichalcum && pickPower < 110)
			num = 0;
		else if (type == TileID.Titanium && pickPower < 150)
			num = 0;
		else
			TileLoader.PickPowerCheck(tile, pickPower, ref num);

		if (type == TileID.SnowBlock || type == TileID.Dirt || type == TileID.ClayBlock || type == TileID.Sand || type == TileID.Ash
			|| type == TileID.Mud || type == TileID.Silt || type == TileID.Slush || type == TileID.HardenedSand)
			num += pickPower;
		if (type == TileID.DesertFossil)
			num += 5;
		if (type == TileID.Stalactite || Main.tileRope[type] || type == TileID.CrimsonGrass)
			num = 100;
		if (Main.getGoodWorld)
			num *= 2;
		return num;
	}
}
