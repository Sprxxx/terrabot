using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit.Abstractions;

namespace TerraBot.Tests;

internal static class TestUtil
{
	public static readonly PhysicsParams P = PhysicsParams.Default;

	/// <summary>Plans from marker 'S' (node: left body column, feet row) to marker 'G' (feet tile).</summary>
	public static PathResult Plan(ArrayTileGrid g, ITestOutputHelper output = null, SearchLimits limits = null)
	{
		var ctx = new MoveContext(g, P);
		var goal = new GoalStandAt(g.Markers['G'], P);
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], goal, limits);
		output?.WriteLine(r.ToString());
		foreach (PathStep s in r.Steps)
			output?.WriteLine("  " + s);
		return r;
	}

	public static bool Has(this PathResult r, MoveKind kind) => r.Steps.Any(s => s.Kind == kind);
}
