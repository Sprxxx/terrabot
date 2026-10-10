using System;
using System.Collections.Generic;
using System.Linq;

namespace TerraBot.Game.SelfTest;

/// <summary>What happened in one scenario run, collected by <see cref="SelfTestSystem"/>.</summary>
public sealed class RunResult
{
	public bool Succeeded;
	public string Outcome = "";
	public int Ticks;
	public int Broken;
	public int Placed;
	public int MaxFeetRow; // in grid coordinates (larger = lower)
	public int FallDamageHits;
	public int FallingBlockHits;
	public int FallingBlocksSeen;
	public int EnemiesLeft;
	public int LifeLost;
	public int Wood;
	public int Swords;
	public bool BenchPlaced;
	public List<string> Log = new();

	public bool LogHas(string s) => Log.Any(l => l.Contains(s));
}

/// <summary>One in-game self-test: a grid, the bot settings, and the checks to apply afterwards.</summary>
public sealed class Scenario
{
	public string Name { get; init; }
	public string[] Rows { get; init; }
	public bool Dig { get; init; }
	public bool Build { get; init; }
	public bool Pickaxe { get; init; } = true;
	public int Blocks { get; init; }
	/// <summary>Item type for hotbar slot 3 (a weapon), 0 = none.</summary>
	public int Weapon { get; init; }
	/// <summary>Item type for hotbar slot 4 (an axe), 0 = none.</summary>
	public int Axe { get; init; }
	/// <summary>Extra inventory setup after the hotbar is set (e.g. tools stashed outside the hotbar).</summary>
	public Action<Terraria.Player> Inventory { get; init; }
	/// <summary>NPC type spawned at each 'E' marker once the scenario starts.</summary>
	public int EnemyType { get; init; }
	/// <summary>Running tick at which the 'E' enemies spawn (0 = at the start).</summary>
	public int SpawnAtTick { get; init; }
	/// <summary>"walk" (to 'G'), "fight" (FightSkill), "chop" (gather 5 wood) or "auto" (early-game brain).</summary>
	public string Mode { get; init; } = "walk";
	public bool ExpectSuccess { get; init; } = true;
	public int TimeoutTicks { get; init; } = 60 * 45;
	/// <summary>Called every running tick with the tick number (for mid-run world changes).</summary>
	public Action<Arena, int> During { get; init; }
	/// <summary>Extra checks; return null when fine, else the failure reason.</summary>
	public Func<RunResult, string> Check { get; init; }

	public int Width => Rows.Max(r => r.Length);
	public int StartFeetRow => Arena.Find(Rows, 'S').Y;

	/// <summary>Built on first use: a static initializer here would run before the grid fields below exist.</summary>
	public static IReadOnlyList<Scenario> All => all ??= CreateAll();
	private static IReadOnlyList<Scenario> all;

	private static string[] Repeat(string row, int n) => Enumerable.Repeat(row, n).ToArray();

	private static string[] Grid(params object[] parts) =>
		parts.SelectMany(p => p is string s ? new[] { s } : (string[])p).ToArray();

	private static string Set(string row, int x, char c) => row.Remove(x, 1).Insert(x, c.ToString());

	private static readonly string[] Corridor = {
		"@@@@@@@@@@@@@@@@@@@@",
		"@.........#........@",
		"@.........#........@",
		"@.S.......#......G.@",
		"@@@@@@@@@@@@@@@@@@@@",
	};

	private static readonly string[] Cliff = Grid(
		Repeat("......................", 3),
		"..............G.......",
		Repeat("..........@@@@@@@@@@@@", 8),
		"..S.......@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@");

	private static string[] WithCell(string[] rows, int x, int y, char c)
	{
		string[] copy = (string[])rows.Clone();
		copy[y] = Set(copy[y], x, c);
		return copy;
	}

	private static string Max(RunResult r, int expected, int actual, string what) =>
		actual > expected ? $"{what} {actual} > {expected}" : null;

