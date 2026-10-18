using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Building;

/// <summary>
/// Picks where a blueprint goes: the spot within <see cref="SearchRadius"/> columns whose floor row is the ground
/// under the plan's middle and that costs least (<see cref="Score"/>: the edits, blocks to place and tiles to mine, dearer
/// in the evil biome, and the walk). Sites with unbreakable or liquid cells, trees, furniture or platforms in the room, or
/// more than <see cref="MaxEdits"/> edits are skipped.
/// </summary>
public static class SiteFinder
{
	public const int SearchRadius = 40;
	public const int MaxEdits = 40;
	public const int GroundScan = 15;

	/// <summary>
	/// Best site within <see cref="Blueprint.SearchRadius"/> of <paramref name="around"/>. <paramref name="avoid"/>:
	/// origins that failed before; <paramref name="keepOut"/>: rectangles (min, max) the plan must not overlap.
	/// </summary>
	public static TilePos? Find(BotContext ctx, Blueprint bp, TilePos around, IReadOnlyCollection<TilePos> avoid = null,
		IReadOnlyList<(TilePos Min, TilePos Max)> keepOut = null)
	{
		TilePos? best = null;
		long bestScore = long.MaxValue;
		Func<int, int?> groundAt = Ground(ctx.Grid, bp, around);
		RaisedFloors raised = RaisedFloors.For(ctx, bp, around);
		// An arena's middle within the radius, either side alike: by its left edge, a 96-wide arena could never go left of the
		// house within 80 tiles (seen in game, measured run S12: "over the house" for 92 of 161 sites, none on the left tried).
		for (int x0 = around.X - bp.SearchRadius - Shift(bp); x0 <= around.X + bp.SearchRadius - Shift(bp); x0++) {
			int? ground = groundAt(x0 + bp.Width / 2);
			foreach (int floor in Floors(ground, raised?.Floor(x0, ground))) {
				var origin = new TilePos(x0, floor - (bp.Height - 1));
				if (avoid != null && avoid.Contains(origin))
					continue;
				if (raised != null && raised.OutOfWormReach(x0, floor))
					continue;
				if (keepOut != null && keepOut.Any(r => origin.X <= r.Max.X && origin.X + bp.Width - 1 >= r.Min.X
					&& origin.Y <= r.Max.Y && origin.Y + bp.Height - 1 >= r.Min.Y))
					continue;
				int edits = Edits(ctx, bp, origin);
				if (edits < 0 || edits > bp.MaxEdits)
					continue;
				long score = Score(ctx, bp, origin, edits, around);
				if (score < bestScore) {
					bestScore = score;
					best = origin;
				}
			}
		}
		return best;
	}

	/// <summary>The floor rows tried for one site: the ground under its middle, then a raised floor (<see cref="RaisedFloors"/>).</summary>
	private static IEnumerable<int> Floors(int? ground, int? raised)
	{
		if (ground is int g)
			yield return g;
		if (raised is int r && r != ground)
			yield return r;
	}

	/// <summary>
	/// The worm's arena (<see cref="Blueprint.InEvilBiome"/>) raised off the ground, as a player bridges the corruption with a
	/// long floor in the air: for each site, the floor row from the highest block of the ground under its span down to the row
	/// over the ground under its middle whose build has the fewest edits (blocks to mine in its open air, two each, and floor
	/// cells to fill, one each), with no unbreakable block or liquid in its open air and a block of the ground in its floor
	/// row or under it to place the first floor block against (BuildSkill: each block next to a solid one). The floor on the
	/// ground's highest block always has one, and its open air is all air: the evil biome's stone the pickaxe cannot break
	/// (ebonstone, pick power 65; BotContext.EvilStoneTooHard) then stays under the floor rather than ruling the site out.
	/// Seen in game (measured run M10o, world_eow, Silver Pickaxe, power 45): on the floor row under each middle, "unbreakable
	/// tile 25" ruled out 132 of the 161 sites within 80 of (1870,305) and 208 of the 321 within 160, liquid 95 and too many
	/// edits 15 of the rest, no arena was built, and the worm was fought in a three-row gully under a hill at (1870,305): two
	/// deaths, the worm left at 6636 and 4264 of 10050. The Eye's arena keeps to the ground (its site finding is unchanged).
	/// </summary>
	private sealed class RaisedFloors
	{
		private readonly Blueprint bp;
		private readonly int cx0, cy0, rows;
		/// <summary>Per column, prefix counts down the rows: blocks, and cells that rule out open air (unbreakable, liquid).</summary>
		private readonly int[][] blocks, bad;
		private readonly bool[][] liquid;
		private readonly int?[] top;
		/// <summary>Per column and row, the first row at or below it with a block (relative to cy0), or rows if none.</summary>
		private readonly int[][] nextBlock;
		/// <summary>The worm's reach over the ground (<see cref="WormPattern.ReachRows"/>).</summary>
		public int ReachRows { get; }
		/// <summary>The evil biome's boss comes up from the ground (the Eater of Worlds), not in a Crimson world.</summary>
		private readonly bool wormComes;

		public static RaisedFloors For(BotContext ctx, Blueprint bp, TilePos around) =>
			!bp.IsHouse && bp.RaisedFloor ? new RaisedFloors(ctx, bp, around) : null;

