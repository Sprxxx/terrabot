using System;
using System.Collections.Generic;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The Brain of Cthulhu, the Crimson's boss (decompiled NPC.cs of tModLoader 1.4.4.9; the Eater of Worlds' twin for Milestone 10
/// in a Crimson world). What <see cref="BossFightSkill"/> uses:
/// <list type="bullet">
/// <item>the Brain (NPC 266, AI style 54: 160 x 110, contact damage 30, defence 14, 1250 life, NPC.cs:7590-7608) spawns 20
/// Creepers round its centre on its first tick (40 in For the Worthy; NPC.cs:33103-33113, 54162-54169). Each Creeper (267, AI
/// style 55: 30 x 30, damage 20, defence 10, 100 life, knockback resist 0.8, NPC.cs:7609-7622) is its own NPC;</item>
/// <item>first phase (ai[0] &gt;= 0): the Brain cannot be hurt (dontTakeDamage, set in SetDefaults and cleared only once ai[0]
/// is below 0, NPC.cs:7592 and 33157), drifts straight at the player at 1 px/tick (the velocity set outright, NPC.cs:33274-33296),
/// and every 120-419 ticks fades out (alpha +5 a tick, 51 ticks), jumps to a spot 12-40 tiles off on each axis that is not solid
/// and sees the player, and fades in (NPC.cs:33316-33383). With no Creeper left it turns to the second phase (NPC.cs:33298-33315);</item>
/// <item>a Creeper (ai[0] 0) orbits the Brain: farther than 90 px from its centre it steers back, v = (15 v + 8 u) / 16 for u the
/// unit vector to it; nearer, it speeds up ×1.05 a tick until |vx| + |vy| reaches 8 (NPC.cs:33411-33432), and each tick there it starts a
/// dash with chance 1 in 200 (1 in 100 more in Expert): 8 px/tick straight at the player's centre, ai[0] 1 (NPC.cs:33433-33446).
/// A dash goes on in a straight line in Classic (Expert eases it at the player, NPC.cs:33448-33461) and ends when the Creeper is
/// 700 px from the Brain or is hit (any hit: its knockback resist is not 0, NPC.cs:33463-33489). With 20 of them a dash starts
/// about every 10 ticks: those coming at us are shot first, a hit ends them;</item>
/// <item>second phase (ai[0] &lt; 0): hurt by arrows, it chases at up to 8 px/tick, eased v = (50 v + 8 u) / 51 (NPC.cs:33157-33169);
/// every 60-179 ticks (less a few when hit) it fades out over 11 ticks (alpha +25, the velocity ×0.9 a tick on top), jumps to a
/// spot 10-12 tiles off on each axis (farther along the player's run if it is the way the player moves, 16 px for each px/tick of
/// the player's speed), and fades in over 11 ticks (NPC.cs:33170-33272);</item>
/// <item>it leaves once its target is dead or not in the Crimson zone (Player.ZoneCrimson): it sinks away, falling faster each
/// tick (NPC.cs:33385-33397), and is gone once 6000 px (across plus down) from the player (NPC.cs:33117-33130). There is no daytime rule. The bar
/// shows the Brain's life and the Creepers' together (BrainOfCthuluBigProgressBar.cs:29-41);</item>
/// <item>its debuffs (Confusion and others, from a touch) are Expert only (Player.StatusFromNPC, Player.cs:20662). Confusion would
/// not turn the bot's controls round anyway: the game swaps the player's controls when confused (Player.cs:23885-23893) before
/// it asks the mods for theirs (PlayerLoader.SetControls, Player.cs:23960), where the bot writes its own.</item>
/// </list>
/// Its loot: 40-90 Crimtane Ore (ItemDropDatabase.cs:510), and from each Creeper 2-5 Tissue Samples and 5-12 Crimtane Ore, each
/// 2 in 3 (ItemDropDatabase.cs:514-515): about 47 samples and 180 ore from a whole fight. Its death sets downedBoss2 and the
/// meteor (NPC.cs:85475-85481), as the Eater of Worlds'.
/// </summary>
public static class BrainPattern
{
	public const int BrainAiStyle = 54, CreeperAiStyle = 55;
	/// <summary>Vanilla NPCID.BrainofCthulhu and Creeper.</summary>
	public const int Brain = 266, Creeper = 267;
	/// <summary>Creepers spawned with the Brain in Classic and Expert (NPC.GetBrainOfCthuluCreepersCount, NPC.cs:54162-54169).</summary>
	public const int CreeperCount = 20;
	/// <summary>The first phase's drift at the player (px/tick, set outright).</summary>
	public const float DriftSpeed = 1f;
	/// <summary>The second phase's chase: its top speed (px/tick) and the weight kept of the old velocity each tick.</summary>
	public const float ChaseSpeed = 8f, ChaseInertia = 50f;
	/// <summary>A Creeper farther than this from the Brain's centre (px) steers back to it; its top speed there (px/tick).</summary>
	public const float OrbitPx = 90f, CreeperSpeed = 8f;
	/// <summary>A Creeper's steering back: v = (n v + 8 u) / (n + 1) with this n.</summary>
	public const float CreeperInertia = 15f;
	/// <summary>Nearer the Brain than <see cref="OrbitPx"/> a Creeper's velocity grows by this each tick while |vx| + |vy| is under 8.</summary>
	public const float OrbitSpeedUp = 1.05f;
	/// <summary>A dash ends this far from the Brain's centre (px).</summary>
	public const float DashLeashPx = 700f;
	/// <summary>
	/// How far from the Brain's centre the orbiting Creepers' centres come (px): out past <see cref="OrbitPx"/> at 8 px/tick, a
	/// Creeper's outward speed falls by (8 + v) / 16 a tick under the steering back, about 11 ticks and 45 px to turn: some
	/// 135 px (the unit tests run the orbit to check it).
	/// </summary>
	public const float SwarmPx = 140f;
	/// <summary>
	/// First phase: keep the body's centre this far across from the Brain's (px): the swarm's reach, a Creeper's half width (15) and
	/// the player's (10), and two tiles. The Brain drifts at 1 px/tick, a third of a run: running from it keeps clear.
	/// </summary>
	public const float SwarmKeepPx = SwarmPx + 15f + 10f + 32f;
	/// <summary>
	/// Second phase: run from the Brain while it is this near across (px): it chases at 8 px/tick, faster than a run, and turns
	/// slowly (a fiftieth of the change a tick); run, and the dodge planner takes the pass.
	/// </summary>
	public const float ChaseKeepPx = 20 * 16f;

