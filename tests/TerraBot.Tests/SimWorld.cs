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
/// With an editable grid and <see cref="TestBlockActions"/> it also mines (one hit counter per cell, broken after
/// BreakTicks ticks of use) and places blocks with vanilla's rules: free cell, a solid 4-neighbour, and no
/// overlap with the player's integer hitbox. Like the game, item use happens after movement in the same tick.
/// </summary>
public sealed class SimWorld
{
	private const int T = Body.TileSize;
	private readonly ITileGrid grid;
	private PhysicsParams p;

	/// <summary>The simulated player's movement stats; may change mid-run (buffs).</summary>
	public PhysicsParams Physics { get => p; set => p = value; }
	private readonly ArrayTileGrid editable;
	private readonly TestBlockActions blocks;
	private readonly System.Collections.Generic.Dictionary<TilePos, int> hits = new();

	public int Broken { get; private set; }
	/// <summary>Breaks that left a falling block (sand) unsupported: in the game it would drop onto the bot.</summary>
	public int Undermined { get; private set; }
	public int Placed { get; private set; }
	/// <summary>Called with each mined cell (before it is cleared), e.g. to drop the block's item.</summary>
	public event Action<TilePos> Mined;
	/// <summary>Item uses on cells out of vanilla tool range (Player.IsTargetTileInItemRange, tileBoost 0) that were ignored.</summary>
	public int OutOfRange { get; private set; }

	public BodyState State;
	public int Ticks { get; private set; }

	public SimWorld(ITileGrid grid, PhysicsParams p, TilePos startNode, TestBlockActions blocks = null)
	{
		this.grid = grid;
		this.p = p;
		this.blocks = blocks;
		editable = grid as ArrayTileGrid;
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
		UseItem(c);
	}

	private void UseItem(in MoveInput c)
	{
		if (blocks == null || editable == null || c.Tool == ToolRequest.None || !c.UseItem || !c.HasAim)
			return;
		var cell = new TilePos(c.AimX, c.AimY);
		CellKind k = grid.Get(cell.X, cell.Y);
		if (!InToolRange(cell)) {
			OutOfRange++;
			return;
		}
		if (c.Tool == ToolRequest.Pickaxe) {
			if (!k.IsSolid())
				return;
			hits.TryGetValue(cell, out int n);
			hits[cell] = ++n;
			if (n >= blocks.BreakTicks(cell.X, cell.Y)) {
				Mined?.Invoke(cell);
				editable.Set(cell.X, cell.Y, k & ~(CellKind.Solid | CellKind.Falling));
				hits.Remove(cell);
				Broken++;
				if (grid.Get(cell.X, cell.Y - 1).IsFalling())
					Undermined++;
			}
			return;
		}
		if (c.Tool != ToolRequest.Block)
			return; // other items (walls, furniture) are the caller's business, not blocks
		// Place: vanilla needs an empty cell, a solid neighbour, and Collision.EmptyTile (no hitbox overlap).
		if (k.CanStandOn() || blocks.BlockCount <= 0)
			return;
		if (blocks is TestBlockActions tb && tb.NoPlace.Contains(cell))
			return; // as the planner was told: nothing goes in there
		bool neighbour = SolidAt(cell.X - 1, cell.Y) || SolidAt(cell.X + 1, cell.Y)
			|| SolidAt(cell.X, cell.Y - 1) || grid.Get(cell.X, cell.Y + 1).CanStandOn();
		int left = (int)State.X, top = (int)State.Y;
		bool overlaps = cell.X * T < left + p.Width && left < cell.X * T + T && cell.Y * T < top + p.Height && top < cell.Y * T + T;
		if (!neighbour || overlaps)
			return;
		editable.Set(cell.X, cell.Y, CellKind.Solid);
		blocks.BlockCount--;
		Placed++;
	}

	/// <summary>Vanilla Player.IsTargetTileInItemRange with tileRangeX 5, tileRangeY 4 and no tileBoost.</summary>
	public bool InToolRange(TilePos cell) =>
		State.X / T - 5f <= cell.X && (State.X + p.Width) / T + 5f - 1f >= cell.X
		&& State.Y / T - 4f <= cell.Y && (State.Y + p.Height) / T + 4f - 2f >= cell.Y;

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
