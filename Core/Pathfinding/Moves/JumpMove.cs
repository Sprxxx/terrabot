using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Standing jumps to any (dx, dy) in the <see cref="JumpLibrary"/>. For each standable destination the first
/// (cheapest) template variant whose swept cells are free is used.
/// </summary>
public sealed class JumpMove : IMoveType
{
	private readonly GridWindow window = new();

	public MoveKind Kind => MoveKind.Jump;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		// Covers every cell any template can touch: columns -MaxDx-2..MaxDx+3, rows from apex to below MaxDown.
		const int margin = 3;
		int left = from.X - JumpLibrary.MaxDx - margin;
		int top = from.Y - JumpLibrary.MaxUp - Body.HeightTiles - margin;
		int width = 2 * JumpLibrary.MaxDx + Body.WidthTiles + 2 * margin;
		int height = JumpLibrary.MaxUp + JumpLibrary.MaxDown + Body.HeightTiles + 2 * margin;
		window.Load(ctx.Grid, left, top, width, height);

		foreach (JumpLibrary.Entry e in ctx.Jumps.Entries) {
			var to = new TilePos(from.X + e.Dx, from.Y + e.Dy);
			if (!Body.IsStandable(window, to))
				continue;
			foreach (JumpTemplate v in e.Variants) {
				if (v.Fits(window, from)) {
					output.Add(new PathStep(Kind, from, to, ctx.Adjust(v.Cost, to), v));
					break;
				}
			}
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) =>
		step.Template != null && Body.IsStandable(ctx.Grid, step.To) && step.Template.Fits(ctx.Grid, step.From);
}
