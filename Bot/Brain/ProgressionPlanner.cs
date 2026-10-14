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
	/// <summary>
	/// Once <see cref="Count"/> was reached, still met down to this many (a stock used as it goes, like arrows). 0: always
	/// <see cref="Count"/>. Seen in the boss scenario: every arrow shot at a demon eye topped the 500 up again, until the
	/// last wood was gone and the plan sent the bot looking for trees in a world without any.
	/// </summary>
	public int RefillBelow { get; init; }
}

/// <summary>A crafting station of this tile type is placed (and remembered).</summary>
public sealed record StationPlaced(int TileType, string Label) : Need(Label);

/// <summary>The best hotbar pickaxe has at least <see cref="Power"/>; otherwise get <see cref="Item"/>.</summary>
/// <summary>A pickaxe of at least <see cref="Power"/>; when there is none, one of <see cref="Items"/> (in tiers, cheapest first).</summary>
public sealed record PickaxeAtLeast(int Power, IReadOnlyList<int> Items, string Label) : Need(Label)
{
	public PickaxeAtLeast(int power, int item, string label) : this(power, new[] { item }, label) { }
}

/// <summary>A house (<see cref="Building.Blueprint.SmallHouse"/>) that passes the game's housing check, optionally with a heart lantern hung inside.</summary>
public sealed record HouseBuilt(string Label, bool HeartLantern = false) : Need(Label);

/// <summary>A boss arena (<see cref="Building.Blueprint.Arena"/>) near the house: flat floor, open air, torches, a campfire.</summary>
public sealed record ArenaBuilt(string Label) : Need(Label);

