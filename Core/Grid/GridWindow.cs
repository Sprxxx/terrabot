namespace TerraBot.Core.Grid;

/// <summary>
/// Copies a rectangle of another grid into a local array so hot loops (jump expansion reads hundreds of cells
/// per node) avoid repeated lookups in the live world. Reads outside the window fall through to the source.
/// </summary>
public sealed class GridWindow : ITileGrid
{
	private ITileGrid source;
	private int x0, y0, w, h;
	private CellKind[] cells = new CellKind[0];

	public void Load(ITileGrid grid, int left, int top, int width, int height)
	{
		source = grid;
		x0 = left;
		y0 = top;
		w = width;
		h = height;
		if (cells.Length < w * h)
			cells = new CellKind[w * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
				cells[y * w + x] = grid.Get(x0 + x, y0 + y);
	}

	public CellKind Get(int x, int y)
	{
		int lx = x - x0, ly = y - y0;
		if ((uint)lx < (uint)w && (uint)ly < (uint)h)
			return cells[ly * w + lx];
		return source.Get(x, y);
	}
}
