using System;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Where a thrown Bomb or Dynamite comes to rest when its fuse runs out: the vanilla explosive AI (Projectile.AI_016) replayed
/// on the tile grid, tick by tick. It leaves the body's centre at the item's shoot speed towards the mouse (Player.ItemCheck_Shoot),
/// flies straight while ai[0] is 5 or less, then falls at 0.2 px/tick²; on the ground (velocity.Y == 0) it slows by
/// <see cref="ExplosiveInfo.GroundFriction"/> a tick and stops under 0.01; off a wall it comes back at <see cref="ExplosiveInfo.Bounce"/>
/// of its speed, off the floor too when it fell faster than 0.7 (Projectile.Update's tile-collision branch for aiStyle 16).
/// Solid cells only (no slopes): a model, as <see cref="Core.Physics.PlayerPhysics"/> is of the player.
/// </summary>
public static class ExplosiveFlight
{
	public const int StraightTicks = 5;
	public const float Gravity = 0.2f, StopSpeed = 0.01f, BounceMinFall = 0.7f;
	/// <summary>A cap on the fall speed so a throw into a shaft stays finite (the game's own cap for projectiles is higher).</summary>
	public const float MaxFall = 16f;

	/// <summary>The explosive's centre (pixels) after <paramref name="ticks"/>, thrown from (x, y) at the mouse point (mx, my).</summary>
	public static (float X, float Y) Rest(ITileGrid g, float x, float y, float mx, float my, ExplosiveInfo e, int ticks)
	{
		float dx = mx - x, dy = my - y, len = MathF.Sqrt(dx * dx + dy * dy);
		float vx = len > 0 ? dx / len * e.ShootSpeed : 0f, vy = len > 0 ? dy / len * e.ShootSpeed : 0f;
		float s = e.SizePx, left = x - s / 2f, top = y - s / 2f;
		// Started inside a block (thrown point blank into a wall): the game spawns it there too and it falls out; here it stays put.
		if (Overlaps(g, left, top, s))
			return (x, y);
		for (int t = 1; t <= ticks; t++) {
			if (t > StraightTicks) {
				if (vy == 0f && vx != 0f) {
					vx *= e.GroundFriction;
					if (vx > -StopSpeed && vx < StopSpeed)
						vx = 0f;
				}
				vy = MathF.Min(vy + Gravity, MaxFall);
			}
			float lastVx = vx, lastVy = vy;
			// Axis by axis, as Collision.TileCollision stops the box at the first solid cell it would enter.
			if (vx != 0f) {
				float nl = left + vx;
				if (Overlaps(g, nl, top, s))
					vx = 0f;
				else
					left = nl;
			}
			if (vy != 0f) {
				float nt = top + vy;
				if (Overlaps(g, left, nt, s))
					vy = 0f;
				else
					top = nt;
			}
			if (vx != lastVx)
				vx = -lastVx * e.Bounce;
			if (vy != lastVy && lastVy > BounceMinFall)
				vy = -lastVy * e.Bounce;
		}
		return (left + s / 2f, top + s / 2f);
	}

	private static bool Overlaps(ITileGrid g, float left, float top, float size)
	{
		int x0 = (int)MathF.Floor(left / 16f), x1 = (int)MathF.Floor((left + size - 0.01f) / 16f);
		int y0 = (int)MathF.Floor(top / 16f), y1 = (int)MathF.Floor((top + size - 0.01f) / 16f);
		for (int x = x0; x <= x1; x++)
			for (int y = y0; y <= y1; y++)
				if (g.Get(x, y).IsSolid())
					return true;
		return false;
	}

	/// <summary>
	/// Does a blast at (cx, cy) break tile (x, y)? Projectile.ExplodeTiles compares tile indices with the explosive's position
	/// (its top-left, Kill's 22 px box for the Bomb, 10 px for Dynamite) in tiles: within the radius, strictly.
	/// </summary>
	public static bool InBlast(float cx, float cy, ExplosiveInfo e, int x, int y)
	{
		float sx = (cx - e.SizePx / 2f) / 16f, sy = (cy - e.SizePx / 2f) / 16f;
		float dx = x - sx, dy = y - sy;
		return MathF.Sqrt(dx * dx + dy * dy) < e.TileRadius;
	}
}
