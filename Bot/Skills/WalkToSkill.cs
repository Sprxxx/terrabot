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

	private readonly AStarPathfinder pathfinder = new();
	private PhysicsParams plannedPhysics;
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
		Goal = GoalResolver.StandNear(ctx.Grid, Target, ctx.Physics);
		ctx.Log.Info($"WalkTo start: target {Target} -> {Goal}, bot at {ctx.Node}");
		State = "planning";
	}

	public void Stop(BotContext ctx)
	{
		Executor = null;
		ctx.Output = MoveInput.None;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			Executor = null; // plan again from wherever we end up
			return SkillStatus.Running;
		}

		TilePos node = ctx.Node;
		if (ctx.Body.Grounded && Goal.IsGoal(node)) {
			ctx.Log.Info($"WalkTo reached {Goal} at {node} after {plans} plan(s), {Replans} replan(s)");
			State = "arrived";
			return SkillStatus.Succeeded;
		}

		if (Executor != null && ctx.Physics != plannedPhysics) {
			ctx.Log.Info($"Physics changed ({ctx.Physics}); re-planning");
			Executor = null;
		}

		if (Executor == null) {
			if (waitTicks > 0) {
				waitTicks--;
				return SkillStatus.Running;
			}
			if (!ctx.Body.Grounded) {
				State = "waiting to land";
				return SkillStatus.Running;
			}
			if (!Plan(ctx, node))
				return NoPathCount >= MaxNoPath ? Fail(ctx, $"no path after {NoPathCount} attempts ({LastPlan})") : SkillStatus.Running;
		}

		MoveInput input = Executor.Tick(ctx.Body);
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

	private bool Plan(BotContext ctx, TilePos node)
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
		plannedPhysics = ctx.Physics;
		LastPlan = pathfinder.FindPath(moves, start, Goal);
		string steps = string.Join(" | ", LastPlan.Steps.Take(40).Select(s => s.ToString()));
		ctx.Log.Info($"Plan #{plans} {start} -> {Goal}: {LastPlan}{(steps.Length > 0 ? " :: " + steps : "")}");

		if (LastPlan.Status is PathStatus.Found or PathStatus.Partial && LastPlan.Steps.Count > 0) {
			Executor = new PathExecutor(moves, LastPlan.Steps);
			State = "executing";
			return true;
		}

		NoPathCount++;
		State = $"no path ({LastPlan.Status}), retry {NoPathCount}/{MaxNoPath}";
		waitTicks = NoPathRetryDelay;
		return false;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"WalkTo {Target} failed: {reason}");
		return SkillStatus.Failed;
	}
}
