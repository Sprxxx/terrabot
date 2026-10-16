using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Melee combat against nearby hostile NPCs.
/// <list type="bullet">
/// <item>Target: keep the current target while it lives and stays near, else the nearest hostile within
/// <see cref="EngageRadius"/>.</item>
/// <item>Far: chase with a quiet <see cref="WalkToSkill"/> towards the target's feet (re-targeted as it moves);
/// close and level: steer straight at it.</item>
/// <item>In reach: stop, face it (melee swings go the way the player faces, which only the movement keys change,
/// and only between swings), aim the virtual mouse at its centre, swing.</item>
/// <item>Below <see cref="HealBelow"/> of max life: press quick-heal (pulsed so vanilla sees a fresh press).</item>
/// </list>
/// Succeeds once no hostile has been within range for <see cref="CalmTicks"/>; fails without a melee weapon.
/// </summary>
public sealed class FightSkill : ISkill
{
	public const float EngageRadius = 25 * 16f;
	/// <summary>Enemies that once came within <see cref="EngageRadius"/> stay targets out to this distance.</summary>
	public const float LeashRadius = 50 * 16f;
	/// <summary>
	/// How far (px) from where the fight started an engaged enemy is chased. Defending (the brain's danger response)
	/// keeps it short: seen in game, a slime pulled the bot from 30 tiles underground back to the surface and 55 tiles
	/// away from its mining. Hunting for drops uses <see cref="LeashRadius"/>. Measured from the start, not the bot:
	/// from the bot, chasing one slime put the other one (15 tiles off at the start) beyond the leash.
	/// </summary>
	public float Leash { get; init; } = DefendLeashTiles * 16f;
	public const float DefendLeashTiles = 24f;
	public const int CalmTicks = 90;
	public const float HealBelow = 0.4f;
	/// <summary>Hits of the hardest recent kind that life should still take after a heal is due (see <see cref="HealAt"/>).</summary>
	public const float HealHitsInHand = 2.5f;

	/// <summary>
	/// The life fraction to heal below: <see cref="HealBelow"/>, or higher while <see cref="HealHitsInHand"/> of the hardest
	/// recent hits would take what is left. Seen in game (measured run S6): hit for 20 a time, the bot healed at 22/100 (a
	/// mushroom, +15), then could not heal again; all 14 deaths came within seconds of a heal, healing items in the bag.
	/// </summary>
	public static float HealAt(BotContext ctx) =>
		ctx.Combat.LifeMax > 0 ? System.Math.Max(HealBelow, HealHitsInHand * ctx.BiggestRecentHit / (float)ctx.Combat.LifeMax) : HealBelow;
	/// <summary>Shoot at targets up to this far (px); arrows lose height beyond.</summary>
	public const float RangedRange = 40 * 16f;
	/// <summary>Ground targets this close are fought in melee when a melee weapon is at hand.</summary>
	public const float MeleePreferredWithin = 6 * 16f;
	/// <summary>NPC AI styles that fly (vanilla NPCAIStyleID: 2 Demon Eye, 4 Eye of Cthulhu, 5 Flying, 14 Bat).</summary>
	public static readonly int[] FlyingAiStyles = { 2, 4, 5, 14 };
	private const int RetargetPathTicks = 30;
	/// <summary>
	/// Per enemy: the closest it has been and the ticks spent on it since. Per enemy, not per target switch: seen in
	/// game (world_eye run 37), two red slimes out of reach above took turns as the target every tick, each switch reset
	/// the count, and one fight lasted 7 game days.
	/// </summary>
	private readonly Dictionary<int, (float Best, int Since)> progress = new();
	/// <summary>Ticks without getting closer before an enemy is left alone.</summary>
	public const int MaxTicksNoCloser = 600;
	/// <summary>A fight in which no enemy loses life for this long is given up altogether.</summary>
	public const int MaxTicksNoProgress = 1200;
	private readonly Dictionary<int, int> lifeSeen = new();
	private int ticksRun, lastProgress, killsSeen;
	private bool wasInReach;
	/// <summary>An enemy given up on is left alone this long (also by later fights and the danger check).</summary>
	public const int IgnoreTicks = 60 * 30;
	/// <summary>An ignored enemy this close (px) is fought anyway: it is biting.</summary>
	public const float IgnoredStillFoughtWithin = 3 * 16f;
	/// <summary>An ignored enemy starts a fight (the brain's danger check) only this close: less than <see cref="IgnoredStillFoughtWithin"/>, for hysteresis.</summary>
	public const float IgnoredDangerWithin = 2 * 16f;
	/// <summary>Enemies more than this many rows below <see cref="BotContext.DeepestRow"/> are not chased (unless they bite).</summary>
	public const int DeepChaseMargin = 5;
	/// <summary>Below the depth limit, an attacker is still fought this close (it is on the bot), not chased off after.</summary>
	public const float DeepAttackerWithin = 4 * 16f;
	/// <summary>Swinging at a target in reach this long without it losing life: it cannot be hit from here; leave it.</summary>
	public const int MaxSwingTicksNoDamage = 300;
	private int swingWatchId = -1, swingWatchLife, swingTicksNoDamage;
	private const int RetargetDistance = 6;
	private const int RetargetAnywayTicks = 180;