/// <summary>The item is worn (armour).</summary>
/// <summary>
/// Wearing any of <see cref="Items"/> (the armour pieces for one slot, in tiers: iron or lead, silver or tungsten).
/// Seen in game (world_eye run 38): 114 iron for anvil, sword, pickaxe and armour, and the iron near the spawn ran out;
/// a player takes whichever ore there is.
/// </summary>
public sealed record Wearing(IReadOnlyList<int> Items, string Label) : Need(Label)
{
	public Wearing(int item, string label) : this(new[] { item }, label) { }
}

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
	/// <summary>After failing to get home at night: no new try for this long (a game hour); the night shelter instead.</summary>
	public const int StayHomeRetryTicks = 60 * 60;
	/// <summary>
	/// Defense from which the night is not spent at home: an iron or lead set (7+). Seen in game: the boss plan, with
	/// iron armour and 200 life, sat in the house all night instead of getting its last wood for the summon.
	/// </summary>
	public const int NightOutDefense = 6;
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
	private long stayHomeFailedAt = long.MinValue / 2;
	/// <summary>Labels of <see cref="HaveItem.RefillBelow"/> needs whose full count was reached (and not used up since).</summary>
	private readonly HashSet<string> stocked = new();
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
		// A house before the iron gear, like a player on day one: somewhere to spend the night (StayHomeSkill).
		if (plan != Plans.Iron)
			stages.Add(new HouseBuilt("house"));
		stages.Add(new StationPlaced(ids.AnvilTile, "anvil"));
		// The sword before the pickaxe: in a real world, mining the pickaxe's 30 ore with a copper shortsword (5 damage, a
		// point or two a hit against a yellow slime) cost 9 deaths in one run.
		if (plan != Plans.Iron)
			stages.Add(new HaveItem(Tiers(ids.IronTierBroadsword, ids.SilverTierBroadsword), 1, "iron-tier broadsword"));
		stages.Add(new PickaxeAtLeast(IronTierPickPower, Tiers(ids.IronTierPickaxe, ids.SilverTierPickaxe), "iron-tier pickaxe"));
		if (plan == Plans.Iron)
			return stages;
		stages.Add(new Wearing(Tiers(ids.IronTierHelmet, ids.SilverTierHelmet), "helmet"));
		stages.Add(new Wearing(Tiers(ids.IronTierChainmail, ids.SilverTierChainmail), "chainmail"));
		stages.Add(new Wearing(Tiers(ids.IronTierGreaves, ids.SilverTierGreaves), "greaves"));
		if (plan == Plans.Settle)
			return stages;
		stages.Add(new PrepFor(ids.EyeBoss, new LifeAtLeast(BossLife, "max life")));
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.IronTierBow, 1, "iron-tier bow")));
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.WoodenArrow, BossArrows, "arrows") { RefillBelow = BossArrows * 4 / 5 }));
		stages.Add(new PrepFor(ids.EyeBoss, new ArenaBuilt("arena")));
		stages.Add(new PrepFor(ids.EyeBoss, new HouseBuilt("heart lantern in the house", HeartLantern: true)));
		stages.Add(new BossDefeated(ids.EyeBoss, ids.SuspiciousEye, new[] { ids.EyeServant }, "Eye of Cthulhu"));
		return stages;
	}

	/// <summary>Mining for this item found none around here (more travel, and a higher cost for it in recipes).</summary>
	public void NoteFruitless(int item) => fruitlessMines[item] = fruitlessMines.GetValueOrDefault(item) + 1;

	/// <summary>The known items of a list (0 = not in this game's ids), each once.</summary>
	private static int[] Tiers(params int[] items) => items.Where(i => i > 0).Distinct().ToArray();

	public void Reset(string plan = null)
	{
		if (plan != null)
			Plan = plan;
		lastDeeperTick = long.MinValue / 2;
		travelDest = null;
		KnownStations.Clear();
		notedAt.Clear();
		stocked.Clear();
		Status = "";
		Complete = false;
		stuck = null;
		lastShelterEnd = long.MinValue / 2;
		stayHomeFailedAt = long.MinValue / 2;
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

	/// <summary>
	/// Does the plan still want a heart lantern (a life crystal goes into it)? Then one crystal is kept back from the
	/// use-on-sight habit. Seen in boss_auto: the crystal given for the lantern was eaten, and the bot explored 220 tiles
	/// for another one, lost the arena and ran out of gel.
	/// </summary>
	private bool LanternStillNeeded(BotContext ctx)
	{
		bool planned = StagesFor(Plan, ctx.Ids).Any(n => n is HouseBuilt { HeartLantern: true } || n is PrepFor { Inner: HouseBuilt { HeartLantern: true } });
		if (!planned || ctx.World.BossDefeated(ctx.Ids.EyeBoss) || ctx.Inventory.Count(ctx.Ids.HeartLantern) > 0)
			return false;
		if (HouseSite is TilePos built) {
			Blueprint bp = Blueprint.SmallHouse(ctx.Ids, heartLantern: true);
			foreach (PlanItem it in bp.Items)
				if (it.ItemType == ctx.Ids.HeartLantern && BuildSkill.IsPlaced(ctx, bp, built, it))
					return false;
		}
		return true;
	}

	public static bool LifeCrystalUseful(BotContext ctx) => ctx.Ids.LifeCrystal > 0 && ctx.Combat.LifeMaxBase > 0 && ctx.Combat.LifeMaxBase < MaxCrystalLife;

	/// <summary>The nearest life crystal (heart tile) the player has seen within <see cref="CrystalRadius"/> that can be mined.</summary>
	public static TilePos? SeenLifeCrystal(BotContext ctx)
	{
		int heart = ctx.Knowledge?.SourceOf(ctx.Ids.LifeCrystal).TileType ?? -1;
		if (heart <= 0)
			return null;
		foreach (TilePos c in ctx.World.FindTiles(heart, ctx.Node, CrystalRadius))
			// Not deeper than the gear allows (mining skips those too): seen in game, a crystal 7 rows below the limit
			// interrupted mining every second, the crystal job failing at once each time.
			// As mining itself would judge it (MineSkill.WouldMine), so the job it gets is one it takes on.
			if (ctx.World.CanMineTarget(c.X, c.Y) && MineSkill.WouldMine(ctx, ctx.Moves, c))
				return c;
		return null;
	}

	/// <summary>Chests closer than this (tiles) that are seen and not looted yet are looted before anything else.</summary>
	public const int LootRadius = 40;

	/// <summary>The nearest seen, unlocked chest not looted (or given up on) yet, within <see cref="LootRadius"/>.</summary>
	public static ChestInfo? NextChest(BotContext ctx)
	{
		foreach (ChestInfo c in ctx.World.SeenChests(ctx.Node, LootRadius))
			if (!c.Locked && !ctx.OpenedChests.Contains(c.TopLeft) && !ctx.OffLimits(c.TopLeft)
				&& c.TopLeft.Y - ChestDepthMargin <= ctx.DeepestRow)
				return c;
		return null;
	}

	/// <summary>
	/// How far below the depth limit a chest is still worth the trip. Deeper ones wait for better armour (the limit
	/// goes down with it). Seen in game: a chest 22 rows into the caverns drew the bot down in four runs, and most of
	/// those runs' deaths were around it.
	/// </summary>
	public const int ChestDepthMargin = 15;

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
		// Not a death on the way (no failure reason): the house is still reachable, and after the respawn at the spawn it
		// is a few steps off (run 52: killed just below the house, then sheltered outside all night).
		if (skill is StayHomeSkill && status == SkillStatus.Failed && !string.IsNullOrEmpty(skill.FailureReason)) {
			stayHomeFailedAt = ctx.Tick;
			ctx.Log.Info($"Auto: cannot get home ({skill.FailureReason}); sheltering where I am tonight");
		}
		if (skill is WalkToSkill { Travel: true } && status != SkillStatus.Running && ctx.Combat.Life > 0
			&& (status == SkillStatus.Succeeded || !string.IsNullOrEmpty(skill.FailureReason)))
			travelDest = null; // there, or no way there (not a death on the way)
		if (skill is MineSkill mine) {
			if (status == SkillStatus.Succeeded) {
				fruitlessMines.Remove(mine.ItemType);
				travelledFor.Remove(mine.ItemType);
			}
			else if (status == SkillStatus.Failed && mine.FailureReason.Contains("explorations"))
				NoteFruitless(mine.ItemType);
		}
		if (skill is CraftSkill && status == SkillStatus.Failed && skill.FailureReason.StartsWith(nameof(CraftResult.NeedsStation))) {
			stationMissed = true;
			ctx.Log.Info("Auto: the game saw no station in range for that craft; walking right up to it first");
		}
		if (skill is CraftSkill && status == SkillStatus.Succeeded || ReferenceEquals(skill, stationWalk) && status == SkillStatus.Succeeded)
			stationMissed = false;
		if (ReferenceEquals(skill, stationWalk) && stationWalkType >= 0) {
			if (status == SkillStatus.Succeeded)
				stationWalkFailures.Remove(stationWalkType);
			// A step that failed 3 times does not count (it is banned and the next walk routes round it): counting it
			// too rebuilt the furnace and anvil twice in one run (5 iron bars each time).
			else if (status == SkillStatus.Failed && !skill.FailureReason.Contains("same step") && !skill.FailureReason.Contains("died")) {
				int n = stationWalkFailures.TryGetValue(stationWalkType, out int f) ? f + 1 : 1;
				stationWalkFailures[stationWalkType] = n;
				if (n >= MaxStationWalkFailures && ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0) {
					// Like a player: drink a recall potion and be home, rather than rebuild the stations (an anvil is 15 ore).
					ctx.Log.Info($"Auto: no way back to the {ctx.Knowledge.TileName(stationWalkType)} ({n} walks failed); drinking a recall potion to go home");
					recallNext = true;
					stationWalkFailures.Remove(stationWalkType);
				}
				else if (n >= MaxStationWalkFailures) {
					ctx.Log.Info($"Auto: no way back to the {ctx.Knowledge.TileName(stationWalkType)} at {KnownStations.GetValueOrDefault(stationWalkType)} ({n} walks failed); making a new one here");
					KnownStations.Remove(stationWalkType);
					stationWalkFailures.Remove(stationWalkType);
				}
			}
		}
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

	/// <summary>
	/// After the plan got stuck (no site, nothing places a station...): forget the given-up sites and searches so the next
	/// try starts fresh, as the brain's rest after failures does for unreachable tiles.
	/// </summary>
	public void ForgetGivenUp()
	{
		stuck = null;
		badSites.Clear();
		badArenaSites.Clear();
		siteFailures = 0;
		arenaFailures = 0;
		stationExplores = 0;
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
		if (recallNext) {
			recallNext = false;
			if (ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0)
				return Do("drinking a recall potion to get home to the stations", new UseItemSkill(ctx.Ids.RecallPotion));
		}
		// A chest in sight comes first, as for a player: healing potions, accessories, better weapons.
		if (NextChest(ctx) is ChestInfo chest)
			return Do($"looting the chest at {chest.TopLeft}", new LootChestSkill(chest.TopLeft));
		// Likewise a life crystal: mined and used on sight, whatever stage the plan is at.
		if (LifeCrystalUseful(ctx)) {
			if (ctx.Inventory.Count(ctx.Ids.LifeCrystal) > (LanternStillNeeded(ctx) ? 1 : 0))
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

	/// <summary>
	/// Where the feet stand inside the house (on the floor, by the left wall's inner side) when a house is built and
	/// passes the housing check, else null. Not the housing check's own tile: that is two rows above the floor, and a
	/// walk to within 1 of it had no end in game.
	/// </summary>
	/// <summary>
	/// The house to run to when badly hurt, if there is one within <see cref="RetreatHomeTiles"/> and getting home has not
	/// just failed. Seen in game: most deaths of the night runs came while digging in to recover, hit as it dug.
	/// </summary>
	public TilePos? HomeToRetreatTo(BotContext ctx, bool anyDistance = false) =>
		ctx.Tick - stayHomeFailedAt >= StayHomeRetryTicks && HomeInside(ctx) is TilePos home && (anyDistance || home.ChebyshevTo(ctx.Node) <= RetreatHomeTiles) ? home : null;
	/// <summary>Farther than this from the house at nightfall, the night is spent where the bot is (a long night walk across the surface killed it, run 46).</summary>
	public const int NightHomeTiles = 150;
	public const int RetreatHomeTiles = 100; // 60 left a vulture death 76 tiles out on the surface (run 45)

	private TilePos? HomeInside(BotContext ctx)
	{
		if (HouseSite is not TilePos site)
			return null;
		Blueprint bp = Blueprint.SmallHouse(ctx.Ids);
		if (ctx.World.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y) != null)
			return null;
		return new TilePos(site.X + HomeStandColumn, site.Y + bp.Height - 2);
	}

	/// <summary>Column of the house where the bot waits: inside the door, clear of the table.</summary>
	public const int HomeStandColumn = 2;

	/// <summary>Work that keeps the bot on or near the surface at night: gathering, hunting, and mining or exploring from up there.</summary>
	private static bool NightWorkOnTheSurface(ISkill skill, BotContext ctx)
	{
		// Travel too: run 48 set off across the surface for new ore at nightfall in no armour.
		// And walks up to the surface from below (run 69: the start of a trip, at night, up among the zombies and a devourer).
		if (skill is GatherWoodSkill or LootChestSkill or WalkToSkill { Travel: true } or WalkToSkill { ToSurface: true } || skill is HuntSkill h && !NocturnalAiStyles.Contains(h.AiStyle))
			return true;
		bool nearSurface = ctx.SurfaceY <= 0 || ctx.Node.Y < ctx.SurfaceY + 15;
		// Any walk on the surface, not just mining: seen in game (world_eye run 65), respawned at night by the spawn, sent
		// to the furnace below the house, killed by the zombies there five times in a row.
		return nearSurface && skill is MineSkill or ExploreSkill or WalkToSkill;
	}

	private ISkill AtNight(ISkill skill, BotContext ctx)
	{
		if (skill is HuntSkill h && NocturnalAiStyles.Contains(h.AiStyle))
			return skill; // the prey only comes out at night
		// With a house: the night is spent in it, unless the work is safely underground (zombies and demon eyes are on
		// the surface). Seen in game: a zombie killed the bot walking home across the surface at night.
		if (ctx.IsNight && ctx.Combat.Defense < NightOutDefense && ctx.Tick - stayHomeFailedAt >= StayHomeRetryTicks && HomeInside(ctx) is TilePos inside
			&& inside.ChebyshevTo(ctx.Node) <= NightHomeTiles && NightWorkOnTheSurface(skill, ctx)) {
			Status = $"night: staying in the house instead of {skill.Name} ({Status})";
			return new StayHomeSkill(inside);
		}
		// Armoured, the night's zombies and demon eyes are fought like a player would (seen in the boss scenario: iron armour
		// and 200 life, dug in for the whole night over 1 wood).
		if (!ctx.IsNight || ctx.Combat.Defense >= NightOutDefense || skill is not (GatherWoodSkill or HuntSkill) || ctx.Tick - lastShelterEnd < ShelterCooldownTicks)
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
			PickaxeAtLeast p => ctx.PickPower >= p.Power ? null : Resolve(new HaveItem(p.Items, 1, p.Label), claims, depth + 1, ctx),
			HaveItem h => ResolveItem(h, claims, depth, ctx),
			HouseBuilt hb => ResolveHouse(hb.HeartLantern, claims, depth, ctx),
			ArenaBuilt => ResolveArena(claims, depth, ctx),
			BossDefeated b => ResolveBoss(b, claims, depth, ctx),
			PrepFor pf => ctx.World.BossDefeated(pf.BossNpc) ? null : Resolve(pf.Inner, claims, depth, ctx),
			LifeAtLeast la => ctx.Combat.LifeMaxBase >= la.Life ? null
				: ctx.Inventory.Count(ctx.Ids.LifeCrystal) > 0 ? Do($"using a life crystal (max life {ctx.Combat.LifeMaxBase}/{la.Life})", new UseItemSkill(ctx.Ids.LifeCrystal))
				: Resolve(new HaveItem(ctx.Ids.LifeCrystal, 1, "life crystal"), claims, depth + 1, ctx),
			Wearing wr => wr.Items.Any(ctx.Inventory.IsEquipped) ? null
				: wr.Items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0) is int carried and > 0 ? Do($"putting on the {wr.Label}", new EquipSkill(carried))
				: Resolve(new HaveItem(wr.Items, 1, wr.Label), claims, depth + 1, ctx),
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
		if (h.RefillBelow > 0 && available >= h.Count)
			stocked.Add(h.Label);
		if (available >= h.Count || h.RefillBelow > 0 && available >= h.RefillBelow && stocked.Contains(h.Label)) {
			claims[h.Accepts[0]] = claimed + Math.Min(h.Count, available);
			return null;
		}
		stocked.Remove(h.Label);
		int more = h.Count - Math.Max(0, available);
		int item = h.Accepts.Count > 1 ? CheapestToFinish(h.Accepts, more, ctx) : Cheapest(h.Accepts, ctx);
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
				if (TravelForOre(ctx, item, name) is ISkill travel)
					return travel;
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
				if (RecallHome(ctx, KnownStations[station]) is ISkill recall)
					return recall;
				stationWalk = WalkToStation(KnownStations[station], ctx);
				stationWalkType = station;
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
			// By the work bench, unless that is far from where the bot respawns: a bench put up 120 tiles off underground (no
			// way back to the old one) left no site within reach, and auto mode stopped. Then near the spawn, like a player.
			TilePos anchor = KnownStations.TryGetValue(ctx.Ids.WorkBenchTile, out TilePos bench) ? bench : ctx.Node;
			if (ctx.SpawnTile is TilePos spawn && anchor.ChebyshevTo(spawn) > HouseNearSpawnTiles)
				anchor = spawn;
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
	/// Of several items that do (armour tiers: iron or silver), the one cheapest to finish from what is carried: the
	/// recipe expanded against the bag, only what is still missing costed. Seen in game (measured run C): costed from
	/// scratch, the choice flipped between iron and silver each time one ore's fruitless count rose, and after two game
	/// days the bot held 41 iron and 57 silver ore, enough of neither for its chainmail.
	/// </summary>
	private int CheapestToFinish(IReadOnlyList<int> items, int count, BotContext ctx)
	{
		int best = -1;
		float bestCost = float.PositiveInfinity;
		foreach (int i in items) {
			if (float.IsPositiveInfinity(Cost(i, ctx)))
				continue;
			float c = Shortfall(i, count, ctx, new Dictionary<int, int>(), 0);
			if (c < bestCost) {
				bestCost = c;
				best = i;
			}
		}
		return best;
	}

	public const int MaxShortfallDepth = 6;

	/// <summary>
	/// Rough ticks still needed for <paramref name="n"/> of the item, taking what is carried (minus what <paramref name="used"/>
	/// has already taken for this estimate) before crafting or gathering the rest.
	/// </summary>
	private float Shortfall(int item, int n, BotContext ctx, Dictionary<int, int> used, int depth)
	{
		int have = Math.Max(0, ctx.Inventory.Count(item) - used.GetValueOrDefault(item));
		int take = Math.Min(have, n);
		used[item] = used.GetValueOrDefault(item) + take;
		int missing = n - take;
		if (missing <= 0)
			return 0f;
		if (ctx.Knowledge.SourceOf(item).Kind != SourceKind.None || depth >= MaxShortfallDepth || BestRecipe(item, ctx) is not RecipeInfo r)
			return missing * Cost(item, ctx);
		int crafts = (missing + Math.Max(1, r.ResultStack) - 1) / Math.Max(1, r.ResultStack);
		float sum = crafts * CraftStepCost;
		foreach (Ingredient ing in r.Ingredients) {
			int pick = -1;
			float unit = float.PositiveInfinity;
			foreach (int a in ing.Accepts) {
				float ca = Cost(a, ctx);
				if (ca < unit) {
					unit = ca;
					pick = a;
				}
			}
			if (pick < 0)
				return float.PositiveInfinity;
			sum += Shortfall(pick, ing.Stack * crafts, ctx, used, depth + 1);
		}
		return sum;
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
				// An ore that mining keeps coming back without costs more each time, so an item with another tier (armour)
				// switches to an ore that is still there.
				SourceKind.Tree => TreeCost, SourceKind.Mine => MineCost * (1 + fruitlessMines.GetValueOrDefault(i)), SourceKind.Hunt => HuntCost, _ => float.PositiveInfinity,
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
	/// <summary>Mining jobs that ran out of places to look (explorations in a row found nothing), per item, since the last success.</summary>
	private readonly Dictionary<int, int> fruitlessMines = new();
	private readonly Dictionary<int, int> travelledFor = new();
	/// <summary>
	/// Where the current trip for new ore is going, until the bot gets there (whichever ore it was for). Seen in game
	/// (world_eye run 61): a fight stopped the trip in the surface desert, mining started there and failed at once (off
	/// limits), and the next trip went the other way: back and forth across the desert until a slime killed the bot.
	/// </summary>
	private TilePos? travelDest;
	public const int TravelArrivedTiles = 8;
	/// <summary>How far (tiles) along the surface to go to look for ore in new ground.</summary>
	public const int TravelTiles = 120;
	/// <summary>A work bench farther than this from the spawn does not anchor the house; the spawn does.</summary>
	public const int HouseNearSpawnTiles = 60;

	/// <summary>
	/// After a mining job found nothing more to mine around here, walk to new ground first: along the surface,
	/// <see cref="TravelTiles"/> one way, then the other way next time, like a player trying another cave. Seen in
	/// game: 5 iron short, the bot explored the same 50 tiles for an hour.
	/// </summary>
	public const int TravelFromSurfaceRows = 15;
	public const int MaxTravelTiles = 600;

	private ISkill TravelForOre(BotContext ctx, int item, string name)
	{
		int fails = fruitlessMines.TryGetValue(item, out int f) ? f : 0;
		if (fails == 0 && travelDest == null)
			return null;
		// A trip under way (whichever ore it was for) stays the trip, unless it has become off limits since.
		if (travelDest is TilePos pending && (pending.ChebyshevTo(ctx.Node) <= TravelArrivedTiles || ctx.OffLimits(pending) || ctx.OffLimitsBelow(pending)))
			travelDest = null;
		bool fresh = false;
		if (travelDest == null) {
			if (fails == 0 || travelledFor.TryGetValue(item, out int t) && t >= fails)
				return null;
			travelledFor[item] = fails;
			// Nowhere to go (seen in game, world_eye run 68: between a corruption and a desert, every way crosses one):
			// no trip, and no recall potion drunk for one either (five were, in a minute, each followed by mining from the spawn).
			// Instead: back to the spawn to look from there, and from there deeper (run 70: auto mode stopped, every job failing).
			if (ChooseTravelDest(ctx, fails) is not TilePos chosen)
				return NoTripPossible(ctx, name);
			travelDest = chosen;
			fresh = true;
		}
		TilePos dest = travelDest.Value;
		// Far from the spawn, the trip starting there, with a recall potion: drink it, as a player would, rather than a long
		// walk out. Seen in game (world_eye run 64): the walk back to the spawn from row 408 followed caves east into the
		// desert, where antlion swarmers killed the bot.
		if (ctx.SpawnTile is TilePos home && home.ChebyshevTo(ctx.Node) > RecallDistance && dest.ChebyshevTo(ctx.Node) > dest.ChebyshevTo(home) + RecallDistance / 2
			&& ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0)
			return Do($"no more {name} found around here; drinking a recall potion to start the trip to {dest} from the spawn ({home.ChebyshevTo(ctx.Node)} tiles away)",
				new UseItemSkill(ctx.Ids.RecallPotion));
		// Up to the surface first, then along it, as a player travels: from underground the trip went through the caverns
		// below the depth limit and a poison spit killed the bot there (world_eye run 53).
		if (ctx.SurfaceY > 0 && ctx.Node.Y > ctx.SurfaceY + TravelFromSurfaceRows && SurfaceAt(ctx.Grid, ctx.Node.X) is TilePos up && up.Y < ctx.Node.Y) {
			// Out of somewhere off limits, or under a desert's or evil biome's ground: back to the spawn instead. Seen in game
			// (world_eye run 61): "up to the surface" from the underground desert's edge, four times, each interrupted by a
			// fight and started again from wherever the bot was, drifted 100 tiles into the desert, where a charger killed it.
			if ((ctx.OffLimits(ctx.Node) || ctx.BiomeGround(up) || ctx.OffLimitsBelow(up)) && ctx.SpawnTile is TilePos spawn && spawn.ChebyshevTo(ctx.Node) > 8)
				return Do($"no more {name} found around here; out of here and back to the spawn at {spawn} before travelling",
					new WalkToSkill(new Core.Pathfinding.GoalNearTile(spawn, 4, ctx.Physics)) { Quiet = true, ToSurface = true });
			return Do($"no more {name} found around here; up to the surface before travelling, at {up}",
				new WalkToSkill(new Core.Pathfinding.GoalNearTile(up, 4, ctx.Physics)) { Quiet = true, ToSurface = true });
		}
		return Do($"no more {name} found around here; {(fresh ? "travelling" : "on")} to new ground at {dest}",
			new WalkToSkill(new Core.Pathfinding.GoalNearTile(dest, 4, ctx.Physics)) { Quiet = true, Travel = true });
	}

	/// <summary>Least time (2 game hours) between steps deeper (<see cref="BotContext.DepthBonus"/>).</summary>
	public const int DeeperEveryTicks = 2 * 3600;
	private long lastDeeperTick = long.MinValue / 2;

	/// <summary>Farther than this (tiles) from the spawn with nowhere to travel: walk back to it first.</summary>
	public const int NoTripBackTiles = 30;

	private ISkill NoTripPossible(BotContext ctx, string name)
	{
		// Deeper each time (wherever the bot is: mining from the spawn wanders far before it fails), but a step at a time:
		// iron and silver each ran out at once and the bonus went from 25 to 50 rows within seconds (world_eye run 72).
		if (ctx.DepthBonus < BotContext.MaxDepthBonus && ctx.Tick - lastDeeperTick >= DeeperEveryTicks) {
			lastDeeperTick = ctx.Tick;
			ctx.DepthBonus += BotContext.DepthBonusStep;
			ctx.ExploresWithoutOre.Clear();
			ctx.Log.Info($"Auto: no more {name} within reach and nowhere to travel; going {ctx.DepthBonus} rows deeper than the gear alone allows (down to row {ctx.DeepestRow})");
		}
		if (ctx.SpawnTile is TilePos spawn && spawn.ChebyshevTo(ctx.Node) > NoTripBackTiles) {
			if (ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0 && spawn.ChebyshevTo(ctx.Node) > RecallDistance)
				return Do($"no more {name} found around here and nowhere to travel; drinking a recall potion to look again from the spawn",
					new UseItemSkill(ctx.Ids.RecallPotion));
			return Do($"no more {name} found around here and nowhere to travel; back to the spawn at {spawn} to look from there",
				new WalkToSkill(new Core.Pathfinding.GoalNearTile(spawn, 4, ctx.Physics)) { Quiet = true, ToSurface = true });
		}
		return null;
	}

	/// <summary>
	/// Where to go for new ore after <paramref name="fails"/> fruitless mining jobs: out from the spawn, farther each time
	/// (east 120, west 120, east 240...), not from wherever the bot is (from the bot, east then west went straight back to the
	/// ground just searched). Not onto somewhere off limits, nor onto or across a desert's or evil biome's ground (run 56: the
	/// spawn between a corruption and a desert, and every trip landed in one of them), nor above known off-limits ground
	/// (run 60: the surface by the spawn, over the underground desert's edge). Null if nowhere.
	/// </summary>
	private static TilePos? ChooseTravelDest(BotContext ctx, int fails)
	{
		int dir = fails % 2 == 1 ? 1 : -1;
		int originX = ctx.SpawnTile?.X ?? ctx.Node.X;
		int reach = Math.Min(TravelTiles * ((fails + 1) / 2), MaxTravelTiles); // not across the world (1,900 tiles in run 47)
		for (int r = reach; r <= MaxTravelTiles; r += TravelTiles)
			foreach (int d in new[] { dir, -dir })
				if (SurfaceAt(ctx.Grid, originX + d * r) is TilePos c && !ctx.OffLimits(c) && !ctx.BiomeGround(c) && !ctx.OffLimitsBelow(c) && !RouteCrossesBiome(ctx, originX, c.X))
					return c;
		return null;
	}

	/// <summary>Surface samples (tiles apart) checked along a trip's way.</summary>
	public const int RouteSampleTiles = 15;

	/// <summary>
	/// Does the way along the surface from <paramref name="fromX"/> to <paramref name="toX"/> cross a desert's or evil biome's
	/// ground (or somewhere off limits) the gear is too weak for? Seen in game (world_eye run 67): a trip west went across the
	/// corruption, and the bot was knocked into a chasm there by an eater of souls and fell 66 tiles.
	/// </summary>
	private static bool RouteCrossesBiome(BotContext ctx, int fromX, int toX)
	{
		// Armed (a broadsword), the surface of a desert or a corruption is crossed by day like a player crosses it: its
		// danger is underground (antlions, chasms), and the trip stays on the surface. Seen in game (measured run C): with
		// the route banned both ways, "nowhere to travel" 18 times while the ore round the spawn was gone (user,
		// 2026-10-14: solve the problem, don't ban the place). Where the bot was killed lately still counts.
		bool armed = ctx.Combat.WeaponDamage >= BotContext.GoodWeaponDamage;
		int dir = Math.Sign(toX - fromX);
		for (int x = fromX + dir * RouteSampleTiles; dir != 0 && (toX - x) * dir > 0; x += dir * RouteSampleTiles)
			if (SurfaceAt(ctx.Grid, x) is TilePos c && (armed ? ctx.InDangerZone(c) : ctx.BiomeGround(c) || ctx.OffLimits(c)))
				return true;
		return false;
	}

	/// <summary>The first spot to stand on, coming down from the sky in column <paramref name="x"/>; null if none.</summary>
	private static TilePos? SurfaceAt(ITileGrid g, int x)
	{
		for (int y = 40; y < 900; y++)
			if (g.Get(x, y + 1).CanStandOn() && Core.Pathfinding.Body.IsStandable(g, new TilePos(x, y)))
				return new TilePos(x, y);
		return null;
	}

	private bool stationMissed;
	private WalkToSkill stationWalk;
	private int stationWalkType = -1;
	/// <summary>A station at home farther than this (tiles) is reached by a recall potion if one is carried.</summary>
	public const int RecallDistance = 50;

	/// <summary>
	/// Far from a station that stands by the spawn, with a recall potion in the bag: drink it, like a player, instead of
	/// a long walk back. Seen in game: the walk home from the mine took 4 game minutes of 3-second plans, every run.
	/// </summary>
	private ISkill RecallHome(BotContext ctx, TilePos station)
	{
		if (ctx.Ids.RecallPotion <= 0 || ctx.Inventory.Count(ctx.Ids.RecallPotion) <= 0 || ctx.SpawnTile is not TilePos spawn)
			return null;
		if (station.ChebyshevTo(ctx.Node) <= RecallDistance || station.ChebyshevTo(spawn) > RecallDistance / 2)
			return null;
		return Do($"drinking a recall potion to get home to the stations ({station.ChebyshevTo(ctx.Node)} tiles away)", new UseItemSkill(ctx.Ids.RecallPotion));
	}

	/// <summary>Set when the way back to the stations is lost and a recall potion is carried: drink it next.</summary>
	private bool recallNext;

	/// <summary>Drink a recall potion as the next thing, if one is carried (false if none).</summary>
	public bool RequestRecall(BotContext ctx)
	{
		if (ctx.Ids.RecallPotion <= 0 || ctx.Inventory.Count(ctx.Ids.RecallPotion) <= 0)
			return false;
		recallNext = true;
		return true;
	}
	private readonly Dictionary<int, int> stationWalkFailures = new();
	/// <summary>
	/// Failed walks to a known station before it is forgotten and a new one made where the bot is, like a player who
	/// cannot find the way back. Seen in game: after a fight 57 tiles below the furnace, three searches of 40,000 nodes
	/// (3 s each) found no path, again and again.
	/// </summary>
	public const int MaxStationWalkFailures = 2;

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
	/// <param name="toFight">Only hostiles a fight would take on (not below the depth limit): for starting a fight.</param>
	public static bool InDanger(BotContext ctx, bool toFight = false)
	{
		Core.Physics.PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		// Only hostiles we can see: one behind solid blocks cannot reach us right now (and must not interrupt a build).
		// Nor ones the fight would not take on below the depth limit: seen in game (world_eye run 66), at row 461 every walk
		// up was stopped for a fight that ended at once with nothing to fight, a few times a second for 6 game hours.
		return (ctx.Combat.HasWeapon || ctx.Combat.HasRanged) && ctx.Hostiles.Any(h => ctx.Fightable(h.Id, h.DistanceTo(cx, cy), FightSkill.IgnoredDangerWithin) && h.DistanceTo(cx, cy) <= DangerRadiusTiles * 16f
			&& (!toFight || FightSkill.NotTooDeep(ctx, h, h.DistanceTo(cx, cy)))
			&& (!FightSkill.InWater(ctx, h) || h.Id == ctx.RecentAttacker)
			&& FightSkill.ClearShot(ctx, cx, cy, h.CenterX, h.CenterY));
	}
}
