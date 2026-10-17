using System;
using Microsoft.Xna.Framework;
using TerraBot.Core.Grid;
using Terraria;
using Terraria.ID;

namespace TerraBot.Game.SelfTest;

/// <summary>
/// TEST FIXTURE ONLY. Builds self-test scenarios straight into the world (tile edits, player placement), the way
/// a unit test builds an ArrayTileGrid. The bot itself never uses any of this; it only ever presses controls.
/// Only reachable when tModLoader was launched with -terrabotselftest (tools/selftest.ps1, isolated save dir).
///
/// Legend (same as the unit tests): '.' air, '#' dirt (breakable), '@' Lihzahrd brick (needs pick power 210, so
/// unbreakable for pre-hardmode tools), '%' sand, 's' stone, 'i' / 'c' this world's iron / copper-tier ore, ',' grass, 'T' tree base, 'H' life crystal (right column of a 2×2 pocket), 'A' demon altar (bottom-middle), '=' wood platform, '!' lava, '~' water, 'h' dirt half block, 'r' minecart track, 'w' open with an ebonstone wall behind, 'e' ebonstone, 'C' crimstone,
/// 'S' start node (left body column, feet row), 'G' target tile.
/// </summary>
public sealed class Arena
{
	/// <summary>Columns cleared for the grids: at least 120, more for wide grids (a grid starts <see cref="GridOffsetX"/> in).</summary>
	public int Width { get; }
	public const int MinWidth = 120;
	/// <summary>Rows cleared above the floor row: 50, or a scenario's own (the Underworld's, docs M12_SPEC.md section 10).</summary>
	public int Height { get; }
	public const int DefaultHeight = 50;
	public const int GridOffsetX = 20;

	/// <summary>Top-left tile of the cleared region and the row the grids stand on (grid bottom row).</summary>
	public int Left { get; }
	public int Top { get; }
	public int FloorY { get; }

	public Arena(int spawnX, int spawnY, int widestGrid = 0, int height = DefaultHeight)
	{
		Height = Math.Max(DefaultHeight, height);
		Width = Math.Max(MinWidth, widestGrid + 2 * GridOffsetX);
		Left = spawnX - Width / 2;
		FloorY = spawnY;
		Top = FloorY - Height + 1;
	}

	public int GridLeft => Left + GridOffsetX;

	/// <summary>World tile of grid cell (gx, gy) for a grid with <paramref name="rows"/> rows (bottom row on FloorY).</summary>
	public TilePos ToWorld(int gx, int gy, int rows) => new(GridLeft + gx, FloorY - (rows - 1) + gy);

	/// <summary>Clears the whole arena to air (no walls, no liquid) on an unbreakable base, then draws the grid.</summary>
	public void Build(string[] rows)
	{
		for (int x = Left; x < Left + Width; x++) {
			for (int y = Top; y <= FloorY + 4; y++) {
				Tile t = Main.tile[x, y];
				t.ClearEverything();
				if (y > FloorY)
					t.ResetToType(TileID.LihzahrdBrick);
			}
		}
		// Unbreakable side walls: the bot must not wander out into the real world (it did, exploring for trees and
		// ore, which made runs depend on the terrain around the spawn).
		for (int y = Top; y <= FloorY + 4; y++) {
			Main.tile[Left, y].ResetToType(TileID.LihzahrdBrick);
			Main.tile[Left + Width - 1, y].ResetToType(TileID.LihzahrdBrick);
		}
		for (int gy = 0; gy < rows.Length; gy++) {
			for (int gx = 0; gx < rows[gy].Length; gx++) {
				TilePos w = ToWorld(gx, gy, rows.Length);
				Tile t = Main.tile[w.X, w.Y];
				switch (rows[gy][gx]) {
					case '#': t.ResetToType(TileID.Dirt); break;
					case ',': t.ResetToType(TileID.Grass); break;
					case '@': t.ResetToType(TileID.LihzahrdBrick); break;
					case 'e': t.ResetToType(TileID.Ebonstone); break; // pickaxe power 65 to mine; a blast breaks it
					case 'C': t.ResetToType(TileID.Crimstone); break; // the Crimson zone's stone (TileID.Sets.CrimsonBiome 1): fight_boc's floor
					case '%': t.ResetToType(TileID.Sand); break;
					case 's': t.ResetToType(TileID.Stone); break;
					case 'i': t.ResetToType((ushort)WorldGen.SavedOreTiers.Iron); break;
					case 'c': t.ResetToType((ushort)WorldGen.SavedOreTiers.Copper); break;
					case '=': t.ResetToType(TileID.Platforms); break;
					case '|': t.ResetToType(TileID.Rope); break;
					case '!': t.LiquidType = LiquidID.Lava; t.LiquidAmount = 255; break;
					case '~': t.LiquidType = LiquidID.Water; t.LiquidAmount = 255; break;
					case 'h': t.ResetToType(TileID.Dirt); t.IsHalfBlock = true; break;
					case 'r': Minecart.PlaceTrack(t, 0); break;
					case 'w': t.WallType = WallID.EbonstoneUnsafe; break; // open, an ebonstone wall behind (a corruption chasm's)
				}
			}
		}
		WorldGen.RangeFrame(Left - 1, Top - 1, Left + Width + 1, FloorY + 5);
		// The bot only knows tiles revealed on the map: a built arena counts as seen.
		for (int x = Left; x < Left + Width; x++)
			for (int y = Top; y <= FloorY + 4; y++)
				Main.Map.Update(x, y, 255);
		Main.refreshMap = true;
		// Demon altars: 'A' is the bottom-middle cell of a 3×2 altar standing on the floor below (WorldGen.Place3x2).
		foreach (TilePos m in FindAll(rows, 'A')) {
			TilePos w = ToWorld(m.X, m.Y, rows.Length);
			WorldGen.Place3x2(w.X, w.Y, TileID.DemonAltar);
			if (!Main.tile[w.X, w.Y].HasTile || Main.tile[w.X, w.Y].TileType != TileID.DemonAltar)
				Terraria.ModLoader.ModContent.GetInstance<TerraBotMod>().Logger.Warn($"SELFTEST could not place a demon altar at {w}");
		}
		// Life crystals: 'H' marks the right column of a 2×2 air pocket over solid floor (WorldGen.AddLifeCrystal scans
		// down from it to the floor and places the heart in the two columns ending there).
		foreach (TilePos m in FindAll(rows, 'H')) {
			TilePos w = ToWorld(m.X, m.Y, rows.Length);
			if (!WorldGen.AddLifeCrystal(w.X, w.Y))
				Terraria.ModLoader.ModContent.GetInstance<TerraBotMod>().Logger.Warn($"SELFTEST could not place a life crystal at {w}");
		}
		// Trees: 'T' marks the base cell; vanilla GrowTree wants grass ground with grass beside it and room above.
		foreach (TilePos m in FindAll(rows, 'T')) {
			TilePos w = ToWorld(m.X, m.Y, rows.Length);
			for (int attempt = 0; attempt < 20 && !WorldGen.GrowTree(w.X, w.Y + 1); attempt++) { }
		}
	}