	private static IReadOnlyList<Scenario> CreateAll()
	{
		var list = new List<Scenario> {
			new() {
				Name = "fight_green_slime",
				Rows = Grid(Repeat("..............................", 6), "..S..............E............", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "fight",
				Weapon = Terraria.ID.ItemID.CopperBroadsword,
				EnemyType = Terraria.ID.NPCID.GreenSlime,
				Check = r => r.EnemiesLeft > 0 ? $"{r.EnemiesLeft} enemy left alive" : null,
			},
			new() {
				Name = "fight_two_slimes_both_sides",
				Rows = Grid(Repeat("........................................", 6), "......E...........S..............E......", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "fight",
				Weapon = Terraria.ID.ItemID.CopperBroadsword,
				EnemyType = Terraria.ID.NPCID.BlueSlime,
				Check = r => r.EnemiesLeft > 0 ? $"{r.EnemiesLeft} enemy left alive" : null,
			},
			new() {
				Name = "chop_trees",
				Rows = Grid(Repeat("..............................................", 20),
					"..S..........T...........T....................",
					",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "chop",
				Axe = Terraria.ID.ItemID.CopperAxe,
				TimeoutTicks = 60 * 90,
				Check = r => r.Wood < 5 ? $"only {r.Wood} wood" : null,
			},
			new() {
				Name = "early_game_auto",
				Rows = Grid(Repeat("....................................................................................................", 20),
					"..S.......T........T........T........T........T........T........T........T.........................",
					",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,",
					"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "auto",
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				TimeoutTicks = 60 * 60 * 4,
				Check = r => !r.BenchPlaced ? "no work bench placed" : r.Swords < 1 ? "no wooden sword" : r.Wood < 30 ? $"only {r.Wood} wood" : null,
			},
			new() {
				// Hotbar full of gel, the axe in the backpack and no weapon: the bot has to drag the axe onto the
				// hotbar, the hand-crafted bench too (to place it), and the crafted sword (which lands in the backpack).
				Name = "early_game_full_hotbar",
				Rows = Grid(Repeat("....................................................................................................", 20),
					"..S.......T........T........T........T........T........T........T........T.........................",
					",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,",
					"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "auto",
				Pickaxe = false,
				Inventory = p => {
					for (int i = 0; i < 10; i++)
						p.inventory[i] = new Terraria.Item(Terraria.ID.ItemID.Gel, 5);
					p.inventory[27] = new Terraria.Item(Terraria.ID.ItemID.CopperAxe);
				},
				TimeoutTicks = 60 * 60 * 4,
				Check = r => !r.BenchPlaced ? "no work bench placed" : r.Swords < 1 ? "no wooden sword"
					: !r.LogHas("Hotbar: moved axe Copper Axe") ? "axe never moved to the hotbar"
					: !r.LogHas("Hotbar: moved weapon Wooden Sword") ? "sword never moved to the hotbar" : null,
			},
			new() {
				Name = "early_game_with_slimes",
				Rows = Grid(Repeat("....................................................................................................", 20),
					"..S...E...T........T.E......T........T........T........T........T........T.........................",
					",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,",
					"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "auto",
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				EnemyType = Terraria.ID.NPCID.BlueSlime,
				SpawnAtTick = 420,
				TimeoutTicks = 60 * 60 * 4,
				Check = r => !r.BenchPlaced ? "no work bench placed" : r.Swords < 1 ? "no wooden sword"
					: r.EnemiesLeft > 0 ? $"{r.EnemiesLeft} slime(s) left" : !r.LogHas("to fight") && !r.LogHas("Fight start") ? "never fought" : null,
			},
			new() {
				Name = "walk_flat",
				Rows = Grid(Repeat("..............................", 4), "..S......................G....", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
			},
			new() {
				Name = "ledge_jump3",
				Rows = Grid(Repeat("......................", 6), "..............G.......", Repeat("..........@@@@@@@@@@@@", 2),
					"..S.......@@@@@@@@@@@@", "@@@@@@@@@@@@@@@@@@@@@@"),
			},
			new() {
				Name = "gap_jump_over_lava",
				Rows = Grid(Repeat("....................", 9), "..S.............G...", "@@@@@@@....@@@@@@@@@", "@@@@@@@!!!!@@@@@@@@@",
					"@@@@@@@@@@@@@@@@@@@@"),
				Check = r => r.MaxFeetRow > 9 ? $"went down into the pit (feet row {r.MaxFeetRow})" : null,
			},
			new() {
				Name = "running_jump_8_gap",
				Rows = Grid(Repeat("..............................", 9), "..S.......................G...",
					"@@@@@@@@@@........@@@@@@@@@@@@", "@@@@@@@@@@!!!!!!!!@@@@@@@@@@@@", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Check = r => r.MaxFeetRow > 9 ? $"fell into the gap (feet row {r.MaxFeetRow})"
					: !r.LogHas("run") ? "no running jump in the plan" : null,
			},
			new() {
				Name = "running_jump_no_room",
				Rows = Grid(Repeat("@@@@@@@@......................", 9), "@@@@@@@@S.................G...",
					"@@@@@@@@@@........@@@@@@@@@@@@", "@@@@@@@@@@!!!!!!!!@@@@@@@@@@@@", "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Pickaxe = false,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 20,
			},
			new() {
				Name = "drop_through_platforms",
				Rows = Grid(Repeat("....................", 3), "......S.............", "====================",
					Repeat("....................", 4), "====================", Repeat("....................", 2),
					"......G.............", "@@@@@@@@@@@@@@@@@@@@"),
				Check = r => !r.LogHas("DropThrough") ? "no DropThrough in the plan" : null,
			},
			new() {
				Name = "drop_6_no_damage",
				Rows = Grid(Repeat("......................", 3), "..S...................", Repeat("@@@@@@@...............", 5),
					"@@@@@@@.........G.....", "@@@@@@@@@@@@@@@@@@@@@@"),
				Check = r => r.FallDamageHits > 0 ? $"took fall damage {r.FallDamageHits}x" : null,
			},
			new() {
				Name = "unreachable_pillar",
				Rows = Grid(Repeat("......................", 3), "..............G.......", Repeat("..............@@......", 10),
					"..S...........@@......", "@@@@@@@@@@@@@@@@@@@@@@"),
				Pickaxe = false,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 20,
			},
			new() {
				Name = "tunnel_through_wall",
				Rows = Corridor,
				Dig = true,
				Check = r => r.Broken != 3 ? $"broke {r.Broken} blocks, expected 3" : null,
			},
			new() {
				Name = "dig_down_into_room",
				Rows = new[] {
					"..........", "..........", "..........", "..S.......",
					"@@######@@", "@@######@@", "@@######@@",
					"@........@", "@........@", "@..G.....@", "@@@@@@@@@@",
				},
				Dig = true,
				Check = r => r.Broken != 6 ? $"broke {r.Broken} blocks, expected 6" : r.FallDamageHits > 0 ? "took fall damage" : null,
			},
			new() {
				Name = "pillar_up_cliff",
				Rows = Cliff,
				Build = true,
				Blocks = 999,
				Check = r => r.Placed < 3 ? $"placed {r.Placed}, expected >= 3" : null,
			},
			new() {
				Name = "bridge_wide_dry_pit",
				Rows = Grid(Repeat("........................................", 7),
					"..S.................................G...",
					Repeat("@@@@@...........................@@@@@@@@", 10),
					"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				Build = true,
				Blocks = 999,
				Check = r => r.MaxFeetRow > 7 ? $"dropped into the pit (feet row {r.MaxFeetRow})"
					: r.Placed < 15 ? $"placed {r.Placed}, expected >= 15" : null,
			},
			new() {
				Name = "sand_in_path_mined_top_down",
				Rows = WithCell(Corridor, 10, 1, '%'),
				Dig = true,
				Check = r => r.FallingBlocksSeen > 0 || r.FallingBlockHits > 0 ? $"sand fell ({r.FallingBlocksSeen} falling, {r.FallingBlockHits} hits)"
					: r.Broken != 3 ? $"broke {r.Broken}, expected 3" : null,
			},
			new() {
				Name = "sand_roof_not_undermined",
				Rows = WithCell(Corridor, 10, 0, '%'),
				Dig = true,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 20,
				Check = r => r.FallingBlocksSeen > 0 ? $"sand fell ({r.FallingBlocksSeen})" : Max(r, 0, r.Broken, "broke"),
			},
			new() {
				Name = "lava_neighbour_not_opened",
				Rows = WithCell(Corridor, 10, 4, '!'),
				Dig = true,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 20,
				Check = r => Max(r, 0, r.Broken, "broke"),
			},
			new() {
				Name = "out_of_blocks_replans",
				Rows = Cliff,
				Build = true,
				Blocks = 3,
				ExpectSuccess = false,
				Check = r => r.Placed != 3 ? $"placed {r.Placed}, expected 3"
					: !r.LogHas("Blocked") ? "no Blocked re-plan in the log"
					: r.LogHas("OffPath") ? "OffPath in the log" : null,
			},
			new() {
				Name = "blocked_wall_appears",
				Rows = Grid(Repeat("........................................", 8),
					"..S...................................G.",
					"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@"),
				During = (arena, tick) => {
					if (tick != 40)
						return;
					for (int gy = 5; gy <= 8; gy++) {
						var w = arena.ToWorld(24, gy, 10);
						Terraria.Main.tile[w.X, w.Y].ResetToType(Terraria.ID.TileID.LihzahrdBrick);
					}
					Terraria.WorldGen.RangeFrame(arena.GridLeft + 23, arena.FloorY - 10, arena.GridLeft + 25, arena.FloorY);
				},
				Check = r => !r.LogHas("Blocked") ? "the new wall was not detected as Blocked" : null,
			},
		};
		return list;
	}
}