	public static bool IsBrain(int bossType) => bossType == Brain;

	/// <summary>The NPC types of the fight: the Brain and its Creepers.</summary>
	public static readonly int[] Parts = { Brain, Creeper };

	public static bool IsPart(int npcType) => npcType is Brain or Creeper;

	/// <summary>The second phase: every Creeper dead, the Brain chasing and open to arrows (ai[0] below 0).</summary>
	public static bool Exposed(EntityInfo brain) => brain.Ai0 < 0f;

	/// <summary>
	/// Can an arrow hurt it: not the Brain while it hides behind its Creepers (dontTakeDamage, which the game side reads; the phase
	/// by ai[0] lags it: out of the zone the AI sets ai[0] to 2 in either phase, NPC.cs:33395).
	/// </summary>
	public static bool Hittable(EntityInfo npc) => !npc.DontTakeDamage;

	/// <summary>A Creeper dashing (ai[0] 1): a straight line at where the player was when it set off.</summary>
	public static bool Dashing(EntityInfo creeper) => creeper.Ai0 == 1f;

	/// <summary>
	/// Is a dashing Creeper coming at the player's centre: its line passes within <paramref name="withinPx"/> of it, ahead of it,
	/// within <paramref name="ticks"/> ticks? Returns the tick of the closest pass, or -1.
	/// </summary>
	public static int ComingAt(EntityInfo creeper, float pcx, float pcy, float withinPx = 48f, int ticks = 90)
	{
		if (!Dashing(creeper))
			return -1;
		float dx = pcx - creeper.CenterX, dy = pcy - creeper.CenterY, v2 = creeper.VX * creeper.VX + creeper.VY * creeper.VY;
		if (v2 <= 0f)
			return -1;
		float t = (dx * creeper.VX + dy * creeper.VY) / v2;
		if (t < 0f || t > ticks)
			return -1;
		float mx = dx - creeper.VX * t, my = dy - creeper.VY * t;
		return mx * mx + my * my <= withinPx * withinPx ? (int)MathF.Ceiling(t) : -1;
	}

	/// <summary>
	/// The Brain's centre on each of the next <paramref name="horizon"/> ticks (index 0 = the next tick): drifting at the player's
	/// centre (<paramref name="pcx"/>, <paramref name="pcy"/>, taken to stay there) at 1 px/tick in the first phase, chasing it in the
	/// second; slowing ×0.9 a tick while fading out to jump (ai[0] -2).
	/// </summary>
	public static (float[] X, float[] Y) BrainPath(EntityInfo brain, float pcx, float pcy, int horizon = Dodge.Horizon)
	{
		var xs = new float[horizon];
		var ys = new float[horizon];
		float x = brain.CenterX, y = brain.CenterY, vx = brain.VX, vy = brain.VY;
		bool exposed = Exposed(brain), fading = (int)brain.Ai0 == -2;
		for (int t = 0; t < horizon; t++) {
			float dx = pcx - x, dy = pcy - y, d = MathF.Sqrt(dx * dx + dy * dy);
			if (exposed) {
				if (d > 0f) {
					vx = (vx * ChaseInertia + dx / d * ChaseSpeed) / (ChaseInertia + 1f);
					vy = (vy * ChaseInertia + dy / d * ChaseSpeed) / (ChaseInertia + 1f);
				}
				if (fading) {
					vx *= 0.9f;
					vy *= 0.9f;
				}
			}
			else if (d < DriftSpeed)
				(vx, vy) = (dx, dy);
			else
				(vx, vy) = (dx / d * DriftSpeed, dy / d * DriftSpeed);
			x += vx;
			y += vy;
			xs[t] = x;
			ys[t] = y;
		}
		return (xs, ys);
	}

