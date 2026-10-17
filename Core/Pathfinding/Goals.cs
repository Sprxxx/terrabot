using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Pathfinding;

/// <summary>Baritone-style goal: a predicate over nodes plus an admissible heuristic in ticks.</summary>
public interface IGoal
{
	bool IsGoal(TilePos node);
	float Heuristic(TilePos node);
	/// <summary>A representative tile for drawing/logging.</summary>
	TilePos Anchor { get; }
}

/// <summary>
/// Feet on <see cref="Row"/> or above it, anywhere (Baritone's GoalYLevel), where <see cref="Accept"/> allows: "up to the
/// surface" as a player goes up, by the cheapest way (a cave, or digging and pillaring), not to one tile picked on the
/// surface. Seen in game (measured run R1): walks to a surface tile 150 rows up wandered the caves in partial segments,
/// came to nothing, and a new tile was picked each time; the bot went back and forth along a cave floor for hours.
/// With <see cref="Below"/>, feet on the row or below it: "down to mining depth", by whatever way down there is.
/// </summary>
public sealed class GoalRow : IGoal
{
	private readonly float riseTicksPerTile, fallTicksPerTile;
	public int Row { get; }
	public bool Below { get; init; }
	public TilePos Anchor { get; }
	public Func<TilePos, bool> Accept { get; init; }

	public GoalRow(int row, int anchorX, PhysicsParams p)
	{
		Row = row;
		Anchor = new TilePos(anchorX, row);
		riseTicksPerTile = Body.TileSize / Math.Max(0.5f, p.JumpSpeed);
		fallTicksPerTile = Body.TileSize / Math.Max(0.5f, p.MaxFallSpeed);
	}

	public bool IsGoal(TilePos n) => (Below ? n.Y >= Row : n.Y <= Row) && (Accept == null || Accept(n));
	public float Heuristic(TilePos n) => Below ? (n.Y < Row ? (Row - n.Y) * fallTicksPerTile : 0f) : n.Y > Row ? (n.Y - Row) * riseTicksPerTile : 0f;
	public override string ToString() => Below ? $"AtOrBelowRow{Row}" : $"AtOrAboveRow{Row}";
}

/// <summary>
/// Feet on <see cref="Row"/>, anywhere from <see cref="MinX"/> to <see cref="MaxX"/>: standing on a floor (an arena's), not
/// just near it. Seen in game (measured run U6): "near the arena's middle" was met four rows under its floor, where the
/// fight's "in the arena" check (that row only) was not, and the walk there began again 87,000 times over 46 heartbeats.
/// </summary>
public sealed class GoalStandOnRow : IGoal
{
	private readonly float horizontalTicksPerTile, verticalTicksPerTile;
	public int Row { get; }
	public int MinX { get; }
	public int MaxX { get; }
	public TilePos Anchor => new((MinX + MaxX) / 2, Row);

	public GoalStandOnRow(int row, int minX, int maxX, PhysicsParams p)
	{
		Row = row;
		MinX = minX;
		MaxX = maxX;
		horizontalTicksPerTile = Body.TileSize / p.MaxHorizontalSpeed;
		verticalTicksPerTile = Body.TileSize / Math.Max(p.MaxFallSpeed, p.JumpSpeed);
	}

	public bool IsGoal(TilePos n) => n.Y == Row && n.X >= MinX && n.X <= MaxX;
	public float Heuristic(TilePos n) =>
		(n.X < MinX ? MinX - n.X : n.X > MaxX ? n.X - MaxX : 0) * horizontalTicksPerTile + Math.Abs(n.Y - Row) * verticalTicksPerTile;
	public override string ToString() => $"OnRow{Row}[{MinX}..{MaxX}]";
}

