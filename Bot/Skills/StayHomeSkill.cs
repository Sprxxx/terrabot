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

	public StayHomeSkill(TilePos insideTile) => inside = insideTile;

	public string Describe() => $"StayHome {inside} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx) => ctx.Log.Info($"StayHome: night; going into the house at {inside}");

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (!ctx.IsNight) {
			State = "morning";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (home) {
			State = "waiting for the morning";
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

	private WalkToSkill StartWalk(BotContext ctx)
	{
		var w = new WalkToSkill(new GoalNearTile(inside, 1, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 2 };
		w.Start(ctx);
		return w;
	}
}
