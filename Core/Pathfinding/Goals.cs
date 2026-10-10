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
