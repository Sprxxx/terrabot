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
	public int NoPathCount { get; private set; }
	/// <summary>The last search started on a goal node next to the body (a zero-step Found).</summary>
	public bool ArrivedNearGoal { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";

	public string Name => "WalkTo";
	public string Key => $"WalkTo {(fixedGoal != null ? fixedGoal.ToString() : Target.ToString())}{(Travel ? " travel" : "")}{(ToSurface ? " to surface" : "")}";

	/// <summary>Used as a sub-skill (e.g. chasing a moving enemy): plan/finish lines go to Debug instead of Info.</summary>
	public bool Quiet { get; init; }
	/// <summary>A trip across the surface to new ground (travel for ore): surface work, so not at night without armour.</summary>
	public bool Travel { get; init; }
	/// <summary>A walk up to the surface (the start of a trip): surface work at night too, wherever it starts from.</summary>
	public bool ToSurface { get; init; }

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
			if (!FinishSearch(ctx)) {
				if (ArrivedNearGoal) {
					Info(ctx, $"WalkTo: {Goal} is next to {node} (search started on it); done");
					State = "arrived";
					return SkillStatus.Succeeded;
				}
				return NoPathCount >= MaxNoPathAttempts ? Fail(ctx, $"no path after {NoPathCount} attempts ({LastPlan})") : SkillStatus.Running;
			}
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
				Info(ctx, $"Path finished at {node} ({LastPlan.Status})");
				Executor = null;
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
					sameStuck = ctx.StepFailures[(cur.From, cur.To)] = (ctx.StepFailures.TryGetValue((cur.From, cur.To), out int f) ? f : 0) + 1;
				if (sameStuck >= MaxSameStuck) {
					if (Executor.Current is PathStep bad && ctx.BadEdges.Add((bad.From, bad.To)))
						ctx.Log.Info($"WalkTo: never planning {bad.Kind} {bad.From}->{bad.To} again; around it (F from, T to, / slope or half block):\n{Around(ctx, bad.From, bad.To)}\n    tile types of the body at T (rows {bad.To.Y - 2}..{bad.To.Y}, both columns): {BodyTileTypes(ctx, bad.To)}");
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
		// A pillar's block is placed mid-jump, outside the action list: a pillar that failed is its block not going in.
		// Seen in game (runs 36-41): beside the work bench at the spawn the block never went in (nothing solid next to
		// the cell to hold it, and no wall: vanilla PlaceThing_Tiles_BlockPlacementForAssortedThings), so every walk home
		// after a death planned the same pillar.
		BlockAction[] tried = step.Kind == MoveKind.Pillar ? step.Actions.Where(a => a.Kind == ActionKind.Place).ToArray()
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
	/// A big search that found no path: write the region between start and goal (legend as <see cref="Around"/>) to a
	/// file, so the case can be replayed in a unit test. Seen in game: the same cave, 57 tiles below spawn, never had a
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
		search = new PathSearch(moves, start, Goal, limits);
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