	private WalkToSkill approach;
	private TilePos approachTarget;
	private int approachAge;
	private int calm;
	private int targetId = -1;
	private string targetName = "";
	private float targetDistance;
	private bool healPressedLastTick;
	private int shotAt = -1;
	/// <summary>Targets the approach found no path to (behind walls): not fought again.</summary>
	private readonly System.Collections.Generic.HashSet<int> unreachable = new();
	private readonly System.Collections.Generic.HashSet<int> engaged = new();

	public int Kills { get; private set; }
	public string State { get; private set; } = "new";
	public string Name => "Fight";
	public string FailureReason { get; private set; } = "";

	public string Describe() =>
		targetId >= 0 ? $"Fight {targetName} ({targetDistance / 16f:0.0} tiles) [{State}] kills {Kills}" : $"Fight [{State}] kills {Kills}";

	/// <summary>Where the fight started, for the leash: set on the first tick (at Start the body can be stale).</summary>
	private float anchorX, anchorY;
	private bool anchored;

	public void Start(BotContext ctx) => ctx.Log.Info($"Fight start: {ctx.Hostiles.Count} hostile(s) near, weapon {ctx.Combat.WeaponName}");

	public void Stop(BotContext ctx)
	{
		approach?.Stop(ctx);
		approach = null;
		ctx.Output = MoveInput.None;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		if (!ctx.Combat.HasWeapon && !ctx.Combat.HasRanged) {
			FailureReason = "no weapon in the hotbar (melee, or a bow with arrows)";
			ctx.Log.Warn("Fight failed: " + FailureReason);
			return SkillStatus.Failed;
		}
		// The whole fight going nowhere: no enemy has lost any life for a long while. Seen in game (world_eye run 57): two
		// slimes 2.7 tiles off behind a ledge, taking turns as the target and hopping in and out of reach, so no
		// per-enemy rule ever fired, for 24 game hours. Leave them all alone for a while, even close by.
		bool hurtOne = false;
		foreach (EntityInfo h in ctx.Hostiles) {
			if (lifeSeen.TryGetValue(h.Id, out int was) && h.Life < was)
				hurtOne = true;
			lifeSeen[h.Id] = h.Life;
		}
		if (hurtOne || Kills > killsSeen) {
			killsSeen = Kills;
			lastProgress = ticksRun;
		}
		if (++ticksRun - lastProgress > MaxTicksNoProgress) {
			float bx = ctx.Body.CenterX(ctx.Physics), by = ctx.Body.CenterY(ctx.Physics);
			foreach (EntityInfo h in ctx.Hostiles)
				if (h.DistanceTo(bx, by) <= EngageRadius)
					ctx.IgnoreEvenClose(h.Id, IgnoreTicks);
			ctx.Log.Info($"Fight: no enemy hurt in {MaxTicksNoProgress} ticks; leaving the ones around alone");
			State = "done";
			return SkillStatus.Succeeded;
		}

		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		if (!anchored) {
			anchored = true;
			anchorX = cx;
			anchorY = cy;
		}
		EntityInfo? target = PickTarget(ctx, cx, cy);
		if (target == null) {
			approach = null;
			if (++calm >= CalmTicks) {
				ctx.Log.Info($"Fight over: {Kills} kill(s)");
				State = "done";
				return SkillStatus.Succeeded;
			}
			State = "watching";
			MindTheEdge(ctx);
			Heal(ctx);
			return SkillStatus.Running;
		}
		calm = 0;
		EntityInfo t = target.Value;
		targetDistance = t.DistanceTo(cx, cy);
		// No closer for a long while (in reach does not count as stuck): give up on it, like an enemy with no path to it.
		bool known = progress.TryGetValue(t.Id, out var pr);
		if (!known || targetDistance < pr.Best - 16f)
			progress[t.Id] = (targetDistance, 0);
		else if (++pr.Since <= MaxTicksNoCloser || wasInReach)
			progress[t.Id] = pr;
		else {
			// Not "within 6 tiles": seen in game, a red slime 3.8 tiles off (out of sword reach, no way closer) was
			// chased for over 7000 ticks without a step taken.
			unreachable.Add(t.Id);
			ctx.Ignore(t.Id, IgnoreTicks);
			ctx.Log.Info($"Fight: no closer to {t.Name} in {MaxTicksNoCloser} ticks ({targetDistance / 16f:0.0} tiles); leaving it");
			targetId = -1;
			progress.Remove(t.Id);
			State = "giving up on it";
			return SkillStatus.Running;
		}

		float dx = t.CenterX - cx, dy = t.CenterY - cy;
		float reachX = p.Width * 0.5f + ctx.Combat.WeaponReach + t.Width * 0.5f - 4f;
		float reachY = p.Height * 0.5f + t.Height * 0.5f + 8f;
		bool inReach = ctx.Combat.HasWeapon && Math.Abs(dx) <= reachX && Math.Abs(dy) <= reachY && ClearShot(ctx, cx, cy, t.CenterX, t.CenterY);
		if (inReach)
			progress[t.Id] = (Math.Min(targetDistance, progress[t.Id].Best), 0); // swinging at it is progress (the no-damage rule covers swings that do nothing)
		wasInReach = inReach;

		bool flier = FlyingAiStyles.Contains(t.AiStyle);
		if (!inReach && UseRanged(ctx, t, cx, cy)) {
			Shoot(ctx, t, cx, cy);
			if (flier)
				KeepOffEdge(ctx);
			MindTheEdge(ctx);
			Heal(ctx);
			return SkillStatus.Running;
		}

		if (inReach) {
			approach = null;
			int want = dx >= 0 ? 1 : -1;
			ctx.Output.Tool = ToolRequest.Weapon;
			ctx.Output.AimMouse(t.CenterX, t.CenterY);
			if (ctx.Combat.Facing != want && ctx.Combat.ItemAnimation == 0) {
				ctx.Output.Horizontal = want; // turn first; the swing starts next tick facing the right way
				State = "turning";
			}
			else {
				ctx.Output.UseItem = true;
				State = "swinging";
				if (t.Id != swingWatchId || t.Life < swingWatchLife) {
					swingWatchId = t.Id;
					swingWatchLife = t.Life;
					swingTicksNoDamage = 0;
				}
				else if (++swingTicksNoDamage > MaxSwingTicksNoDamage) {
					unreachable.Add(t.Id);
					ctx.IgnoreEvenClose(t.Id, IgnoreTicks);
					TilePos me = ctx.Node, it = new((int)MathF.Floor(t.CenterX / 16f), (int)MathF.Floor(t.CenterY / 16f));
					ctx.Log.Info($"Fight: {MaxSwingTicksNoDamage} ticks of swinging at {t.Name} in reach ({targetDistance / 16f:0.0} tiles) did no damage; "
						+ $"leaving it (F feet, T target):\n{WalkToSkill.Around(ctx, me, it)}");
					targetId = -1;
					swingWatchId = -1;
					State = "cannot hit it";
				}
			}
		}
		else if (flier) {
			// A flier is never chased on foot: it comes at the player on its own (AI 2, 5 and 14 all fly at their target), and
			// the ground beneath it is wherever it happens to be over. In sight with a sword: hold ground and meet it (chasing
			// demon eyes' "feet tile" cost the bot 150 life on Classic). Out of sight: wait for it in the open, shooting the
			// moment the arc clears. Seen in game (measured run M10a): chasing eaters of souls walked to "beneath" them, a
			// corruption chasm's floor 26 rows and more down ("target (1780,315) -> StandAt(1780,341)"), and dug tunnels to them
			// at rows 333-340 where no shot was ever clear (45 fights, 0 kills, 13 "out of sight and there is no way to it");
			// over the chasm at x 1777, three falls to its floor at row 401 killed the bot (801-804 damage).
			if (ctx.Combat.HasWeapon && ClearShot(ctx, cx, cy, t.CenterX, t.CenterY))
				HoldAndMeet(ctx, t, dx, dy, reachX, reachY);
			else
				WaitInTheOpen(ctx, t, dx);
			KeepOffEdge(ctx);
		}
		else {
			Approach(ctx, t, dx, dy);
			// Swing early at targets about to come into reach (slimes hop in), unless the path is mining/building.
			if (ctx.Output.Tool == ToolRequest.None && Math.Abs(dx) <= reachX + 48f && Math.Abs(dy) <= reachY + 16f
				&& ctx.Combat.Facing == Math.Sign(dx)) {
				ctx.Output.Tool = ToolRequest.Weapon;
				ctx.Output.UseItem = true;
				ctx.Output.AimMouse(t.CenterX, t.CenterY);
			}
		}
		MindTheEdge(ctx);
		Heal(ctx);
		return SkillStatus.Running;
	}

