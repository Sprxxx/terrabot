using System;
using System.Collections.Generic;
using System.Diagnostics;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

public enum PathStatus
{
	/// <summary>Search still in progress (incremental use only).</summary>
	Searching,
	/// <summary>Complete path to the goal.</summary>
	Found,
	/// <summary>Search budget ran out; path leads to the node closest to the goal. Execute, then plan again.</summary>
	Partial,
	/// <summary>Search space exhausted without reaching the goal: unreachable with the current moves.</summary>
	NoPath,
	/// <summary>The start node is not standable (player in the air, inside blocks, ...).</summary>
	InvalidStart,
}

public sealed class PathResult
{
	public PathStatus Status { get; init; }
	public IReadOnlyList<PathStep> Steps { get; init; } = Array.Empty<PathStep>();
	public int Expanded { get; init; }
	/// <summary>CPU time spent searching (summed over all slices).</summary>
	public double ElapsedMs { get; init; }
	public float Cost { get; init; }
	public TilePos Start { get; init; }
	/// <summary>Node closest to the goal by heuristic (end of a Partial path).</summary>
	public TilePos Best { get; init; }
	/// <summary>
	/// Diagnostics of a search that found nothing: the start's heuristic, the lowest heuristic reached (tainted nodes too),
	/// and the deepest and highest rows expanded. Seen in game (measured run J2): 31 searches of 40,000 nodes from a big cave
	/// ended "no path" with no node better than the start, which the dump's replay does not reproduce.
	/// </summary>
	public string Why { get; init; } = "";

	public override string ToString() =>
		$"{Status}: {Steps.Count} steps, cost {Cost:0.#}t, expanded {Expanded} in {ElapsedMs:0.#} ms";
}

public sealed class SearchLimits
{
	public int MaxExpansions { get; init; } = 40_000;
	/// <summary>Nodes farther than this (Chebyshev, tiles) from the start are ignored.</summary>
	public int MaxRadius { get; init; } = 400;
	/// <summary>Tests can switch the broken-support check off to compare (per search: a static raced with parallel tests).</summary>
	public bool CheckBrokenSupport { get; init; } = true;
	/// <summary>
	/// After this many expansions, a search whose best (untainted) node is at least <see cref="SegmentMinTiles"/> from the
	/// start ends there with a partial plan: a segment to walk while the rest is planned from its end (Baritone plans in
	/// time-limited segments). Seen in game (measured run E): 4,021 plans took 440 s, 99 of them over 2 s, each a 100+ tile
	/// walk searched to the end (27,000-40,000 nodes) with the bot standing still; then a step failed and it all began again.
	/// 0 = off.
	/// </summary>
	public int SegmentAfter { get; init; } = 3_000;
	public int SegmentMinTiles { get; init; } = 12;

	public static readonly SearchLimits Default = new();
}

/// <summary>
/// A* over standable nodes, resumable so the game can spread a long search over several ticks
/// (<see cref="Step"/> with a time budget). Terraria-free; the world comes in through <see cref="MoveContext.Grid"/>.
/// </summary>
public sealed class PathSearch
{
	private struct NodeRec
	{
		public float G;
		public long Parent;
		public bool HasParent;
		public bool Closed;
		public PathStep Via;
		/// <summary>The path here has a step held up only by cells the same path mines (it carries the penalty).</summary>
		public bool Tainted;
	}

	private readonly MoveContext ctx;
	private readonly IGoal goal;
	private readonly SearchLimits limits;
	private readonly TilePos start;
	private readonly Dictionary<long, NodeRec> nodes = new(4096);
	private readonly PriorityQueue<long, float> open = new();
	private readonly List<PathStep> edges = new(64);
	private TilePos best;
	private float bestH;
	private TilePos bestTainted;
	private float bestTaintedH;
	private int expanded;
	private double elapsedMs;
	private int topRow = int.MaxValue, bottomRow = int.MinValue, taintedNodes;
	private float lowestH = float.MaxValue;

	/// <summary>Null until the search has finished.</summary>
	public PathResult Result { get; private set; }
	public int Expanded => expanded;
	public bool Done => Result != null;

	public PathSearch(MoveContext ctx, TilePos start, IGoal goal, SearchLimits limits = null)
	{
		this.ctx = ctx;
		this.goal = goal;
		this.limits = limits ?? SearchLimits.Default;
		this.start = start;
		best = start;
		bestH = goal.Heuristic(start);
		bestTainted = start;
		bestTaintedH = bestH;

		if (!Body.IsStartable(ctx.Grid, start)) {
			Result = new PathResult { Status = PathStatus.InvalidStart, Start = start, Best = start };
			return;
		}
		ctx.BeginSearch(start);
		nodes[start.Key] = new NodeRec { G = 0f };
		open.Enqueue(start.Key, bestH);
	}