/// <summary>
/// <see cref="Inner"/> as a walk must meet it: where the body standing at the node is read. A node with a slope, half block
/// or platform in its feet row is read a row up as well (<see cref="Body.ReadARowUp"/>; the executor counts both as there,
/// PathExecutor.SameNode), so it is a goal only if the node above is one too. Seen in game (measured run U23): a plan to
/// reach ore at (2075,358) ended at (2076,357), beside a slope at (2077,357); the body rode up it to (2076,356), one row out
/// of the copper pickaxe's reach, the executor said "finished" and the walk planned the same two steps again, 418 times
/// over 1.5 game hours. Only for searches whose end must hold the goal: a plan repair's GoalNode joins a plan at a node,
/// which the executor meets either way.
/// </summary>
public sealed class GoalAsStood : IGoal
{
	private readonly ITileGrid grid;
	public IGoal Inner { get; }
	public TilePos Anchor => Inner.Anchor;

	public GoalAsStood(IGoal inner, ITileGrid grid)
	{
		Inner = inner;
		this.grid = grid;
	}

	public bool IsGoal(TilePos n) => Inner.IsGoal(n) && (!Body.ReadARowUp(grid, n) || Inner.IsGoal(new TilePos(n.X, n.Y - 1)));
	public float Heuristic(TilePos n) => Inner.Heuristic(n);
	public override string ToString() => Inner.ToString();
}

/// <summary>Exactly this node (plan repair: rejoin the old plan where one of its steps starts).</summary>
public sealed class GoalNode : IGoal
{
	private readonly GoalStandAt estimate;
	public TilePos Node { get; }
	public TilePos Anchor => Node;

	public GoalNode(TilePos node, Core.Physics.PhysicsParams p)
	{
		Node = node;
		estimate = new GoalStandAt(node, p);
	}

	public bool IsGoal(TilePos n) => n == Node;
	public float Heuristic(TilePos n) => estimate.Heuristic(n);
	public override string ToString() => $"Node{Node}";
}

/// <summary>Stand with the body covering column Feet.X and feet on row Feet.Y.</summary>
public sealed class GoalStandAt : IGoal
{
	private readonly float horizontalTicksPerTile;
	private readonly float fallTicksPerTile;
	private readonly float riseTicksPerTile;

	public TilePos Feet { get; }
	public TilePos Anchor => Feet;

	public GoalStandAt(TilePos feet, PhysicsParams p)
	{
		Feet = feet;
		horizontalTicksPerTile = Body.TileSize / p.MaxHorizontalSpeed;
		fallTicksPerTile = Body.TileSize / p.MaxFallSpeed;
		riseTicksPerTile = Body.TileSize / Math.Max(0.5f, p.JumpSpeed);
	}

	/// <summary>Extra slack in tiles (Chebyshev) around the exact standing spot; 0 = exact.</summary>
	public int Tolerance { get; init; }

	/// <summary>Node x may be Feet.X-1 or Feet.X: both put a body column over Feet.X (± Tolerance).</summary>
	public bool IsGoal(TilePos n) =>
		Math.Abs(n.Y - Feet.Y) <= Tolerance && n.X >= Feet.X - 1 - Tolerance && n.X <= Feet.X + Tolerance;

	public float Heuristic(TilePos n)
	{
		int dx = n.X < Feet.X - 1 - Tolerance ? Feet.X - 1 - Tolerance - n.X : n.X > Feet.X + Tolerance ? n.X - Feet.X - Tolerance : 0;
		int dy = Feet.Y - n.Y;
		dy = dy > Tolerance ? dy - Tolerance : dy < -Tolerance ? dy + Tolerance : 0;
		float h = dx * horizontalTicksPerTile;
		float v = dy > 0 ? dy * fallTicksPerTile : -dy * riseTicksPerTile;
		return Math.Max(h, v);
	}

	public override string ToString() => $"StandAt{Feet}";
}

/// <summary>
/// Stand anywhere a tool can reach <see cref="Tile"/>. Vanilla Player.IsTargetTileInItemRange, applied to the
/// canonical standing hitbox of node (x, y) (left x*16+6, 20×42, bottom on row y+1), gives the tile ranges
/// dx ∈ [−4−b, 5+b], dy ∈ [−5−b, 3+b] relative to the node, b = the item's tileBoost. One tile of
/// <see cref="Slack"/> is taken off every side so position drift never leaves the bot just out of range.
/// Nodes standing on the tile itself are excluded: mining it would drop the bot.
/// </summary>
public sealed class GoalReachTile : IGoal
{
	public const int Slack = 1;

