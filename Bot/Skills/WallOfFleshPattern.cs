using System;
using System.Collections.Generic;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The Wall of Flesh, read from the decompiled NPC.AI of tModLoader 1.4.4.9 (docs M12_SPEC.md sections 3-6).
/// <list type="bullet">
/// <item>The mouth (NPC 113, AI style 27, NPC.cs:26119-26541): 100 x 100, 8000 life, defence 12. It never stops and never turns:
/// its velocity is set to its speed each tick the way it set off (toward the player on its first tick), 1.5 px/tick at full life,
/// 1.75 below 75 %, 2.15 below 50 %, 2.65 below 25 %, 3.25 below 10 % in Classic (NPC.cs:26337-26383).</item>
/// <item>The eyes (114, AI style 28, NPC.cs:26542-26700): at the mouth's x, ai[0] > 0 the upper one, defence 0, sharing the mouth's
/// life (realLife). Each fires lasers (projectile 83, 9 px/tick with extraUpdates 2: 27 px/tick) at the player's centre, only at
/// a player in front of the Wall and in line of sight; the laser stops on tiles.</item>
/// <item>The Hungry (115, AI style 29, NPC.cs:26701-26898): leashed to the Wall's centre x and its own height in the band, flying
/// at the player within 300 px of that anchor (500 below 75 %, 700 below 50 % in Classic), 1.3 times that half of each 200-tick
/// cycle. In Classic they never respawn; a dead one becomes a free Hungry II (116).</item>
/// <item>The Tongue (Player.WOFTongue, Player.cs:20590-20657): a player whose top is above the Underworld's first row, or whose
/// centre is more than 40 px behind the mouth's centre, is pulled in and dies beyond 3000 px.</item>
/// </list>
/// The fight (BossFightSkill) keeps a band ahead of the Wall beyond the Hungry's reach, never turns back toward it, shoots the eyes
/// (defence 0: every arrow does more there than on the mouth), and dodges the lasers as fast threats.
/// </summary>
public static class WallOfFleshPattern
{
	public const int MouthAiStyle = 27, EyeAiStyle = 28, HungryAiStyle = 29;
	/// <summary>Vanilla NPCID.WallofFlesh, WallofFleshEye, TheHungry, TheHungryII, LeechHead, LeechBody, LeechTail.</summary>
	public const int Mouth = 113, Eye = 114, Hungry = 115, HungryII = 116, LeechHead = 117, LeechBody = 118, LeechTail = 119;
	/// <summary>The eye laser (ProjectileID.EyeLaser, Projectile.cs:1817-1830).</summary>
	public const int Laser = 83;
	/// <summary>A player's centre this far behind the mouth's centre is grabbed by The Tongue (Player.cs:20622-20632).</summary>
	public const float BehindTonguePx = 40f;
	/// <summary>Tiles kept beyond the Hungry's longest reach (spec section 6: reach + 5 gives 30 / 46 / 62 tiles by life band).</summary>
	public const int BandMarginTiles = 5;
	/// <summary>The band's width (tiles) past its near edge: stand in it and let the Wall come, run on once at its near edge.</summary>
	public const int BandWidthTiles = 15;
	/// <summary>Never closer than this to the Wall's centre (tiles): its contact strip and the mouth (Player.cs:20600-20610, 140 px).</summary>
	public const int MinGapTiles = 8;
	/// <summary>
	/// Run from this many tiles inside the band's near edge, and stop this many short of its far edge: the bot runs at 3 px/tick
	/// (6 with Hermes Boots) against the Wall's 1.5-3.25, so a run covers the band in a few seconds and a stand lets it come.
	/// </summary>
	public const int RunFromTiles = 2, StopShortTiles = 3;
	/// <summary>The Hungry's leash grows by this half of each 200-tick cycle (NPC.cs:26776-26784).</summary>
	public const float HungryStretch = 1.3f;

	public static bool IsWallOfFlesh(int bossType) => bossType == Mouth;

	/// <summary>Any part of the Wall or its servants (113-119).</summary>
	public static bool IsPart(int type) => type >= Mouth && type <= LeechTail;

	public static bool IsLeech(int type) => type is LeechHead or LeechBody or LeechTail;

	/// <summary>The mouth's speed (px/tick) at this share of its life (NPC.cs:26337-26383; the Expert terms as the AI has them).</summary>
	public static float Speed(float lifeFraction, bool expert)
	{
		float s = 1.5f;
		if (lifeFraction < 0.75f) s += 0.25f;
		if (lifeFraction < 0.5f) s += 0.4f;
		if (lifeFraction < 0.25f) s += 0.5f;
		if (lifeFraction < 0.1f) s += 0.6f;
		if (expert) {
			if (lifeFraction < 0.66f) s += 0.3f;
			if (lifeFraction < 0.33f) s += 0.3f;
			if (lifeFraction < 0.05f) s += 0.6f;
			if (lifeFraction < 0.035f) s += 0.6f;
			if (lifeFraction < 0.025f) s += 0.6f;
			s = s * 1.35f + 0.35f;
		}
		return s;
	}

	/// <summary>
	/// The Hungry's longest reach from its anchor (px) at this share of the Wall's life: 300, 500 below 75 %, 700 below 50 % in
	/// Classic, stretched by <see cref="HungryStretch"/>; in Expert 300 times at most 1.75 x 1.5 x 0.75 by its slot (NPC.cs:26714-26770).
	/// </summary>
	public static float HungryReachPx(float lifeFraction, bool expert)
	{
		float leash = expert ? 300f * 1.75f * 1.5f * 0.75f : lifeFraction < 0.5f ? 700f : lifeFraction < 0.75f ? 500f : 300f;
		return leash * HungryStretch;
	}

