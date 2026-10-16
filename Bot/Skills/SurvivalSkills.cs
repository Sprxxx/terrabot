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
			if (s == SkillStatus.Running && !(sub is WalkToSkill && PreyInSight(ctx)))
				return SkillStatus.Running;
			if (s == SkillStatus.Failed && sub is WalkToSkill { ToSurface: true } && ++failedWalksUp >= MaxFailedWalksUp) {
				sub.Stop(ctx);
				sub = null;
				return Fail(ctx, $"no way up to row {SpawnsAboveRow} after {failedWalksUp} walks");
			}
			bool fought = sub is FightSkill;
			sub.Stop(ctx);
			sub = null;
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
			Begin(ctx, new FightSkill { Leash = FightSkill.LeashRadius }, "fighting"); // hunting: the prey is the point
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
		// In town (a town NPC near: vanilla spawns no hostile there, spawnInfo.PlayerSafe): out of it first, one way, as a
		// player goes out of town to farm. Seen in game (measured runs U2, U3): night hunts by the house, the Guide's,
		// failed "no NPC with AI style 2 found after 12 wanders", wandering 30 tiles out and back; 70 heartbeats of hunting
		// and 34 waiting for the next night, and still 2 lenses short at day 10.
		TilePos goal;
		if (ctx.Combat.InTown && townWalks < MaxTownWalks && wanderFrom == null) {
			if (townWalks++ == 0) {
				wanderDir = ctx.SaferSide(node);
				ctx.Log.Info($"Hunt: in town (nothing spawns here); walking out of it {(wanderDir > 0 ? "east" : "west")}");
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
			else if (ctx.Combat.InTown && townSide == 0 && node.X != from.X)
				townSide = System.Math.Sign(node.X - from.X); // a wander back into town: nothing spawns that side
			int dir = townSide != 0 ? -townSide
				: wandersOut[Side(wanderDir)] <= wandersOut[Side(-wanderDir)] ? wanderDir : -wanderDir;
			goal = new TilePos(from.X + dir * WanderTiles * ++wandersOut[Side(dir)], System.Math.Min(from.Y, SpawnsAboveRow ?? int.MaxValue) - 10);
		}
		waitTicks = WaitTicks;
		ctx.Log.Info($"Hunt: nothing to hunt in sight, wandering towards {goal}");
		Begin(ctx, new WalkToSkill(goal) { Quiet = true, Tolerance = 4 }, "wandering");
		return SkillStatus.Running;
	}

	private static int Side(int dir) => dir > 0 ? 1 : 0;

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

	/// <summary>A retreat to recover life (<see cref="Recover"/>) rather than sitting out the night: never interrupted to fight.</summary>
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

	public void Start(BotContext ctx)
	{
		// Hurt with something on it: run off first (the bot outruns slimes and zombies), then dig. Seen in game: most
		// deaths of the night runs were while digging in with the attacker beside the hole, or in it.
		if (Retreat && NearestHostile(ctx, out float hx) <= FleeWithinPx) {
			TilePos n0 = ctx.Node;
			int dir = hx > ctx.Body.CenterX(ctx.Physics) ? -1 : 1;
			var away = new TilePos(n0.X + dir * FleeTiles, n0.Y);
			// Not into somewhere off limits (seen in game, run 54: away from one antlion, straight into the desert it came from).
			var other = new TilePos(n0.X - dir * FleeTiles, n0.Y);
			if (ctx.OffLimits(away) && !ctx.OffLimits(other)) {
				dir = -dir;
				away = other;
			}
			ctx.Log.Info($"Shelter start at {n0}: something on me; running {(dir > 0 ? "right" : "left")} first, then digging in {why}");
			dig = new WalkToSkill(new Core.Pathfinding.GoalNearTile(away, FleeTolerance, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
			dig.Start(ctx);
			phase = Phase.Fleeing;
			return;
		}
		StartDigging(ctx);
	}

	/// <summary>Running off before digging in: an enemy this close, this far, at most this long.</summary>
	public const float FleeWithinPx = 6 * 16f, FleeClearPx = 14 * 16f;
	public const int FleeTiles = 25, FleeTolerance = 8, MaxFleeTicks = 60 * 6;
	private int fleeTicks;

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
					if (fs == SkillStatus.Running && ++fleeTicks <= MaxFleeTicks && NearestHostile(ctx, out _) <= FleeClearPx)
						return SkillStatus.Running;
					dig.Stop(ctx);
					dig = null;
				}
				if (!ctx.Body.Grounded)
					return SkillStatus.Running; // dig in from the ground
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
				TilePos n = ctx.Node;
				seal = new[] { new TilePos(n.X, n.Y - 3), new TilePos(n.X + 1, n.Y - 3) };
				if (s == SkillStatus.Failed)
					ctx.Log.Info($"Shelter: could not dig down ({why}); waiting here at {n}");
				phase = Phase.Sealing;
				actionTicks = 0;
				return SkillStatus.Running;
			}
			case Phase.Sealing: {
				TilePos? open = seal.Select(c => (TilePos?)c).FirstOrDefault(c => !ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid());
				if (open == null) {
					sealedOk = true;
					ctx.Log.Info($"Shelter: sealed in at {ctx.Node}, waiting {(until != null ? why : "for morning")}");
					phase = Phase.Waiting;
					return SkillStatus.Running;
				}
				if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0 || ++actionTicks > MaxActionTicks) {
					ctx.Log.Info($"Shelter: cannot seal {open.Value} (blocks {ctx.Blocks?.BlockCount ?? 0}); waiting unsealed");
					phase = Phase.Waiting;
					return SkillStatus.Running;
				}
				ctx.Output.Tool = ToolRequest.Block;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(open.Value.X, open.Value.Y);
				return SkillStatus.Running;
			}
			case Phase.Waiting:
				if (until != null ? !until(ctx) && ++waitTicks <= MaxRetreatTicks : ctx.IsNight)
					return SkillStatus.Running;
				ctx.Log.Info(until != null ? $"Shelter: done waiting ({why}), opening up" : "Shelter: morning, opening up");
				phase = Phase.Unsealing;
				actionTicks = 0;
				return SkillStatus.Running;
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
