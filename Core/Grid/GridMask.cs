namespace TerraBot.Core.Grid;

/// <summary>
/// Bit rows of a grid, 64 cells to a word, filled lazily from the source and kept for one search (the grid hardly
/// changes during one). Jump templates test their cells a row at a time with a mask instead of a read per cell: the
/// jump move tested 328 templates of 40-80 cells at every node, 64% of all search time (measured on run J2's cave).
/// Bits are set where a cell is NOT headroom, NOT passable, or CAN be stood on. The words sit in a flat array around
/// the search's start (a dictionary lookup per word cost more than the cells it saved), cleared by a generation stamp.
/// </summary>
public sealed class GridMask
{
	/// <summary>Words across (64 tiles each) and rows kept around the start: covers SearchLimits.MaxRadius 400 and then some.</summary>
	public const int BlocksAcross = 16, Rows = 1024;

	private readonly ulong[] notHeadroom = new ulong[BlocksAcross * Rows];
	private readonly ulong[] notPassable = new ulong[BlocksAcross * Rows];
	private readonly ulong[] standOn = new ulong[BlocksAcross * Rows];
	private readonly int[] filled = new int[BlocksAcross * Rows];
	private int generation;
	private int bx0, y0;
	private ITileGrid grid;

	public ITileGrid Grid => grid;

	/// <summary>Forget everything and read from <paramref name="g"/> around <paramref name="centre"/> from now on (start of a search).</summary>
	public void Reset(ITileGrid g, TilePos centre)
	{
		grid = g;
		if (++generation == int.MaxValue) {
			System.Array.Clear(filled);
			generation = 1;
		}
		bx0 = (centre.X >> 6) - BlocksAcross / 2;
		y0 = centre.Y - Rows / 2;
	}

	public enum Kind { NotHeadroom, NotPassable, StandOn }

	/// <summary>Bit i = property of cell (x + i, y), for i below <paramref name="width"/> (at most 64).</summary>
	public ulong Bits(Kind kind, int x, int y, int width)
	{
		int bx = x >> 6, off = x & 63;
		ulong lo = Word(kind, bx, y) >> off;
		if (off != 0 && off + width > 64)
			lo |= Word(kind, bx + 1, y) << (64 - off);
		return width >= 64 ? lo : lo & ((1UL << width) - 1);
	}

	private ulong Word(Kind kind, int bx, int y)
	{
		int lx = bx - bx0, ly = y - y0;
		if ((uint)lx >= BlocksAcross || (uint)ly >= Rows)
			return Read(kind, bx, y); // outside the window: straight from the grid
		int i = ly * BlocksAcross + lx;
		if (filled[i] != generation) {
			Fill(bx, y, out notHeadroom[i], out notPassable[i], out standOn[i]);
			filled[i] = generation;
		}
		return kind switch {
			Kind.NotHeadroom => notHeadroom[i],
			Kind.NotPassable => notPassable[i],
			_ => standOn[i],
		};
	}

	private ulong Read(Kind kind, int bx, int y)
	{
		Fill(bx, y, out ulong h, out ulong p, out ulong s);
		return kind switch { Kind.NotHeadroom => h, Kind.NotPassable => p, _ => s };
	}

	private void Fill(int bx, int y, out ulong h, out ulong p, out ulong s)
	{
		h = p = s = 0;
		int x0 = bx << 6;
		for (int i = 0; i < 64; i++) {
			CellKind k = grid.Get(x0 + i, y);
			ulong bit = 1UL << i;
			if (!k.IsHeadroom())
				h |= bit;
			if (!k.IsPassable())
				p |= bit;
			if (k.CanStandOn())
				s |= bit;
		}
	}
}