	private readonly float horizontalTicksPerTile;
	private readonly float fallTicksPerTile;
	private readonly float riseTicksPerTile;
	private readonly int minDx, maxDx, minDy, maxDy;

	public TilePos Tile { get; }
	public TilePos Anchor => Tile;

	public GoalReachTile(TilePos tile, PhysicsParams p, int tileBoost = 0)
	{
		Tile = tile;
		minDx = -4 - tileBoost + Slack;
		maxDx = 5 + tileBoost - Slack;
		minDy = -5 - tileBoost + Slack;
		maxDy = 3 + tileBoost - Slack;
		horizontalTicksPerTile = Body.TileSize / p.MaxHorizontalSpeed;
		fallTicksPerTile = Body.TileSize / p.MaxFallSpeed;
		riseTicksPerTile = Body.TileSize / Math.Max(0.5f, p.JumpSpeed);
	}

	/// <summary>Is the tile in tool range from node <paramref name="n"/> (ignoring the stand-on exclusion)?</summary>
	public bool InRange(TilePos n)
	{
		int dx = Tile.X - n.X, dy = Tile.Y - n.Y;
		return dx >= minDx && dx <= maxDx && dy >= minDy && dy <= maxDy;
	}

	/// <summary>Nodes that are not goals although in range (e.g. standing where a block or table is to go).</summary>
	public Func<TilePos, bool> Exclude { get; init; }

	public bool IsGoal(TilePos n) => InRange(n) && !(Tile.Y == n.Y + 1 && (Tile.X == n.X || Tile.X == n.X + 1)) && !(Exclude?.Invoke(n) ?? false);

	public float Heuristic(TilePos n)
	{
		// Goal nodes: n.X ∈ [Tile.X − maxDx, Tile.X − minDx], n.Y ∈ [Tile.Y − maxDy, Tile.Y − minDy].
		int loX = Tile.X - maxDx, hiX = Tile.X - minDx, loY = Tile.Y - maxDy, hiY = Tile.Y - minDy;
		int dx = n.X < loX ? loX - n.X : n.X > hiX ? n.X - hiX : 0;
		int dy = n.Y < loY ? loY - n.Y : n.Y > hiY ? hiY - n.Y : 0; // > 0: goal is lower (fall)
		float h = dx * horizontalTicksPerTile;
		float v = dy > 0 ? dy * fallTicksPerTile : -dy * riseTicksPerTile;
		return Math.Max(h, v);
	}

	public override string ToString() => $"Reach{Tile}";
}

/// <summary>
/// Stand within <see cref="Radius"/> tiles (Chebyshev, node to tile) of <see cref="Tile"/> with the feet no higher
/// than the tile's row: next to a crafting station rather than on top of it (from up there the floor beside the
/// bot is the station itself, so nothing else could be placed next to it).
/// </summary>
public sealed class GoalNearTile : IGoal
{
	private readonly float horizontalTicksPerTile;
	private readonly float fallTicksPerTile;
	private readonly float riseTicksPerTile;

	public TilePos Tile { get; }
	public int Radius { get; }
	public TilePos Anchor => Tile;

	public GoalNearTile(TilePos tile, int radius, PhysicsParams p)
	{
		Tile = tile;
		Radius = radius;
		horizontalTicksPerTile = Body.TileSize / p.MaxHorizontalSpeed;
		fallTicksPerTile = Body.TileSize / p.MaxFallSpeed;
		riseTicksPerTile = Body.TileSize / Math.Max(0.5f, p.JumpSpeed);
	}

	public bool IsGoal(TilePos n) => n.ChebyshevTo(Tile) <= Radius && n.Y >= Tile.Y;

	public float Heuristic(TilePos n)
	{
		int dx = Math.Max(0, Math.Abs(n.X - Tile.X) - Radius);
		int dy = n.Y < Tile.Y ? Tile.Y - n.Y : n.Y > Tile.Y + Radius ? -(n.Y - Tile.Y - Radius) : 0; // > 0: goal is lower
		float h = dx * horizontalTicksPerTile;
		float v = dy > 0 ? dy * fallTicksPerTile : -dy * riseTicksPerTile;
		return Math.Max(h, v);
	}

