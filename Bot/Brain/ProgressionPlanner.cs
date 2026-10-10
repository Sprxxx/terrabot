using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Brain;

/// <summary>Something the progression wants to be true.</summary>
public abstract record Need(string Label);

/// <summary>Carry <see cref="Count"/> of any of <see cref="Accepts"/>.</summary>
public sealed record HaveItem(IReadOnlyList<int> Accepts, int Count, string Label) : Need(Label)
{
	public HaveItem(int item, int count, string label) : this(new[] { item }, count, label) { }
}

/// <summary>A crafting station of this tile type is placed (and remembered).</summary>
public sealed record StationPlaced(int TileType, string Label) : Need(Label);

/// <summary>The best hotbar pickaxe has at least <see cref="Power"/>; otherwise get <see cref="Item"/>.</summary>
public sealed record PickaxeAtLeast(int Power, int Item, string Label) : Need(Label);

/// <summary>
/// Altoclef-style "what is the next thing I need?" planner, re-evaluated whenever a skill ends. Works through a
/// list of stages (<see cref="Plans"/>); for the first unmet one it resolves the dependency tree from the
/// game's own recipes (<see cref="IGameKnowledge"/>) depth-first and returns the first leaf action:
/// <list type="bullet">
/// <item>raw material: gather it (chop trees, mine its tile, hunt the NPC that drops it);</item>
/// <item>crafted item: make sure every ingredient is carried (recursively), every station is placed and
/// nearby, then craft;</item>
/// <item>station: place the item that makes it (next to the work bench, so all stations stay in reach of
/// one another), crafting that item first if needed.</item>
/// </list>
/// Ingredients already counted for one branch are "claimed", so a sibling branch (e.g. torches for a furnace)
/// does not count the same wood twice. Among several recipes or station items it picks the cheapest by a
/// rough gather-time estimate, which also rules out unobtainable ones (e.g. lead in an iron world).
/// At night, surface gathering (wood, hunting) is replaced by sitting it out in a <see cref="ShelterSkill"/>.
/// </summary>
public sealed class ProgressionPlanner
{
	public const int WoodStockpile = 30;
	public const int DangerRadiusTiles = 12;
	/// <summary>A remembered station is forgotten only if the bot is this close and no longer sees it.</summary>
	public const int StationSightTiles = 20;
	/// <summary>A station we placed ourselves is trusted this long even if perception has not caught up yet.</summary>
	public const int PlacedGraceTicks = 120;
	/// <summary>Close enough to a station for crafting (vanilla AdjTiles: ±4 columns, 3 rows up / 2 down from the floor).</summary>
	public const int StationReach = 3;
	public const int MaxDepth = 10;
	/// <summary>After a shelter ends, don't dig in again for this long (e.g. it could not be sealed).</summary>
	public const int ShelterCooldownTicks = 60 * 60;
	/// <summary>Pickaxe power of the iron tier (Iron Pickaxe 40, Lead Pickaxe 43).</summary>
	public const int IronTierPickPower = 40;

	public static class Plans
	{
		/// <summary>Work bench, wooden sword, 30 wood (Milestone 4).</summary>
		public const string Early = "early";
		/// <summary>Work bench, wooden sword, furnace, anvil and an iron-tier pickaxe (Milestone 5).</summary>
		public const string Iron = "iron";
		public static readonly string[] All = { Early, Iron };
	}

	private readonly Dictionary<int, long> notedAt = new();
	private readonly Dictionary<int, float> costMemo = new();
	private readonly HashSet<int> costVisiting = new();
	private string stuck;
	private long lastShelterEnd = long.MinValue / 2;

	/// <summary>Stations the bot has seen (tile type -> top-left tile), kept while out of sight.</summary>
	public Dictionary<int, TilePos> KnownStations { get; } = new();
	public string Plan { get; private set; } = Plans.Iron;
	public string Status { get; private set; } = "";
	public bool Complete { get; private set; }
	/// <summary>Set when a stage cannot be resolved (no recipe, nothing places the station...).</summary>
	public bool Stuck => stuck != null;

