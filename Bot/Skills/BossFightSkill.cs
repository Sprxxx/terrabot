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
/// <para>
/// A worm boss (the Eater of Worlds, <see cref="WormPattern"/>) is fought differently, chosen by <see cref="BossType"/>:
/// <list type="bullet">
/// <item>the boss is every segment of it (head, bodies, tail; a split makes more heads): alive while any is in view or its
/// health bar shows life (<see cref="IWorldInfo.BossBarLife"/>: burrowed out of view), won once none is and the game records
/// it defeated, gone after <see cref="WormPattern.GoneTicks"/> with none in view and its bar empty;</item>
/// <item>shoot the nearest segment the arrow can reach (any segment's damage counts);</item>
/// <item>move: run away from the nearest head, from where it will cross our height (a head out of the ground falls),
/// and past it when an end of the arena leaves no room; the dodge planner jumps over or runs from under a head with a
/// falling-head model, no Eye of Cthulhu dash model or dash timing;</item>
/// <item>stay inside the arena, or within <see cref="HardHalfWidth"/> of where the fight started, so as not to leave the
/// Corruption (the worm despawns then); knocked off it, or down a chasm deeper than a safe fall, walk back.</item>
/// </list>
/// </para>
/// <para>
/// Skeletron (<see cref="SkeletronPattern"/>, docs M11_SPEC.md section 3 and S7) is summoned by another skill (<see cref="Summon"/>:
/// the Old Man's chat button) and fought on its own rules:
/// <list type="bullet">
/// <item>shoot the head (the kill is the head's; in Classic the hands add nothing to its defence), a hand only when the head
/// has no clear line;</item>
/// <item>move: while it hovers, stand under it (it hovers straight overhead, a short shot up); while it spins, run from it (1.5
/// px/tick against the player's 3), past it when an end of the arena leaves no room; the dodge planner sees the hands' lunges
/// coming from their wind-up (<see cref="SkeletronPattern.PredictHand"/>);</item>
/// <item>escape by a recall potion when dawn is near (<see cref="DawnEscapeTicks"/>: by day it does 1000 damage, NPC.cs:22206),
/// once it is enraged, or when a Dungeon Guardian shows: the fight fails "escaped", the head despawns once we are more than
/// 2000 px off (NPC.cs:22198-22205).</item>
/// </list>
/// </para>
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
	private WalkToSkill backToArena;
	/// <summary>After a walk back that found no way, this long (5 s) fighting from where the bot is before the next.</summary>
	public const int BackRetryTicks = 300;
	/// <summary>Columns past an arena end that still count as on it (see BackToArena).</summary>
	public const int EndSlack = 1;
	private long backFailedAt = long.MinValue / 2;
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
	/// <summary>
	/// A worm's fight lasts longer: 10,050 life in Classic, about 7 minutes with an iron bow and wooden arrows when every
	/// arrow lands and 10-14 at a realistic 50-70 % (M10_SPEC section 4); unholy arrows halve that.
	/// </summary>
	public const int WormMaxFightTicks = 60 * 60 * 15;
	/// <summary>
	/// Run from a worm's head this near (px, across): as far as one at top speed goes in the dodge planner's horizon,
	/// so a head that could reach us before the planner sees it coming is already being run from.
	/// </summary>
	public const float WormAwayPx = WormPattern.HeadTopSpeed * Dodge.Horizon;
	/// <summary>
	/// Of the segments in a clear line, nearest first, solve the shot (lead and arc) for at most this many a tick: the
	/// segments of one arc lie close together, so when none of the nearest few can be hit a farther one seldom can.
	/// </summary>
	public const int WormShotTries = 8;
	/// <summary>A line on the worm's state this often (10 s): its fight lasts minutes.</summary>
	public const int WormLogTicks = 600;
	/// <summary>Ticks with no boss around after it appeared before concluding it left (or died).</summary>
	public const int GoneTicks = 90;

	private ISkill summon;
	private UseItemSkill escape;
	private string escapeWhy;
	private bool escapeLogged;
	/// <summary>
	/// Skeletron: escape this many ticks before dawn (Main.time 30600 of the night's 32400, docs M11_SPEC.md section 3e): at dawn
	/// it turns to 1000 damage and 9999 defence for good (NPC.cs:22206-22210, 22462-22475). Half a minute: the recall potion's
	/// 30-tick use (Item.cs:27252) after a swing under way, with room to spare.
	/// </summary>
	public const int DawnEscapeTicks = 1800;
	/// <summary>
	/// Skeletron hovering: stand still while under the head, within its half width (80 px wide, NPC.cs:4272) of its centre;
	/// the head follows us across at up to 8 px/tick, so from farther off we walk under it.
	/// </summary>
	public const float UnderHeadPx = 40f;
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
	private readonly int[] segmentTypes;
	private System.Collections.Generic.List<EntityInfo> segments = new();
	/// <summary>Worm: running past the nearest head this way (an arena end left no room to run from it), else 0.</summary>
	private int pastHeadDir;
	private int maxSegmentsSeen;
	private long lastWormLog = long.MinValue / 2;
	private readonly System.Collections.Generic.Dictionary<int, (float X, float Y, long Tick)> lastPositions = new();
	/// <summary>Worm: the last node stood on where the game's flag said the zone held (<see cref="CombatInfo.InEvilZone"/>).</summary>
	private TilePos? lastInZone;
	/// <summary>Worm: the farthest columns west and east the kiting goes, from where the zone was lost in this fight.</summary>
	private int? zoneWest, zoneEast;
	/// <summary>Worm: the tick the zone was lost, while it is.</summary>
	private long? zoneLostAt;

	/// <summary>
	/// Columns kept inside where the zone was seen to end (the flag lost there, in this fight or where the bot stood before,
	/// <see cref="BotContext.Zone"/>): a hit knocks the bot a tile or two (as <see cref="ArenaHardMargin"/>), and braking
	/// before the bound is the kiting's own (<see cref="Move"/>).
	/// </summary>
	public const int ZoneSlack = 2;

	/// <summary>
	/// The arena's columns and the feet row of standing on its floor: kite inside it (turning points from its ends),
	/// brake before its ends, and walk back if knocked or slid off it.
	/// </summary>
	public (int MinX, int MaxX, int StandY)? Arena { get; init; }
	public int SummonItem { get; }
	public int BossType { get; }
	public int[] MinionTypes { get; }
	/// <summary>A worm boss (<see cref="WormPattern"/>): fought with the worm tactics, not the Eye's.</summary>
	public bool IsWorm { get; }
	/// <summary>Skeletron (<see cref="SkeletronPattern"/>): fought with its own rules (see the class).</summary>
	public bool IsSkeletron { get; }
	/// <summary>
	/// The summon, when it is not an item to use (<see cref="SummonItem"/> 0): a skill made when the summon starts, such as the
	/// Old Man's chat button for Skeletron (<see cref="ChatButtonSkill"/>). Null: use the summon item.
	/// </summary>
	public Func<ISkill> Summon { get; init; }
	/// <summary>Why the fight was escaped (a recall potion drunk), or null.</summary>
	public string Escaped { get; private set; }
	/// <summary>Worm: its segments in view this tick.</summary>
	public int SegmentsInView => segments.Count;
	/// <summary>The NPC (whoAmI) aimed at this tick, or null.</summary>
	public int? TargetId { get; private set; }
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
		IsWorm = WormPattern.IsWormBoss(bossType);
		IsSkeletron = SkeletronPattern.IsSkeletron(bossType);
		segmentTypes = WormPattern.SegmentTypes(bossType);
	}

	public string Describe() => $"Boss {BossType} [{State}]" + (IsWorm ? $" {segments.Count} segments in view"
		: bossLifeSeen < int.MaxValue ? $" boss life {bossLifeSeen}" : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"Boss fight start: boss {BossType}, summon item {SummonItem}, {ctx.Combat.RangedName} with {ctx.Combat.AmmoCount} arrows, life {ctx.Combat.Life}/{ctx.Combat.LifeMax}");

	public void Stop(BotContext ctx)
	{
		summon?.Stop(ctx);
		escape?.Stop(ctx);
		backToArena?.Stop(ctx);
		backToArena = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		SkillStatus s = TickFight(ctx);
		ctx.Output.BestAmmo = true; // the best arrows for the boss (AmmoOrder), whatever the tick did with the output
		return s;
	}

	private SkillStatus TickFight(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		TargetId = null;
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		if (IsWorm)
			segments = ctx.Hostiles.Where(h => segmentTypes.Contains(h.Type)).Select(h => Measured(ctx, h)).OrderBy(h => h.DistanceTo(cx, cy)).ToList();
		RememberPositions(ctx);
		// A worm is defeated with its last segment (the flag is set as it dies); one still in view is another worm.
		if (ctx.World.BossDefeated(BossType) && segments.Count == 0) {
			ctx.Log.Info($"Boss fight: boss {BossType} defeated after {fightTicks} ticks, {Dodges} dodges, life {ctx.Combat.Life}/{ctx.Combat.LifeMax}"
				+ (IsWorm ? $", up to {maxSegmentsSeen} segments seen at once" : ""));
			State = "won";
			return SkillStatus.Succeeded;
		}
		deathsAtStart ??= ctx.Deaths;
		if (ctx.BodyIssue == "dead" || ctx.Deaths > deathsAtStart)
			return Fail(ctx, "died");
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		// A worm's fight on the arena from where the Worm Food is used, the planner's spot where the zone holds
		// (ProgressionPlanner.EvilSummonSpot), not necessarily the arena's middle.
		centre ??= Arena is (int ax0, int ax1, int ay) && !(IsWorm && ctx.Node.Y == ay && ctx.Node.X >= ax0 && ctx.Node.X <= ax1)
			? new TilePos((ax0 + ax1) / 2, ay) : ctx.Node;

		// A worm: its nearest segment stands for it.
		EntityInfo? boss = IsWorm ? segments.Cast<EntityInfo?>().FirstOrDefault()
			: ctx.Hostiles.Where(h => h.Type == BossType).Cast<EntityInfo?>().FirstOrDefault();
		// A worm's bar shows the life of all its segments, wherever they are (IWorldInfo.BossBarLife): one burrowed out of view
		// is still up. A fight taken up again with the worm out of view (after a pause, or a new instance of this job) does not
		// summon a second time: the Worm Food is used up, and the game allows no second head while one lives (SummonItemCheck).
		int barLife = IsWorm ? ctx.World.BossBarLife(BossType) : 0;
		if (boss == null && !seenBoss && barLife > 0) {
			seenBoss = true;
			ctx.Log.Info($"Boss fight: the worm is up (its bar shows {barLife} life) but none of it is in view; waiting for it");
		}

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
			if (summon == null && Summon == null && ctx.Inventory.Count(SummonItem) <= 0)
				return Fail(ctx, "no summon item");
			// Worm Food does nothing out of the Corruption zone (Player.cs:43462), and the worm would not stay (NPC.cs:55436):
			// the planner finds where it holds (the bot's look at it here is kept, BotContext.Zone).
			if (summon == null && IsWorm && ctx.Combat.InEvilZone == false)
				return Fail(ctx, $"not in the Corruption zone at {ctx.Node}: the summon would do nothing here");
			summon ??= Start(ctx, Summon?.Invoke() ?? new UseItemSkill(SummonItem));
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
			if (IsWorm)
				LogWorm(ctx);
			else {
				if (b.Life <= lifeLogged - b.LifeMax / 10 || b.Life <= b.LifeMax / 2 && lifeLogged > b.LifeMax / 2) {
					lifeLogged = b.Life;
					ctx.Log.Info($"Boss fight: {b.Name} at {b.Life}/{b.LifeMax}, us {ctx.Combat.Life}/{ctx.Combat.LifeMax}, {ctx.Combat.AmmoCount} arrows, {Dodges} dodges");
				}
				bossLifeSeen = Math.Min(bossLifeSeen, b.Life);
			}
		}
		else if (++goneTicks == 1)
			ctx.Log.Info(IsWorm ? $"Boss fight: no segment of the worm in view, us at {ctx.Node}"
				: $"Boss fight: lost sight of the boss (last seen at {bossLifeSeen} life), us at {ctx.Node}");
		else if (goneTicks > (IsWorm ? WormPattern.GoneTicks : GoneTicks)) {
			if (ctx.World.BossDefeated(BossType))
				return SkillStatus.Running;
			// Out of view that long but still on its bar: burrowed or diving, not gone. Wait for it where the fight is (back on
			// the arena, or within the leash), which keeps us in the zone; the worm only dives off for good while no player is in
			// it (NPC.cs:55436-55481), and then its bar empties as it despawns. Seen in game (measured run M10j): "no segment of
			// the worm in view" at (1914,361), in a chasm's water, the worm at 8597 of 10050, and the fight was over.
			if (barLife <= 0)
				return Fail(ctx, IsWorm ? $"the worm left: no segment in view for {WormPattern.GoneTicks} ticks, not defeated, its bar empty" : "the boss left");
			if (ctx.Tick - lastWormLog >= WormLogTicks) {
				lastWormLog = ctx.Tick;
				ctx.Log.Info($"Boss fight: no segment of the worm in view for {goneTicks} ticks, but its bar shows {barLife} life: "
					+ $"waiting for it {(Arena != null ? "on the arena" : $"round {centre}")}, us at {ctx.Node}");
			}
		}
		int maxTicks = IsWorm ? WormMaxFightTicks : MaxFightTicks;
		if (++fightTicks > maxTicks)
			return Fail(ctx, $"still fighting after {maxTicks} ticks");
		if (!ctx.Combat.HasRanged)
			return Fail(ctx, "out of arrows");
		if (IsSkeletron && Escape(ctx, boss) is SkillStatus escaping)
			return escaping;

		if (IsWorm)
			TrackZone(ctx);
		bool walkingBack = BackToArena(ctx);
		if (!walkingBack)
			Move(ctx, boss);
		// Target: a servant about to bite, else the boss. For a worm every segment is the boss (WormShot takes the nearest it
		// can hit): the ones about to bite besides are the zone's own spawns, eaters of souls (40 life, 22 contact damage,
		// NPC.cs:3849; 2-3 arrows), which the boss fight shot none of. Seen in game (measured run M10k, world_eow from the
		// worm_food checkpoint): held in a pocket at x 1720-1722, hit by segments and eaters of souls in turn, dead in the fight.
		EntityInfo? servant = ctx.Hostiles.Where(h => (IsWorm ? !segmentTypes.Contains(h.Type) && h.Damage > 0 : MinionTypes.Contains(h.Type) && !IsSkeletron)
				&& h.DistanceTo(cx, cy) <= ServantDangerTiles * 16f)
			.OrderBy(h => h.DistanceTo(cx, cy)).Cast<EntityInfo?>().FirstOrDefault();
		// A worm: its nearest segment in reach (WormShot).
		EntityInfo? target = servant ?? (IsWorm ? null : boss);
		// Skeletron: the head, the hands only when the head has no clear line (in Classic they add nothing to its defence,
		// NPC.cs:22212-22265 is Expert only; unholy arrows pierce through a hand into the head anyway).
		if (IsSkeletron)
			target = SkeletronTarget(ctx, boss, cx, cy);
		// While walking back the path may need the pickaxe or blocks: no shooting then.
		bool canShoot = !walkingBack || ctx.Output.Tool == ToolRequest.None;
		AimSolution? aim = null;
		bool clear = false;
		if (canShoot && target is EntityInfo t && FightSkill.ClearShot(ctx, cx, cy, t.CenterX, t.CenterY)) {
			AimSolution s = Ballistics.Lead(cx, cy, t.CenterX, t.CenterY, t.VX, t.VY, ctx.Combat.Projectile);
			aim = s;
			clear = s.Hits && FightSkill.ArcClear(ctx, cx, cy, s, ctx.Combat.Projectile);
			TargetId = t.Id;
		}
		else if (canShoot && IsWorm) // no servant, or none in a clear line: the worm
			(aim, clear) = WormShot(ctx, cx, cy);
		if (aim is AimSolution a) {
			ctx.Output.Tool = ToolRequest.Ranged;
			ctx.Output.AimMouse(a.AimX, a.AimY);
			ctx.Output.UseItem = clear;
			State = !clear ? "no clear arc" : servant is EntityInfo sv && TargetId == sv.Id ? (IsWorm ? $"shooting the {sv.Name} by us" : "shooting a servant") : IsWorm ? "shooting a segment" : "shooting the boss";
		}
		else
			State = "kiting";
		if (walkingBack)
			State = $"walking back to {(Arena != null ? "the arena" : "where the fight began")}, " + State;
		if (zoneLostAt != null)
			State = "out of the zone, " + State;
		Heal(ctx);
		return SkillStatus.Running;
	}

	/// <summary>
	/// The nearest segment an arrow reaches: in a clear line, with a lead that hits and an arc clear of tiles. If none of
	/// the nearest <see cref="WormShotTries"/> in a clear line can be hit, aim at the nearest of them without shooting.
	/// </summary>
	private (AimSolution? Aim, bool Clear) WormShot(BotContext ctx, float cx, float cy)
	{
		AimSolution? first = null;
		int firstId = 0, tries = 0;
		foreach (EntityInfo seg in segments) {
			if (!FightSkill.ClearShot(ctx, cx, cy, seg.CenterX, seg.CenterY))
				continue;
			AimSolution s = Ballistics.Lead(cx, cy, seg.CenterX, seg.CenterY, seg.VX, seg.VY, ctx.Combat.Projectile);
			if (s.Hits && FightSkill.ArcClear(ctx, cx, cy, s, ctx.Combat.Projectile)) {
				TargetId = seg.Id;
				return (s, true);
			}
			if (first == null) {
				first = s;
				firstId = seg.Id;
			}
			if (++tries >= WormShotTries)
				break;
		}
		if (first != null)
			TargetId = firstId;
		return (first, false);
	}

	/// <summary>
	/// A worm's bodies and tail report no velocity: the AI moves each one by its position to keep its distance behind
	/// the segment ahead and zeroes its velocity (NPC.cs:55280-55327). Take their motion since the last tick instead, for
	/// the lead of a shot and the dodge planner. A jump faster than a head goes (twice its top speed) is a new NPC in
	/// the slot, not motion.
	/// </summary>
	private EntityInfo Measured(BotContext ctx, EntityInfo s)
	{
		if (s.VX != 0f || s.VY != 0f || !lastPositions.TryGetValue(s.Id, out (float X, float Y, long Tick) last) || last.Tick != ctx.Tick - 1)
			return s;
		float vx = s.X - last.X, vy = s.Y - last.Y, limit = 2f * WormPattern.TopSpeed(ctx.Expert);
		return vx * vx + vy * vy > limit * limit ? s : s with { VX = vx, VY = vy };
	}

	private void RememberPositions(BotContext ctx)
	{
		lastPositions.Clear();
		foreach (EntityInfo s in segments)
			lastPositions[s.Id] = (s.X, s.Y, ctx.Tick);
	}

	private void LogWorm(BotContext ctx)
	{
		maxSegmentsSeen = Math.Max(maxSegmentsSeen, segments.Count);
		if (ctx.Tick - lastWormLog < WormLogTicks)
			return;
		lastWormLog = ctx.Tick;
		int heads = segments.Count(s => s.Type == BossType), life = segments.Sum(s => s.Life);
		ctx.Log.Info($"Boss fight: {segments[0].Name}: {segments.Count} segments in view ({heads} heads, {life} life), "
			+ $"us {ctx.Combat.Life}/{ctx.Combat.LifeMax}, {ctx.Combat.AmmoCount} arrows, {Dodges} dodges");
	}

	/// <summary>
	/// Off the arena (slid past an end, knocked off): walk back to its middle. Seen in game: the bot slid a tile past
	/// the end of a floor built over open air and fell eight tiles, then ran into the wall down there for a minute.
	/// </summary>
	private bool BackToArena(BotContext ctx)
	{
		TilePos n = ctx.Node;
		bool off;
		Func<IGoal> goal;
		string place;
		if (Arena is (int x0, int x1, int standY)) {
			// A column of slack at the ends: braking slides the body a few pixels past an end, and the node (rounded to the
			// nearest column, Body.NodeOf) then reads one column out; off the floor's end the body falls and the row test
			// catches it. Seen in game (checkpoint run U16cp): "off the arena at (2030,266), walking back" four times, the
			// floor from 2031, and the walks back (no dodging) let the Eye of Cthulhu kill the bot at (2028,272).
			off = n.X < x0 - EndSlack || n.X > x1 + EndSlack || n.Y > standY + 1;
			// Onto the arena's floor anywhere, the same test as "off" above: a goal near its middle was "reached" from below
			// the floor, and every tick the next walk began. Seen in game (measured run U11cp): knocked down to (2084,272),
			// 5 rows under the floor and within 6 tiles of the middle, "off the arena, walking back" 6460 times while the Eye
			// of Cthulhu killed the bot.
			// A worm's: onto the part of it the zone holds over (ZoneBounds); there the kiting keeps within it.
			var (zw, ze) = IsWorm ? ZoneBounds(ctx) : (int.MinValue, int.MaxValue);
			int gx0 = Math.Max(x0, zw), gx1 = Math.Min(x1, ze);
			goal = () => gx0 <= gx1 ? new GoalStandOnRow(standY, gx0, gx1, ctx.Physics) : new GoalStandOnRow(standY, x0, x1, ctx.Physics);
			place = "the arena";
		}
		else if (IsWorm && zoneLostAt != null && lastInZone is TilePos held && n.ChebyshevTo(held) > ZoneSlack) {
			// No arena, out of the zone: back to where it last held, by a walk (the ground may not be flat).
			off = true;
			goal = () => new GoalNearTile(held, ZoneSlack, ctx.Physics);
			place = $"the zone (it last held at {held})";
		}
		else if (IsWorm) {
			// No arena: the worm's leash (MoveFreely), and no deeper than a safe fall under where the fight began: a walk never
			// drops further on its own (the paths' Drop moves), so lower down means knocked or slid down a chasm. Seen in game
			// (measured run M10j): from (1874,291), on the corruption's open ground, down a chasm to (1921,358), under water.
			TilePos c = centre.Value;
			int lowest = c.Y + ctx.Physics.SafeFallTiles;
			off = Math.Abs(n.X - c.X) > HardHalfWidth + EndSlack || n.Y > lowest;
			goal = () => new GoalRow(lowest, c.X, ctx.Physics) { Accept = t => Math.Abs(t.X - c.X) <= HardHalfWidth };
			place = $"the ground round {c}";
		}
		else
			return false;
		if (!off && ctx.Body.Grounded) {
			if (backToArena != null) {
				backToArena.Stop(ctx);
				backToArena = null;
				ctx.Log.Info(Arena != null ? $"Boss fight: back in the arena at {n}" : $"Boss fight: back on {place} at {n}");
			}
			return false;
		}
		if (!off)
			return backToArena != null && WalkBack(ctx); // in the air on the way back: keep going
		// No way back found lately: fight on from here for a while (the fight works on open ground) rather than plan again
		// every tick.
		if (backToArena == null && ctx.Tick - backFailedAt < BackRetryTicks)
			return false;
		if (backToArena == null) {
			ctx.Log.Info(Arena != null ? $"Boss fight: off the arena at {n}, walking back" : $"Boss fight: off {place} at {n}, walking back");
			backToArena = new WalkToSkill(goal()) { Quiet = true };
			backToArena.Start(ctx);
		}
		return WalkBack(ctx);
	}

	/// <summary>
	/// Worm: the evil biome's zone as the game shows it (<see cref="CombatInfo.InEvilZone"/>; the worm dives and despawns once
	/// no player is in it, NPC.cs:55436). In it, where the bot stands is where it last held; out of it, the side it was lost on
	/// gets a bound <see cref="ZoneSlack"/> columns short of here, never past where it last held, and the kiting heads back
	/// in (<see cref="MoveFreely"/>; with no arena, a walk back, <see cref="BackToArena"/>). Seen in game (measured runs M10m and
	/// M10n, world_eow): on the arena at (1872,239), kiting east to (1935,245) and from (1933,245), past x 1933 where the game's
	/// count fell under 300, and "the worm left: no segment in view for 320 ticks, not defeated, its bar empty", twice.
	/// </summary>
	private void TrackZone(BotContext ctx)
	{
		ctx.NoteZone();
		if (ctx.Combat.InEvilZone is not bool inZone)
			return;
		TilePos n = ctx.Node;
		if (inZone) {
			if (ctx.Body.Grounded)
				lastInZone = n;
			if (zoneLostAt is long lost) {
				ctx.Log.Info($"Boss fight: back in the Corruption zone at {n} after {ctx.Tick - lost} ticks");
				zoneLostAt = null;
			}
			return;
		}
		if (zoneLostAt != null)
			return;
		// The bound from where it was lost, not from each tick out of it: the flag lags the body (the game's scene scan), and
		// on the way back in it still reads "out" a little inside where it was lost.
		TilePos held = lastInZone ?? centre.Value;
		zoneLostAt = ctx.Tick;
		ctx.Log.Info($"Boss fight: out of the Corruption zone at {n} (it last held at {held}): back towards it");
		int side = Math.Sign(n.X - held.X);
		if (side == 0)
			side = Math.Sign(n.X - centre.Value.X);
		if (side > 0)
			zoneEast = Math.Max(held.X, Math.Min(zoneEast ?? int.MaxValue, n.X - 1 - ZoneSlack));
		else if (side < 0)
			zoneWest = Math.Min(held.X, Math.Max(zoneWest ?? int.MinValue, n.X + 1 + ZoneSlack));
	}

	/// <summary>
	/// Worm: the columns the kiting keeps to for the zone (int.MinValue / MaxValue where nothing bounds it): the bounds from
	/// where it was lost in this fight (<see cref="TrackZone"/>), and on the arena the nearest columns of its floor either side
	/// of where it last held that the bot saw it not hold on (<see cref="BotContext.Zone"/>: the build walked the whole floor),
	/// <see cref="ZoneSlack"/> short of them.
	/// </summary>
	private (int West, int East) ZoneBounds(BotContext ctx)
	{
		int west = zoneWest ?? int.MinValue, east = zoneEast ?? int.MaxValue;
		if (Arena is (int x0, int x1, int y)) {
			int from = (lastInZone ?? centre.Value).X;
			for (int x = from + 1; x <= x1; x++)
				if (ctx.Zone.At(new TilePos(x, y)) == false) {
					east = Math.Min(east, Math.Max(from, x - 1 - ZoneSlack));
					break;
				}
			for (int x = from - 1; x >= x0; x--)
				if (ctx.Zone.At(new TilePos(x, y)) == false) {
					west = Math.Max(west, Math.Min(from, x + 1 + ZoneSlack));
					break;
				}
		}
		return (west, east);
	}

	private bool WalkBack(BotContext ctx)
	{
		SkillStatus s = backToArena.Tick(ctx);
		State = $"walking back to {(Arena != null ? "the arena" : "where the fight began")}";
		if (s == SkillStatus.Failed)
			backFailedAt = ctx.Tick;
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
		if (Arena == null && !IsWorm) // a worm's fight keeps to its leash (MoveFreely) as to an arena
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
		(floorY, minX, maxX) = Surroundings(ctx, IsWorm);
		if (Arena is (int ax0, int ax1, _)) {
			// Past the arena's ends the ground is unknown: the dodge planner treats them as walls.
			minX = Math.Max(minX, ax0 * 16f);
			maxX = Math.Min(maxX, (ax1 + 1) * 16f - p.Width);
		}
		else if (IsWorm) {
			// No arena: a leash round where the fight started, its ends walls to the planner like an arena's. The worm
			// despawns once we leave the Corruption; ZoneCorrupt counts evil tiles over about 169 tiles across round the
			// player (Main.buffScanAreaWidth, Main.cs:882), so 40 tiles from a spot in it keeps most of that ground.
			minX = Math.Max(minX, (centre.Value.X - HardHalfWidth) * 16f);
			maxX = Math.Min(maxX, (centre.Value.X + HardHalfWidth + 1) * 16f - p.Width);
		}
		if (IsWorm) {
			// Within the zone (ZoneBounds), its ends walls to the planner as the arena's are. Out past one (the zone lost there):
			// a wall where the body is, so the only way on is back in (the planner would put a body past a wall back at it).
			var (zw, ze) = ZoneBounds(ctx);
			if (zw > int.MinValue)
				minX = Math.Max(minX, zw * 16f);
			if (ze < int.MaxValue)
				maxX = Math.Min(maxX, (ze + 1) * 16f - p.Width);
			minX = Math.Min(minX, ctx.Body.X);
			maxX = Math.Max(maxX, ctx.Body.X);
			KiteWorm(ctx, minX, maxX);
		}
		else if (IsSkeletron)
			KiteSkeletron(ctx, boss, minX, maxX);
		else {
			TrackDash(ctx, boss, cx, cy);
			Kite(ctx, boss, floorY, minX, maxX);
		}
		threats.Clear();
		// A worm's segments along the chain (WormPattern.PredictChain, from their measured motion), then anything else.
		if (IsWorm) {
			Threat[] chain = WormPattern.PredictChain(segments, ctx.Grid, ctx.Body.X, ctx.Body.Y, ctx.Expert);
			for (int i = 0; i < segments.Count; i++)
				if (segments[i].Damage > 0 && segments[i].DistanceTo(cx, cy) <= ThreatRadiusTiles * 16f)
					threats.Add(chain[i]);
		}
		foreach (EntityInfo h in IsWorm ? ctx.Hostiles.Where(o => !segmentTypes.Contains(o.Type)) : ctx.Hostiles) {
			if (h.Damage <= 0 || h.DistanceTo(cx, cy) > (h.AiStyle == SkeletronPattern.HandAiStyle ? SkeletronPattern.LungeReachPx : ThreatRadiusTiles * 16f))
				continue;
			threats.Add(h.AiStyle == EyePattern.EyeAiStyle ? EyePattern.Predict(h, ctx.Expert)
				: h.AiStyle is SkeletronPattern.HeadAiStyle or SkeletronPattern.HandAiStyle ? SkeletronPattern.Predict(h, ctx.Hostiles, ctx.Expert, cx, cy)
				: new Threat(h.X, h.Y, h.Width, h.Height, h.VX, h.VY));
		}
		if (threats.Count == 0)
			return;
		// A worm's fight: the planner knows the immunity left from the last hit (a touch then does not hurt, so getting out of
		// a body streaming through is what counts, not that every way out touches it now), moves the body through the real
		// tiles (a pit's sides end; a one-tile step is walked up), and keeps off where the fight would walk back from.
		int immune = IsWorm ? ImmuneTicksLeft(ctx) : 0;
		ITileGrid terrain = IsWorm ? ctx.Grid : null;
		float maxBottom = IsWorm ? LowestBottom(ctx) : float.PositiveInfinity;
		// Skeletron's kiting may stand (under the hovering head): the plan judged is the one the kiting chose.
		int kiteDir = IsSkeletron ? ctx.Output.Horizontal : runDir;
		DodgePlan kite = Dodge.Evaluate(ctx.Body, p, floorY, minX, maxX, threats, kiteDir, 0, Dodge.Horizon, immune, terrain);
		// Hysteresis: start dodging when the kite comes within DodgeStartGap, stop once it clears DodgeStopGap again.
		if (!kite.Hits && kite.Clearance >= (dodging ? DodgeStopGap : DodgeStartGap) && kite.EndBottom <= maxBottom) {
			dodging = false;
			return;
		}
		DodgePlan best = Dodge.Best(ctx.Body, p, floorY, minX, maxX, threats, kiteDir, Dodge.Horizon, immune, terrain, maxBottom);
		if (!dodging) {
			Dodges++;
			if (ctx.Tick - lastDodgeLog >= 30) {
				lastDodgeLog = ctx.Tick;
				ctx.Log.Info($"Boss fight: dodge {(best.Dir > 0 ? "right" : best.Dir < 0 ? "left" : "stand")}{(best.JumpHold > 0 ? $" + jump {best.JumpHold}" : "")} " +
				$"(kiting {(kiteDir > 0 ? "right" : kiteDir < 0 ? "left" : "standing")} {(kite.Hits ? $"is hit in {kite.HitTick} ticks" : $"passes {kite.Clearance:0} px from it")}; the dodge {(best.Hits ? $"is hit in {best.HitTick}" : $"clears by {best.Clearance:0} px")})");
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
	/// Ticks of immunity left from the last hit by an enemy (<see cref="BotContext.HurtImmunityTicks"/>), one taken off: the hit
	/// may be noted on either side of the player's update in its tick, so the last tick counted could already be the first one
	/// a touch hurts again.
	/// </summary>
	private static int ImmuneTicksLeft(BotContext ctx) =>
		(int)Math.Max(0L, BotContext.HurtImmunityTicks - (ctx.Tick - ctx.LastEnemyHitTick) - 1);

	/// <summary>
	/// Worm: the body's bottom (px) below which a dodge would leave where the fight is held, and BackToArena walk it back:
	/// under the arena's floor, or deeper than a safe fall under where the fight began.
	/// </summary>
	private float LowestBottom(BotContext ctx) => Arena is (_, _, int standY)
		? (standY + 2) * 16f
		: (centre.Value.Y + ctx.Physics.SafeFallTiles + 1) * 16f;

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

	/// <summary>
	/// Floor under the feet (px) and the walls left and right (bounds for the hitbox's left edge, px). With
	/// <paramref name="overJumps"/> (a worm's fight, whose planner moves the body through the tiles) a wall is only a column
	/// too high to jump (<see cref="Unjumpable"/>): on uneven ground every step and pit side was a wall, so the kiting found
	/// "no room to run from the worm's head" and ran past it into its body, again and again (measured runs M10o and M10p, at
	/// x 1713-1729 by the chasm at x 1702), and braked short of steps it could have walked up.
	/// </summary>
	private static (float FloorY, float MinX, float MaxX) Surroundings(BotContext ctx, bool overJumps = false)
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
		int jumpTiles = overJumps ? JumpTiles(p) : 0;
		bool Wall(int x) => overJumps ? Unjumpable(ctx.Grid, x, n.Y, jumpTiles) : Blocked(ctx.Grid, x, n.Y);
		for (int dx = 1; dx <= WallScanTiles; dx++)
			if (Wall(n.X - dx)) {
				minX = (n.X - dx + 1) * 16f;
				break;
			}
		for (int dx = 1; dx <= WallScanTiles; dx++)
			if (Wall(n.X + dx)) {
				maxX = (n.X + dx) * 16f - p.Width;
				break;
			}
		return (floorY, minX, maxX);
	}

	/// <summary>A wall at body height in this column (the two cells above the feet row's floor).</summary>
	private static bool Blocked(ITileGrid g, int x, int feetY) => g.Get(x, feetY).IsSolid() || g.Get(x, feetY - 1).IsSolid();

	/// <summary>Whole tiles a full jump lifts the feet (<see cref="PlayerPhysics.ApexRise"/>): steps up to this high are jumped onto.</summary>
	private static int JumpTiles(PhysicsParams p) => (int)(PlayerPhysics.ApexRise(p, p.JumpHeight) / 16f);

	/// <summary>A column solid from the feet row up past <paramref name="jumpTiles"/>: no jump gets over it.</summary>
	private static bool Unjumpable(ITileGrid g, int x, int feetY, int jumpTiles)
	{
		for (int j = 0; j <= jumpTiles; j++)
			if (!g.Get(x, feetY - j).IsSolid())
				return false;
		return true;
	}

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

	/// <summary>
	/// Worm kiting. The danger is contact as the worm arcs up through us and falls back; a head (the fastest part, the
	/// bodies follow it) is outrun only sideways. So: run away from the nearest head within <see cref="WormAwayPx"/>
	/// across, from where it will cross our height (<see cref="WormPattern.CrossingX"/>: a head in the air falls). When the
	/// way from it is out of room (past <see cref="HalfWidths"/>' hard end, or a tile short of a wall or the leash), run
	/// the other way, past it, until it is behind us; the dodge planner times that (a jump over, or a wait). With no head
	/// near, run to and fro between the soft ends as the Eye's kiting does.
	/// </summary>
	private void KiteWorm(BotContext ctx, float minX, float maxX)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		TilePos node = ctx.Node;
		int offset = node.X - centre.Value.X;
		var (soft, hard) = HalfWidths();
		bool Room(int dir) => dir > 0 ? offset < hard && maxX - ctx.Body.X >= 16f : offset > -hard && ctx.Body.X - minX >= 16f;
		float? headX = null;
		foreach (EntityInfo s in segments) {
			if (s.Type != BossType)
				continue;
			float x = WormPattern.CrossingX(s, WormPattern.Airborne(s, ctx.Grid, ctx.Body.X, ctx.Body.Y), cy, ctx.Expert);
			if (Math.Abs(x - cx) <= WormAwayPx && (headX == null || Math.Abs(x - cx) < Math.Abs(headX.Value - cx)))
				headX = x;
		}
		if (headX is float hx) {
			int ahead = Math.Sign(hx - cx); // where the head is, seen from us
			int away = ahead != 0 ? -ahead : runDir;
			if (pastHeadDir != 0 && ahead == pastHeadDir && Room(pastHeadDir))
				runDir = pastHeadDir;
			else if (Room(away)) {
				runDir = away;
				pastHeadDir = 0;
			}
			else {
				runDir = -away;
				pastHeadDir = runDir;
				if (ctx.Tick - lastForcedTurnLog > 60) {
					lastForcedTurnLog = ctx.Tick;
					ctx.Log.Info($"Boss fight: no room to run from the worm's head at {node}: running past it");
				}
			}
		}
		else {
			pastHeadDir = 0;
			bool outward = runDir > 0 ? offset >= soft : offset <= -soft;
			if (outward || !Room(runDir))
				runDir = -runDir;
		}
		ctx.Output.Horizontal = runDir;
		// Over what is in the way (walls are only what no jump clears, Surroundings): a step two or more tiles high in front of
		// the feet is jumped onto, the jump held to its top; one tile high the game walks up on its own (Collision.StepUp).
		bool grounded = ctx.Body.Grounded;
		int front = (int)MathF.Floor((runDir > 0 ? ctx.Body.X + p.Width + 1f : ctx.Body.X - 1f) / 16f);
		bool stepAhead = ctx.Grid.Get(front, node.Y - 1).IsSolid() && ctx.Grid.Get(front, node.Y).IsSolid();
		if (grounded && stepAhead && !hopping)
			hopping = ctx.Output.Jump = true;
		else if (hopping && !grounded)
			ctx.Output.Jump = true;
		else
			hopping = false;
	}

	/// <summary>
	/// Skeletron's kiting (<see cref="SkeletronPattern"/>). Hovering, it keeps its top 250 px over ours and follows us across: stand
	/// under it (within <see cref="UnderHeadPx"/> of its centre, else walk under it) and shoot straight up; the hands' lunges are
	/// the dodge planner's, aimed at where we were when they set off. Spinning (or enraged by day), it flies straight at us: run
	/// away from it, and past it when the way from it is out of room (as the worm's kiting, <see cref="KiteWorm"/>).
	/// </summary>
	private void KiteSkeletron(BotContext ctx, EntityInfo? head, float minX, float maxX)
	{
		ctx.Output.Horizontal = 0;
		if (head is not EntityInfo h)
			return;
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p);
		int offset = ctx.Node.X - centre.Value.X;
		var (_, hard) = HalfWidths();
		bool Room(int dir) => dir > 0 ? offset < hard && maxX - ctx.Body.X >= 16f : offset > -hard && ctx.Body.X - minX >= 16f;
		int ahead = Math.Sign(h.CenterX - cx);
		if (SkeletronPattern.HeadPhase(h) == SkeletronPattern.Phase.Hover) {
			if (Math.Abs(h.CenterX - cx) > UnderHeadPx && Room(ahead)) {
				runDir = ahead;
				ctx.Output.Horizontal = ahead;
			}
			return;
		}
		int away = ahead != 0 ? -ahead : runDir;
		if (Room(away))
			runDir = away;
		else {
			runDir = -away;
			if (ctx.Tick - lastForcedTurnLog > 60) {
				lastForcedTurnLog = ctx.Tick;
				ctx.Log.Info($"Boss fight: no room to run from Skeletron's head at {ctx.Node}: running past it");
			}
		}
		ctx.Output.Horizontal = runDir;
	}

	/// <summary>Skeletron: the head if an arrow reaches it, else the nearest hand one reaches, else the head (aimed at, not shot).</summary>
	private EntityInfo? SkeletronTarget(BotContext ctx, EntityInfo? head, float cx, float cy)
	{
		if (head is EntityInfo h && FightSkill.ClearShot(ctx, cx, cy, h.CenterX, h.CenterY))
			return h;
		foreach (EntityInfo e in ctx.Hostiles)
			if (MinionTypes.Contains(e.Type) && e.DistanceTo(cx, cy) <= ThreatRadiusTiles * 16f && FightSkill.ClearShot(ctx, cx, cy, e.CenterX, e.CenterY))
				return e;
		return head;
	}

	/// <summary>
	/// Skeletron: time to go (<see cref="DawnEscapeTicks"/> before dawn, day already, the head enraged, or a Dungeon Guardian in
	/// view)? Then a recall potion (the fight fails "escaped ..." once it is drunk); with none, the kiting runs from the head
	/// (the AI's 2000 px rule drops us only that far off, NPC.cs:22198-22205). Null while the fight goes on.
	/// </summary>
	private SkillStatus? Escape(BotContext ctx, EntityInfo? head)
	{
		if (escape == null) {
			string why = !ctx.IsNight ? "it is day: Skeletron is enraged (1000 damage)"
				: ctx.NightTicksLeft <= DawnEscapeTicks ? $"dawn in {ctx.NightTicksLeft / 60} s: Skeletron turns to 1000 damage at dawn"
				: head is EntityInfo h && SkeletronPattern.HeadPhase(h) == SkeletronPattern.Phase.Day ? "Skeletron is enraged"
				: ctx.Hostiles.Any(e => e.Type == SkeletronPattern.Guardian) ? "a Dungeon Guardian is here"
				: null;
			if (why == null)
				return null;
			if (ctx.Ids.RecallPotion <= 0 || ctx.Inventory.Count(ctx.Ids.RecallPotion) <= 0) {
				if (!escapeLogged) {
					escapeLogged = true;
					ctx.Log.Warn($"Boss fight: {why}, and no recall potion: running from it");
				}
				return null;
			}
			escapeWhy = why;
			ctx.Log.Warn($"Boss fight: {why}; drinking a recall potion (boss at {(head is EntityInfo b ? $"{b.Life}/{b.LifeMax}" : "?")}, us {ctx.Combat.Life}/{ctx.Combat.LifeMax})");
			escape = Start(ctx, new UseItemSkill(ctx.Ids.RecallPotion));
		}
		State = $"escaping: {escapeWhy}";
		SkillStatus s = escape.Tick(ctx);
		if (s == SkillStatus.Running)
			return SkillStatus.Running;
		Escaped = escapeWhy;
		return Fail(ctx, s == SkillStatus.Succeeded ? $"escaped: {escapeWhy}" : $"escape failed ({escape.FailureReason}): {escapeWhy}");
	}

	/// <summary>Worm kiting: jumping onto a step in front (held until the body is back on the ground).</summary>
	private bool hopping;

	private void Heal(BotContext ctx)
	{
		bool want = ctx.Combat.LifeFraction < System.Math.Max(HealBelow, FightSkill.HealAt(ctx)) && ctx.Combat.CanQuickHeal && !healPressedLastTick;
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