		private RaisedFloors(BotContext ctx, Blueprint bp, TilePos around)
		{
			this.bp = bp;
			ReachRows = WormPattern.ReachRows(ctx.Expert);
			// The worm's reach rules out a floor only in a Corruption world: the Brain of Cthulhu flies and teleports to us (BrainPattern),
			// a floor high over the crimson's ground is no worse for it.
			wormComes = !ctx.Ids.Crimson;
			int reach = bp.SearchRadius;
			// The columns of every site's span and one either side; the rows as far up and down as the ground is followed
			// (Ground), and the open air over the highest floor.
			cx0 = around.X - reach - Shift(bp) - 1;
			int cols = 2 * reach + bp.Width + 3;
			cy0 = around.Y - reach - bp.Height;
			rows = 2 * reach + bp.Height + 2;
			blocks = new int[cols][];
			bad = new int[cols][];
			liquid = new bool[cols][];
			top = new int?[cols];
			nextBlock = new int[cols][];
			ITileGrid g = ctx.Grid;
			for (int i = 0; i < cols; i++) {
				int x = cx0 + i;
				blocks[i] = new int[rows + 1];
				bad[i] = new int[rows + 1];
				liquid[i] = new bool[rows];
				for (int j = 0; j < rows; j++) {
					int y = cy0 + j;
					CellKind k = g.Get(x, y);
					bool block = k.IsBlock() && !ctx.World.IsTreeTrunk(x, y);
					liquid[i][j] = k.IsLiquid();
					bool hard = block && !(bp.ChopTrees && ctx.World.IsTreeTrunk(x, y - 1)) // as Edits: felled first, then mined
						&& float.IsInfinity(ctx.Blocks?.BreakTicks(x, y) ?? float.PositiveInfinity);
					blocks[i][j + 1] = blocks[i][j] + (block ? 1 : 0);
					bad[i][j + 1] = bad[i][j] + (hard || liquid[i][j] ? 1 : 0);
					if (block && top[i] == null)
						top[i] = y;
				}
				nextBlock[i] = new int[rows + 1];
				nextBlock[i][rows] = rows;
				for (int j = rows - 1; j >= 0; j--)
					nextBlock[i][j] = blocks[i][j + 1] != blocks[i][j] ? j : nextBlock[i][j + 1];
			}
		}

		/// <summary>
		/// Is the floor at <paramref name="floor"/> for the site whose left edge is <paramref name="x0"/> higher over the
		/// ground under any of its columns (the first block below the floor row) than the worm reaches
		/// (<see cref="ReachRows"/>)? The kiting goes the floor's whole length, and the worm comes up at the bot from the
		/// ground under it.
		/// </summary>
		public bool OutOfWormReach(int x0, int floor)
		{
			if (!bp.InEvilBiome || !wormComes)
				return false; // only the worm comes up from the ground; Skeletron's arena over the lake flies free of it
			int i0 = x0 - cx0, jf = floor - cy0;
			if (jf < 0 || jf + 1 >= rows)
				return false;
			for (int i = i0; i < i0 + bp.Width; i++)
				if (nextBlock[i][jf + 1] - jf > ReachRows)
					return true;
			return false;
		}

		private static int Count(int[] prefix, int j0, int j1) => prefix[j1 + 1] - prefix[j0];

		/// <summary>The raised floor row for the site whose left edge is <paramref name="x0"/>, or null if there is none.</summary>
		public int? Floor(int x0, int? ground)
		{
			int i0 = x0 - cx0, open = bp.Height - 1;
			int? highest = null;
			for (int i = i0; i < i0 + bp.Width; i++)
				if (top[i] is int t && (highest == null || t < highest))
					highest = t;
			if (highest is not int hi)
				return null;
			int lowest = ground is int gr ? gr - 1 : hi;
			int? best = null;
			int bestEdits = int.MaxValue;
			for (int floor = lowest; floor >= hi; floor--) {
				int jf = floor - cy0, ja = jf - open;
				if (ja < 0 || jf + 1 >= rows)
					continue;
				int edits = 0;
				bool ok = true, attached = blocks[i0 - 1][jf + 1] != blocks[i0 - 1][jf] || blocks[i0 + bp.Width][jf + 1] != blocks[i0 + bp.Width][jf];
				for (int i = i0; i < i0 + bp.Width && ok; i++) {
					if (Count(bad[i], ja, jf - 1) > 0 || liquid[i][jf]) {
						ok = false;
						break;
					}
					edits += 2 * Count(blocks[i], ja, jf - 1) + (Count(blocks[i], jf, jf) == 0 ? 1 : 0);
					attached |= Count(blocks[i], jf, jf + 1) > 0;
				}
				if (ok && attached && edits < bestEdits && !OutOfWormReach(x0, floor)) {
					bestEdits = edits;
					best = floor;
				}
			}
			return best;
		}
	}

	/// <summary>
	/// A site's cost: its build steps (edits and items to place) at <see cref="StepScore"/> each, times
	/// <see cref="EvilStepFactor"/> in the evil biome, plus the columns from <paramref name="around"/> to its middle. For an
	/// arena wanted in the evil biome (<see cref="Blueprint.InEvilBiome"/>, the Eater of Worlds') the factor is inverted: off
	/// the biome it is the dear one, a cost and not a ban (what the map shows of the zone undercounts it), and a thin zone
	/// round its middle costs more (<see cref="ZoneShortfall"/>).
	/// </summary>
	public static long Score(BotContext ctx, Blueprint bp, TilePos origin, int edits, TilePos around)
	{
		long steps = edits + bp.Items.Count;
		if (!bp.IsHouse && InEvilBiome(ctx, bp, origin) != bp.InEvilBiome)
			steps *= EvilStepFactor;
		if (!bp.IsHouse && bp.InEvilBiome)
			steps += ZoneShortfall(ctx, bp, origin);
		steps += SkyBlocks(ctx, bp, origin) * 2;
		return steps * StepScore + Math.Abs(origin.X + Shift(bp) - around.X);
	}

	/// <summary>
	/// How far above the zone's threshold (<see cref="BotContext.CorruptionZoneTiles"/>) the worm's arena wants the count round
	/// its middle once built (<see cref="CorruptionOnceBuilt"/>): the slope the game's count was seen to fall by round the
	/// M10 arena (<see cref="ZoneSlopePerColumn"/>) over the columns the kiting goes from the fight's centre before turning
	/// (<see cref="BossFightSkill.ArenaHalfWidth"/>).
	/// </summary>
	public const int ZoneMargin = ZoneSlopePerColumn * BossFightSkill.ArenaHalfWidth;

