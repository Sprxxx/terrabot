using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Chooses what to do and sequences skills. Milestone 1 stub: holds one movement target and runs a
/// <see cref="WalkToSkill"/> for it. Later: goal stack / priorities (survive > progress > gather).
/// </summary>
public sealed class BotBrain
{
	public TilePos? Target { get; private set; }
	public ISkill Current { get; private set; }
	public string LastResult { get; private set; } = "";

	public void SetTarget(TilePos target, BotContext ctx)
	{
		Interrupt(ctx);
		Target = target;
		LastResult = "";
	}

	/// <summary>Forget the target and stop the running skill.</summary>
	public void Clear(BotContext ctx)
	{
		Interrupt(ctx);
		Target = null;
	}

	/// <summary>Stop the running skill but keep the target (bot switched off; resumes with a fresh plan).</summary>
	public void Interrupt(BotContext ctx)
	{
		Current?.Stop(ctx);
		Current = null;
	}

	public void Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (Current == null && Target != null) {
			Current = new WalkToSkill(Target.Value);
			Current.Start(ctx);
		}
		if (Current == null)
			return;

		SkillStatus status = Current.Tick(ctx);
		if (status == SkillStatus.Running)
			return;

		LastResult = $"{Current.Name} {status}" + (Current is WalkToSkill w && status == SkillStatus.Failed ? $": {w.FailureReason}" : "");
		ctx.Log.Info($"Skill finished: {LastResult}");
		Current.Stop(ctx);
		Current = null;
		Target = null;
	}

	public string Describe() =>
		Current != null ? Current.Describe()
		: Target != null ? $"target {Target.Value} (idle)"
		: LastResult.Length > 0 ? $"idle - last: {LastResult}"
		: "idle - no target";
}