	/// <summary>Runs until finished or until <paramref name="budgetMs"/> of CPU time is used. Returns <see cref="Done"/>.</summary>
	public bool Step(double budgetMs = double.PositiveInfinity)
	{
		if (Done)
			return true;
		var sw = Stopwatch.StartNew();
		ctx.InSearch = true;
		try {
			while (open.TryDequeue(out long key, out _)) {
				NodeRec rec = nodes[key];
				if (rec.Closed)
					continue;
				rec.Closed = true;
				nodes[key] = rec;

				TilePos pos = TilePos.FromKey(key);
				if (goal.IsGoal(pos)) {
					Result = Build(PathStatus.Found, pos, sw);
					return true;
				}

				// A partial plan never ends past a tainted step: it would fail there and re-plan. The penalty alone does not
				// keep such nodes out (the partial end is picked by heuristic, not cost): seen in game, 40 re-plans in a row
				// of 3 s each, one stair step of progress each, climbing out of a mine.
				float h = goal.Heuristic(pos);
				if (h < bestH && !rec.Tainted) {
					bestH = h;
					best = pos;
				}
				// ...unless every way on is tainted (the relaxed search, where such steps are allowed): then the best tainted
				// node, as a plan that re-plans part way beats none (world_eye run 46: "no path" after 40,000 nodes, twice).
				if (h < bestTaintedH && rec.Tainted) {
					bestTaintedH = h;
					bestTainted = pos;
				}

				// Not one node closer to the goal than the start after the segment's worth of nodes: the goal is walled off from
				// here, and the rest of the search only floods the cave (seen in game, measured run K1: "start h 26, lowest h
				// 26, rows 335..416", 40,000 nodes and seconds each time). Baritone ends a search that is getting nowhere too.
				if (limits.SegmentAfter > 0 && expanded >= 2 * limits.SegmentAfter && lowestH >= goal.Heuristic(start) - 0.01f && bestTainted == start) {
					Result = new PathResult { Status = PathStatus.NoPath, Start = start, Best = best, Expanded = expanded, ElapsedMs = elapsedMs + sw.Elapsed.TotalMilliseconds,
						Why = $"no node closer than the start in {expanded} (start h {goal.Heuristic(start):0}, rows {topRow}..{bottomRow})" };
					return true;
				}
				if (limits.SegmentAfter > 0 && expanded >= limits.SegmentAfter && best != start && best.ChebyshevTo(start) >= limits.SegmentMinTiles) {
					Result = Build(PathStatus.Partial, best, sw);
					return true;
				}
				if (pos.Y < topRow) topRow = pos.Y;
				if (pos.Y > bottomRow) bottomRow = pos.Y;
				if (h < lowestH) lowestH = h;
				if (rec.Tainted) taintedNodes++;
				if (++expanded > limits.MaxExpansions) {
					TilePos end = best != start ? best : bestTainted;
					Result = end == start
						? new PathResult { Status = PathStatus.NoPath, Start = start, Best = best, Expanded = expanded, ElapsedMs = elapsedMs + sw.Elapsed.TotalMilliseconds,
							Why = $"start h {goal.Heuristic(start):0}, lowest h {lowestH:0}, rows {topRow}..{bottomRow}, tainted {taintedNodes}" }
						: Build(PathStatus.Partial, end, sw);
					return true;
				}

				Expand(pos, rec.G);

				// Check the clock every 64 expansions; Stopwatch reads are cheap but not free.
				if ((expanded & 63) == 0 && sw.Elapsed.TotalMilliseconds >= budgetMs)
					return false;
			}

			Result = new PathResult {
				Status = PathStatus.NoPath,
				Start = start,
				Best = best,
				Expanded = expanded,
				ElapsedMs = elapsedMs + sw.Elapsed.TotalMilliseconds,
			};
			return true;
		}
		finally {
			ctx.InSearch = false;
			elapsedMs += sw.Elapsed.TotalMilliseconds;
		}
	}

