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
	/// <summary>Best hotbar pickaxe power at the end.</summary>
	public int PickPower;
	/// <summary>The game's housing check on the built house: null = valid, else the reason ("no house" if none was built).</summary>
	public string Housing = "no house";
	/// <summary>Iron-tier armour pieces worn at the end (0-3).</summary>
	public int ArmourWorn;
	/// <summary>A wooden sword is in hotbar slots 1-10 at the end (usable).</summary>
	public bool SwordOnHotbar;
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
	/// <summary>"walk" (to 'G'), "fight" (FightSkill), "chop" (gather 5 wood), "build" (a small house near 'S') or "auto" (the planner).</summary>
	public string Mode { get; init; } = "walk";
	/// <summary>Planner plan for Mode "auto" (ProgressionPlanner.Plans).</summary>
	public string AutoPlan { get; init; } = Bot.Brain.ProgressionPlanner.Plans.Early;
	/// <summary>Start at nightfall instead of mid-morning.</summary>
	public bool Night { get; init; }
	/// <summary>Main.time at the start for a day scenario (27000 = mid-morning; 0 = dawn, 15 minutes of daylight).</summary>
	public double DayTime { get; init; } = 27000;
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
					: !r.SwordOnHotbar ? "the crafted sword is not on the hotbar" : null,
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
				// Milestone 5 from a fresh character's kit: wood, bench, sword, gel from the slimes for torches, stone
				// for a furnace, ore smelted into bars for an anvil and an iron-tier pickaxe (iron or lead: whatever
				// this world has; 'i' places that ore).
				Name = "iron_age_auto",
				Rows = Grid(Repeat(new string('.', 100), 17),
					"..S.........T.........T......E..T.........T.........T.........T.........T.E.......T.........T.......",
					Repeat(new string(',', 100), 1),
					Repeat(new string('#', 100), 1),
					Repeat(new string('s', 100), 2),
					Repeat(new string('s', 15) + new string('i', 31) + new string('s', 54), 3),
					Repeat(new string('s', 100), 1),
					new string('@', 100)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Iron,
				Dig = true,
				Build = true,
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => p.inventory[0] = new Terraria.Item(Terraria.ID.ItemID.CopperPickaxe),
				EnemyType = Terraria.ID.NPCID.BlueSlime,
				TimeoutTicks = 60 * 60 * 12,
				Check = r => r.PickPower < Bot.Brain.ProgressionPlanner.IronTierPickPower ? $"pick power {r.PickPower}"
					: r.FallDamageHits > 0 ? $"{r.FallDamageHits} fall-damage hit(s)" : null,
			},
			new() {
				// Milestone 6: a house from a kit (dirt, wood walls, door, chair, table, torches) on flat ground, checked
				// with the game's own housing query.
				Name = "build_house",
				Rows = Grid(Repeat(new string('.', 60), 16),
					"..S.........................................................",
					Repeat(new string(',', 60), 1),
					Repeat(new string('#', 60), 4),
					new string('@', 60)),
				Mode = "build",
				Dig = true,
				Build = true,
				Blocks = 60,
				Inventory = p => {
					p.inventory[10] = new Terraria.Item(Terraria.ID.ItemID.WoodWall, 50);
					p.inventory[11] = new Terraria.Item(Terraria.ID.ItemID.WoodenDoor);
					p.inventory[12] = new Terraria.Item(Terraria.ID.ItemID.WoodenChair);
					p.inventory[13] = new Terraria.Item(Terraria.ID.ItemID.WoodenTable);
					p.inventory[14] = new Terraria.Item(Terraria.ID.ItemID.Torch, 5);
				},
				TimeoutTicks = 60 * 60 * 3,
				Check = r => r.Housing != null ? $"housing check: {r.Housing}" : r.FallDamageHits > 0 ? "fall damage" : null,
			},
			new() {
				// Milestone 6 from copper tools: the iron chain, then a house, an iron-tier broadsword and the full
				// iron-tier armour set, worn. Starts at dawn so the whole run is daylight (no night to sit out). Four slimes for gel: with
				// spawns off in the arena there are no more later (the house needs a torch on top of the furnace's three).
				Name = "settle_auto",
				Rows = Grid(Repeat(new string('.', 100), 17),
					"..S.......T.......T.......T.......T.....E.....E.....E...T.......T.......T...E...T.......T.......T...",
					Repeat(new string(',', 100), 1),
					Repeat(new string('#', 100), 1),
					Repeat(new string('s', 100), 2),
					Repeat(new string('s', 10) + new string('i', 50) + new string('s', 40), 6),
					Repeat(new string('s', 100), 2),
					new string('@', 100)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Settle,
				DayTime = 0,
				Dig = true,
				Build = true,
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => p.inventory[0] = new Terraria.Item(Terraria.ID.ItemID.CopperPickaxe),
				EnemyType = Terraria.ID.NPCID.BlueSlime,
				TimeoutTicks = 60 * 60 * 25,
				Check = r => r.PickPower < Bot.Brain.ProgressionPlanner.IronTierPickPower ? $"pick power {r.PickPower}"
					: r.Housing != null ? $"housing check: {r.Housing}"
					: r.ArmourWorn < 3 ? $"{r.ArmourWorn}/3 armour pieces worn"
					: r.FallDamageHits > 0 ? $"{r.FallDamageHits} fall-damage hit(s)" : null,
			},
			new() {
				// Nightfall with nothing built yet: dig in and seal the hole, ignore the zombie that turns up, open up in
				// the morning and get on with the early chain.
				Name = "night_shelter",
				Rows = Grid(Repeat(new string('.', 90), 16),
					"..S......................E....T.........T.........T.........T.........T.........T........",
					Repeat(new string(',', 90), 1),
					Repeat(new string('#', 90), 6),
					new string('@', 90)),
				Mode = "auto",
				Night = true,
				Dig = true,
				Build = true,
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => p.inventory[0] = new Terraria.Item(Terraria.ID.ItemID.CopperPickaxe),
				EnemyType = Terraria.ID.NPCID.Zombie,
				SpawnAtTick = 500,
				During = (a, t) => {
					if (t == 1500) {
						Terraria.Main.dayTime = true;
						Terraria.Main.time = 0;
					}
				},
				TimeoutTicks = 60 * 60 * 5,
				Check = r => !r.LogHas("Shelter: sealed in") ? "never sealed in"
					: !r.LogHas("Shelter: morning") ? "never opened up"
					: r.LogHas("Danger: pausing Shelter") && r.Log.FindIndex(l => l.Contains("Danger: pausing Shelter")) > r.Log.FindIndex(l => l.Contains("Shelter: sealed in"))
						&& r.Log.FindIndex(l => l.Contains("Danger: pausing Shelter")) < r.Log.FindIndex(l => l.Contains("Shelter: morning")) ? "left the sealed shelter to fight"
					: !r.BenchPlaced ? "no work bench placed" : null,
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