	/// <summary>
	/// Shoot rather than close in: with a bow and arrows, at fliers (demon eyes, bosses: chasing them on foot is
	/// hopeless) and at anything out of melee reach within <see cref="RangedRange"/>, as long as the line of fire
	/// is clear (arrows stop at blocks). Without a melee weapon, always.
	/// </summary>
	private bool UseRanged(BotContext ctx, EntityInfo t, float cx, float cy)
	{
		CombatInfo k = ctx.Combat;
		if (!k.HasRanged)
			return false;
		if (t.DistanceTo(cx, cy) > RangedRange)
			return !k.HasWeapon;
		bool flier = FlyingAiStyles.Contains(t.AiStyle);
		return (flier || !k.HasWeapon || t.DistanceTo(cx, cy) > MeleePreferredWithin) && ClearShot(ctx, cx, cy, t.CenterX, t.CenterY);
	}

	private void Shoot(BotContext ctx, EntityInfo t, float cx, float cy)
	{
		approach?.Stop(ctx);
		approach = null;
		AimSolution s = Ballistics.Lead(cx, cy, t.CenterX, t.CenterY, t.VX, t.VY, ctx.Combat.Projectile);
		ctx.Output.Tool = ToolRequest.Ranged;
		ctx.Output.AimMouse(s.AimX, s.AimY);
		bool clear = s.Hits && ArcClear(ctx, cx, cy, s, ctx.Combat.Projectile);
		ctx.Output.UseItem = clear;
		if (s.Hits && shotAt != t.Id) {
			shotAt = t.Id;
			ctx.Log.Info($"Fight: shooting {t.Name} with {ctx.Combat.RangedName} ({ctx.Combat.AmmoCount} arrows), arrival in {s.Ticks} ticks");
		}
		State = clear ? $"shooting (arrives in {s.Ticks}t)" : s.Hits ? "no clear arc" : "out of range";
	}