	/// <summary>
	/// The game's Corruption count, read from the measured run M10m's checkpoint worlds (world_eow) along the arena's stand row
	/// 245: 358 at its middle (1920) before the build and 346 after it, 300 at x 1932 and 291 at 1936; 46 over 12 columns, about
	/// 4 a column. The bot's own count there was 306, and the fights (M10m, M10n) kited east past 1932 and lost the worm.
	/// </summary>
	public const int ZoneSlopePerColumn = 4;

	/// <summary>
	/// The worm's arena's cost for too thin a zone: a build step for each tile the count round its middle once built falls
	/// short of <see cref="ZoneMargin"/> over the threshold, at most <see cref="ZoneMargin"/> steps (a middle under the
	/// threshold is off the zone, <see cref="EvilStepFactor"/>). A preference, not a ban: the M10 site (306, 6 over) costs 74
	/// steps more than one with the margin.
	/// </summary>
	public static int ZoneShortfall(BotContext ctx, Blueprint bp, TilePos origin) =>
		Math.Clamp(ZoneMargin - (CorruptionOnceBuilt(ctx, bp, origin, Middle(bp, origin)) - BotContext.CorruptionZoneTiles), 0, ZoneMargin);

	/// <summary>Where a player stands in the arena's middle (the fight's centre, and where the zone is counted).</summary>
	public static TilePos Middle(Blueprint bp, TilePos origin) => new(origin.X + bp.Width / 2, origin.Y + bp.StandRow);

	/// <summary>
	/// Blocks over the plan, in the rows up to <see cref="Blueprint.SkyRows"/> over its floor (0 when it wants none): a site
	/// pays two build steps for each (<see cref="Score"/>), as <see cref="Edits"/> counts a block to mine in the plan's open air.
	/// A cost, not a ban: under the Dungeon's porch roof (rows 182-186 over its floor at 201, docs M11_SPEC.md section 0) the
	/// head hovers inside the bricks, where arrows stop and it does not.
	/// </summary>
	public static int SkyBlocks(BotContext ctx, Blueprint bp, TilePos origin)
	{
		int floor = origin.Y + bp.Height - 1, n = 0;
		for (int x = origin.X; bp.SkyRows > 0 && x < origin.X + bp.Width; x++)
			for (int y = floor - bp.SkyRows; y < origin.Y; y++)
				if (ctx.Grid.Get(x, y).IsBlock())
					n++;
		return n;
	}

	/// <summary>
	/// <see cref="BotContext.CorruptionAround"/> round <paramref name="t"/> once the plan at <paramref name="origin"/> is built:
	/// without the seen tiles in its open cells, which the build mines or clears (the floor's blocks it keeps, and what it
	/// places there, dirt or stone, counts nothing). Built already, nothing is left there to take off. Seen in game (measured
	/// run M10m, its checkpoint worlds before and after the build): the arena at (1872,239) took 12-15 off the game's count
	/// round its stand row.
	/// </summary>
	public static int CorruptionOnceBuilt(BotContext ctx, Blueprint bp, TilePos origin, TilePos t)
	{
		int n = ctx.CorruptionAround(t);
		var (x0, x1, y0, y1) = BotContext.CorruptionCounted(t);
		for (int x = Math.Max(x0, origin.X); x <= Math.Min(x1, origin.X + bp.Width - 1); x++)
			for (int y = Math.Max(y0, origin.Y); y <= Math.Min(y1, origin.Y + bp.Height - 1); y++) {
				PlanCell c = bp.At(x - origin.X, y - origin.Y);
				if (c != PlanCell.None && c != PlanCell.Block)
					n -= ctx.CorruptionWeightAt(x, y);
			}
		return n;
	}

	/// <summary>
	/// A build step's cost in columns walked: an edit took about 47 ticks in the arena builds off the evil biome (measured runs
	/// U2, U10 and U20: 949 edits in 45,000 ticks) and a column walked is a 5-tick Walk step, so a step is worth about 10.
	/// </summary>
	public const int StepScore = 10;

	/// <summary>
	/// How much more a build step costs in the evil biome. Seen in game (measured runs U14 and U23, the same site (1896,241) on
	/// the corruption's edge): 186 and 196 edits, 91,800 ticks each and not done ("pausing Build to fight", corrupt thorns,
	/// the walk under the floor), at least 468 ticks an edit against 47 off it. The Eye fight there has the zone's spawns too
	/// (vanilla NPC.SpawnNPC in ZoneCorrupt/ZoneCrimson: spawnRate x0.65, maxSpawns x1.3): eaters of souls with the boss.
	/// </summary>
	public const int EvilStepFactor = 10;

	/// <summary>
	/// Is the arena's middle, where the bot stands to fight, on the evil biome's ground (<see cref="BotContext.InEvilBiome"/>)
	/// or in the game's Corruption zone (<see cref="BotContext.CorruptionAround"/>, its spawns)?
	/// </summary>
	public static bool InEvilBiome(BotContext ctx, Blueprint bp, TilePos origin)
	{
		var mid = Middle(bp, origin);
		if (!bp.InEvilBiome)
			return ctx.InEvilBiome(mid) || ctx.CorruptionAround(mid) >= BotContext.CorruptionZoneTiles;
		// The worm's arena: the zone as it will be once built (CorruptionOnceBuilt), the tiles its build clears not counted.
		int Zone(TilePos t) => CorruptionOnceBuilt(ctx, bp, origin, t);
		if (!ctx.InEvilBiome(mid) && Zone(mid) < BotContext.CorruptionZoneTiles)
			return false;
		// The Eater of Worlds' arena: the Corruption zone (vanilla SceneMetrics.CorruptionTileThreshold, 300 in the scan round
		// the player) must hold over the whole floor, its ends too, where the kiting takes the bot; the worm dives and despawns
		// once no player is in it. Seen in game (measured run M10m, world_eow): the arena at (1872,239) had 306 corruption tiles
		// round its middle; kiting east to (1935,245) the bot left the zone, and "the worm left: no segment in view for 320
		// ticks, not defeated, its bar empty", the Worm Food spent.
		var west = new TilePos(origin.X + 1, mid.Y);
		var east = new TilePos(origin.X + bp.Width - 2, mid.Y);
		return Zone(west) >= BotContext.CorruptionZoneTiles && Zone(east) >= BotContext.CorruptionZoneTiles;
	}

