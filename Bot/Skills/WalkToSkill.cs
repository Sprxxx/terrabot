using System.Collections.Generic;
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
	public IGoal Goal { get; private set; }
	public PathResult LastPlan { get; private set; }
	public PathExecutor Executor { get; private set; }
	public int Replans { get; private set; }
	/// <summary>Searches started by this walk (re-plans and resumed plans not included in the latter).</summary>
	public int Plans => plans;
	public int NoPathCount { get; private set; }
	/// <summary>The last search started on a goal node next to the body (a zero-step Found).</summary>
	public bool ArrivedNearGoal { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";

	public string Name => "WalkTo";
	public string Key => $"WalkTo {(fixedGoal != null ? fixedGoal.ToString() : Target.ToString())}{(Travel ? " travel" : "")}{(ToSurface ? " to surface" : "")}{(Underground ? " underground" : "")}{(Home ? " home" : "")}";

	/// <summary>Used as a sub-skill (e.g. chasing a moving enemy): plan/finish lines go to Debug instead of Info.</summary>
	public bool Quiet { get; init; }
	/// <summary>A trip across the surface to new ground (travel for ore): surface work, so not at night without armour.</summary>
	public bool Travel { get; init; }
	/// <summary>A walk up to the surface (the start of a trip): surface work at night too, wherever it starts from.</summary>
	public bool ToSurface { get; init; }
	/// <summary>A walk down into the ground for the night's mining: not surface work, though it starts there.</summary>
	public bool Underground { get; init; }
	/// <summary>A walk home to the house at night (not surface work the night turns into staying home).</summary>
	public bool Home { get; init; }

	/// <summary>Accept any standing node within this many tiles (Chebyshev) of the goal feet tile.</summary>
	public int Tolerance { get; init; }

	/// <summary>"No path" results before giving up (sub-skills that have alternatives use 1).</summary>
	public int MaxNoPathAttempts { get; init; } = MaxNoPath;

	/// <summary>A* limits (null = the defaults). Sub-skills with alternatives give up sooner on far-off targets.</summary>
	public SearchLimits Limits { get; init; }
	/// <summary>After a no-path with the broken-support check: plan the rest of this walk without it.</summary>
	private bool relaxed;

	public WalkToSkill(TilePos target)
	{
		Target = target;
	}

	/// <summary>Walk to any node satisfying <paramref name="goal"/> (e.g. <see cref="GoalReachTile"/>).</summary>
	public WalkToSkill(IGoal goal)
	{
		Target = goal.Anchor;
		fixedGoal = goal;
	}

	private readonly IGoal fixedGoal;
	private bool forgaveHere;

	/// <summary>Banned steps (BotContext.BadEdges) from or to a node within 2 tiles of <paramref name="at"/> are lifted; returns how many.</summary>
	public static int ForgiveBansAround(BotContext ctx, TilePos at) =>
		ctx.BadEdges.RemoveWhere(e => e.Item1.ChebyshevTo(at) <= 2 || e.Item2.ChebyshevTo(at) <= 2);
	/// <summary>Longest wait (ticks) for the ground before planning: a walk held in the air fails instead.</summary>
	public const int MaxAirborneTicks = 180;
	/// <summary>Ticks of mining out of a block the body is inside before planning from there anyway.</summary>
	public const int MaxUnstickTicks = 240;
	private int unstickTicks;

	/// <summary>A solid block the player's hitbox overlaps (it should never), that the pickaxe can break; null if none.</summary>
	public static TilePos? EmbeddedIn(BotContext ctx)
	{
		const float T = Body.TileSize, Margin = 0.5f;
		float left = ctx.Body.X, top = ctx.Body.Y, right = left + ctx.Physics.Width, bottom = top + ctx.Physics.Height;
		for (int x = (int)System.Math.Floor((left + Margin) / T); x <= (int)System.Math.Floor((right - Margin) / T); x++)
			for (int y = (int)System.Math.Floor((top + Margin) / T); y <= (int)System.Math.Floor((bottom - Margin) / T); y++)
				if (ctx.Grid.Get(x, y).IsSolid() && ctx.Moves.Blocks is IBlockActions b && !float.IsInfinity(b.BreakTicks(x, y)))
					return new TilePos(x, y);
		return null;
	}
	/// <summary>A no-path search at least this big is not tried again from the same place.</summary>
	public const int BigNoPath = 20_000;
	/// <summary>Longest swim (ticks) towards the target from deep water before the walk gives up.</summary>
	public const int MaxSwimTicks = 60 * 20;
	/// <summary>Ticks bobbing in water over something to stand on before that counts as landed.</summary>
	public const int SettleInWaterTicks = 30;
	private int airborneTicks;
	private TilePos? swimTo;
	private TilePos swimFrom;
	private TilePos heldFrom;
	private int heldStill;
	public const int HeldStillTicks = 60;

	/// <summary>A real tile (any kind) the hitbox overlaps, else one in the row over the head; null if none.</summary>
	internal static TilePos? TileInOrOverBody(BotContext ctx)
	{
		const float T = Body.TileSize, Margin = 0.5f;
		float left = ctx.Body.X, top = ctx.Body.Y, right = left + ctx.Physics.Width, bottom = top + ctx.Physics.Height;
		int x0 = (int)System.Math.Floor((left + Margin) / T), x1 = (int)System.Math.Floor((right - Margin) / T);
		int y0 = (int)System.Math.Floor((top + Margin) / T) - 1, y1 = (int)System.Math.Floor((bottom - Margin) / T);
		for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
				if (ctx.World.TileTypeAt(x, y) >= 0 && !ctx.World.IsTreeTrunk(x, y) && ctx.Moves.Blocks is IBlockActions b && !float.IsInfinity(b.BreakTicks(x, y)))
					return new TilePos(x, y);
		return null;
	}
	private int swimStill;
	/// <summary>Ticks afloat at the same node before what is over the head gets mined.</summary>
	public const int SwimStillTicks = 120;
	public const int SwimReplanTicks = 60, SwimSearchCells = 2000;

	/// <summary>
	/// Somewhere to stand, breadth-first through passable cells (water and air) from <paramref name="from"/>: a dry spot
	/// (body out of the liquid) on the target's side if there is one in reach (cells swum plus half the way left to the
	/// target), else the nearest spot at all (a pool's floor).
	/// </summary>
	private TilePos? NearestFooting(BotContext ctx, TilePos from)
	{
		ITileGrid g = ctx.Grid;
		var dist = new System.Collections.Generic.Dictionary<TilePos, int> { [from] = 0 };
		var queue = new System.Collections.Generic.Queue<TilePos>();
		queue.Enqueue(from);
		TilePos? wet = null, dry = null;
		float bestDry = float.MaxValue;
		while (queue.Count > 0 && dist.Count < SwimSearchCells) {
			TilePos c = queue.Dequeue();
			int dc = dist[c];
			if (c != from && Body.IsStandable(g, c)) {
				if (!Body.InLiquid(g, c)) {
					float score = dc + 0.5f * c.ChebyshevTo(Target);
					if (score < bestDry) {
						bestDry = score;
						dry = c;
					}
				}
				else
					wet ??= c;
			}
			foreach (TilePos d in new[] { new TilePos(0, -1), new TilePos(-1, 0), new TilePos(1, 0), new TilePos(0, 1) }) {
				var n = new TilePos(c.X + d.X, c.Y + d.Y);
				if (!dist.ContainsKey(n) && Body.IsClear(g, n)) {
					dist[n] = dc + 1;
					queue.Enqueue(n);
				}
			}
		}
		return dry ?? wet;
	}

	private void Info(BotContext ctx, string message)
	{
		if (Quiet)
			ctx.Log.Debug(message);
		else
			ctx.Log.Info(message);
	}

	public string Describe()
	{
		string path = Executor == null ? "" : $" step {Executor.StepIndex + 1}/{Executor.Steps.Count}";
		return $"WalkTo {Target} goal {Goal} [{State}]{path} replans {Replans}";
	}

	public void Start(BotContext ctx)
	{
		if (fixedGoal != null)
			Goal = fixedGoal;
		else {
			GoalStandAt stand = GoalResolver.StandNear(ctx.Grid, Target, ctx.Physics, ctx.Moves.CanBreak);
			Goal = Tolerance > 0 ? new GoalStandAt(stand.Feet, ctx.Physics) { Tolerance = Tolerance } : stand;
		}
		Info(ctx, $"WalkTo start: target {Target} -> {Goal}, bot at {ctx.Node}");
		State = "planning";
	}

	public void Stop(BotContext ctx)
	{
		Executor = null;
		search = null;
		ahead = null;
		pausedSteps = null;
		ctx.Output = MoveInput.None;
	}

	/// <summary>
	/// The rest of the plan, kept through a pause (a fight, a body issue): on the way again the walk carries on from
	/// whichever kept step the bot stands at, and plans afresh only when it is off them. Seen in game (measured run N1):
	/// 3,668 walk starts a run, every fight and resumption planning the whole way again (about 75 ticks standing each).
	/// </summary>
	private System.Collections.Generic.List<PathStep> pausedSteps;
	/// <summary>Kept steps searched for the bot's node on the way again.</summary>
	public const int ResumeLookahead = 40;

	public void Pause(BotContext ctx)
	{
		KeepPlan();
		ctx.Output = MoveInput.None;
		State = "paused";
	}

	public void Resume(BotContext ctx)
	{
		airborneTicks = 0; // a swim interrupted by a fight starts afresh (the count ran on through it and the walk failed at once)
		swimTo = null;
		Info(ctx, $"WalkTo resumed: {(pausedSteps != null ? $"{pausedSteps.Count} step(s) of the plan kept" : "planning again")}, bot at {ctx.Node}");
		State = "resumed";
	}

	/// <summary>
	/// The next segment, searched while the bot walks the end of a partial one (Baritone plans the next segment while the
	/// current one runs): from the plan's last node, on the world as the remaining steps will leave it, a few ms a tick.
	/// When the segment ends it carries straight on. Seen in game: every segment's end stopped the bot for its search.
	/// </summary>
	private PathSearch ahead;
	private TilePos aheadFrom;
	private MoveContext aheadMoves, aheadBase;
	private readonly OverlayGrid aheadGrid = new();
	/// <summary>Steps left in a partial plan when the next segment's search starts, and its CPU budget per tick.</summary>
	public const int PlanAheadSteps = 12;
	public const double PlanAheadMsPerTick = 2.0;
	/// <summary>Segments joined on without a stop (tests and logs).</summary>
	public int Splices { get; private set; }

	private void PlanAhead(BotContext ctx)
	{
		if (Executor == null || LastPlan == null || LastPlan.Status != PathStatus.Partial)
			return;
		if (ahead == null) {
			if (Executor.Steps.Count - Executor.StepIndex > PlanAheadSteps || relaxed)
				return;
			IReadOnlyList<PathStep> steps = Executor.Steps;
			aheadGrid.Reset(ctx.Moves.Grid);
			for (int i = Executor.StepIndex; i < steps.Count; i++)
				if (steps[i].Actions != null)
					foreach (BlockAction a in steps[i].Actions)
						aheadGrid.Set(a.Cell.X, a.Cell.Y, a.Kind == ActionKind.Break ? CellKind.Empty : CellKind.Solid);
			if (!ReferenceEquals(aheadBase, ctx.Moves)) {
				aheadBase = ctx.Moves;
				aheadMoves = ctx.Moves.WithGrid(aheadGrid);
			}
			aheadFrom = steps[steps.Count - 1].To;
			ahead = new PathSearch(aheadMoves, aheadFrom, new GoalAsStood(Goal, aheadMoves.Grid), Limits ?? SearchLimits.Default);
		}
		if (!ahead.Done)
			ahead.Step(PlanAheadMsPerTick);
	}

	/// <summary>The segment ended: carry on with the one planned ahead, if it is ready, starts here and its first steps hold.</summary>
	private bool TrySplice(BotContext ctx, TilePos node)
	{
		PathSearch next = ahead;
		ahead = null;
		if (next == null || node != aheadFrom)
			return false;
		if (!next.Done)
			next.Step(PlanBudgetMsPerTick);
		if (!next.Done) {
			// Not ready yet: finish it standing here rather than starting over (FinishSearch then follows it on the live grid).
			search = next;
			searchMoves = ctx.Moves;
			plans++;
			State = "planning";
			return true;
		}
		if (next.Result.Status is not (PathStatus.Found or PathStatus.Partial) || next.Result.Steps.Count == 0)
			return false;
		for (int i = 0; i < System.Math.Min(3, next.Result.Steps.Count); i++)
			if (!ctx.Moves.IsStepValid(next.Result.Steps[i]))
				return false;
		LastPlan = next.Result;
		plans++;
		Splices++;
		Executor = new PathExecutor(ctx.Moves, next.Result.Steps);
		searchMoves = ctx.Moves;
		// With its steps, as FinishSearch logs a plan: measured run U22 died on the last step of a planned-ahead segment
		// whose steps the log never showed.
		Info(ctx, $"Plan #{plans} (planned ahead) {LastPlan.Start} -> {Goal}: {LastPlan}; carrying straight on :: {StepList(LastPlan.Steps)}");
		State = "executing";
		return true;
	}

	private void KeepPlan()
	{
		if (Executor != null && Executor.Status == ExecStatus.Running && Executor.StepIndex < Executor.Steps.Count)
			pausedSteps = Executor.Steps.Skip(Executor.StepIndex).ToList();
		Executor = null;
		search = null;
		ahead = null;
	}

	/// <summary>Back on the kept plan if the bot stands on one of its next steps; true when an executor is set up.</summary>
	private bool TryResumePlan(BotContext ctx, TilePos node)
	{
		System.Collections.Generic.List<PathStep> kept = pausedSteps;
		pausedSteps = null;
		if (kept == null)
			return false;
		int n = System.Math.Min(kept.Count, ResumeLookahead);
		for (int i = 0; i < n; i++) {
			if (!PathExecutor.SameNode(ctx.Grid, node, kept[i].From) || !ctx.Moves.IsStepValid(kept[i]))
				continue;
			Executor = new PathExecutor(ctx.Moves, kept.GetRange(i, kept.Count - i));
			searchMoves = ctx.Moves;
			Info(ctx, $"WalkTo: back on the plan at {node}, step {i + 1} of the {kept.Count} kept");
			State = "executing";
			return true;
		}
		return false;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			KeepPlan(); // back on it if the bot is still on it afterwards, else plan again
			return SkillStatus.Running;
		}

		TilePos node = ctx.Node;
		if (ctx.Body.Grounded && Goal.IsGoal(node)) {
			Info(ctx, $"WalkTo reached {Goal} at {node} after {plans} plan(s), {Replans} replan(s)");
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
				// Bobbing in water on a slope is standing too (vy stays about -0.2 there, never 0): seen in game, every walk
				// from such a spot waited to land until it gave up.
				bool settledInWater = airborneTicks >= SettleInWaterTicks && System.Math.Abs(ctx.Body.VY) < 0.5f
					&& (ctx.Grid.Get(node.X, node.Y).IsLiquid() || ctx.Grid.Get(node.X + 1, node.Y).IsLiquid())
					&& (ctx.Grid.Get(node.X, node.Y + 1).CanStandOn() || ctx.Grid.Get(node.X + 1, node.Y + 1).CanStandOn());
				if (!ctx.Body.Grounded && !settledInWater) {
					// Not for ever: seen in game (world_eye, the first run with paused work resuming), "waiting to land" at one
					// spot for three game days, never moving (held in the air by something: a cobweb, a ledge corner). Fights
					// and nightfall used to throw the job away and so broke it; resumed, nothing did.
					// Afloat in deep water (nothing under the feet but more water): swim for it like a player, towards the
					// target with jump strokes, until there is footing. Seen in game (measured run D): "never landed ... under
					// Water/Water", every walk failing from the middle of a pool.
					bool afloat = (ctx.Grid.Get(node.X, node.Y).IsLiquid() || ctx.Grid.Get(node.X + 1, node.Y).IsLiquid())
						&& !ctx.Grid.Get(node.X, node.Y + 1).CanStandOn() && !ctx.Grid.Get(node.X + 1, node.Y + 1).CanStandOn();
					if (afloat && airborneTicks < MaxSwimTicks) {
						airborneTicks++;
						// For the nearest place to stand (through the water and the air above it), as a player swims for the
						// nearest shore, not towards the target: seen in game (measured run S0), afloat against a wall on the
						// target's side, every walk swam into it for 1,200 ticks and failed, until auto mode stopped.
						if (swimTo == null || airborneTicks % SwimReplanTicks == 1)
							swimTo = NearestFooting(ctx, node);
						TilePos dest = swimTo ?? Target;
						ctx.Output.Horizontal = dest.X > node.X ? 1 : dest.X < node.X ? -1 : 0;
						ctx.Output.Jump = dest.Y <= node.Y && (airborneTicks / 10) % 2 == 0; // strokes up; none to sink to a lower floor
						State = $"swimming for footing at {dest}";
						// Not getting anywhere: whatever is over the head is mined, by the tile really there (the grid read it open).
						// Seen in game (measured run S2): swimming 4 rows up for footing, held at the same row for 490,000 ticks.
						swimStill = node == swimFrom ? swimStill + 1 : 0;
						swimFrom = node;
						if (swimStill > SwimStillTicks && ctx.World != null) {
							for (int dx = 0; dx <= 1; dx++)
								for (int up = Body.HeightTiles; up <= Body.HeightTiles + 1; up++)
									if (ctx.World.TileTypeAt(node.X + dx, node.Y - up) >= 0) {
										ctx.Output.Tool = ToolRequest.Pickaxe;
										ctx.Output.UseItem = true;
										ctx.Output.Aim(node.X + dx, node.Y - up);
										State = $"swimming, held at {node}: mining ({node.X + dx},{node.Y - up}) over the head";
										return SkillStatus.Running;
									}
						}
						return SkillStatus.Running;
					}
					// Held off the ground and not moving: mine whatever really is in or over the body (the grid may read it open: a
					// half block wedged in, a slope) and lean towards the target. Seen in game (measured run S3): "never landed ...
					// vy -2.01" at (2500,339) walk after walk, and the watchdog's climb-out failed the same way.
					heldStill = node == heldFrom ? heldStill + 1 : 0;
					heldFrom = node;
					if (!afloat && heldStill > HeldStillTicks && ctx.World != null && TileInOrOverBody(ctx) is TilePos wedge) {
						ctx.Output.Tool = ToolRequest.Pickaxe;
						ctx.Output.UseItem = true;
						ctx.Output.Aim(wedge.X, wedge.Y);
						ctx.Output.Horizontal = Target.X > node.X ? 1 : Target.X < node.X ? -1 : 0;
						State = $"held at {node}: mining {wedge}";
						if (heldStill == HeldStillTicks + 1)
							Info(ctx, $"WalkTo: held off the ground at {node} (vy {ctx.Body.VY:0.##}); mining {wedge} (tile {ctx.World.TileTypeAt(wedge.X, wedge.Y)})");
					}
					if (++airborneTicks > (afloat ? MaxSwimTicks : MaxAirborneTicks))
						return Fail(ctx, $"never landed in {airborneTicks} ticks at {node} (vy {ctx.Body.VY:0.###}, feet {ctx.Grid.Get(node.X, node.Y)}/{ctx.Grid.Get(node.X + 1, node.Y)}, under {ctx.Grid.Get(node.X, node.Y + 1)}/{ctx.Grid.Get(node.X + 1, node.Y + 1)})");
					State = "waiting to land";
					return SkillStatus.Running;
				}
				airborneTicks = 0;
				// Inside a block (a pillar step's block, placed as the bot came down beside it): mine it out first, as a player
				// does. Seen in game (measured runs K1 and L2): "feet cells Empty/Solid" after a pillar step; every plan from
				// then on began on top of the block the bot was in, and step 0 timed out 40-odd times at one spot.
				if (unstickTicks < MaxUnstickTicks && ctx.UnstickFailedAt != node && EmbeddedIn(ctx) is TilePos inside) {
					if (unstickTicks++ == 0)
						Info(ctx, $"WalkTo: inside the block at {inside} (tile {ctx.World?.TileTypeAt(inside.X, inside.Y)}, bot at {node}); mining out of it first");
					ctx.Output.Tool = ToolRequest.Pickaxe;
					ctx.Output.UseItem = true;
					ctx.Output.Aim(inside.X, inside.Y);
					State = $"mining out of {inside}";
					return SkillStatus.Running;
				}
				if (EmbeddedIn(ctx) is not TilePos still)
					unstickTicks = 0;
				else if (ctx.UnstickFailedAt != node) {
					// Mined at for MaxUnstickTicks and still "inside" it: the body stands there all the same, so the grid is
					// wrong about it. Seen in game (measured run T0a): the head cell at (2574,588) stayed solid in the grid,
					// and every plan from (2574,590) failed "InvalidStart" for the last 83 heartbeats of the run.
					ctx.UnstickFailedAt = node;
					Info(ctx, $"WalkTo: could not mine out of {still} (tile {ctx.World?.TileTypeAt(still.X, still.Y)}); planning from where the body stands");
				}
				if (!TryResumePlan(ctx, node))
					StartSearch(ctx, node);
			}
			// Stand still while searching; long searches are spread over several ticks.
			if (Executor == null && !search.Step(PlanBudgetMsPerTick)) {
				State = $"planning ({search.Expanded} nodes)";
				return SkillStatus.Running;
			}
			if (Executor == null && !FinishSearch(ctx)) {
				if (ArrivedNearGoal) {
					Info(ctx, $"WalkTo: {Goal} is next to {node} (search started on it); done");
					State = "arrived";
					return SkillStatus.Succeeded;
				}
				return NoPathCount >= MaxNoPathAttempts ? Fail(ctx, $"no path after {NoPathCount} attempts ({LastPlan})") : SkillStatus.Running;
			}
		}

		MoveInput input = Executor.Tick(ctx.Body);
		ctx.PlannedCatch = Executor.Status == ExecStatus.Running ? Executor.CatchCell : null;
		if (Executor.PopNotice() is string notice)
			ctx.Log.Info(notice);
		if (ctx.Trace)
			ctx.Log.Debug($"TRACE t{ctx.Tick} node {node} {ctx.Body} step {Executor.StepIndex} {Executor.Current} [{Executor.Phase}] in {input}");

		switch (Executor.Status) {
			case ExecStatus.Running:
				ctx.Output = input;
				State = Executor.Phase;
				PlanAhead(ctx);
				break;
			case ExecStatus.Finished:
				// Either the goal check above succeeds next tick, or this was a partial path: plan the next leg.
				Info(ctx, $"Path finished at {node} ({LastPlan.Status})");
				TilePos began = Executor.Steps.Count > 0 ? Executor.Steps[0].From : node;
				Executor = null;
				if (Goal.IsGoal(node))
					break;
				if (TrySplice(ctx, node)) {
					ctx.Output = MoveInput.None;
					break;
				}
				// A whole plan done and the goal not met is no arrival: a re-plan, so a walk cannot plan for ever (Baritone: a
				// plan that gets nowhere is a failure). Back where it began, the same search from the same place would give
				// the same plan: fail now. Seen in game (measured run U23): 418 times "Path finished at (2076,356) (Found)",
				// each plan the same two steps from there, 1.5 game hours.
				if (LastPlan.Status == PathStatus.Found) {
					Replans++;
					ctx.Log.Warn($"Re-plan #{Replans}: the plan ended at {node} and {Goal} is not met there");
					if (PathExecutor.SameNode(ctx.Grid, node, began) || PathExecutor.SameNode(ctx.Grid, began, node))
						return Fail(ctx, $"a plan from {began} ended back there, at {node}, without {Goal}");
					if (Replans > MaxReplans)
						return Fail(ctx, $"too many re-plans ({Replans})");
				}
				break;
			default:
				if (Executor.Status == ExecStatus.Blocked && TryRepair(ctx, node))
					break;
				Replans++;
				ctx.Log.Warn($"Re-plan #{Replans}: {Executor.Status}: {Executor.Reason}");
				NoteFailedActions(ctx);
				// The same step stuck again and again: the plan keeps asking for something the world will not do (seen in game:
				// 26 re-plans holding Down on a slope). Give up; the skill above picks something else.
				// Counted per step over the whole walk, not only in a row: seen in game, two steps from the same tile took
				// turns (a walk placing a block, a drop breaking one), each stuck "pressing right", 26 re-plans in all.
				string stuckStep = Executor.Current?.ToString();
				// Kept on the context, across walks: seen in game, three different jumps on the way home each failed once
				// per walk, every walk, and never reached the count.
				if (Executor.Current is PathStep cur)
					sameStuck = ctx.NoteStepFailure(cur.From, cur.To);
				if (sameStuck >= MaxSameStuck) {
					if (Executor.Current is PathStep bad && ctx.BadEdges.Add((bad.From, bad.To)))
						ctx.Log.Info($"WalkTo: not planning {bad.Kind} {bad.From}->{bad.To} for {BotContext.VetoTicks / 3600} game hours; around it (F from, T to, / slope or half block):\n{Around(ctx, bad.From, bad.To)}\n    tile types of the body at T (rows {bad.To.Y - 2}..{bad.To.Y}, both columns): {BodyTileTypes(ctx, bad.To)}");
					Executor = null;
					return Fail(ctx, $"the same step failed {sameStuck} times: {stuckStep}");
				}
				Executor = null;
				if (Replans > MaxReplans)
					return Fail(ctx, $"too many re-plans ({Replans})");
				break;
		}
		return SkillStatus.Running;
	}

	/// <summary>Actions of the step that got stuck which did not happen: twice at a cell, and path moves stop planning them there.</summary>
	private void NoteFailedActions(BotContext ctx)
	{
		// Only the action that was actually being worked on: a step stuck before its actions began (positioning) says
		// nothing about them (seen in game: cells vetoed that were never tried).
		if (Executor.Current is not PathStep step || !step.HasActions)
			return;
		// A pillar's block is placed mid-jump (an arrested drop's mid-fall), outside the action list: a pillar that failed is its
		// block not going in.
		// Seen in game (runs 36-41): beside the work bench at the spawn the block never went in (nothing solid next to
		// the cell to hold it, and no wall: vanilla PlaceThing_Tiles_BlockPlacementForAssortedThings), so every walk home
		// after a death planned the same pillar.
		BlockAction[] tried = step.Kind is MoveKind.Pillar or MoveKind.ArrestedDrop ? step.Actions.Where(a => a.Kind == ActionKind.Place).ToArray()
			: Executor.CurrentAction is BlockAction current ? new[] { current } : System.Array.Empty<BlockAction>();
		foreach (BlockAction a in tried) {
			bool place = a.Kind == ActionKind.Place;
			CellKind cell = ctx.Grid.Get(a.Cell.X, a.Cell.Y);
			if (place ? cell.IsSolid() : !cell.IsBlockTile())
				continue; // it did happen
			var key = (a.Cell, place);
			int n = ctx.ActionFailures.TryGetValue(key, out int k) ? k + 1 : 1;
			ctx.ActionFailures[key] = n;
			if (n >= MaxActionFailures && (place ? ctx.NoPlace.Add(a.Cell) : ctx.NoBreak.Add(a.Cell)))
				ctx.Log.Info($"WalkTo: {(place ? "placing a block at" : "breaking")} {a.Cell} failed {n} times; planning round it from now on");
		}
	}

	private bool dumped;
	/// <summary>Plan repairs this walk (each a small local search): capped so a walk cannot repair for ever.</summary>
	public int Repairs { get; private set; }
	public const int MaxRepairs = 20, RepairSkip = 3, RepairExpansions = 3000;

	/// <summary>
	/// A step ahead became invalid (the world changed, or the plan stood on a block it dug away): instead of a whole new
	/// search (2.6 s on a 60-tile walk home), find a short way to where the plan carries on a few steps later and keep
	/// the rest. Seen in game: the same long walk re-planned from scratch every few steps.
	/// </summary>
	private bool TryRepair(BotContext ctx, TilePos node)
	{
		if (Executor.InvalidStepIndex is not int bad || Repairs >= MaxRepairs || !ctx.Body.Grounded || !Body.IsStartable(ctx.Grid, node))
			return false;
		System.Collections.Generic.IReadOnlyList<PathStep> old = Executor.Steps;
		int join = System.Math.Min(bad + RepairSkip, old.Count - 1);
		TilePos rejoin = old[join].To;
		var search = new PathSearch(ctx.Moves, node, new GoalNode(rejoin, ctx.Physics), new SearchLimits { MaxExpansions = RepairExpansions });
		search.Step(double.MaxValue);
		if (search.Result.Status != PathStatus.Found || search.Result.Steps.Count == 0)
			return false;
		var steps = new System.Collections.Generic.List<PathStep>(search.Result.Steps);
		for (int i = join + 1; i < old.Count; i++)
			steps.Add(old[i]);
		Repairs++;
		Info(ctx, $"WalkTo: step {bad} no longer valid; repaired with {search.Result.Steps.Count} step(s) to rejoin at {rejoin} ({search.Result.Expanded} nodes), {steps.Count} steps left");
		Executor = new PathExecutor(ctx.Moves, steps);
		searchMoves = ctx.Moves;
		return true;
	}

	/// <summary>
	/// A big search that found no path: write the region between start and goal (legend as <see cref="Around"/>, then the
	/// "bad" steps, then a "walls" map) to a file, so the case can be replayed in a unit test. Seen in game: the same cave, 57 tiles below spawn, never had a
	/// way back in three runs.
	/// </summary>
	private void DumpRegion(BotContext ctx, TilePos start)
	{
		dumped = true;
		if (ctx.DumpDir == null)
			return;
		try {
			TilePos goal = Goal.Anchor;
			int x0 = System.Math.Min(start.X, goal.X) - 12, x1 = System.Math.Max(start.X, goal.X) + 12;
			int y0 = System.Math.Min(start.Y, goal.Y) - 12, y1 = System.Math.Max(start.Y, goal.Y) + 8;
			string map = Around(ctx, start, goal, x0, x1, y0, y1);
			var bad = new System.Text.StringBuilder();
			foreach (var (from, to) in ctx.BadEdges)
				if (from.X >= x0 && from.X <= x1 && from.Y >= y0 && from.Y <= y1)
					bad.Append($"bad {from.X - x0} {from.Y - y0} {to.X - x0} {to.Y - y0}").Append(System.Environment.NewLine);
			map += System.Environment.NewLine + bad;
			// The walls behind the cells ('w'), what a block placed into open air is fixed to (an arrested drop's catch): the U30
			// dumps of the corruption chasm had none, and the wall behind its shaft had to be inferred for the replay.
			if (ctx.World != null) {
				var walls = new System.Text.StringBuilder("walls");
				for (int y = y0; y <= y1; y++) {
					walls.Append(System.Environment.NewLine).Append("    ");
					for (int x = x0; x <= x1; x++)
						walls.Append(ctx.World.HasWall(x, y) ? 'w' : '.');
				}
				map += walls + System.Environment.NewLine;
			}
			string file = System.IO.Path.Combine(ctx.DumpDir, $"nopath_{start.X}_{start.Y}_to_{goal.X}_{goal.Y}.txt");
			System.IO.File.WriteAllText(file, $"start {start} goal {Goal} origin ({x0},{y0}) blocks {ctx.Blocks?.BlockCount ?? 0} pick {ctx.PickPower}" + System.Environment.NewLine + map + System.Environment.NewLine);
			ctx.Log.Info($"WalkTo: no path from {start} to {Goal}; region written to {file}");
		}
		catch (System.Exception e) {
			ctx.Log.Warn("WalkTo: region dump failed: " + e.Message);
		}
	}

	/// <summary>
	/// A small map of the cells round a failed step, for the log. Solid cells the path may not break are 'X' (too hard
	/// for the pickaxe, under furniture, vetoed, protected) and 'U' (unsafe: liquid or a falling block next to it).
	/// </summary>
	/// <summary>
	/// The tile types where the body would be at <paramref name="at"/> (-1 for none): a "pressing left without moving"
	/// against cells the grid reads open (world_eye run 62) needs to know what is there.
	/// </summary>
	private static string BodyTileTypes(BotContext ctx, TilePos at)
	{
		if (ctx.World == null)
			return "?";
		var parts = new System.Collections.Generic.List<string>();
		for (int y = at.Y - 2; y <= at.Y; y++)
			parts.Add($"{ctx.World.TileTypeAt(at.X, y)}/{ctx.World.TileTypeAt(at.X + 1, y)}");
		return string.Join(" ", parts);
	}

	internal static string Around(BotContext ctx, TilePos from, TilePos to) =>
		Around(ctx, from, to, System.Math.Min(from.X, to.X) - 3, System.Math.Max(from.X, to.X) + 4,
			System.Math.Min(from.Y, to.Y) - 4, System.Math.Max(from.Y, to.Y) + 2);

	internal static string Around(BotContext ctx, TilePos from, TilePos to, int x0, int x1, int y0, int y1)
	{
		var sb = new System.Text.StringBuilder();
		MoveContext moves = ctx.Moves;
		char Cell(int x, int y)
		{
			CellKind k = ctx.Grid.Get(x, y);
			if (moves.Blocks != null && k.ToChar() == '.' && !moves.Blocks.CanPlaceAt(x, y))
				return ','; // open, but no block can go here (vetoed, reserved, nothing to attach to...)
			if (!k.IsSolid() || moves.Blocks == null)
				return k.ToChar();
			if (BlockRules.Unsafe(ctx.Grid, new TilePos(x, y), System.Array.Empty<TilePos>()))
				return 'U';
			return float.IsInfinity(moves.Blocks.BreakTicks(x, y)) ? 'X' : k.ToChar();
		}
		for (int y = y0; y <= y1; y++) {
			sb.Append("    ");
			for (int x = x0; x <= x1; x++)
				sb.Append(x == from.X && y == from.Y ? 'F' : x == to.X && y == to.Y ? 'T' : Cell(x, y));
			if (y < y1)
				sb.Append('\n');
		}
		return sb.ToString();
	}

	private int sameStuck;
	/// <summary>The identical step failing this many re-plans in a row ends the walk.</summary>
	public const int MaxSameStuck = 3;

	/// <summary>Failures of the same action at a cell before path moves stop planning it.</summary>
	public const int MaxActionFailures = 2;

	private void StartSearch(BotContext ctx, TilePos node)
	{
		MoveContext moves = ctx.Moves;
		TilePos start = node;
		// Standing (grounded, clear of blocks): the plan starts where the body is, whatever the grid makes of its footing,
		// so the executor's "at the start" check can hold. Otherwise the nearest node the grid can start from.
		// Grounded and in no solid block the pickaxe could clear (EmbeddedIn): the body fits there whatever the grid makes of
		// it (a slope or half block sharing its rows). Seen in game (measured run U1): grounded at (1902,480), the grid's body
		// check failed on a shaped cell, no node near was startable, and one explore leg failed "InvalidStart" 33 times.
		bool standing = ctx.Body.Grounded && (Body.IsClear(moves.Grid, node) || ctx.UnstickFailedAt == node || EmbeddedIn(ctx) == null)
			&& !Body.IsStartable(moves.Grid, node);
		if (!standing)
			foreach (TilePos d in StartCandidates) {
				if (Body.IsStartable(moves.Grid, node + d)) {
					start = node + d;
					break;
				}
			}
		ArrivedNearGoal = false;
		plans++;
		searchMoves = moves;
		SearchLimits limits = Limits ?? SearchLimits.Default;
		if (relaxed)
			limits = new SearchLimits { MaxExpansions = limits.MaxExpansions, MaxRadius = limits.MaxRadius, CheckBrokenSupport = false };
		// To where the body, read where it stands, meets the goal (GoalAsStood), as Tick's arrival check reads it.
		search = new PathSearch(moves, start, new GoalAsStood(Goal, moves.Grid), limits, trustStart: standing);
		lastTrustStart = standing;
		State = "planning";
	}

	private bool lastTrustStart, deepSearched;

	/// <summary>
	/// The deep search after an early "no node closer than the start": no segments (so no such end either), up to the default
	/// search's own cap (<see cref="SearchLimits.MaxExpansions"/>); a partial plan to the best node if the goal is not met in it.
	/// </summary>
	public static SearchLimits DeepLimits(bool relaxed) => new() {
		MaxExpansions = SearchLimits.Default.MaxExpansions, MaxRadius = SearchLimits.Default.MaxRadius, SegmentAfter = 0, CheckBrokenSupport = !relaxed,
	};

	/// <summary>
	/// Steps of a plan written to the log: the first <see cref="LoggedSteps"/> and the last <see cref="LoggedTailSteps"/>, the
	/// middle left out to keep the line readable. The end matters too: measured run U22 died on step 61 of 61.
	/// </summary>
	public const int LoggedSteps = 40, LoggedTailSteps = 8;

	internal static string StepList(IReadOnlyList<PathStep> steps)
	{
		if (steps.Count <= LoggedSteps + LoggedTailSteps)
			return string.Join(" | ", steps.Select(s => s.ToString()));
		return string.Join(" | ", steps.Take(LoggedSteps).Select(s => s.ToString()))
			+ $" | ... {steps.Count - LoggedSteps - LoggedTailSteps} more ... | "
			+ string.Join(" | ", steps.Skip(steps.Count - LoggedTailSteps).Select(s => s.ToString()));
	}

	private bool FinishSearch(BotContext ctx)
	{
		LastPlan = search.Result;
		search = null;
		string steps = StepList(LastPlan.Steps);
		int breaks = LastPlan.Steps.Sum(s => s.CountActions(ActionKind.Break));
		int places = LastPlan.Steps.Sum(s => s.CountActions(ActionKind.Place));
		string world = breaks + places > 0 ? $", breaks {breaks}, places {places}" : "";
		Info(ctx, $"Plan #{plans} {LastPlan.Start} -> {Goal}: {LastPlan}{world}{(steps.Length > 0 ? " :: " + steps : "")}");

		if (LastPlan.Status is PathStatus.Found or PathStatus.Partial && LastPlan.Steps.Count > 0) {
			Executor = new PathExecutor(searchMoves, LastPlan.Steps);
			State = "executing";
			return true;
		}

		if (LastPlan.Status == PathStatus.Found && LastPlan.Steps.Count == 0) {
			// The search started on a goal node: the start candidate next to us (we are not standing exactly on a node,
			// or not grounded yet). That is "there", not "no path".
			ArrivedNearGoal = true;
			return false;
		}
		// The broken-support check rules out steps onto blocks the same path digs away; with no blocks to place, a way out
		// of a cave can need them (they only cost a re-plan when reached). Seen in game (world_eye run 38): three 40,000-node
		// searches from the cave below the spawn found nothing; the replay finds 109 steps without the check.
		if (LastPlan.Status == PathStatus.NoPath && !relaxed && (Limits ?? SearchLimits.Default).CheckBrokenSupport) {
			relaxed = true;
			Info(ctx, "WalkTo: no path that keeps off blocks it digs away; planning again allowing them (re-planning where they fail)");
			waitTicks = 0;
			return false;
		}
		// No move at all from where the bot stands, with steps from here banned earlier: the bans are forgiven and it plans
		// again (a ban is a guess about a step, not a fact for ever: Baritone keeps none). Seen in game (measured run G1):
		// "no path ... expanded 1" from the night's walk down, five rests in a row, and auto mode stopped.
		if (LastPlan.Status == PathStatus.NoPath && LastPlan.Expanded <= 2 && !forgaveHere && ForgiveBansAround(ctx, LastPlan.Start) is int forgiven and > 0) {
			forgaveHere = true;
			Info(ctx, $"WalkTo: boxed in at {LastPlan.Start} by {forgiven} step(s) banned earlier; forgiving them and planning again");
			waitTicks = 0;
			return false;
		}
		if (LastPlan.Status == PathStatus.NoPath && LastPlan.Why.Length > 0)
			Info(ctx, $"WalkTo: no path from {LastPlan.Start}: {LastPlan.Why}");
		// Ended early, "no node closer than the start": the start may be a dead end the way on first leads away from (a
		// staircase a partial segment dug up into, under rock the pickaxe cannot break), not a walled-off goal. Search once
		// more without that end, up to a whole search's nodes. Seen in game (measured runs M10j, M10k, world_eow): from (1872,366),
		// the top of such a staircase, every walk ended "no node closer than the start in 6000 (rows 366..496)" for 12 game
		// hours, while a tunnel east through mud led out (the region dump's replay: 91 steps). Once per start for a while
		// (DeepSearchedFrom), and not for sub-skills with their own limits (they have alternatives).
		if (LastPlan.Status == PathStatus.NoPath && LastPlan.EndedNoCloser && Limits == null && !deepSearched && !ctx.DeepSearchedFrom.Contains(LastPlan.Start)) {
			deepSearched = true;
			ctx.DeepSearchedFrom.Add(LastPlan.Start);
			SearchLimits deep = DeepLimits(relaxed);
			Info(ctx, $"WalkTo: searching once more from {LastPlan.Start} without ending at a dead end (up to {deep.MaxExpansions} nodes)");
			plans++;
			searchMoves = ctx.Moves;
			search = new PathSearch(searchMoves, LastPlan.Start, new GoalAsStood(Goal, searchMoves.Grid), deep, trustStart: lastTrustStart);
			State = "planning (deep)";
			return false;
		}
		// A big search from here found nothing: another the same would too (seen in game, measured run J2: 31 searches of
		// 40,000 nodes, 5 s each, from one cave). Fail now and let the job choose something else.
		if (LastPlan.Status == PathStatus.NoPath && LastPlan.Expanded >= BigNoPath) {
			NoPathCount = MaxNoPathAttempts;
			return false;
		}
		NoPathCount++;
		// Big searches that found nothing, and tiny ones (boxed in: run 55 sat in a 2-node pocket with nothing to show why).
		if (LastPlan.Status == PathStatus.NoPath && (LastPlan.Expanded > 1000 || LastPlan.Expanded <= 20) && !dumped)
			DumpRegion(ctx, LastPlan.Start);
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
