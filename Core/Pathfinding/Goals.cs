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

	/// <summary>Node x may be Feet.X-1 or Feet.X: both put a body column over Feet.X.</summary>
	public bool IsGoal(TilePos n) => n.Y == Feet.Y && (n.X == Feet.X || n.X == Feet.X - 1);

	public float Heuristic(TilePos n)
	{
		int dx = n.X < Feet.X - 1 ? Feet.X - 1 - n.X : n.X > Feet.X ? n.X - Feet.X : 0;
		int dy = Feet.Y - n.Y;
		float h = dx * horizontalTicksPerTile;
		float v = dy > 0 ? dy * fallTicksPerTile : -dy * riseTicksPerTile;
		return Math.Max(h, v);
	}

	public override string ToString() => $"StandAt{Feet}";
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
