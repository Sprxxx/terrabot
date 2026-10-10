using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// A family of movement edges (Baritone's "Movement"). Expand() appends every edge of this kind that is
/// possible from a standable node; IsValid() re-checks a planned edge against the current world (used to
/// detect a blocked path while executing).
///
/// To add block breaking/placing later: implement an IMoveType whose cost includes the time to mine/place
/// (from tool power and tile hardness supplied by an extended grid view), and let the executor perform the
/// break/place before moving. A* needs no changes.
/// </summary>
public interface IMoveType
{
	MoveKind Kind { get; }

	void Expand(MoveContext ctx, TilePos from, List<PathStep> output);

	bool IsValid(MoveContext ctx, in PathStep step);
}
