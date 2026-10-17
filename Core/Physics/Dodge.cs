using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Core.Physics;

/// <summary>
/// Something that hurts on contact (px, px/tick; top-left corner). By default it keeps its velocity; optional
/// behaviour, modelled on vanilla flying-boss AI, makes the prediction follow what it will do:
/// <list type="bullet">
/// <item>seek: steer towards a point <see cref="SeekAbove"/> px above the player's centre, changing each velocity
/// component by <see cref="SeekAccel"/> per tick (twice that when it points the wrong way) up to
/// <see cref="SeekSpeed"/> (the Eye of Cthulhu's hover);</item>
/// <item>dash: at tick <see cref="DashAt"/> take <see cref="DashSpeed"/> straight at the player's centre (where
/// the simulated player is then), and after <see cref="BrakeAfter"/> ticks of a dash multiply the velocity by
/// <see cref="Brake"/> each tick. <see cref="DashedFor"/> ≥ 0 means a dash is under way already;</item>
/// <item><see cref="SeekFrom"/>: the tick a dash under way ends and seeking resumes;</item>
/// <item>fall: add <see cref="Gravity"/> to the vertical velocity each tick, up to <see cref="MaxFallSpeed"/> (a worm's
/// head out of the ground);</item>
/// <item>path: where it will be, worked out beforehand (<see cref="PathX"/>, <see cref="PathY"/>: top-left on ticks 1, 2, ...;
/// it stays at the last one after): a worm's body, which follows the segment ahead of it rather than a velocity of its own;</item>
/// <item>homing: each tick its velocity is set straight at the player's centre at <see cref="HomingSpeed"/> (Skeletron's spin
/// and its daytime chase, NPC.cs:22383-22399 and 22462-22475: the AI sets the velocity outright, no acceleration);</item>
/// <item>wind-up: before its dash (<see cref="DashAt"/>), each tick the velocity is multiplied by <see cref="WindDragX"/> and
/// <see cref="WindDragY"/>, <see cref="WindAX"/> and <see cref="WindAY"/> are added, and each component is kept within
/// <see cref="WindMaxSpeed"/> (a Skeletron hand rising before its slam, or swinging out before its sweep, NPC.cs:22703-22789).</item>
/// </list>
/// </summary>
public sealed record Threat(float X, float Y, int Width, int Height, float VX, float VY)
{
	public float SeekAccel { get; init; }
	public float SeekSpeed { get; init; }
	public float SeekAbove { get; init; }
	public int DashAt { get; init; } = -1;
	public float DashSpeed { get; init; }
	public int DashedFor { get; init; } = -1;
	public int BrakeAfter { get; init; } = 40;
	public float Brake { get; init; } = 1f;
	public int SeekFrom { get; init; } = int.MaxValue;
	public float Gravity { get; init; }
	public float MaxFallSpeed { get; init; } = float.PositiveInfinity;
	public float[] PathX { get; init; }
	public float[] PathY { get; init; }
	public float HomingSpeed { get; init; }
	public float WindDragX { get; init; } = 1f;
	public float WindDragY { get; init; } = 1f;
	public float WindAX { get; init; }
	public float WindAY { get; init; }
	public float WindMaxSpeed { get; init; } = float.PositiveInfinity;
	public bool Seeks => SeekAccel > 0f && SeekSpeed > 0f;
	public bool WindsUp => WindDragX != 1f || WindDragY != 1f || WindAX != 0f || WindAY != 0f;
}

/// <summary>
/// One short movement plan: run <see cref="Dir"/> (−1, 0, +1) and hold jump for the first <see cref="JumpHold"/> ticks.
/// <see cref="HitTick"/> is the first tick a threat touches the player while it is not immune (−1: none within the horizon);
/// <see cref="Clearance"/> the smallest gap in px to any threat over the ticks it could hurt (negative = overlap);
/// <see cref="RoomLeft"/> how far (px) the player ends up from the nearer wall; <see cref="HitCount"/> the hits over the
/// horizon, each leaving the player immune for <see cref="Dodge.HurtImmunityTicks"/>; <see cref="ContactTicks"/> the ticks
/// spent touching a threat while immune (inside a worm's body as it streams by); <see cref="EndBottom"/> the body's bottom
/// (px) at the end.
/// </summary>
public readonly record struct DodgePlan(int Dir, int JumpHold, int HitTick, float Clearance, float RoomLeft = float.PositiveInfinity,
	int HitCount = 0, int ContactTicks = 0, float EndBottom = 0f)
{
	public bool Hits => HitTick >= 0;
	public MoveInput InputAt(int tick) => new() { Horizontal = Dir, Jump = tick < JumpHold };
}