	/// <summary>The arrow's simulated flight up to the target (the arc, not the straight line) passes no solid cell.</summary>
	public static bool ArcClear(BotContext ctx, float ox, float oy, AimSolution s, ProjectileModel m)
	{
		float angle = MathF.Atan2(s.AimY - oy, s.AimX - ox);
		float x = ox, y = oy, vx = MathF.Cos(angle) * m.Speed, vy = MathF.Sin(angle) * m.Speed;
		int updates = Math.Max(1, s.Ticks) * m.UpdatesPerTick;
		for (int u = 0; u < updates; u++) {
			if (u >= m.GravityDelay)
				vy = MathF.Min(vy + m.Gravity, m.MaxFallSpeed);
			x += vx;
			y += vy;
			if (ctx.Grid.Get((int)MathF.Floor(x / 16f), (int)MathF.Floor(y / 16f)).IsSolid())
				return false;
		}
		return true;
	}

	/// <summary>
	/// Is the enemy in liquid? Water enemies (jellyfish, piranhas) are left alone unless they bite: seen in game, the bot
	/// chased a jellyfish out to 20 tiles, into the water it was told to keep out of.
	/// </summary>
	public static bool InWater(BotContext ctx, EntityInfo e) =>
		ctx.Grid.Get((int)MathF.Floor(e.CenterX / 16f), (int)MathF.Floor(e.CenterY / 16f)).IsLiquid();
	/// <summary>No solid cell on the straight line between the two points (sampled every 8 px).</summary>
	public static bool ClearShot(BotContext ctx, float x0, float y0, float x1, float y1)
	{
		float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
		int steps = Math.Max(1, (int)(len / 8f));
		for (int i = 1; i < steps; i++) {
			float f = (float)i / steps;
			int tx = (int)MathF.Floor((x0 + (x1 - x0) * f) / 16f), ty = (int)MathF.Floor((y0 + (y1 - y0) * f) / 16f);
			if (ctx.Grid.Get(tx, ty).IsSolid())
				return false;
		}
		return true;
	}