	private void Expand(TilePos pos, float g0)
	{
		edges.Clear();
		foreach (IMoveType move in ctx.Moves)
			move.Expand(ctx, pos, edges);

		long parent = pos.Key;
		bool parentTainted = nodes[parent].Tainted;
		BrokenOnPathTo(pos);
		foreach (PathStep e in edges) {
			if (e.To.ChebyshevTo(start) > limits.MaxRadius)
				continue;
			if (ctx.BadEdges != null && ctx.BadEdges.Contains((e.From, e.To)))
				continue;
			// Steps held up only by cells the path itself mines (within BrokenLookback steps): banned in the normal search, so a
			// plan never stands on what it digs away (zigzag staircases re-planned every few steps, 2 s each: runs 15, 32, 44).
			// A ban can leave no way at all (A* keeps one path per node: seen in game, no way out of a cave), so WalkToSkill
			// then plans again with CheckBrokenSupport off, where such steps only cost BrokenSupportPenalty.
			// (Not pillars: they stand on the block they place, whatever was mined there before.)
			bool undermined = e.Kind != MoveKind.Pillar && SupportOnlyFromBroken(ctx.Grid, e.To, brokenOnPath);
			if (undermined && limits.CheckBrokenSupport)
				continue;
			float brokenPenalty = undermined ? BrokenSupportPenalty : 0f;
			if (float.IsPositiveInfinity(e.Cost))
				continue; // lava
			float g = g0 + e.Cost + brokenPenalty;
			long k = e.To.Key;
			if (nodes.TryGetValue(k, out NodeRec existing) && (existing.Closed || existing.G <= g))
				continue;
			nodes[k] = new NodeRec { G = g, Parent = parent, HasParent = true, Via = e, Tainted = parentTainted || brokenPenalty > 0f };
			open.Enqueue(k, g + goal.Heuristic(e.To));
		}
	}

	/// <summary>How many steps back along the path <see cref="BrokenOnPathTo"/> looks for mined cells.</summary>
	public const int BrokenLookback = 8;
	/// <summary>Extra cost of a step held up only by cells its own path mines: such plans re-plan when they get there.</summary>
	public const float BrokenSupportPenalty = 1500f;
	private readonly HashSet<TilePos> brokenOnPath = new();

	/// <summary>
	/// Cells the last <see cref="BrokenLookback"/> steps of the path to <paramref name="pos"/> mine. The planner works
	/// on the unedited grid, so without this a step could stand on a block an earlier step of the same plan digs
	/// away. Seen in game: a zigzag staircase up through rock, each plan invalid a few steps in, 26 re-plans of 1 s each.
	/// </summary>
	private void BrokenOnPathTo(TilePos pos)
	{
		brokenOnPath.Clear();
		long k = pos.Key;
		for (int i = 0; i < BrokenLookback && nodes.TryGetValue(k, out NodeRec r) && r.HasParent; i++) {
			if (r.Via.Actions != null)
				foreach (BlockAction a in r.Via.Actions)
					if (a.Kind == ActionKind.Break)
						brokenOnPath.Add(a.Cell);
			k = r.Parent;
		}
	}

	/// <summary>Would the body at <paramref name="to"/> only be held up by cells in <paramref name="broken"/>?</summary>
	public static bool SupportOnlyFromBroken(ITileGrid g, TilePos to, HashSet<TilePos> broken)
	{
		if (broken.Count == 0)
			return false;
		TilePos a = new(to.X, to.Y + 1), b = new(to.X + 1, to.Y + 1);
		bool ga = g.Get(a.X, a.Y).CanStandOn(), gb = g.Get(b.X, b.Y).CanStandOn();
		if (!ga && !gb)
			return false; // not held up by the grid at all (a jump's apex, a pillar's placed block): not our concern
		return !(ga && !broken.Contains(a)) && !(gb && !broken.Contains(b));
	}

	private PathResult Build(PathStatus status, TilePos end, Stopwatch sw)
	{
		var steps = new List<PathStep>();
		long k = end.Key;
		float cost = nodes[k].G;
		while (nodes[k].HasParent) {
			NodeRec r = nodes[k];
			steps.Add(r.Via);
			k = r.Parent;
		}
		steps.Reverse();
		return new PathResult {
			Status = status,
			Steps = steps,
			Start = start,
			Best = end,
			Cost = cost,
			Expanded = expanded,
			ElapsedMs = elapsedMs + sw.Elapsed.TotalMilliseconds,
		};
	}
}

/// <summary>Convenience wrapper: run a <see cref="PathSearch"/> to completion.</summary>
public sealed class AStarPathfinder
{
	public PathResult FindPath(MoveContext ctx, TilePos start, IGoal goal, SearchLimits limits = null)
	{
		var search = new PathSearch(ctx, start, goal, limits);
		search.Step();
		return search.Result;
	}
}
