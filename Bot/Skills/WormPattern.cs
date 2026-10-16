using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The Eater of Worlds, a worm boss (worm AI, AI style 6: NPC.AI_006_Worms, decompiled NPC.cs of tModLoader 1.4.4.9,
/// from line 54193). What <see cref="BossFightSkill"/> uses:
/// <list type="bullet">
/// <item>it is a chain of separate NPCs: a head (13), bodies (14) and a tail (15) (NPCID.cs:11605-11609), each with its
/// own 150 life and contact damage 22 / 13 / 11 (NPC.cs:4007-4065); in Classic the head spawns 65 bodies
/// (NPC.cs:54699-54702), 67 segments in all. None of them sets dontTakeDamage, so every segment is CanBeChasedBy
/// (NPC.cs:107202-107209) and perceived;</item>
/// <item>killing a body splits the worm: the body behind the gap becomes a head (13), the one in front a tail (15)
/// (NPC.cs:54892-54922). Heads multiply, and the boss lives while any segment does: the last one to die drops the boss
/// loot and sets the "downed" flag (DropEoWLoot, NPC.cs:84392-84412);</item>
/// <item>a head whose box touches a solid tile, a platform top or liquid is "in the ground" and steers at the player at
/// up to 10 px/tick (12 in Expert) (NPC.cs:54926-54984, 55091-55100); so is one more than 1000 px from every player
/// (NPC.cs:54991-55013). In open air nearer than that it falls at 0.11 px/tick², up to the same speed
/// (NPC.cs:55366-55378): it arcs up out of the ground at the player and falls back. Bodies follow the chain;</item>
/// <item>when its target is in neither the Corruption nor the Crimson, every head dives and the chain despawns deep down
/// (NPC.cs:55436-55481). There is no Vile Spit in Classic (Expert only, NPC.cs:54458-54491): contact is the only danger.</item>
/// </list>
/// </summary>
public static class WormPattern
{
	public const int WormAiStyle = 6;
	public const int EaterHead = 13, EaterBody = 14, EaterTail = 15;
	private static readonly int[] EaterSegments = { EaterHead, EaterBody, EaterTail };
	/// <summary>A head's top speed (px/tick), Classic and Expert.</summary>
	public const float HeadTopSpeed = 10f, ExpertHeadTopSpeed = 12f;
	/// <summary>A head in open air falls this much faster each tick (px/tick²).</summary>
	public const float AirGravity = 0.11f;
	/// <summary>Farther than this (px, on either axis) from every player a head flies as if in the ground.</summary>
	public const float FreeFlightPx = 1000f;
	/// <summary>
	/// Ticks with no segment in view before the worm counts as gone. A worm still after us never leaves view: it turns
	/// back within <see cref="FreeFlightPx"/>, and an arc tops out about 454 px (10² / (2 × 0.11)) above the ground,
	/// both far inside the 200 tiles round the player in which the game side perceives a boss's segments. Out of sight
	/// means diving to despawn, or gone. Wait as long as a head at top speed takes to cross that radius
	/// (200 × 16 px / 10 px/tick = 320 ticks): a worm that turned back anywhere just outside it would be in view again.
	/// </summary>
	public const int GoneTicks = (int)(200 * 16 / HeadTopSpeed);

	/// <summary>Is this boss (its head's NPC type) a worm whose segments are separate NPCs?</summary>
	public static bool IsWormBoss(int headType) => headType == EaterHead;

	/// <summary>The NPC types that make up the boss: head, body and tail for a worm, else the boss alone.</summary>
	public static int[] SegmentTypes(int headType) => IsWormBoss(headType) ? EaterSegments : new[] { headType };

	public static float TopSpeed(bool expert) => expert ? ExpertHeadTopSpeed : HeadTopSpeed;

	/// <summary>
	/// A head in open air, falling: not touching a solid tile, a platform or liquid (the cells its box overlaps, as
	/// vanilla tests them), and within <see cref="FreeFlightPx"/> of the player at (<paramref name="playerX"/>,
	/// <paramref name="playerY"/>, the body's top-left).
	/// </summary>
	public static bool Airborne(EntityInfo head, ITileGrid grid, float playerX, float playerY)
	{
		if (Math.Abs(head.X - playerX) > FreeFlightPx || Math.Abs(head.Y - playerY) > FreeFlightPx)
			return false;
		int x0 = (int)MathF.Floor(head.X / Body.TileSize), x1 = (int)MathF.Floor((head.X + head.Width - 1f) / Body.TileSize);
		int y0 = (int)MathF.Floor(head.Y / Body.TileSize), y1 = (int)MathF.Floor((head.Y + head.Height - 1f) / Body.TileSize);
		for (int x = x0; x <= x1; x++)
			for (int y = y0; y <= y1; y++) {
				CellKind k = grid.Get(x, y);
				if (k.IsSolid() || k.IsPlatform() || k.IsLiquid())
					return false;
			}
		return true;
	}

	/// <summary>A segment as a <see cref="Threat"/>: a falling head with gravity, anything else in a straight line.</summary>
	public static Threat Predict(EntityInfo seg, bool airborneHead, bool expert)
	{
		var linear = new Threat(seg.X, seg.Y, seg.Width, seg.Height, seg.VX, seg.VY);
		return airborneHead ? linear with { Gravity = AirGravity, MaxFallSpeed = TopSpeed(expert) } : linear;
	}

	/// <summary>
	/// Where (centre x, px) a head will be when its centre comes to height <paramref name="y"/>: falling under gravity in
	/// the air, for up to a whole arc (up and down at top speed, 2 × 10 / 0.11 ≈ 180 ticks); in the ground in a straight
	/// line for the dodge planner's horizon (it steers there, so a line is good for no longer). Its x now if it does not
	/// get there.
	/// </summary>
	public static float CrossingX(EntityInfo head, bool airborne, float y, bool expert)
	{
		float top = TopSpeed(expert);
		int ticks = airborne ? (int)(2f * top / AirGravity) : Dodge.Horizon;
		float x = head.CenterX, cy = head.CenterY, vx = head.VX, vy = head.VY;
		float side = Math.Sign(y - cy);
		if (side == 0f)
			return x;
		for (int t = 0; t < ticks; t++) {
			if (airborne)
				vy = MathF.Min(vy + AirGravity, top);
			x += vx;
			cy += vy;
			if (Math.Sign(y - cy) != side)
				return x;
		}
		return head.CenterX;
	}
}
