using System.Collections.Generic;
using System.Diagnostics;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>Wraps a move type and adds up the time its expansions take (path benchmarks).</summary>
public sealed class ProfilingMove : IMoveType
{
	public IMoveType Inner { get; }
	public long Ticks;
	public int Edges;

	public ProfilingMove(IMoveType inner) => Inner = inner;

	public MoveKind Kind => Inner.Kind;

	public double Ms => Ticks * 1000.0 / Stopwatch.Frequency;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		long t0 = Stopwatch.GetTimestamp();
		int n = output.Count;
		Inner.Expand(ctx, from, output);
		Ticks += Stopwatch.GetTimestamp() - t0;
		Edges += output.Count - n;
	}

	public bool IsValid(MoveContext ctx, in PathStep step) => Inner.IsValid(ctx, step);
}