	public override string ToString() => $"Near{Tile}±{Radius}";
}

public static class GoalResolver
{
	/// <summary>
	/// Turns a clicked/typed tile into a standing goal. A solid tile means "stand on it". If it is buried and
	/// <paramref name="canDig"/> is false, climb the column (≤ <paramref name="maxScan"/> tiles) to the surface;
	/// with digging allowed the bot digs down to stand on that very block. An empty tile means "stand here":
	/// scan down to the first floor. Falls back to the raw tile.
	/// </summary>
	public static GoalStandAt StandNear(ITileGrid g, TilePos target, PhysicsParams p, bool canDig = false, int maxScan = 40)
	{
		if (g.Get(target.X, target.Y).CanStandOn()) {
			if (canDig)
				return new GoalStandAt(new TilePos(target.X, target.Y - 1), p);
			for (int y = target.Y; y > target.Y - maxScan; y--) {
				if (!g.Get(target.X, y - 1).CanStandOn())
					return new GoalStandAt(new TilePos(target.X, y - 1), p);
			}
			return new GoalStandAt(target, p);
		}

		for (int y = target.Y; y <= target.Y + maxScan; y++) {
			if (g.Get(target.X, y + 1).CanStandOn())
				return new GoalStandAt(new TilePos(target.X, y), p);
		}
		return new GoalStandAt(target, p);
	}
}

/// <summary>
/// Stand where the body's hitbox is clear of a square blast box of half-size <see cref="HalfPx"/> round <see cref="CenterX"/>/<see cref="CenterY"/> (pixels):
/// out of reach of an explosion, whose damage is the box touching the hitbox (vanilla Projectile.Damage on the resized
/// explosive), wherever in the node the body stands: <see cref="Body.NodeOf"/> rounds to the nearest node, so a body counted
/// at a node is up to half a tile either way of <see cref="Body.CanonicalLeft"/> and <see cref="Body.StandingTop"/>. No heuristic:
/// the search is a short Dijkstra round the throw spot.
/// </summary>
public sealed class GoalClearOfBlast : IGoal
{
	private readonly PhysicsParams p;
	public float CenterX { get; }
	public float CenterY { get; }
	public float HalfPx { get; }
	public TilePos Anchor { get; }

	public GoalClearOfBlast(float centerX, float centerY, float halfPx, PhysicsParams p)
	{
		CenterX = centerX;
		CenterY = centerY;
		HalfPx = halfPx;
		this.p = p;
		Anchor = new TilePos((int)Math.Floor(centerX / Body.TileSize), (int)Math.Floor(centerY / Body.TileSize));
	}

	/// <summary>Is the hitbox standing at <paramref name="n"/> clear of the box?</summary>
	public bool IsGoal(TilePos n)
	{
		const float Slop = Body.TileSize / 2f;
		float left = Body.CanonicalLeft(n.X, p) - Slop, top = Body.StandingTop(n.Y, p) - Slop;
		float right = left + p.Width + 2 * Slop, bottom = top + p.Height + 2 * Slop;
		return right <= CenterX - HalfPx || left >= CenterX + HalfPx || bottom <= CenterY - HalfPx || top >= CenterY + HalfPx;
	}

	public float Heuristic(TilePos n) => 0f;
	public override string ToString() => $"ClearOfBlast({CenterX:0},{CenterY:0})±{HalfPx:0}px";
}

/// <summary>Any node <see cref="Test"/> accepts, nearest by cost (no heuristic: a short Dijkstra). A test that always says no
/// makes the search a flood fill that sees every node it reaches (<see cref="Test"/> is asked once per node, cheapest first).</summary>
public sealed class GoalWhere : IGoal
{
	public Func<TilePos, bool> Test { get; }
	public TilePos Anchor { get; }
	private readonly string name;

	public GoalWhere(Func<TilePos, bool> test, TilePos anchor, string name)
	{
		Test = test;
		Anchor = anchor;
		this.name = name;
	}

	public bool IsGoal(TilePos n) => Test(n);
	public float Heuristic(TilePos n) => 0f;
	public override string ToString() => name;
}
