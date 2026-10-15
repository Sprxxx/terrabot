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
	/// <summary>
	/// Last resort when boxed in: blocks under sand or beside water may be broken (at <see cref="BlockRules.DigAnywayTicks"/>
	/// more). Seen in game (world_eye run 52): shut in by sand in the underground desert, a 14-node search, and after five
	/// rests the bot switched itself off.
	/// </summary>
	public bool DigAnyway { get; init; }

	public bool CanBreak => Blocks != null && AllowBreak;

	/// <summary>
	/// Edges (from, to) that kept failing in the world: never planned again. Live set, filled while playing (seen in game:
	/// one Walk step failed over and over, walk after walk, each new walk planning through it again).
	/// </summary>
	public IReadOnlySet<(TilePos From, TilePos To)> BadEdges { get; init; }
	/// <summary>Extra cost of an edge (steps that failed lately), or null for none.</summary>
	public Func<TilePos, TilePos, float> EdgePenalty { get; init; }
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
	public float HeadUnderCostMultiplier { get; init; } = 200f; // 15 still sent the bot through a flooded cave

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

	/// <summary>Bit rows of <see cref="Grid"/> for the current search (reset by each <see cref="PathSearch"/>): jump templates test them.</summary>
	public GridMask Mask { get; } = new();

	/// <summary>
	/// Set by <see cref="PathSearch.Step"/> while it runs: break times and placeability are then remembered per cell for
	/// the search (the game's answers walk the tile tables and WorldGen.CanKillTile; one search asks about the same cells
	/// from every neighbouring node). Outside a search, as when a running step is checked, they are asked afresh.
	/// </summary>
	internal bool InSearch;
	private readonly Dictionary<long, float> breakMemo = new(4096);
	private readonly Dictionary<long, bool> placeMemo = new(1024);

	internal void BeginSearch(TilePos start)
	{
		Mask.Reset(Grid, start);
		breakMemo.Clear();
		placeMemo.Clear();
	}

	/// <summary>Ticks to mine the cell (<see cref="IBlockActions.BreakTicks"/>), remembered during a search.</summary>
	public float BreakTicks(int x, int y)
	{
		if (!InSearch)
			return Blocks.BreakTicks(x, y);
		long k = ((long)x << 32) | (uint)y;
		if (!breakMemo.TryGetValue(k, out float t))
			breakMemo[k] = t = Blocks.BreakTicks(x, y);
		return t;
	}

	/// <summary>Can a block go into the cell (<see cref="IBlockActions.CanPlaceAt"/>), remembered during a search.</summary>
	public bool CanPlaceAt(int x, int y)
	{
		if (!InSearch)
			return Blocks.CanPlaceAt(x, y);
		long k = ((long)x << 32) | (uint)y;
		if (!placeMemo.TryGetValue(k, out bool v))
			placeMemo[k] = v = Blocks.CanPlaceAt(x, y);
		return v;
	}

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

	/// <summary>The same context (moves, settings, costs) over another grid: planning ahead on the world as a plan will leave it.</summary>
	public MoveContext WithGrid(ITileGrid grid) => new(grid, Physics, Moves) {
		Blocks = Blocks, AllowBreak = AllowBreak, AllowPlace = AllowPlace, DigAnyway = DigAnyway, BadEdges = BadEdges, EdgePenalty = EdgePenalty,
		LiquidCostMultiplier = LiquidCostMultiplier, HeadUnderCostMultiplier = HeadUnderCostMultiplier,
		BreakCostMultiplier = BreakCostMultiplier, BreakOverheadTicks = BreakOverheadTicks, PillarRiskTicks = PillarRiskTicks,
		PrecariousTicks = PrecariousTicks, AvoidBelowRow = AvoidBelowRow, BelowRowTicks = BelowRowTicks, Avoid = Avoid, AvoidTicks = AvoidTicks,
	};

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

	/// <summary>Extra cost for an edge ending on precarious footing (<see cref="Body.IsPrecarious"/>, over a drop beyond <see cref="MaxDrop"/>).</summary>
	public float PrecariousTicks { get; init; } = 200f;

	/// <summary>
	/// Edges ending below this row cost <see cref="BelowRowTicks"/> more (the caverns, too dangerous for the gear worn).
	/// Seen in game: mining at the depth limit, then walking home through the open caverns just below it, where
	/// crawdads and skeletons killed the bot twice. int.MaxValue = off.
	/// </summary>
	public int AvoidBelowRow { get; init; } = int.MaxValue;
	public float BelowRowTicks { get; init; } = 120f; // 25 was cheaper than digging: a trip to new ground went through the caverns (run 51)
	/// <summary>
	/// Nodes to keep away from (where the bot was killed lately): edges ending there cost <see cref="AvoidTicks"/> more.
	/// Seen in game (world_eye run 40): a death spot was off limits for ore and exploring, but the walks still went
	/// through it, and rolling cacti killed the bot there three times.
	/// </summary>
	public System.Func<TilePos, bool> Avoid { get; init; }
	public float AvoidTicks { get; init; } = 150f; // 40: a trip east crossed the underground desert, killed by an antlion charger (run 51)

	/// <summary>Applies the liquid, footing and depth penalties to an edge ending at <paramref name="to"/>.</summary>
	public float Adjust(float cost, TilePos to)
	{
		// Never into lava: 3x cost for "liquid" sent the bot into a lava pool chasing an antlion charger (78 damage, dead).
		if (Body.TouchesLava(Grid, to))
			return float.PositiveInfinity;
		float c = Body.HeadInLiquid(Grid, to) ? cost * HeadUnderCostMultiplier : Body.InLiquid(Grid, to) ? cost * LiquidCostMultiplier : cost;
		if (to.Y > AvoidBelowRow)
			c += BelowRowTicks;
		if (Avoid != null && Avoid(to))
			c += AvoidTicks;
		return Body.IsPrecarious(Grid, to, MaxDrop + 1) ? c + PrecariousTicks : c;
	}
}
