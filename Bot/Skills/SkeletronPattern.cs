using System;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Skeletron's attack cycle, read from its AI state (decompiled NPC.AI of tModLoader 1.4.4.9; docs M11_SPEC.md section 3).
/// <list type="bullet">
/// <item>The head (NPC 35, AI style 11, NPC.cs:22170-22512): ai[1] is the phase. 0 = hover for 800 ticks (ai[2] counts),
/// its top 250 px above the player's top, x at up to 8 px/tick; 1 = spin for 400 ticks, flying straight at the player at
/// 1.5 px/tick in Classic (the AI sets the velocity outright each tick), defence 10 less; 2 = by day (Main.IsItDay), 1000
/// damage and 9999 defence, flying at the player at 8 px/tick; 3 = leaving (lost its target).</item>
/// <item>A hand (NPC 36, AI style 12, NPC.cs:22513-22796): ai[0] is its side (-1 rests 200 px right of the head's centre,
/// +1 left), ai[1] the head's whoAmI, ai[2] the step: 0 and 3 rest (230 px below the head's top, ai[3] counting to 300 while
/// the head hovers), 1 rises (velocity.X ×0.95, velocity.Y −0.1 a tick down to −8) until its top is 200 px above the head's,
/// then 2 slams: straight at the player's centre at 18 px/tick until its top is below the player's; 4 swings out sideways
/// (velocity.Y ×0.95, velocity.X ±0.1 a tick up to 8, away from its rest side's centre) until its centre is 500 px from the
/// head's, then 5 sweeps: straight at the player's centre at 17 px/tick until it passes the player.</item>
/// <item>The Dungeon Guardian (NPC 68) is the same head AI, always in the day branch (NPC.cs:22206): 8 px/tick at us.</item>
/// </list>
/// Expert numbers are given where the AI has them; the skull bolts and the hands' defence bonus are Expert only.
/// </summary>
public static class SkeletronPattern
{
	public const int HeadAiStyle = 11, HandAiStyle = 12;
	/// <summary>Vanilla NPCID.SkeletronHead, SkeletronHand, DungeonGuardian.</summary>
	public const int Head = 35, Hand = 36, Guardian = 68;
	public const int HoverTicks = 800, SpinTicks = 400;
	/// <summary>The head's top is kept this far above the player's top while it hovers (NPC.cs:22298-22321).</summary>
	public const float HoverAbovePx = 250f;
	/// <summary>The head's height (NPC.cs:4273) and the player's (Player.height 42).</summary>
	public const int HeadHeightPx = 102, PlayerHeightPx = 42;
	/// <summary>
	/// Rows over an arena's floor the hovering head reaches up to: its top 250 px over the player's top, which is 42 px over the
	/// floor, so 292 px, 18.25 rows: an arrow at it wants these rows open over the floor (SiteFinder's sky cost).
	/// </summary>
	public const int HoverTopRows = (int)((HoverAbovePx + PlayerHeightPx + 15) / 16);
	public const float SpinSpeed = 1.5f, ExpertSpinSpeed = 3.5f, DaySpeed = 8f;
	public const float SlamSpeed = 18f, ExpertSlamSpeed = 21f, SweepSpeed = 17f, ExpertSweepSpeed = 22f;
	/// <summary>A rising hand slams once its top is this far above the head's top; a swinging one sweeps this far out (px).</summary>
	public const float SlamFromAbovePx = 200f, SweepFromPx = 500f;
	/// <summary>
	/// A hand is a threat to the dodge planner this far off (px): as far as its fastest lunge goes in the planner's look-ahead
	/// (Expert's 21 px/tick for <see cref="Dodge.Horizon"/> ticks). A slam sets off from 200 px over the head's top, some 500 px
	/// from the player, beyond the 30 tiles other threats are looked at from.
	/// </summary>
	public const float LungeReachPx = ExpertSlamSpeed * Dodge.Horizon;
	/// <summary>Ticks a wind-up is followed for to find when its lunge starts (a rise from rest takes about 50, a swing out 90).</summary>
	public const int MaxWindUpTicks = 180;

	public enum Phase { Hover, Spin, Day, Leaving }

	public static bool IsSkeletron(int bossType) => bossType == Head;

	public static Phase HeadPhase(EntityInfo head) => head.Type == Guardian ? Phase.Day : (int)head.Ai1 switch {
		0 => Phase.Hover, 1 => Phase.Spin, 2 => Phase.Day, _ => Phase.Leaving,
	};

	/// <summary>Ticks until the head starts spinning (0 while it spins).</summary>
	public static int TicksUntilSpin(EntityInfo head) => HeadPhase(head) == Phase.Hover ? Math.Max(0, HoverTicks - (int)head.Ai2) : 0;

	/// <summary>A hand lunging at where the player was (slam or sweep): a straight line.</summary>
	public static bool Lunging(EntityInfo hand) => (int)hand.Ai2 is 2 or 5;