	/// <summary>
	/// Not chased down into the depths the gear is not fit for; what just bit the bot only while it is on the bot. Seen in
	/// game (world_eye run 45): cave bats hit and flew off down, and the bot followed them into the caverns and died.
	/// </summary>
	/// <summary>Not below the depth the gear allows, unless it is hitting the bot from close by (also what counts as danger).</summary>
	public static bool NotTooDeep(BotContext ctx, EntityInfo e, float d) =>
		e.FeetTile.Y - DeepChaseMargin <= ctx.DeepestRow || e.Id == ctx.RecentAttacker && d <= DeepAttackerWithin;

	private EntityInfo? PickTarget(BotContext ctx, float cx, float cy)
	{
		// With a bow, anything within shooting range is a target (a demon eye circling at 30 tiles, a boss).
		float engage = ctx.Combat.HasRanged ? Math.Max(EngageRadius, RangedRange) : EngageRadius;
		EntityInfo? current = null, nearest = null;
		float best = float.MaxValue;
		foreach (EntityInfo e in ctx.Hostiles) {
			float d = e.DistanceTo(cx, cy);
			if (d <= engage)
				engaged.Add(e.Id); // once engaged, keep fighting it while it stays within the leash
			// Enemies we found no path to (behind a wall) are left alone; out of sight is fine otherwise (we walk up).
			bool candidate = (d <= engage || (engaged.Contains(e.Id) && e.DistanceTo(anchorX, anchorY) <= Leash)) && !unreachable.Contains(e.Id) && ctx.Fightable(e.Id, d)
				&& NotTooDeep(ctx, e, d)
				&& (!InWater(ctx, e) || e.Id == ctx.RecentAttacker); // nor into water after jellyfish and piranhas
			if (e.Id == targetId && candidate)
				current = e;
			if (candidate && d < best) {
				best = d;
				nearest = e;
			}
		}
		engaged.RemoveWhere(id => !ctx.Hostiles.Any(h => h.Id == id));
		if (targetId >= 0 && current == null) {
			// The target vanished: killed (or despawned / ran off).
			if (!ctx.Hostiles.Any(e => e.Id == targetId)) {
				Kills++;
				ctx.Log.Info($"Fight: {targetName} gone (kill #{Kills})");
			}
			targetId = -1;
		}
		EntityInfo? pick = current ?? nearest;
		// Whatever just bit us comes first: seen in game, the bot chased a slime 20 tiles off while two others bit it.
		int attacker = ctx.RecentAttacker;
		if (attacker >= 0 && attacker != pick?.Id && !unreachable.Contains(attacker))
			foreach (EntityInfo e in ctx.Hostiles)
				if (e.Id == attacker && e.DistanceTo(cx, cy) <= engage && NotTooDeep(ctx, e, e.DistanceTo(cx, cy))) {
					pick = e;
					break;
				}
		if (pick is EntityInfo e2 && e2.Id != targetId) {
			targetId = e2.Id;
			targetName = e2.Name;
			ctx.Log.Info($"Fight: target {e2.Name} #{e2.Id} at {e2.FeetTile}, {e2.DistanceTo(cx, cy) / 16f:0.0} tiles, life {e2.Life}/{e2.LifeMax}");
		}
		return pick;
	}

