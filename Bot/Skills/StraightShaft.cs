using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Bot.Skills;

/// <summary>What opening the floor under a body standing in a 2-wide shaft leads to (<see cref="StraightShaft.Below"/>).</summary>
public enum ShaftKind
{
	/// <summary>The floor's cells mined, the body comes down onto ground (or into a web) within a safe fall, no lava met.</summary>
	Dig,
	/// <summary>
	/// A liquid would come in (lava, or water or honey, beside, above or in a cell mined) or lava would be fallen into:
	/// <see cref="ShaftRow.Cells"/> are its cells. Water too: seen in game (S2), the shaft at x 2155 mined into a water pocket at row
	/// 418, the water filled the shaft 16 rows deep, and the bot surfaced and dived again for minutes, three rows in two.
	/// </summary>
	Lava,
	/// <summary>A cell the pickaxe may not or cannot mine (too hard, protected), or a hazard the body cannot pass.</summary>
	Hard,
	/// <summary>Nothing to stand on within a safe fall under the floor once mined (a cavern, the open band over the lava sea).</summary>
	Deep,
	/// <summary>
	/// No block in the floor row: nothing to mine, the body comes down onto ground within a safe fall (<see cref="ShaftRow.LandFeet"/>;
	/// the feet row itself when a platform holds it there).
	/// </summary>
	Open,
}

/// <summary>One row of a straight shaft as the bot sees it before it mines it: what to mine, or why not.</summary>
public readonly record struct ShaftRow(ShaftKind Kind, IReadOnlyList<TilePos> Cells, int LandFeet, string Why);

/// <summary>
/// The straight shaft (docs M12_SPEC.md section 1; user design 2026-10-17: "a real straight shaft, as players and Baritone do it,
/// instead of pathfinder legs"): the body in a shaft two columns wide (the node's, <c>col</c> and <c>col + 1</c>), the two cells
/// under the feet mined, the body down into them, again. Before a row is mined it is read here, as a player looks at what is under
/// the shaft: what the lava beside it would do once it is open, where the body comes down, whether that is within a safe fall.
/// Seen in game (world_wof M12e, 14:45): the leg walk from (2153,865) to "row 885 within 6 of x 2155" ran out of nodes (Partial,
/// 3000) and followed a plan 70 columns west to the edge of a lava pool, where a drop overshot into it; under the shaft were
/// cobwebs (rows 867-881, the region dump nopath_2086_885), which a dig move cannot stand on or pass, not lava.
/// Terraria-free: only the grid and the move context's break times.
/// </summary>
public static class StraightShaft
{
	/// <summary>
	/// Reading the floor under a body at feet row <paramref name="feet"/> in the shaft at <paramref name="col"/>: the block cells of
	/// row feet + 1 in the two columns to mine, and where the body then comes to rest. Vanilla: lava flows sideways and down, never up
	/// (Liquid.Update), so the lava that comes in is beside or above a cell mined (<see cref="BlockRules.LetsLavaIn"/>); a body in a
	/// cobweb is slowed and its fall begins again there (Player.StickyMovement: fallStart reset, decompiled Player.cs 21290-21330),
	/// so a web under the shaft is a floor to mine, not a fall; a fall deeper than <see cref="MoveContext.MaxDrop"/> (two rows short
	/// of a safe fall) is <see cref="ShaftKind.Deep"/>.
	/// </summary>
	public static ShaftRow Below(MoveContext m, int col, int feet)
	{
		ITileGrid g = m.Grid;
		int r = feet + 1;
		var cells = new List<TilePos>(2);
		var lava = new List<TilePos>();
		for (int x = col; x <= col + 1; x++) {
			CellKind k = g.Get(x, r);
			if (k.IsBlockTile()) {
				var c = new TilePos(x, r);
				if (m.Blocks == null || float.IsInfinity(m.BreakTicks(x, r)) || float.IsNaN(m.BreakTicks(x, r)))
					return new ShaftRow(ShaftKind.Hard, new[] { c }, feet, $"{c} cannot be mined");
				cells.Add(c);
			}
			else if (k.IsLiquid() && !k.IsSolid() && LiquidOutside(g, col, new TilePos(x, r)))
				lava.Add(new TilePos(x, r));
			else if (!k.IsPassable())
				return new ShaftRow(ShaftKind.Hard, new[] { new TilePos(x, r) }, feet, $"a hazard at ({x},{r})");
		}
		foreach (TilePos c in cells) {
			if (g.Get(c.X, c.Y).IsLiquid())
				AddOnce(lava, c);
			foreach (TilePos n in new[] { new TilePos(c.X - 1, c.Y), new TilePos(c.X + 1, c.Y), new TilePos(c.X, c.Y - 1) })
				if (g.Get(n.X, n.Y).IsLiquid() && !g.Get(n.X, n.Y).IsSolid() && LiquidOutside(g, col, n))
					AddOnce(lava, n);
		}
		// Where the body comes to rest once the row is open (or from where it is, nothing to mine): the first row under it with a
		// floor (or a web) under either column.
		bool open = cells.Count == 0 && lava.Count == 0;
		int f = open ? feet : r;
		while (true) {
			CellKind a = g.Get(col, f + 1), b = g.Get(col + 1, f + 1);
			if (Stops(a) || Stops(b))
				break;
			if (a.HasLava())
				AddOnce(lava, new TilePos(col, f + 1));
			if (b.HasLava())
				AddOnce(lava, new TilePos(col + 1, f + 1));
			if (lava.Count > 0)
				break; // into lava: no need to look further down
			if (!a.IsPassable() || !b.IsPassable())
				return new ShaftRow(ShaftKind.Hard, cells, f, $"a hazard under ({col},{f})");
			f++;
			if (f - feet > m.MaxDrop)
				return new ShaftRow(ShaftKind.Deep, cells, f, $"nothing to stand on within {m.MaxDrop} rows under ({col},{r})");
		}
		if (lava.Count > 0)
			return new ShaftRow(ShaftKind.Lava, lava, f, $"lava at {string.Join(" ", lava)}");
		return open ? new ShaftRow(ShaftKind.Open, cells, f, f == feet ? "on a floor with nothing to mine" : "")
			: new ShaftRow(ShaftKind.Dig, cells, f, "");
	}