	public static System.Collections.Generic.IEnumerable<TilePos> FindAll(string[] rows, char marker)
	{
		for (int gy = 0; gy < rows.Length; gy++)
			for (int gx = 0; gx < rows[gy].Length; gx++)
				if (rows[gy][gx] == marker)
					yield return new TilePos(gx, gy);
	}

	public static TilePos Find(string[] rows, char marker)
	{
		for (int gy = 0; gy < rows.Length; gy++) {
			int gx = rows[gy].IndexOf(marker);
			if (gx >= 0)
				return new TilePos(gx, gy);
		}
		throw new ArgumentException($"marker '{marker}' not in grid");
	}

	/// <summary>Solid-cell snapshot of the grid area, to count blocks broken/placed during a run.</summary>
	public bool[,] SnapshotSolid(string[] rows, int width)
	{
		var snap = new bool[width, rows.Length];
		for (int gy = 0; gy < rows.Length; gy++)
			for (int gx = 0; gx < width; gx++) {
				TilePos w = ToWorld(gx, gy, rows.Length);
				snap[gx, gy] = TerrariaTileGrid.Instance.Get(w.X, w.Y).IsSolid();
			}
		return snap;
	}

	/// <summary>Puts the player standing centred on world node <paramref name="node"/>, at rest.</summary>
	public static void PlacePlayer(Player p, TilePos node)
	{
		p.position = new Vector2(node.X * 16 + (32 - p.width) / 2f, (node.Y + 1) * 16 - p.height);
		p.velocity = Vector2.Zero;
		p.fallStart = (int)(p.position.Y / 16f);
		p.statLife = p.statLifeMax2;
		p.immune = true;
		p.immuneTime = 30;
	}

	/// <summary>No minions, pets, sentries or buffs from the copied character: they would fight or move for the bot.</summary>
	public static void StripHelpers(Player p)
	{
		for (int i = 0; i < Main.maxProjectiles; i++)
			if (Main.projectile[i].active && Main.projectile[i].owner == p.whoAmI)
				Main.projectile[i].Kill();
		for (int b = Player.MaxBuffs - 1; b >= 0; b--)
			if (p.buffType[b] > 0)
				p.DelBuff(b);
	}

	/// <summary>Hotbar for a scenario: optional iron pickaxe (slot 1) and a dirt stack (slot 2); slots 3-10 emptied.</summary>
	public static void SetHotbar(Player p, bool pickaxe, int blocks, int weapon = 0, int axe = 0)
	{
		// The whole main inventory is cleared so counts (wood, benches...) start from zero.
		for (int i = 0; i < 50; i++)
			p.inventory[i] = new Item();
		for (int i = 0; i < 3; i++)
			p.armor[i] = new Item(); // worn armour (head, body, legs): the copied character's own set would satisfy armour goals
		p.inventory[0] = pickaxe ? new Item(ItemID.IronPickaxe) : new Item();
		p.inventory[1] = blocks > 0 ? new Item(ItemID.DirtBlock, blocks) : new Item();
		p.inventory[2] = weapon > 0 ? new Item(weapon) : new Item();
		p.inventory[3] = axe > 0 ? new Item(axe) : new Item();
		p.selectedItem = 0;
	}
}
