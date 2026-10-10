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
/// unbreakable for pre-hardmode tools), '%' sand, 's' stone, 'i' / 'c' this world's iron / copper-tier ore, ',' grass, 'T' tree base, '=' wood platform, '!' lava, '~' water,
/// 'S' start node (left body column, feet row), 'G' target tile.
/// </summary>
public sealed class Arena
{
	public const int Width = 120;
	public const int Height = 50;
	public const int GridOffsetX = 20;

	/// <summary>Top-left tile of the cleared region and the row the grids stand on (grid bottom row).</summary>
	public int Left { get; }
	public int Top { get; }
	public int FloorY { get; }

	public Arena(int spawnX, int spawnY)
	{
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
		for (int gy = 0; gy < rows.Length; gy++) {
			for (int gx = 0; gx < rows[gy].Length; gx++) {
				TilePos w = ToWorld(gx, gy, rows.Length);
				Tile t = Main.tile[w.X, w.Y];
				switch (rows[gy][gx]) {
					case '#': t.ResetToType(TileID.Dirt); break;
					case ',': t.ResetToType(TileID.Grass); break;
					case '@': t.ResetToType(TileID.LihzahrdBrick); break;
					case '%': t.ResetToType(TileID.Sand); break;
					case 's': t.ResetToType(TileID.Stone); break;
					case 'i': t.ResetToType((ushort)WorldGen.SavedOreTiers.Iron); break;
					case 'c': t.ResetToType((ushort)WorldGen.SavedOreTiers.Copper); break;
					case '=': t.ResetToType(TileID.Platforms); break;
					case '!': t.LiquidType = LiquidID.Lava; t.LiquidAmount = 255; break;
					case '~': t.LiquidType = LiquidID.Water; t.LiquidAmount = 255; break;
				}
			}
		}
		WorldGen.RangeFrame(Left - 1, Top - 1, Left + Width + 1, FloorY + 5);
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
		p.inventory[0] = pickaxe ? new Item(ItemID.IronPickaxe) : new Item();
		p.inventory[1] = blocks > 0 ? new Item(ItemID.DirtBlock, blocks) : new Item();
		p.inventory[2] = weapon > 0 ? new Item(weapon) : new Item();
		p.inventory[3] = axe > 0 ? new Item(axe) : new Item();
		p.selectedItem = 0;
	}
}
