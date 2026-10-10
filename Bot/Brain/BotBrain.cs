using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Chooses what to do and sequences skills.
/// <list type="bullet">
/// <item>Manual: one movement target (<see cref="SetTarget"/>) or one explicit skill (<see cref="StartSkill"/>).</item>
/// <item>Auto (<see cref="SetAuto"/>): asks <see cref="ProgressionPlanner"/> for the next skill whenever nothing runs,
/// and pre-empts anything but a fight (or a sealed shelter) when a hostile comes close; after a fight it picks up
/// the loot, then re-plans from the new situation.
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
	public ProgressionPlanner Planner { get; } = new();
	/// <summary>Pick up what a fight dropped (gel, coins...) before going back to the plan.</summary>
	public const int LootRadiusTiles = 15;

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

	public void SetAuto(bool on, BotContext ctx, string plan = null)
	{
		Interrupt(ctx);
		Target = null;
		Auto = on;
		failures = 0;
		Planner.Reset(plan);
		ctx.UnreachableTiles.Clear();
		LastResult = "";
		ctx.Log.Info($"Auto mode {(on ? "ON" : "OFF")}" + (on ? $", plan '{Planner.Plan}'" : ""));
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
		if (Auto && Current is not FightSkill && Current is not ShelterSkill { Sealed: true } && ProgressionPlanner.InDanger(ctx)) {
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
				else if (Planner.Stuck) {
					ctx.Log.Warn("Auto: " + Planner.Status + "; stopping auto mode");
					LastResult = "auto: " + Planner.Status;
					Auto = false;
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

		string why = Current.FailureReason;
		LastResult = $"{Current.Name} {status}" + (status == SkillStatus.Failed && why.Length > 0 ? $": {why}" : "");
		ctx.Log.Info($"Skill finished: {LastResult}");
		bool wasFight = Current is FightSkill;
		if (Auto)
			Planner.OnSkillFinished(Current, status, ctx);
		Current.Stop(ctx);
		Current = null;
		Target = null;
		if (Auto && wasFight && CollectDropsSkill.AnyInRange(ctx, _ => true, LootRadiusTiles)) {
			ctx.Log.Info("Auto: picking up loot after the fight");
			Begin(new CollectDropsSkill(_ => true, LootRadiusTiles), ctx);
			return;
		}
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
