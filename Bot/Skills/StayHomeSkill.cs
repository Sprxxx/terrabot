using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Spend the night in the house, like a player: walk inside and wait for the morning. Enemies that get in are fought
/// by the brain as usual. Seen in game: a zombie killed the bot walking home across the surface at night.
/// </summary>
public sealed class StayHomeSkill : ISkill
{
	private readonly TilePos inside;
	private WalkToSkill walk;
	private bool home;

	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "StayHome";
	public string Key => $"StayHome {inside} {RecoverTo}";
	/// <summary>Inside, waiting: enemies outside the walls are left alone (see BotBrain).</summary>
	public bool Home => home;
	/// <summary>Where it is going.</summary>
	public TilePos Inside => inside;

	public StayHomeSkill(TilePos insideTile) => inside = insideTile;

	/// <summary>When above 0: a retreat home to recover to this life fraction (day or night), not a night at home.</summary>
	public float RecoverTo { get; init; }

	public string Describe() => $"StayHome {inside} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		startTick = ctx.Tick;
		ctx.Log.Info($"StayHome: {(RecoverTo > 0 ? "hurt" : "night")}; going into the house at {inside}");
	}

	private long startTick;

	/// <summary>
	/// A hurt flight home that an enemy hit since it began (as for a shelter, <see cref="ShelterSkill.FightBackReason"/>): it
	/// is not getting away. Seen in game (measured run M10e, world_eow): 3 of 5 deaths on flights home from the corruption,
	/// 300 tiles, "walking home" while a zombie, eaters of souls and devourers hit it 8 times and more, not one shot back.
	/// </summary>
	public string FightBackReason(BotContext ctx) =>
		RecoverTo > 0 && !home && ctx.LastEnemyHitTick >= startTick && ctx.Tick - ctx.LastEnemyHitTick < ShelterSkill.HitRecentTicks
			? "hit on the way home" : null;

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	/// <summary>Paused for a reflex: the sub-skills pause too, and go on from where they were (their plans kept).</summary>
	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
	}

	public void Resume(BotContext ctx)
	{
		walk?.Resume(ctx);
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (RecoverTo > 0 ? home && ctx.Combat.LifeFraction >= RecoverTo : !ctx.IsNight) {
			// Morning, but the night's zombies still at the door: wait in for them to go (they leave by day). Seen in game
			// (world_eye run 63): out at first light among 6 zombies, killed below the house.
			if (home && HostilesOutside(ctx) is int n and > 0 && ++morningWait <= MaxMorningWaitTicks) {
				State = $"morning; waiting for {n} hostile(s) outside to go";
				return SkillStatus.Running;
			}
			State = RecoverTo > 0 ? "recovered" : "morning";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (home) {
			State = RecoverTo > 0 ? "recovering" : "waiting for the morning";
			return SkillStatus.Running;
		}
		walk ??= StartWalk(ctx);
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Running) {
			State = "walking home";
			return SkillStatus.Running;
		}
		walk.Stop(ctx);
		if (s == SkillStatus.Failed) {
			FailureReason = "cannot get home: " + walk.FailureReason;
			walk = null;
			return SkillStatus.Failed;
		}
		walk = null;
		home = true;
		ctx.Log.Info($"StayHome: inside at {ctx.Node}; waiting for the morning");
		return SkillStatus.Running;
	}

	/// <summary>Longest a morning wait for the hostiles outside to leave (3 game hours).</summary>
	public const int MaxMorningWaitTicks = 3 * 3600;
	/// <summary>Hostiles this close (tiles) keep the bot in after the night.</summary>
	public const int OutsideTiles = 25;
	private int morningWait;

	private static int HostilesOutside(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		int n = 0;
		foreach (EntityInfo e in ctx.Hostiles)
			if (e.DistanceTo(cx, cy) <= OutsideTiles * 16f)
				n++;
		return n;
	}

	private WalkToSkill StartWalk(BotContext ctx)
	{
		var w = new WalkToSkill(new GoalNearTile(inside, 1, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 2 };
		w.Start(ctx);
		return w;
	}
}