/// <summary>
/// Dodging by look-ahead. Each tick, every plan in <see cref="Plans"/> is simulated for <see cref="Horizon"/> ticks with
/// the real player physics (<see cref="PlayerPhysics"/>) on a flat floor between two walls, against threats moving
/// as <see cref="Threat"/> describes (straight lines, or a modelled hover and dash that react to the simulated
/// player); the plan that is hit latest (or not at all, with the widest gap) wins. Re-planned every tick, so a
/// threat that does something unforeseen is answered as soon as it shows.
/// </summary>
public static class Dodge
{
	public const int Horizon = 50;
	/// <summary>Gaps wider than this (px) count as equally safe, so the caller's preferred direction decides.</summary>
	public const float SafeGap = 48f;
	/// <summary>Plans ending closer than this (px) to a wall lose up to <see cref="CorneredPx"/> × <see cref="CorneredWeight"/> points.</summary>
	public const float CorneredPx = 160f, CorneredWeight = 0.3f;

	public static readonly (int Dir, int JumpHold)[] Plans = {
		(1, 0), (-1, 0), (0, 0),
		(1, 15), (-1, 15), (0, 15),
		(1, 6), (-1, 6),
	};

	/// <summary>
	/// Ticks a hit leaves the player immune to the next touch (vanilla Player.Hurt, Player.cs:37832-37839: immuneTime 40;
	/// 80 with the Cross Necklace, so this is the short side). Contact damage from an NPC needs the player not immune
	/// (Player.cs:29510).
	/// </summary>
	public const int HurtImmunityTicks = 40;

	/// <summary>
	/// Simulates one plan. <paramref name="floorY"/> is the floor's top in px (feet stop there; +∞ = no floor);
	/// <paramref name="minX"/>/<paramref name="maxX"/> bound the hitbox's left edge (walls). With <paramref name="terrain"/> the
	/// body moves through the real tiles instead (<see cref="TerrainStep"/>; <paramref name="floorY"/> is unused, the bounds
	/// still hold). <paramref name="immuneTicks"/>: ticks the player is still immune from a hit already taken; a touch then does
	/// not hurt, and each hit taken in the plan makes it immune for <see cref="HurtImmunityTicks"/> more.
	/// </summary>
	public static DodgePlan Evaluate(BodyState start, PhysicsParams p, float floorY, float minX, float maxX,
		IReadOnlyList<Threat> threats, int dir, int jumpHold, int horizon = Horizon, int immuneTicks = 0, ITileGrid terrain = null)
	{
		BodyState s = start;
		int hit = -1, hits = 0, contact = 0;
		int immuneUntil = immuneTicks; // touches on ticks up to this one do not hurt
		float clearance = float.MaxValue;
		int n = threats.Count;
		Span<float> tx = stackalloc float[n], ty = stackalloc float[n], tvx = stackalloc float[n], tvy = stackalloc float[n];
		Span<int> dashed = stackalloc int[n];
		for (int i = 0; i < n; i++) {
			Threat th = threats[i];
			(tx[i], ty[i], tvx[i], tvy[i], dashed[i]) = (th.X, th.Y, th.VX, th.VY, th.DashedFor);
		}
		for (int t = 1; t <= horizon; t++) {
			var input = new MoveInput { Horizontal = dir, Jump = t - 1 < jumpHold };
			if (terrain != null)
				TerrainStep(ref s, input, p, terrain);
			else {
				PlayerPhysics.StepFreeSpace(ref s, input, p);
				if (s.Y + p.Height >= floorY && s.VY >= 0f) {
					s.Y = floorY - p.Height;
					s.VY = 0f;
				}
			}
			if (s.X < minX) {
				s.X = minX;
				s.VX = 0f;
			}
			else if (s.X > maxX) {
				s.X = maxX;
				s.VX = 0f;
			}
			// Threats move after the player (vanilla updates players before NPCs) and aim at where it is now.
			float pcx = s.X + p.Width * 0.5f, pcy = s.Y + p.Height * 0.5f;
			float gapNow = float.MaxValue;
			for (int i = 0; i < n; i++) {
				Threat th = threats[i];
				Step(th, t, pcx, pcy, ref tx[i], ref ty[i], ref tvx[i], ref tvy[i], ref dashed[i]);
				gapNow = Math.Min(gapNow, Gap(s.X, s.Y, p.Width, p.Height, tx[i], ty[i], th.Width, th.Height));
			}
			if (t <= immuneUntil) {
				if (gapNow < 0f)
					contact++;
				continue;
			}
			clearance = Math.Min(clearance, gapNow);
			if (gapNow < 0f) {
				hits++;
				if (hit < 0)
					hit = t;
				immuneUntil = t + HurtImmunityTicks;
			}
		}
		return new DodgePlan(dir, jumpHold, hit, clearance, Math.Min(s.X - minX, maxX - s.X), hits, contact, s.Y + p.Height);
	}

