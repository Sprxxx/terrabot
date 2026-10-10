using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Tests;

/// <summary>
/// Approximate stand-in for vanilla tile collision, for closed-loop executor tests. Implements axis-separated
/// AABB collision against Solid cells, platforms that only collide from above, Collision.StepUp's 1-tile lift
/// and Collision.StepDown's snap. Not bit-exact with the game; the point is to exercise the executor's control
/// loop on the same terrain the planner saw.
/// </summary>
public sealed class SimWorld
{
	private const int T = Body.TileSize;
	private readonly ITileGrid grid;
	private readonly PhysicsParams p;

	public BodyState State;
	public int Ticks { get; private set; }

	public SimWorld(ITileGrid grid, PhysicsParams p, TilePos startNode)
	{
		this.grid = grid;
		this.p = p;
		State = new BodyState {
			X = Body.CanonicalLeft(startNode.X, p),
			Y = Body.StandingTop(startNode.Y, p),
			ReleaseJump = true,
		};
	}

	public TilePos Node => Body.NodeOf(State, p);

	public void Step(MoveInput c)
	{
		Ticks++;
		PlayerPhysics.ApplyControls(ref State, c, p);

		if (State.VY == p.Gravity)
			StepDown();
		if (State.VY >= p.Gravity && !c.Down && State.VX != 0f)
			StepUp();

		MoveX();
		MoveY(c.Down);
	}

	private bool SolidAt(int x, int y) => grid.Get(x, y).IsSolid();

	private void MoveX()
	{
		float nx = State.X + State.VX;
		int r0 = Floor(State.Y / T), r1 = Floor((State.Y + p.Height - 0.001f) / T);
		if (State.VX > 0f) {
			int c0 = Floor((State.X + p.Width - 0.001f) / T) + 1, c1 = Floor((nx + p.Width - 0.001f) / T);
			for (int c = c0; c <= c1; c++)
				for (int r = r0; r <= r1; r++)
					if (SolidAt(c, r)) {
						State.X = c * T - p.Width;
						State.VX = 0f;
						return;
					}
		}
		else if (State.VX < 0f) {
			int c0 = Floor(State.X / T) - 1, c1 = Floor(nx / T);
			for (int c = c0; c >= c1; c--)
				for (int r = r0; r <= r1; r++)
					if (SolidAt(c, r)) {
						State.X = (c + 1) * T;
						State.VX = 0f;
						return;
					}
		}
		State.X = nx;
	}

	private void MoveY(bool down)
	{
		float ny = State.Y + State.VY;
		int c0 = Floor(State.X / T), c1 = Floor((State.X + p.Width - 0.001f) / T);
		if (State.VY > 0f) {
			float oldBottom = State.Y + p.Height;
			float newBottom = ny + p.Height;
			int rFirst = Floor((oldBottom - 0.001f) / T) + 1, rLast = Floor((newBottom - 0.001f) / T);
			for (int r = rFirst; r <= rLast; r++) {
				for (int c = c0; c <= c1; c++) {
					CellKind k = grid.Get(c, r);
					if (k.IsSolid() || (k.IsPlatform() && !down && oldBottom <= r * T + 0.001f)) {
						State.Y = r * T - p.Height;
						State.VY = 0f;
						return;
					}
				}
			}
		}
		else if (State.VY < 0f) {
			int rFirst = Floor(State.Y / T) - 1, rLast = Floor(ny / T);
			for (int r = rFirst; r >= rLast; r--) {
				for (int c = c0; c <= c1; c++) {
					if (SolidAt(c, r)) {
						State.Y = (r + 1) * T;
						State.VY = 0f;
						return;
					}
				}
			}
		}
		State.Y = ny;
	}

	/// <summary>Collision.StepUp, gravDir 1, solid tiles only.</summary>
	private void StepUp()
	{
		int dir = Math.Sign(State.VX);
		float vx = State.X + State.VX;
		int front = Floor((vx + p.Width / 2 + (p.Width / 2 + 1) * dir) / T);
		int feet = Floor((State.Y + p.Height - 1f) / T);
		if (!SolidAt(front, feet))
			return;
		for (int j = 1; j <= 3; j++)
			if (SolidAt(front, feet - j))
				return;
		if (SolidAt(front - dir, feet - 3))
			return;
		if (!(front * T < vx + p.Width && front * T + T > vx))
			return;
		float rise = State.Y + p.Height - feet * T;
		if (rise > 0f && rise <= 16.1f)
			State.Y = feet * T - p.Height;
	}

	/// <summary>Collision.StepDown: snap onto ground 7..17 px below on the first unsupported tick.</summary>
	private void StepDown()
	{
		float bottom = State.Y + p.Height;
		int c0 = Floor((State.X + State.VX) / T), c1 = Floor((State.X + State.VX + p.Width) / T);
		int r = Floor((bottom + 4f) / T);
		float best = float.MaxValue;
		for (int c = c0; c <= c1; c++)
			for (int y = r; y <= r + 1; y++)
				if (grid.Get(c, y).CanStandOn())
					best = Math.Min(best, y * T);
		float gap = best - bottom;
		if (gap > 7f && gap < 17f)
			State.Y = best - p.Height;
	}

	private static int Floor(float v) => (int)Math.Floor(v);
}
