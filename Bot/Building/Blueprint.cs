using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Building;

/// <summary>What a blueprint wants in one cell.</summary>
public enum PlanCell
{
	/// <summary>Not part of the plan (left as it is).</summary>
	None,
	/// <summary>A solid block (frame, roof, floor).</summary>
	Block,
	/// <summary>No tile, background wall behind it (room interior).</summary>
	Wall,
	/// <summary>No tile, no requirement on the wall (doorway, space for furniture placed later).</summary>
	Clear,
}

/// <summary>
/// A multi-tile item placed at a cell rectangle of the plan (table, chair, torch, door). <paramref name="Optional"/>: a
/// comfort the plan works without (an arena's campfire and torches), left out if it will not go down (BuildSkill).
/// <see cref="Face"/>: the way the player faces when it is placed (-1 left, 1 right, 0 any): a chair takes the placer's
/// direction (Player.PlaceThing passes Player.direction to TileObject.CanPlace), so a chair beside its table faces it.
/// </summary>
public readonly record struct PlanItem(int ItemType, int X, int Y, int Width, int Height, bool PlaceLast = false, bool Optional = false, int Face = 0)
{
	public bool Covers(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// A small building plan in local coordinates (x right, y down, (0,0) = top-left of the plan). The site's
/// top-left tile in the world is chosen by <see cref="SiteFinder"/>. Items are placed after the blocks and walls,
/// in list order; <see cref="PlanItem.PlaceLast"/> ones (the door) after the bot has stepped outside.
/// </summary>
public sealed class Blueprint
{
	private readonly PlanCell[,] cells;

	public string Name { get; }
	public int Width { get; }
	public int Height { get; }
	public IReadOnlyList<PlanItem> Items { get; }
	/// <summary>A cell inside the room, for the housing check.</summary>
	public TilePos Inside { get; }
	/// <summary>Where the bot stands to place the <see cref="PlanItem.PlaceLast"/> items (outside the room).</summary>
	public TilePos OutsideStand { get; }
	/// <summary>Wall item to put on <see cref="PlanCell.Wall"/> cells.</summary>
	public int WallItem { get; }
	/// <summary>
	/// Every wall item the plan takes, the preferred first (<see cref="WallItem"/> alone unless set): an NPC room takes Stone Wall
	/// before Wood Wall, wood being what its furniture is made of. Seen in game (world_wof E51, 2026-10-18): 10 of the 24 Wood
	/// carried made 38 Wood Walls for a room, and its door was 4 Wood short with no tree in reach.
	/// </summary>
	public IReadOnlyList<int> WallItems { get => wallItems ?? new[] { WallItem }; init => wallItems = value; }
	private readonly IReadOnlyList<int> wallItems;
	/// <summary>A house: the build ends with the game's housing check on <see cref="Inside"/>.</summary>
	public bool IsHouse { get; init; } = true;
	/// <summary>Fell trees standing in the plan's open cells (else a tree rules the site out).</summary>
	public bool ChopTrees { get; init; }
	/// <summary>A row of blocks over a chasm's opening (<see cref="Bridge"/>): the planner keeps its own count of these.</summary>
	public bool IsBridge { get; init; }
	/// <summary>Reserve only the block and item cells while building, so walking may dig through open cells that are mined anyway.</summary>
	public bool ReserveBlocksOnly { get; init; }
	/// <summary>
	/// The plan's blocks are not reserved but rebuilt (<see cref="BotContext.Rebuilt"/>): a walk may mine one at the cost of
	/// placing it again, and place one where the plan has one. An arena's floor bridged over a dip is a roof over the dip:
	/// seen in game (measured run U23), the walk from the house came up the dip under it, and with the floor off limits found
	/// "no path ... (start h 3, rows 251..374)" to the cells above it, and the site was given up. A house's frame stays reserved.
	/// </summary>
	public bool RebuildBlocks { get; init; }
	/// <summary>Sites needing more edits than this are not considered (<see cref="SiteFinder"/>).</summary>
	public int MaxEdits { get; init; } = SiteFinder.MaxEdits;
	/// <summary>How far (columns) <see cref="SiteFinder"/> looks for a site.</summary>
	public int SearchRadius { get; init; } = SiteFinder.SearchRadius;
	/// <summary>
	/// An arena for the evil biome's boss: wanted IN the evil biome, so <see cref="SiteFinder.Score"/> inverts its evil-site
	/// cost (a site whose middle is off the biome and out of the Corruption zone costs <see cref="SiteFinder.EvilStepFactor"/>
	/// times as much, one in it the plain cost). Worm Food works only in the zone (Player.cs:43462) and the worm dives and
	/// despawns once no player is in it (NPC.cs:55436-55481): an arena off it is of no use for that fight.
	/// </summary>
	public bool InEvilBiome { get; init; }
	/// <summary>
	/// An arena whose floor may be raised off the ground (SiteFinder's raised floors: a long floor in the air, as a player
	/// bridges a chasm or a lake with it). The worm's arena over the corruption, and Skeletron's over the lake by the Dungeon.
	/// </summary>
	public bool RaisedFloor { get; init; }
	/// <summary>
	/// Rows over the floor an arena wants open to the sky, beyond its own open air (0: none): each block in them costs a site as
	/// if the build mined it (SiteFinder.Score). Skeletron hovers with its top 250 px over the player's top
	/// (<see cref="Skills.SkeletronPattern.HoverTopRows"/>), and an arrow stops on a tile where the boss does not.
	/// </summary>
	public int SkyRows { get; init; }
	/// <summary>Skeletron's arena by the Dungeon's entrance (the planner keeps its own memory of it).</summary>
	public bool AtDungeon { get; init; }

	public Blueprint(string name, string[] rows, IReadOnlyList<PlanItem> items, TilePos inside, TilePos outsideStand, int wallItem)
	{
		Name = name;
		Height = rows.Length;
		Width = rows[0].Length;
		cells = new PlanCell[Width, Height];
		for (int y = 0; y < Height; y++)
			for (int x = 0; x < Width; x++)
				cells[x, y] = rows[y][x] switch { '#' => PlanCell.Block, 'w' => PlanCell.Wall, '.' => PlanCell.Clear, _ => PlanCell.None };
		Items = items;
		Inside = inside;
		OutsideStand = outsideStand;
		WallItem = wallItem;
	}

	public PlanCell At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? PlanCell.None : cells[x, y];

	public int Count(PlanCell kind)
	{
		int n = 0;
		foreach (PlanCell c in cells)
			if (c == kind)
				n++;
		return n;
	}

	/// <summary>
	/// The smallest house vanilla accepts with some margin: 12×6 including the frame (72 room tiles; vanilla
	/// WorldGen.StartRoomCheck wants at least 60, frame included). Interior 10×4 with wood walls behind it, a
	/// 1×3 door on the left, a table and a chair on the floor, a torch on the back wall. The floor row is the
	/// ground the house stands on (blocks where it is missing).
	/// <code>
	///   ############   y0 roof
	///   #wwwwwwwwww#   y1
	///   .wwwwwwwwtw#   y2  door column x0, torch at (9,2)
	///   .wwwwwwwwww#   y3  table 3×2 at x4-6, chair at x7 (y3-4)
	///   .wwwwwwwwww#   y4
	///   ############   y5 floor
	/// </code>
	/// </summary>
	public static Blueprint SmallHouse(GameIds ids, bool heartLantern = false) => new(
		heartLantern ? "small house with a heart lantern" : "small house",
		new[] {
			"############",
			"#wwwwwwwwww#",
			".wwwwwwwwww#",
			".wwwwwwwwww#",
			".wwwwwwwwww#",
			"############",
		},
		new[] {
			new PlanItem(ids.Table, 4, 3, 3, 2),
			new PlanItem(ids.Chair, 7, 3, 1, 2, Face: -1), // facing the table on its left
			new PlanItem(ids.Torch, 9, 2, 1, 1),
			new PlanItem(ids.Door, 0, 2, 1, 3, PlaceLast: true),
		}.Concat(heartLantern ? new[] { new PlanItem(ids.HeartLantern, 2, 1, 1, 2) } : Array.Empty<PlanItem>()).ToArray(),
		inside: new TilePos(5, 2),
		outsideStand: new TilePos(-3, 4),
		wallItem: ids.WoodWall);

	/// <summary>Width of an NPC room's plan (<see cref="NpcRoom"/>): the house's 12, frame included.</summary>
	public const int NpcRoomWidth = 12;
	/// <summary>Columns from one NPC room's origin to the next one's along a row: the side wall between them is shared.</summary>
	public const int NpcRoomStep = NpcRoomWidth - 1;

	/// <summary>
	/// A room for a town NPC, built onto a wall that stands already (<paramref name="shared"/> -1: the plan's left column is a
	/// neighbour's right wall, +1: its right column a neighbour's left wall; 0: a room on its own) as players build a row of
	/// rooms: the side wall between two rooms is one column, a door in it serves both. The game's housing rules, from the
	/// decompiled WorldGen: StartRoomCheck floods the room from a cell over every non-solid tile, counting the frame tiles it
	/// touches, and wants 60 to 750 (CheckRoom's maxRoomTiles); a closed door is solid there (Main.tileSolid[10]), so the shared
	/// door bounds both rooms. Each flooded cell needs a house wall (Main.wallHouse: a placed Wood Wall) or a solid tile within 2
	/// cells on its row and on its column. RoomNeeds wants a chair, a table, a door (a platform also counts:
	/// TileID.Sets.RoomNeeds.CountsAsDoor) and a light source among the tiles flooded. ScoreRoom wants a home spot: a solid,
	/// not solid-top floor block (no platform) with solid blocks either side and three open cells over it. The house's 12×6
	/// passes all of it (72 tiles); so does this, the same frame and furniture with the door on the far side from the shared
	/// wall (the right for a room on its own), and table, chair and torch kept out of the columns by the doors (an open door
	/// swings into the column beside it).
	/// <code>
	///   S###########   y0 roof            S: the shared column (left or right), not part of the plan
	///   Swwwwwwwwww#   y1
	///   Swwwwwwwtww.   y2  torch at (8,2); the door at x11 (x0 when the right column is shared)
	///   Swwwwwwwwww.   y3  table 3×2 at x3-5, chair at x6 (y3-4)
	///   Swwwwwwwwww.   y4
	///   S###########   y5 floor
	/// </code>
	/// </summary>
	public static Blueprint NpcRoom(GameIds ids, int shared)
	{
		int door = shared > 0 ? 0 : NpcRoomWidth - 1;
		var rows = new string[6];
		for (int y = 0; y < rows.Length; y++) {
			char[] row = (y == 0 || y == rows.Length - 1 ? new string('#', NpcRoomWidth) : "#" + new string('w', NpcRoomWidth - 2) + "#").ToCharArray();
			if (y >= 2 && y <= 4)
				row[door] = '.';
			if (shared < 0)
				row[0] = ' ';
			else if (shared > 0)
				row[NpcRoomWidth - 1] = ' ';
			rows[y] = new string(row);
		}
		return new Blueprint(shared == 0 ? "NPC room" : shared < 0 ? "NPC room (left wall shared)" : "NPC room (right wall shared)", rows,
			new[] {
				new PlanItem(ids.Table, 3, 3, 3, 2),
				new PlanItem(ids.Chair, 6, 3, 1, 2, Face: -1), // facing the table on its left
				new PlanItem(ids.Torch, 8, 2, 1, 1),
				new PlanItem(ids.Door, door, 2, 1, 3, PlaceLast: true),
			},
			inside: new TilePos(5, 2),
			outsideStand: new TilePos(shared > 0 ? -3 : NpcRoomWidth + 2, 4),
			wallItem: ids.WoodWall) {
			IsNpcRoom = true, SharedSide = shared,
			WallItems = ids.StoneWall > 0 ? new[] { ids.StoneWall, ids.WoodWall } : new[] { ids.WoodWall },
			// A tree beside the house is felled for a room there, as a player would (the bot's own house keeps away from trees).
			ChopTrees = true,
		};
	}

	/// <summary>A room for a town NPC (<see cref="NpcRoom"/>): the planner keeps its own list of these.</summary>
	public bool IsNpcRoom { get; init; }
	/// <summary>An NPC room's shared side wall: -1 left, +1 right, 0 none (<see cref="NpcRoom"/>).</summary>
	public int SharedSide { get; init; }

	/// <summary>
	/// About a screen wide, as the guides say. 64 was too narrow in game: between "turn when safe" (20 from the
	/// middle) and the end there were only 9 tiles, less than one gap between the Eye's dashes, so the bot was
	/// often forced to turn with a dash coming.
	/// </summary>
	public const int ArenaWidth = 96;
	public const int ArenaClearance = 7;
	/// <summary>Blocks of each end post (a body pushed into a wall two blocks high stops; the bot jumps 6).</summary>
	public const int ArenaEndPostHeight = 2;
	public const int ArenaTorchSpacing = 12;
	/// <summary>How far (columns, the arena's middle from the house) an arena site is looked for first.</summary>
	public const int ArenaSearchRadius = 80;
	/// <summary>
	/// How far it is looked for when there is none that close, as a player walks a little further for good ground: twice
	/// <see cref="ArenaSearchRadius"/>, the arena then at most about one more arena's width farther out.
	/// </summary>
	public const int ArenaFarSearchRadius = 2 * ArenaSearchRadius;

	/// <summary>
	/// A boss arena as the guides build one for the Eye of Cthulhu: a flat floor about a screen wide
	/// (<see cref="ArenaWidth"/>) with <see cref="ArenaClearance"/> rows of open air above it (jumps, and shots at a
	/// boss hovering overhead), torches every <see cref="ArenaTorchSpacing"/> tiles and a campfire in the middle
	/// (Cozy Fire: +0.5 HP/s and 10% more regeneration within 170×125 tiles). The heart lantern goes in the house
	/// (it hangs from a ceiling; same range), see <see cref="SmallHouse"/>.
	/// <code>
	///   ..........................   y0 .. y6  open air (bumps mined, trees felled)
	///   ...t...........CCC.....t..   y6  torches and the campfire on the floor
	///   ##########################   y7  floor (pits filled)
	/// </code>
	/// The same arena serves the Eater of Worlds (<paramref name="inEvilBiome"/>, <see cref="InEvilBiome"/>): the guides fight
	/// it from a long flat floor on the corruption's surface, kiting sideways as it arcs out of the ground (docs M10_SPEC.md
	/// section 4 and G9, at least 60 tiles; this one is 96).
	/// </summary>
	public static Blueprint Arena(GameIds ids, int width = ArenaWidth, int searchRadius = ArenaSearchRadius, bool inEvilBiome = false, bool atDungeon = false)
	{
		var rows = new string[ArenaClearance + 1];
		for (int y = 0; y < ArenaClearance; y++)
			rows[y] = new string('.', width);
		rows[ArenaClearance] = new string('#', width);
		// A post at each end, ArenaEndPostHeight blocks on the floor: knockback does not push the bot off an end. Seen in game
		// (measured run U17): the Eye of Cthulhu at 234/2800 knocked the bot past the east end at (2127,268), down a 41-row
		// drop (183 fall damage), and it was killed there. The bot jumps over a post to come and go.
		for (int y = ArenaClearance - ArenaEndPostHeight; y < ArenaClearance; y++)
			rows[y] = "#" + rows[y].Substring(1, width - 2) + "#";
		int top = ArenaClearance - 1, mid = width / 2;
		var items = new List<PlanItem> { new(ids.Campfire, mid - 1, top - 1, 3, 2, Optional: true) };
		for (int x = ArenaTorchSpacing / 2; x < width; x += ArenaTorchSpacing)
			if (x < mid - 2 || x > mid + 2)
				items.Add(new PlanItem(ids.Torch, x, top, 1, 1, Optional: true));
		return new Blueprint(atDungeon ? "boss arena by the Dungeon" : inEvilBiome ? "boss arena in the evil biome" : "boss arena", rows, items, inside: new TilePos(mid, top), outsideStand: new TilePos(mid, top), wallItem: 0) {
			IsHouse = false, ChopTrees = true, ReserveBlocksOnly = true, RebuildBlocks = true, MaxEdits = ArenaMaxEdits(width), SearchRadius = searchRadius,
			InEvilBiome = inEvilBiome, RaisedFloor = inEvilBiome || atDungeon, AtDungeon = atDungeon,
			SkyRows = atDungeon ? Skills.SkeletronPattern.HoverTopRows : 0,
		};
	}

	/// <summary>
	/// Edits an arena may take: 600 for the standard 96-wide one; in proportion for a wider one (Skeletron's by the Dungeon).
	/// </summary>
	private static int ArenaMaxEdits(int width) => width <= ArenaWidth ? 600 : 600 * width / ArenaWidth;

	/// <summary>
	/// Skeletron's arena width: long enough to run from the spinning head for a whole spin from the middle, either way. It spins
	/// for 400 ticks at 1.5 px/tick (NPC.cs:22382-22399) against the player's 3 (Player.maxRunSpeed): 400 × 3 = 1200 px, 75
	/// tiles a side, 150, and the arena's hard margins (BossFightSkill.ArenaHardMargin, 4) at each end: 158, rounded up to 160.
	/// The spec's floor in the M11 world is 170 long (docs M11_SPEC.md section 3d); with no site that long, the standard one.
	/// </summary>
	public const int DungeonArenaWidth = 160;

	/// <summary>
	/// One row of blocks <paramref name="width"/> long across a chasm's opening, level with the lower of its two lips
	/// (<see cref="Chasms.Openings"/>), as a player bridges a corruption chasm with dirt: the walk, the patrol and the fights
	/// go across on it, and a hit's knockback lands the bot on it rather than at the chasm's floor. Placed from a lip
	/// outwards, each block next to the last (BuildSkill: a solid neighbour first).
	/// <code>
	///   ######.......######   the lips, the opening between them
	///   ######XXXXXXX######   the bridge, the lower lip's ground row
	///   ######.......######
	/// </code>
	/// </summary>
	public static Blueprint Bridge(int width) => new("chasm bridge", new[] { new string('#', width) }, Array.Empty<PlanItem>(),
		inside: new TilePos(0, 0), outsideStand: new TilePos(0, 0), wallItem: 0) {
		IsHouse = false, IsBridge = true, ReserveBlocksOnly = true, MaxEdits = Chasms.MaxBridgeTiles,
	};

	/// <summary>The feet row a player standing on the floor has (arena blueprints: the row above the floor).</summary>
	public int StandRow => Height - 2;
}
