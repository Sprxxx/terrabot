using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// A pre-simulated jump from a node at rest to the node (Dx, Dy) relative to it. Produced by
/// <see cref="JumpLibrary"/> by running <see cref="Physics.PlayerPhysics"/> with exactly the controller the
/// executor replays: hold jump for <see cref="HoldTicks"/>, press nothing horizontally for
/// <see cref="DelayTicks"/>, then arrive-steer onto the destination column.
/// </summary>
public sealed class JumpTemplate
{
	public int Dx { get; init; }
	public int Dy { get; init; }
	public int HoldTicks { get; init; }
	public int DelayTicks { get; init; }

	/// <summary>Ticks from pressing jump to touching down (worst case over the simulated start offsets).</summary>
	public int Ticks { get; init; }

	/// <summary>Planner cost in ticks: <see cref="Ticks"/> plus the stop-and-align overhead before take-off.</summary>
	public float Cost { get; init; }

	/// <summary>Cells (relative to the start node) the hitbox overlaps at any time. Must all be passable.</summary>
	public TilePos[] BodyCells { get; init; }

	/// <summary>Cells whose top edge the feet cross while descending before the landing. Must not be standable,
	/// otherwise the player would land early (platforms) or clip a corner.</summary>
	public TilePos[] CrossCells { get; init; }

	public string Variant => $"h{HoldTicks} d{DelayTicks}";

	public bool Fits(ITileGrid g, TilePos from)
	{
		foreach (TilePos c in BodyCells)
			if (!g.Get(from.X + c.X, from.Y + c.Y).IsPassable())
				return false;
		foreach (TilePos c in CrossCells)
			if (g.Get(from.X + c.X, from.Y + c.Y).CanStandOn())
				return false;
		return true;
	}
}