	public static IReadOnlyList<Need> StagesFor(string plan, GameIds ids)
	{
		var stages = new List<Need> {
			new StationPlaced(ids.WorkBenchTile, "work bench"),
			new HaveItem(ids.WoodenSword, 1, "wooden sword"),
		};
		if (plan == Plans.Early) {
			stages.Add(new HaveItem(ids.Wood, WoodStockpile, "wood stockpile"));
			return stages;
		}
		stages.Add(new StationPlaced(ids.FurnaceTile, "furnace"));
		stages.Add(new StationPlaced(ids.AnvilTile, "anvil"));
		stages.Add(new PickaxeAtLeast(IronTierPickPower, ids.IronTierPickaxe, "iron-tier pickaxe"));
		return stages;
	}

	public void Reset(string plan = null)
	{
		if (plan != null)
			Plan = plan;
		KnownStations.Clear();
		notedAt.Clear();
		Status = "";
		Complete = false;
		stuck = null;
		lastShelterEnd = long.MinValue / 2;
	}

	/// <summary>Tell the planner about a finished skill (so a just-placed station counts before the next scan).</summary>
	public void OnSkillFinished(ISkill skill, SkillStatus status, BotContext ctx)
	{
		if (status == SkillStatus.Succeeded && skill is PlaceItemSkill place && place.PlacedAt is TilePos at) {
			int tile = ctx.Knowledge.TilePlacedBy(place.ItemType);
			if (tile >= 0) {
				KnownStations[tile] = at;
				notedAt[tile] = ctx.Tick;
			}
		}
		if (skill is ShelterSkill)
			lastShelterEnd = ctx.Tick;
	}

	/// <summary>The next skill to run, or null when the plan is complete or stuck.</summary>
	public ISkill Next(BotContext ctx)
	{
		RememberStations(ctx);
		costMemo.Clear();
		stuck = null;
		var claims = new Dictionary<int, int>();
		foreach (Need stage in StagesFor(Plan, ctx.Ids)) {
			ISkill skill = Resolve(stage, claims, 0, ctx);
			if (stuck != null) {
				Status = $"stuck on {stage.Label}: {stuck}";
				Complete = false;
				return null;
			}
			if (skill != null)
				return AtNight(skill, ctx);
		}
		Status = Plan == Plans.Early
			? "early-game chain complete: work bench placed, wooden sword, wood stockpiled"
			: "iron-age chain complete: work bench, furnace and anvil placed, iron-tier pickaxe in hand";
		Complete = true;
		return null;
	}

	private ISkill AtNight(ISkill skill, BotContext ctx)
	{
		if (!ctx.IsNight || skill is not (GatherWoodSkill or HuntSkill) || ctx.Tick - lastShelterEnd < ShelterCooldownTicks)
			return skill;
		Status = $"night: sheltering instead of {skill.Name} ({Status})";
		return new ShelterSkill();
	}

	private ISkill Resolve(Need need, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (depth > MaxDepth) {
			stuck = $"dependency chain deeper than {MaxDepth} at {need.Label}";
			return null;
		}
		return need switch {
			StationPlaced s => ResolveStation(s, claims, depth, ctx),
			PickaxeAtLeast p => ctx.PickPower >= p.Power ? null : Resolve(new HaveItem(p.Item, 1, p.Label), claims, depth + 1, ctx),
			HaveItem h => ResolveItem(h, claims, depth, ctx),
			_ => null,
		};
	}