	/// <summary>Arenas are searched by their middle (see <see cref="Find"/>); houses, small, by their left edge as before.</summary>
	private static int Shift(Blueprint bp) => bp.IsHouse ? 0 : bp.Width / 2;

	/// <summary>First standable floor row in the column, scanning down from <see cref="GroundScan"/> above <paramref name="y"/>.</summary>
	private static int? GroundAt(ITileGrid g, int x, int y)
	{
		for (int gy = y - GroundScan; gy <= y + GroundScan; gy++)
			if (g.Get(x, gy).IsBlock() && !g.Get(x, gy - 1).IsBlock())
				return gy;
		return null;
	}

	/// <summary>
	/// The ground row under each column a site's middle may be over. A house, small and by the spawn: within
	/// <see cref="GroundScan"/> rows of <paramref name="around"/>. An arena: the ground's surface followed out from
	/// <paramref name="around"/> column by column, as a player walks it, up a hill to its top and down a slope or a cliff to its
	/// foot, at most as far up or down as the search goes sideways (<see cref="Blueprint.SearchRadius"/>). Seen in game
	/// (measured runs U14 and U23): with the house on a hilltop (roof row 263, floor 268) and the ground round it at rows
	/// 283-285, the rows 248..278 round the roof had "no ground under the middle" for 128 of 161 sites within 80 and 215 of
	/// 321 within 160; the only site left was on the corruption's edge, and the arena was never built.
	/// </summary>
	private static Func<int, int?> Ground(ITileGrid g, Blueprint bp, TilePos around)
	{
		if (bp.IsHouse)
			return x => GroundAt(g, x, around.Y);
		int reach = bp.SearchRadius;
		var rows = new Dictionary<int, int?>();
		int? here = Follow(g, around.X, around.Y, around.Y, reach);
		rows[around.X] = here;
		foreach (int dir in new[] { -1, 1 }) {
			int from = here ?? around.Y;
			for (int x = around.X + dir; Math.Abs(x - around.X) <= reach; x += dir) {
				int? gy = Follow(g, x, from, around.Y, reach);
				rows[x] = gy;
				if (gy is int r)
					from = r;
			}
		}
		return x => rows.TryGetValue(x, out int? r) ? r : null;
	}

	/// <summary>
	/// The ground's surface in column <paramref name="x"/> next to a column whose ground is at row <paramref name="from"/>: up
	/// through the solid to its top, or down through the open to the first block; null past <paramref name="reach"/> rows from
	/// <paramref name="anchorY"/>.
	/// </summary>
	private static int? Follow(ITileGrid g, int x, int from, int anchorY, int reach)
	{
		int gy = from;
		if (g.Get(x, gy).IsBlock()) {
			while (g.Get(x, gy - 1).IsBlock())
				if (anchorY - --gy > reach)
					return null;
		}
		else {
			while (!g.Get(x, gy).IsBlock())
				if (++gy - anchorY > reach)
					return null;
		}
		return gy;
	}

	/// <summary>
	/// Why no site was found, in one line for the log: for each origin tried, the first reason it was skipped, counted, with
	/// the cell that ruled out the most sites for that reason ("a tree in the floor 29 (14 at (1986,283))"), and the fewest
	/// edits of any site that could be built. Seen in game (measured run U9): "a tree in the floor 29, unbreakable tile 2
	/// under 18 18" said what but not where: it took a parse of the saved world to find the trees and work benches.
	/// </summary>
	public static string Explain(BotContext ctx, Blueprint bp, TilePos around, IReadOnlyList<(TilePos Min, TilePos Max)> keepOut = null,
		IReadOnlyCollection<TilePos> avoid = null)
	{
		var why = new Dictionary<string, int>();
		var where = new Dictionary<string, Dictionary<TilePos, int>>();
		void Count(string r, TilePos? at = null)
		{
			why[r] = why.GetValueOrDefault(r) + 1;
			if (at is TilePos c) {
				if (!where.TryGetValue(r, out var cells))
					where[r] = cells = new Dictionary<TilePos, int>();
				cells[c] = cells.GetValueOrDefault(c) + 1;
			}
		}
		int fewest = int.MaxValue;
		TilePos fewestAt = default;
		Func<int, int?> groundAt = Ground(ctx.Grid, bp, around);
		RaisedFloors raised = RaisedFloors.For(ctx, bp, around);
		for (int x0 = around.X - bp.SearchRadius - Shift(bp); x0 <= around.X + bp.SearchRadius - Shift(bp); x0++) {
			int? ground = groundAt(x0 + bp.Width / 2);
			if (ground == null)
				Count("no ground under the middle");
			int? up = raised?.Floor(x0, ground);
			if (raised != null && up == null)
				Count("no raised floor");
			foreach (int floor in Floors(ground, up)) {
				// The worm's arena: the raised floors' reasons counted apart from the ground's.
				string kind = floor == ground ? "" : "raised: ";
				var origin = new TilePos(x0, floor - (bp.Height - 1));
				if (avoid != null && avoid.Contains(origin)) {
					Count(kind + "given up before");
					continue;
				}
				if (raised != null && raised.OutOfWormReach(x0, floor)) {
					Count(kind + $"over ground more than {raised.ReachRows} rows down (the worm's reach)", new TilePos(x0 + bp.Width / 2, floor));
					continue;
				}
				if (keepOut != null && keepOut.Any(r => origin.X <= r.Max.X && origin.X + bp.Width - 1 >= r.Min.X
					&& origin.Y <= r.Max.Y && origin.Y + bp.Height - 1 >= r.Min.Y)) {
					Count(kind + "over the house");
					continue;
				}
				int edits = Edits(ctx, bp, origin, out string reason, out TilePos? cell);
				if (edits < 0) {
					Count(kind + reason, cell);
					continue;
				}
				bool evil = !bp.IsHouse && InEvilBiome(ctx, bp, origin);
				Count(kind + (edits > bp.MaxEdits ? "too many edits" : bp.IsHouse || evil == bp.InEvilBiome ? "fine"
					: evil ? "fine, in the evil biome" : "fine, off the evil biome"));
				if (edits < fewest) {
					fewest = edits;
					fewestAt = origin;
				}
			}
		}
		string Most(string r) => where.TryGetValue(r, out var cells) && cells.MaxBy(kv => kv.Value) is var (c, n) ? $" ({n} at {c})" : "";
		return string.Join(", ", why.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}{Most(kv.Key)}"))
			+ (fewest < int.MaxValue ? $"; fewest edits {fewest} at {fewestAt}" : "");
	}

