using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Bot;

/// <summary>
/// Places remembered for the whole game (not the last few hundred), filed by area so "any within this box?" looks at a few
/// areas only, as Baritone keeps the chunks it has been in. Seen in game (measured runs T0b, U1, U5): with the last 300 leg
/// targets kept, of some 850 a run makes, the crystal searches of later days went back over the same legs ("leg 2 to
/// (2094,318)" on days 6, 7 and 8 of U5), 336 game hours of crystal searching over ten runs.
/// </summary>
public sealed class PlaceMemory
{
	/// <summary>Side of the areas places are filed by (tiles).</summary>
	public const int AreaTiles = 16;
	private readonly Dictionary<(int, int), List<TilePos>> areas = new();

	public int Count { get; private set; }

	public void Add(TilePos p)
	{
		var key = (Floor(p.X), Floor(p.Y));
		if (!areas.TryGetValue(key, out List<TilePos> list))
			areas[key] = list = new List<TilePos>();
		list.Add(p);
		Count++;
	}

	/// <summary>Is a remembered place within <paramref name="rx"/> columns and <paramref name="ry"/> rows of (x, y)?</summary>
	public bool AnyWithin(int x, int y, int rx, int ry)
	{
		for (int ax = Floor(x - rx); ax <= Floor(x + rx); ax++)
			for (int ay = Floor(y - ry); ay <= Floor(y + ry); ay++)
				if (areas.TryGetValue((ax, ay), out List<TilePos> list))
					foreach (TilePos v in list)
						if (System.Math.Abs(v.X - x) <= rx && System.Math.Abs(v.Y - y) <= ry)
							return true;
		return false;
	}

	public void Clear()
	{
		areas.Clear();
		Count = 0;
	}

	private static int Floor(int v) => v >= 0 ? v / AreaTiles : (v - AreaTiles + 1) / AreaTiles;
}
