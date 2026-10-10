using System;

namespace TerraBot.Core.Physics;

/// <summary>How a fired projectile moves, per game tick (extra updates folded in).</summary>
public readonly record struct ProjectileModel(
	/// <summary>Launch speed, px per update (item shootSpeed + ammo shootSpeed for bows).</summary>
	float Speed,
	/// <summary>Updates before gravity starts.</summary>
	int GravityDelay,
	/// <summary>Added to the vertical velocity per update once gravity is on.</summary>
	float Gravity,
	float MaxFallSpeed,
	/// <summary>Updates per tick: 1 + Projectile.extraUpdates.</summary>
	int UpdatesPerTick = 1);

/// <summary>A firing solution: where to point the mouse, and when the shot arrives.</summary>
public readonly record struct AimSolution(float AimX, float AimY, int Ticks, bool Hits);

/// <summary>
/// Aiming with lead and drop: the projectile is simulated update by update (the same model as the game's
/// arrow AI), the target is assumed to keep its velocity, and the launch angle is searched so the shot passes
/// through the target's predicted position. Pure maths, no Terraria types.
/// </summary>
public static class Ballistics
{
	public const int MaxTicks = 120;

	/// <summary>Position after <paramref name="ticks"/> game ticks of a shot from (x, y) at angle <paramref name="angle"/>.</summary>
	public static (float X, float Y) At(float x, float y, float angle, ProjectileModel m, int ticks)
	{
		float vx = MathF.Cos(angle) * m.Speed, vy = MathF.Sin(angle) * m.Speed;
		int updates = ticks * m.UpdatesPerTick;
		for (int u = 0; u < updates; u++) {
			if (u >= m.GravityDelay)
				vy = MathF.Min(vy + m.Gravity, m.MaxFallSpeed);
			x += vx;
			y += vy;
		}
		return (x, y);
	}

	/// <summary>
	/// Best launch from (<paramref name="ox"/>, <paramref name="oy"/>) to hit a target at (tx, ty) moving (tvx, tvy) px/tick.
	/// For each arrival tick, the angle that crosses the predicted target column at the right moment is refined
	/// by bisection on the vertical miss; the tick with the smallest miss wins. <see cref="AimSolution.Hits"/> is
	/// false when nothing gets within <paramref name="tolerance"/> px (out of range): the aim then simply points
	/// straight at the target.
	/// </summary>
	public static AimSolution Lead(float ox, float oy, float tx, float ty, float tvx, float tvy, ProjectileModel m, float tolerance = 12f)
	{
		// Solve as if the target were to the right (then "higher" always means a smaller angle) and mirror back.
		bool left = tx < ox;
		if (left) {
			tx = 2 * ox - tx;
			tvx = -tvx;
		}
		AimSolution best = new(left ? 2 * ox - tx : tx, ty, 0, false);
		float bestMiss = float.MaxValue;
		float perTick = m.Speed * m.UpdatesPerTick;
		for (int t = 1; t <= MaxTicks; t++) {
			float px = tx + tvx * t, py = ty + tvy * t;
			float dist = MathF.Sqrt((px - ox) * (px - ox) + (py - oy) * (py - oy));
			if (dist > perTick * t * 1.05f)
				continue; // cannot get there this soon
			if (dist < perTick * (t - 1) * 0.6f)
				break; // later arrivals only get worse (the shot would have passed long ago)
			float direct = MathF.Atan2(py - oy, px - ox);
			float angle = Solve(ox, oy, px, py, direct, m, t, out float miss);
			if (miss < bestMiss) {
				bestMiss = miss;
				float a = left ? MathF.PI - angle : angle;
				best = new AimSolution(ox + MathF.Cos(a) * 100f, oy + MathF.Sin(a) * 100f, t, miss <= tolerance);
			}
			if (miss <= 2f)
				break;
		}
		return best;
	}

	/// <summary>Angle near <paramref name="direct"/> whose shot is closest to (px, py) after <paramref name="ticks"/>.</summary>
	private static float Solve(float ox, float oy, float px, float py, float direct, ProjectileModel m, int ticks, out float miss)
	{
		// Target to the right, screen y down: higher = smaller angle. Search from straight at it to 60° above.
		float lo = direct - MathF.PI / 3f, hi = direct;
		float bestAngle = direct;
		miss = float.MaxValue;
		for (int i = 0; i < 24; i++) {
			float mid = (lo + hi) * 0.5f;
			var (x, y) = At(ox, oy, mid, m, ticks);
			float d = MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
			if (d < miss) {
				miss = d;
				bestAngle = mid;
			}
			// The target is to the right here: is the shot below the line to it (needs more elevation)?
			float cross = (px - ox) * (y - oy) - (py - oy) * (x - ox);
			if (cross > 0)
				hi = mid;
			else
				lo = mid;
		}
		return bestAngle;
	}
}