	/// <summary>Number of edits to build here, or -1 if it cannot be built here.</summary>
	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin) => Edits(ctx, bp, origin, out _);

	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin, out string reason) => Edits(ctx, bp, origin, out reason, out _);

	/// <summary>As <see cref="Edits(BotContext, Blueprint, TilePos)"/>, with why a site cannot be built and the cell that rules it out.</summary>
	public static int Edits(BotContext ctx, Blueprint bp, TilePos origin, out string reason, out TilePos? cell)
	{
		reason = null;
		cell = null;
		ITileGrid g = ctx.Grid;
		int edits = 0;
		for (int y = 0; y < bp.Height; y++) {
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				if (c == PlanCell.None)
					continue;
				int wx = origin.X + x, wy = origin.Y + y;
				CellKind k = g.Get(wx, wy);
				cell = new TilePos(wx, wy);
				if (k.IsLiquid()) {
					reason = "liquid";
					return -1;
				}
				// Where the body would be in the Dungeon before Skeletron is beaten: every spawn there is a Guardian (BotContext.InGuardianZone).
				if (c != PlanCell.Block && ctx.InGuardianZone(wx, wy)) {
					reason = "the Dungeon Guardian's ground";
					return -1;
				}
				if (ctx.World.IsTreeTrunk(wx, wy)) {
					if (!bp.ChopTrees) {
						reason = c == PlanCell.Block ? "a tree in the floor" : "a tree in the room";
						return -1;
					}
					// Felled with the axe; in the floor row (a tree standing lower, its trunk up through the floor) the floor block
					// then goes in where it stood. Seen in game (measured run U9): trees at (1986,283), (2124,284) and (2166,285),
					// in dips beside the house, ruled out 29 of the 47 arena sites clear of the house, and auto mode stopped.
					edits += c == PlanCell.Block ? 2 : 1;
					continue;
				}
				if (c == PlanCell.Block) {
					if (k.IsPlatform() && !k.IsShaped()) {
						reason = "a platform in the floor";
						return -1;
					}
					if (!k.IsBlock())
						edits++;
				}
				else if (k.IsBlock()) {
					// A slope or half block of the ground is a block to mine, like any other: seen in game (measured run S10),
					// read as platforms they ruled out 122 of 161 arena sites round the house, and the boss plan stopped.
					// A block under a tree cannot be mined while the tree stands; once it is felled, it can.
					bool underTree = bp.ChopTrees && ctx.World.IsTreeTrunk(wx, wy - 1);
					if (!underTree && float.IsInfinity(ctx.Blocks?.BreakTicks(wx, wy) ?? float.PositiveInfinity)) {
						reason = $"unbreakable tile {ctx.World.TileTypeAt(wx, wy)} under {ctx.World.TileTypeAt(wx, wy - 1)}";
						return -1;
					}
					edits += 2;
				}
				// Furniture in an arena's open air (a work bench, a table: solid-topped, read as platforms) stays and does no harm:
				// seen in game (a run from U7's arena checkpoint), the bot's own benches and tables ruled out 94 of 161 sites, and
				// the one left was then over the house. A house's room must be clear.
				else if (bp.IsHouse && (k.IsPlatform() || ctx.World.TileTypeAt(wx, wy) is int t && t >= 0 && ctx.Seen.Stations.ContainsKey(t))) {
					reason = k.IsPlatform() ? $"a platform (tile {ctx.World.TileTypeAt(wx, wy)}) in the room" : "a station in the room";
					return -1; // furniture or a station in the way
				}
			}
		}
		cell = null;
		return edits;
	}
}

/// <summary>
/// Builds a <see cref="Blueprint"/> at a site, the way a player would, one swing at a time:
/// <list type="number">
/// <item>fell trees in the plan's cells, the floor row's too (arenas);</item>
/// <item>mine everything solid where the room, doorway and furniture go (top-down);</item>
/// <item>place blocks for the frame (bottom-up, each next to an existing tile);</item>
/// <item>put walls behind the room;</item>
/// <item>place the furniture (table, chair, torch), never standing where it goes;</item>
/// <item>step outside and place the door;</item>
/// <item>run the game's housing check on the room (houses).</item>
/// </list>
/// While it runs, every cell of the plan is reserved (<see cref="BotContext.Reserved"/>), so walking there never
/// digs through the frame or pillars up inside the room; an arena's floor blocks are rebuilt instead
/// (<see cref="Blueprint.RebuildBlocks"/>: a walk may take one out at the cost of putting it back). An action that has no effect after
/// <see cref="MaxTicksPerAction"/> ticks in range, or whose spot cannot be reached, counts as a failure; three
/// on the same cell fail the skill.
/// </summary>
public sealed class BuildSkill : ISkill
{
	public const int MaxTicksPerAction = 90;
	public const int MaxFailuresPerCell = 3;
	/// <summary>Ticks to wait for hotbar stocking when the hotbar block stack ran out but the backpack has more.</summary>
	public const int MaxWaitForBlocks = 90;
	private int waitForBlocks;

