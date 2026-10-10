using System;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Summon a boss and fight it with a bow, kiting. Written for the Eye of Cthulhu (vanilla NPC 4: hovers above
/// the player, dashes at it three times in a row, spits Servants of Cthulhu in phase 1, loses its defense in
/// phase 2, leaves at dawn):
/// <list type="bullet">
/// <item>summon: use the summon item (the game only allows it at night) unless the boss is already here;</item>
/// <item>move: run back and forth across an arena <see cref="ArenaHalfWidth"/> tiles to either side of where the
/// fight started, turning at the ends, with a hop every so often (a moving target is hard to dash into);</item>
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
	public const int HopEvery = 50;
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
	private int hop;
	private int blocked;
	private bool healPressedLastTick;
	private TilePos? centre;
	private int bossLifeSeen = int.MaxValue;

	public int SummonItem { get; }
	public int BossType { get; }
	public int[] MinionTypes { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Boss";

	public BossFightSkill(int summonItem, int bossType, params int[] minionTypes)
	{
		SummonItem = summonItem;
		BossType = bossType;
		MinionTypes = minionTypes;
	}

	public string Describe() => $"Boss {BossType} [{State}]" + (bossLifeSeen < int.MaxValue ? $" boss life {bossLifeSeen}" : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"Boss fight start: boss {BossType}, summon item {SummonItem}, {ctx.Combat.RangedName} with {ctx.Combat.AmmoCount} arrows, life {ctx.Combat.Life}/{ctx.Combat.LifeMax}");

	public void Stop(BotContext ctx) => summon?.Stop(ctx);

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.World.BossDefeated(BossType)) {
			ctx.Log.Info($"Boss fight: boss {BossType} defeated after {fightTicks} ticks");
			State = "won";
			return SkillStatus.Succeeded;
		}
		if (ctx.BodyIssue == "dead")
			return Fail(ctx, "died");
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		centre ??= ctx.Node;

		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		EntityInfo? boss = ctx.Hostiles.Where(h => h.Type == BossType).Cast<EntityInfo?>().FirstOrDefault();

		if (boss == null && !seenBoss) {
			if (summoned) {
				// The item is used up the moment the game spawns the boss, a few tiles off screen: wait for it to show.
				if (++waitForBoss > MaxWaitForBoss)
					return Fail(ctx, $"no boss within {MaxWaitForBoss} ticks of summoning");
				State = "waiting for the boss";
				Kite(ctx);
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
			if (b.Life < bossLifeSeen - b.LifeMax / 10 || b.Life <= b.LifeMax / 2 && bossLifeSeen > b.LifeMax / 2)
				ctx.Log.Info($"Boss fight: {b.Name} at {b.Life}/{b.LifeMax}, us {ctx.Combat.Life}/{ctx.Combat.LifeMax}, {ctx.Combat.AmmoCount} arrows");
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

		Kite(ctx);
		// Target: a servant about to bite, else the boss.
		EntityInfo? servant = ctx.Hostiles.Where(h => MinionTypes.Contains(h.Type) && h.DistanceTo(cx, cy) <= ServantDangerTiles * 16f)
			.OrderBy(h => h.DistanceTo(cx, cy)).Cast<EntityInfo?>().FirstOrDefault();
		EntityInfo? target = servant ?? boss;
		if (target is EntityInfo t && FightSkill.ClearShot(ctx, cx, cy, t.CenterX, t.CenterY)) {
			AimSolution s = Ballistics.Lead(cx, cy, t.CenterX, t.CenterY, t.VX, t.VY, ctx.Combat.Projectile);
			ctx.Output.Tool = ToolRequest.Ranged;
			ctx.Output.AimMouse(s.AimX, s.AimY);
			ctx.Output.UseItem = s.Hits;
			State = servant != null ? "shooting a servant" : "shooting the boss";
		}
		else
			State = "kiting";
		Heal(ctx);
		return SkillStatus.Running;
	}

	/// <summary>Run to one end of the arena and back, hopping now and then; turn early at walls.</summary>
	private void Kite(BotContext ctx)
	{
		TilePos node = ctx.Node;
		int offset = node.X - centre.Value.X;
		if (offset >= ArenaHalfWidth)
			runDir = -1;
		else if (offset <= -ArenaHalfWidth)
			runDir = 1;
		else if (ctx.Body.Grounded && Math.Abs(ctx.Body.VX) < 0.1f) {
			if (++blocked > 10) {
				runDir = -runDir; // against a wall: turn round
				blocked = 0;
			}
		}
		else
			blocked = 0;
		ctx.Output.Horizontal = runDir;
		if (ctx.Body.Grounded && ++hop >= HopEvery) {
			hop = 0;
			ctx.Output.Jump = true;
		}
	}

	private void Heal(BotContext ctx)
	{
		bool want = ctx.Combat.LifeFraction < HealBelow && ctx.Combat.CanQuickHeal && !healPressedLastTick;
		ctx.Output.QuickHeal = want;
		healPressedLastTick = want;
		if (want)
			ctx.Log.Info($"Boss fight: quick-heal at {ctx.Combat.Life}/{ctx.Combat.LifeMax}");
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
