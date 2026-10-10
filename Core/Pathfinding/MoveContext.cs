using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using TerraBot.Core.Pathfinding.Moves;

namespace TerraBot.Core.Pathfinding;

/// <summary>Everything a move needs to evaluate edges: the world view, physics, jump templates and cost tuning.</summary>
public sealed class MoveContext
{
	public ITileGrid Grid { get; }
	public PhysicsParams Physics { get; }
	public JumpLibrary Jumps { get; }
	public IReadOnlyList<IMoveType> Moves { get; }

	/// <summary>Ticks to walk one tile at full speed.</summary>
	public float WalkTileCost { get; }

	/// <summary>Largest drop (tiles) the planner allows: safe fall distance minus a small margin.</summary>
	public int MaxDrop { get; }

	/// <summary>Cost multiplier for edges ending with the body in liquid (water physics are not modelled yet).</summary>
	public float LiquidCostMultiplier { get; init; } = 3f;

	/// <summary>Per-context scratch buffer for moves (move types are shared singletons and must stay stateless).
	/// A MoveContext is therefore not thread-safe; use one per search thread.</summary>
	public GridWindow Scratch { get; } = new();

	private readonly int[] fallTicks;

	public MoveContext(ITileGrid grid, PhysicsParams physics, IReadOnlyList<IMoveType> moves = null)
	{
		Grid = grid;
		Physics = physics;
		Jumps = JumpLibrary.For(physics);
		Moves = moves ?? DefaultMoves;
		WalkTileCost = Body.TileSize / Math.Max(0.5f, physics.MaxRunSpeed);
		MaxDrop = Math.Clamp(physics.SafeFallTiles - 2, 1, 60);
		fallTicks = new int[MaxDrop + 1];
		for (int k = 1; k <= MaxDrop; k++)
			fallTicks[k] = PlayerPhysics.FallTicks(physics, k * Body.TileSize);
	}

	public static readonly IReadOnlyList<IMoveType> DefaultMoves = new IMoveType[] {
		new WalkMove(),
		new StepUpMove(),
		new DropMove(),
		new JumpMove(),
	};

	public int FallTicks(int tiles) => fallTicks[Math.Clamp(tiles, 0, MaxDrop)];

	public IMoveType MoveFor(MoveKind kind)
	{
		foreach (IMoveType m in Moves)
			if (m.Kind == kind)
				return m;
		return null;
	}

	public bool IsStepValid(in PathStep step)
	{
		IMoveType m = MoveFor(step.Kind);
		return m != null && m.IsValid(this, step);
	}

	/// <summary>Applies liquid penalty to an edge ending at <paramref name="to"/>.</summary>
	public float Adjust(float cost, TilePos to) => Body.InLiquid(Grid, to) ? cost * LiquidCostMultiplier : cost;
}
