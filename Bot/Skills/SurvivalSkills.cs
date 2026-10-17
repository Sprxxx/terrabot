using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Get <see cref="Amount"/> of an item that NPCs drop (gel from slimes): fight NPCs with the given AI style
/// when they are around and pick up the drops; otherwise wander along the surface and wait for spawns.
/// </summary>
public sealed class HuntSkill : ISkill
{
	public const int SearchTiles = 40;
	public const int WanderTiles = 30;
	public const int WaitTicks = 60 * 5;
	public const int MaxWanders = 12;
	/// <summary>Wander steps out of town before the wanders count (town range is 120 tiles: vanilla NPC.townRangeX, 1920 px).</summary>
	public const int MaxTownWalks = 10;
	private int townWalks;

	private ISkill sub;
	private int wanders;
	private readonly System.Collections.Generic.HashSet<int> ignoredDrops = new();
	private int wanderDir = 1;
	/// <summary>Where the wanders out of town start from (set once out of town): they sweep outward from here.</summary>
	private TilePos? wanderFrom;
	/// <summary>Wanders made so far to the west [0] and east [1] of <see cref="wanderFrom"/>: the next one goes one further.</summary>
	private readonly int[] wandersOut = new int[2];
	/// <summary>The side of <see cref="wanderFrom"/> the town is on (-1 west, 1 east, 0 none known): no wander goes there.</summary>
	private int townSide;
	private int waitTicks;
	private int lastHave;

