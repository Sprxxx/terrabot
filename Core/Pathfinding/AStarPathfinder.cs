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

	public override string ToString() =>
		$"{Status}: {Steps.Count} steps, cost {Cost:0.#}t, expanded {Expanded} in {ElapsedMs:0.#} ms";
}

public sealed class SearchLimits
{
	public int MaxExpansions { get; init; } = 40_000;
	/// <summary>Nodes farther than this (Chebyshev, tiles) from the start are ignored.</summary>
	public int MaxRadius { get; init; } = 400;

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
	private int expanded;
	private double elapsedMs;

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

		if (!Body.IsStartable(ctx.Grid, start)) {
			Result = new PathResult { Status = PathStatus.InvalidStart, Start = start, Best = start };
			return;
		}
		nodes[start.Key] = new NodeRec { G = 0f };
		open.Enqueue(start.Key, bestH);
	}

	/// <summary>Runs until finished or until <paramref name="budgetMs"/> of CPU time is used. Returns <see cref="Done"/>.</summary>
	public bool Step(double budgetMs = double.PositiveInfinity)
	{
		if (Done)
			return true;
		var sw = Stopwatch.StartNew();
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

				float h = goal.Heuristic(pos);
				if (h < bestH) {
					bestH = h;
					best = pos;
				}

				if (++expanded > limits.MaxExpansions) {
					Result = best == start
						? new PathResult { Status = PathStatus.NoPath, Start = start, Best = best, Expanded = expanded, ElapsedMs = elapsedMs + sw.Elapsed.TotalMilliseconds }
						: Build(PathStatus.Partial, best, sw);
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
			elapsedMs += sw.Elapsed.TotalMilliseconds;
		}
	}

	private void Expand(TilePos pos, float g0)
	{
		edges.Clear();
		foreach (IMoveType move in ctx.Moves)
			move.Expand(ctx, pos, edges);

		long parent = pos.Key;
		BrokenOnPathTo(pos);
		foreach (PathStep e in edges) {
			if (e.To.ChebyshevTo(start) > limits.MaxRadius)
				continue;
			if (ctx.BadEdges != null && ctx.BadEdges.Contains((e.From, e.To)))
				continue;
			// A penalty, not a ban: the check depends on the path a node was first reached by, and A* keeps one path per
			// node, so banning made nodes dead that another path reaches fine (seen in game: no way out of a cave in three
			// runs; replayed, a 73-step path exists).
			// Mined by one of the last BrokenBanSteps steps: banned (that path really mines it; the zigzag staircase of run 15
			// re-planned 26 times otherwise). Mined further back: only a penalty.
			if (CheckBrokenSupport && SupportOnlyFromBroken(ctx.Grid, e.To, brokenRecently))
				continue;
			float brokenPenalty = CheckBrokenSupport && SupportOnlyFromBroken(ctx.Grid, e.To, brokenOnPath) ? BrokenSupportPenalty : 0f;
			if (float.IsPositiveInfinity(e.Cost))
				continue; // lava
			float g = g0 + e.Cost + brokenPenalty;
			long k = e.To.Key;
			if (nodes.TryGetValue(k, out NodeRec existing) && (existing.Closed || existing.G <= g))
				continue;
			nodes[k] = new NodeRec { G = g, Parent = parent, HasParent = true, Via = e };
			open.Enqueue(k, g + goal.Heuristic(e.To));
		}
	}

	/// <summary>How many steps back along the path <see cref="BrokenOnPathTo"/> looks for mined cells.</summary>
	public const int BrokenLookback = 8;
	/// <summary>Tests can switch the broken-support check off to compare.</summary>
	public static bool CheckBrokenSupport = true;
	/// <summary>Extra cost of a step held up only by cells its own path mines: such plans re-plan when they get there.</summary>
	public const float BrokenSupportPenalty = 1500f;
	private readonly HashSet<TilePos> brokenOnPath = new();
	private readonly HashSet<TilePos> brokenRecently = new();
	/// <summary>Cells mined by this many of the last steps make a step held up only by them banned, not just costlier.</summary>
	public const int BrokenBanSteps = 2;

	/// <summary>
	/// Cells the last <see cref="BrokenLookback"/> steps of the path to <paramref name="pos"/> mine. The planner works
	/// on the unedited grid, so without this a step could stand on a block an earlier step of the same plan digs
	/// away. Seen in game: a zigzag staircase up through rock, each plan invalid a few steps in, 26 re-plans of 1 s each.
	/// </summary>
	private void BrokenOnPathTo(TilePos pos)
	{
		brokenOnPath.Clear();
		brokenRecently.Clear();
		long k = pos.Key;
		for (int i = 0; i < BrokenLookback && nodes.TryGetValue(k, out NodeRec r) && r.HasParent; i++) {
			if (r.Via.Actions != null)
				foreach (BlockAction a in r.Via.Actions)
					if (a.Kind == ActionKind.Break) {
						brokenOnPath.Add(a.Cell);
						if (i < BrokenBanSteps)
							brokenRecently.Add(a.Cell);
					}
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
