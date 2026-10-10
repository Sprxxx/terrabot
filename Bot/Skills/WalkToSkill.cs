using System.Linq;
using TerraBot.Core.Execution;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Walk/jump/drop to a target tile: plan with A*, follow with <see cref="PathExecutor"/>, re-plan when the
/// executor reports Stuck / OffPath / Blocked, when a partial path runs out, or when physics params change.
/// </summary>
public sealed class WalkToSkill : ISkill
{
	public const int MaxReplans = 25;
	public const int MaxNoPath = 3;
	private const int NoPathRetryDelay = 60;

	private static readonly TilePos[] StartCandidates = {
		new(0, 0), new(-1, 0), new(1, 0), new(0, -1), new(0, 1), new(-1, -1), new(1, -1),
	};

	/// <summary>CPU budget per game tick for A* (a frame is ~16.7 ms).</summary>
	public const double PlanBudgetMsPerTick = 4.0;

	private PathSearch search;
	private MoveContext searchMoves;
	private int waitTicks;
	private int plans;

	public TilePos Target { get; }
	public GoalStandAt Goal { get; private set; }
	public PathResult LastPlan { get; private set; }
	public PathExecutor Executor { get; private set; }
	public int Replans { get; private set; }
	public int NoPathCount { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";

	public string Name => "WalkTo";

	public WalkToSkill(TilePos target)
	{
		Target = target;
	}

	public string Describe()
	{
		string path = Executor == null ? "" : $" step {Executor.StepIndex + 1}/{Executor.Steps.Count}";
		return $"WalkTo {Target} goal {Goal?.Feet} [{State}]{path} replans {Replans}";
	}

	public void Start(BotContext ctx)
	{
		Goal = GoalResolver.StandNear(ctx.Grid, Target, ctx.Physics, ctx.Moves.CanBreak);
		ctx.Log.Info($"WalkTo start: target {Target} -> {Goal}, bot at {ctx.Node}");
		State = "planning";
	}

	public void Stop(BotContext ctx)
	{
		Executor = null;
		search = null;
		ctx.Output = MoveInput.None;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			Executor = null; // plan again from wherever we end up
			search = null;
			return SkillStatus.Running;
		}

		TilePos node = ctx.Node;
		if (ctx.Body.Grounded && Goal.IsGoal(node)) {
			ctx.Log.Info($"WalkTo reached {Goal} at {node} after {plans} plan(s), {Replans} replan(s)");
			State = "arrived";
			return SkillStatus.Succeeded;
		}

		if (Executor != null && !ReferenceEquals(ctx.Moves, searchMoves))
			CheckPlanStillFits(ctx);

		if (Executor == null) {
			if (search == null) {
				if (waitTicks > 0) {
					waitTicks--;
					return SkillStatus.Running;
				}
				if (!ctx.Body.Grounded) {
					State = "waiting to land";
					return SkillStatus.Running;
				}
				StartSearch(ctx, node);
			}
			// Stand still while searching; long searches are spread over several ticks.
			if (!search.Step(PlanBudgetMsPerTick)) {
				State = $"planning ({search.Expanded} nodes)";
				return SkillStatus.Running;
			}
			if (!FinishSearch(ctx))
				return NoPathCount >= MaxNoPath ? Fail(ctx, $"no path after {NoPathCount} attempts ({LastPlan})") : SkillStatus.Running;
		}

		MoveInput input = Executor.Tick(ctx.Body);
		if (Executor.PopNotice() is string notice)
			ctx.Log.Info(notice);
		if (ctx.Trace)
			ctx.Log.Debug($"TRACE t{ctx.Tick} node {node} {ctx.Body} step {Executor.StepIndex} {Executor.Current} [{Executor.Phase}] in {input}");

		switch (Executor.Status) {
			case ExecStatus.Running:
				ctx.Output = input;
				State = Executor.Phase;
				break;
			case ExecStatus.Finished:
				// Either the goal check above succeeds next tick, or this was a partial path: plan the next leg.
				ctx.Log.Info($"Path finished at {node} ({LastPlan.Status})");
				Executor = null;
				break;
			default:
				Replans++;
				ctx.Log.Warn($"Re-plan #{Replans}: {Executor.Status}: {Executor.Reason}");
				Executor = null;
				if (Replans > MaxReplans)
					return Fail(ctx, $"too many re-plans ({Replans})");
				break;
		}
		return SkillStatus.Running;
	}

	private void StartSearch(BotContext ctx, TilePos node)
	{
		MoveContext moves = ctx.Moves;
		TilePos start = node;
		foreach (TilePos d in StartCandidates) {
			if (Body.IsStandable(moves.Grid, node + d)) {
				start = node + d;
				break;
			}
		}
		plans++;
		searchMoves = moves;
		search = new PathSearch(moves, start, Goal);
		State = "planning";
	}

	private bool FinishSearch(BotContext ctx)
	{
		LastPlan = search.Result;
		search = null;
		string steps = string.Join(" | ", LastPlan.Steps.Take(40).Select(s => s.ToString()));
		int breaks = LastPlan.Steps.Sum(s => s.CountActions(ActionKind.Break));
		int places = LastPlan.Steps.Sum(s => s.CountActions(ActionKind.Place));
		string world = breaks + places > 0 ? $", breaks {breaks}, places {places}" : "";
		ctx.Log.Info($"Plan #{plans} {LastPlan.Start} -> {Goal}: {LastPlan}{world}{(steps.Length > 0 ? " :: " + steps : "")}");

		if (LastPlan.Status is PathStatus.Found or PathStatus.Partial && LastPlan.Steps.Count > 0) {
			Executor = new PathExecutor(searchMoves, LastPlan.Steps);
			State = "executing";
			return true;
		}

		NoPathCount++;
		State = $"no path ({LastPlan.Status}), retry {NoPathCount}/{MaxNoPath}";
		waitTicks = NoPathRetryDelay;
		return false;
	}

	/// <summary>
	/// Physics or settings changed since the plan was made. Dig/build switches and material physics changes
	/// (jump, gravity, hitbox, more than ±25% run speed) mean re-planning. Small speed drift (e.g. a +21%
	/// movement-speed buff toggling) keeps the path: the remaining jumps are re-fitted to templates simulated
	/// with the new physics, and only if one no longer fits do we re-plan.
	/// </summary>
	private void CheckPlanStillFits(BotContext ctx)
	{
		MoveContext now = ctx.Moves;
		bool settingsChanged = !ReferenceEquals(now.Blocks, searchMoves.Blocks)
			|| now.AllowBreak != searchMoves.AllowBreak || now.AllowPlace != searchMoves.AllowPlace;
		if (settingsChanged || ctx.Physics.MateriallyDiffers(searchMoves.Physics)) {
			ctx.Log.Info($"{(settingsChanged ? "Dig/build settings" : "Physics")} changed ({ctx.Physics}; dig {ctx.AllowBreak}, build {ctx.AllowPlace}); re-planning");
			Executor = null;
			return;
		}
		PhysicsParams planned = searchMoves.Physics;
		if (!Executor.TryAdopt(now, out int refitted, out string reason)) {
			ctx.Log.Info($"Physics changed ({ctx.Physics}): {reason}; re-planning");
			Executor = null;
			return;
		}
		searchMoves = now;
		if (ctx.Physics != planned)
			ctx.Log.Info($"Physics changed slightly ({ctx.Physics}, was {planned}); keeping the plan, re-fitted {refitted} jump(s)");
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"WalkTo {Target} failed: {reason}");
		return SkillStatus.Failed;
	}
}