	/// <summary>
	/// One tick of the player among tiles: controls, then Collision.StepDown's snap onto ground a little below and
	/// Collision.StepUp's lift onto a one-tile step while running on the ground, then position += velocity resolved axis by
	/// axis against solid cells (platforms, slopes and half blocks only from above, while falling onto their top). Not
	/// bit-exact with the game's Collision.TileCollision, but it knows a pit's sides end where they do: with a floor and two
	/// walls as high as the sky the planner found no way out of a pit two columns wide (measured runs M10o and M10p, 38 hits
	/// in the pit at (1721,268)).
	/// </summary>
	public static void TerrainStep(ref BodyState s, in MoveInput c, PhysicsParams p, ITileGrid g)
	{
		PlayerPhysics.ApplyControls(ref s, c, p);
		if (s.VY == p.Gravity)
			StepDown(ref s, p, g);
		if (s.VY >= p.Gravity && !c.Down && s.VX != 0f)
			StepUp(ref s, p, g);
		MoveX(ref s, p, g);
		MoveY(ref s, p, g, c.Down);
	}

	private const int T = 16;
	private static int Floor(float v) => (int)MathF.Floor(v);

	/// <summary>A cell the hitbox cannot enter: solid, but not a cobweb (the body sinks through one).</summary>
	private static bool Collides(ITileGrid g, int x, int y)
	{
		CellKind k = g.Get(x, y);
		return k.IsSolid() && !k.IsWeb();
	}

	private static void MoveX(ref BodyState s, PhysicsParams p, ITileGrid g)
	{
		float nx = s.X + s.VX;
		int r0 = Floor(s.Y / T), r1 = Floor((s.Y + p.Height - 0.001f) / T);
		if (s.VX > 0f) {
			int c0 = Floor((s.X + p.Width - 0.001f) / T) + 1, c1 = Floor((nx + p.Width - 0.001f) / T);
			for (int c = c0; c <= c1; c++)
				for (int r = r0; r <= r1; r++)
					if (Collides(g, c, r)) {
						s.X = c * T - p.Width;
						s.VX = 0f;
						return;
					}
		}
		else if (s.VX < 0f) {
			int c0 = Floor(s.X / T) - 1, c1 = Floor(nx / T);
			for (int c = c0; c >= c1; c--)
				for (int r = r0; r <= r1; r++)
					if (Collides(g, c, r)) {
						s.X = (c + 1) * T;
						s.VX = 0f;
						return;
					}
		}
		s.X = nx;
	}

