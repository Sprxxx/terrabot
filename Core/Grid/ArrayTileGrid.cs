using System;
using System.Collections.Generic;
using System.Text;

namespace TerraBot.Core.Grid;

/// <summary>Fixed-size in-memory grid. Out of bounds reads as Solid. Used by tests and offline tools.</summary>
public sealed class ArrayTileGrid : ITileGrid
{
	private readonly CellKind[] cells;

	public int Width { get; }
	public int Height { get; }

	/// <summary>Named markers found while parsing ASCII (e.g. 'S' start, 'G' goal). Marker cells are Empty.</summary>
	public Dictionary<char, TilePos> Markers { get; } = new();

	public ArrayTileGrid(int width, int height)
	{
		Width = width;
		Height = height;
		cells = new CellKind[width * height];
	}

	public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

	public CellKind Get(int x, int y) => InBounds(x, y) ? cells[y * Width + x] : CellKind.Solid;

	public void Set(int x, int y, CellKind kind)
	{
		if (!InBounds(x, y))
			throw new ArgumentOutOfRangeException($"({x},{y}) outside {Width}x{Height}");
		cells[y * Width + x] = kind;
	}

	public void Fill(int x0, int y0, int x1, int y1, CellKind kind)
	{
		for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
				Set(x, y, kind);
	}

	/// <summary>
	/// Parses rows top to bottom. Legend: '#' solid, '&' a solid block holding water, '*' a cobweb, '=' platform, '.' or ' ' empty, '~' water, '!' lava, 'h' honey.
	/// Any letter other than 'h' is an Empty cell recorded in <see cref="Markers"/>.
	/// </summary>
	public static ArrayTileGrid Parse(params string[] rows)
	{
		int width = 0;
		foreach (string r in rows)
			width = Math.Max(width, r.Length);

		var grid = new ArrayTileGrid(width, rows.Length);
		for (int y = 0; y < rows.Length; y++) {
			for (int x = 0; x < rows[y].Length; x++) {
				char c = rows[y][x];
				if (char.IsLetter(c) && c != 'h') {
					grid.Markers[c] = new TilePos(x, y);
					continue;
				}
				grid.Set(x, y, CellKindExtensions.FromChar(c));
			}
		}
		return grid;
	}

	public string ToAscii()
	{
		var sb = new StringBuilder();
		for (int y = 0; y < Height; y++) {
			for (int x = 0; x < Width; x++)
				sb.Append(Get(x, y).ToChar());
			sb.Append('\n');
		}
		return sb.ToString();
	}
}
