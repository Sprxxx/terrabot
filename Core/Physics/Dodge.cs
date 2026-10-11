using System;
using System.Collections.Generic;

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
/// <item><see cref="SeekFrom"/>: the tick a dash under way ends and seeking resumes.</item>
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
	public bool Seeks => SeekAccel > 0f && SeekSpeed > 0f;
}

/// <summary>
/// One short movement plan: run <see cref="Dir"/> (−1, 0, +1) and hold jump for the first <see cref="JumpHold"/> ticks.
/// <see cref="HitTick"/> is the first tick a threat touches the player (−1: none within the horizon);
/// <see cref="Clearance"/> the smallest gap in px to any threat over the horizon (negative = overlap).
/// </summary>
public readonly record struct DodgePlan(int Dir, int JumpHold, int HitTick, float Clearance)
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

	public static readonly (int Dir, int JumpHold)[] Plans = {
		(1, 0), (-1, 0), (0, 0),
		(1, 15), (-1, 15), (0, 15),
		(1, 6), (-1, 6),
	};

	/// <summary>
	/// Simulates one plan. <paramref name="floorY"/> is the floor's top in px (feet stop there; +∞ = no floor);
	/// <paramref name="minX"/>/<paramref name="maxX"/> bound the hitbox's left edge (walls).
	/// </summary>
	public static DodgePlan Evaluate(BodyState start, PhysicsParams p, float floorY, float minX, float maxX,
		IReadOnlyList<Threat> threats, int dir, int jumpHold, int horizon = Horizon)
	{
		BodyState s = start;
		int hit = -1;
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
			PlayerPhysics.StepFreeSpace(ref s, input, p);
			if (s.Y + p.Height >= floorY && s.VY >= 0f) {
				s.Y = floorY - p.Height;
				s.VY = 0f;
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
			for (int i = 0; i < n; i++) {
				Threat th = threats[i];
				Step(th, t, pcx, pcy, ref tx[i], ref ty[i], ref tvx[i], ref tvy[i], ref dashed[i]);
				float gap = Gap(s.X, s.Y, p.Width, p.Height, tx[i], ty[i], th.Width, th.Height);
				clearance = Math.Min(clearance, gap);
				if (gap < 0f && hit < 0)
					hit = t;
			}
			if (hit >= 0)
				break;
		}
		return new DodgePlan(dir, jumpHold, hit, clearance);
	}

	/// <summary>One tick of a threat's behaviour (see <see cref="Threat"/>), then position += velocity.</summary>
	private static void Step(Threat th, int t, float pcx, float pcy, ref float x, ref float y, ref float vx, ref float vy, ref int dashed)
	{
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
		if (dashed < 0 && th.Seeks) {
			float dx = pcx - cx, dy = pcy - th.SeekAbove - cy, d = MathF.Sqrt(dx * dx + dy * dy);
			if (d > 0f) {
				float wx = dx / d * th.SeekSpeed, wy = dy / d * th.SeekSpeed, a = th.SeekAccel;
				vx = SeekAxis(vx, wx, a);
				vy = SeekAxis(vy, wy, a);
			}
		}
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
	/// The best plan: not hit (else hit as late as possible), then the widest gap up to <see cref="SafeGap"/>, then
	/// running <paramref name="preferDir"/>, then not jumping.
	/// </summary>
	public static DodgePlan Best(BodyState start, PhysicsParams p, float floorY, float minX, float maxX,
		IReadOnlyList<Threat> threats, int preferDir, int horizon = Horizon)
	{
		DodgePlan best = default;
		float bestScore = float.NegativeInfinity;
		foreach ((int dir, int hold) in Plans) {
			DodgePlan plan = Evaluate(start, p, floorY, minX, maxX, threats, dir, hold, horizon);
			float score = Score(plan, preferDir, horizon);
			if (score > bestScore) {
				bestScore = score;
				best = plan;
			}
		}
		return best;
	}

	private static float Score(DodgePlan plan, int preferDir, int horizon)
	{
		float score = plan.Hits ? -1000f * (horizon + 1 - plan.HitTick) : Math.Min(plan.Clearance, SafeGap);
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
