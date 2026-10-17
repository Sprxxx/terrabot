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
	/// Rows above the ground the worm gets at: out of the ground a head flies on at its top speed and falls at
	/// <see cref="AirGravity"/>, so an arc coming up at 45° (half its speed upwards, in square) tops out at
	/// TopSpeed² / (4 × AirGravity) px: 227 px, 14 rows in Classic, 20 in Expert. A floor higher than that over the ground
	/// is out of its reach. Seen in game (measured run M10r): the arena floor at row 246 over ground at rows 285-297 (40-50
	/// rows down); in a fight of 54,000 ticks the bot shot 14 arrows, took no hit, and the worm stayed at 10050 of 10050,
	/// its 67 segments in the ground below, none in a clear line.
	/// </summary>
	public static int ReachRows(bool expert) => (int)(TopSpeed(expert) * TopSpeed(expert) / (4f * AirGravity) / 16f);

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
	/// Every segment in view as a <see cref="Threat"/> with its path over <paramref name="horizon"/> ticks worked out as the game
	/// moves the chain: a head (or a segment whose leader is out of view) as <see cref="Predict"/> has it, on its velocity
	/// (measured for a body, BossFightSkill.Measured); each other segment pulled along the line to the one ahead of it (its
	/// ai[1], NPC.cs:54749) until it is as far from it as it is now (the game keeps it 38 px off, NPC.cs:55280-55327), the
	/// leader moved first. A body does not go on in a straight line: it follows the chain ahead round the bends a head made.
	/// Returned in the order of <paramref name="segs"/>.
	/// </summary>
	public static Threat[] PredictChain(System.Collections.Generic.IReadOnlyList<EntityInfo> segs, ITileGrid grid,
		float playerX, float playerY, bool expert, int horizon = Dodge.Horizon)
	{
		int n = segs.Count;
		var index = new System.Collections.Generic.Dictionary<int, int>(n);
		for (int i = 0; i < n; i++)
			index[segs[i].Id] = i;
		var leader = new int[n];
		for (int i = 0; i < n; i++)
			leader[i] = segs[i].Type != EaterHead && index.TryGetValue((int)segs[i].Ai1, out int l) && l != i ? l : -1;
		// Leaders before their followers: by how many links each is from a free segment (one in a loop counts as free).
		var depth = new int[n];
		for (int i = 0; i < n; i++) {
			int d = 0;
			for (int j = leader[i]; j >= 0 && d <= n; j = leader[j])
				d++;
			if (d > n) {
				leader[i] = -1;
				d = 0;
			}
			depth[i] = d;
		}
		var order = new int[n];
		for (int i = 0; i < n; i++)
			order[i] = i;
		Array.Sort((int[])depth.Clone(), order);
		var px = new float[n][];
		var py = new float[n][];
		var cx = new float[n];
		var cy = new float[n];
		var vx = new float[n];
		var vy = new float[n];
		var gravity = new float[n];
		var spacing = new float[n];
		float top = TopSpeed(expert);
		for (int i = 0; i < n; i++) {
			EntityInfo s = segs[i];
			(cx[i], cy[i], vx[i], vy[i]) = (s.CenterX, s.CenterY, s.VX, s.VY);
			px[i] = new float[horizon];
			py[i] = new float[horizon];
			if (leader[i] < 0)
				gravity[i] = s.Type == EaterHead && Airborne(s, grid, playerX, playerY) ? AirGravity : 0f;
			else {
				EntityInfo l = segs[leader[i]];
				float dx = l.CenterX - s.CenterX, dy = l.CenterY - s.CenterY;
				spacing[i] = MathF.Sqrt(dx * dx + dy * dy);
			}
		}
		for (int t = 0; t < horizon; t++)
			foreach (int i in order) {
				if (leader[i] < 0) {
					if (gravity[i] != 0f)
						vy[i] = MathF.Min(vy[i] + gravity[i], top);
					cx[i] += vx[i];
					cy[i] += vy[i];
				}
				else {
					int l = leader[i];
					float dx = cx[l] - cx[i], dy = cy[l] - cy[i], d = MathF.Sqrt(dx * dx + dy * dy);
					if (d > 0f) {
						float f = (d - spacing[i]) / d;
						cx[i] += dx * f;
						cy[i] += dy * f;
					}
				}
				px[i][t] = cx[i] - segs[i].Width * 0.5f;
				py[i][t] = cy[i] - segs[i].Height * 0.5f;
			}
		var threats = new Threat[n];
		for (int i = 0; i < n; i++) {
			EntityInfo s = segs[i];
			threats[i] = new Threat(s.X, s.Y, s.Width, s.Height, s.VX, s.VY) { PathX = px[i], PathY = py[i] };
		}
		return threats;
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
