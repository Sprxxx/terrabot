using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// All jump templates for one set of physics params. Built by simulation, never by hand-tuned reach tables:
/// a (dx, dy) entry exists only if the simulated controller actually lands there from every start offset.
/// </summary>
public sealed class JumpLibrary
{
	public const int MaxDx = 8;
	public const int MaxUp = 8;
	public const int MaxDown = 6;

	/// <summary>The executor aligns to the start node within this many px; templates are robust to it.</summary>
	public const float AlignTolerance = 3f;

	/// <summary>Ticks added to each jump for stopping and aligning before take-off.</summary>
	public const float OverheadTicks = 8f;

	private static readonly float[] StartOffsets = { -AlignTolerance, 0f, AlignTolerance };
	private const int MaxSimTicks = 240;

	public sealed record Entry(int Dx, int Dy, JumpTemplate[] Variants);

	public PhysicsParams Physics { get; }
	public IReadOnlyList<Entry> Entries { get; }

	/// <summary>Apex rise in px of a full jump from rest.</summary>
	public float FullJumpRise { get; }

	private JumpLibrary(PhysicsParams physics, IReadOnlyList<Entry> entries, float fullRise)
	{
		Physics = physics;
		Entries = entries;
		FullJumpRise = fullRise;
	}

	private static readonly Dictionary<PhysicsParams, JumpLibrary> Cache = new();
	private static readonly object CacheLock = new();

	public static JumpLibrary For(PhysicsParams p)
	{
		lock (CacheLock) {
			if (Cache.TryGetValue(p, out JumpLibrary lib))
				return lib;
			if (Cache.Count > 8)
				Cache.Clear();
			lib = Build(p);
			Cache[p] = lib;
			return lib;
		}
	}

	public static JumpLibrary Build(PhysicsParams p)
	{
		int fullHold = p.JumpHeight + 1; // tick 0 starts the jump, then JumpHeight sustained ticks
		var apex = new float[fullHold + 1];
		for (int h = 1; h <= fullHold; h++)
			apex[h] = PlayerPhysics.ApexRise(p, h);

		var entries = new List<Entry>();
		for (int dy = -MaxUp; dy <= MaxDown; dy++) {
			for (int dx = -MaxDx; dx <= MaxDx; dx++) {
				int adx = Math.Abs(dx);
				if (adx <= 1 && dy >= 0)
					continue; // walking / dropping covers these
				float needRise = Math.Max(0, -dy) * Body.TileSize;
				if (needRise > apex[fullHold] - 2f)
					continue;

				// Lower jumps fit under low ceilings; higher ones reach farther. Offer a spread from the
				// minimal sufficient hold up to the full jump.
				int minimal = Enumerable.Range(1, fullHold).FirstOrDefault(h => apex[h] >= needRise + 8f);
				if (minimal == 0)
					minimal = fullHold;
				var holds = new SortedSet<int> { minimal, fullHold };
				for (int h = minimal + 3; h < fullHold; h += 3)
					holds.Add(h);

				var variants = new List<JumpTemplate>();
				foreach (int hold in holds) {
					var delays = new SortedSet<int> { 0 };
					if (dy < 0) {
						int d = RisingDelay(p, hold, needRise + 2f);
						if (d > 0)
							delays.Add(d);
					}
					foreach (int delay in delays) {
						JumpTemplate t = Simulate(p, dx, dy, hold, delay);
						if (t != null)
							variants.Add(t);
					}
				}
				if (variants.Count > 0)
					entries.Add(new Entry(dx, dy, variants.OrderBy(v => v.Cost).ToArray()));
			}
		}
		return new JumpLibrary(p, entries, apex[fullHold]);
	}

	/// <summary>First tick after which the jump has risen at least <paramref name="rise"/> px (-1 if never).</summary>
	private static int RisingDelay(PhysicsParams p, int hold, float rise)
	{
		var s = new BodyState { ReleaseJump = true };
		for (int t = 0; t < MaxSimTicks; t++) {
			PlayerPhysics.StepFreeSpace(ref s, new MoveInput { Jump = t < hold }, p);
			if (-s.Y >= rise)
				return t + 1;
			if (s.VY > 0f)
				return -1;
		}
		return -1;
	}