	/// <summary>
	/// A liquid cell that is not the shaft's own: outside its two columns, or in them but lava (water already in the shaft, from an
	/// earlier trip, is fallen through: a soft landing, not a pocket to step round).
	/// </summary>
	private static bool LiquidOutside(ITileGrid g, int col, TilePos c) => c.X < col || c.X > col + 1 || g.Get(c.X, c.Y).HasLava();

	/// <summary>A cell the falling body stops on: a block (whole or shaped), a platform, or a cobweb (it holds the body, slowly).</summary>
	private static bool Stops(CellKind k) => k.IsSolid() || k.IsPlatform();

	/// <summary>
	/// The next <paramref name="rows"/> rows of the shaft at <paramref name="col"/> from feet row <paramref name="feet"/> on, each read
	/// as <see cref="Below"/> reads it once the rows above it are mined: the first that is lava or too hard (null if none before a deep
	/// drop or the end). The planner does not track its own edits; neither does this, and it need not: a row reads only the rows under it.
	/// </summary>
	public static ShaftRow? TroubleAhead(MoveContext m, int col, int feet, int rows)
	{
		for (int i = 0; i < rows; i++) {
			ShaftRow row = Below(m, col, feet + i);
			if (row.Kind is ShaftKind.Lava or ShaftKind.Hard)
				return row;
			if (row.Kind == ShaftKind.Deep)
				return null;
		}
		return null;
	}

	/// <summary>
	/// The cells to mine to step the body one column sideways at its feet row, from node (<paramref name="x"/>, <paramref name="feet"/>)
	/// toward <paramref name="dir"/>, or null if that is not safe: a cell that lets lava in or drops sand onto the body
	/// (<see cref="BlockRules.Unsafe"/>), one the pickaxe may not mine, lava or a hazard in the way, or no ground under the new node.
	/// </summary>
	public static List<TilePos> SideStep(MoveContext m, int x, int feet, int dir)
	{
		ITileGrid g = m.Grid;
		int nx = x + dir, front = dir > 0 ? nx + Body.WidthTiles - 1 : nx;
		var cells = new List<TilePos>(Body.HeightTiles);
		for (int y = feet - Body.HeightTiles + 1; y <= feet; y++) {
			CellKind k = g.Get(front, y);
			if (k.IsBlockTile())
				cells.Add(new TilePos(front, y));
			else if (k.HasLava() || !k.IsPassable())
				return null;
		}
		foreach (TilePos c in cells)
			if (m.Blocks == null || float.IsInfinity(m.BreakTicks(c.X, c.Y)) || BlockRules.Unsafe(g, c, cells))
				return null;
		// No ground under the new node: a drop, into a shaft dug before (its ledges, a jog's lip), if it comes down within a safe fall.
		if (!Body.HasSupport(g, nx, feet)) {
			ShaftRow drop = Below(m, nx, feet);
			if (drop.Kind is not (ShaftKind.Open or ShaftKind.Dig) || drop.LandFeet == feet)
				return null; // (Dig: cobwebs under it, which hold the body)
		}
		return cells;
	}

	/// <summary>Can the body get from column <paramref name="from"/> to <paramref name="to"/> on its feet row, column by column (<see cref="SideStep"/>)?</summary>
	public static bool SideStepsClear(MoveContext m, int from, int to, int feet)
	{
		int dir = Math.Sign(to - from);
		for (int x = from; x != to; x += dir)
			if (SideStep(m, x, feet, dir) == null)
				return false;
		return true;
	}

	/// <summary>
	/// The lava cells joined to <paramref name="seeds"/> (4-neighbours with lava in them), counted up to <paramref name="cap"/>: the
	/// pocket's size in tiles, what that many empty buckets take out of it (one full tile a bucket, Player.ItemCheck_UseBuckets).
	/// </summary>
	public static int PocketSize(ITileGrid g, IReadOnlyList<TilePos> seeds, int cap)
	{
		var seen = new HashSet<TilePos>();
		var queue = new Queue<TilePos>();
		foreach (TilePos s in seeds)
			if (g.Get(s.X, s.Y).HasLava() && seen.Add(s))
				queue.Enqueue(s);
		while (queue.Count > 0 && seen.Count <= cap) {
			TilePos c = queue.Dequeue();
			foreach (TilePos n in new[] { new TilePos(c.X - 1, c.Y), new TilePos(c.X + 1, c.Y), new TilePos(c.X, c.Y - 1), new TilePos(c.X, c.Y + 1) })
				if (g.Get(n.X, n.Y).HasLava() && seen.Add(n))
					queue.Enqueue(n);
		}
		return seen.Count;
	}

	private static void AddOnce(List<TilePos> list, TilePos c)
	{
		if (!list.Contains(c))
			list.Add(c);
	}
}
