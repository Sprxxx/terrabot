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
	public int Hits;
	public int DamageTaken;
	public int Deaths;
	public int Wood;
	public int Swords;
	public bool BenchPlaced;
	/// <summary>Best hotbar pickaxe power at the end.</summary>
	public int PickPower;
	/// <summary>The game's housing check on the built house: null = valid, else the reason ("no house" if none was built).</summary>
	public string Housing = "no house";
	/// <summary>The planner's NPC rooms at the end, and the game's housing check on them: null when every one passes, else the first that does not.</summary>
	public int NpcRooms;
	public string NpcRoomHousing;
	/// <summary>Town NPCs the game gave a home in the bot's rooms (the house and the NPC rooms), by name.</summary>
	public List<string> NpcsHoused = new();
	/// <summary>Iron-tier armour pieces worn at the end (0-3).</summary>
	public int ArmourWorn;
	/// <summary>A wooden sword is in hotbar slots 1-10 at the end (usable).</summary>
	public bool SwordOnHotbar;
	/// <summary>NPC.downedBoss1 at the end (the Eye of Cthulhu).</summary>
	public bool EyeDefeated;
	/// <summary>NPC.downedBoss2 at the end (the Eater of Worlds, or the Brain of Cthulhu in a Crimson world).</summary>
	public bool EvilBossDefeated;
	/// <summary>NPC.downedBoss3 at the end (Skeletron).</summary>
	public bool SkeletronDefeated;
	/// <summary>Main.hardMode at the end: the Wall of Flesh was defeated (docs M12_SPEC.md section 7).</summary>
	public bool HardMode;
	/// <summary>Guide Voodoo Dolls carried at the end, and whether a Guide (NPC 22) is alive.</summary>
	public int Dolls;
	public bool GuideAlive;
	/// <summary>Real-world runs: the milestones reached, in order, with the tick each came at.</summary>
	public string Milestones = "";
	/// <summary>The planner's arena: null = finished (floor, open air, torches, campfire), else why not.</summary>
	public string Arena = "no arena";
	/// <summary>A heart lantern hangs where the house blueprint puts it.</summary>
	public bool HeartLanternHung;
	/// <summary>Player.statLifeMax at the end (life crystals and fruit used).</summary>
	public int LifeMax;
	/// <summary>Journey Mode's research and duplication were on for the run (a Journey character, and the scenario allowing it).</summary>
	public bool JourneyOn;
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
	/// <summary>A second NPC type, spawned at each 'F' marker with the first (the Guide beside a Voodoo Demon, voodoo_rule).</summary>
	public int SecondEnemyType { get; init; }
	/// <summary>The hellbridge's length for the plan (ProgressionPlanner.BridgeLength); 0 = the plan's own (1700).</summary>
	public int BridgeLength { get; init; }
	/// <summary>Running tick at which the 'E' enemies spawn (0 = at the start).</summary>
	public int SpawnAtTick { get; init; }
	/// <summary>"walk" (to 'G'), "fight" (FightSkill), "chop" (gather 5 wood), "build" (a small house near 'S') or "auto" (the planner).</summary>
	public string Mode { get; init; } = "walk";
	/// <summary>Planner plan for Mode "auto" (ProgressionPlanner.Plans).</summary>
	public string AutoPlan { get; init; } = Bot.Brain.ProgressionPlanner.Plans.Early;
	/// <summary>Start at nightfall instead of mid-morning.</summary>
	public bool Night { get; init; }
	/// <summary>Reset the copied character to 100 max life (no life crystals or fruit eaten).</summary>
	public bool ResetLife { get; init; }
	/// <summary>Max life to set instead (life crystals counted accordingly), 0 = leave as is.</summary>
	public int MaxLife { get; init; }
	/// <summary>Clear the world's "boss defeated" flags (throw-away copy of the world).</summary>
	public bool ResetBosses { get; init; }
	/// <summary>Clear NPC.downedBoss3 (throw-away copy of the world): the Old Man stays, Skeletron can be summoned.</summary>
	public bool ResetSkeletron { get; init; }
	/// <summary>Armour to wear (head, body, legs item types), empty = none.</summary>
	public int[] Wear { get; init; } = System.Array.Empty<int>();
	/// <summary>Main.time at the start for a day scenario (27000 = mid-morning; 0 = dawn, 15 minutes of daylight).</summary>
	public double DayTime { get; init; } = 27000;
	public bool ExpectSuccess { get; init; } = true;
	public int TimeoutTicks { get; init; } = 60 * 45;
	/// <summary>
	/// Play the copied world as it is: no arena, no fixture, the character's own inventory, normal enemy spawns and
	/// town NPCs, starting at the world spawn. For "does it actually play the game" runs.
	/// </summary>
	public bool RealWorld { get; init; }
	/// <summary>
	/// A real-world run started from a checkpoint begins where the player stood when it was saved (the checkpoint's start.txt,
	/// SelfTestSystem.SaveCheckpoint), not at the world's spawn: the Skeletron fight from its "dungeon arena" checkpoint without
	/// the walk of 1,225 tiles to the Dungeon again.
	/// </summary>
	public bool ResumeAtCheckpoint { get; init; }
	/// <summary>
	/// Where the scenario's own arena goes (world column of its middle, world row of the grid's bottom row) and how many rows it
	/// clears; 0 = by the spawn, as the others share. For the Underworld's scenarios (docs M12_SPEC.md section 10): the Wall of
	/// Flesh spawns only for a doll below row maxTilesY - 205, and its side is the doll's half of the world (NPC.SpawnWOF).
	/// </summary>
	public int ArenaCentreX { get; init; }
	public int ArenaFloorRow { get; init; }
	public int ArenaHeight { get; init; }
	/// <summary>Run only when asked for by name (not in "all"): long, or changing the world for the rest (Hardmode).</summary>
	public bool ByName { get; init; }
	/// <summary>A real-world walk's goal: feet at this offset from the world's spawn tile (Main.spawnTileX, spawnTileY).</summary>
	public (int X, int Y)? GoalFromSpawn { get; init; }
	/// <summary>
	/// A Journey character's "infinite placement range" power off for the run (it is on by default, CreativePowers.cs:744-753):
	/// with it the player pulls items in from 240 px farther (Player.GetItemGrabRange, Player.cs:34313; PullItem_Pickup at 7 px/tick,
	/// 34086), and the thrown Guide Voodoo Doll was pulled back out of the lava's top. Seen in game (m12_synthetic_2, Pepi in
	/// Journey): every doll fell to row 1055 at 7 px/tick, then rose at (-1.7,-6.8) toward the bot 100 ticks after the throw, when
	/// its no-grab delay ran out (Player.cs:6003). The M12 world's character is Classic, without it.
	/// </summary>
	public bool NoFarReach { get; init; }
	/// <summary>
	/// Journey Mode's research and duplication on for this synthetic scenario (a Journey character needed; the other synthetic
	/// scenarios run with them off, as on Classic). Real-world runs have them on whenever the character is Journey's.
	/// </summary>
	public bool Journey { get; init; }
	/// <summary>Items set back to unresearched on the copied character before the run (the scenario's fixture).</summary>
	public int[] ResearchReset { get; init; } = System.Array.Empty<int>();
	/// <summary>Called every running tick with the tick number (for mid-run world changes).</summary>
	public Action<Arena, int> During { get; init; }
	/// <summary>The run also ends once the bot logs a line containing this (for skills that never finish on their own).</summary>
	public string FinishOnLog { get; init; }
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

	/// <summary>A room over a 36-row rope shaft: start and goal at the room's floor and at the rope's foot (or the other way).</summary>
	private static string[] LongRope(bool down)
	{
		var rows = new List<string> { new string('@', 20) };
		for (int y = 1; y <= 6; y++)
			rows.Add("@" + new string('.', 18) + "@");
		rows.Add("@@@@@@@@=====@@@@@@@");
		for (int y = 8; y <= 43; y++) {
			char[] r = (y < 36 ? "@@@@@@@@.....@@@@@@@" : "@..................@").ToCharArray();
			r[10] = '|';
			if (y == 43)
				r[8] = down ? 'G' : 'S';
			rows.Add(new string(r));
		}
		rows[6] = Set(rows[6], down ? 9 : 10, down ? 'S' : 'G');
		rows.Add(new string('@', 20));
		return rows.ToArray();
	}

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

	/// <summary>Surface over 10 stone rows with five 2×2 life-crystal pockets at different depths ('H' = pocket top-right).</summary>
	/// <summary>
	/// Rough ground for an arena, 110 wide: a dirt mound 5 wide and 2 high (48-52), a pit 4 wide and 2 deep (55-58),
	/// trees at 45 and 70. Any 96-wide site in the field (the side walls are 20 columns out) covers columns 33-76, so the mound and pit are always in it.
	/// </summary>
	private static string[] ArenaField()
	{
		const int width = 110;
		string feet = "..S" + new string('.', width - 3), above = new string('.', width);
		string ground = new string(',', width), under = new string('#', width);
		for (int x = 48; x <= 52; x++) { // columns every 96-wide site in the field includes
			above = Set(above, x, '#');
			feet = Set(feet, x, '#');
		}
		feet = Set(Set(feet, 45, 'T'), 70, 'T');
		for (int x = 55; x <= 58; x++) {
			ground = Set(ground, x, '.');
			under = Set(under, x, '.');
		}
		return Grid(Repeat(new string('.', width), 13), above, feet, ground, under, new string('#', width),
			Repeat(new string('s', width), 2), new string('@', width));
	}

	private static string[] CrystalCave()
	{
		const int width = 80;
		var stone = Enumerable.Range(0, 10).Select(_ => new string('s', width).ToCharArray()).ToArray();
		foreach (var (x, y) in new[] { (21, 2), (36, 4), (51, 1), (63, 6), (13, 7) }) {
			stone[y][x - 1] = '.';
			stone[y][x] = 'H';
			stone[y + 1][x - 1] = '.';
			stone[y + 1][x] = '.';
		}
		return Grid(Repeat(new string('.', width), 14), "..S" + new string('.', width - 3), new string(',', width), new string('#', width),
			stone.Select(r => new string(r)).ToArray(), new string('@', width));
	}

	/// <summary>The bow bot's kit for Skeletron (spec S1): the Demon Bow, 999 unholy arrows, 10 healing potions, a recall potion.</summary>
	private static void SkeletronKit(Terraria.Player p)
	{
		p.inventory[3] = new Terraria.Item(Terraria.ID.ItemID.DemonBow);
		p.inventory[20] = new Terraria.Item(Terraria.ID.ItemID.UnholyArrow, 999);
		p.inventory[21] = new Terraria.Item(Terraria.ID.ItemID.HealingPotion, 10);
		p.inventory[22] = new Terraria.Item(Terraria.ID.ItemID.RecallPotion, 1);
	}

	/// <summary>
	/// The Underworld's synthetic ground (docs M12_SPEC.md section 10), as grid rows 0-60 for the world's rows UnderworldLayer + 15
	/// to + 75 (1015-1075 in a 1200-row world): a stone ceiling on the first, a stone floor (the ledge, the bridge) on row + 42 from
	/// column 0 to <paramref name="floorTo"/>, open air east of it, lava on rows + 66 to + 74 under it all, the arena's unbreakable
	/// base below. The bot ('S') two columns short of the floor's east end, the Guide ('E') penned at its west end.
	/// </summary>
	private static string[] Underworld(int width, int floorTo, bool guide = true)
	{
		var rows = new char[UnderworldRows][];
		for (int y = 0; y < UnderworldRows; y++) {
			rows[y] = new string(y == 0 ? 's' : y == UnderworldRows - 1 ? '@' : y >= UnderworldLavaRow ? '!' : '.', width).ToCharArray();
			if (y == UnderworldFloorRow)
				for (int x = 0; x <= floorTo; x++)
					rows[y][x] = 's';
		}
		// The Guide's pen at the floor's west end: walls 5 high (a town NPC does not jump out of it), the Guide inside.
		for (int y = UnderworldFloorRow - 5; y < UnderworldFloorRow; y++)
			rows[y][0] = rows[y][8] = '#';
		if (guide)
			rows[UnderworldFloorRow - 1][4] = 'E';
		rows[UnderworldFloorRow - 1][floorTo - 2] = 'S';
		return rows.Select(r => new string(r)).ToArray();
	}

	/// <summary>Grid rows of <see cref="Underworld"/>: 61 in all, the floor on row 27 (+ 42), lava from row 51 (+ 66).</summary>
	private const int UnderworldRows = 61, UnderworldFloorRow = 27, UnderworldLavaRow = 51;
	/// <summary>The world row of <see cref="Underworld"/>'s last grid row: Main.UnderworldLayer + 75 (the arena's floor row).</summary>
	private static int UnderworldFloor => Terraria.Main.UnderworldLayer + 75;
	/// <summary>
	/// fight_wof's bridge: the real plan's (ProgressionPlanner.DefaultBridgeLength, 3829), as the plan lays it: its east end, where
	/// the bot starts and throws the doll, BridgeEdgeRoom short of the world's edge, the rest west past the middle. Seen in game
	/// (fight_wof, m12_synthetic_classic): on 1750 tiles the bot ran off the far end into the lava with the Wall at 2046 of 8000.
	/// </summary>
	private static int LongBridge => Bot.Brain.ProgressionPlanner.DefaultBridgeLength;
	/// <summary>The arena's middle column that puts <see cref="LongBridge"/>'s east end BridgeEdgeRoom short of the world's edge.</summary>
	private static int LongBridgeCentreX
	{
		get {
			int left = Terraria.Main.maxTilesX - Bot.Brain.ProgressionPlanner.BridgeEdgeRoom - LongBridge - Arena.GridOffsetX;
			return left + Math.Max(Arena.MinWidth, LongBridge + 11 + 2 * Arena.GridOffsetX) / 2;
		}
	}

	/// <summary>The world's middle column (NPC.SpawnWOF's maxTilesX / 2: the doll's side of it is the Wall's).</summary>
	private static int WorldMiddle => Terraria.Main.maxTilesX / 2;

	/// <summary>
	/// voodoo_rule's ground (spec section 10): <see cref="Underworld"/> 140 wide with its floor (the bridge's start) to x 40, the bot
	/// at its end, the Guide penned at its west end ('E'), a Voodoo Demon ('F') over the lava 6 columns past the floor's end.
	/// </summary>
	private static string[] VoodooRuleGround()
	{
		string[] rows = Underworld(140, 40);
		char[] r = rows[UnderworldFloorRow - 6].ToCharArray();
		r[46] = 'F';
		rows[UnderworldFloorRow - 6] = new string(r);
		return rows;
	}

	/// <summary>The Wall of Flesh's synthetic scenarios: no Guide Voodoo Doll thrown by day or night matters (spec S5); the bow kit.</summary>
	private static Action<Terraria.Player> DollKit(int dolls) => p => {
		// The copied character's accessories off (a Treasure Magnet's 150 px pull, Item.treasureGrabRange, could take the thrown doll
		// back): the kit's own only (WofKit), as the M10u character has none.
		for (int a = 3; a < p.armor.Length; a++)
			p.armor[a] = new Terraria.Item();
		p.inventory[3] = new Terraria.Item(Terraria.ID.ItemID.DemonBow);
		p.inventory[20] = new Terraria.Item(Terraria.ID.ItemID.WoodenArrow, 999);
		p.inventory[21] = new Terraria.Item(Terraria.ID.ItemID.HealingPotion, 10);
		p.inventory[24] = new Terraria.Item(Terraria.ID.ItemID.GuideVoodooDoll, dolls);
	};

	/// <summary>
	/// The S1 kit for the Wall of Flesh (spec S1, the bot's own at M10u's eow_arena): the Demon Bow, about 2000 wooden arrows (and
	/// <paramref name="unholy"/> unholy ones), 10 healing potions, one doll; Hermes Boots, a Cloud in a Bottle and an Aglet worn.
	/// </summary>
	private static Action<Terraria.Player> WofKit(int unholy) => p => {
		DollKit(1)(p);
		p.inventory[22] = new Terraria.Item(Terraria.ID.ItemID.WoodenArrow, 999);
		if (unholy > 0)
			p.inventory[23] = new Terraria.Item(Terraria.ID.ItemID.UnholyArrow, unholy);
		p.armor[3] = new Terraria.Item(Terraria.ID.ItemID.HermesBoots);
		p.armor[4] = new Terraria.Item(Terraria.ID.ItemID.CloudinaBottle);
		p.armor[5] = new Terraria.Item(Terraria.ID.ItemID.Aglet);
	};

	/// <summary>The doll scenarios' check: the Wall summoned from <paramref name="side"/>, the Guide dead, <paramref name="dollsLeft"/> dolls left.</summary>
	private static string DollChecks(RunResult r, string side, int dollsLeft) =>
		!r.LogHas("wall of flesh summoned") ? "no Wall of Flesh after the throw"
		: !r.LogHas(side) ? $"the Wall did not come {side}"
		: r.GuideAlive ? "the Guide is alive after the doll burnt"
		: r.Dolls != dollsLeft ? $"{r.Dolls} dolls left, not {dollsLeft}"
		: null;

	private static string Max(RunResult r, int expected, int actual, string what) =>
		actual > expected ? $"{what} {actual} > {expected}" : null;

	private static IReadOnlyList<Scenario> CreateAll()
	{
		var list = new List<Scenario> {
		new() {
			// Milestone 9: the copied world and character as they are, /bot auto boss from the spawn in the morning.
			// Not part of "all" runs (see SelfTestSystem): it plays for up to an hour. Run it by name.
			// The user's world Journey Test (2026-10-17): a house the mod StartWithBase builds over the spawn of a new world, its way in a
			// rope from the spawn 21 tiles up a shaft to the platforms of its floor (spawn-area dump: rope x 3201, rows 414-434, the
			// floor's platforms x 3199-3203 on row 413, spawn tile (3201,435)). Walked to its bottom floor: up the rope (ClimbMove),
			// nothing mined or placed. selftest.ps1 -Scenarios rope_house -Player Nenne -World "Journey Test" -WorldFile Journey_Test
			// -Mods StartWithBase.
			Name = "rope_house",
			RealWorld = true,
			ByName = true,
			Rows = new[] { "S" },
			GoalFromSpawn = (-1, -23),
			TimeoutTicks = 60 * 60,
			Check = r => r.Placed > 0 || r.Broken > 0 ? $"{r.Placed} blocks placed and {r.Broken} broken on the way (not up the rope)" : null,
		},
		new() {
			Name = "world_eye",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "auto",
			AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Boss,
			Dig = true,
			Build = true,
			DayTime = 3000,
			// A fixed game-time budget, so runs can be compared by the milestones they reach in it (SELFTEST milestone lines):
			// 10 in-game days (a day is 86,400 ticks), 15-20 real minutes with -Fast -DrawEvery 16 at its usual 13-20x. Was 5:
			// the best runs got full armour by day 5, and the stages after it (life, bow, arena, the fight) were never seen.
			TimeoutTicks = 10 * 86400,
			Check = r => !r.EyeDefeated ? "the Eye of Cthulhu was not defeated" : null,
		},
		new() {
			// Milestone 10: the Eater of Worlds by Worm Food (docs M10_SPEC.md, the Worm Food path), /bot auto evilboss. Started
			// from the checkpoint after the Eye (selftest.ps1 -FromCheckpoint ...\Checkpoints\Eye_loot_collected): the boss plan's
			// stages are met there, and the evil boss's begin. Run it by name.
			Name = "world_eow",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "auto",
			AutoPlan = Bot.Brain.ProgressionPlanner.Plans.EvilBoss,
			Dig = true,
			Build = true,
			DayTime = 3000,
			// 5 in-game days, the spec's budget (about 3 expected: the hunt for 45 eaters of souls about 1, the rest about 1.5).
			TimeoutTicks = 5 * 86400,
			Check = r => !r.EvilBossDefeated ? "the Eater of Worlds was not defeated" : null,
		},
		new() {
			// Milestone 11: Skeletron (docs M11_SPEC.md section 6), /bot auto skeletron: the prep, the walk to the Dungeon, an
			// arena by the Old Man, the night, the Curse, the fight. Started from a checkpoint after the Eye (selftest.ps1
			// -FromCheckpoint ...\checkpoints\M10j\worm_food, or Eye_loot_collected): nothing in it needs the Eater of Worlds. From
			// its own "dungeon arena" checkpoint it starts where that was saved, by the Dungeon (ResumeAtCheckpoint). Run it by name.
			Name = "world_skeletron",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "auto",
			AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Skeletron,
			Dig = true,
			Build = true,
			DayTime = 3000,
			ResumeAtCheckpoint = true,
			// 3 in-game days, the spec's budget: about 1 expected (the walk 0.25-0.5 of a day, the build 0.1-0.2, the wait for
			// dusk, the fight under 5 minutes), and two nights more for failed tries (one summon a night).
			TimeoutTicks = 3 * 86400,
			Check = r => !r.SkeletronDefeated ? "Skeletron was not defeated" : null,
		},
		new() {
			// Milestone 12: the Wall of Flesh (docs M12_SPEC.md section 9), /bot auto wof: the prep, the Guide, a hellevator down to the
			// Underworld, a hellbridge of 1700 tiles over its lava, a Guide Voodoo Doll from a Voodoo Demon at its far end, the summon
			// there, the fight along it, the loot. Started from the checkpoint after the Eater's arena (selftest.ps1 -FromCheckpoint
			// ...\checkpoints\M10u\eow_arena: the Demon Bow, about 1185 arrows, 2290 blocks, 29 healing potions, the Guide at home);
			// from its own "underworld reached", "hellbridge" or "guide voodoo doll" checkpoints it starts where those were saved
			// (ResumeAtCheckpoint). Run it by name.
			Name = "world_wof",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "auto",
			AutoPlan = Bot.Brain.ProgressionPlanner.Plans.WallOfFlesh,
			Dig = true,
			Build = true,
			DayTime = 3000,
			ResumeAtCheckpoint = true,
			// 6 in-game days, the spec's budget: about 2.5 expected (the top-up 0.3, the shaft 0.5, the bridge 0.7-1, the doll hunt
			// 0.3-1 with a long tail, the fight under 5 minutes), the rest the margin for one failed fight (a new doll, the Guide back by day).
			TimeoutTicks = 6 * 86400,
			Check = r => !r.HardMode ? "the Wall of Flesh was not defeated" : null,
		},
		new() {
			// Times path searches on the real world from the spawn (PathBench). Run it by name.
			Name = "world_pathbench",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "pathbench",
			Dig = true,
			Build = true,
			DayTime = 3000,
			ExpectSuccess = false,
			TimeoutTicks = 60 * 60,
			FinishOnLog = "PATHBENCH done",
		},
		new() {
			// Why no arena site is found round the spawn of the real world (SiteFinder.Explain). Run it by name.
			Name = "world_arenasite",
			RealWorld = true,
			Rows = new[] { "S" },
			Mode = "arenasite",
			Dig = true,
			Build = true,
			DayTime = 3000,
			ExpectSuccess = false,
			TimeoutTicks = 60 * 60,
			FinishOnLog = "ARENASITE",
		},
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
				// Milestone 7: a bow and arrows against a flier, at night (demon eyes leave at dawn). No melee weapon.
				Name = "shoot_demon_eye",
				Rows = Grid(Repeat(new string('.', 60), 12), "..S........................................E................", new string('@', 60)),
				Mode = "fight",
				Night = true,
				Pickaxe = false,
				Inventory = p => {
					p.inventory[0] = new Terraria.Item(Terraria.ID.ItemID.WoodenBow);
					p.inventory[20] = new Terraria.Item(Terraria.ID.ItemID.WoodenArrow, 200);
				},
				EnemyType = Terraria.ID.NPCID.DemonEye,
				TimeoutTicks = 60 * 90,
				Check = r => r.EnemiesLeft > 0 ? $"{r.EnemiesLeft} demon eye(s) left" : !r.LogHas("shooting") ? "never shot" : null,
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
				// Milestone 8: a boss arena on rough ground (mound, pit, two trees): fell the trees, mine the mound, fill the
				// pit, craft torches and a campfire from wood and gel, place them.
				Name = "arena_auto",
				Rows = ArenaField(),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Arena,
				Dig = true,
				Build = true,
				Blocks = 20,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => {
					p.inventory[10] = new Terraria.Item(Terraria.ID.ItemID.Wood, 30);
					p.inventory[11] = new Terraria.Item(Terraria.ID.ItemID.Gel, 6);
				},
				TimeoutTicks = 60 * 60 * 5,
				Check = r => r.Arena != null ? $"arena: {r.Arena}" : r.FallDamageHits > 0 ? "fall damage" : null,
			},
			new() {
				// Journey Mode (needs a Journey character, selftest.ps1 -Player <one>; the copy's research of these three is set back to
				// none first). Plan "crystals" (max life 200) at 100 max life with 10 life crystals, which is what research takes of them
				// (Sacrifices.tsv "H" = 10): researched, not used, then duplicated from the menu and used to max life 400 (used, the ten
				// would make 300). 120 wood (research takes 100, "L") and one work bench ("D" = 1): the habit researches both, the bench
				// duplicated back. The iron pickaxe in the hotbar (one, in use) is not researched. Run it by name.
				Name = "journey_research",
				Rows = Grid(Repeat(new string('.', 40), 8), "..S" + new string('.', 37), new string('@', 40)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Crystals,
				Journey = true,
				ByName = true,
				ResetLife = true,
				ResearchReset = new int[] { Terraria.ID.ItemID.LifeCrystal, Terraria.ID.ItemID.Wood, Terraria.ID.ItemID.WorkBench, Terraria.ID.ItemID.IronPickaxe,
					Terraria.ID.ItemID.HermesBoots, Terraria.ID.ItemID.CopperCoin, Terraria.ID.ItemID.MagicMirror, Terraria.ID.ItemID.WoodenArrow },
				Inventory = p => {
					p.inventory[20] = new Terraria.Item(Terraria.ID.ItemID.LifeCrystal, 10);
					p.inventory[21] = new Terraria.Item(Terraria.ID.ItemID.Wood, 120);
					p.inventory[22] = new Terraria.Item(Terraria.ID.ItemID.WorkBench);
					// The user (2026-10-17): accessories researched "and THEN" worn; "same thing with ammo and coins"; Journey run J2:
					// the only Magic Mirror went into research and never came back.
					p.inventory[23] = new Terraria.Item(Terraria.ID.ItemID.HermesBoots);
					p.inventory[24] = new Terraria.Item(Terraria.ID.ItemID.MagicMirror);
					p.inventory[50] = new Terraria.Item(Terraria.ID.ItemID.CopperCoin, 99);
					p.inventory[54] = new Terraria.Item(Terraria.ID.ItemID.WoodenArrow, 99);
				},
				TimeoutTicks = 60 * 60 * 3,
				Check = r => !r.JourneyOn ? "research was off: not a Journey character in a Journey world (selftest.ps1 -Player <a Journey character>)"
					: !r.LogHas("Journey: researched 10 Life Crystal") ? "the 10 life crystals were not researched"
					: !r.Log.Any(l => l.Contains("Journey: duplicated") && l.Contains("Life Crystal")) ? "no life crystals duplicated"
					: r.LifeMax < Bot.Brain.ProgressionPlanner.MaxCrystalLife ? $"max life {r.LifeMax}, not {Bot.Brain.ProgressionPlanner.MaxCrystalLife}"
					: !r.LogHas("Journey: researched 100 Wood") ? "the wood was not researched"
					: !r.LogHas("Journey: researched 1 Work Bench") ? "the work bench was not researched"
					: !r.LogHas("Journey: researched 1 Hermes Boots") ? "the Hermes Boots were not researched"
					: r.Log.FindIndex(l => l.Contains("Gear: wearing Hermes Boots")) < r.Log.FindIndex(l => l.Contains("Journey: researched 1 Hermes Boots")) ? "the Hermes Boots were worn before research (or not at all)"
					: !r.LogHas("Journey: researched 1 Magic Mirror") || !r.Log.Any(l => l.Contains("Journey: duplicated") && l.Contains("Magic Mirror")) ? "the Magic Mirror was not researched and duplicated back"
					: !r.LogHas("Journey: researched 99 Copper Coin") ? "the 99 copper coins were not researched"
					: !r.LogHas("Journey: researched 99 Wooden Arrow") ? "the 99 arrows were not researched"
					: null,
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
				// NPC housing (plan "housing"): the house from a kit, then rooms for the town NPCs built onto it from wood and gel
				// at the bench, until two have moved in. The fixture clears every NPC, so the Guide can come (always, Main.cs:83508),
				// and the Merchant: a gold coin carried (NPC.SpawnAllowed_Merchant wants 50 silver, NPC.cs:2118); max life 120 (one
				// life crystal used) lets the Nurse come after him (SpawnAllowed_Nurse, NPC.cs:2101). The game moves them in itself:
				// Main.UpdateTime_SpawnTownNPCs picks one every 7200 ticks of day, then WorldGen.SpawnTownNPC takes an empty valid
				// room when a random tile update lands on its walls; so the run starts at dawn, with the whole day for it.
				// By name, on a world where time runs: with Journey's time frozen (the default test world's saved powers) no NPC
				// is ever picked (run 2026-10-17 on Pepi: rooms valid, "0/2 housed" at the timeout). PASS on Peppe, Noxious Vision
				// of Organs: the Merchant and the Nurse in the bot's two rooms after 24271 ticks.
				Name = "npc_housing",
				ByName = true,
				Rows = Grid(Repeat(new string('.', 70), 16),
					"..S" + new string('.', 67),
					new string(',', 70),
					Repeat(new string('#', 70), 4),
					new string('@', 70)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Housing,
				DayTime = 0,
				Dig = true,
				Build = true,
				Blocks = 60,
				MaxLife = 120,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => {
					int slot = 10;
					void Give(int type, int stack = 1) => p.inventory[slot++] = new Terraria.Item(type, stack);
					Give(Terraria.ID.ItemID.WorkBench);
					Give(Terraria.ID.ItemID.WoodWall, 40); // the house's kit; the NPC rooms' from the wood
					Give(Terraria.ID.ItemID.WoodenDoor);
					Give(Terraria.ID.ItemID.WoodenChair);
					Give(Terraria.ID.ItemID.WoodenTable);
					Give(Terraria.ID.ItemID.Torch, 3);
					Give(Terraria.ID.ItemID.Wood, 100); // about 30 a room: 10 for 40 walls, a table 8, a door 6, a chair 4, torches
					Give(Terraria.ID.ItemID.Gel, 4);
					Give(Terraria.ID.ItemID.GoldCoin);
				},
				TimeoutTicks = 60 * 60 * 15,
				Check = r => r.Housing != null ? $"housing check (the house): {r.Housing}"
					: r.NpcRooms < 1 ? "no NPC room built"
					: r.NpcRoomHousing != null ? $"housing check (an NPC room): {r.NpcRoomHousing}"
					: r.NpcsHoused.Count < 2 ? $"{r.NpcsHoused.Count} town NPCs moved in ({string.Join(", ", r.NpcsHoused)})"
					: !r.LogHas("NPC housing: ") ? "no NPC housing log line" : null,
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
				// The night in the house: no armour, so after building the house the bot walks in and waits, rather than
				// mining the anvil's ore on the surface. Seen in game: a goal on the housing check's tile (2 rows above the
				// floor) had no path, and the closed door counted as a wall.
				Name = "night_home",
				Rows = Grid(Repeat(new string('.', 70), 17),
					"..S...........................................................T......",
					Repeat(new string(',', 70), 1),
					Repeat(new string('#', 70), 1),
					Repeat(new string('s', 70), 2),
					Repeat(new string('s', 10) + new string('i', 40) + new string('s', 20), 4),
					new string('@', 70)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Settle,
				Night = true,
				Dig = true,
				Build = true,
				Weapon = Terraria.ID.ItemID.CopperShortsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => {
					int slot = 10;
					void Give(int type, int stack = 1) => p.inventory[slot++] = new Terraria.Item(type, stack);
					Give(Terraria.ID.ItemID.WoodenSword);
					Give(Terraria.ID.ItemID.WorkBench);
					Give(Terraria.ID.ItemID.Furnace);
					Give(Terraria.ID.ItemID.Wood, 30);
					Give(Terraria.ID.ItemID.WoodWall, 40);
					Give(Terraria.ID.ItemID.WoodenDoor);
					Give(Terraria.ID.ItemID.WoodenChair);
					Give(Terraria.ID.ItemID.WoodenTable);
					Give(Terraria.ID.ItemID.Torch, 5);
				},
				FinishOnLog = "StayHome: inside",
				ExpectSuccess = false,
				TimeoutTicks = 60 * 60 * 2,
				Check = r => r.Housing != null ? $"housing check: {r.Housing}" : r.LogHas("StayHome: inside") ? null : "never got into the house",
			},
			new() {
				// Milestone 7 end to end from a settled character (iron gear worn, 200 life, stations and a house kit as
				// items, an iron bow, wood and stone for arrows, 3 lenses), at nightfall: place the stations, build the house,
				// craft 500 arrows, build the arena (Milestone 8) and hang a heart lantern in the house, hunt demon eyes for
				// the other lenses, find the altar, craft the Suspicious Looking Eye, walk to the arena, summon and win.
				// Spawns are off in the arena, so the scenario sends demon eyes while lenses are wanted.
				Name = "boss_auto",
				// 150 wide: the house near the start, the 96-wide arena beside it, and the altar off to the left (an altar
				// inside the arena's open air would rule the site out).
				Rows = Grid(Repeat(new string('.', 150), 14),
					Set(Set(new string('.', 150), 6, 'A'), 40, 'S'),
					new string(',', 150), Repeat(new string('#', 150), 2), Repeat(new string('s', 150), 4), new string('@', 150)),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Boss,
				Night = true,
				ResetBosses = true,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Dig = true,
				Build = true,
				Blocks = 60,
				Weapon = Terraria.ID.ItemID.IronBroadsword,
				Axe = Terraria.ID.ItemID.CopperAxe,
				Inventory = p => {
					int slot = 10;
					void Give(int type, int stack = 1) => p.inventory[slot++] = new Terraria.Item(type, stack);
					Give(Terraria.ID.ItemID.WoodenSword);
					Give(Terraria.ID.ItemID.WorkBench);
					Give(Terraria.ID.ItemID.Furnace);
					Give(Terraria.ID.ItemID.IronAnvil);
					Give(Terraria.ID.ItemID.IronBow);
					Give(Terraria.ID.ItemID.Wood, 45); // arrows 20, campfire 10, torches
					Give(Terraria.ID.ItemID.Gel, 4);
					Give(Terraria.ID.ItemID.LifeCrystal); // the heart lantern (max life is already 200)
					Give(Terraria.ID.ItemID.IronBar); // chains for it
					Give(Terraria.ID.ItemID.StoneBlock, 30);
					Give(Terraria.ID.ItemID.Lens, 3);
					Give(Terraria.ID.ItemID.WoodWall, 40);
					Give(Terraria.ID.ItemID.WoodenDoor);
					Give(Terraria.ID.ItemID.WoodenChair);
					Give(Terraria.ID.ItemID.WoodenTable);
					Give(Terraria.ID.ItemID.Torch, 5);
				},
				During = (a, t) => {
					Terraria.Player pl = Terraria.Main.LocalPlayer;
					// Only while the bot is out for lenses (hunting, or fighting what came): sent from the first tick, the eyes
					// met a bot with only a sword, building its house, and on Classic wore it down for most of the night.
					var current = pl.GetModPlayer<TerraBotPlayer>().Brain.Current;
					bool wantLenses = pl.CountItem(Terraria.ID.ItemID.Lens) < 6 && pl.CountItem(Terraria.ID.ItemID.SuspiciousLookingEye) == 0
						&& !Terraria.NPC.AnyNPCs(Terraria.ID.NPCID.EyeofCthulhu) && !Terraria.NPC.downedBoss1
						&& current is Bot.Skills.HuntSkill or Bot.Skills.FightSkill;
					if (!wantLenses || t % 300 != 0 || Terraria.Main.npc.Count(n => n.active && n.type == Terraria.ID.NPCID.DemonEye) >= 2)
						return;
					int side = (t / 300) % 2 == 0 ? 1 : -1;
					// Inside the arena walls (an eye spawned outside was fought through the wall).
					int x = Math.Clamp((int)(pl.Center.X / 16) + side * 25, a.Left + 4, a.Left + a.Width - 5);
					Terraria.NPC.NewNPC(new Terraria.DataStructures.EntitySource_DebugCommand("TerraBot selftest"),
						x * 16, (int)pl.Center.Y - 10 * 16, Terraria.ID.NPCID.DemonEye);
				},
				TimeoutTicks = 60 * 60 * 15,
				Check = r => !r.EyeDefeated ? "the Eye of Cthulhu was not defeated" : r.Housing != null ? $"housing: {r.Housing}"
					: r.Arena != null ? $"arena: {r.Arena}" : !r.HeartLanternHung ? "no heart lantern in the house" : !r.LogHas("in the arena") ? "did not fight in the arena" : null,
			},
			new() {
				// Milestone 7: the Eye of Cthulhu, with the gear the boss plan collects: iron bow, wooden arrows, iron armour,
				// 200 max life, a few lesser healing potions. Summoned at nightfall on a wide flat floor (kiting room).
				Name = "eye_of_cthulhu",
				Rows = Grid(Repeat(new string('.', 100), 20), "..................................................S" + new string('.', 49), new string('@', 100)),
				Mode = "boss",
				Night = true,
				ResetBosses = true,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Pickaxe = false,
				Weapon = Terraria.ID.ItemID.IronBroadsword,
				Inventory = p => {
					p.inventory[3] = new Terraria.Item(Terraria.ID.ItemID.IronBow);
					p.inventory[20] = new Terraria.Item(Terraria.ID.ItemID.WoodenArrow, 999);
					p.inventory[21] = new Terraria.Item(Terraria.ID.ItemID.LesserHealingPotion, 10);
					p.inventory[22] = new Terraria.Item(Terraria.ID.ItemID.SuspiciousLookingEye);
				},
				TimeoutTicks = 60 * 60 * 8,
				Check = r => !r.EyeDefeated ? "the Eye of Cthulhu was not defeated" : null,
			},
			new() {
				// Milestone 11 (docs M11_SPEC.md, the cheap synthetic scenarios): the Old Man two tiles off at night, the fight's
				// summon is his Curse button (ChatButtonSkill, TerrariaGameInfo.PressFirstChatButton). Passes once the head is up.
				Name = "oldman_curse",
				Rows = Grid(Repeat(new string('.', 40), 12), "..........S..E.........................", new string('@', 40)),
				Mode = "skeletron",
				Night = true,
				ResetSkeletron = true,
				EnemyType = Terraria.ID.NPCID.OldMan,
				Pickaxe = false,
				Inventory = SkeletronKit,
				ExpectSuccess = false,
				FinishOnLog = "Boss fight: Skeletron is here",
				TimeoutTicks = 60 * 30,
				Check = r => !r.LogHas("Chat: pressed") ? "the Curse button was not pressed" : !r.LogHas("Skeletron is here") ? "no Skeletron after the Curse" : null,
			},
			new() {
				// The same by day: no Curse button (Main.cs:51819-51825), nothing spawns, the fight fails at once.
				Name = "oldman_curse_day",
				Rows = Grid(Repeat(new string('.', 40), 12), "..........S..E.........................", new string('@', 40)),
				Mode = "skeletron",
				ResetSkeletron = true,
				EnemyType = Terraria.ID.NPCID.OldMan,
				Pickaxe = false,
				Inventory = SkeletronKit,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 30,
				Check = r => !r.LogHas("no Curse button by day") ? "no \"no Curse button by day\" refusal" : r.LogHas("Skeletron is here") ? "Skeletron came by day" : null,
			},
			new() {
				// Skeletron on a flat 170-tile floor at nightfall, the head spawned 16 rows over the bot (about its hover, 250 px
				// over the player's top): the Demon Bow and unholy arrows, iron armour, 200 life, healing potions. Measures the
				// fight's length and the hits taken, and tunes SkeletronPattern and the dodge against the hands' 18 and 17 px/tick.
				Name = "fight_skeletron",
				Rows = Grid(Repeat(new string('.', 170), 4), Set(new string('.', 170), 85, 'E'), Repeat(new string('.', 170), 15),
					Set(new string('.', 170), 85, 'S'), new string('@', 170)),
				Mode = "skeletron",
				Night = true,
				ResetSkeletron = true,
				EnemyType = Terraria.ID.NPCID.SkeletronHead,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Pickaxe = false,
				Inventory = SkeletronKit,
				TimeoutTicks = 60 * 60 * 9,
				Check = r => !r.SkeletronDefeated ? "Skeletron was not defeated" : null,
			},
			new() {
				// Milestone 12 (docs M12_SPEC.md section 10, S0): one Guide Voodoo Doll thrown east off a stone ledge at row 1042 into the
				// lava below, the Guide alive, in the world's east half (x 2400 > 2100): the Wall comes from the east. Passes once it is up.
				Name = "doll_lava_east",
				Rows = Underworld(60, 30),
				ArenaCentreX = WorldMiddle + 300,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				EnemyType = Terraria.ID.NPCID.Guide,
				Inventory = DollKit(1),
				ExpectSuccess = false,
				FinishOnLog = "wall of flesh summoned",
				TimeoutTicks = 60 * 30,
				Check = r => DollChecks(r, "from the east", 0),
			},
			new() {
				// The same in the west half (x 1800 < 2100): from the west (NPC.SpawnWOF, NPC.cs:90937-90962).
				Name = "doll_lava_west",
				Rows = Underworld(60, 30),
				ArenaCentreX = WorldMiddle - 300,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				EnemyType = Terraria.ID.NPCID.Guide,
				Inventory = DollKit(1),
				ExpectSuccess = false,
				FinishOnLog = "wall of flesh summoned",
				TimeoutTicks = 60 * 30,
				Check = r => DollChecks(r, "from the west", 0),
			},
			new() {
				// No Guide alive: the doll would only burn (Item.cs:50409-50425): it is not thrown, nothing spawns.
				Name = "doll_lava_noguide",
				Rows = Underworld(60, 30, guide: false),
				ArenaCentreX = WorldMiddle + 300,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Inventory = DollKit(1),
				ExpectSuccess = false,
				TimeoutTicks = 60 * 30,
				Check = r => !r.LogHas("doll wasted, no Guide") ? "no \"doll wasted, no Guide\" refusal" : r.LogHas("wall of flesh summoned") ? "a Wall with no Guide"
					: r.Dolls != 1 ? $"{r.Dolls} dolls left, not 1 (thrown?)" : null,
			},
			new() {
				// A stack of two: one is split off and thrown (a stack of N kills N - 1 more town NPCs, Item.cs:50426-50460), one is left.
				Name = "doll_lava_stack2",
				Rows = Underworld(60, 30),
				ArenaCentreX = WorldMiddle + 300,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				EnemyType = Terraria.ID.NPCID.Guide,
				Inventory = DollKit(2),
				ExpectSuccess = false,
				FinishOnLog = "wall of flesh summoned",
				TimeoutTicks = 60 * 30,
				Check = r => DollChecks(r, "from the east", 1),
			},
			new() {
				// The Wall of Flesh fought on the plan's flat stone bridge (LongBridge, 3829 tiles) at row 1042 over lava, a ceiling at
				// row 1015, the bot at its east end (120 short of the world's edge) with the S1 kit, iron armour and 200 life: the doll thrown east, the Wall from the
				// east, the fight west along the bridge. Measures the DPS (the fight's ticks), the distance, the laser hits and dodges,
				// the potions (spec S0: its numbers set the bridge's length L = 1.25 x 62,175 / DPS). Hardmode after it: by name only.
				Name = "fight_wof",
				Rows = Underworld(LongBridge + 11, LongBridge),
				ArenaCentreX = LongBridgeCentreX,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				EnemyType = Terraria.ID.NPCID.Guide,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Inventory = WofKit(0),
				TimeoutTicks = 60 * 60 * 15,
				Check = r => !r.HardMode ? "the Wall of Flesh was not defeated" : null,
			},
			new() {
				// The same with 75 unholy arrows (the 15 worm teeth's, spec 5a), shot first in a boss fight (AmmoOrder).
				Name = "fight_wof_unholy",
				Rows = Underworld(LongBridge + 11, LongBridge),
				ArenaCentreX = LongBridgeCentreX,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "wof",
				EnemyType = Terraria.ID.NPCID.Guide,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Inventory = WofKit(75),
				TimeoutTicks = 60 * 60 * 15,
				Check = r => !r.HardMode ? "the Wall of Flesh was not defeated" : null,
			},
			new() {
				// A Voodoo Demon over the lava by the hellbridge while it is being built, the Guide alive (spec section 10, G7): it is
				// never fought nor shot (one killed over lava summons the Wall at once), and a 60-tile bridge is built from the floor's
				// end. The plan "hellbridge" (the bridge alone) with the fights of auto mode. Passes once built with no Wall.
				Name = "voodoo_rule",
				Rows = VoodooRuleGround(),
				ArenaCentreX = WorldMiddle + 300,
				ArenaFloorRow = UnderworldFloor,
				ByName = true,
				NoFarReach = true,
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Hellbridge,
				BridgeLength = 60,
				Dig = true,
				Build = true,
				Blocks = 400,
				EnemyType = Terraria.ID.NPCID.Guide,
				SecondEnemyType = Terraria.ID.NPCID.VoodooDemon,
				MaxLife = 200,
				Wear = new int[] { Terraria.ID.ItemID.IronHelmet, Terraria.ID.ItemID.IronChainmail, Terraria.ID.ItemID.IronGreaves },
				Inventory = DollKit(0),
				TimeoutTicks = 60 * 60 * 6,
				// The rule (BotContext.Spared): never killed over lava; over the bridge's deck it may be fought (its doll would land there).
				Check = r => r.LogHas("wall of flesh summoned") ? "the Wall was summoned (a Voodoo Demon killed over lava)"
					: r.Deaths > 0 ? $"died {r.Deaths} time(s)"
					: !r.LogHas("Hellbridge done") ? "the bridge was not finished" : null,
			},
			new() {
				// Milestone 7: max life 100 -> 200 from five life crystals in stone pockets below the surface.
				Name = "life_crystals",
				Rows = CrystalCave(),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Crystals,
				ResetLife = true,
				Dig = true,
				Build = true,
				Blocks = 50,
				TimeoutTicks = 60 * 60 * 6,
				Check = r => r.LogHas("could not place a life crystal") ? "fixture: a crystal was not placed" : r.FallDamageHits > 0 ? "fall damage" : null,
			},
			new() {
				// Measured run S9: the bag full of dirt, stone, mud, cobwebs and seeds, a mined life crystal could not be picked
				// up, and chests kept their best items. The worthless stacks go to the trash.
				Name = "life_crystals_full_bag",
				Rows = CrystalCave(),
				Mode = "auto",
				AutoPlan = Bot.Brain.ProgressionPlanner.Plans.Crystals,
				ResetLife = true,
				Dig = true,
				Build = true,
				Blocks = 50,
				Inventory = p => {
					int[] junk = { Terraria.ID.ItemID.Amethyst, Terraria.ID.ItemID.Topaz, Terraria.ID.ItemID.Sapphire, Terraria.ID.ItemID.Emerald, Terraria.ID.ItemID.Ruby }; // each worth something (measured run S11)
					for (int i = 0, k = 0; i < 50; i++)
						if (p.inventory[i].IsAir)
							p.inventory[i] = new Terraria.Item(junk[k++ % junk.Length], 99);
				},
				TimeoutTicks = 60 * 60 * 6,
				Check = r => r.LogHas("could not place a life crystal") ? "fixture: a crystal was not placed" : r.FallDamageHits > 0 ? "fall damage" : null,
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
				// Measured runs M10j, M10k: walled in by ebonstone ('e') the Iron Pickaxe (40) cannot mine (65 needed), bombs in the
				// bag. BlastOutSkill throws one at the wall towards G, keeps out of the blast's 128 px box and waits for the blast.
				Name = "blast_out_ebonstone",
				Rows = Grid(Repeat(new string('e', 34), 3),
					Repeat("ee..............eee.............ee", 4),
					"ee.S............eee...........G.ee",
					Repeat(new string('e', 34), 4)),
				Mode = "blast",
				Dig = true,
				Inventory = p => p.inventory[10] = new Terraria.Item(Terraria.ID.ItemID.Bomb, 3),
				TimeoutTicks = 60 * 30,
				Check = r => r.LifeLost > 0 ? $"hurt by its own blast ({r.LifeLost} life)" : r.Broken == 0 ? "the wall still stands"
					: !r.LogHas("is open") ? "no blast seen" : null,
			},
			// A rope of 36 rows (deeper than a safe fall) from the platforms of a room's floor down a shaft to the ground, as in the
			// StartWithBase house over Journey Test's spawn but longer (2026-10-17): up it (ClimbMove), down it (ClimbDownMove), with no
			// digging or building; down, no life lost (a plain drop there is 36 rows, a fall that hurts).
			new() {
				Name = "rope_long_up",
				Rows = LongRope(down: false),
				Check = r => r.Placed > 0 || r.Broken > 0 ? $"{r.Placed} placed, {r.Broken} broken (not up the rope)" : null,
			},
			new() {
				Name = "rope_long_down",
				Rows = LongRope(down: true),
				Check = r => r.LifeLost > 0 ? $"{r.LifeLost} life lost on the way down" : r.Placed > 0 || r.Broken > 0 ? $"{r.Placed} placed, {r.Broken} broken" : null,
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
				// Measured run U30 (world_eye): the corruption chasm under the lip at (1779,341), open 55 rows, its wall behind; every
				// walk from the lip ended "no path" for 117,000 ticks. Down it on blocks placed mid-fall (ArrestedDrop), no fall
				// damage, the fall reflex left out of it. No digging: the ebonstone is beyond the pickaxe there.
				Name = "chasm_arrested_descent",
				Rows = Grid(Repeat("@......................@", 3), "@..S...................@",
					Repeat("@eeeeeewwwwwwwwwweeeeee@", 41), "@eeeeeewwwGwwwwwweeeeee@", "@@@@@@@@@@@@@@@@@@@@@@@@"),
				Build = true,
				Blocks = 200,
				TimeoutTicks = 60 * 60,
				Check = r => r.FallDamageHits > 0 ? $"took fall damage {r.FallDamageHits}x"
					: !r.LogHas("ArrestedDrop") ? "no ArrestedDrop in the plan"
					: r.LogHas("Fall: from row") ? "the fall reflex had to act" : null,
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
				// Measured run R1: walks and drops onto a half block in the feet row reached the right column a row "too high"
				// (the body rides on top of it) and timed out there, 54 times.
				Name = "walk_over_half_blocks",
				Rows = Grid(Repeat("..........................", 3), "..S...h..h.h.....hh....G..", "##########################"),
				Check = r => r.LogHas("timed out") || r.LogHas("Re-plan") ? "a step timed out or re-planned" : null,
			},
			new() {
				// Measured run S3: standing half over a 1-wide flooded hole the body bobs (vy -0.2) and vanilla starts no jump; 280
				// of 338 stalled steps began in water. Step off the hole first, then jump.
				Name = "jump_up_from_beside_a_flooded_hole",
				Rows = Grid(Repeat("@.....................", 3), "@..........G..........", Repeat("@.........@@@@@@@@@@@@", 2),
					"@~S~~~~~~~@@@@@@@@@@@@", "@@@~@@@@@@@@@@@@@@@@@@", "@@@~@@@@@@@@@@@@@@@@@@", "@@@@@@@@@@@@@@@@@@@@@@"),
				Check = r => r.LogHas("timed out") ? "a step timed out" : null,
			},
			new() {
				// Measured run S5: an Inner Tube from a chest, worn, floated the bot over a flooded hole at vy -3 for 278
				// heartbeats (every walk "never landed"). Auto mode takes it off.
				Name = "inner_tube_comes_off",
				Rows = Grid(Repeat(new string('.', 40), 8), "..S.....................................", Repeat(new string('@', 40), 2)),
				Mode = "auto",
				Inventory = p => {
					p.inventory[0] = new Terraria.Item(Terraria.ID.ItemID.CopperPickaxe);
					p.armor[3] = new Terraria.Item(Terraria.ID.ItemID.FloatingTube);
				},
				FinishOnLog = "Gear: taking off",
				ExpectSuccess = false,
				TimeoutTicks = 60 * 30,
				Check = r => r.LogHas("Gear: taking off") ? null : "the Inner Tube stayed on",
			},
			new() {
				// Measured run S1: drops from a node with a half block in its feet row waited 330-650 ticks "at the edge" (71
				// times): the body stood on the half block a row up and never fell. Now: plan again from where it stands.
				Name = "dig_down_under_half_blocks",
				Rows = Grid(Repeat("........................", 3), "..S...hh................", Repeat("@@@@@@##@@@@@@@@@@@@@@@@", 2),
					Repeat("@......................@", 2), "@..........G...........@", "@@@@@@@@@@@@@@@@@@@@@@@@"),
				Dig = true,
				Check = r => r.LogHas("timed out") ? "a step timed out" : null,
			},
			new() {
				// Measured run R1: on a minecart track along a cave floor nothing could be placed (the track fills the cell)
				// or broken (tracks counted as furniture): no pillar, no way up, hours back and forth.
				Name = "pillar_up_from_a_track",
				Rows = Grid(Repeat("......................", 3), "...........G..........", Repeat("..........@@@@@@@@@@@@", 8),
					"rrrrrrrrSr@@@@@@@@@@@@", "@@@@@@@@.@@@@@@@@@@@@@", "@@@@@@@@@@@@@@@@@@@@@@"),
				Dig = true,
				Build = true,
				Blocks = 30,
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
				// Measured run U23: a shelter at row 457 "could not dig down" twice, on a minecart track (a frame-important
				// tile) whose blocks below read as "under furniture"; dug in nowhere, the bot was killed by a black slime.
				Name = "dig_down_from_a_track",
				Rows = new[] {
					"..........", "..........", "..........", "rrSrrrrrrr",
					"@@######@@", "@@######@@", "@@######@@",
					"@........@", "@........@", "@..G.....@", "@@@@@@@@@@",
				},
				Dig = true,
				Check = r => r.FallDamageHits > 0 ? "took fall damage" : null,
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
				Rows = WithCell(Corridor, 10, 0, '!'), // lava over the wall: opening it would let it pour in
				Dig = true,
				ExpectSuccess = false,
				TimeoutTicks = 60 * 20,
				Check = r => Max(r, 0, r.Broken, "broke"),
			},
			new() {
				// Seen in game (measured runs L1, M1, M2): 5 of 17 deaths were drownings, the bot under water with air a few rows
				// up through a ceiling, pillaring at a column it could not rise in. Mine up through the ceiling instead.
				Name = "flooded_under_a_ceiling",
				Rows = Grid(
					Repeat("@....................@", 3),
					"@ssssssssssssssssssss@",
					Repeat("@~~~~~~~~~~~~~~~~~~~~@", 4),
					"@~~S~~~~~~~~~~~~~~~~~@",
					"@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "surface",
				Dig = true,
				Build = true,
				Blocks = 30,
				TimeoutTicks = 60 * 60,
				Check = r => r.Deaths > 0 ? $"died {r.Deaths} time(s)" : null,
			},
			new() {
				// Seen in game (measured run O1): surfacing at 3/4 breath with air 22 rows up, drowned on the way. Deep water under
				// a two-row ceiling: straight up, mining through.
				Name = "flooded_deep_shaft",
				Rows = Grid(
					Repeat("@....................@", 3),
					Repeat("@ssssssssssssssssssss@", 2),
					Repeat("@~~~~~~~~~~~~~~~~~~~~@", 17),
					"@~~S~~~~~~~~~~~~~~~~~@",
					"@@@@@@@@@@@@@@@@@@@@@@"),
				Mode = "surface",
				Dig = true,
				Build = true,
				Blocks = 60,
				TimeoutTicks = 60 * 60,
				Check = r => r.Deaths > 0 ? $"died {r.Deaths} time(s)" : null,
			},
			new() {
				// Lava under the wall stays put when the wall is dug (liquid never flows up): dug through, unhurt.
				Name = "lava_below_dug_over",
				Rows = WithCell(Corridor, 10, 4, '!'),
				Dig = true,
				TimeoutTicks = 60 * 20,
				Check = r => r.LifeLost > 0 ? $"lost {r.LifeLost} life" : null,
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
				Check = r => !r.LogHas("Blocked") && !r.LogHas("no longer valid; repaired") ? "the new wall was not detected (no Blocked re-plan or repair)" : null,
			},
		};
		return list;
	}
}
