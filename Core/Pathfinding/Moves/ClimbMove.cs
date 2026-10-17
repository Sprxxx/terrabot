using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding.Moves;

/// <summary>
/// Up a rope hanging over the body (<see cref="CellKind.Rope"/>: rope, chain, vine, silk or web rope, Main.tileRope) and off
/// its top with a jump, as a player climbs into a house a rope leads up to. Vanilla, decompiled:
/// <list type="bullet">
/// <item>grab: with Up or Down held, a rope in the body's middle column 8 px over the hitbox's top sets the body on it, centred
/// on the rope (or 6 px to a side where that is blocked) (Player.FindPulley, Player.cs:18334-18431);</item>
/// <item>climb: Up speeds it up 0.2 px/tick a tick to 3 px/tick, then 0.02 up to 8, while the cell 16 px over the top is rope,
/// and stops it there (Player.cs:25212-25292): the hang at the rope's top;</item>
/// <item>off: Jump from the rope is the ground's jump, jumpSpeed for jumpHeight ticks (Player.cs:25196-25202), so any standing
/// jump of the <see cref="JumpLibrary"/> from the hang reaches what it reaches from the ground.</item>
/// </list>
/// One step from the standing node under the rope to where a jump from the hang lands. Seen in game (the user's world Journey
/// Test, 2026-10-17): a house a mod builds over the spawn of a new world, its one way in a rope 21 tiles up a shaft to the
/// platforms of its floor; the moves had nothing for it but a pillar of blocks up the shaft.
/// </summary>
public sealed class ClimbMove : IMoveType
{
	public MoveKind Kind => MoveKind.Climb;

	/// <summary>Ticks to take hold: Up pressed, the body set on the rope.</summary>
	public const float GrabTicks = 6f;
	/// <summary>Climbing speed: 0.2 px/tick a tick up to 3 px/tick, then 0.02 a tick (Player.cs:25279-25291).</summary>
	public const float ClimbAccel = 0.2f, ClimbSpeed = 3f, ClimbAccel2 = 0.02f;

	public void Expand(MoveContext ctx, TilePos from, List<PathStep> output)
	{
		if (!FindHang(ctx.Grid, from, out TilePos hang))
			return;
		ITileGrid g = ctx.Grid;
		float climb = GrabTicks + ClimbTicks((from.Y - hang.Y) * Body.TileSize);
		foreach (JumpLibrary.Entry e in ctx.Jumps.Entries) {
			if (e.Runup > 0 || e.Dy > 0)
				continue; // from rest on the rope, and up or across: down is the drop's
			var to = new TilePos(hang.X + e.Dx, hang.Y + e.Dy);
			if (!Body.IsStandable(g, to) || g.Get(to.X, to.Y).IsShaped() || g.Get(to.X + 1, to.Y).IsShaped())
				continue;
			foreach (JumpTemplate v in e.Variants)
				if (v.RunupTiles == 0 && v.Fits(g, hang)) {
					output.Add(new PathStep(Kind, from, to, ctx.Adjust(climb + v.Cost, to), v, Source: this, Via: hang));
					break;
				}
		}
	}

	public bool IsValid(MoveContext ctx, in PathStep step) =>
		step.Template != null && step.Via is TilePos via && FindHang(ctx.Grid, step.From, out TilePos hang) && hang == via
		&& Body.IsStandable(ctx.Grid, step.To) && step.Template.Fits(ctx.Grid, hang);

	/// <summary>
	/// The hang at the top of a rope the body standing at <paramref name="from"/> can take hold of: the node the body is read at
	/// there, its column the rope's (centred on rope column r the hitbox's left is r*16 - 2, which <see cref="Body.NodeOf(float, float, Physics.PhysicsParams)"/>
	/// reads as column r), its feet row two under the rope's top cell (the climb stops with the hitbox's top a few px into that
	/// cell). The rope in the body's middle column (r = from.X + 1, the standing body's middle) or the column left of it, at the
	/// head (8 px over the top: row from.Y - 3), the shaft clear for the body on it from there up to the top.
	/// </summary>
	public static bool FindHang(ITileGrid g, TilePos from, out TilePos hang)
	{
		hang = default;
		foreach (int r in new[] { from.X + 1, from.X }) {
			int head = from.Y - Body.HeightTiles;
			if (!IsRope(g, r, head))
				continue;
			int top = head;
			while (IsRope(g, r, top - 1))
				top--;
			var h = new TilePos(r, top + 2);
			if (h.Y >= from.Y - 1)
				continue; // a rope just over the head: no climb worth a step
			// Room for the body on the rope all the way: its column and one beside it (centred, or 6 px to the free side).
			bool clear = true;
			for (int y = top; y <= from.Y && clear; y++)
				clear = g.Get(r, y).IsHeadroom() && (g.Get(r - 1, y).IsHeadroom() || g.Get(r + 1, y).IsHeadroom());
			if (!clear)
				continue;
			hang = h;
			return true;
		}
		return false;
	}

	/// <summary>
	/// A cell the game takes for rope (WorldGen.IsRope, WorldGen.cs:64554-64567): a rope tile, or a platform with rope above and
	/// below it (a rope through a floor).
	/// </summary>
	public static bool IsRope(ITileGrid g, int x, int y) =>
		g.Get(x, y).IsRope() || g.Get(x, y).IsPlatform() && g.Get(x, y - 1).IsRope() && g.Get(x, y + 1).IsRope();

	/// <summary>Ticks to climb <paramref name="px"/> from rest at the rope's speeds.</summary>
	public static float ClimbTicks(float px)
	{
		float rampTicks = ClimbSpeed / ClimbAccel, rampPx = 0.5f * ClimbAccel * rampTicks * rampTicks;
		if (px <= rampPx)
			return MathF.Sqrt(2f * px / ClimbAccel);
		float rest = px - rampPx;
		// v = 3 + 0.02 t: rest = 3 t + 0.01 t^2.
		return rampTicks + (-ClimbSpeed + MathF.Sqrt(ClimbSpeed * ClimbSpeed + 2f * ClimbAccel2 * rest)) / ClimbAccel2;
	}
}
