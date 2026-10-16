using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Building;

/// <summary>
/// A chasm's opening in the walked surface: columns <see cref="X0"/>..<see cref="X1"/>, where the ground lies deeper under
/// the lip than a fall the body takes unhurt, bridged by one row of blocks at <see cref="Row"/> (the lower lip's ground row).
/// </summary>
public readonly record struct ChasmOpening(int X0, int X1, int Row)
{
	public int Width => X1 - X0 + 1;
	/// <summary>The bridge blueprint's top-left cell (<see cref="Blueprint.Bridge"/>).</summary>
	public TilePos Origin => new(X0, Row);
	public override string ToString() => $"x {X0}-{X1} at row {Row}";
}

/// <summary>
/// Where the surface a player walks drops away into a chasm. Seen in game (measured run M10c, world_eow): three deaths by
/// falls into the corruption's chasms, 665-765 damage, at (1702,364), (1701,362) and (1778,396), from its surface at rows
/// 269-294: knocked back off a lip by eaters of souls and zombies, and run off one closing in on a zombie. Players bridge
/// them with a row of dirt; so does the planner (<see cref="Blueprint.Bridge"/>).
/// </summary>
public static class Chasms
{
	/// <summary>
	/// The widest opening bridged: twice the widest shaft ChasmRunner carves (7-20 tiles, WorldGen.cs:70191-70447, docs
	/// M10_SPEC.md section 3), for the funnel at its top. Wider than that, the ground is a valley, not a chasm: its foot is
	/// walked to (the paths drop no more than a safe fall, and pillar and dig back up).
	/// </summary>
	public const int MaxBridgeTiles = 40;

	/// <summary>
	/// The chasm openings along the surface in columns <paramref name="x0"/>..<paramref name="x1"/>: walked from west to east
	/// on the surface (each column's first cell to stand on from the sky, <see cref="TopRow"/>), a run of columns whose ground
	/// lies more than <paramref name="safeFall"/> rows under the last lip (or that have none), closed within
	/// <see cref="MaxBridgeTiles"/> by a column the walk can stand on again, no more than a safe fall under the lip. The scan
	/// reaches <see cref="MaxBridgeTiles"/> beyond either end, so an opening at an end has its lip. Not where liquid breaks
	/// the fall (vanilla Player.Update resets the fall in water). Openings whose columns overlap the range are returned.
	/// </summary>
	public static List<ChasmOpening> Openings(ITileGrid g, int x0, int x1, int safeFall)
	{
		int lo = x0 - MaxBridgeTiles, n = x1 - x0 + 1 + 2 * MaxBridgeTiles;
		var ground = new int?[n];
		for (int i = 0; i < n; i++)
			ground[i] = TopRow(g, lo + i);
		var found = new List<ChasmOpening>();
		int? lip = null;
		for (int i = 0; i < n;) {
			if (lip == null || ground[i] is int here && here - lip.Value <= safeFall) {
				lip = ground[i++];
				continue;
			}
			int j = i;
			while (j < n && j - i < MaxBridgeTiles && !(ground[j] is int r && r - lip.Value <= safeFall))
				j++;
			if (j < n && ground[j] is int far && far - lip.Value <= safeFall) {
				var o = new ChasmOpening(lo + i, lo + j - 1, Math.Max(lip.Value, far));
				if (o.X1 >= x0 && o.X0 <= x1 && !Wet(g, o, safeFall))
					found.Add(o);
				lip = far;
				i = j + 1;
			}
			else
				lip = ground[i++]; // a cliff down, not a chasm: its foot is the ground from here on
		}
		return found;
	}

	/// <summary>
	/// The column's first cell to stand on, coming down from the sky (rows as ProgressionPlanner.SurfaceAt scans them): the
	/// column's own, not a two-column body's, or a dip a body cannot stand in at its wall reads as a hole. Null if none.
	/// </summary>
	public static int? TopRow(ITileGrid g, int x)
	{
		for (int y = SkyRow; y < BottomRow; y++)
			if (g.Get(x, y).CanStandOn())
				return y;
		return null;
	}

	/// <summary>The rows <see cref="TopRow"/> scans, as ProgressionPlanner.SurfaceAt does.</summary>
	private const int SkyRow = 40, BottomRow = 900;

	/// <summary>Liquid under the bridge row within a safe fall: what falls in lands in it unhurt.</summary>
	private static bool Wet(ITileGrid g, ChasmOpening o, int safeFall)
	{
		for (int x = o.X0; x <= o.X1; x++)
			for (int y = o.Row; y <= o.Row + safeFall; y++)
				if (g.Get(x, y).IsLiquid())
					return true;
		return false;
	}
}
