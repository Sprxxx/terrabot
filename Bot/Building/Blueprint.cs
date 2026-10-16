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

/// <summary>A multi-tile item placed at a cell rectangle of the plan (table, chair, torch, door).</summary>
public readonly record struct PlanItem(int ItemType, int X, int Y, int Width, int Height, bool PlaceLast = false)
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
	/// <summary>A house: the build ends with the game's housing check on <see cref="Inside"/>.</summary>
	public bool IsHouse { get; init; } = true;
	/// <summary>Fell trees standing in the plan's open cells (else a tree rules the site out).</summary>
	public bool ChopTrees { get; init; }
	/// <summary>Reserve only the block and item cells while building, so walking may dig through open cells that are mined anyway.</summary>
	public bool ReserveBlocksOnly { get; init; }
	/// <summary>Sites needing more edits than this are not considered (<see cref="SiteFinder"/>).</summary>
	public int MaxEdits { get; init; } = SiteFinder.MaxEdits;
	/// <summary>How far (columns) <see cref="SiteFinder"/> looks for a site.</summary>
	public int SearchRadius { get; init; } = SiteFinder.SearchRadius;

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
			new PlanItem(ids.Chair, 7, 3, 1, 2),
			new PlanItem(ids.Torch, 9, 2, 1, 1),
			new PlanItem(ids.Door, 0, 2, 1, 3, PlaceLast: true),
		}.Concat(heartLantern ? new[] { new PlanItem(ids.HeartLantern, 2, 1, 1, 2) } : Array.Empty<PlanItem>()).ToArray(),
		inside: new TilePos(5, 2),
		outsideStand: new TilePos(-3, 4),
		wallItem: ids.WoodWall);

	/// <summary>
	/// About a screen wide, as the guides say. 64 was too narrow in game: between "turn when safe" (20 from the
	/// middle) and the end there were only 9 tiles, less than one gap between the Eye's dashes, so the bot was
	/// often forced to turn with a dash coming.
	/// </summary>
	public const int ArenaWidth = 96;
	public const int ArenaClearance = 7;
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
	/// </summary>
	public static Blueprint Arena(GameIds ids, int width = ArenaWidth, int searchRadius = ArenaSearchRadius)
	{
		var rows = new string[ArenaClearance + 1];
		for (int y = 0; y < ArenaClearance; y++)
			rows[y] = new string('.', width);
		rows[ArenaClearance] = new string('#', width);
		int top = ArenaClearance - 1, mid = width / 2;
		var items = new List<PlanItem> { new(ids.Campfire, mid - 1, top - 1, 3, 2) };
		for (int x = ArenaTorchSpacing / 2; x < width; x += ArenaTorchSpacing)
			if (x < mid - 2 || x > mid + 2)
				items.Add(new PlanItem(ids.Torch, x, top, 1, 1));
		return new Blueprint("boss arena", rows, items, inside: new TilePos(mid, top), outsideStand: new TilePos(mid, top), wallItem: 0) {
			IsHouse = false, ChopTrees = true, ReserveBlocksOnly = true, MaxEdits = 600, SearchRadius = searchRadius,
		};
	}

	/// <summary>The feet row a player standing on the floor has (arena blueprints: the row above the floor).</summary>
	public int StandRow => Height - 2;
}
