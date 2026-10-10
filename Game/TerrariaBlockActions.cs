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

	/// <summary>Re-reads the hotbar. Cheap (10 slots); called every tick for the local player.</summary>
	public void Refresh(Player p)
	{
		PickSlot = -1;
		PickPower = 0;
		BlockSlot = -1;
		BlockCount = 0;
		BlockName = "";
		NoBuilding = p.noBuilding;
		int bestBlockScore = int.MinValue;
		for (int i = 0; i < HotbarSlots; i++) {
			Item it = p.inventory[i];
			if (it == null || it.IsAir)
				continue;
			if (it.pick > PickPower) {
				PickPower = it.pick;
				PickSlot = i;
			}
			if (IsThrowawayBlock(it)) {
				int score = (IsCheapBlock(it.createTile) ? 1_000_000 : 0) + it.stack;
				if (score > bestBlockScore) {
					bestBlockScore = score;
					BlockSlot = i;
				}
			}
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

	public string Describe() =>
		(PickSlot >= 0 ? $"pick {PickPower} (slot {PickSlot + 1}, {HitTicks:0}t/hit)" : "no pickaxe in hotbar")
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
		// Furniture, doors, chests, trees, altars: never part of a path.
		if (Main.tileFrameImportant[type] || Main.tileContainer[type] || Main.tileAxe[type] || Main.tileHammer[type])
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
