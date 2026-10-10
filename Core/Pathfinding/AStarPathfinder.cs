using System;
using System.Collections.Generic;
using System.Diagnostics;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

public enum PathStatus
{
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

/// <summary>A* over standable nodes. Terraria-free; the world comes in through <see cref="MoveContext.Grid"/>.</summary>
public sealed class AStarPathfinder
{
	private struct NodeRec
	{
		public float G;
		public long Parent;
		public bool HasParent;
		public bool Closed;
		public PathStep Via;
	}

	public PathResult FindPath(MoveContext ctx, TilePos start, IGoal goal, SearchLimits limits = null)
	{
		limits ??= SearchLimits.Default;
		var sw = Stopwatch.StartNew();

		if (!Body.IsStandable(ctx.Grid, start))
			return new PathResult { Status = PathStatus.InvalidStart, Start = start, Best = start, ElapsedMs = sw.Elapsed.TotalMilliseconds };

		var nodes = new Dictionary<long, NodeRec>(4096);
		var open = new PriorityQueue<long, float>();
		var edges = new List<PathStep>(64);

		nodes[start.Key] = new NodeRec { G = 0f };
		open.Enqueue(start.Key, goal.Heuristic(start));
		TilePos best = start;
		float bestH = goal.Heuristic(start);
		int expanded = 0;

		while (open.TryDequeue(out long key, out _)) {
			NodeRec rec = nodes[key];
			if (rec.Closed)
				continue;
			rec.Closed = true;
			nodes[key] = rec;

			TilePos pos = TilePos.FromKey(key);
			if (goal.IsGoal(pos))
				return Build(PathStatus.Found, nodes, start, pos, expanded, sw);

			float h = goal.Heuristic(pos);
			if (h < bestH) {
				bestH = h;
				best = pos;
			}

			if (++expanded > limits.MaxExpansions)
				return best == start
					? new PathResult { Status = PathStatus.NoPath, Start = start, Best = best, Expanded = expanded, ElapsedMs = sw.Elapsed.TotalMilliseconds }
					: Build(PathStatus.Partial, nodes, start, best, expanded, sw);

			edges.Clear();
			foreach (IMoveType move in ctx.Moves)
				move.Expand(ctx, pos, edges);

			foreach (PathStep e in edges) {
				if (e.To.ChebyshevTo(start) > limits.MaxRadius)
					continue;
				float g = rec.G + e.Cost;
				long k = e.To.Key;
				if (nodes.TryGetValue(k, out NodeRec existing) && (existing.Closed || existing.G <= g))
					continue;
				nodes[k] = new NodeRec { G = g, Parent = key, HasParent = true, Via = e };
				open.Enqueue(k, g + goal.Heuristic(e.To));
			}
		}

		return new PathResult {
			Status = PathStatus.NoPath,
			Start = start,
			Best = best,
			Expanded = expanded,
			ElapsedMs = sw.Elapsed.TotalMilliseconds,
		};
	}

	private static PathResult Build(PathStatus status, Dictionary<long, NodeRec> nodes, TilePos start, TilePos end, int expanded, Stopwatch sw)
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
			ElapsedMs = sw.Elapsed.TotalMilliseconds,
		};
	}
}