	/// <summary>A hand winding up (rising, or swinging out): its lunge is about to come.</summary>
	public static bool WindingUp(EntityInfo hand) => (int)hand.Ai2 is 1 or 4;

	/// <summary>
	/// The head (or the Guardian) as a <see cref="Threat"/> for the dodge planner: spinning or by day it homes in on the player
	/// at its speed; hovering or leaving, it goes on as it moves (the hover keeps it 250 px over the player's top).
	/// </summary>
	public static Threat PredictHead(EntityInfo head, bool expert, float playerCX, float playerCY)
	{
		var linear = new Threat(head.X, head.Y, head.Width, head.Height, head.VX, head.VY);
		return HeadPhase(head) switch {
			Phase.Spin => linear with { HomingSpeed = expert ? ExpertSpin(head, playerCX, playerCY) : SpinSpeed },
			Phase.Day => linear with { HomingSpeed = DaySpeed },
			_ => linear,
		};
	}

	/// <summary>Expert's spin speed, as the AI scales 3.5 with the distance (×1.05 past 150 px, ×1.1 per 50 px more up to 600; NPC.cs:22401-22445).</summary>
	private static float ExpertSpin(EntityInfo head, float px, float py)
	{
		float dx = px - head.CenterX, dy = py - head.CenterY, d = MathF.Sqrt(dx * dx + dy * dy);
		float s = ExpertSpinSpeed;
		if (d > 150f)
			s *= 1.05f;
		for (float at = 200f; at <= 600f; at += 50f)
			if (d > at)
				s *= 1.1f;
		return s;
	}

	/// <summary>
	/// A hand as a <see cref="Threat"/>: lunging, a straight line; winding up, its wind-up and then the lunge at the player's
	/// centre when it comes (its head in view: <paramref name="head"/>); resting, as it moves.
	/// </summary>
	public static Threat PredictHand(EntityInfo hand, EntityInfo? head, bool expert)
	{
		var linear = new Threat(hand.X, hand.Y, hand.Width, hand.Height, hand.VX, hand.VY);
		if (head is not EntityInfo h || !WindingUp(hand))
			return linear;
		bool rise = (int)hand.Ai2 == 1;
		float dragX = rise ? 0.95f : 1f, dragY = rise ? 1f : 0.95f;
		float side = -hand.Ai0; // a swing goes out away from its rest side's centre: velocity.X += 0.1 × −ai[0]
		float ax = rise ? 0f : (expert ? 0.17f : 0.1f) * side, ay = rise ? (expert ? -0.16f : -0.1f) : 0f;
		float cap = rise ? (expert ? 13f : 8f) : (expert ? 12f : 8f);
		int at = LungeIn(hand, h, rise, dragX, dragY, ax, ay, cap);
		if (at < 0)
			return linear with { WindDragX = dragX, WindDragY = dragY, WindAX = ax, WindAY = ay, WindMaxSpeed = cap };
		return linear with {
			WindDragX = dragX, WindDragY = dragY, WindAX = ax, WindAY = ay, WindMaxSpeed = cap,
			DashAt = at, DashSpeed = rise ? (expert ? ExpertSlamSpeed : SlamSpeed) : (expert ? ExpertSweepSpeed : SweepSpeed),
			BrakeAfter = int.MaxValue, Brake = 1f,
		};
	}

	/// <summary>
	/// The tick (1 = the next) the hand's lunge starts: the AI tests where the hand is before it moves, against its head, which
	/// is taken to go on as it moves. -1 if not within <see cref="MaxWindUpTicks"/>.
	/// </summary>
	private static int LungeIn(EntityInfo hand, EntityInfo head, bool rise, float dragX, float dragY, float ax, float ay, float cap)
	{
		float x = hand.X, y = hand.Y, vx = hand.VX, vy = hand.VY, hx = head.CenterX, hy = head.Y;
		for (int t = 1; t <= MaxWindUpTicks; t++) {
			bool due = rise ? y < hy - SlamFromAbovePx : Math.Abs(x + hand.Width / 2f - hx) > SweepFromPx;
			if (due)
				return t;
			vx = Math.Clamp(vx * dragX + ax, -cap, cap);
			vy = Math.Clamp(vy * dragY + ay, -cap, cap);
			x += vx;
			y += vy;
			hx += head.VX;
			hy += head.VY;
		}
		return -1;
	}

	/// <summary>Any of Skeletron's parts or the Guardian, by AI style, as the dodge planner should model it.</summary>
	public static Threat Predict(EntityInfo npc, System.Collections.Generic.IReadOnlyList<EntityInfo> hostiles, bool expert, float playerCX, float playerCY)
	{
		if (npc.AiStyle == HeadAiStyle)
			return PredictHead(npc, expert, playerCX, playerCY);
		EntityInfo? head = null;
		foreach (EntityInfo e in hostiles)
			if (e.Id == (int)npc.Ai1 && e.AiStyle == HeadAiStyle) {
				head = e;
				break;
			}
		return PredictHand(npc, head, expert);
	}
}