	/// <summary>Stand, face the flier and swing as it comes within <see cref="MeetMargin"/> of reach.</summary>
	private void HoldAndMeet(BotContext ctx, EntityInfo t, float dx, float dy, float reachX, float reachY)
	{
		approach?.Stop(ctx);
		approach = null;
		int want = dx >= 0 ? 1 : -1;
		ctx.Output.Tool = ToolRequest.Weapon;
		ctx.Output.AimMouse(t.CenterX, t.CenterY);
		if (ctx.Combat.Facing != want && ctx.Combat.ItemAnimation == 0) {
			ctx.Output.Horizontal = want;
			State = "turning to meet";
			return;
		}
		ctx.Output.UseItem = Math.Abs(dx) <= reachX + MeetMargin && Math.Abs(dy) <= reachY + MeetMargin;
		State = ctx.Output.UseItem ? "meeting it" : "holding";
		// While it is still far off, step over to drops lying close by (lenses from the last one): with fliers
		// coming one after another the fight never ends, so loot left for "after the fight" was never picked up.
		if (!ctx.Output.UseItem && ctx.Combat.ItemAnimation == 0 && MathF.Sqrt(dx * dx + dy * dy) > PickUpWhileFarTiles * 16f) {
			PhysicsParams p = ctx.Physics;
			float bx = ctx.Body.CenterX(p), by = ctx.Body.CenterY(p);
			DropInfo? drop = ctx.Drops.Where(d => Math.Abs(d.CenterX - bx) <= PickUpRadiusTiles * 16f && Math.Abs(d.CenterY - by) <= 3 * 16f)
				.OrderBy(d => Math.Abs(d.CenterX - bx)).Cast<DropInfo?>().FirstOrDefault();
			if (drop is DropInfo d && Math.Abs(d.CenterX - bx) > 8f) {
				ctx.Output.Horizontal = Math.Sign(d.CenterX - bx);
				State = $"picking up {d.Name} while it is far";
			}
		}
	}

	/// <summary>A flier out of sight: stand, face it with the weapon to hand (the bow if carried), and let it come.</summary>
	private void WaitInTheOpen(BotContext ctx, EntityInfo t, float dx)
	{
		approach?.Stop(ctx);
		approach = null;
		int want = dx >= 0 ? 1 : -1;
		ctx.Output.Tool = ctx.Combat.HasRanged ? ToolRequest.Ranged : ToolRequest.Weapon;
		ctx.Output.AimMouse(t.CenterX, t.CenterY);
		if (ctx.Combat.Facing != want && ctx.Combat.ItemAnimation == 0)
			ctx.Output.Horizontal = want;
		State = "waiting for it in the open";
	}

	/// <summary>
	/// How far (tiles) a hit throws the player: vanilla Player.Hurt sets velocity (4.5 * hitDirection, -4.5 * 7/9 = -3.5) for
	/// an enemy's 4.5 knockback, so about 17 ticks in the air at gravity 0.4, some 79 px.
	/// </summary>
	public const int KnockbackTiles = 5;

	/// <summary>
	/// Standing against fliers within <see cref="KnockbackTiles"/> of a sheer drop on one side (deeper than a fall the body
	/// takes unhurt, <see cref="PhysicsParams.SafeFallTiles"/>): a step away from it, so a hit does not throw the bot over
	/// the edge. Both sides sheer (a ledge, a pillar): stay. Seen in game (measured run M10a): knocked back by eaters of souls
	/// ("Hurt 12 ... knockback 4.5") at a chasm's edge at x 1778, the bot fell 105 rows to (1777,401) and died.
	/// </summary>
	private void KeepOffEdge(BotContext ctx)
	{
		if (ctx.Output.Horizontal != 0 || !ctx.Body.Grounded)
			return;
		TilePos n = ctx.Node;
		int left = EdgeWithin(ctx, n, -1), right = EdgeWithin(ctx, n, 1);
		if ((left > 0) == (right > 0))
			return;
		ctx.Output.Horizontal = left > 0 ? 1 : -1;
		State += ", off the edge";
	}

	/// <summary>
	/// Columns short of a sheer drop (<see cref="EdgeWithin"/>) inside which the fight presses no key towards it: a body at
	/// run speed (3 px a tick) stops in 3 * 3 / (2 * 0.2) = 22.5 px with the keys let go (vanilla HorizontalMovement's
	/// runSlowdown), and stands on the lip until 26 px past its canonical spot (6 px gap plus the 20 px hitbox), so the drop's
	/// column next but one is the last that is safe to run at.
	/// </summary>
	public const int EdgeMarginTiles = 2;
	/// <summary>
	/// Ticks a hit's knockback carries the body: 4.5 px a tick (vanilla Player.Hurt) lost at 0.1 a tick in the air (half the
	/// runSlowdown, HorizontalMovement): 45. Meanwhile the edge is minded even while a path walk has the keys.
	/// </summary>
	public const int KnockbackTicks = 45;

