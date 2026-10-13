using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Building;
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

/// <summary>A house (<see cref="Building.Blueprint.SmallHouse"/>) that passes the game's housing check, optionally with a heart lantern hung inside.</summary>
public sealed record HouseBuilt(string Label, bool HeartLantern = false) : Need(Label);

/// <summary>A boss arena (<see cref="Building.Blueprint.Arena"/>) near the house: flat floor, open air, torches, a campfire.</summary>
public sealed record ArenaBuilt(string Label) : Need(Label);

/// <summary>The item is worn (armour).</summary>
public sealed record Wearing(int Item, string Label) : Need(Label);

/// <summary>Max life (from life crystals) is at least <see cref="Life"/>.</summary>
public sealed record LifeAtLeast(int Life, string Label) : Need(Label);

/// <summary>The world records this boss as defeated: get its summon item, wait for night, summon and fight.</summary>
public sealed record BossDefeated(int Npc, int SummonItem, int[] Minions, string Label) : Need(Label);

/// <summary>Preparation for a boss (life, bow, arrows): moot once that boss is defeated.</summary>
public sealed record PrepFor(int BossNpc, Need Inner) : Need(Inner.Label);

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
	/// <summary>Max life to have before a boss: 5 life crystals (also what vanilla wants before the Eye spawns on its own).</summary>
	public const int BossLife = 200;
	/// <summary>Arrows to carry into a boss fight (the Eye took about 250 in testing, misses included).</summary>
	public const int BossArrows = 500;
	/// <summary>Longest wait for nightfall: a whole Terraria day (15 minutes).</summary>
	public const int NightWaitTicks = 54000 + 600;
	/// <summary>Stations nothing can place (demon altars) are looked for this far around the bot.</summary>
	public const int StationSearchRadius = 150;
	/// <summary>NPC AI styles that only come out at night (demon eyes): hunt them at night, wait for it by day.</summary>
	public static readonly int[] NocturnalAiStyles = { 2 };

	public static class Plans
	{
		/// <summary>Work bench, wooden sword, 30 wood (Milestone 4).</summary>
		public const string Early = "early";
		/// <summary>Work bench, wooden sword, furnace, anvil and an iron-tier pickaxe (Milestone 5).</summary>
		public const string Iron = "iron";
		/// <summary>Iron plus a house, an iron-tier broadsword and the iron-tier armour set, worn (Milestone 6).</summary>
		public const string Settle = "settle";
		/// <summary>Settle plus 200 max life, an iron-tier bow, 500 arrows and the Eye of Cthulhu defeated (Milestone 7).</summary>
		public const string Boss = "boss";
		/// <summary>Just max life 200 from life crystals (a building block of the boss plan; handy on its own).</summary>
		public const string Crystals = "crystals";
		/// <summary>Just the boss arena (Milestone 8; also part of the boss plan).</summary>
		public const string Arena = "arena";
		public static readonly string[] All = { Early, Iron, Settle, Crystals, Arena, Boss };
	}

	private readonly Dictionary<int, long> notedAt = new();
	/// <summary>Item costs worked out in this planning pass (<see cref="SolveCosts"/>).</summary>
	private readonly Dictionary<int, float> costs = new();
	private int costEvaluations;
	/// <summary>Recipe cost evaluations in the last planning pass (tests).</summary>
	public int CostEvaluations => costEvaluations;
	/// <summary>Most items one cost solve looks at (the recipes reachable from a single item are far fewer).</summary>
	public const int MaxCostItems = 5000;
	private string stuck;
	private long lastShelterEnd = long.MinValue / 2;
	private long lastRestEnd = long.MinValue / 2;
	private readonly List<TilePos> badSites = new();
	private int siteFailures;
	public const int MaxBuildFailuresPerSite = 2;
	/// <summary>Spare building blocks on top of the missing frame cells: walking there may pillar or bridge with them too.</summary>
	public const int BlockMargin = 10;

	/// <summary>Top-left tile of the house being built or already built (kept across re-plans).</summary>
	public TilePos? HouseSite { get; private set; }
	/// <summary>Top-left tile of the boss arena being built or already built.</summary>
	public TilePos? ArenaSite { get; private set; }
	private readonly List<TilePos> badArenaSites = new();
	private int arenaFailures;
	/// <summary>Keep the arena this many tiles clear of the house (bosses kill town NPCs; the guides keep them apart).</summary>
	public const int ArenaHouseGap = 4;
	/// <summary>Night left (ticks) needed to summon a night boss: the Eye took about 2.5 min on Classic; 4.5 min with margin.</summary>
	public const int MinNightForBoss = 60 * 60 * 9 / 2;
	/// <summary>Rest to this much of max life before summoning a boss (at most <see cref="MaxRestTicks"/>).</summary>
	public const float SummonLifeFraction = 0.9f;
	public const int MaxRestTicks = 60 * 60;
	/// <summary>Summon within this many columns of the arena's middle.</summary>
	public const int ArenaCentreTolerance = 4;

	/// <summary>Stations the bot has seen (tile type -> top-left tile), kept while out of sight.</summary>
	public Dictionary<int, TilePos> KnownStations { get; } = new();
	public string Plan { get; private set; } = Plans.Settle;
	public string Status { get; private set; } = "";
	public bool Complete { get; private set; }
	/// <summary>Set when a stage cannot be resolved (no recipe, nothing places the station...).</summary>
	public bool Stuck => stuck != null;

	public static IReadOnlyList<Need> StagesFor(string plan, GameIds ids)
	{
		if (plan == Plans.Crystals)
			return new Need[] { new LifeAtLeast(BossLife, "max life") };
		if (plan == Plans.Arena)
			return new Need[] { new ArenaBuilt("arena") };
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
		// The sword before the pickaxe: in a real world, mining the pickaxe's 30 ore with a copper shortsword (5 damage, a
		// point or two a hit against a yellow slime) cost 9 deaths in one run.
		if (plan != Plans.Iron)
			stages.Add(new HaveItem(ids.IronTierBroadsword, 1, "iron-tier broadsword"));
		stages.Add(new PickaxeAtLeast(IronTierPickPower, ids.IronTierPickaxe, "iron-tier pickaxe"));
		if (plan == Plans.Iron)
			return stages;
		stages.Add(new HouseBuilt("house"));
		stages.Add(new Wearing(ids.IronTierHelmet, "helmet"));
		stages.Add(new Wearing(ids.IronTierChainmail, "chainmail"));
		stages.Add(new Wearing(ids.IronTierGreaves, "greaves"));
		if (plan == Plans.Settle)
			return stages;
		stages.Add(new PrepFor(ids.EyeBoss, new LifeAtLeast(BossLife, "max life")));
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.IronTierBow, 1, "iron-tier bow")));
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.WoodenArrow, BossArrows, "arrows")));
		stages.Add(new PrepFor(ids.EyeBoss, new ArenaBuilt("arena")));
		stages.Add(new PrepFor(ids.EyeBoss, new HouseBuilt("heart lantern in the house", HeartLantern: true)));
		stages.Add(new BossDefeated(ids.EyeBoss, ids.SuspiciousEye, new[] { ids.EyeServant }, "Eye of Cthulhu"));
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
		lastRestEnd = long.MinValue / 2;
		HouseSite = null;
		ArenaSite = null;
		searchedFor.Clear();
		badSites.Clear();
		badArenaSites.Clear();
		stationExplores = 0;
		siteFailures = 0;
		arenaFailures = 0;
	}

	/// <summary>Max life from life crystals stops at 400 (vanilla: 15 crystals).</summary>
	public const int MaxCrystalLife = 400;
	/// <summary>Life crystals seen within this many tiles are mined right away.</summary>
	public const int CrystalRadius = 25;

	public static bool LifeCrystalUseful(BotContext ctx) => ctx.Ids.LifeCrystal > 0 && ctx.Combat.LifeMaxBase > 0 && ctx.Combat.LifeMaxBase < MaxCrystalLife;

	/// <summary>The nearest life crystal (heart tile) the player has seen within <see cref="CrystalRadius"/> that can be mined.</summary>
	public static TilePos? SeenLifeCrystal(BotContext ctx)
	{
		int heart = ctx.Knowledge?.SourceOf(ctx.Ids.LifeCrystal).TileType ?? -1;
		if (heart <= 0)
			return null;
		foreach (TilePos c in ctx.World.FindTiles(heart, ctx.Node, CrystalRadius))
			if (!ctx.UnreachableTiles.Contains(c) && ctx.World.CanMineTarget(c.X, c.Y))
				return c;
		return null;
	}

	/// <summary>Chests closer than this (tiles) that are seen and not looted yet are looted before anything else.</summary>
	public const int LootRadius = 40;

	/// <summary>The nearest seen, unlocked chest not looted (or given up on) yet, within <see cref="LootRadius"/>.</summary>
	public static ChestInfo? NextChest(BotContext ctx)
	{
		foreach (ChestInfo c in ctx.World.SeenChests(ctx.Node, LootRadius))
			if (!c.Locked && !ctx.OpenedChests.Contains(c.TopLeft))
				return c;
		return null;
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
		if (skill is CraftSkill && status == SkillStatus.Failed && skill.FailureReason.StartsWith(nameof(CraftResult.NeedsStation))) {
			stationMissed = true;
			ctx.Log.Info("Auto: the game saw no station in range for that craft; walking right up to it first");
		}
		if (skill is CraftSkill && status == SkillStatus.Succeeded || ReferenceEquals(skill, stationWalk) && status == SkillStatus.Succeeded)
			stationMissed = false;
		if (skill is LootChestSkill loot)
			ctx.OpenedChests.Add(loot.Chest); // looted, or not reachable: either way not again
		if (skill is WaitSkill { What: "life" })
			lastRestEnd = ctx.Tick; // one rest per summon attempt: a rest that timed out is not started again at once
		if (skill is BuildSkill arena && !arena.Blueprint.IsHouse) {
			if (status == SkillStatus.Succeeded)
				arenaFailures = 0;
			else if (status == SkillStatus.Failed && ++arenaFailures >= MaxBuildFailuresPerSite) {
				ctx.Log.Info($"Auto: giving up on the arena site {arena.Origin}");
				badArenaSites.Add(arena.Origin);
				ArenaSite = null;
				arenaFailures = 0;
			}
			return;
		}
		if (skill is BuildSkill done && status == SkillStatus.Succeeded) {
			HouseSite = done.Origin;
			siteFailures = 0;
		}
		if (skill is BuildSkill build && status == SkillStatus.Failed && ++siteFailures >= MaxBuildFailuresPerSite) {
			ctx.Log.Info($"Auto: giving up on the house site {build.Origin}");
			badSites.Add(build.Origin);
			HouseSite = null;
			siteFailures = 0;
		}
	}

	/// <summary>The next skill to run, or null when the plan is complete or stuck.</summary>
	public ISkill Next(BotContext ctx)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		try {
			return NextUntimed(ctx);
		}
		finally {
			if (watch.ElapsedMilliseconds > SlowPassMs)
				ctx.Log.Warn($"Planner: slow pass, {watch.ElapsedMilliseconds} ms ({costs.Count} item costs, {costEvaluations} recipe evaluations)");
		}
	}

	/// <summary>A planning pass slower than this (ms) is logged.</summary>
	public const int SlowPassMs = 50;

	private ISkill NextUntimed(BotContext ctx)
	{
		RememberStations(ctx);
		ProtectBuilt(ctx);
		// A chest in sight comes first, as for a player: healing potions, accessories, better weapons.
		if (NextChest(ctx) is ChestInfo chest)
			return Do($"looting the chest at {chest.TopLeft}", new LootChestSkill(chest.TopLeft));
		// Likewise a life crystal: mined and used on sight, whatever stage the plan is at.
		if (LifeCrystalUseful(ctx)) {
			if (ctx.Inventory.Count(ctx.Ids.LifeCrystal) > 0)
				return Do($"using a life crystal (max life {ctx.Combat.LifeMaxBase})", new UseItemSkill(ctx.Ids.LifeCrystal));
			if (SeenLifeCrystal(ctx) is TilePos heart)
				return Do($"mining the life crystal seen at {heart}",
					new MineSkill(ctx.Ids.LifeCrystal, ctx.Knowledge.SourceOf(ctx.Ids.LifeCrystal).TileType, ctx.Inventory.Count(ctx.Ids.LifeCrystal) + 1));
		}
		costs.Clear();
		costEvaluations = 0;
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
		Status = Plan switch {
			Plans.Early => "early-game chain complete: work bench placed, wooden sword, wood stockpiled",
			Plans.Iron => "iron-age chain complete: work bench, furnace and anvil placed, iron-tier pickaxe in hand",
			Plans.Crystals => $"max life {ctx.Combat.LifeMaxBase} from life crystals",
			Plans.Arena => $"the arena at {ArenaSite} is ready",
			Plans.Boss => "the Eye of Cthulhu is defeated",
			_ => "settled: house built, iron-tier pickaxe, broadsword and armour",
		};
		Complete = true;
		return null;
	}

	private ISkill AtNight(ISkill skill, BotContext ctx)
	{
		if (skill is HuntSkill h && NocturnalAiStyles.Contains(h.AiStyle))
			return skill; // the prey only comes out at night
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
			HouseBuilt hb => ResolveHouse(hb.HeartLantern, claims, depth, ctx),
			ArenaBuilt => ResolveArena(claims, depth, ctx),
			BossDefeated b => ResolveBoss(b, claims, depth, ctx),
			PrepFor pf => ctx.World.BossDefeated(pf.BossNpc) ? null : Resolve(pf.Inner, claims, depth, ctx),
			LifeAtLeast la => ctx.Combat.LifeMaxBase >= la.Life ? null
				: ctx.Inventory.Count(ctx.Ids.LifeCrystal) > 0 ? Do($"using a life crystal (max life {ctx.Combat.LifeMaxBase}/{la.Life})", new UseItemSkill(ctx.Ids.LifeCrystal))
				: Resolve(new HaveItem(ctx.Ids.LifeCrystal, 1, "life crystal"), claims, depth + 1, ctx),
			Wearing wr => ctx.Inventory.IsEquipped(wr.Item) ? null
				: ctx.Inventory.Count(wr.Item) > 0 ? Do($"putting on the {wr.Label}", new EquipSkill(wr.Item))
				: Resolve(new HaveItem(wr.Item, 1, wr.Label), claims, depth + 1, ctx),
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
		if (items.Count == 0) {
			// Nothing places it (a demon altar): find one in the world.
			TilePos? found = ctx.World.FindTiles(s.TileType, ctx.Node, StationSearchRadius).Cast<TilePos?>().FirstOrDefault();
			if (found == null) {
				// None seen yet (only what is revealed on the map counts): go and look, a new direction each time.
				if (stationExplores >= MaxStationExplores) {
					stuck = $"no {k.TileName(s.TileType)} found after {stationExplores} explorations, and nothing can place one";
					return null;
				}
				int tile = s.TileType;
				int heading = stationExplores++ % 2 == 0 ? 1 : -1;
				return Do($"exploring for a {k.TileName(s.TileType)} (none seen yet)",
					new ExploreSkill(k.TileName(s.TileType), c => c.World.FindTiles(tile, c.Node, StationSearchRadius).Count > 0, ExploreBias.Down, heading));
			}
			KnownStations[s.TileType] = found.Value;
			notedAt[s.TileType] = ctx.Tick + 60 * 60 * 60; // far away: do not forget it before going there
			ctx.Log.Info($"Auto: found a {k.TileName(s.TileType)} at {found.Value}");
			return null;
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
			stuck = $"no way to get {h.Label}" + MissingStationHint(h.Accepts, ctx);
			return null;
		}
		string name = k.ItemName(item);
		int target = ctx.Inventory.Count(item) + more;
		ItemSource src = k.SourceOf(item);
		switch (src.Kind) {
			case SourceKind.Tree:
				return Do($"need {more} more {name} (gathering to {target})", new GatherWoodSkill(item, target));
			case SourceKind.Mine:
				// Stone is also what the bot builds with on the way: seen in game, it mined exactly 20 stone for the furnace,
				// used one to climb out of its hole on the way back to the bench, and went down for one more, forever.
				if (ctx.Ids.BuildingBlocks.Contains(item))
					target += BlockMargin;
				return Do($"need {more} more {name} (mining to {target})", new MineSkill(item, src.TileType, target));
			case SourceKind.Hunt:
				if (NocturnalAiStyles.Contains(src.NpcAiStyle) && !ctx.IsNight)
					return Do($"waiting for night to hunt for {name}", new WaitSkill("night", c => c.IsNight, NightWaitTicks));
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
			if (!NearStation(ctx, station)) {
				stationWalk = WalkToStation(KnownStations[station], ctx);
				return Do($"walking to the {k.TileName(station)} to craft {name}", stationWalk);
			}
		string have = string.Join(", ", recipe.Ingredients.Select(i => $"{k.ItemName(i.Item)} {ctx.Inventory.CountAny(i.Accepts)}/{i.Stack * crafts}"));
		return Do($"crafting {more} {name} ({have})", new CraftSkill(item, target));
	}

	/// <summary>
	/// A valid house, or: pick a site (near the work bench), get the materials it still needs (blocks for the
	/// missing frame cells, walls for the bare ones, each furniture item not yet placed), then build.
	/// </summary>
	private ISkill ResolveHouse(bool heartLantern, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		Blueprint bp = Blueprint.SmallHouse(ctx.Ids, heartLantern);
		if (HouseSite is TilePos built && ctx.World.CheckHousing(built.X + bp.Inside.X, built.Y + bp.Inside.Y) == null
			&& bp.Items.All(it => BuildSkill.IsPlaced(ctx, bp, built, it)))
			return null;
		if (HouseSite == null) {
			TilePos anchor = KnownStations.TryGetValue(ctx.Ids.WorkBenchTile, out TilePos bench) ? bench : ctx.Node;
			HouseSite = SiteFinder.Find(ctx, bp, anchor, badSites);
			if (HouseSite == null) {
				stuck = $"no building site within {SiteFinder.SearchRadius} tiles of {anchor}";
				return null;
			}
			ctx.Log.Info($"Auto: house site {HouseSite.Value} ({SiteFinder.Edits(ctx, bp, HouseSite.Value)} edits)");
		}
		TilePos site = HouseSite.Value;
		int blocks = 0, walls = 0;
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++) {
				int wx = site.X + x, wy = site.Y + y;
				if (bp.At(x, y) == PlanCell.Block && !ctx.Grid.Get(wx, wy).IsSolid())
					blocks++;
				else if (bp.At(x, y) == PlanCell.Wall && !ctx.World.HasWall(wx, wy))
					walls++;
			}
		var needs = new List<Need>();
		if (blocks > 0)
			needs.Add(new HaveItem(ctx.Ids.BuildingBlocks, blocks + BlockMargin, "building blocks"));
		if (walls > 0)
			needs.Add(new HaveItem(bp.WallItem, walls, ctx.Knowledge.ItemName(bp.WallItem)));
		foreach (PlanItem it in bp.Items)
			if (!BuildSkill.IsPlaced(ctx, bp, site, it))
				needs.Add(new HaveItem(it.ItemType, 1, ctx.Knowledge.ItemName(it.ItemType)));
		foreach (Need n in needs) {
			ISkill sub = Resolve(n, claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		return Do($"building a {bp.Name} at {site}", new BuildSkill(bp, site));
	}

	/// <summary>
	/// A finished arena, or: pick a site near the house (not overlapping it), get what it still needs (blocks for
	/// the missing floor, the torches and campfire not yet placed), then build it with <see cref="BuildSkill"/>.
	/// </summary>
	private ISkill ResolveArena(Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		Blueprint bp = Blueprint.Arena(ctx.Ids);
		if (ArenaSite is TilePos done && ArenaReady(ctx, bp, done))
			return null;
		if (ArenaSite == null) {
			TilePos anchor = HouseSite ?? (KnownStations.TryGetValue(ctx.Ids.WorkBenchTile, out TilePos bench) ? bench : ctx.Node);
			var keepOut = new List<(TilePos, TilePos)>();
			if (HouseSite is TilePos h) {
				Blueprint house = Blueprint.SmallHouse(ctx.Ids);
				keepOut.Add((new TilePos(h.X - ArenaHouseGap, h.Y - ArenaHouseGap),
					new TilePos(h.X + house.Width - 1 + ArenaHouseGap, h.Y + house.Height - 1 + ArenaHouseGap)));
			}
			ArenaSite = SiteFinder.Find(ctx, bp, anchor, badArenaSites, keepOut);
			if (ArenaSite == null) {
				stuck = $"no arena site ({bp.Width} wide, at most {bp.MaxEdits} edits) within {bp.SearchRadius} tiles of {anchor}";
				return null;
			}
			ctx.Log.Info($"Auto: arena site {ArenaSite.Value} ({SiteFinder.Edits(ctx, bp, ArenaSite.Value)} edits)");
		}
		TilePos site = ArenaSite.Value;
		int blocks = 0;
		for (int x = 0; x < bp.Width; x++)
			for (int y = 0; y < bp.Height; y++)
				if (bp.At(x, y) == PlanCell.Block && !ctx.Grid.Get(site.X + x, site.Y + y).IsSolid())
					blocks++;
		var needs = new List<Need>();
		if (blocks > 0)
			needs.Add(new HaveItem(ctx.Ids.BuildingBlocks, blocks + BlockMargin, "building blocks"));
		foreach (var g in bp.Items.Where(it => !BuildSkill.IsPlaced(ctx, bp, site, it)).GroupBy(it => it.ItemType))
			needs.Add(new HaveItem(g.Key, g.Count(), ctx.Knowledge.ItemName(g.Key)));
		foreach (Need n in needs) {
			ISkill sub = Resolve(n, claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		return Do($"building the arena at {site}", new BuildSkill(bp, site));
	}

	private (TilePos?, TilePos?) protectedFor = (null, null);

	/// <summary>
	/// Keep what was built off limits to path moves (<see cref="BotContext.Protected"/>): every cell of the house
	/// and the arena's floor. Rebuilt only when a site changes.
	/// </summary>
	private void ProtectBuilt(BotContext ctx)
	{
		if (protectedFor == (HouseSite, ArenaSite) && ctx.Protected != null)
			return;
		protectedFor = (HouseSite, ArenaSite);
		var cells = new HashSet<TilePos>();
		if (HouseSite is TilePos h) {
			Blueprint house = Blueprint.SmallHouse(ctx.Ids);
			for (int x = 0; x < house.Width; x++)
				for (int y = 0; y < house.Height; y++)
					if (house.At(x, y) != PlanCell.None)
						cells.Add(new TilePos(h.X + x, h.Y + y));
		}
		if (ArenaSite is TilePos a) {
			Blueprint arena = Blueprint.Arena(ctx.Ids);
			for (int x = 0; x < arena.Width; x++)
				for (int y = 0; y < arena.Height; y++)
					if (arena.At(x, y) == PlanCell.Block)
						cells.Add(new TilePos(a.X + x, a.Y + y));
		}
		ctx.Protected = cells;
	}

	/// <summary>Floor solid, open cells empty (no trees), torches and campfire placed.</summary>
	public static bool ArenaReady(BotContext ctx, Blueprint bp, TilePos site)
	{
		for (int x = 0; x < bp.Width; x++)
			for (int y = 0; y < bp.Height; y++) {
				int wx = site.X + x, wy = site.Y + y;
				PlanCell c = bp.At(x, y);
				if (c == PlanCell.Block && !ctx.Grid.Get(wx, wy).IsSolid())
					return false;
				if (c == PlanCell.Clear && !bp.Items.Any(it => it.Covers(x, y)) && (ctx.Grid.Get(wx, wy).IsSolid() || ctx.World.IsTreeTrunk(wx, wy)))
					return false;
			}
		return bp.Items.All(it => BuildSkill.IsPlaced(ctx, bp, site, it));
	}

	/// <summary>The arena's columns and the feet row of a player standing on its floor, if there is one.</summary>
	public (int MinX, int MaxX, int StandY)? ArenaBounds(BotContext ctx)
	{
		if (ArenaSite is not TilePos s)
			return null;
		Blueprint bp = Blueprint.Arena(ctx.Ids);
		return (s.X, s.X + bp.Width - 1, s.Y + bp.StandRow);
	}

	private static bool InArena(TilePos n, int x0, int x1, int standY) =>
		Math.Abs(n.X - (x0 + x1) / 2) <= ArenaCentreTolerance + 1 && n.Y == standY;

	/// <summary>Defeated, or: get the summon item (recursively), wait for night, walk to the arena, then summon and fight.</summary>
	private ISkill ResolveBoss(BossDefeated b, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (ctx.World.BossDefeated(b.Npc))
			return null;
		bool bossHere = ctx.Hostiles.Any(h => h.Type == b.Npc);
		if (!bossHere) {
			ISkill sub = Resolve(new HaveItem(b.SummonItem, 1, ctx.Knowledge.ItemName(b.SummonItem)), claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
			if (!ctx.IsNight)
				return Do($"waiting for night to summon the {b.Label}", new WaitSkill("night", c => c.IsNight, NightWaitTicks));
			// The Eye leaves at dawn: seen in game, summoned late after a long fight with demon eyes, it flew off at
			// 1275/2800. Not enough night left: wait for the next one (through the day).
			if (ctx.NightTicksLeft > 0 && ctx.NightTicksLeft < MinNightForBoss)
				return Do($"too little night left for the {b.Label} ({ctx.NightTicksLeft / 60}s); waiting for the next night",
					new WaitSkill("next night", c => c.IsNight && c.NightTicksLeft >= MinNightForBoss, NightWaitTicks + 32400));
			if (ArenaBounds(ctx) is (int ax0, int ax1, int ay) && !InArena(ctx.Node, ax0, ax1, ay)) {
				var middle = new TilePos((ax0 + ax1) / 2, ay);
				return Do($"walking to the arena to summon the {b.Label}",
					new WalkToSkill(new Core.Pathfinding.GoalNearTile(middle, ArenaCentreTolerance, ctx.Physics)));
			}
			// Summon at (nearly) full life: the arena's campfire and the heart lantern bring it back up quickly.
			if (ctx.Combat.LifeFraction < SummonLifeFraction && ctx.Tick - lastRestEnd > MaxRestTicks)
				return Do($"resting to {SummonLifeFraction:P0} life before summoning the {b.Label} ({ctx.Combat.Life}/{ctx.Combat.LifeMax})",
					new WaitSkill("life", c => c.Combat.LifeFraction >= SummonLifeFraction, MaxRestTicks));
		}
		var arena = ArenaBounds(ctx);
		return Do($"fighting the {b.Label}{(arena != null ? " in the arena" : "")}",
			new BossFightSkill(b.SummonItem, b.Npc, b.Minions) { Arena = arena });
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
			float c = Cost(i, ctx);
			if (c < bestCost) {
				bestCost = c;
				best = i;
			}
		}
		return best;
	}

	/// <summary>
	/// Cheapest recipe for the item. A recipe that goes back through the item (iron bar -> iron fence -> iron bar)
	/// always costs more than the item itself, so it never wins over a real one.
	/// </summary>
	private RecipeInfo BestRecipe(int item, BotContext ctx)
	{
		RecipeInfo best = null;
		float bestCost = float.PositiveInfinity;
		foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(item)) {
			float c = RecipeCost(r, ctx, i => Cost(i, ctx));
			if (c < bestCost) {
				bestCost = c;
				best = r;
			}
		}
		return best;
	}

	/// <summary>Rough ticks to get the item (infinity = cannot be had), worked out by <see cref="SolveCosts"/> and kept for the pass.</summary>
	private float Cost(int item, BotContext ctx)
	{
		if (!costs.TryGetValue(item, out float c)) {
			SolveCosts(item, ctx);
			c = costs[item];
		}
		return c;
	}

	/// <summary>
	/// Costs of the item and everything it can depend on (ingredients, items that place missing stations), as a
	/// fixpoint: raw materials cost their gather time, a crafted item the cheapest of its recipes, repeated until
	/// nothing gets cheaper. Exact and path-independent, cycles included (a way round a cycle is never cheaper,
	/// each step costs <see cref="CraftStepCost"/>), and polynomial. The recursive estimate this replaced was
	/// exponential on the real game's recipes once its depth limit was raised: the game froze for minutes.
	/// </summary>
	private void SolveCosts(int root, BotContext ctx)
	{
		IGameKnowledge k = ctx.Knowledge;
		var items = new List<int>();
		var current = new Dictionary<int, float>();
		var todo = new Stack<int>();
		todo.Push(root);
		while (todo.Count > 0 && items.Count < MaxCostItems) {
			int i = todo.Pop();
			if (costs.ContainsKey(i) || current.ContainsKey(i))
				continue;
			ItemSource src = k.SourceOf(i);
			current[i] = src.Kind switch {
				SourceKind.Tree => TreeCost, SourceKind.Mine => MineCost, SourceKind.Hunt => HuntCost, _ => float.PositiveInfinity,
			};
			if (src.Kind != SourceKind.None)
				continue;
			items.Add(i);
			foreach (RecipeInfo r in k.RecipesFor(i)) {
				foreach (Ingredient ing in r.Ingredients)
					foreach (int a in ing.Accepts)
						todo.Push(a);
				foreach (int station in r.Stations)
					if (!KnownStations.ContainsKey(station))
						foreach (int placer in k.ItemsPlacing(station))
							if (ctx.Inventory.Count(placer) <= 0)
								todo.Push(placer);
			}
		}
		float Lookup(int i) => costs.TryGetValue(i, out float c) ? c : current.TryGetValue(i, out float d) ? d : float.PositiveInfinity;
		bool changed = true;
		for (int round = 0; changed && round <= items.Count; round++) {
			changed = false;
			foreach (int i in items) {
				float best = current[i];
				foreach (RecipeInfo r in k.RecipesFor(i))
					best = Math.Min(best, RecipeCost(r, ctx, Lookup));
				if (best < current[i] - 0.001f) {
					current[i] = best;
					changed = true;
				}
			}
		}
		foreach (var kv in current)
			costs[kv.Key] = kv.Value;
	}

	private float RecipeCost(RecipeInfo r, BotContext ctx, Func<int, float> cost)
	{
		costEvaluations++;
		float sum = 0f;
		foreach (Ingredient ing in r.Ingredients) {
			float c = float.PositiveInfinity;
			foreach (int a in ing.Accepts)
				c = Math.Min(c, cost(a));
			sum += ing.Stack * c;
		}
		sum /= Math.Max(1, r.ResultStack);
		sum += CraftStepCost; // every crafting step costs a little, so a roundabout chain never ties with a direct one
		foreach (int station in r.Stations) {
			if (KnownStations.ContainsKey(station))
				continue;
			IReadOnlyList<int> placers = ctx.Knowledge.ItemsPlacing(station);
			if (placers.Count == 0) {
				// Nothing places it (a demon altar): usable only if one exists out there.
				sum += FindUnplaceableStation(station, ctx) ? FoundStationCost : UnseenStationCost; // not seen yet: explore for it
				continue;
			}
			float c = float.PositiveInfinity;
			foreach (int i in placers)
				c = Math.Min(c, ctx.Inventory.Count(i) > 0 ? 0f : cost(i));
			sum += c;
		}
		return sum;
	}

	// ---------- helpers ----------

	/// <summary>" (needs a Demon Altar: none within 150 tiles, and nothing can place one)" when that is why.</summary>
	private string MissingStationHint(IReadOnlyList<int> items, BotContext ctx)
	{
		foreach (int i in items)
			foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(i))
				foreach (int st in r.Stations)
					if (!KnownStations.ContainsKey(st) && ctx.Knowledge.ItemsPlacing(st).Count == 0)
						return $" (needs a {ctx.Knowledge.TileName(st)}: none within {StationSearchRadius} tiles, and nothing can place one)";
		return "";
	}

	/// <summary>Walking to a station found far away, roughly.</summary>
	private const float FoundStationCost = 200f;
	/// <summary>A station nothing can place and none seen yet: findable by exploring, but costly.</summary>
	private const float UnseenStationCost = 3000f;
	private int stationExplores;
	public const int MaxStationExplores = 10; // fights interrupt explores and each restart counts
	private readonly HashSet<int> searchedFor = new();

	/// <summary>Look for a station nothing can place (once per planner reset); remember it when found.</summary>
	private bool FindUnplaceableStation(int tile, BotContext ctx)
	{
		if (KnownStations.ContainsKey(tile))
			return true;
		if (!searchedFor.Add(tile))
			return false;
		TilePos? found = ctx.World.FindTiles(tile, ctx.Node, StationSearchRadius).Cast<TilePos?>().FirstOrDefault();
		if (found == null)
			return false;
		KnownStations[tile] = found.Value;
		notedAt[tile] = ctx.Tick + 60 * 60 * 60; // far away: do not forget it before going there
		ctx.Log.Info($"Auto: found a {ctx.Knowledge.TileName(tile)} at {found.Value}");
		return true;
	}

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
		// Against where the scan was made, not where the bot is now: right after a respawn the scan is still from where it
		// died, and the work bench at the spawn counted as gone (seen in game: a new bench after every death, 60 wood).
		TilePos node = ctx.Seen.Origin ?? ctx.Node;
		foreach (int type in KnownStations.Keys.ToList())
			if (KnownStations[type].ChebyshevTo(node) <= StationSightTiles && !ctx.Seen.Stations.ContainsKey(type)
				&& !(notedAt.TryGetValue(type, out long t) && ctx.Tick - t < PlacedGraceTicks))
				KnownStations.Remove(type); // in sight but gone (broken, or never really there)
	}

	/// <summary>Stand beside (not on) the station, within <see cref="StationReach"/> - 1 so <see cref="NearStation"/> holds.</summary>
	private static WalkToSkill WalkToStation(TilePos at, BotContext ctx) =>
		new(new Core.Pathfinding.GoalNearTile(at, StationReach - 1, ctx.Physics));

	/// <summary>
	/// Vanilla Player.AdjTiles counts station tiles within 4 columns of the body's centre column and from 2 rows above
	/// the feet to 3 below (a row short of that below). After
	/// a craft failed for want of the station (<see cref="stationMissed"/>), only once a walk to it has been made.
	/// Seen in game: standing in a hole below the furnace, "near" by distance but not for the game, the bot crafted,
	/// failed and crafted again for a minute until auto mode gave up.
	/// </summary>
	private bool NearStation(BotContext ctx, int tileType)
	{
		if (stationMissed || !KnownStations.TryGetValue(tileType, out TilePos at))
			return false;
		TilePos n = ctx.Node;
		int dx = at.X - (n.X + 1), dy = at.Y - n.Y;
		return Math.Abs(dx) <= 4 && dy >= -2 && dy <= 2;
	}

	/// <summary>A craft failed because the game saw no station in range: walk up to it before the next craft.</summary>
	private bool stationMissed;
	private WalkToSkill stationWalk;

	/// <summary>The stuck reason of the last <see cref="ResolveForTest"/> (null if not stuck).</summary>
	public string ResolveForTestStuck => stuck;

	/// <summary>Resolve a single need against the current state (tests).</summary>
	public ISkill ResolveForTest(Need need, BotContext ctx)
	{
		costs.Clear();
		costEvaluations = 0;
		stuck = null;
		ISkill skill = Resolve(need, new Dictionary<int, int>(), 0, ctx);
		return skill == null ? null : AtNight(skill, ctx);
	}

	/// <summary>Hostile close enough that the brain should drop everything and fight.</summary>
	public static bool InDanger(BotContext ctx)
	{
		Core.Physics.PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		// Only hostiles we can see: one behind solid blocks cannot reach us right now (and must not interrupt a build).
		return (ctx.Combat.HasWeapon || ctx.Combat.HasRanged) && ctx.Hostiles.Any(h => ctx.Fightable(h.Id, h.DistanceTo(cx, cy), FightSkill.IgnoredDangerWithin) && h.DistanceTo(cx, cy) <= DangerRadiusTiles * 16f
			&& FightSkill.ClearShot(ctx, cx, cy, h.CenterX, h.CenterY));
	}
}