	private static void MoveY(ref BodyState s, PhysicsParams p, ITileGrid g, bool down)
	{
		float ny = s.Y + s.VY;
		int c0 = Floor(s.X / T), c1 = Floor((s.X + p.Width - 0.001f) / T);
		if (s.VY > 0f) {
			float oldBottom = s.Y + p.Height, newBottom = ny + p.Height;
			int rFirst = Floor((oldBottom - 0.001f) / T) + 1, rLast = Floor((newBottom - 0.001f) / T);
			for (int r = rFirst; r <= rLast; r++)
				for (int c = c0; c <= c1; c++) {
					CellKind k = g.Get(c, r);
					if (Collides(g, c, r) || k.IsPlatform() && !down && oldBottom <= r * T + 0.001f) {
						s.Y = r * T - p.Height;
						s.VY = 0f;
						return;
					}
				}
		}
		else if (s.VY < 0f) {
			int rFirst = Floor(s.Y / T) - 1, rLast = Floor(ny / T);
			for (int r = rFirst; r >= rLast; r--)
				for (int c = c0; c <= c1; c++)
					if (Collides(g, c, r)) {
						s.Y = (r + 1) * T;
						s.VY = 0f;
						s.Jump = 0;
						return;
					}
		}
		s.Y = ny;
	}

	/// <summary>Collision.StepUp, gravDir 1: onto a one-tile step in front with room above it.</summary>
	private static void StepUp(ref BodyState s, PhysicsParams p, ITileGrid g)
	{
		int dir = Math.Sign(s.VX);
		float vx = s.X + s.VX;
		int front = Floor((vx + p.Width / 2 + (p.Width / 2 + 1) * dir) / T);
		int feet = Floor((s.Y + p.Height - 1f) / T);
		if (!Collides(g, front, feet))
			return;
		for (int j = 1; j <= 3; j++)
			if (Collides(g, front, feet - j))
				return;
		if (Collides(g, front - dir, feet - 3))
			return;
		if (!(front * T < vx + p.Width && front * T + T > vx))
			return;
		float rise = s.Y + p.Height - feet * T;
		if (rise > 0f && rise <= 16.1f)
			s.Y = feet * T - p.Height;
	}

	/// <summary>Collision.StepDown: snap onto ground 7..17 px below on the first unsupported tick.</summary>
	private static void StepDown(ref BodyState s, PhysicsParams p, ITileGrid g)
	{
		float bottom = s.Y + p.Height;
		int c0 = Floor((s.X + s.VX) / T), c1 = Floor((s.X + s.VX + p.Width) / T);
		int r = Floor((bottom + 4f) / T);
		float best = float.MaxValue;
		for (int c = c0; c <= c1; c++)
			for (int y = r; y <= r + 1; y++)
				if (g.Get(c, y).CanStandOn())
					best = Math.Min(best, y * T);
		float gap = best - bottom;
		if (gap > 7f && gap < 17f)
			s.Y = best - p.Height;
	}

	/// <summary>One tick of a threat's behaviour (see <see cref="Threat"/>), then position += velocity.</summary>
	private static void Step(Threat th, int t, float pcx, float pcy, ref float x, ref float y, ref float vx, ref float vy, ref int dashed)
	{
		if (th.PathX != null) {
			int k = Math.Min(t, th.PathX.Length) - 1;
			if (k >= 0)
				(x, y) = (th.PathX[k], th.PathY[k]);
			return;
		}
		float cx = x + th.Width * 0.5f, cy = y + th.Height * 0.5f;
		if (t == th.DashAt) {
			float dx = pcx - cx, dy = pcy - cy, d = MathF.Sqrt(dx * dx + dy * dy);
			if (d > 0f) {
				vx = dx / d * th.DashSpeed;
				vy = dy / d * th.DashSpeed;
			}
			dashed = 0;
		}
		else if (dashed >= 0 && t >= th.SeekFrom) {
			dashed = -1;
		}
		else if (dashed >= 0) {
			if (++dashed >= th.BrakeAfter) {
				vx *= th.Brake;
				vy *= th.Brake;
			}
		}
		if (dashed < 0 && th.HomingSpeed > 0f) {
			float dx = pcx - cx, dy = pcy - cy, d = MathF.Sqrt(dx * dx + dy * dy);
			if (d > 0f) {
				vx = dx / d * th.HomingSpeed;
				vy = dy / d * th.HomingSpeed;
			}
		}
		if (dashed < 0 && th.WindsUp && (th.DashAt < 0 || t < th.DashAt)) {
			vx = Math.Clamp(vx * th.WindDragX + th.WindAX, -th.WindMaxSpeed, th.WindMaxSpeed);
			vy = Math.Clamp(vy * th.WindDragY + th.WindAY, -th.WindMaxSpeed, th.WindMaxSpeed);
		}
		if (dashed < 0 && th.Seeks) {
			float dx = pcx - cx, dy = pcy - th.SeekAbove - cy, d = MathF.Sqrt(dx * dx + dy * dy);
			if (d > 0f) {
				float wx = dx / d * th.SeekSpeed, wy = dy / d * th.SeekSpeed, a = th.SeekAccel;
				vx = SeekAxis(vx, wx, a);
				vy = SeekAxis(vy, wy, a);
			}
		}
		if (th.Gravity != 0f)
			vy = MathF.Min(vy + th.Gravity, th.MaxFallSpeed);
		x += vx;
		y += vy;
	}