	/// <summary>
	/// Whatever the fight does, not over a sheer drop (deeper than a safe fall, <see cref="EdgeWithin"/>):
	/// <list type="bullet">
	/// <item>no key pressed towards one within <see cref="EdgeMarginTiles"/> (running at an enemy, stepping to a drop),
	/// a one-tick turn to face an enemy aside;</item>
	/// <item>carried towards one (a hit's knockback, the rest of a jump) so far it would go over before stopping: the key
	/// against the motion, which takes off runSlowdown + runAcceleration = 0.28 px a tick instead of 0.1 in the air
	/// (vanilla HorizontalMovement), 36 px to stop from a hit's 4.5 instead of about 80;</item>
	/// <item>in the air over one: the key towards the nearer side with ground.</item>
	/// </list>
	/// Not while the path walk drives (it jumps and bridges gaps on purpose), unless a hit just threw the body
	/// (<see cref="KnockbackTicks"/>) or it moves faster than the keys make it. Seen in game (measured run M10c, world_eow):
	/// hit by eaters of souls and a zombie in the air at (1708-1709,267-270), "knockback 4.5 dir -1", the bot was carried 5
	/// columns west into a corruption chasm while the fight shot at fliers with no key pressed, and fell to (1702,364) and
	/// (1701,362), 715 and 665 damage; KeepOffEdge acts only on the ground and against fliers.
	/// </summary>
	private void MindTheEdge(BotContext ctx)
	{
		PhysicsParams p = ctx.Physics;
		BodyState b = ctx.Body;
		bool thrown = Math.Abs(b.VX) > p.MaxHorizontalSpeed || ctx.LastAttackedTick >= 0 && ctx.Tick - ctx.LastAttackedTick <= KnockbackTicks;
		if (approach != null && !thrown)
			return;
		TilePos n = ctx.Node;
		int press = ctx.Output.Horizontal;
		if (press != 0 && ctx.Combat.Facing == press && EdgeWithin(ctx, n, press) is int k && k > 0 && k <= EdgeMarginTiles) {
			ctx.Output.Horizontal = 0;
			State += ", stopped at the edge";
		}
		if (!b.Grounded && SheerUnder(ctx, n)) {
			int left = GroundWithin(ctx, n, -1), right = GroundWithin(ctx, n, 1);
			if (left > 0 || right > 0) {
				ctx.Output.Horizontal = right > 0 && (left == 0 || right <= left) ? 1 : -1;
				State += ", back to the lip";
				return;
			}
		}
		int moving = Math.Sign(b.VX);
		if (moving == 0 || ctx.Output.Horizontal == -moving || EdgeWithin(ctx, n, moving) is not (int e and > 0))
			return;
		float decel = b.Grounded ? p.RunSlowdown : p.RunSlowdown * 0.5f;
		float coast = b.VX * b.VX / (2f * decel);
		float toEdge = (e - 1) * Core.Pathfinding.Body.TileSize + EdgeSupportPx;
		if (coast >= toEdge) {
			ctx.Output.Horizontal = -moving;
			State += ", braking at the edge";
		}
	}

	/// <summary>How far (px) past its canonical spot the body still stands on the lip: the 6 px gap to the next column plus its 20 px width.</summary>
	private const float EdgeSupportPx = 26f;

	/// <summary>Nothing to stand on under either of the body's columns within a safe fall.</summary>
	private static bool SheerUnder(BotContext ctx, TilePos feet) => Sheer(ctx, feet.X, feet.Y) && Sheer(ctx, feet.X + 1, feet.Y);

	private static bool Sheer(BotContext ctx, int x, int feetY)
	{
		for (int d = 1; d <= ctx.Physics.SafeFallTiles + 1; d++)
			if (ctx.Grid.Get(x, feetY + d).CanStandOn())
				return false;
		return true;
	}

	/// <summary>Columns (1..<see cref="KnockbackTiles"/>) to the first one that way with ground within a safe fall; 0 if none.</summary>
	private static int GroundWithin(BotContext ctx, TilePos feet, int dir)
	{
		for (int k = 1; k <= KnockbackTiles; k++) {
			int x = dir > 0 ? feet.X + 1 + k : feet.X - k;
			if (ctx.Grid.Get(x, feet.Y).IsSolid())
				return 0;
			if (!Sheer(ctx, x, feet.Y))
				return k;
		}
		return 0;
	}

	/// <summary>Open air over the head this many rows: as far as a fight engages (<see cref="EngageRadius"/>), where fliers come from.</summary>
	public const int OpenSkyRows = (int)(EngageRadius / 16f);

