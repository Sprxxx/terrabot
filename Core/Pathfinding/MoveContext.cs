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

	/// <summary>Mining/placing capabilities; null disables digging and building moves.</summary>
	public IBlockActions Blocks { get; init; }
	public bool AllowBreak { get; init; } = true;
	public bool AllowPlace { get; init; } = true;

	public bool CanBreak => Blocks != null && AllowBreak;
	public bool CanPlace => Blocks != null && AllowPlace && Blocks.BlockCount > 0;

	/// <summary>Ticks to walk one tile at full speed.</summary>
	public float WalkTileCost { get; }

	/// <summary>Largest drop (tiles) the planner allows: safe fall distance minus a small margin.</summary>
	public int MaxDrop { get; }

	/// <summary>Cost multiplier for edges ending with the body in liquid (water physics are not modelled yet).</summary>
	public float LiquidCostMultiplier { get; init; } = 3f;
	/// <summary>
	/// For edges ending with the head under: breath runs out in about 7 s (200 breath, one per 2 ticks), and the bot
	/// drowned twice in the same flooded cave going for ore under the water.
	/// </summary>
	public float HeadUnderCostMultiplier { get; init; } = 15f;

	/// <summary>Mining time is multiplied by this, so existing routes win unless digging is clearly shorter.</summary>
	public float BreakCostMultiplier { get; init; } = 1.5f;
	/// <summary>Per broken block: switching tools, aiming, and picking the drop up.</summary>
	public float BreakOverheadTicks { get; init; } = 8f;
	/// <summary>Extra cost per pillar step. A pillar is a 1-wide tower: knockback can throw the bot off, and a mistimed
	/// placement means landing back down and jumping again. It also keeps the bot from preferring to drop into a pit
	/// and climb out over bridging across it (first in-game Milestone 2 test).</summary>
	public float PillarRiskTicks { get; init; } = 30f;

	/// <summary>
	/// Cost per placed block on top of the swing time, scaled by how many blocks we carry. With a big stack,
	/// blocks are cheap and a straight bridge beats dropping into a pit and pillaring out. When scarce, the
	/// planner prefers routes that spend fewer blocks.
	/// </summary>
	public float PlacePenaltyTicks => PlacePenaltyFor(Blocks?.BlockCount ?? 0);

	public static float PlacePenaltyFor(int blockCount) =>
		blockCount >= 100 ? 3f : blockCount >= 30 ? 8f : 25f;

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
		PillarAirTicks = ComputePillarAirTicks(physics) + 8f; // + settle/align
	}

	/// <summary>Ticks from jumping for a pillar until landing one tile higher.</summary>
	public float PillarAirTicks { get; }

	private static int ComputePillarAirTicks(PhysicsParams p)
	{
		int hold = BuildMove.PillarHoldTicks(p);
		var s = new BodyState { ReleaseJump = true };
		for (int t = 0; t < 200; t++) {
			PlayerPhysics.StepFreeSpace(ref s, new MoveInput { Jump = t < hold }, p);
			if (s.VY > 0f && s.Y >= -Body.TileSize)
				return t + 1; // falling back past the new floor (one tile above the start)
		}
		return 60;
	}

	public static readonly IReadOnlyList<IMoveType> DefaultMoves = new IMoveType[] {
		new WalkMove(),
		new StepUpMove(),
		new DropMove(),
		new DropThroughMove(),
		new JumpMove(),
		new DigMove(),
		new BuildMove(),
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
		IMoveType m = step.Source ?? MoveFor(step.Kind);
		return m != null && m.IsValid(this, step);
	}

	/// <summary>Applies liquid penalty to an edge ending at <paramref name="to"/>.</summary>
	public float Adjust(float cost, TilePos to) =>
		Body.HeadInLiquid(Grid, to) ? cost * HeadUnderCostMultiplier : Body.InLiquid(Grid, to) ? cost * LiquidCostMultiplier : cost;
}