	private enum Kind { Chop, Mine, Block, Wall, Item, Wait }

	private readonly record struct Action(Kind Kind, TilePos Cell, TilePos Aim, int Item, (TilePos Min, TilePos Max)? Keep, bool Outside, int Face = 0);

	private readonly Blueprint bp;
	private readonly Dictionary<TilePos, int> failures = new();
	private HashSet<TilePos> reserved;
	private HashSet<TilePos> rebuilt;
	private WalkToSkill walk;
	private ChopTreeSkill chop;
	private Action? current;
	private int actionTicks;

	public TilePos Origin { get; }
	public Blueprint Blueprint => bp;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Build";
	public string Key => $"Build {bp.GetType().Name} {Origin} {bp.Width}x{bp.Height}";

	public BuildSkill(Blueprint blueprint, TilePos origin)
	{
		bp = blueprint;
		Origin = origin;
	}

	public string Describe() => $"Build {bp.Name} at {Origin} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		reserved = new HashSet<TilePos>();
		rebuilt = bp.RebuildBlocks ? new HashSet<TilePos>() : null;
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				bool itemCell = bp.Items.Any(it => it.Covers(x, y));
				if (rebuilt != null && c == PlanCell.Block && !itemCell)
					rebuilt.Add(new TilePos(Origin.X + x, Origin.Y + y));
				else if (c != PlanCell.None && (!bp.ReserveBlocksOnly || c == PlanCell.Block || itemCell))
					reserved.Add(new TilePos(Origin.X + x, Origin.Y + y));
			}
		ctx.Reserved = reserved;
		ctx.Rebuilt = rebuilt;
		ctx.Log.Info($"Build start: {bp.Name} at {Origin} ({bp.Width}x{bp.Height})");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		chop?.Stop(ctx);
		chop = null;
		if (ReferenceEquals(ctx.Reserved, reserved))
			ctx.Reserved = null;
		if (rebuilt != null && ReferenceEquals(ctx.Rebuilt, rebuilt))
			ctx.Rebuilt = null;
		// The build changed the tiles there: what the chunks round it count (the Corruption zone's tiles among them) is stale.
		ctx.RecountChunks(Origin.X, Origin.Y, Origin.X + bp.Width - 1, Origin.Y + bp.Height - 1);
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
		chop?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		walk?.Resume(ctx);
		chop?.Resume(ctx);
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		Action? next = NextAction(ctx, out string problem);
		if (problem != null)
			return Fail(ctx, problem);
		if (next == null) {
			if (bp.IsHouse) {
				string why = ctx.World.CheckHousing(Origin.X + bp.Inside.X, Origin.Y + bp.Inside.Y);
				if (why != null)
					return Fail(ctx, "built, but the housing check says: " + why);
			}
			ctx.Log.Info($"Build done: {bp.Name} at {Origin}{(bp.IsHouse ? " is valid housing" : "")}");
			State = "done";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		Action a = next.Value;
		if (a.Kind == Kind.Wait) {
			State = "waiting for blocks on the hotbar";
			return SkillStatus.Running;
		}
		if (current != a) {
			current = a;
			actionTicks = 0;
			walk?.Stop(ctx);
			walk = null;
		}

		if (a.Kind == Kind.Chop) {
			// Trees take several swings (and their own reach rules): the tree-felling skill does it.
			if (chop == null || chop.Base != a.Cell) {
				chop?.Stop(ctx);
				chop = new ChopTreeSkill(a.Cell);
				chop.Start(ctx);
			}
			State = $"felling the tree at {a.Cell}";
			SkillStatus cs = chop.Tick(ctx);
			if (cs == SkillStatus.Running)
				return SkillStatus.Running;
			string why = chop.FailureReason;
			chop.Stop(ctx);
			chop = null;
			return cs == SkillStatus.Failed ? Failed(ctx, a.Cell, why) : SkillStatus.Running;
		}

		var reach = new GoalReachTile(a.Aim, ctx.Physics) { Exclude = n => Excluded(a, n) };
		TilePos node = ctx.Node;
		if (walk == null && ctx.Body.Grounded && reach.IsGoal(node)) {
			if (++actionTicks > MaxTicksPerAction)
				return Failed(ctx, a.Cell, $"no effect after {MaxTicksPerAction} ticks");
			State = $"{a.Kind} {a.Cell}";
			// Turned the way the item is to face first (a chair to its table): a tap towards it, as a player turns (Player.direction
			// follows velocity.X between swings). User report (2026-10-17): "the chair is facing away from the table".
			if (a.Face != 0 && ctx.Combat.Facing != a.Face) {
				if (ctx.Combat.ItemAnimation == 0)
					ctx.Output.Horizontal = a.Face;
				return SkillStatus.Running;
			}
			ctx.Output.UseItem = true;
			ctx.Output.Aim(a.Aim.X, a.Aim.Y);
			switch (a.Kind) {
					case Kind.Mine: ctx.Output.Tool = ToolRequest.Pickaxe; break;
				case Kind.Block: ctx.Output.Tool = ToolRequest.Block; break;
				default:
					ctx.Output.Tool = ToolRequest.Item;
					ctx.Output.ToolItem = a.Item;
					break;
			}
			return SkillStatus.Running;
		}
		if (walk == null) {
			walk = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 2 };
			walk.Start(ctx);
		}
		State = $"walking to {a.Kind} {a.Cell}";
		SkillStatus w = walk.Tick(ctx);
		if (w == SkillStatus.Failed) {
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			return Failed(ctx, a.Cell, "cannot get there: " + why);
		}
		if (w == SkillStatus.Succeeded) {
			walk.Stop(ctx);
			walk = null;
		}
		return SkillStatus.Running;
	}

	/// <summary>Standing spots that would block the action: inside the footprint, or inside the house for the door.</summary>
	private bool Excluded(Action a, TilePos n)
	{
		int bx0 = n.X, bx1 = n.X + Body.WidthTiles - 1, by0 = n.Y - Body.HeightTiles + 1, by1 = n.Y;
		if (a.Keep is var (min, max) && bx1 >= min.X && bx0 <= max.X && by1 >= min.Y && by0 <= max.Y)
			return true;
		return a.Outside && bx1 >= Origin.X && bx0 < Origin.X + bp.Width && by0 < Origin.Y + bp.Height && by1 >= Origin.Y;
	}

	/// <summary>Optional items that would not go down, left out of this build.</summary>
	private readonly HashSet<PlanItem> leftOut = new();

	/// <summary>The cell whose action failed <see cref="MaxFailuresPerCell"/> times and failed the build, if that is why.</summary>
	public TilePos? FailedCell { get; private set; }

	private SkillStatus Failed(BotContext ctx, TilePos cell, string why)
	{
		int n = failures.GetValueOrDefault(cell) + 1;
		failures[cell] = n;
		ctx.Log.Info($"Build: {current?.Kind} {cell} failed ({why}), attempt {n}/{MaxFailuresPerCell}");
		current = null;
		walk?.Stop(ctx);
		walk = null;
		if (n < MaxFailuresPerCell)
			return SkillStatus.Running;
		// An optional item (an arena's campfire or torch) that will not go down is left out, not the build failed. Seen in game
		// (measured run M10l, world_eow): the arena in the corruption at (1872,239), its campfire at (1919,244) "failed (no
		// effect after 90 ticks)" 3 times, the build failed and the site was given up, no other site found, and the worm was
		// fought on open ground and killed the bot.
		if (bp.Items.FirstOrDefault(it => it.Optional && new TilePos(Origin.X + it.X, Origin.Y + it.Y) == cell) is PlanItem extra && extra.ItemType > 0) {
			leftOut.Add(extra);
			ctx.Log.Info($"Build: leaving the {ctx.Knowledge?.ItemName(extra.ItemType) ?? extra.ItemType.ToString()} at {cell} out ({why}, {n} tries): the {bp.Name} works without it");
			return SkillStatus.Running;
		}
		FailedCell = cell;
		return Fail(ctx, $"{why} at {cell}");
	}

	/// <summary>The next thing to do, in phase order; null when the plan is complete. Sets <paramref name="problem"/> if stuck.</summary>
	private Action? NextAction(BotContext ctx, out string problem)
	{
		problem = null;
		ITileGrid g = ctx.Grid;
		IWorldInfo w = ctx.World;
		TilePos node = ctx.Node;
		var placedItems = bp.Items.Where(it => IsPlaced(ctx, it)).ToList();
		bool CoveredByPlaced(int x, int y) => placedItems.Any(it => it.Covers(x - Origin.X, y - Origin.Y));

		// Stick with the current target while it still needs doing: re-picking "the nearest" every tick makes the bot
		// step off a cell it stands on, which makes the next cell the nearest one, and so on (oscillation).
		if (current is Action cur && StillNeeded(ctx, cur, CoveredByPlaced))
			return cur;

		// 0. Fell trees standing in the plan's cells (at the trunk's lowest tile; the whole tree comes down): the open air, and
		// the floor row too, where a tree standing lower has its trunk (SiteFinder counts it as felled and filled in).
		if (bp.ChopTrees) {
			TilePos? tree = null;
			int treeDist = int.MaxValue;
			for (int x = 0; x < bp.Width; x++)
				for (int y = 0; y < bp.Height; y++) {
					int wx = Origin.X + x, wy = Origin.Y + y;
					if (bp.At(x, y) != PlanCell.None && w.IsTreeTrunk(wx, wy) && Math.Abs(wx - node.X) < treeDist) {
						treeDist = Math.Abs(wx - node.X);
						tree = TreeBase(w, wx, wy);
						break;
					}
				}
			if (tree is TilePos t) {
				if (ctx.AxePower <= 0) {
					problem = $"a tree at {t} is in the way and there is no axe";
					return null;
				}
				return new Action(Kind.Chop, t, t, 0, null, false);
			}
		}

		// 1. Mine the room, doorway and furniture cells clear (top-down, nearest first).
		Action? best = null;
		for (int y = 0; y < bp.Height && best == null; y++) {
			int bestDist = int.MaxValue;
			for (int x = 0; x < bp.Width; x++) {
				PlanCell c = bp.At(x, y);
				int wx = Origin.X + x, wy = Origin.Y + y;
				if (c is PlanCell.Wall or PlanCell.Clear && g.Get(wx, wy).IsBlock() && !CoveredByPlaced(wx, wy)) {
					int d = Math.Abs(wx - node.X);
					if (d < bestDist) {
						bestDist = d;
						var cell = new TilePos(wx, wy);
						best = new Action(Kind.Mine, cell, cell, 0, null, false);
					}
				}
			}
		}
		if (best != null)
			return best;

		// 2. Frame blocks, bottom-up, each one next to something already there.
		List<TilePos> missingBlocks = Cells(PlanCell.Block).Where(c => !g.Get(c.X, c.Y).IsBlock()).ToList();
		if (missingBlocks.Count > 0) {
			if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0) {
				// The hotbar stack ran out; if more blocks are in the backpack, hotbar stocking brings them up shortly.
				if (ctx.Inventory.CountAny(ctx.Ids.BuildingBlocks) > 0 && ++waitForBlocks <= MaxWaitForBlocks)
					return new Action(Kind.Wait, node, node, 0, null, false);
				problem = $"out of blocks ({missingBlocks.Count} frame cells left)";
				return null;
			}
			waitForBlocks = 0;
			TilePos? b = missingBlocks.Where(c => HasSolidNeighbour(g, c)).OrderByDescending(c => c.Y).ThenBy(c => Math.Abs(c.X - node.X)).Cast<TilePos?>().FirstOrDefault();
			if (b == null) {
				problem = "frame cells with nothing to attach to";
				return null;
			}
			return new Action(Kind.Block, b.Value, b.Value, 0, (b.Value, b.Value), false);
		}

		// 3. Walls behind the room.
		List<TilePos> missingWalls = Cells(PlanCell.Wall).Where(c => !w.HasWall(c.X, c.Y)).ToList();
		if (missingWalls.Count > 0) {
			int wallItem = bp.WallItems.FirstOrDefault(i => ctx.Inventory.Count(i) > 0, -1);
			if (wallItem < 0) {
				problem = $"out of walls ({missingWalls.Count} left)";
				return null;
			}
			TilePos? c = missingWalls.Where(m => w.CanPlaceWall(wallItem, m.X, m.Y)).OrderBy(m => Math.Abs(m.X - node.X) + Math.Abs(m.Y - node.Y)).Cast<TilePos?>().FirstOrDefault();
			if (c == null) {
				problem = "walls with nothing to attach to";
				return null;
			}
			return new Action(Kind.Wall, c.Value, c.Value, wallItem, null, false);
		}

		// 4./5. Furniture, then the door from outside.
		foreach (PlanItem it in bp.Items.OrderBy(i => i.PlaceLast)) {
			if (placedItems.Contains(it) || leftOut.Contains(it))
				continue;
			int item = CarriedFor(ctx, it);
			if (item < 0) {
				problem = $"missing item {it.ItemType}";
				return null;
			}
			var min = new TilePos(Origin.X + it.X, Origin.Y + it.Y);
			var max = new TilePos(min.X + it.Width - 1, min.Y + it.Height - 1);
			return new Action(Kind.Item, min, AimFor(ctx, it), item, (min, max), it.PlaceLast, it.Face);
		}
		return null;
	}

	private bool StillNeeded(BotContext ctx, Action a, Func<int, int, bool> coveredByPlaced) => a.Kind switch {
		Kind.Chop => ctx.World.IsTreeTrunk(a.Cell.X, a.Cell.Y),
		Kind.Mine => ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsBlock() && !coveredByPlaced(a.Cell.X, a.Cell.Y),
		Kind.Block => !ctx.Grid.Get(a.Cell.X, a.Cell.Y).IsBlock() && ctx.Blocks != null && ctx.Blocks.BlockCount > 0,
		Kind.Wall => !ctx.World.HasWall(a.Cell.X, a.Cell.Y) && ctx.Inventory.Count(a.Item) > 0,
		Kind.Wait => false, // re-check every tick
		_ => ctx.World.TileTypeAt(a.Aim.X, a.Aim.Y) != ctx.Knowledge.TilePlacedBy(a.Item) && ctx.Inventory.Count(a.Item) > 0,
	};

	private TilePos AimFor(BotContext ctx, PlanItem it)
	{
		var (_, _, ox, oy) = ctx.World.Footprint(it.ItemType);
		return new TilePos(Origin.X + it.X + ox, Origin.Y + it.Y + oy);
	}

	private bool IsPlaced(BotContext ctx, PlanItem it) => IsPlaced(ctx, bp, Origin, it);

	/// <summary>
	/// The item to place for a plan item: the one it names if carried, else a carried item that places the same tile with the same
	/// footprint (an Ebonwood Chair for a Wooden Chair: the housing check counts the tile, ProgressionPlanner.FurnitureChoices). -1: none.
	/// </summary>
	public static int CarriedFor(BotContext ctx, PlanItem it)
	{
		if (ctx.Inventory.Count(it.ItemType) > 0)
			return it.ItemType;
		int tile = ctx.Knowledge?.TilePlacedBy(it.ItemType) ?? -1;
		if (tile < 0)
			return -1;
		var size = ctx.World.Footprint(it.ItemType);
		foreach (int other in ctx.Knowledge.ItemsPlacing(tile))
			if (other != it.ItemType && ctx.Inventory.Count(other) > 0 && ctx.World.Footprint(other) == size)
				return other;
		return -1;
	}

	/// <summary>Is the item already standing where the plan puts it (its tile at the aim cell)?</summary>
	public static bool IsPlaced(BotContext ctx, Blueprint bp, TilePos origin, PlanItem it)
	{
		int tile = ctx.Knowledge.TilePlacedBy(it.ItemType);
		var (_, _, ox, oy) = ctx.World.Footprint(it.ItemType);
		return tile >= 0 && ctx.World.TileTypeAt(origin.X + it.X + ox, origin.Y + it.Y + oy) == tile;
	}

	private IEnumerable<TilePos> Cells(PlanCell kind)
	{
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++)
				if (bp.At(x, y) == kind)
					yield return new TilePos(Origin.X + x, Origin.Y + y);
	}

	/// <summary>
	/// The base of the tree a tree tile belongs to: the trunk tile with trunk above and none below (what the game
	/// adapter's tree scan calls a tree). Branches and roots are tree tiles beside the trunk, so the neighbouring
	/// columns are searched too; seen in game, axing a branch had no effect.
	/// </summary>
	private static TilePos TreeBase(IWorldInfo w, int x, int y)
	{
		foreach (int dx in new[] { 0, -1, 1, -2, 2 })
			for (int yy = y - 1; yy <= y + TreeBaseScan; yy++)
				if (w.IsTreeTrunk(x + dx, yy) && w.IsTreeTrunk(x + dx, yy - 1) && !w.IsTreeTrunk(x + dx, yy + 1))
					return new TilePos(x + dx, yy);
		int by = y;
		while (w.IsTreeTrunk(x, by + 1))
			by++;
		return new TilePos(x, by);
	}

	private const int TreeBaseScan = 30;

	private static bool HasSolidNeighbour(ITileGrid g, TilePos c) =>
		g.Get(c.X - 1, c.Y).IsBlock() || g.Get(c.X + 1, c.Y).IsBlock() || g.Get(c.X, c.Y - 1).IsBlock() || g.Get(c.X, c.Y + 1).IsBlock();

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Build {bp.Name} at {Origin} failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