	/// <summary>
	/// Is the sky open over the body standing at <paramref name="feet"/>: no solid cell in its two columns for
	/// <see cref="OpenSkyRows"/> rows over its head? Not in a tunnel, a cave or under a chasm's overhang.
	/// </summary>
	public static bool OpenSky(BotContext ctx, TilePos feet)
	{
		int head = feet.Y - Core.Pathfinding.Body.HeightTiles + 1;
		for (int y = head - 1; y >= head - OpenSkyRows; y--)
			if (ctx.Grid.Get(feet.X, y).IsSolid() || ctx.Grid.Get(feet.X + 1, y).IsSolid())
				return false;
		return true;
	}

	/// <summary>Columns (1..<see cref="KnockbackTiles"/>) to a sheer drop on that side of the feet tile; 0 if none.</summary>
	public static int EdgeWithin(BotContext ctx, TilePos feet, int dir)
	{
		int maxDrop = ctx.Physics.SafeFallTiles;
		for (int k = 1; k <= KnockbackTiles; k++) {
			int x = dir > 0 ? feet.X + 1 + k : feet.X - k; // the body is two columns wide from its feet tile
			if (ctx.Grid.Get(x, feet.Y).IsSolid())
				return 0; // a wall that way: the bot is stopped there, not thrown over anything
			int drop = 0;
			while (drop <= maxDrop && !ctx.Grid.Get(x, feet.Y + 1 + drop).CanStandOn())
				drop++;
			if (drop > maxDrop)
				return k;
		}
		return 0;
	}

	/// <summary>Drops within this many tiles (and 3 rows) are picked up while the flier is over <see cref="PickUpWhileFarTiles"/> away.</summary>
	public const float PickUpRadiusTiles = 10f, PickUpWhileFarTiles = 9f;

	/// <summary>Start a swing this far (px) before a flier is in reach: the swing takes a while to come round.</summary>
	public const float MeetMargin = 32f;

	private void Approach(BotContext ctx, EntityInfo t, float dx, float dy)
	{
		PhysicsParams p = ctx.Physics;
		// Close and roughly level: just run at it (the path would be stale before it is finished), unless a sheer drop lies
		// between: then the path's way, which jumps or bridges it. Seen in game (measured run M10c): closing in on a zombie on a
		// corruption chasm's far side, the bot ran off the lip at (1778,294) and fell to (1778,396), 765 damage.
		int towards = Math.Sign(dx);
		bool dropBetween = EdgeWithin(ctx, ctx.Node, towards) is int edge && edge > 0 && edge * 16f < Math.Abs(dx);
		if (Math.Abs(dx) <= 4 * 16f && Math.Abs(dy) <= 1.5f * 16f && !dropBetween) {
			approach = null;
			ctx.Output.Horizontal = Math.Sign(dx);
			State = "closing in";
			return;
		}
		TilePos feet = t.FeetTile;
		approachAge++;
		// Re-aim only when the target has really moved, or now and then: a hopping slime re-aimed every half second, and
		// each new path began with a pillar step that never got finished (seen in game: a minute at the bottom of a shaft).
		bool moved = approachAge > RetargetPathTicks && feet.ChebyshevTo(approachTarget) > RetargetDistance;
		if (approach == null || moved || approachAge > RetargetAnywayTicks) {
			approach = new WalkToSkill(feet) { Quiet = true, Tolerance = 2, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
			approach.Start(ctx);
			approachTarget = feet;
			approachAge = 0;
		}
		SkillStatus s = approach.Tick(ctx);
		if (s == SkillStatus.Failed && !ClearShot(ctx, ctx.Body.CenterX(p), ctx.Body.CenterY(p), t.CenterX, t.CenterY)) {
			unreachable.Add(t.Id);
			ctx.Ignore(t.Id, IgnoreTicks);
			ctx.Log.Info($"Fight: {t.Name} is out of sight and there is no way to it; leaving it");
			targetId = -1;
		}
		if (s != SkillStatus.Running)
			approach = null;
		State = "chasing";
	}

	private void Heal(BotContext ctx)
	{
		bool want = ctx.Combat.LifeFraction < HealAt(ctx) && ctx.Combat.CanQuickHeal && !healPressedLastTick;
		ctx.Output.QuickHeal = want;
		healPressedLastTick = want;
		if (want)
			ctx.Log.Info($"Fight: quick-heal at {ctx.Combat.Life}/{ctx.Combat.LifeMax}");
	}
}