	private ISkill ResolveStation(StationPlaced s, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (KnownStations.ContainsKey(s.TileType))
			return null;
		IGameKnowledge k = ctx.Knowledge;
		IReadOnlyList<int> items = k.ItemsPlacing(s.TileType);
		int carried = items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0, -1);
		if (carried >= 0) {
			string name = k.ItemName(carried);
			int bench = ctx.Ids.WorkBenchTile;
			if (s.TileType != bench && KnownStations.TryGetValue(bench, out TilePos b) && b.ChebyshevTo(ctx.Node) > StationReach)
				return Do($"walking to the work bench to place a {name}", WalkToStation(b, ctx));
			return Do($"placing a {name}", new PlaceItemSkill(carried));
		}
		int best = Cheapest(items, ctx);
		if (best < 0) {
			stuck = $"no obtainable item places a {k.TileName(s.TileType)}";
			return null;
		}
		return Resolve(new HaveItem(best, 1, k.ItemName(best)), claims, depth + 1, ctx);
	}

	private ISkill ResolveItem(HaveItem h, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		IGameKnowledge k = ctx.Knowledge;
		int claimed = claims.GetValueOrDefault(h.Accepts[0]);
		int available = ctx.Inventory.CountAny(h.Accepts) - claimed;
		if (available >= h.Count) {
			claims[h.Accepts[0]] = claimed + h.Count;
			return null;
		}
		int more = h.Count - Math.Max(0, available);
		int item = Cheapest(h.Accepts, ctx);
		if (item < 0) {
			stuck = $"no way to get {h.Label}";
			return null;
		}
		string name = k.ItemName(item);
		int target = ctx.Inventory.Count(item) + more;
		ItemSource src = k.SourceOf(item);
		switch (src.Kind) {
			case SourceKind.Tree:
				return Do($"need {more} more {name} (gathering to {target})", new GatherWoodSkill(item, target));
			case SourceKind.Mine:
				return Do($"need {more} more {name} (mining to {target})", new MineSkill(item, src.TileType, target));
			case SourceKind.Hunt:
				return Do($"need {more} more {name} (hunting to {target})", new HuntSkill(item, src.NpcAiStyle, target));
		}

		RecipeInfo recipe = BestRecipe(item, ctx);
		if (recipe == null) {
			stuck = $"no recipe or source for {name}";
			return null;
		}
		int crafts = (more + recipe.ResultStack - 1) / recipe.ResultStack;
		foreach (Ingredient ing in recipe.Ingredients) {
			ISkill sub = Resolve(new HaveItem(ing.Accepts, ing.Stack * crafts, k.ItemName(ing.Item)), claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		foreach (int station in recipe.Stations) {
			ISkill sub = Resolve(new StationPlaced(station, k.TileName(station)), claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		foreach (int station in recipe.Stations)
			if (!NearStation(ctx, station))
				return Do($"walking to the {k.TileName(station)} to craft {name}", WalkToStation(KnownStations[station], ctx));
		return Do($"crafting {more} {name}", new CraftSkill(item, target));
	}

	// ---------- cost estimate (rough ticks; infinity = cannot be had) ----------

	private const float TreeCost = 30f, MineCost = 60f, HuntCost = 300f;
	private const float CraftStepCost = 5f;

	/// <summary>Cheapest obtainable item of the list (ties: the first, i.e. the recipe's own item). -1 if none.</summary>
	private int Cheapest(IReadOnlyList<int> items, BotContext ctx)
	{
		int best = -1;
		float bestCost = float.PositiveInfinity;
		foreach (int i in items) {
			float c = Cost(i, 0, ctx);
			if (c < bestCost) {
				bestCost = c;
				best = i;
			}
		}
		return best;
	}

	/// <summary>
	/// Cheapest recipe for the item, evaluated with the item itself marked in progress: a recipe whose ingredients
	/// can only be made from the item again (iron bar -> iron fence -> iron bar) costs infinity.
	/// </summary>
	private RecipeInfo BestRecipe(int item, BotContext ctx)
	{
		RecipeInfo best = null;
		float bestCost = float.PositiveInfinity;
		costVisiting.Add(item);
		try {
			foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(item)) {
				float c = RecipeCost(r, 0, ctx);
				if (c < bestCost) {
					bestCost = c;
					best = r;
				}
			}
		}
		finally {
			costVisiting.Remove(item);
		}
		return best;
	}

	private float Cost(int item, int depth, BotContext ctx)
	{
		// The cycle check comes first: a cached cost was worked out on another path and may go through an item
		// that is in progress on this one.
		if (depth > MaxDepth || costVisiting.Contains(item))
			return float.PositiveInfinity; // a cycle (e.g. wood -> platform -> wood) is not a way to get it
		if (costMemo.TryGetValue(item, out float memo))
			return memo;
		costVisiting.Add(item);
		try {
			float cost;
			ItemSource src = ctx.Knowledge.SourceOf(item);
			if (src.Kind != SourceKind.None) {
				cost = src.Kind switch { SourceKind.Tree => TreeCost, SourceKind.Mine => MineCost, _ => HuntCost };
			}
			else {
				cost = float.PositiveInfinity;
				foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(item))
					cost = Math.Min(cost, RecipeCost(r, depth + 1, ctx));
			}
			if (src.Kind != SourceKind.None)
				costMemo[item] = cost; // only raw costs are path-independent
			return cost;
		}
		finally {
			costVisiting.Remove(item);
		}
	}

	private float RecipeCost(RecipeInfo r, int depth, BotContext ctx)
	{
		float sum = 0f;
		foreach (Ingredient ing in r.Ingredients) {
			float c = float.PositiveInfinity;
			foreach (int a in ing.Accepts)
				c = Math.Min(c, Cost(a, depth + 1, ctx));
			sum += ing.Stack * c;
		}
		sum /= Math.Max(1, r.ResultStack);
		sum += CraftStepCost; // every crafting step costs a little, so a roundabout chain never ties with a direct one
		foreach (int station in r.Stations) {
			if (KnownStations.ContainsKey(station))
				continue;
			float c = float.PositiveInfinity;
			foreach (int i in ctx.Knowledge.ItemsPlacing(station))
				c = Math.Min(c, ctx.Inventory.Count(i) > 0 ? 0f : Cost(i, depth + 1, ctx));
			sum += c;
		}
		return sum;
	}

	// ---------- helpers ----------

	private ISkill Do(string why, ISkill skill)
	{
		Status = why;
		Complete = false;
		return skill;
	}

	private void RememberStations(BotContext ctx)
	{
		foreach (var kv in ctx.Seen.Stations)
			KnownStations[kv.Key] = kv.Value;
		TilePos node = ctx.Node;
		foreach (int type in KnownStations.Keys.ToList())
			if (KnownStations[type].ChebyshevTo(node) <= StationSightTiles && !ctx.Seen.Stations.ContainsKey(type)
				&& !(notedAt.TryGetValue(type, out long t) && ctx.Tick - t < PlacedGraceTicks))
				KnownStations.Remove(type); // in sight but gone (broken, or never really there)
	}

	/// <summary>Stand beside (not on) the station, within <see cref="StationReach"/> - 1 so <see cref="NearStation"/> holds.</summary>
	private static WalkToSkill WalkToStation(TilePos at, BotContext ctx) =>
		new(new Core.Pathfinding.GoalNearTile(at, StationReach - 1, ctx.Physics));

	private bool NearStation(BotContext ctx, int tileType) =>
		KnownStations.TryGetValue(tileType, out TilePos at) && at.ChebyshevTo(ctx.Node) <= StationReach;

	/// <summary>Hostile close enough that the brain should drop everything and fight.</summary>
	public static bool InDanger(BotContext ctx)
	{
		Core.Physics.PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		return ctx.Combat.HasWeapon && ctx.Hostiles.Any(h => h.DistanceTo(cx, cy) <= DangerRadiusTiles * 16f);
	}
}
