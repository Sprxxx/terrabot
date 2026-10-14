using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Walk up to a chest and take everything in it (<see cref="ICrafter.LootChest"/>: right-click it, Loot All, close).
/// Succeeds once it is looted; fails if it cannot be reached or will not open.
/// </summary>
public sealed class LootChestSkill : ISkill
{
	public const int MaxTicks = 60 * 60;
	/// <summary>Stand within this many tiles of the chest (the game's interaction range is about 5 × 4).</summary>
	public const int StandWithin = 2;
	/// <summary>Ticks to keep trying to open it once there.</summary>
	public const int MaxOpenTicks = 30;

	private WalkToSkill walk;
	private int ticks;
	private int openTicks;
	private bool arrived;

	public TilePos Chest { get; }
	/// <summary>What was taken (after success).</summary>
	public string Loot { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "LootChest";
	public string Key => $"LootChest {Chest}";

	public LootChestSkill(TilePos chestTopLeft) => Chest = chestTopLeft;

	public string Describe() => $"LootChest {Chest} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"LootChest start: chest at {Chest}, bot at {ctx.Node}");

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		if (++ticks > MaxTicks)
			return Fail(ctx, $"not looted after {MaxTicks} ticks");
		if (ctx.Body.Grounded) {
			string got = ctx.Crafter.LootChest(Chest);
			if (got != null) {
				Loot = got;
				ctx.Log.Info($"LootChest: opened the chest at {Chest}: {(got.Length > 0 ? got : "empty")}");
				State = "looted";
				Stop(ctx);
				return SkillStatus.Succeeded;
			}
		}
		if (arrived) {
			State = "opening";
			return ++openTicks > MaxOpenTicks ? Fail(ctx, "in reach but it would not open (locked?)") : SkillStatus.Running;
		}
		if (walk == null) {
			walk = new WalkToSkill(new GoalNearTile(Chest, StandWithin, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 2, Limits = MineSkill.ReachLimits };
			walk.Start(ctx);
		}
		State = "walking";
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Failed)
			return Fail(ctx, "cannot get to it: " + walk.FailureReason);
		if (s == SkillStatus.Succeeded) {
			walk.Stop(ctx);
			walk = null;
			arrived = true;
		}
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"LootChest {Chest} failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