	/// <summary>
	/// An orbiting Creeper's path (top-left, ticks 1 to <paramref name="horizon"/>) round the Brain's centre on each tick
	/// (<paramref name="brainX"/>, <paramref name="brainY"/>, as <see cref="BrainPath"/> has it), as its AI moves it: steering back
	/// past <see cref="OrbitPx"/>, speeding up inside it (NPC.cs:33411-33432). The dash it may start any tick is not foreseen.
	/// </summary>
	public static (float[] X, float[] Y) OrbitPath(EntityInfo creeper, float[] brainX, float[] brainY)
	{
		int n = brainX.Length;
		var xs = new float[n];
		var ys = new float[n];
		float cx = creeper.CenterX, cy = creeper.CenterY, vx = creeper.VX, vy = creeper.VY;
		for (int t = 0; t < n; t++) {
			float dx = brainX[t] - cx, dy = brainY[t] - cy, d = MathF.Sqrt(dx * dx + dy * dy);
			if (d > OrbitPx) {
				float k = CreeperSpeed / d;
				vx = (vx * CreeperInertia + dx * k) / (CreeperInertia + 1f);
				vy = (vy * CreeperInertia + dy * k) / (CreeperInertia + 1f);
			}
			else if (Math.Abs(vx) + Math.Abs(vy) < CreeperSpeed) {
				vx *= OrbitSpeedUp;
				vy *= OrbitSpeedUp;
			}
			cx += vx;
			cy += vy;
			xs[t] = cx - creeper.Width * 0.5f;
			ys[t] = cy - creeper.Height * 0.5f;
		}
		return (xs, ys);
	}

	/// <summary>
	/// A part of the fight as a <see cref="Threat"/> for the dodge planner. The Brain: drifting at the player at 1 px/tick (homing,
	/// set outright), or chasing at 8 eased by 50 (and slowing while it fades out to jump). A dashing Creeper: a straight line (eased
	/// at the player in Expert, (99 v + 9 u) / 100, NPC.cs:33448-33461). An orbiting one: its orbit round where the Brain will be
	/// (<see cref="OrbitPath"/>), the Brain in <paramref name="hostiles"/>; with the Brain out of view, a straight line.
	/// </summary>
	public static Threat Predict(EntityInfo npc, IReadOnlyList<EntityInfo> hostiles, bool expert, float pcx, float pcy)
	{
		var linear = new Threat(npc.X, npc.Y, npc.Width, npc.Height, npc.VX, npc.VY);
		if (npc.Type == Brain) {
			if (!Exposed(npc))
				return linear with { HomingSpeed = DriftSpeed };
			var chase = linear with { HomingSpeed = ChaseSpeed, HomingInertia = ChaseInertia };
			return (int)npc.Ai0 == -2 ? chase with { WindDragX = 0.9f, WindDragY = 0.9f } : chase;
		}
		if (Dashing(npc))
			return expert ? linear with { HomingSpeed = 9f, HomingInertia = 99f } : linear;
		EntityInfo? brain = null;
		foreach (EntityInfo h in hostiles)
			if (h.Type == Brain) {
				brain = h;
				break;
			}
		if (brain is not EntityInfo b)
			return linear;
		var (bx, by) = BrainPath(b, pcx, pcy);
		var (px, py) = OrbitPath(npc, bx, by);
		return linear with { PathX = px, PathY = py };
	}

	/// <summary>
	/// The Creepers in the order to shoot them: those dashing at the player (<see cref="ComingAt"/>), soonest first (a hit ends the
	/// dash), then the rest nearest first.
	/// </summary>
	public static List<EntityInfo> ShotOrder(IEnumerable<EntityInfo> creepers, float pcx, float pcy)
	{
		var coming = new List<(EntityInfo C, int T)>();
		var rest = new List<EntityInfo>();
		foreach (EntityInfo c in creepers) {
			int t = ComingAt(c, pcx, pcy);
			if (t >= 0)
				coming.Add((c, t));
			else
				rest.Add(c);
		}
		coming.Sort((a, b) => a.T.CompareTo(b.T));
		rest.Sort((a, b) => a.DistanceTo(pcx, pcy).CompareTo(b.DistanceTo(pcx, pcy)));
		var order = new List<EntityInfo>(coming.Count + rest.Count);
		foreach (var (c, _) in coming)
			order.Add(c);
		order.AddRange(rest);
		return order;
	}
}
