using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Chooses what to do and sequences skills.
/// <list type="bullet">
/// <item>Manual: one movement target (<see cref="SetTarget"/>) or one explicit skill (<see cref="StartSkill"/>).</item>
/// <item>Auto (<see cref="SetAuto"/>): asks <see cref="EarlyGamePlanner"/> for the next skill whenever nothing runs,
/// and pre-empts anything but a fight when a hostile comes close (then re-plans from the new situation).
/// A skill that fails three times in a row stops auto mode, so a stuck goal cannot loop forever.</item>
/// </list>
/// </summary>
public sealed class BotBrain
{
	public const int MaxConsecutiveFailures = 3;

	private int failures;

	public TilePos? Target { get; private set; }
	public ISkill Current { get; private set; }
	public string LastResult { get; private set; } = "";
	public bool Auto { get; private set; }
	public EarlyGamePlanner Planner { get; } = new();

	public void SetTarget(TilePos target, BotContext ctx)
	{
		Interrupt(ctx);
		Auto = false;
		Target = target;
		LastResult = "";
	}

	/// <summary>Run <paramref name="skill"/> now (replacing whatever runs, and forgetting any walk target).</summary>
	public void StartSkill(ISkill skill, BotContext ctx)
	{
		Interrupt(ctx);
		Auto = false;
		Target = null;
		LastResult = "";
		Current = skill;
		skill.Start(ctx);
	}

	public void SetAuto(bool on, BotContext ctx)
	{
		Interrupt(ctx);
		Target = null;
		Auto = on;
		failures = 0;
		Planner.Reset();
		LastResult = "";
		ctx.Log.Info($"Auto mode {(on ? "ON" : "OFF")}");
	}

	/// <summary>Forget the target, leave auto mode and stop the running skill.</summary>
	public void Clear(BotContext ctx)
	{
		Interrupt(ctx);
		Target = null;
		Auto = false;
	}

	/// <summary>Stop the running skill but keep the target/mode (bot switched off; resumes with a fresh plan).</summary>
	public void Interrupt(BotContext ctx)
	{
		Current?.Stop(ctx);
		Current = null;
	}

	public void Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (Auto && Current is not FightSkill && EarlyGamePlanner.InDanger(ctx)) {
			if (Current != null)
				ctx.Log.Info($"Danger: pausing {Current.Name} to fight");
			Interrupt(ctx);
			Begin(new FightSkill(), ctx);
		}
		if (Current == null && Target != null)
			Begin(new WalkToSkill(Target.Value), ctx);
		if (Current == null && Auto) {
			ISkill next = Planner.Next(ctx);
			if (next == null) {
				if (Planner.Complete && LastResult != "auto: done") {
					ctx.Log.Info("Auto: " + Planner.Status);
					LastResult = "auto: done";
				}
				return;
			}
			ctx.Log.Info($"Auto: {Planner.Status} -> {next.Name}");
			Begin(next, ctx);
		}
		if (Current == null)
			return;

		SkillStatus status = Current.Tick(ctx);
		if (status == SkillStatus.Running)
			return;

		string why = Current switch {
			WalkToSkill w => w.FailureReason,
			FightSkill f => f.FailureReason,
			ChopTreeSkill c => c.FailureReason,
			GatherWoodSkill g => g.FailureReason,
			CraftSkill c => c.FailureReason,
			PlaceItemSkill p => p.FailureReason,
			_ => "",
		};
		LastResult = $"{Current.Name} {status}" + (status == SkillStatus.Failed && why.Length > 0 ? $": {why}" : "");
		ctx.Log.Info($"Skill finished: {LastResult}");
		if (Auto)
			Planner.OnSkillFinished(Current, status, ctx);
		Current.Stop(ctx);
		Current = null;
		Target = null;
		if (Auto) {
			failures = status == SkillStatus.Failed ? failures + 1 : 0;
			if (failures >= MaxConsecutiveFailures) {
				ctx.Log.Warn($"Auto: {failures} failures in a row, stopping auto mode ({LastResult})");
				Auto = false;
			}
		}
	}

	private void Begin(ISkill skill, BotContext ctx)
	{
		Current = skill;
		skill.Start(ctx);
	}

	public string Describe() =>
		Current != null ? (Auto ? "auto: " : "") + Current.Describe()
		: Target != null ? $"target {Target.Value} (idle)"
		: Auto ? "auto: " + Planner.Status
		: LastResult.Length > 0 ? $"idle - last: {LastResult}"
		: "idle - no target";
}
