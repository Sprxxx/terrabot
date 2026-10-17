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

	public bool Contains(TilePos p) => areas.TryGetValue((Floor(p.X), Floor(p.Y)), out List<TilePos> list) && list.Contains(p);

	/// <summary>Add the place unless it is remembered already (a set of places, not a trail of them); true if it was new.</summary>
	public bool AddOnce(TilePos p)
	{
		if (Contains(p))
			return false;
		Add(p);
		return true;
	}

	/// <summary>Forget the place (every copy of it); true if it was remembered.</summary>
	public bool Remove(TilePos p)
	{
		var key = (Floor(p.X), Floor(p.Y));
		if (!areas.TryGetValue(key, out List<TilePos> list))
			return false;
		int n = list.RemoveAll(v => v == p);
		if (list.Count == 0)
			areas.Remove(key);
		Count -= n;
		return n > 0;
	}

	/// <summary>The remembered places within <paramref name="rx"/> columns and <paramref name="ry"/> rows of (x, y).</summary>
	public List<TilePos> Within(int x, int y, int rx, int ry)
	{
		var found = new List<TilePos>();
		for (int ax = Floor(x - rx); ax <= Floor(x + rx); ax++)
			for (int ay = Floor(y - ry); ay <= Floor(y + ry); ay++)
				if (areas.TryGetValue((ax, ay), out List<TilePos> list))
					foreach (TilePos v in list)
						if (System.Math.Abs(v.X - x) <= rx && System.Math.Abs(v.Y - y) <= ry)
							found.Add(v);
		return found;
	}

	/// <summary>Every remembered place (in no particular order).</summary>
	public IEnumerable<TilePos> All
	{
		get {
			foreach (List<TilePos> list in areas.Values)
				foreach (TilePos v in list)
					yield return v;
		}
	}

	public void Clear()
	{
		areas.Clear();
		Count = 0;
	}

	private static int Floor(int v) => v >= 0 ? v / AreaTiles : (v - AreaTiles + 1) / AreaTiles;
}

/// <summary>
/// Whether the evil biome's zone held where the bot stood (<see cref="BotContext.Zone"/>): the latest look at each node, and
/// when. A node, not a column: the scene scan is centred on the body, and an arena's floor is one row.
/// </summary>
public sealed class ZoneMemory
{
	private readonly Dictionary<TilePos, (bool In, long Tick)> seen = new();

	public int Count => seen.Count;

	public void Note(TilePos node, bool inZone, long tick) => seen[node] = (inZone, tick);

	/// <summary>The zone at the node at the latest look, null if never stood on.</summary>
	public bool? At(TilePos node) => seen.TryGetValue(node, out var s) ? s.In : null;

	/// <summary>The latest look at any node within <paramref name="tiles"/> (Chebyshev) of the tile, null if none.</summary>
	public bool? Near(TilePos t, int tiles)
	{
		bool? latest = null;
		long at = long.MinValue;
		for (int x = t.X - tiles; x <= t.X + tiles; x++)
			for (int y = t.Y - tiles; y <= t.Y + tiles; y++)
				if (seen.TryGetValue(new TilePos(x, y), out var s) && s.Tick > at) {
					at = s.Tick;
					latest = s.In;
				}
		return latest;
	}
}
