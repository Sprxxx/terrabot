using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Execution;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Walled in by blocks the pickaxe cannot break but a blast can (ebonstone under a Silver Pickaxe: power 45, ebonstone needs
/// 65, while vanilla Projectile.CanExplodeTile lets explosives break it): throw a carried Bomb or Dynamite at the wall that
/// opens the way, walk out of the blast's reach before the fuse runs out, wait for the blast, and leave the walk to be planned
/// again. Seen in game (measured runs M10j and M10k, world_eow): after the Worm Food was crafted, every walk from (1872,366)
/// ended "no path" for the last 12 game hours of the run, under an ebonstone ceiling, with 7 bombs and 2 dynamite in the bag.
/// <para/>
/// Which wall: of the walls round the cells the bot can walk to (no digging), those the pickaxe cannot break and a blast can,
/// nearest the goal first; for each, the blast is played on a copy of the grid and the walk planned through it. The first
/// whose walk then uses a cell the blast cleared, from a throw spot within the throw's reach and with a way out of the blast
/// in time, is the one. Everything else is a fact from the game: the blast's radius and box, the fuse, the throw speed.
/// </summary>
public sealed class BlastOutSkill : ISkill
{
	/// <summary>
	/// Nodes of the walkable pocket looked at (no digging): one plan segment's worth (<see cref="SearchLimits.SegmentAfter"/>),
	/// the planner's own measure of "a stretch of ground near here".
	/// </summary>
	public static int PocketNodes => SearchLimits.Default.SegmentAfter;
	/// <summary>
	/// Walls tried at most. Each costs a planned walk (up to two segments' worth of nodes); a wall within the smallest blast's
	/// radius of one already tried is covered by that one's blast and not tried on its own.
	/// </summary>
	public const int MaxCandidates = 8;
	/// <summary>
	/// The explosive flies straight for this many ticks, then falls at <see cref="ThrowGravity"/> px/tick² (Projectile.AI_016:
	/// "ai[0] > 5f" before "velocity.Y += 0.2f").
	/// </summary>
	public const int StraightTicks = ExplosiveFlight.StraightTicks;
	public const float ThrowGravity = ExplosiveFlight.Gravity;
	/// <summary>Ticks after the fuse's end that the blast is waited for, before the throw counts as a dud (the fuse is the wiki's).</summary>
	public const int BlastGraceTicks = 60;
	/// <summary>Ticks of holding use for the throw before the game is taken to refuse it (several swings of the slowest, Dynamite's 40).</summary>
	public const int ThrowWaitTicks = 120;
	/// <summary>
	/// Close enough to the throw spot's planned place (pixels), and the ticks given to step there: half a tile at the run's
	/// 0.08 px/tick² takes about 14 ticks from standing (PhysicsParams.RunAcceleration), twice that allowed.
	/// </summary>
	public const float NudgePx = 2f;
	public const int MaxNudgeTicks = 30;

	public IGoal Goal { get; }
	public string Name => "BlastOut";
	public string Key => $"BlastOut {Goal}";
	public string FailureReason { get; private set; } = "";
	public string State { get; private set; } = "new";

	/// <summary>The chosen blast (after planning).</summary>
	public BlastPlan Plan { get; private set; }

	private enum Phase { Planning, ToStance, Throwing, Retreating, Waiting }
	private Phase phase;
	private List<Candidate> candidates;
	private int nextCandidate, nextExplosive;
	private PathSearch way;
	private OverlayGrid wayGrid;
	private BlastPlan pending;
	private readonly List<string> rejected = new();
	private WalkToSkill walk;
	private PathExecutor retreat;
	private int countBefore, throwTicks, nudgeTicks;
	private long thrownAt;
	private bool retreatReplanned;
	/// <summary>Squared distance to the goal of the open cell nearest it that the bot can walk to (FindCandidates).</summary>
	private float pocketNearest = float.MaxValue;
	/// <summary>The first blast that only gets nearer the goal, kept in case none opens the way.</summary>
	private BlastPlan fallback;
	private int blasts;

	public BlastOutSkill(IGoal goal) => Goal = goal;

	/// <summary>A wall cell to blast and the open cell in front of it where the explosive lands.</summary>
	private readonly record struct Candidate(TilePos Wall, TilePos Impact);

	/// <summary>A blast worth throwing: what, where from, and the way out of its reach.</summary>
	public sealed class BlastPlan
	{
		public ExplosiveInfo Explosive { get; init; }
		public TilePos Wall { get; init; }
		/// <summary>The cell the explosive comes to rest in (where the blast is), and the mouse point that puts it there.</summary>
		public TilePos Impact { get; init; }
		public float AimX { get; set; }
		public float AimY { get; set; }
		/// <summary>The open cell in front of the wall the throw was planned at.</summary>
		public TilePos Front { get; init; }
		public TilePos Stance { get; init; }
		public int Cleared { get; init; }
		public PathResult Way { get; init; }
		public GoalClearOfBlast Clear { get; set; }
		public PathResult Retreat { get; set; }
		/// <summary>Squared distance (tiles) to the goal's anchor of the cleared cell nearest it.</summary>
		public float NearestCleared { get; init; }
		/// <summary>No way past this blast yet, but it opens ground nearer the goal than any the bot can walk to now: a first blast.</summary>
		public bool OnlyNearer { get; init; }
	}

	public string Describe() => $"BlastOut [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	/// <summary>The explosives carried that break tiles, smallest blast first.</summary>
	public static IReadOnlyList<ExplosiveInfo> Carried(BotContext ctx) =>
		ctx.Ids.TileExplosives.Where(e => ctx.Inventory != null && ctx.Inventory.Count(e.ItemType) > 0).ToList();

	public void Start(BotContext ctx)
	{
		string bag = string.Join(", ", Carried(ctx).Select(e => $"{e.Name} x{ctx.Inventory.Count(e.ItemType)}"));
		ctx.Log.Info($"BlastOut start: walks to {Goal} from {ctx.Node} find no way; carrying {bag}; looking for a wall the pickaxe cannot break and a blast can");
		phase = Phase.Planning;
		State = "planning";
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		ctx.Output = MoveInput.None;
	}

	public void Pause(BotContext ctx)
	{
		walk?.Pause(ctx);
		retreat = null; // off the plan after a fight: planned again from where the bot is
		ctx.Output = MoveInput.None;
	}

	public void Resume(BotContext ctx) => walk?.Resume(ctx);

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null && phase is not (Phase.Retreating or Phase.Waiting)) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		switch (phase) {
			case Phase.Planning:
				return PlanTick(ctx);
			case Phase.ToStance:
				return StanceTick(ctx);
			case Phase.Throwing:
				return ThrowTick(ctx);
			case Phase.Retreating:
				return RetreatTick(ctx);
			default:
				return WaitTick(ctx);
		}
	}

	// ---------- planning ----------

	private SkillStatus PlanTick(BotContext ctx)
	{
		IReadOnlyList<ExplosiveInfo> carried = Carried(ctx);
		if (carried.Count == 0)
			return Fail(ctx, "no explosive that breaks tiles is carried");
		if (ctx.World == null)
			return Fail(ctx, "no world view");
		if (!ctx.AllowBreak)
			return Fail(ctx, "digging is off (a blast changes the world too)");
		candidates ??= FindCandidates(ctx, carried.Min(e => e.TileRadius));
		if (candidates.Count == 0)
			return Fail(ctx, "no wall round the walkable ground here that the pickaxe cannot break and a blast can");
		// A pending walk through the blast: a few ms a tick, as WalkToSkill searches.
		if (way != null) {
			if (!way.Step(WalkToSkill.PlanBudgetMsPerTick)) {
				State = $"planning: the way past a blast at {pending.Wall} ({way.Expanded} nodes)";
				return SkillStatus.Running;
			}
			PathResult r = way.Result;
			way = null;
			if (r.Status is PathStatus.Found or PathStatus.Partial && r.Steps.Count > 0 && UsesCleared(r, wayGrid)) {
				var chosen = new BlastPlan {
					Explosive = pending.Explosive, Wall = pending.Wall, Impact = pending.Impact, AimX = pending.AimX, AimY = pending.AimY, Front = pending.Front, Stance = pending.Stance,
					Cleared = pending.Cleared, Way = r, Clear = pending.Clear, Retreat = pending.Retreat,
				};
				return Choose(ctx, chosen);
			}
			rejected.Add($"{pending.Explosive.Name} at {pending.Wall}: the walk past it is {r.Status}{(r.Status is PathStatus.Found or PathStatus.Partial ? " without the blast's cells" : "")}");
			if (fallback == null && pending.NearestCleared < pocketNearest)
				fallback = new BlastPlan {
					Explosive = pending.Explosive, Wall = pending.Wall, Impact = pending.Impact, AimX = pending.AimX, AimY = pending.AimY, Front = pending.Front, Stance = pending.Stance,
					Cleared = pending.Cleared, Way = r, Clear = pending.Clear, Retreat = pending.Retreat, NearestCleared = pending.NearestCleared, OnlyNearer = true,
				};
			pending = null;
			Advance(carried.Count);
		}
		while (nextCandidate < candidates.Count) {
			Candidate c = candidates[nextCandidate];
			ExplosiveInfo e = carried[nextExplosive];
			string why = TryStart(ctx, c, e);
			if (why == null) {
				State = $"planning: {e.Name} at {c.Wall}";
				return SkillStatus.Running; // the way is searched from next tick on
			}
			rejected.Add($"{e.Name} at {c.Wall}: {why}");
			Advance(carried.Count);
		}
		string all = string.Join("; ", rejected);
		// A wall too thick for one blast: the one that opens ground nearest the goal first, then planned again from there (as a
		// player blasts on through), while there is something to throw.
		if (fallback != null) {
			ctx.Log.Info($"BlastOut: no one blast opens the way ({all}); {fallback.Explosive.Name} at {fallback.Wall} opens ground "
				+ $"{System.MathF.Sqrt(pocketNearest) - System.MathF.Sqrt(fallback.NearestCleared):0.#} tiles nearer the goal; blasting on from there");
			rejected.Clear();
			return Choose(ctx, fallback);
		}
		return Fail(ctx, $"no blast opens a way ({candidates.Count} wall(s) tried: {all})");
	}

	private void Advance(int explosives)
	{
		if (++nextExplosive >= explosives) {
			nextExplosive = 0;
			nextCandidate++;
		}
	}

	/// <summary>
	/// Walls of the pocket the bot can walk round in (no digging, no placing): solid cells beside the body cells of the nodes
	/// reached, that the pickaxe cannot break, that the bot has seen, and that a blast breaks; nearest the goal first.
	/// </summary>
	private List<Candidate> FindCandidates(BotContext ctx, int spacing)
	{
		MoveContext walking = ctx.Moves.WalkingOnly();
		var open = new HashSet<TilePos>();
		var flood = new PathSearch(walking, ctx.Node, new GoalWhere(n => {
			for (int dx = 0; dx <= 1; dx++)
				for (int dy = -Body.HeightTiles + 1; dy <= 0; dy++)
					open.Add(new TilePos(n.X + dx, n.Y + dy));
			return false;
		}, ctx.Node, "pocket"), new SearchLimits { MaxExpansions = PocketNodes, SegmentAfter = 0 }, trustStart: ctx.Body.Grounded);
		flood.Step(double.MaxValue);
		var walls = new Dictionary<TilePos, TilePos>(); // wall -> impact (the open cell in front of it nearest the goal)
		pocketNearest = open.Count > 0 ? open.Min(DistanceToGoal) : float.MaxValue;
		foreach (TilePos o in open)
			foreach (TilePos d in new[] { new TilePos(0, -1), new TilePos(0, 1), new TilePos(-1, 0), new TilePos(1, 0) }) {
				var w = new TilePos(o.X + d.X, o.Y + d.Y);
				if (open.Contains(w) || !ctx.Grid.Get(w.X, w.Y).IsSolid() || !Unbreakable(ctx, w) || !ctx.World.IsRevealed(w.X, w.Y) || !ctx.World.CanExplode(w.X, w.Y))
					continue;
				if (!walls.TryGetValue(w, out TilePos had) || DistanceToGoal(o) < DistanceToGoal(had))
					walls[w] = o;
			}
		var picked = new List<Candidate>();
		foreach (var kv in walls.OrderBy(kv => DistanceToGoal(kv.Key)).ThenBy(kv => kv.Key.ChebyshevTo(ctx.Node))) {
			if (picked.Any(p => p.Wall.ChebyshevTo(kv.Key) < spacing))
				continue;
			picked.Add(new Candidate(kv.Key, kv.Value));
			if (picked.Count >= MaxCandidates)
				break;
		}
		ctx.Log.Info($"BlastOut: {open.Count} open cells walkable from {ctx.Node} ({flood.Expanded} nodes), {walls.Count} wall cell(s) the pickaxe cannot break and a blast can; "
			+ $"trying {picked.Count} nearest the goal, {spacing} apart (the smallest blast's radius): {string.Join(" ", picked.Select(p => p.Wall))}");
		return picked;
	}

	private float DistanceToGoal(TilePos c)
	{
		float dx = c.X - Goal.Anchor.X, dy = c.Y - Goal.Anchor.Y;
		return dx * dx + dy * dy;
	}

	private static bool Unbreakable(BotContext ctx, TilePos c) =>
		ctx.Moves.Blocks == null || float.IsInfinity(ctx.Moves.Blocks.BreakTicks(c.X, c.Y));

	/// <summary>
	/// Sets up the throw of <paramref name="e"/> at <paramref name="c"/>: a throw spot and aim whose explosive comes to rest where
	/// its blast breaks the wall (<see cref="ExplosiveFlight"/>), the blast on a copy of the grid, the way out of its reach, and
	/// starts the walk's search past it. Null when started, else why not.
	/// </summary>
	private string TryStart(BotContext ctx, Candidate c, ExplosiveInfo e)
	{
		PhysicsParams p = ctx.Physics;
		float wx = c.Wall.X * Body.TileSize + Body.TileSize / 2f, wy = c.Wall.Y * Body.TileSize + Body.TileSize / 2f;
		float reach = ThrowReachPx(e);
		// Every walkable node within reach, cheapest first; the throw that comes to rest nearest the wall wins (the cheaper spot on a
		// tie): a throw at a wall bounces back off it and rolls (ExplosiveFlight), so where it is thrown from matters.
		TilePos? stanceAt = null;
		(float AimX, float AimY, float RestX, float RestY) chosen = default;
		float best = float.MaxValue;
		MoveContext walking = ctx.Moves.WalkingOnly();
		var flood = new PathSearch(walking, ctx.Node, new GoalWhere(n => {
			float cx = Body.CanonicalLeft(n.X, p) + p.Width / 2f, cy = Body.StandingTop(n.Y, p) + p.Height / 2f;
			if ((cx - wx) * (cx - wx) + (cy - wy) * (cy - wy) > reach * reach)
				return false;
			foreach (var (ax, ay) in Aims(c, cx, cy)) {
				var (rx, ry) = ExplosiveFlight.Rest(ctx.Grid, cx, cy, ax, ay, e, e.FuseTicks);
				float d = (rx - wx) * (rx - wx) + (ry - wy) * (ry - wy);
				if (d < best && ExplosiveFlight.InBlast(rx, ry, e, c.Wall.X, c.Wall.Y)) {
					best = d;
					stanceAt = n;
					chosen = (ax, ay, rx, ry);
				}
			}
			return false;
		}, c.Wall, "throw spots"), new SearchLimits { MaxExpansions = PocketNodes, SegmentAfter = 0 }, trustStart: ctx.Body.Grounded);
		flood.Step(double.MaxValue);
		if (stanceAt is not TilePos stance)
			return $"no spot within {reach / Body.TileSize:0.#} tiles to throw from so that it comes to rest within {e.TileRadius} of the wall";
		var (aimX, aimY, restX, restY) = chosen;

		var clear = new GoalClearOfBlast(restX, restY, e.BlastHalfPx, p);
		PathResult away = RetreatFrom(ctx, stance, clear);
		int allowed = e.FuseTicks - e.UseTicks;
		if (away.Status != PathStatus.Found)
			return $"no way out of the blast's {e.BlastHalfPx:0} px box from {stance} without digging ({e.Damage} damage to stay)";
		if (away.Cost > allowed)
			return $"the way out of the blast takes {away.Cost:0} ticks, more than the {e.FuseTicks}-tick fuse less the {e.UseTicks}-tick throw";

		OverlayGrid grid = BlastGrid(ctx, e, restX, restY, out int cleared, out float nearest);
		var rest = new TilePos((int)System.MathF.Floor(restX / Body.TileSize), (int)System.MathF.Floor(restY / Body.TileSize));
		pending = new BlastPlan {
			Explosive = e, Wall = c.Wall, Impact = rest, AimX = aimX, AimY = aimY, Front = c.Impact, Stance = stance, Cleared = cleared, Clear = clear, Retreat = away,
			NearestCleared = nearest,
		};
		wayGrid = grid;
		// From where the bot will stand when it goes off: out of the blast's box, at the end of the way out.
		TilePos after = away.Steps.Count > 0 ? away.Steps[^1].To : stance;
		way = new PathSearch(ctx.Moves.WithGrid(grid), after, Goal, SearchLimits.Default, trustStart: after == ctx.Node && ctx.Body.Grounded);
		return null;
	}

	/// <summary>The grid as the blast at (restX, restY) leaves it: every solid cell in reach that a blast breaks, cleared.</summary>
	private OverlayGrid BlastGrid(BotContext ctx, ExplosiveInfo e, float restX, float restY, out int cleared, out float nearest)
	{
		var grid = new OverlayGrid();
		grid.Reset(ctx.Grid);
		cleared = 0;
		nearest = float.MaxValue;
		int r = e.TileRadius + 1, bx = (int)System.MathF.Floor(restX / Body.TileSize), by = (int)System.MathF.Floor(restY / Body.TileSize);
		for (int x = bx - r; x <= bx + r; x++)
			for (int y = by - r; y <= by + r; y++)
				if (ExplosiveFlight.InBlast(restX, restY, e, x, y) && ctx.Grid.Get(x, y).IsSolid() && ctx.World.CanExplode(x, y)) {
					grid.Set(x, y, CellKind.Empty);
					cleared++;
					nearest = System.Math.Min(nearest, DistanceToGoal(new TilePos(x, y)));
				}
		return grid;
	}

	/// <summary>
	/// Where to point the mouse (pixels): straight down at the feet and straight up (it comes to rest where the bot stands, no
	/// roll: a throw at a wall bounces back off it and rolls away, Projectile.Update's 0.4), the wall cell itself, the open cell
	/// in front of it, and the wall's 8 neighbours.
	/// </summary>
	private static IEnumerable<(float X, float Y)> Aims(Candidate c, float cx, float cy)
	{
		const float T = Body.TileSize;
		yield return (cx, cy + 3 * T);
		yield return (cx, cy - 3 * T);
		yield return (c.Wall.X * T + T / 2f, c.Wall.Y * T + T / 2f);
		yield return (c.Impact.X * T + T / 2f, c.Impact.Y * T + T / 2f);
		for (int dx = -1; dx <= 1; dx++)
			for (int dy = -1; dy <= 1; dy++)
				if (dx != 0 || dy != 0)
					yield return ((c.Wall.X + dx) * T + T / 2f, (c.Wall.Y + dy) * T + T / 2f);
	}

	/// <summary>
	/// How far the throw carries at least (pixels, from the body's centre): <see cref="StraightTicks"/> at the item's shoot speed,
	/// then the climb a throw straight up still has (v²/2g). The least of any direction: across, it carries much farther. Throw
	/// spots are looked for this near the wall.
	/// </summary>
	public static float ThrowReachPx(ExplosiveInfo e) => e.ShootSpeed * StraightTicks + e.ShootSpeed * e.ShootSpeed / (2f * ThrowGravity);

	/// <summary>The cheapest walk (no digging) from <paramref name="from"/> to where the blast's box misses the body.</summary>
	private static PathResult RetreatFrom(BotContext ctx, TilePos from, GoalClearOfBlast clear)
	{
		var s = new PathSearch(ctx.Moves.WalkingOnly(), from, clear, new SearchLimits { MaxExpansions = PocketNodes, SegmentAfter = 0 }, trustStart: from == ctx.Node && ctx.Body.Grounded);
		s.Step(double.MaxValue);
		return s.Result;
	}

	/// <summary>Does the walk enter (or break) a cell the blast cleared?</summary>
	private static bool UsesCleared(PathResult r, OverlayGrid blasted)
	{
		bool Cleared(int x, int y) => blasted.Get(x, y) == CellKind.Empty && blasted.Base.Get(x, y).IsSolid();
		foreach (PathStep s in r.Steps) {
			for (int dx = 0; dx <= 1; dx++)
				for (int dy = -Body.HeightTiles + 1; dy <= 0; dy++)
					if (Cleared(s.To.X + dx, s.To.Y + dy) || Cleared(s.From.X + dx, s.From.Y + dy))
						return true;
		}
		return false;
	}

	private SkillStatus Choose(BotContext ctx, BlastPlan plan)
	{
		Plan = plan;
		ExplosiveInfo e = plan.Explosive;
		int tile = ctx.World.TileTypeAt(plan.Wall.X, plan.Wall.Y);
		ctx.Log.Info($"BlastOut: {e.Name} at {plan.Wall} (tile {tile}, pickaxe {ctx.PickPower} cannot break it, a blast can): thrown from {plan.Stance} at ({plan.AimX:0},{plan.AimY:0}) px to come to rest at {plan.Impact}, "
			+ $"radius {e.TileRadius} clears {plan.Cleared} cell(s); then the walk is {plan.Way.Status} ({plan.Way.Steps.Count} steps, {plan.Way.Cost:0}t); "
			+ $"out of the blast's box (±{e.BlastHalfPx:0} px, {e.Damage} damage) in {plan.Retreat.Cost:0} of the {e.FuseTicks}-tick fuse less the {e.UseTicks}-tick throw");
		if (rejected.Count > 0)
			ctx.Log.Info($"BlastOut: passed over: {string.Join("; ", rejected)}");
		phase = Phase.ToStance;
		State = $"going to throw from {plan.Stance}";
		return SkillStatus.Running;
	}

	// ---------- doing it ----------

	/// <summary>The planned aim, give or take what the body's place in the node moves it (an aim straight up or down follows the body).</summary>
	private bool SameAim(float x, float y) =>
		System.Math.Abs(x - Plan.AimX) < Body.TileSize / 2f && System.Math.Abs(y - Plan.AimY) < Body.TileSize / 2f;

	private SkillStatus StanceTick(BotContext ctx)
	{
		if (ctx.Body.Grounded && ctx.Node == Plan.Stance) {
			walk?.Stop(ctx);
			walk = null;
			// Onto the place the throw was planned from (the node's canonical place: a body counts as at the node half a tile
			// either way of it), a step at a time, as a player shuffles into place.
			float off = Body.CanonicalLeft(Plan.Stance.X, ctx.Physics) - ctx.Body.X;
			if (System.Math.Abs(off) > NudgePx && nudgeTicks++ < MaxNudgeTicks) {
				if (System.Math.Abs(ctx.Body.VX) < 1f)
					ctx.Output.Horizontal = off > 0 ? 1 : -1;
				State = $"stepping into place at {Plan.Stance} ({off:0.#} px)";
				return SkillStatus.Running;
			}
			// From where the body really is (up to half a tile off the node, and the throw starts at its centre): each aim whose
			// explosive rests within reach of the wall, the planned one first, is played again, blast, way out and walk past it.
			ExplosiveInfo e = Plan.Explosive;
			float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.Y + ctx.Physics.Height / 2f;
			float wx = Plan.Wall.X * Body.TileSize + Body.TileSize / 2f, wy = Plan.Wall.Y * Body.TileSize + Body.TileSize / 2f;
			var aims = Aims(new Candidate(Plan.Wall, Plan.Front), cx, cy).Select(a => {
				var (rx, ry) = ExplosiveFlight.Rest(ctx.Grid, cx, cy, a.X, a.Y, e, e.FuseTicks);
				return (a.X, a.Y, rx, ry, d: (rx - wx) * (rx - wx) + (ry - wy) * (ry - wy));
			}).Where(t => ExplosiveFlight.InBlast(t.rx, t.ry, e, Plan.Wall.X, Plan.Wall.Y))
				.OrderBy(t => SameAim(t.X, t.Y) ? 0 : 1).ThenBy(t => t.d).ToList();
			string why = $"no throw from {ctx.Node} comes to rest within {e.TileRadius} of {Plan.Wall}";
			bool ready = false;
			foreach (var (ax, ay, rx, ry, _) in aims) {
				var clear = new GoalClearOfBlast(rx, ry, e.BlastHalfPx, ctx.Physics);
				PathResult away = RetreatFrom(ctx, ctx.Node, clear);
				if (away.Status != PathStatus.Found || away.Cost > e.FuseTicks - e.UseTicks) {
					why = $"from {ctx.Node} the way out of the blast is {away.Status} ({away.Cost:0}t)";
					continue;
				}
				OverlayGrid blasted = BlastGrid(ctx, e, rx, ry, out _, out float nearest);
				if (Plan.OnlyNearer) {
					if (nearest >= pocketNearest) {
						why = $"from {ctx.Node} as the body stands the blast opens nothing nearer the goal";
						continue;
					}
				}
				else {
					TilePos after = away.Steps.Count > 0 ? away.Steps[^1].To : ctx.Node;
					var past = new PathSearch(ctx.Moves.WithGrid(blasted), after, Goal, SearchLimits.Default, trustStart: after == ctx.Node);
					past.Step(double.MaxValue);
					if (past.Result.Status is not (PathStatus.Found or PathStatus.Partial) || past.Result.Steps.Count == 0 || !UsesCleared(past.Result, blasted)) {
						why = $"from {ctx.Node} as the body stands the walk past the blast is {past.Result.Status}";
						continue;
					}
				}
				if (!SameAim(ax, ay))
					ctx.Log.Info($"BlastOut: from {ctx.Node} as the body stands, aiming at ({ax:0},{ay:0}) px instead");
				Plan.AimX = ax;
				Plan.AimY = ay;
				Plan.Clear = clear;
				Plan.Retreat = away;
				ready = true;
				break;
			}
			if (!ready)
				return Fail(ctx, why);
			countBefore = ctx.Inventory.Count(Plan.Explosive.ItemType);
			phase = Phase.Throwing;
			State = $"throwing a {Plan.Explosive.Name} at {Plan.Impact}";
			return ThrowTick(ctx);
		}
		if (walk == null) {
			walk = new WalkToSkill(new GoalNode(Plan.Stance, ctx.Physics)) { Quiet = true, MaxNoPathAttempts = 1 };
			walk.Start(ctx);
		}
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Failed)
			return Fail(ctx, $"no way to the throw spot {Plan.Stance}: {walk.FailureReason}");
		if (s == SkillStatus.Succeeded) {
			walk = null;
			if (ctx.Node != Plan.Stance)
				return Fail(ctx, $"the walk to the throw spot {Plan.Stance} ended at {ctx.Node}");
		}
		State = $"going to throw from {Plan.Stance}";
		return SkillStatus.Running;
	}

	private SkillStatus ThrowTick(BotContext ctx)
	{
		int have = ctx.Inventory.Count(Plan.Explosive.ItemType);
		if (have < countBefore) {
			thrownAt = ctx.Tick;
			retreat = new PathExecutor(ctx.Moves.WalkingOnly(), Plan.Retreat.Steps);
			ctx.Log.Info($"BlastOut: threw a {Plan.Explosive.Name} at {Plan.Impact} from {ctx.Node} ({have} left); out of its reach to {(Plan.Retreat.Steps.Count > 0 ? Plan.Retreat.Steps[^1].To : ctx.Node)} ({Plan.Retreat.Steps.Count} steps, {Plan.Retreat.Cost:0}t)");
			phase = Phase.Retreating;
			State = "getting clear of the blast";
			return RetreatTick(ctx);
		}
		if (have <= 0)
			return Fail(ctx, $"no {Plan.Explosive.Name} left to throw");
		if (++throwTicks > ThrowWaitTicks)
			return Fail(ctx, $"the {Plan.Explosive.Name} was not thrown in {ThrowWaitTicks} ticks (the game refused it)");
		ctx.Output.Tool = ToolRequest.Item;
		ctx.Output.ToolItem = Plan.Explosive.ItemType;
		ctx.Output.UseItem = true;
		ctx.Output.AimMouse(Plan.AimX, Plan.AimY);
		return SkillStatus.Running;
	}

	private SkillStatus RetreatTick(BotContext ctx)
	{
		if (Blasted(ctx))
			return Done(ctx);
		if (ctx.Tick - thrownAt > Plan.Explosive.FuseTicks + BlastGraceTicks)
			return Fail(ctx, $"no blast at {Plan.Wall} {ctx.Tick - thrownAt} ticks after the throw (fuse {Plan.Explosive.FuseTicks})");
		if (ctx.Body.Grounded && Plan.Clear.IsGoal(ctx.Node)) {
			phase = Phase.Waiting;
			State = "clear of the blast; waiting for it";
			ctx.Log.Info($"BlastOut: clear of the blast at {ctx.Node}, {ctx.Tick - thrownAt} ticks after the throw; waiting for it");
			return SkillStatus.Running;
		}
		if (retreat == null || retreat.Status is not (ExecStatus.Running or ExecStatus.Finished)) {
			if (retreat != null)
				ctx.Log.Warn($"BlastOut: the way out of the blast failed ({retreat.Status}: {retreat.Reason})");
			if (retreatReplanned || !ctx.Body.Grounded) {
				State = "getting clear of the blast (off the plan)";
				return SkillStatus.Running;
			}
			retreatReplanned = true;
			PathResult again = RetreatFrom(ctx, ctx.Node, Plan.Clear);
			ctx.Log.Info($"BlastOut: the way out planned again from {ctx.Node}: {again}");
			retreat = again.Status == PathStatus.Found ? new PathExecutor(ctx.Moves.WalkingOnly(), again.Steps) : null;
			if (retreat == null)
				return SkillStatus.Running;
		}
		ctx.Output = retreat.Tick(ctx.Body);
		return SkillStatus.Running;
	}

	private SkillStatus WaitTick(BotContext ctx)
	{
		if (Blasted(ctx))
			return Done(ctx);
		if (ctx.Tick - thrownAt > Plan.Explosive.FuseTicks + BlastGraceTicks)
			return Fail(ctx, $"no blast at {Plan.Wall} {ctx.Tick - thrownAt} ticks after the throw (fuse {Plan.Explosive.FuseTicks})");
		State = $"waiting for the blast ({ctx.Tick - thrownAt}/{Plan.Explosive.FuseTicks} ticks)";
		return SkillStatus.Running;
	}

	private bool Blasted(BotContext ctx) => !ctx.Grid.Get(Plan.Wall.X, Plan.Wall.Y).IsSolid();

	private SkillStatus Done(BotContext ctx)
	{
		blasts++;
		if (Plan.OnlyNearer && Carried(ctx).Count > 0) {
			ctx.Log.Info($"BlastOut: {Plan.Wall} is open {ctx.Tick - thrownAt} ticks after the throw (blast {blasts}); looking for the next wall from {ctx.Node}");
			walk?.Stop(ctx);
			walk = null;
			retreat = null;
			Plan = null;
			candidates = null;
			pending = null;
			fallback = null;
			way = null;
			rejected.Clear();
			nextCandidate = nextExplosive = throwTicks = nudgeTicks = 0;
			retreatReplanned = false;
			phase = Phase.Planning;
			State = "planning the next blast";
			return SkillStatus.Running;
		}
		ctx.Log.Info($"BlastOut: {Plan.Wall} is open {ctx.Tick - thrownAt} ticks after the throw ({blasts} blast(s)); the walk to {Goal} is planned again");
		State = "done";
		Stop(ctx);
		return SkillStatus.Succeeded;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"BlastOut failed: {reason}");
		Stop(ctx);
		return SkillStatus.Failed;
	}
}