	/// <summary>Simulates one variant from every start offset; returns null unless all of them land on target.</summary>
	public static JumpTemplate Simulate(PhysicsParams p, int dx, int dy, int hold, int delay)
	{
		var body = new HashSet<TilePos>();
		var cross = new HashSet<TilePos>();
		int worst = 0;
		foreach (float offset in StartOffsets) {
			if (!SimulateOnce(p, dx, dy, hold, delay, offset, body, cross, out int ticks))
				return null;
			worst = Math.Max(worst, ticks);
		}
		return new JumpTemplate {
			Dx = dx,
			Dy = dy,
			HoldTicks = hold,
			DelayTicks = delay,
			Ticks = worst,
			Cost = worst + OverheadTicks,
			BodyCells = body.ToArray(),
			CrossCells = cross.ToArray(),
		};
	}

	/// <summary>
	/// Start node is (0,0): standing at rest with the hitbox centred (+offset) on columns 0..1, bottom at y = 16.
	/// The controller here MUST stay identical to <see cref="Execution.PathExecutor"/>'s jump phase.
	/// </summary>
	private static bool SimulateOnce(PhysicsParams p, int dx, int dy, int hold, int delay, float offset,
		HashSet<TilePos> body, HashSet<TilePos> cross, out int ticks)
	{
		const int T = Body.TileSize;
		ticks = 0;
		var s = new BodyState { X = Body.CanonicalLeft(0, p) + offset, Y = Body.StandingTop(0, p), ReleaseJump = true };
		float targetX = Body.CanonicalLeft(dx, p);
		float landLine = (dy + 1) * T;
		float minLandX = dx * T;
		float maxLandX = (dx + Body.WidthTiles) * T - p.Width;
		AddCells(body, s.X, s.Y, p);

		for (int t = 0; t < MaxSimTicks; t++) {
			MoveInput input = ControllerInput(s, t, hold, delay, targetX, p);
			BodyState prev = s;
			PlayerPhysics.StepFreeSpace(ref s, input, p);
			if (t == 0 && s.VY >= 0f)
				return false;

			float prevBottom = prev.Y + p.Height;
			float bottom = s.Y + p.Height;
			if (s.VY > 0f && bottom > prevBottom) {
				int rFirst = (int)Math.Floor(prevBottom / T) + 1;
				int rLast = (int)Math.Floor(bottom / T);
				for (int r = rFirst; r <= rLast; r++) {
					float line = r * T;
					float frac = (line - prevBottom) / (bottom - prevBottom);
					float xAt = prev.X + (s.X - prev.X) * frac;
					if (r == dy + 1 && xAt >= minLandX - 0.01f && xAt <= maxLandX + 0.01f) {
						AddCells(body, xAt, line - p.Height, p);
						AddCells(body, (prev.X + xAt) * 0.5f, (prev.Y + line - p.Height) * 0.5f, p);
						ticks = t + 1;
						return true;
					}
					int c0 = (int)Math.Floor(xAt / T);
					int c1 = (int)Math.Floor((xAt + p.Width - 0.001f) / T);
					for (int c = c0; c <= c1; c++)
						cross.Add(new TilePos(c, r));
				}
			}

			AddCells(body, (prev.X + s.X) * 0.5f, (prev.Y + s.Y) * 0.5f, p);
			AddCells(body, s.X, s.Y, p);
			if (bottom > landLine + 6 * T)
				return false;
		}
		return false;
	}

	/// <summary>The jump controller, shared by simulation and execution. t = ticks since take-off was pressed.</summary>
	public static MoveInput ControllerInput(in BodyState s, int t, int hold, int delay, float targetX, PhysicsParams p)
	{
		var input = new MoveInput { Jump = t < hold };
		input.Horizontal = t >= delay ? Arrive.Steer(s.X, s.VX, targetX, p) : 0;
		return input;
	}

	private static void AddCells(HashSet<TilePos> set, float x, float y, PhysicsParams p)
	{
		const int T = Body.TileSize;
		int c0 = (int)Math.Floor(x / T);
		int c1 = (int)Math.Floor((x + p.Width - 0.001f) / T);
		int r0 = (int)Math.Floor(y / T);
		int r1 = (int)Math.Floor((y + p.Height - 0.001f) / T);
		for (int r = r0; r <= r1; r++)
			for (int c = c0; c <= c1; c++)
				set.Add(new TilePos(c, r));
	}
}
