using System;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Summon a boss and fight it with a bow, kiting. Written for the Eye of Cthulhu (vanilla NPC 4: hovers above
/// the player, dashes at it three times in a row, spits Servants of Cthulhu in phase 1, loses its defense in
/// phase 2, leaves at dawn):
/// <list type="bullet">
/// <item>summon: use the summon item (the game only allows it at night) unless the boss is already here;</item>
/// <item>move: run back and forth across an arena <see cref="ArenaHalfWidth"/> tiles to either side of where the
/// fight started, turning at the ends (the Eye aims each dash at where we are, so a runner is hard to hit); when
/// the boss or a servant would touch us on that course, take the best <see cref="Dodge"/> plan instead;</item>
/// <item>shoot: servants that come within <see cref="ServantDangerTiles"/> first, else the boss, with lead and
/// drop (<see cref="Ballistics"/>); melee servants that touch us when there is no clear shot;</item>
/// <item>heal below <see cref="HealBelow"/> of max life (quick-heal).</item>
/// </list>
/// Succeeds once the game records the boss as defeated; fails if the bot dies, the boss leaves, or after
/// <see cref="MaxFightTicks"/>.
/// </summary>
public sealed class BossFightSkill : ISkill
{
	public const int ArenaHalfWidth = 20;
	public const float ServantDangerTiles = 8f;
	public const float HealBelow = 0.45f;
	/// <summary>Hostiles this close (tiles) are fed to the <see cref="Dodge"/> planner.</summary>
	public const float ThreatRadiusTiles = 30f;
	public const int WallScanTiles = 40;
	/// <summary>Past this many tiles from the start, turn round even when it is not safe.</summary>
	public const int HardHalfWidth = 40;
	/// <summary>In an arena, turn when safe this many columns before its ends, and anyway this many before.</summary>
	public const int ArenaSoftMargin = 10, ArenaHardMargin = 4; // hard: a hit knocks the bot a tile or two (4.5 px/tick)
	/// <summary>Extra room (px) kept from an arena end when braking.</summary>
	public const float BrakeMarginPx = 8f;
	/// <summary>Walking back to the arena ends within this many columns of its middle.</summary>
	public const int ArenaReturnTolerance = 6;
	private WalkToSkill backToArena;
	/// <summary>Boss speed (px/tick) that counts as a dash (the Eye: 6 / 6.8 Classic, 7+ Expert; hovers at up to 5–6).</summary>
	public const float DashSpeed = 5.5f;
	/// <summary>Ticks after a dash starts before it has gone by, and how long the gap that follows is safe for a turn.</summary>
	public const int DashPassTicks = 30, TurnWindowTicks = 50;
	/// <summary>Speed of the hypothetical dash a turn must survive.</summary>
	public const float VirtualDashSpeed = 7f;
	/// <summary>A turn from full speed: ~11 ticks to stop, ~37 to get back to full speed; the slow part is the danger.</summary>
	public const int TurnNeedsTicks = 60;
	/// <summary>Kite clearance (px) under which the dodge planner takes over, and over which it hands back.</summary>
	public const float DodgeStartGap = 16f, DodgeStopGap = 32f;
	public const int MaxFightTicks = 60 * 60 * 8;
	/// <summary>Ticks with no boss around after it appeared before concluding it left (or died).</summary>
	public const int GoneTicks = 90;

	private UseItemSkill summon;
	private bool seenBoss;
	private bool summoned;
	private int waitForBoss;
	public const int MaxWaitForBoss = 60 * 5;
	private int fightTicks;
	private int goneTicks;
	private int runDir = 1;
	private bool dodging;
	private int? deathsAtStart;
	private long lastDashTick = -100000;
	private (float, float)? lastBossV;
	private long lastForcedTurnLog = -100000;
	private long lastDodgeLog = -100000;
	private readonly System.Collections.Generic.List<Threat> threats = new();
	private int blocked;
	private bool healPressedLastTick;
	private TilePos? centre;
	private int bossLifeSeen = int.MaxValue;
	private int lifeLogged = int.MaxValue;

	/// <summary>
	/// The arena's columns and the feet row of standing on its floor: kite inside it (turning points from its ends),
	/// brake before its ends, and walk back if knocked or slid off it.
	/// </summary>
	public (int MinX, int MaxX, int StandY)? Arena { get; init; }
	public int SummonItem { get; }
	public int BossType { get; }
	public int[] MinionTypes { get; }
	/// <summary>Times the dodge planner overrode kiting.</summary>
	public int Dodges { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Boss";
	public string Key => $"Boss {BossType} {SummonItem}";

	public BossFightSkill(int summonItem, int bossType, params int[] minionTypes)
	{
		SummonItem = summonItem;
		BossType = bossType;
		MinionTypes = minionTypes;
	}

	public string Describe() => $"Boss {BossType} [{State}]" + (bossLifeSeen < int.MaxValue ? $" boss life {bossLifeSeen}" : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"Boss fight start: boss {BossType}, summon item {SummonItem}, {ctx.Combat.RangedName} with {ctx.Combat.AmmoCount} arrows, life {ctx.Combat.Life}/{ctx.Combat.LifeMax}");

	public void Stop(BotContext ctx)
	{
		summon?.Stop(ctx);
		backToArena?.Stop(ctx);
		backToArena = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.World.BossDefeated(BossType)) {
			ctx.Log.Info($"Boss fight: boss {BossType} defeated after {fightTicks} ticks, {Dodges} dodges, life {ctx.Combat.Life}/{ctx.Combat.LifeMax}");
			State = "won";
			return SkillStatus.Succeeded;
		}
		deathsAtStart ??= ctx.Deaths;
		if (ctx.BodyIssue == "dead" || ctx.Deaths > deathsAtStart)
			return Fail(ctx, "died");
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		centre ??= Arena is (int ax0, int ax1, int ay) ? new TilePos((ax0 + ax1) / 2, ay) : ctx.Node;

		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		EntityInfo? boss = ctx.Hostiles.Where(h => h.Type == BossType).Cast<EntityInfo?>().FirstOrDefault();

		if (boss == null && !seenBoss) {
			if (summoned) {
				// The item is used up the moment the game spawns the boss, a few tiles off screen: wait for it to show.
				if (++waitForBoss > MaxWaitForBoss)
					return Fail(ctx, $"no boss within {MaxWaitForBoss} ticks of summoning");
				State = "waiting for the boss";
				Move(ctx, null);
				return SkillStatus.Running;
			}
			if (!ctx.Combat.HasRanged)
				return Fail(ctx, "no bow with arrows");
			if (summon == null && ctx.Inventory.Count(SummonItem) <= 0)
				return Fail(ctx, "no summon item");
			summon ??= Start(ctx, new UseItemSkill(SummonItem));
			SkillStatus s = summon.Tick(ctx);
			if (s == SkillStatus.Failed)
				return Fail(ctx, "could not summon: " + summon.FailureReason);
			if (s == SkillStatus.Succeeded) {
				summoned = true;
				ctx.Log.Info("Boss fight: summoned");
			}
			State = "summoning";
			return SkillStatus.Running;
		}
		if (boss is EntityInfo b) {
			if (!seenBoss)
				ctx.Log.Info($"Boss fight: {b.Name} is here, life {b.Life}/{b.LifeMax}");
			seenBoss = true;
			goneTicks = 0;
			if (b.Life <= lifeLogged - b.LifeMax / 10 || b.Life <= b.LifeMax / 2 && lifeLogged > b.LifeMax / 2) {
				lifeLogged = b.Life;
				ctx.Log.Info($"Boss fight: {b.Name} at {b.Life}/{b.LifeMax}, us {ctx.Combat.Life}/{ctx.Combat.LifeMax}, {ctx.Combat.AmmoCount} arrows, {Dodges} dodges");
			}
			bossLifeSeen = Math.Min(bossLifeSeen, b.Life);
		}
		else if (++goneTicks == 1)
			ctx.Log.Info($"Boss fight: lost sight of the boss (last seen at {bossLifeSeen} life), us at {ctx.Node}");
		else if (goneTicks > GoneTicks) {
			return ctx.World.BossDefeated(BossType) ? SkillStatus.Running : Fail(ctx, "the boss left");
		}
		if (++fightTicks > MaxFightTicks)
			return Fail(ctx, $"still fighting after {MaxFightTicks} ticks");
		if (!ctx.Combat.HasRanged)
			return Fail(ctx, "out of arrows");

		bool walkingBack = BackToArena(ctx);
		if (!walkingBack)
			Move(ctx, boss);
		// Target: a servant about to bite, else the boss.
		EntityInfo? servant = ctx.Hostiles.Where(h => MinionTypes.Contains(h.Type) && h.DistanceTo(cx, cy) <= ServantDangerTiles * 16f)
			.OrderBy(h => h.DistanceTo(cx, cy)).Cast<EntityInfo?>().FirstOrDefault();
		EntityInfo? target = servant ?? boss;
		// While walking back the path may need the pickaxe or blocks: no shooting then.
		bool canShoot = !walkingBack || ctx.Output.Tool == ToolRequest.None;
		if (canShoot && target is EntityInfo t && FightSkill.ClearShot(ctx, cx, cy, t.CenterX, t.CenterY)) {
			AimSolution s = Ballistics.Lead(cx, cy, t.CenterX, t.CenterY, t.VX, t.VY, ctx.Combat.Projectile);
			ctx.Output.Tool = ToolRequest.Ranged;
			ctx.Output.AimMouse(s.AimX, s.AimY);
			bool clear = s.Hits && FightSkill.ArcClear(ctx, cx, cy, s, ctx.Combat.Projectile);
			ctx.Output.UseItem = clear;
			State = !clear ? "no clear arc" : servant != null ? "shooting a servant" : "shooting the boss";
		}
		else
			State = "kiting";
		if (walkingBack)
			State = "walking back to the arena, " + State;
		Heal(ctx);
		return SkillStatus.Running;
	}

	/// <summary>
	/// Off the arena (slid past an end, knocked off): walk back to its middle. Seen in game: the bot slid a tile past
	/// the end of a floor built over open air and fell eight tiles, then ran into the wall down there for a minute.
	/// </summary>
	private bool BackToArena(BotContext ctx)
	{
		if (Arena is not (int x0, int x1, int standY)) {
			return false;
		}
		TilePos n = ctx.Node;
		bool off = n.X < x0 || n.X > x1 || n.Y > standY + 1;
		if (!off && ctx.Body.Grounded) {
			if (backToArena != null) {
				backToArena.Stop(ctx);
				backToArena = null;
				ctx.Log.Info($"Boss fight: back in the arena at {n}");
			}
			return false;
		}
		if (!off)
			return backToArena != null && WalkBack(ctx); // in the air on the way back: keep going
		if (backToArena == null) {
			ctx.Log.Info($"Boss fight: off the arena at {n}, walking back");
			backToArena = new WalkToSkill(new GoalNearTile(new TilePos((x0 + x1) / 2, standY), ArenaReturnTolerance, ctx.Physics)) { Quiet = true };
			backToArena.Start(ctx);
		}
		return WalkBack(ctx);
	}

	private bool WalkBack(BotContext ctx)
	{
		SkillStatus s = backToArena.Tick(ctx);
		State = "walking back to the arena";
		if (s != SkillStatus.Running) {
			backToArena.Stop(ctx);
			backToArena = null;
		}
		return true;
	}

	/// <summary>Kite, unless something is about to hit us: then take the best <see cref="Dodge"/> plan; never slide off the arena.</summary>
	private void Move(BotContext ctx, EntityInfo? boss)
	{
		MoveFreely(ctx, boss, out float minX, out float maxX);
		if (Arena == null)
			return;
		// Brake before an end: never press towards it within the stopping distance (v² / 2a, braking is
		// slowdown + acceleration) plus a few pixels. Pressing towards an end only once at it still slid a tile past.
		PhysicsParams p = ctx.Physics;
		float vx = ctx.Body.VX, stop = vx * vx / (2f * p.BrakeDecel) + BrakeMarginPx;
		if (vx > 0f && maxX - ctx.Body.X <= stop && ctx.Output.Horizontal >= 0)
			ctx.Output.Horizontal = -1;
		else if (vx < 0f && ctx.Body.X - minX <= stop && ctx.Output.Horizontal <= 0)
			ctx.Output.Horizontal = 1;
		else if (vx == 0f && (ctx.Output.Horizontal > 0 && maxX - ctx.Body.X < BrakeMarginPx || ctx.Output.Horizontal < 0 && ctx.Body.X - minX < BrakeMarginPx))
			ctx.Output.Horizontal = 0;
	}

	private void MoveFreely(BotContext ctx, EntityInfo? boss, out float minX, out float maxX)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		float floorY;
		(floorY, minX, maxX) = Surroundings(ctx);
		if (Arena is (int ax0, int ax1, _)) {
			// Past the arena's ends the ground is unknown: the dodge planner treats them as walls.
			minX = Math.Max(minX, ax0 * 16f);
			maxX = Math.Min(maxX, (ax1 + 1) * 16f - p.Width);
		}
		TrackDash(ctx, boss, cx, cy);
		Kite(ctx, boss, floorY, minX, maxX);
		threats.Clear();
		foreach (EntityInfo h in ctx.Hostiles)
			if (h.Damage > 0 && h.DistanceTo(cx, cy) <= ThreatRadiusTiles * 16f)
				threats.Add(h.AiStyle == EyePattern.EyeAiStyle ? EyePattern.Predict(h, ctx.Expert) : new Threat(h.X, h.Y, h.Width, h.Height, h.VX, h.VY));
		if (threats.Count == 0)
			return;
		DodgePlan kite = Dodge.Evaluate(ctx.Body, p, floorY, minX, maxX, threats, runDir, 0);
		// Hysteresis: start dodging when the kite comes within DodgeStartGap, stop once it clears DodgeStopGap again.
		if (!kite.Hits && kite.Clearance >= (dodging ? DodgeStopGap : DodgeStartGap)) {
			dodging = false;
			return;
		}
		DodgePlan best = Dodge.Best(ctx.Body, p, floorY, minX, maxX, threats, runDir);
		if (!dodging) {
			Dodges++;
			if (ctx.Tick - lastDodgeLog >= 30) {
				lastDodgeLog = ctx.Tick;
				ctx.Log.Info($"Boss fight: dodge {(best.Dir > 0 ? "right" : best.Dir < 0 ? "left" : "stand")}{(best.JumpHold > 0 ? $" + jump {best.JumpHold}" : "")} " +
				$"(kiting {(runDir > 0 ? "right" : "left")} {(kite.Hits ? $"is hit in {kite.HitTick} ticks" : $"passes {kite.Clearance:0} px from it")}; the dodge {(best.Hits ? $"is hit in {best.HitTick}" : $"clears by {best.Clearance:0} px")})");
			}
		}
		dodging = true;
		// The planner treats the arena ends as walls, so running into one scores like standing still; in the world
		// there is no wall there and the bot would run out of the arena. Stand instead.
		bool atEnd = best.Dir < 0 ? ctx.Body.X <= minX + 1f : best.Dir > 0 && ctx.Body.X >= maxX - 1f;
		ctx.Output.Horizontal = atEnd ? 0 : best.Dir;
		ctx.Output.Jump = best.JumpHold > 0;
		// Keep going the way the dodge took us rather than turning straight back, but not out past the arena end
		// (the kite would turn straight round again: a tug of war that froze the bot at the far end).
		int offset = ctx.Node.X - centre.Value.X;
		int softHalf = HalfWidths().Soft;
		if (best.Dir != 0 && (best.Dir > 0 ? offset < softHalf : offset > -softHalf))
			runDir = best.Dir;
	}

	/// <summary>
	/// Notes the tick a dash starts: the boss's velocity jumps (a hover only changes it by its small acceleration)
	/// to dash speed, pointing at us. The Eye's next dash comes 130–150 ticks later (90–100 in Expert).
	/// </summary>
	private void TrackDash(BotContext ctx, EntityInfo? boss, float cx, float cy)
	{
		if (boss is not EntityInfo b) {
			lastBossV = null;
			return;
		}
		float speed = MathF.Sqrt(b.VX * b.VX + b.VY * b.VY);
		float dx = cx - b.CenterX, dy = cy - b.CenterY, dist = MathF.Sqrt(dx * dx + dy * dy);
		bool atUs = speed > 0f && dist > 0f && (b.VX * dx + b.VY * dy) / (speed * dist) > 0.9f;
		if (lastBossV is (float pvx, float pvy) && speed >= DashSpeed && atUs
			&& MathF.Sqrt((b.VX - pvx) * (b.VX - pvx) + (b.VY - pvy) * (b.VY - pvy)) > 1.5f)
			lastDashTick = ctx.Tick;
		lastBossV = (b.VX, b.VY);
	}

	/// <summary>
	/// Turning round means slowing to a stop, and a dash aimed at a standing player cannot be outrun
	/// (<see cref="Dodge"/>; the tests show it). For the Eye, read its attack cycle (<see cref="EyePattern"/>) and
	/// turn only when the next dash is at least <see cref="TurnNeedsTicks"/> away. For other bosses: in the gap after
	/// a dash went by, or when the turn would survive a dash starting right now from where the boss is.
	/// </summary>
	private bool SafeToTurn(BotContext ctx, EntityInfo? boss, float floorY, float minX, float maxX)
	{
		if (boss is not EntityInfo b)
			return true;
		if (b.AiStyle == EyePattern.EyeAiStyle)
			return EyePattern.TicksUntilDash(b, ctx.Expert) >= TurnNeedsTicks;
		long since = ctx.Tick - lastDashTick;
		if (since >= DashPassTicks && since <= DashPassTicks + TurnWindowTicks)
			return true;
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		float dx = cx - b.CenterX, dy = cy - b.CenterY, dist = MathF.Sqrt(dx * dx + dy * dy);
		if (dist < 1f)
			return false;
		var dash = new Threat(b.X, b.Y, b.Width, b.Height, dx / dist * VirtualDashSpeed, dy / dist * VirtualDashSpeed);
		return !Dodge.Evaluate(ctx.Body, p, floorY, minX, maxX, new[] { dash }, -runDir, 0).Hits;
	}

	private static string NextDashText(BotContext ctx, EntityInfo? boss) =>
		boss is EntityInfo b && b.AiStyle == EyePattern.EyeAiStyle ? $"next dash in {EyePattern.TicksUntilDash(b, ctx.Expert)} ticks" : "";

	/// <summary>Where kiting turns when safe (soft) and turns anyway (hard), in columns from the centre.</summary>
	private (int Soft, int Hard) HalfWidths()
	{
		if (Arena is not (int x0, int x1, _))
			return (ArenaHalfWidth, HardHalfWidth);
		int half = (x1 - x0) / 2;
		return (Math.Min(ArenaHalfWidth, half - ArenaSoftMargin), half - ArenaHardMargin);
	}

	/// <summary>Floor under the feet (px) and the walls left and right (bounds for the hitbox's left edge, px).</summary>
	private static (float FloorY, float MinX, float MaxX) Surroundings(BotContext ctx)
	{
		PhysicsParams p = ctx.Physics;
		TilePos n = ctx.Node;
		float floorY = float.PositiveInfinity;
		if (ctx.Body.Grounded)
			floorY = ctx.Body.Y + p.Height;
		else
			for (int y = n.Y + 1; y <= n.Y + 30; y++)
				if (ctx.Grid.Get(n.X, y).CanStandOn()) {
					floorY = y * 16f;
					break;
				}
		float minX = float.NegativeInfinity, maxX = float.PositiveInfinity;
		for (int dx = 1; dx <= WallScanTiles; dx++)
			if (Blocked(ctx.Grid, n.X - dx, n.Y)) {
				minX = (n.X - dx + 1) * 16f;
				break;
			}
		for (int dx = 1; dx <= WallScanTiles; dx++)
			if (Blocked(ctx.Grid, n.X + dx, n.Y)) {
				maxX = (n.X + dx) * 16f - p.Width;
				break;
			}
		return (floorY, minX, maxX);
	}

	/// <summary>A wall at body height in this column (the two cells above the feet row's floor).</summary>
	private static bool Blocked(ITileGrid g, int x, int feetY) => g.Get(x, feetY).IsSolid() || g.Get(x, feetY - 1).IsSolid();

	/// <summary>
	/// Run to one end of the arena and back. Past <see cref="ArenaHalfWidth"/> turn when <see cref="SafeToTurn"/>;
	/// past <see cref="HardHalfWidth"/>, or a tile short of a wall, turn anyway (the dodge planner handles it).
	/// </summary>
	private void Kite(BotContext ctx, EntityInfo? boss, float floorY, float minX, float maxX)
	{
		TilePos node = ctx.Node;
		int offset = node.X - centre.Value.X;
		var (soft, hardHalf) = HalfWidths();
		bool outward = runDir > 0 ? offset >= soft : offset <= -soft;
		bool atWall = runDir > 0 ? maxX - ctx.Body.X < 16f : ctx.Body.X - minX < 16f;
		bool hard = atWall || (runDir > 0 ? offset >= hardHalf : offset <= -hardHalf);
		if (hard || outward && SafeToTurn(ctx, boss, floorY, minX, maxX)) {
			runDir = -runDir;
			blocked = 0;
			if (hard && boss != null && ctx.Tick - lastForcedTurnLog > 60) {
				lastForcedTurnLog = ctx.Tick;
				ctx.Log.Info($"Boss fight: forced to turn at {node} ({(atWall ? "wall" : "far end")}), {NextDashText(ctx, boss)}");
			}
		}
		else if (ctx.Body.Grounded && Math.Abs(ctx.Body.VX) < 0.1f) {
			if (++blocked > 10) {
				runDir = -runDir; // stuck on something: turn round
				blocked = 0;
			}
		}
		else
			blocked = 0;
		ctx.Output.Horizontal = runDir;
	}

	private void Heal(BotContext ctx)
	{
		bool want = ctx.Combat.LifeFraction < HealBelow && ctx.Combat.CanQuickHeal && !healPressedLastTick;
		ctx.Output.QuickHeal = want;
		healPressedLastTick = want;
	}

	private static T Start<T>(BotContext ctx, T skill) where T : ISkill
	{
		skill.Start(ctx);
		return skill;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Boss fight failed: {reason}");
		return SkillStatus.Failed;
	}
}
