using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Down a rope deeper than a safe fall: Down held, the body drops through the platforms under it onto the rope and takes hold
/// (Player.FindPulley takes hold of a rope at the head while Down is held too, Player.cs:18334), slides down it (Down: 0.2 px/tick
/// a tick to 3, then 0.1 up to maxFallSpeed, Player.cs:25293-25318; no fall counted on the rope: fallStart is the hitbox's row
/// every tick on it, Player.cs:25214), lets go at its foot (no rope at the head: pulley ends, Player.cs:25189) and falls the
/// rest. Planned only where a plain drop would fall farther than a safe fall (shorter ones are DropThrough's), and where what
/// is left under the rope's foot is a safe fall. The way out of a house a rope leads up to, when the rope is long (the user's
/// world Journey Test: the StartWithBase house over the spawn, 2026-10-17).
/// </summary>
public sealed class ClimbDownMove : IMoveType
{
	public MoveKind Kind => MoveKind.ClimbDown;

	/// <summary>Rows under the feet the rope's top may begin at (platforms dropped through on the way onto it).</summary>
	public const int MaxGapRows = 4;
	/// <summary>Sliding down: 0.2 px/tick a tick to 3 px/tick, then 0.1 a tick (Player.cs:25306-25317).</summary>
	public const float SlideAccel = 0.2f, SlideSpeed = 3f, SlideAccel2 = 0.1f;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (Find(ctx, from, out TilePos to, out TilePos foot, out float slidePx))
			output.Add(new PathStep(Kind, from, to, ctx.Adjust(8f + SlideTicks(slidePx, ctx.Physics.MaxFallSpeed) + ctx.FallTicks(to.Y - foot.Y) + 4f, to),
				Source: this, Via: foot));
	}

	public bool IsValid(MoveContext ctx, in PathStep step) =>
		Find(ctx, step.From, out TilePos to, out TilePos foot, out _) && to == step.To && step.Via == foot;

	/// <summary>
	/// The rope under the standing node: in its middle or left column, its top within <see cref="MaxGapRows"/> under the feet
	/// with nothing solid between; its foot; the node the body is read at when it lets go there (the head a row under the foot,
	/// feet three under it); the landing straight below within a safe fall, the whole way down farther than one.
	/// </summary>
	private static bool Find(MoveContext ctx, TilePos from, out TilePos to, out TilePos foot, out float slidePx)
	{
		ITileGrid g = ctx.Grid;
		to = foot = default;
		slidePx = 0f;
		foreach (int r in new[] { from.X + 1, from.X }) {
			int top = -1;
			for (int y = from.Y + 1; y <= from.Y + MaxGapRows; y++) {
				if (g.Get(from.X, y).IsSolid() || g.Get(from.X + 1, y).IsSolid())
					break;
				if (ClimbMove.IsRope(g, r, y)) {
					top = y;
					break;
				}
			}
			if (top < 0)
				continue;
			int bottom = top;
			while (ClimbMove.IsRope(g, r, bottom + 1))
				bottom++;
			// Down the rope: the first floor the feet meet, still on it (a rope to the ground: a solid floor stops the slide, Down
			// held drops through platforms) or after letting go at its foot (the head a row under it, feet three under it, then
			// any floor), within a safe fall of the foot.
			int release = bottom + 3;
			for (int feet = from.Y + 1; feet <= release + ctx.MaxDrop; feet++) {
				// Room for the body on the rope: its column and the one beside it (the body at node r, centred on the rope).
				if (!Body.IsClear(g, r, feet))
					break;
				CellKind under = g.Get(r, feet + 1), under2 = g.Get(r + 1, feet + 1);
				bool floor = feet < release ? under.IsSolid() && under.CanStandOn() || under2.IsSolid() && under2.CanStandOn() : Body.HasSupport(g, r, feet);
				if (!floor)
					continue;
				if (feet - from.Y <= ctx.MaxDrop)
					break; // a safe fall all the way: DropThrough's
				to = new TilePos(r, feet);
				foot = new TilePos(r, System.Math.Min(feet, release));
				slidePx = (foot.Y - from.Y) * Body.TileSize;
				return true;
			}
		}
		return false;
	}

	/// <summary>Ticks to slide <paramref name="px"/> down from rest at the rope's speeds, capped at the fall speed.</summary>
	public static float SlideTicks(float px, float maxFallSpeed)
	{
		float ramp = SlideSpeed / SlideAccel, rampPx = 0.5f * SlideAccel * ramp * ramp;
		if (px <= rampPx)
			return System.MathF.Sqrt(2f * px / SlideAccel);
		float t = ramp, v = SlideSpeed, d = rampPx;
		while (d < px && t < 2000f) {
			v = System.MathF.Min(v + SlideAccel2, maxFallSpeed);
			d += v;
			t++;
		}
		return t;
	}
}