	/// <summary>Vanilla's hover steering for one axis (NPC.AI style 4): accelerate towards the wanted velocity, doubly when reversing.</summary>
	private static float SeekAxis(float v, float want, float a)
	{
		if (v < want) {
			v += a;
			if (v < 0f && want > 0f)
				v += a;
		}
		else if (v > want) {
			v -= a;
			if (v > 0f && want < 0f)
				v -= a;
		}
		return v;
	}

	/// <summary>
	/// The best plan: not hit (else hit as late as possible, and as few times), then the fewest ticks inside a threat while
	/// immune, then the widest gap up to <see cref="SafeGap"/>, then running <paramref name="preferDir"/>, then not jumping.
	/// A plan not hit that ends with the body's bottom below <paramref name="maxBottom"/> (px: off where the fight is held)
	/// scores as one hit on the horizon's last tick. See <see cref="Evaluate"/> for <paramref name="immuneTicks"/> and
	/// <paramref name="terrain"/>.
	/// </summary>
	public static DodgePlan Best(BodyState start, PhysicsParams p, float floorY, float minX, float maxX,
		IReadOnlyList<Threat> threats, int preferDir, int horizon = Horizon, int immuneTicks = 0, ITileGrid terrain = null,
		float maxBottom = float.PositiveInfinity)
	{
		DodgePlan best = default;
		float bestScore = float.NegativeInfinity;
		foreach ((int dir, int hold) in Plans) {
			DodgePlan plan = Evaluate(start, p, floorY, minX, maxX, threats, dir, hold, horizon, immuneTicks, terrain);
			float score = Score(plan, preferDir, horizon, maxBottom);
			if (score > bestScore) {
				bestScore = score;
				best = plan;
			}
		}
		return best;
	}

	/// <summary>
	/// Points a tick spent inside a threat while immune costs: as much as a pixel of clearance afterwards. Both are margin
	/// against a prediction that is off; and with every plan touching a worm's body at once (measured runs M10o and M10p:
	/// all tied at "hit in 1 tick", so the kiting's way won, into the body) they are what tells getting out of it from
	/// staying in it.
	/// </summary>
	public const float ContactWeight = 1f;

	private static float Score(DodgePlan plan, int preferDir, int horizon, float maxBottom)
	{
		float score = plan.Hits ? -1000f * (horizon + 1 - plan.HitTick) - 1000f * horizon * (plan.HitCount - 1)
			: plan.EndBottom > maxBottom ? -1000f : Math.Min(plan.Clearance, SafeGap);
		score -= plan.ContactTicks * ContactWeight;
		// Ending up against a wall (an arena end) leaves no way out of the next dash: seen in game, a dodge ran the bot
		// into the end of the arena, where the next dash knocked it off.
		if (plan.RoomLeft < CorneredPx)
			score -= (CorneredPx - plan.RoomLeft) * CorneredWeight;
		if (plan.Dir == preferDir)
			score += 2f;
		if (plan.JumpHold == 0)
			score += 1f;
		return score;
	}

	/// <summary>Gap between two boxes: the larger of the horizontal and vertical gaps; negative when they overlap.</summary>
	public static float Gap(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
	{
		float gx = Math.Max(bx - (ax + aw), ax - (bx + bw));
		float gy = Math.Max(by - (ay + ah), ay - (by + bh));
		return Math.Max(gx, gy);
	}
}