	public int ItemType { get; }
	public int AiStyle { get; }
	public int Amount { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hunt";
	public string Key => $"Hunt {ItemType} {AiStyle} {Amount}";
	/// <summary>
	/// The prey spawns only at or above this row (null: anywhere): the hunt goes up there first and wanders there. Vanilla
	/// (NPC.SpawnNPC): demon eyes come only at night and only where the spawn tile is above the world's surface row
	/// (flag20 = num2 &lt;= Main.worldSurface). Seen in game (measured run U3): a lens hunt wandered round (1975,345), below
	/// it, for 13 heartbeats, then "no NPC with AI style 2 found after 12 wanders".
	/// </summary>
	public int? SpawnsAboveRow { get; init; }
	public const int MaxFailedWalksUp = 2;
	private int failedWalksUp;
	/// <summary>
	/// Where the prey lives (null: anywhere): the hunt goes there first, and its wanders sweep round it, not round wherever
	/// the bot stood. Eaters of souls spawn only while the player is in the Corruption zone (Player.ZoneCorrupt), so a hunt
	/// for rotten chunks from the house, 120-200 tiles from the corruption in the M10 world (docs M10_SPEC.md), finds none.
	/// </summary>
	public TilePos? Ground { get; init; }
	/// <summary>
	/// Wanders round <see cref="Ground"/> go out this many steps of <see cref="WanderTiles"/> to each side, then start over:
	/// the ground is a biome, and wandering on out of it leaves the spawns (the M10 corruption is about 80 tiles wide).
	/// </summary>
	public const int GroundWanderSteps = 2;
	/// <summary>Wanders out of town go this many steps of <see cref="WanderTiles"/> to each side, then start over (see Tick).</summary>
	public const int OpenWanderSteps = 2;
	/// <summary>
	/// Farther than this (tiles) from <see cref="Ground"/> with no prey in sight (a fight or a fall took the bot off), the hunt
	/// walks back to it: the wanders' own reach. At one wander's reach, every far wander was followed by a walk back first.
	/// </summary>
	public const int GroundTiles = WanderTiles * GroundWanderSteps + Tolerance;
	/// <summary>Walks to the ground (each after a fight or a failed walk) before the hunt gives up on getting there.</summary>
	public const int MaxGroundWalks = 12;
	private int groundWalks;
	/// <summary>How near (tiles) a walk of the hunt gets to its goal: the wanders' own 4.</summary>
	public const int Tolerance = 4;

	public HuntSkill(int itemType, int aiStyle, int amount)
	{
		ItemType = itemType;
		AiStyle = aiStyle;
		Amount = amount;
	}

	public string Describe() => $"Hunt (AI {AiStyle}) for item {ItemType} {lastHave}/{Amount} [{State}]" + (sub != null ? " > " + sub.Describe() : "");
	public void Start(BotContext ctx) => ctx.Log.Info($"Hunt start: item {ItemType} x{Amount} from NPCs with AI style {AiStyle}, have {ctx.Inventory.Count(ItemType)}");

	public void Stop(BotContext ctx)
	{
		sub?.Stop(ctx);
		sub = null;
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		sub?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		sub?.Resume(ctx);
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		int have = ctx.Inventory.Count(ItemType);
		lastHave = have;
		if (have >= Amount && sub is not CollectDropsSkill) {
			ctx.Log.Info($"Hunt done: item {ItemType} {have}/{Amount}");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (!ctx.Combat.HasWeapon)
			return Fail(ctx, "no melee weapon");
		if (sub != null) {
			SkillStatus s = sub.Tick(ctx);
			if (s == SkillStatus.Running && !(sub is WalkToSkill && State != ToOpenGround && PreyInSight(ctx)))
				return SkillStatus.Running;
			if (s == SkillStatus.Failed && sub is WalkToSkill { ToSurface: true } && ++failedWalksUp >= MaxFailedWalksUp) {
				sub.Stop(ctx);
				sub = null;
				return Fail(ctx, $"no way up to row {SpawnsAboveRow} after {failedWalksUp} walks");
			}
			bool fought = sub is FightSkill;
			sub.Stop(ctx);
			sub = null;
			// On its ground the prey was there: the wanders that found none start counting afresh. A long hunt (15 rotten
			// chunks at 1 in 3 is about 45 eaters of souls, spec G4) would otherwise run out of wanders between kills.
			if (fought && Ground != null)
				wanders = 0;
			if (fought) {
				Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, 20, ignoredDrops), "collecting");
				return SkillStatus.Running;
			}
		}
		if (CollectDropsSkill.AnyInRange(ctx, d => d.Type == ItemType && !ignoredDrops.Contains(d.Id), 20)) {
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, 20, ignoredDrops), "collecting");
			return SkillStatus.Running;
		}
		if (PreyInSight(ctx)) {
			// Fliers are fought from open ground: from under cover (a tunnel, a chasm's side) no shot at them is clear, and
			// they are waited for, not chased (FightSkill). Seen in game (measured run M10a): at (1868-1889,333-340), in
			// tunnels under the corruption's surface, 45 fights against eaters of souls ended with 0 kills.
			if (Ground is TilePos open && FightSkill.FlyingAiStyles.Contains(AiStyle) && !FightSkill.OpenSky(ctx, ctx.Node)
				&& open.ChebyshevTo(ctx.Node) > Tolerance && groundWalks < MaxGroundWalks) {
				groundWalks++;
				ctx.Log.Info($"Hunt: prey in sight from under cover at {ctx.Node}; out to open ground at {open} first");
				Begin(ctx, new WalkToSkill(new Core.Pathfinding.GoalNearTile(open, Tolerance, ctx.Physics)) { Quiet = true }, ToOpenGround);
				return SkillStatus.Running;
			}
			Begin(ctx, new FightSkill { Leash = FightSkill.LeashRadius, ChaseWithin = float.MaxValue }, "fighting"); // hunting: the prey is the point
			return SkillStatus.Running;
		}
		if (waitTicks-- > 0) {
			State = "waiting for spawns";
			return SkillStatus.Running;
		}
		TilePos node = ctx.Node;
		if (SpawnsAboveRow is int top && node.Y > top) {
			ctx.Log.Info($"Hunt: nothing spawns down here (row {node.Y}); up to row {top} first");
			Begin(ctx, new WalkToSkill(new Core.Pathfinding.GoalRow(top, node.X, ctx.Physics)) { Quiet = true, ToSurface = true }, "going up");
			return SkillStatus.Running;
		}
		if (Ground is TilePos ground) {
			// To the prey's ground first, then round it (no town walk: the ground is where it spawns).
			if (ground.ChebyshevTo(node) > GroundTiles) {
				if (++groundWalks > MaxGroundWalks)
					return Fail(ctx, $"no way to the hunting ground at {ground} after {MaxGroundWalks} walks");
				waitTicks = WaitTicks;
				ctx.Log.Info($"Hunt: to the hunting ground at {ground} ({ground.ChebyshevTo(node)} tiles away)");
				Begin(ctx, new WalkToSkill(new Core.Pathfinding.GoalNearTile(ground, Tolerance, ctx.Physics)) { Quiet = true }, "going to the hunting ground");
				return SkillStatus.Running;
			}
			if (++wanders > MaxWanders)
				return Fail(ctx, $"no NPC with AI style {AiStyle} found after {MaxWanders} wanders round {ground}");
			int side = wanders % 2 == 1 ? 1 : -1;
			int step = (wanders - 1) / 2 % GroundWanderSteps + 1;
			int wx = ground.X + side * WanderTiles * step;
			TilePos to = GroundNear(ctx, ground, wx, side) ?? new TilePos(wx, ground.Y - Tolerance);
			waitTicks = WaitTicks;
			ctx.Log.Info($"Hunt: nothing to hunt in sight, wandering round the hunting ground towards {to}");
			Begin(ctx, new WalkToSkill(to) { Quiet = true, Tolerance = Tolerance }, "wandering");
			return SkillStatus.Running;
		}
		// In town (a town NPC near: vanilla spawns no hostile there, spawnInfo.PlayerSafe): out of it first, one way, as a
		// player goes out of town to farm. Seen in game (measured runs U2, U3): night hunts by the house, the Guide's,
		// failed "no NPC with AI style 2 found after 12 wanders", wandering 30 tiles out and back; 70 heartbeats of hunting
		// and 34 waiting for the next night, and still 2 lenses short at day 10.
		// On the evil biome's ground, prey of the plain surface does not spawn either (vanilla's spawn pool there is the biome's
		// own: NPC.cs, the Corruption's eaters of souls in place of demon eyes): out of it, the same way. Seen in game
		// (measured run U23): the lens hunts of nights 6 and 7 wandered x 1720-1840 by the arena site in the corruption,
		// fought eaters of souls, and gave 1 and 2 lenses; 3 nights of hunting for 6, the summon at d8 +22h.
		bool evilHere = Ground == null && ctx.InEvilBiome(node);
		TilePos goal;
		if ((ctx.Combat.InTown || evilHere) && townWalks < MaxTownWalks && wanderFrom == null) {
			if (townWalks++ == 0) {
				wanderDir = evilHere ? AwayFromEvil(ctx, node) : ctx.SaferSide(node);
				ctx.Log.Info($"Hunt: {(evilHere ? "on the evil biome's ground (its own spawns, not this prey)" : "in town (nothing spawns here)")}; walking out of it {(wanderDir > 0 ? "east" : "west")}");
			}
			goal = new TilePos(node.X + wanderDir * WanderTiles, node.Y - 10);
		}
		else {
			if (++wanders > MaxWanders)
				return Fail(ctx, $"no NPC with AI style {AiStyle} found after {MaxWanders} wanders");
			// Outward from where the wanders began, the two sides in turn and further each time (+30, -30, +60, -60...), as
			// GatherWood explores. Seen in game (measured runs U2, U3): 30 tiles from where the bot stood, alternately, went to
			// and fro between the same two places, and no new ground (nor its spawns) was reached.
			if (wanderFrom is not TilePos from) {
				from = node;
				wanderFrom = from;
				// Walked out of town: the town is behind, and the sweep goes on away from it.
				if (townWalks > 0)
					townSide = -wanderDir;
			}
			else if ((ctx.Combat.InTown || evilHere) && townSide == 0 && node.X != from.X)
				townSide = System.Math.Sign(node.X - from.X); // a wander back into town (or the evil biome): nothing spawns that side
			int dir = townSide != 0 ? -townSide
				: wandersOut[Side(wanderDir)] <= wandersOut[Side(-wanderDir)] ? wanderDir : -wanderDir;
			// Out to OpenWanderSteps on each side, then round again: vanilla spawns 62-84 tiles from the player wherever it
			// stands (NPC.safeRangeX, spawnRangeX: 0.52 and 0.7 of a 1920-px screen), so walking farther out brings no more
			// prey. Seen in game (measured run U20): the lens hunt wandered out to x 2494-2704, 400-600 tiles from the house.
			if (wandersOut[Side(dir)] >= OpenWanderSteps)
				wandersOut[Side(dir)] = 0;
			goal = new TilePos(from.X + dir * WanderTiles * ++wandersOut[Side(dir)], System.Math.Min(from.Y, SpawnsAboveRow ?? int.MaxValue) - 10);
		}
		waitTicks = WaitTicks;
		ctx.Log.Info($"Hunt: nothing to hunt in sight, wandering towards {goal}");
		Begin(ctx, new WalkToSkill(goal) { Quiet = true, Tolerance = 4 }, "wandering");
		return SkillStatus.Running;
	}

	private static int Side(int dir) => dir > 0 ? 1 : 0;

	/// <summary>The side (+1 east, -1 west) with less of the evil biome a wander's length off.</summary>
	private static int AwayFromEvil(BotContext ctx, TilePos node) =>
		ctx.CorruptionAround(new TilePos(node.X + WanderTiles, node.Y)) <= ctx.CorruptionAround(new TilePos(node.X - WanderTiles, node.Y)) ? 1 : -1;

	/// <summary>The state of the walk out from under cover to the open ground (not broken off for prey in sight).</summary>
	public const string ToOpenGround = "to open ground";

	/// <summary>
	/// A wander's goal round <see cref="Ground"/>: the surface of column <paramref name="x"/> (the first spot to stand on from
	/// the sky), or of the nearest column back towards the ground whose surface is no deeper below the ground's row than a
	/// fall the body takes unhurt (<see cref="PhysicsParams.SafeFallTiles"/>): not the floor of a chasm. A goal in the air,
	/// a few rows over the ground's row, was walked to as the spot beneath it. Seen in game (measured run M10a): from the
	/// hunting ground (1915,310) the wanders went to rows under the corruption's surface, and its chasms' floors lie
	/// 26-110 rows down (the bot died three times at (1777,401), a chasm's floor).
	/// </summary>
	private static TilePos? GroundNear(BotContext ctx, TilePos ground, int x, int side)
	{
		for (int d = 0; d < WanderTiles; d++)
			if (Brain.ProgressionPlanner.SurfaceAt(ctx.Grid, x - side * d) is TilePos c && c.Y - ground.Y <= ctx.Physics.SafeFallTiles)
				return c;
		return null;
	}

	private bool PreyInSight(BotContext ctx)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		return ctx.Hostiles.Any(h => h.AiStyle == AiStyle && h.DistanceTo(cx, cy) <= SearchTiles * 16f);
	}

	private void Begin(BotContext ctx, ISkill skill, string state)
	{
		sub = skill;
		State = state;
		skill.Start(ctx);
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Hunt failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>
/// Sit out the night in a sealed hole: dig a shaft <see cref="Depth"/> rows straight down, place blocks in the
/// two cells above the head (the old floor row, where the shaft walls give the placement a neighbour), wait
/// for morning, then mine the seal open again. Zombies and demon eyes cannot get through blocks. Without blocks
/// the bot still waits at the bottom of the shaft.
/// </summary>
public sealed class ShelterSkill : ISkill
{
	public const int Depth = 5;
	public const int MaxActionTicks = 60 * 6;

	private enum Phase { Fleeing, Digging, Sealing, Waiting, Unsealing }

	private Phase phase;
	private WalkToSkill dig;
	private TilePos[] seal;
	private int actionTicks;

	/// <summary>Sealed in and waiting: the brain does not interrupt this for hostiles it cannot reach anyway.</summary>
	public bool Sealed => phase == Phase.Waiting && seal != null && sealedOk;
	private bool sealedOk;

	/// <summary>A retreat to recover life (<see cref="Recover"/>) rather than sitting out the night: interrupted to fight only when caught (<see cref="FightBackReason"/>).</summary>
	public bool Retreat => until != null;
	private readonly System.Func<BotContext, bool> until;
	private readonly string why = "for the night";
	private int waitTicks;
	/// <summary>Longest a retreat waits (it may not have sealed, or regeneration may be stopped by a debuff).</summary>
	public const int MaxRetreatTicks = 60 * 120;

	public ShelterSkill() { }

	/// <summary>Dig in and seal, and open up again once <paramref name="done"/> holds (instead of at morning).</summary>
	public ShelterSkill(System.Func<BotContext, bool> done, string reason)
	{
		until = done;
		why = reason;
	}

	/// <summary>Hide until life is back to <paramref name="fraction"/> of max: the retreat when hurt without potions.</summary>
	public static ShelterSkill Recover(float fraction) =>
		new(c => c.Combat.LifeFraction >= fraction, $"to recover to {fraction:P0} life");

	public string State => phase.ToString();
	public string FailureReason { get; private set; } = "";
	public string Name => "Shelter";
	public string Key => $"Shelter {why}";

	public string Describe() => $"Shelter [{phase}]" + (dig != null ? " > " + dig.Describe() : "");

	private long startTick;

	/// <summary>
	/// A hit this recent (ticks) means the shelter is not keeping enemies off: two of the immunity windows a hit gives, so an
	/// enemy still on the bot has had the chance to bite again (<see cref="BotContext.HurtImmunityTicks"/>).
	/// </summary>
	public const int HitRecentTicks = 2 * BotContext.HurtImmunityTicks;
	/// <summary>Hitboxes this close (px) touch: half a tile, closed by a walker in a few ticks.</summary>
	public const float ContactPx = 8f;
	/// <summary>
	/// A flight starts with the enemy beside the bot (that is why it runs): its contact counts only after the immunity the
	/// hit that set off the retreat gave (<see cref="BotContext.HurtImmunityTicks"/>), when it would bite again.
	/// </summary>
	public const int FleeGraceTicks = BotContext.HurtImmunityTicks;

	/// <summary>
	/// Why the shelter should turn and fight now, or null: an enemy hit the bot since it began (the flight is not getting
	/// away, the hole is not holding), or a hostile is within a swing (or touching, without a sword) with a clear line to it.
	/// The brain lets a retreat be interrupted only for this. Seen in game (13 deaths in Shelter in measured runs U13, U14,
	/// U17, U19, U20 and M10a): not one swing once a retreat began; walkers caught up while it dug, a slime sat in the hole,
	/// a crawdad bit through the seal, a giant worm 12 times while it waited.
	/// </summary>
	public string FightBackReason(BotContext ctx)
	{
		if (phase == Phase.Unsealing)
			return null;
		if (ctx.LastEnemyHitTick >= startTick && ctx.Tick - ctx.LastEnemyHitTick < HitRecentTicks)
			return $"hit while sheltering ({phase})";
		if (cornered != null && NearestHostile(ctx, out _) <= FleeClearPx)
			return cornered;
		if (phase == Phase.Fleeing && ctx.Tick - startTick < FleeGraceTicks)
			return null;
		PhysicsParams p = ctx.Physics;
		float reach = System.Math.Max(ctx.Combat.WeaponReach, ContactPx);
		float left = ctx.Body.X, top = ctx.Body.Y, cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		foreach (EntityInfo e in ctx.Hostiles) {
			float gapX = System.Math.Max(0f, System.Math.Max(e.X - (left + p.Width), left - (e.X + e.Width)));
			float gapY = System.Math.Max(0f, System.Math.Max(e.Y - (top + p.Height), top - (e.Y + e.Height)));
			if (gapX <= reach && gapY <= reach && FightSkill.ClearShot(ctx, cx, cy, e.CenterX, e.CenterY))
				return $"a {e.Name} on me while sheltering ({phase})";
		}
		return null;
	}

	public void Start(BotContext ctx)
	{
		startTick = ctx.Tick;
		// Hurt with something on it: run off first (the bot outruns slimes and zombies), then dig. Seen in game: most
		// deaths of the night runs were while digging in with the attacker beside the hole, or in it.
		if (Retreat && NearestHostile(ctx, out _) <= FleeWithinPx) {
			StartFlee(ctx, $"Shelter start at {ctx.Node}: something on me", $"first, then digging in {why}");
			return;
		}
		StartDigging(ctx);
	}

	/// <summary>
	/// A leg of the flight: <see cref="FleeTiles"/> away from the nearest hostile. <paramref name="moveOn"/>: to dig in somewhere
	/// else, so the leg is walked to its end even with nothing near (a flight stops once clear, <see cref="FleeClearPx"/>).
	/// </summary>
	private void StartFlee(BotContext ctx, string what, string then, bool moveOn = false)
	{
		movingOn = moveOn;
		TilePos n0 = ctx.Node;
		NearestHostile(ctx, out float hx);
		int dir = hx > ctx.Body.CenterX(ctx.Physics) ? -1 : hx < ctx.Body.CenterX(ctx.Physics) ? 1 : fleeDir;
		var away = new TilePos(n0.X + dir * FleeTiles, n0.Y);
		// Not into somewhere off limits (seen in game, run 54: away from one antlion, straight into the desert it came from).
		var other = new TilePos(n0.X - dir * FleeTiles, n0.Y);
		if (ctx.OffLimits(away) && !ctx.OffLimits(other)) {
			dir = -dir;
			away = other;
		}
		fleeDir = dir;
		ctx.Log.Info($"{what}; running {(dir > 0 ? "right" : "left")} {then}");
		dig = new WalkToSkill(new Core.Pathfinding.GoalNearTile(away, FleeTolerance, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
		dig.Start(ctx);
		phase = Phase.Fleeing;
	}

	/// <summary>Running off before digging in: an enemy this close, this far, at most this long.</summary>
	public const float FleeWithinPx = 6 * 16f, FleeClearPx = 14 * 16f;
	public const int FleeTiles = 25, FleeTolerance = 8, MaxFleeTicks = 60 * 6;
	private int fleeTicks;
	private int fleeDir = 1;
	private bool movingOn;
	/// <summary>
	/// Set when the flight could not outrun what chased it, or no hole could be made with it near: the brain fights it
	/// (<see cref="FightBackReason"/>, while it is within <see cref="FleeClearPx"/>).
	/// </summary>
	private string cornered;

	/// <summary>
	/// Places to dig in at before the shelter fails (the brain decides again): as the brain's three failures in a row,
	/// a pattern rather than bad luck. Each is a flight of <see cref="FleeTiles"/> on from the last.
	/// </summary>
	public const int MaxSpots = 3;
	private int spotsTried;

	/// <summary>
	/// No hole here (the dig failed, the seal timed out): never wait in the open. Seen in game (measured runs U17, U19 and
	/// U20: 4 deaths): after a failed dig the seal went to the two cells over the head out in the open, and after a seal
	/// timed out it "waited unsealed", both among the enemies. With one near, fight it; and run on to try elsewhere.
	/// </summary>
	private SkillStatus GiveUpSpot(BotContext ctx, string what)
	{
		sealedOk = false;
		if (++spotsTried >= MaxSpots) {
			FailureReason = $"{what}; no hole after {MaxSpots} places";
			ctx.Log.Info($"Shelter failed: {FailureReason}");
			return SkillStatus.Failed;
		}
		float near = NearestHostile(ctx, out _);
		if (near <= FleeClearPx)
			cornered = $"{what} with something {near / 16f:0} tiles off";
		fleeTicks = 0;
		StartFlee(ctx, $"Shelter: {what} at {ctx.Node}", $"on to dig in further away ({spotsTried + 1}/{MaxSpots})", moveOn: true);
		return SkillStatus.Running;
	}

	/// <summary>
	/// Ticks in the air after the flight's walk before what holds the body is cut (or fought from): fewer than the
	/// <see cref="BotContext.HurtImmunityTicks"/> (40) a hit gives, so it is done before the next bite can land, and about
	/// what a jump cut short still takes to come down (a full jump falls from its apex, 106 px at gravity 0.4, in 23).
	/// Seen in game (measured runs U17 and U19, wall creepers): held in a cobweb after the walk gave up ("never landed in
	/// 181 ticks"), the flight waited for the ground with no limit while it was bitten to death.
	/// </summary>
	public const int AirborneWaitTicks = 30;
	private int airborneTicks;
	private TilePos? cutting;

	/// <summary>The flight's walk is over but the body is off the ground: wait to land, then cut free (a web) or fight.</summary>
	private SkillStatus HeldInTheAir(BotContext ctx)
	{
		bool hit = ctx.LastEnemyHitTick >= startTick && ctx.Tick - ctx.LastEnemyHitTick < HitRecentTicks;
		if (++airborneTicks <= AirborneWaitTicks && !hit)
			return SkillStatus.Running;
		if (ctx.World != null && WalkToSkill.TileInOrOverBody(ctx) is TilePos web) {
			if (web != cutting)
				ctx.Log.Info($"Shelter: held in the air at {ctx.Node} after the flight; cutting {web} (tile {ctx.World.TileTypeAt(web.X, web.Y)})");
			cutting = web;
			ctx.Output.Tool = ToolRequest.Pickaxe;
			ctx.Output.UseItem = true;
			ctx.Output.Aim(web.X, web.Y);
		}
		if (cornered == null && (hit || NearestHostile(ctx, out _) <= FleeClearPx)) {
			cornered = $"held in the air at {ctx.Node} for {airborneTicks} ticks";
			ctx.Log.Info($"Shelter: {cornered}");
		}
		return SkillStatus.Running;
	}

	private long waitSince;

	private void BeginWaiting(BotContext ctx)
	{
		phase = Phase.Waiting;
		waitSince = ctx.Tick;
	}

	/// <summary>How near (px) a hostile that comes through blocks makes a hole no hiding place.</summary>
	public const float BurrowerNearPx = 30 * 16f;

	/// <summary>
	/// The nearest worm within <see cref="BurrowerNearPx"/> that really goes through blocks (vanilla NPC.noTileCollide: giant
	/// worms, devourers, the Eater of Worlds), so a seal does not keep it out.
	/// </summary>
	private static EntityInfo? Burrower(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		EntityInfo? best = null;
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.AiStyle == WormPattern.WormAiStyle && e.NoTileCollide && e.DistanceTo(cx, cy) <= BurrowerNearPx
				&& (best == null || e.DistanceTo(cx, cy) < best.Value.DistanceTo(cx, cy)))
				best = e;
		return best;
	}

	/// <summary>The cells walling the body in a hole: both sides, from the feet to the roof's row (its corners give the roof a neighbour to place against).</summary>
	private static System.Collections.Generic.IEnumerable<TilePos> SideCells(TilePos n)
	{
		for (int y = n.Y; y >= n.Y - Core.Pathfinding.Body.HeightTiles; y--) {
			yield return new TilePos(n.X - 1, y);
			yield return new TilePos(n.X + 2, y);
		}
	}

	/// <summary>
	/// A hostile in the hole with the bot (its hitbox in the body's cells or the roof's): sealing it in is no shelter. Seen in
	/// game (measured run U20): "sealed in" with a red slime in the hole, which killed the bot there.
	/// </summary>
	private static EntityInfo? InTheHole(BotContext ctx, TilePos n)
	{
		const float T = Core.Pathfinding.Body.TileSize;
		float left = n.X * T, right = (n.X + 2) * T, top = (n.Y - Core.Pathfinding.Body.HeightTiles) * T, bottom = (n.Y + 1) * T;
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.X < right && e.X + e.Width > left && e.Y < bottom && e.Y + e.Height > top)
				return e;
		return null;
	}

	private static float NearestHostile(BotContext ctx, out float x)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics), best = float.MaxValue;
		x = cx;
		foreach (EntityInfo e in ctx.Hostiles) {
			float d = e.DistanceTo(cx, cy);
			if (d < best) {
				best = d;
				x = e.CenterX;
			}
		}
		return best;
	}

	private void StartDigging(BotContext ctx)
	{
		phase = Phase.Digging;
		TilePos n = ctx.Node;
		TilePos target = DigTarget(ctx, n);
		ctx.Log.Info($"Shelter start at {n}: digging in {why} at column {target.X}");
		// A small search and one attempt: a retreat that plans for seconds while being bitten is no retreat.
		dig = new WalkToSkill(target) { Quiet = true, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
		dig.Start(ctx);
	}

	/// <summary>
	/// Where to dig down to: the nearest column pair whose cells from the ground down to <see cref="Depth"/> can all
	/// be mined (or are open). Seen in game: straight down was under the work bench, which cannot be mined from
	/// under it, and the plan ran out of nodes after 2.7 s while demon eyes finished the bot off.
	/// </summary>
	private static TilePos DigTarget(BotContext ctx, TilePos n)
	{
		int ground = n.Y + 1;
		while (ground < n.Y + 12 && !ctx.Grid.Get(n.X, ground).IsSolid())
			ground++; // in the air (knocked up, mid-jump): count from the ground below
		int stand = ground - 1;
		for (int d = 0; d <= DigSearchColumns; d++)
			foreach (int x in d == 0 ? new[] { n.X } : new[] { n.X - d, n.X + d })
				if (Diggable(ctx, x, ground) && Diggable(ctx, x + 1, ground))
					return new TilePos(x, stand + Depth);
		return new TilePos(n.X, stand + Depth);
	}

	private static bool Diggable(BotContext ctx, int x, int ground)
	{
		for (int y = ground; y < ground + Depth; y++) {
			CellKind k = ctx.Grid.Get(x, y);
			if (k.IsLiquid())
				return false;
			// Path blocks: the house and arena floor count as unbreakable (BotContext.Protected).
			if (k.IsSolid() && float.IsInfinity(ctx.PathBlocks?.BreakTicks(x, y) ?? float.PositiveInfinity))
				return false;
			if (ctx.Protected != null && ctx.Protected.Contains(new TilePos(x, y)))
				return false; // open house cells too (the doorway): a hole there would wreck the house
		}
		return true;
	}

	/// <summary>Columns either side searched for a place to dig in.</summary>
	public const int DigSearchColumns = 8;

	public void Stop(BotContext ctx)
	{
		dig?.Stop(ctx);
		dig = null;
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		dig?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		dig?.Resume(ctx);
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		switch (phase) {
			case Phase.Fleeing: {
				// The flight over but still in the air: the walk is gone, wait for the ground (world_eye run 71: the next tick
				// ticked the dropped walk, and the exception switched the bot off among zombies).
				if (dig != null) {
					SkillStatus fs = dig.Tick(ctx);
					float near = NearestHostile(ctx, out _);
					if (fs == SkillStatus.Running && ++fleeTicks <= MaxFleeTicks && (near <= FleeClearPx || movingOn))
						return SkillStatus.Running;
					dig.Stop(ctx);
					dig = null;
					// There, but the chaser is still close behind: not digging in under its nose, on with another leg. Seen in game
					// (measured runs U20, M10a and U19: 6 deaths): the walk "arrived" 17 tiles on (25 tiles, tolerance 8) with the
					// enemy beside it, digging 5 rows took 170-430 ticks, and zombies and a crawdad caught up. Out of time or of
					// way on with it still close: it cannot be outrun, so the brain fights it (FightBackReason).
					if (near <= FleeClearPx) {
						if (fs == SkillStatus.Succeeded && fleeTicks <= MaxFleeTicks) {
							StartFlee(ctx, $"Shelter: something still {near / 16f:0} tiles behind at {ctx.Node}", "on");
							return SkillStatus.Running;
						}
						cornered = $"cannot outrun it ({near / 16f:0} tiles behind after {fleeTicks} ticks{(fs == SkillStatus.Failed ? ", no way on" : "")})";
						ctx.Log.Info($"Shelter: {cornered}");
					}
				}
				if (!ctx.Body.Grounded)
					return HeldInTheAir(ctx); // dig in from the ground
				airborneTicks = 0;
				StartDigging(ctx);
				return SkillStatus.Running;
			}
			case Phase.Digging: {
				SkillStatus s = dig.Tick(ctx);
				if (s == SkillStatus.Running)
					return SkillStatus.Running;
				string why = dig.FailureReason;
				dig.Stop(ctx);
				dig = null;
				if (s == SkillStatus.Failed)
					return GiveUpSpot(ctx, $"could not dig down ({why})");
				phase = Phase.Sealing;
				actionTicks = 0;
				return SkillStatus.Running;
			}
			case Phase.Sealing: {
				// From where the body is now, not where the dig ended: seen in game (measured run U19), knockback moved the bot
				// 6 tiles, the seal cells stayed at the shaft, and placing ran out of time with 1854 blocks in hand.
				if (!ctx.Body.Grounded)
					return ++actionTicks > MaxActionTicks ? GiveUpSpot(ctx, $"knocked about for {MaxActionTicks} ticks while sealing") : SkillStatus.Running;
				TilePos n = ctx.Node;
				seal = new[] { new TilePos(n.X, n.Y - Core.Pathfinding.Body.HeightTiles), new TilePos(n.X + 1, n.Y - Core.Pathfinding.Body.HeightTiles) };
				TilePos? open = SideCells(n).Concat(seal).Select(c => (TilePos?)c).FirstOrDefault(c => !ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid());
				EntityInfo? inside = InTheHole(ctx, n);
				if (open == null && inside == null) {
					sealedOk = true;
					ctx.Log.Info($"Shelter: sealed in at {n}, waiting {(until != null ? why : "for morning")}");
					BeginWaiting(ctx);
					return SkillStatus.Running;
				}
				// No blocks at all: the shaft's walls still keep walkers off until they drop in (and one that does is fought,
				// FightBackReason); out of the shaft there is nothing to wait in.
				if (open != null && (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0)) {
					if (!SideCells(n).All(c => ctx.Grid.Get(c.X, c.Y).IsSolid()))
						return GiveUpSpot(ctx, $"no blocks, and {open.Value} beside the body is open");
					ctx.Log.Info($"Shelter: no blocks to seal {open.Value}; waiting unsealed at the bottom of the shaft at {n}");
					BeginWaiting(ctx);
					return SkillStatus.Running;
				}
				if (++actionTicks > MaxActionTicks)
					return GiveUpSpot(ctx, inside is EntityInfo e ? $"a {e.Name} in the hole" : $"could not seal {open.Value} in {MaxActionTicks} ticks");
				if (inside != null)
					return SkillStatus.Running; // not sealing it in with the bot: the brain fights it (FightBackReason, within a swing)
				ctx.Output.Tool = ToolRequest.Block;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(open.Value.X, open.Value.Y);
				return SkillStatus.Running;
			}
			case Phase.Waiting: {
				// The hole has failed: hit in it, or something that comes through blocks is near. Out (the brain fights, or a
				// retreat goes home from any distance with a worm about), not on waiting to be bitten. Seen in game (measured run
				// U17): a giant worm bit the bot 12 times in its hole over about 7100 ticks, until it died there; the worm check
				// was made only when the retreat began.
				string failed = ctx.LastEnemyHitTick >= waitSince ? "hit in the hole"
					: Burrower(ctx) is EntityInfo w ? $"a {w.Name} that burrows through blocks is {w.DistanceTo(ctx.Body.CenterX(ctx.Physics), ctx.Body.CenterY(ctx.Physics)) / 16f:0} tiles off"
					: null;
				if (failed != null) {
					FailureReason = $"the hole failed: {failed}";
					ctx.Log.Info($"Shelter: {FailureReason}; leaving it at {ctx.Node}");
					return SkillStatus.Failed;
				}
				if (until != null ? !until(ctx) && ++waitTicks <= MaxRetreatTicks : ctx.IsNight)
					return SkillStatus.Running;
				ctx.Log.Info(until != null ? $"Shelter: done waiting ({why}), opening up" : "Shelter: morning, opening up");
				phase = Phase.Unsealing;
				actionTicks = 0;
				return SkillStatus.Running;
			}
			default: {
				TilePos? closed = sealedOk ? seal.Select(c => (TilePos?)c).FirstOrDefault(c => ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid()) : null;
				if (closed == null || ctx.PickPower <= 0 || ++actionTicks > MaxActionTicks) {
					ctx.Log.Info("Shelter done");
					return SkillStatus.Succeeded;
				}
				ctx.Output.Tool = ToolRequest.Pickaxe;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(closed.Value.X, closed.Value.Y);
				return SkillStatus.Running;
			}
		}
	}
}
