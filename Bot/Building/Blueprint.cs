using System.Collections.Generic;
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
	public static Blueprint SmallHouse(GameIds ids) => new(
		"small house",
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
		},
		inside: new TilePos(5, 2),
		outsideStand: new TilePos(-3, 4),
		wallItem: ids.WoodWall);
}
