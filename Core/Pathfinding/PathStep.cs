using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// Kinds of movement edges. New kinds (BreakBlock, PlaceBlock, Pillar, DropThroughPlatform, Rope...) are added
/// by writing another <see cref="IMoveType"/>; A* and the executor dispatch on this enum.
/// </summary>
public enum MoveKind
{
	Walk,
	StepUp,
	Drop,
	Jump,
}

/// <summary>One edge of a planned path. Cost is in game ticks.</summary>
public readonly record struct PathStep(MoveKind Kind, TilePos From, TilePos To, float Cost, JumpTemplate Template = null)
{
	public int Dir => To.X > From.X ? 1 : To.X < From.X ? -1 : 0;

	public override string ToString() =>
		Template == null
			? $"{Kind} {From}->{To} {Cost:0.#}t"
			: $"{Kind} {From}->{To} {Cost:0.#}t [{Template.Variant}]";
}
