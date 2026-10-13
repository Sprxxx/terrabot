using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Standing and running jumps to any entry in the <see cref="JumpLibrary"/>. For each standable destination the first
/// (cheapest) template variant whose swept cells are free is used.
/// </summary>
public sealed class JumpMove : IMoveType
{
	public MoveKind Kind => MoveKind.Jump;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		GridWindow window = ctx.Scratch;
		// Covers every cell any template can touch: the library's reach plus a margin, rows from apex to below MaxDown.
		const int margin = 3;
		int reach = ctx.Jumps.MaxReach;
		int left = from.X - reach - margin;
		int top = from.Y - JumpLibrary.MaxUp - Body.HeightTiles - margin;
		int width = 2 * reach + Body.WidthTiles + 2 * margin;
		int height = JumpLibrary.MaxUp + JumpLibrary.MaxDown + Body.HeightTiles + 2 * margin;
		window.Load(ctx.Grid, left, top, width, height);

		foreach (JumpLibrary.Entry e in ctx.Jumps.Entries) {
			var to = new TilePos(from.X + e.Dx, from.Y + e.Dy);
			if (!Body.IsStandable(window, to) || LandsInASlope(window, to))
				continue;
			foreach (JumpTemplate v in e.Variants) {
				if (v.Fits(window, from)) {
					output.Add(new PathStep(Kind, from, to, ctx.Adjust(v.Cost, to), v, Source: this));
					break;
				}
			}
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) =>
		step.Template != null && Body.IsStandable(ctx.Grid, step.To) && step.Template.Fits(ctx.Grid, step.From);

	/// <summary>
	/// Feet would end up inside a slope or half block: the templates assume a flat landing, and the slope's surface is
	/// lower or higher than that. Seen in game: two such jumps failed 3 times each. Walking and step-ups handle slopes.
	/// </summary>
	private static bool LandsInASlope(ITileGrid g, TilePos to) => g.Get(to.X, to.Y).IsShaped() || g.Get(to.X + 1, to.Y).IsShaped();
}