	/// <summary>The band's near edge (tiles ahead of the Wall's centre): the Hungry's reach and <see cref="BandMarginTiles"/>, at least <see cref="MinGapTiles"/>.</summary>
	public static int BandMinTiles(float lifeFraction, bool expert) =>
		Math.Max(MinGapTiles, (int)MathF.Ceiling(HungryReachPx(lifeFraction, expert) / 16f) + BandMarginTiles);

	/// <summary>The way the Wall travels (-1 west, +1 east): its velocity's sign; standing still (its first tick), toward the player.</summary>
	public static int Direction(in EntityInfo wall, float playerCX) =>
		wall.VX > 0f ? 1 : wall.VX < 0f ? -1 : playerCX >= wall.CenterX ? 1 : -1;

	/// <summary>How far ahead of the Wall's centre (px, the way it travels) the point is: negative behind it.</summary>
	public static float Ahead(in EntityInfo wall, int dir, float x) => (x - wall.CenterX) * dir;

	/// <summary>Is the player's centre so far behind the mouth that The Tongue has it (Player.cs:20622-20632)?</summary>
	public static bool Behind(in EntityInfo mouth, int dir, float playerCX) => Ahead(mouth, dir, playerCX) < -BehindTonguePx;

	/// <summary>The Wall's share of its life, from the mouth or an eye (the eyes take the mouth's life each tick, NPC.cs:26549-26553).</summary>
	public static float LifeFraction(in EntityInfo part) => part.LifeMax > 0 ? (float)part.Life / part.LifeMax : 1f;

	/// <summary>The upper eye (ai[0] > 0, NPC.cs:26558-26560): over the hellbridge, it is the one with a line to us.</summary>
	public static bool UpperEye(in EntityInfo eye) => eye.Ai0 > 0f;

	/// <summary>
	/// The Wall as the fight sees it: the mouth if in view, else an eye (at the mouth's x and with its life), else none.
	/// </summary>
	public static EntityInfo? Wall(IReadOnlyList<EntityInfo> hostiles)
	{
		EntityInfo? eye = null;
		foreach (EntityInfo h in hostiles) {
			if (h.Type == Mouth)
				return h;
			if (h.Type == Eye && eye == null)
				eye = h;
		}
		return eye;
	}

	/// <summary>
	/// A laser (or any hostile shot) as a <see cref="Threat"/>: a straight line at its move per tick (ShotInfo: velocity times
	/// 1 + extraUpdates). It is aimed at the player's centre when fired, with no lead (NPC.cs:26688-26697).
	/// </summary>
	public static Threat ShotThreat(in ShotInfo s) => new(s.X, s.Y, s.Width, s.Height, s.VX, s.VY);

	/// <summary>
	/// Will this shot come our way within <paramref name="ticks"/>: heading toward the body's centre (px), close enough to get
	/// there in time? A laser going away, or past us already, is no threat.
	/// </summary>
	public static bool Incoming(in ShotInfo s, float cx, float cy, int ticks)
	{
		float dx = cx - s.CenterX, dy = cy - s.CenterY;
		float closing = dx * s.VX + dy * s.VY;
		if (closing <= 0f)
			return false;
		float speed2 = s.VX * s.VX + s.VY * s.VY;
		return speed2 > 0f && closing / speed2 <= ticks;
	}

	/// <summary>
	/// The summon (spec S6): one Guide Voodoo Doll thrown <paramref name="facing"/> off the bridge's end into the lava, refused with
	/// no Guide alive (the doll would be wasted, Item.cs:50409-50425), done once the Wall is in view or on its bar; says where it
	/// came from ("wall of flesh summoned").
	/// </summary>
	public static ThrowItemSkill Summon(GameIds ids, int facing) => new(ids.GuideVoodooDoll, facing, "Guide Voodoo Doll") {
		Refuse = c => c.World.TownNpcAlive(c.Ids.Guide) ? null : "no Guide alive: the doll would only burn (doll wasted, no Guide); he comes back by day",
		Done = c => Wall(c.Hostiles) != null || c.World.BossBarLife(c.Ids.WallOfFlesh) > 0,
		DoneText = c => Wall(c.Hostiles) is EntityInfo w ? SummonedText(c, w) : $"Throw: wall of flesh summoned (on its bar, not in view), us at {c.Node}",
	};

	/// <summary>The summon's line (the self-test's "wall of flesh summoned" milestone): where it is and the side it came from.</summary>
	public static string SummonedText(BotContext ctx, in EntityInfo wall) =>
		$"wall of flesh summoned at ({(int)(wall.CenterX / 16f)},{(int)(wall.CenterY / 16f)}), {FromSide(Direction(wall, ctx.Body.CenterX(ctx.Physics)))}, "
		+ $"{Math.Abs(wall.CenterX - ctx.Body.CenterX(ctx.Physics)) / 16f:0} tiles off, us at {ctx.Node}";

	/// <summary>"from the east" / "from the west": the side a Wall travelling <paramref name="dir"/> came from.</summary>
	public static string FromSide(int dir) => dir < 0 ? "from the east" : "from the west";

	/// <summary>
	/// The side the Wall will come from for a doll at column <paramref name="dollX"/> (-1 west, +1 east): NPC.SpawnWOF moves the spawn
	/// point away from the players toward the nearer world edge (NPC.cs:90937-90962: the doll at or left of maxTilesX / 2 west).
	/// </summary>
	public static int SpawnSide(int dollX, int worldWidth) => dollX <= worldWidth / 2 ? -1 : 1;
}
