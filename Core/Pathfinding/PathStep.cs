using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// How the body moves during a step. World changes (breaking/placing) ride along as
/// <see cref="PathStep.Actions"/>, so e.g. tunnelling is a Walk with Break actions. New movement kinds are added
/// by writing another <see cref="IMoveType"/> and teaching the executor the kind.
/// </summary>
public enum MoveKind
{
	Walk,
	StepUp,
	Drop,
	Jump,
	/// <summary>Jump straight up and place a block under the feet (Place action), landing one row higher.</summary>
	Pillar,
	/// <summary>Stand on platforms only, hold Down to fall through them, land k rows lower in the same columns.</summary>
	DropThrough,
	/// <summary>
	/// Walk off into a shaft deeper than a safe fall and place a block under the feet on the way down (Place action, done
	/// mid-air), landing on it within a safe fall: the fall arrest of <c>Bot.Brain.FallArrest</c>, planned.
	/// </summary>
	ArrestedDrop,
	/// <summary>Take hold of a rope over the body, climb it to the top (<see cref="PathStep.Via"/>, the hang) and jump off to To.</summary>
	Climb,
}

/// <summary>One edge of a planned path. Cost is in game ticks.</summary>
/// <param name="Actions">Ordered world changes done before the movement (null = none). For Pillar and ArrestedDrop the Place is done mid-air.</param>
/// <param name="Source">The move type that produced the step; used to re-validate it while executing.</param>
public readonly record struct PathStep(
	MoveKind Kind,
	TilePos From,
	TilePos To,
	float Cost,
	JumpTemplate Template = null,
	IReadOnlyList<BlockAction> Actions = null,
	IMoveType Source = null,
	TilePos? Via = null)
{
	public int Dir => To.X > From.X ? 1 : To.X < From.X ? -1 : 0;

	public bool HasActions => Actions != null && Actions.Count > 0;

	public int CountActions(ActionKind kind) => Actions?.Count(a => a.Kind == kind) ?? 0;

	public override string ToString()
	{
		string s = $"{Kind} {From}->{To} {Cost:0.#}t";
		if (Template != null)
			s += $" [{Template.Variant}]";
		if (HasActions)
			s += " {" + string.Join(" ", Actions) + "}";
		return s;
	}
}
