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

/// <summary>
/// A boss arena (<see cref="Building.Blueprint.Arena"/>): flat floor, open air, torches, a campfire. Near the house (the Eye
/// of Cthulhu's), or with <see cref="InEvilBiome"/> on the evil biome's surface, where the Corruption zone holds at its middle
/// (the Eater of Worlds': Worm Food works only in the zone, Player.cs:43462, and the worm dives and despawns once no player
/// is in it, NPC.cs:55436-55481; docs M10_SPEC.md G9).
/// </summary>
public sealed record ArenaBuilt(string Label, bool InEvilBiome = false) : Need(Label)
{
	/// <summary>
	/// Skeletron's arena: by the Dungeon's entrance, on the side of the Old Man the entrance opens to, with the sky open over it
	/// (<see cref="Building.Blueprint.SkyRows"/>), raised over the lake there if need be (docs M11_SPEC.md section 3d, S4).
	/// </summary>
	public bool AtDungeon { get; init; }
}

/// <summary>
/// The Dungeon found: its Old Man seen, and where he is remembered (docs M11_SPEC.md section 1c, S2). Looked for along the
/// surface, on the side of the world the snow is on (or away from the jungle), as world generation puts them (WorldGen.cs:8209-8259).
/// </summary>
public sealed record DungeonFound(string Label) : Need(Label);

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

/// <summary>The world records this boss as defeated: get its summon item, wait for night if it wants one, summon and fight.</summary>
public sealed record BossDefeated(int Npc, int SummonItem, int[] Minions, string Label) : Need(Label)
{
	/// <summary>
	/// Summoned only at night, and gone at dawn: the Eye of Cthulhu (its summon item does nothing by day, and it leaves at
	/// dawn, measured in game at 1275/2800). The Eater of Worlds is not: Worm Food has no time condition (Player.cs:43462)
	/// and it has no daytime despawn (NPC.cs:54533, docs M10_SPEC.md section 4).
	/// </summary>
	public bool NightOnly { get; init; } = true;
	/// <summary>
	/// Summoned only in the evil biome's zone (Worm Food: Player.ZoneCorrupt, Player.cs:43462), and fought there: away from
	/// it the worm dives and despawns (NPC.cs:55436). The arena is used only if it lies in the zone.
	/// </summary>
	public bool InEvilBiome { get; init; }
	/// <summary>
	/// Summoned by the Old Man's "Curse" chat button, there only at night (Main.cs:51819-51825), and fought by the Dungeon:
	/// Skeletron (docs M11_SPEC.md section 2, S5-S7). <see cref="SummonItem"/> is 0.
	/// </summary>
	public bool ByOldMan { get; init; }
	/// <summary>
	/// Summoned by the Guide Voodoo Doll (<see cref="SummonItem"/>) thrown into the Underworld's lava from the hellbridge's far end,
	/// and fought along the bridge: the Wall of Flesh (docs M12_SPEC.md S5-S7).
	/// </summary>
	public bool ByVoodooDoll { get; init; }
	/// <summary>
	/// Fought again though beaten, for its drops (the Eater of Worlds' Shadow Scales for the Nightmare Pickaxe, the Brain's Tissue
	/// Samples for the Deathbringer): the need that asks is the item's, met once the bag holds it (<see cref="ProgressionPlanner.BossDropOf"/>).
	/// </summary>
	public bool Again { get; init; }
}

/// <summary>A town NPC of this type is alive in the world (the map shows his head): the Guide, without whom the doll is wasted (docs M12_SPEC.md S2).</summary>
public sealed record TownNpcAlive(int Npc, string Label) : Need(Label);

/// <summary>Standing on the hellbridge's row in the Underworld, down a hellevator from the house (docs M12_SPEC.md S3).</summary>
public sealed record UnderworldReached(string Label) : Need(Label);

/// <summary>The hellbridge built, its length the plan's (<see cref="ProgressionPlanner.BridgeLength"/>; docs M12_SPEC.md S4).</summary>
public sealed record HellbridgeBuilt(string Label) : Need(Label);

/// <summary>Blocks for what is left of the hellbridge to build, and a margin (docs M12_SPEC.md S1: L + 100).</summary>
public sealed record BridgeBlocks(string Label) : Need(Label)
{
	/// <summary>
	/// At most this many blocks asked for at a time (0: all the bridge still takes): the expedition errand gets the Underworld's ash
	/// a stretch at a time while it builds, not 3,900 blocks before the first one is laid (WallOfFleshErrand).
	/// </summary>
	public int Batch { get; init; }
}

/// <summary>The boss's loot picked up where it died (the Wall of Flesh's brick box, docs M12_SPEC.md S8): moot until it is defeated.</summary>
public sealed record BossLoot(int Npc, string Label) : Need(Label);

/// <summary>Preparation for a boss (life, bow, arrows): moot once that boss is defeated.</summary>
public sealed record PrepFor(int BossNpc, Need Inner) : Need(Inner.Label);

/// <summary>
/// <see cref="Inner"/> (an item) only as far as what the bag holds makes it, crafting down its recipes, never by gathering:
/// "the better bow when buildable". The Demon Bow from the demonite the Eye dropped (8 bars of 3 ore, Recipe.cs:10260 and
/// 10250); unholy arrows from the worm teeth that devourers drop when the hunt meets one (5 wooden arrows and a tooth make 5,
/// Recipe.cs:2276). Met when the count is in or no more can be made from the bag.
/// </summary>
public sealed record FromBag(HaveItem Inner) : Need(Inner.Label);

/// <summary>
/// <see cref="Inner"/> on the surface only, before the Wall of Flesh's descent: moot once the bot has stood on the hellbridge's row
/// or is in the Underworld (a run resumed down there). The work is the surface's (a hunt in the evil biome, a craft at the house's
/// anvil) and there is no way back up but a death (docs M12_SPEC.md section 1b: no rope, no mirror, the recall potions drunk).
/// </summary>
public sealed record BeforeTheUnderworld(Need Inner) : Need(Inner.Label);

/// <summary>
/// <see cref="Inner"/> worked on for <see cref="MaxTicks"/> at most, then let go for a game day (logged): a comfort the plan is
/// better with but goes on without, as more max life before a boss when the world's crystals near home are gone.
/// </summary>
public sealed record BestEffort(Need Inner, int MaxTicks) : Need(Inner.Label);

/// <summary>
/// A step toward a later stage, taken in the plan's order (the summon's parts one by one: the chunks, the powder, then the
/// Worm Food the boss stage summons with): moot once any of <see cref="Made"/> is carried (what the step's things go into),
/// and claiming nothing, so the later stage that uses them still counts them as there.
/// </summary>
public sealed record StepToward(IReadOnlyList<(int Item, int Count)> Made, Need Inner) : Need(Inner.Label);

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
public sealed partial class ProgressionPlanner : Errands.IErrandSupport
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
	/// <summary>Rows below the surface (or the spawn) where the night's mining starts.</summary>
	public const int NightMineRows = 25;
	/// <summary>After the walk down for the night's mining fails, the house for this long (rest of the night) instead.</summary>
	public const int NightMineRetryTicks = 9 * 3600;
	private long nightMineFailedAt = long.MinValue / 2;
	/// <summary>Pickaxe power of the iron tier (Iron Pickaxe 40, Lead Pickaxe 43).</summary>
	public const int IronTierPickPower = 40;
	/// <summary>Max life to have before a boss: 5 life crystals (also what vanilla wants before the Eye spawns on its own).</summary>
	public const int BossLife = 200;
	/// <summary>Arrows to carry into a boss fight (the Eye took about 250 in testing, misses included).</summary>
	public const int BossArrows = 500;
	/// <summary>
	/// Arrows (Wooden, besides the Unholy ones) to carry into the Eater of Worlds fight. Seen in game (measured run M10s, the
	/// worm defeated): 319 arrows took the worm from 10050 to 4167, about 18 a arrow, so the whole worm takes about 550;
	/// with 498 the fight ran "out of arrows" with 2 segments left. Twice that, for the misses of a longer fight (25 a craft
	/// of 1 Wood and 1 Stone Block, Recipe.cs).
	/// </summary>
	public const int WormArrows = 1000;
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
		/// <summary>
		/// Boss (the Eye first, if not done), then the evil biome's boss defeated: the Eater of Worlds by Worm Food (Milestone
		/// 10, docs M10_SPEC.md: the Worm Food path).
		/// </summary>
		public const string EvilBoss = "evilboss";
		/// <summary>
		/// Boss (the Eye first, if not done), then Skeletron, summoned at the Old Man by the Dungeon (Milestone 11, docs
		/// M11_SPEC.md section 6). Not after the Eater of Worlds: nothing in Skeletron's chain needs it.
		/// </summary>
		public const string Skeletron = "skeletron";
		/// <summary>
		/// Boss (the Eye first, if not done), then the Wall of Flesh (Milestone 12, docs M12_SPEC.md section 9): the prep, the Guide
		/// alive, a hellevator down to the Underworld, a hellbridge over its lava, a Guide Voodoo Doll, the summon and the fight.
		/// Needs neither Skeletron nor the Eater of Worlds.
		/// </summary>
		public const string WallOfFlesh = "wof";
		/// <summary>Just the hellbridge, from where the bot stands in the Underworld (a building block of "wof"; the synthetic scenarios).</summary>
		public const string Hellbridge = "hellbridge";
		/// <summary>
		/// A work bench and the house, rooms for the town NPCs (<see cref="NpcHousing"/>), and two NPCs moved in (<see cref="NpcsHoused"/>):
		/// the boss plans' NPC housing on its own (its self-test).
		/// </summary>
		public const string Housing = "housing";
		public static readonly string[] All = { Early, Iron, Settle, Crystals, Arena, Boss, EvilBoss, Skeletron, WallOfFlesh, Hellbridge, Housing };
	}

	/// <summary>Worm Food: 30 Vile Powder + 15 Rotten Chunk at a Demon Altar (Recipe.cs:14407-14413).</summary>
	public const int WormFoodPowder = 30, WormFoodChunks = 15;
	/// <summary>Unholy arrows to carry into the worm fight (spec G3: 150 or more; they pierce 5, Projectile.cs:954).</summary>
	public const int UnholyArrows = 150;

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
	/// <summary>
	/// <see cref="HaveItem.RefillBelow"/> needs whose full count was reached (and not used up since), by label and count: the
	/// Eye's 500 "arrows" and the worm's 1000 "arrows" are two needs, and the worm's unmet one (the day's work before the Eye's
	/// night, <see cref="DayWorkBeforeTheNight"/>) must not take the Eye's met one off the list.
	/// </summary>
	private readonly HashSet<string> stocked = new();
	private static string StockKey(HaveItem h) => $"{h.Label}/{h.Count}";
	/// <summary>Set while a pass resolves a night-only need by day: its wait for night, or the walk to the arena to wait there.</summary>
	private bool nightOnlyByDay;
	/// <summary>
	/// The last pass chose day work over a night-only stage waiting for dark (<see cref="DayWorkBeforeTheNight"/>): at nightfall
	/// the brain plans again, whatever the work, so that the night-only stage goes first again.
	/// </summary>
	public bool DayWorkBeforeNight { get; private set; }
	private long lastRestEnd = long.MinValue / 2;
	private readonly List<TilePos> badSites = new();
	private int siteFailures;
	public const int MaxBuildFailuresPerSite = 2;
	/// <summary>Spare building blocks on top of the missing frame cells: walking there may pillar or bridge with them too.</summary>
	public const int BlockMargin = 10;

	/// <summary>Top-left tile of the house being built or already built (kept across re-plans).</summary>
	public TilePos? HouseSite { get; private set; }
	/// <summary>
	/// One boss arena's memory, kept across re-plans: its site (top-left tile, being built or built), the sites and cells
	/// given up, the builds failed in a row, and "no site to be had" (the boss fought on open ground). The Eye's arena by the
	/// house, and the Eater of Worlds' in the corruption (<see cref="ArenaBuilt.InEvilBiome"/>): the same arena code, each with
	/// its own memory.
	/// </summary>
	private sealed class ArenaSlot
	{
		public TilePos? Site;
		public readonly List<TilePos> BadSites = new(), BadCells = new();
		public int Failures;
		public bool OnOpenGround;
		/// <summary>The arena's width (Skeletron's is wider, Blueprint.DungeonArenaWidth, when a site that long is found).</summary>
		public int Width = Blueprint.ArenaWidth;

		public void ForgetGivenUp()
		{
			BadSites.Clear();
			BadCells.Clear();
			OnOpenGround = false;
			Failures = 0;
		}

		public void Reset()
		{
			Site = null;
			ForgetGivenUp();
		}
	}

	private readonly ArenaSlot eyeArena = new(), evilArena = new(), dungeonArena = new() { Width = Blueprint.DungeonArenaWidth };

	/// <summary>Top-left tile of the boss arena by the house, being built or already built.</summary>
	public TilePos? ArenaSite { get => eyeArena.Site; private set => eyeArena.Site = value; }
	/// <summary>
	/// No arena site near the house nor farther out: the boss is fought on open ground, as the fight does without an arena
	/// (<see cref="BossFightSkill.Arena"/> null). Kept until the given-up sites are forgotten (<see cref="ForgetGivenUp"/>).
	/// </summary>
	public bool ArenaOnOpenGround => eyeArena.OnOpenGround;
	/// <summary>Top-left tile of the Eater of Worlds' arena in the corruption (built for it, or the Eye's lying in the zone).</summary>
	public TilePos? EvilArenaSite => evilArena.Site;
	/// <summary>No arena site in the corruption either: the worm is fought on its open ground (<see cref="EvilGround"/>).</summary>
	public bool EvilArenaOnOpenGround => evilArena.OnOpenGround;
	/// <summary>The Eater of Worlds' arena stage (<see cref="ArenaBuilt.InEvilBiome"/>).</summary>
	public const string EvilArenaLabel = "arena in the corruption";
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
	/// <summary>
	/// How long-running work is scheduled (docs DESCENT_DIAGNOSIS.md, ERRAND_DESIGN.md): <see cref="Schedulers.Restart"/>, every pass
	/// walks the plan's stages from the top, whatever was being done; <see cref="Schedulers.Operation"/>, a continuing job (the Wall's
	/// descent) owns the bot until it is done, blocked or a death, with only its remaining demand resupplied, and optional work
	/// (chests, crystals in sight) deferred; <see cref="Schedulers.Errands"/>, the stage's errand (<see cref="Errands.Errand"/>) owns
	/// the bot (the brain asks it, not the planner) until its goal holds or it is blocked, through deaths, with its packing list
	/// reserved and optional work taken by its detour cost. Survival reflexes preempt all three. Self-test: selftest.ps1 -Scheduler.
	/// The older two stay selectable until migration step 4 (docs ERRAND_DESIGN.md section 12).
	/// </summary>
	public enum Schedulers { Restart, Operation, Errands }
	public Schedulers Scheduler { get; set; } = Schedulers.Errands;
	public string Status { get; private set; } = "";
	public bool Complete { get; private set; }
	/// <summary>Set when a stage cannot be resolved (no recipe, nothing places the station...).</summary>
	public bool Stuck => stuck != null;

	/// <summary>
	/// The errand the last pass proposed for the stage it stopped at (<see cref="Schedulers.Errands"/>): the brain takes it
	/// (<see cref="TakeProposed"/>) and runs it in place of the plan until it ends.
	/// </summary>
	public Errands.Errand Proposed { get; private set; }

	public Errands.Errand TakeProposed()
	{
		Errands.Errand e = Proposed;
		Proposed = null;
		return e;
	}

	/// <summary>Shafts whose access errands ended blocked (their columns): the next errand chooses elsewhere.</summary>
	private readonly List<int> blockedShafts = new();
	/// <summary>Access errands blocked at different columns before the plan is stuck on the Underworld (policy).</summary>
	public const int MaxBlockedShafts = 3;
	/// <summary>The errands that ended and how (the failure record a later decision reads).</summary>
	public List<(string Id, string Identity, Errands.Outcome Outcome, long Tick)> ErrandOutcomes { get; } = new();

	/// <summary>The brain runs the errand: the planner's memory follows it (the shaft's column for the bridge), and its status line too.</summary>
	public void AdoptErrand(Errands.Errand e)
	{
		if (e is Errands.WallOfFleshErrand a)
			shaftX = a.ShaftX;
		Status = $"errand {e.Id}: {e.Status} [{e.Describe()}]";
		Complete = false;
	}

	/// <summary>
	/// The errand is over: its outcome recorded; a Wall of Flesh errand blocked on the way down leaves its column out of the next
	/// one's choice, and one blocked later (the bridge, the fight) twice with the same reason leaves the plan stuck on it.
	/// </summary>
	public void OnErrandEnded(Errands.Errand e, Errands.Outcome outcome, BotContext ctx)
	{
		ErrandOutcomes.Add((e.Id, e.Identity, outcome, ctx.Tick));
		if (e is Errands.WallOfFleshErrand a) {
			if (outcome.Kind == Errands.OutcomeKind.Blocked && a.Phase == Errands.WallOfFleshErrand.DescendPhase) {
				blockedShafts.Add(a.ShaftX);
				shaftX = null;
			}
			else if (a.Landed is TilePos landed && !bridgeKnown)
				KnowBridge(ctx, landed, a.StandRow + 1);
		}
	}

	/// <summary>The last outcome recorded for an errand id, if any.</summary>
	private Errands.Outcome? LastOutcome(string id)
	{
		for (int i = ErrandOutcomes.Count - 1; i >= 0; i--)
			if (ErrandOutcomes[i].Id == id)
				return ErrandOutcomes[i].Outcome;
		return null;
	}

	/// <summary>The same blocked outcome recorded twice in a row for the id: no sense in a third try without a change.</summary>
	private bool BlockedTwice(string id)
	{
		int n = 0;
		string reason = null;
		for (int i = ErrandOutcomes.Count - 1; i >= 0 && n < 2; i--) {
			if (ErrandOutcomes[i].Id != id)
				continue;
			if (ErrandOutcomes[i].Outcome.Kind != Errands.OutcomeKind.Blocked)
				return false;
			if (reason != null && ErrandOutcomes[i].Outcome.Reason != reason)
				return false;
			reason = ErrandOutcomes[i].Outcome.Reason;
			n++;
		}
		return n == 2;
	}

	/// <summary>
	/// <see cref="Errands.IErrandSupport.ResolveNeeds"/>: the errand's needs (its packing list, its remaining demand) resolved as a
	/// stage's are, with fresh claims; the first unmet one's skill, or null when all are met; <paramref name="blocked"/> for one that
	/// cannot be met at all.
	/// </summary>
	public ISkill ResolveNeeds(IEnumerable<Need> needs, BotContext ctx, out string blocked)
	{
		costs.Clear();
		costEvaluations = 0;
		stuck = null;
		blocked = null;
		var claims = new Dictionary<int, int>();
		ErrandAsking = true;
		try {
			foreach (Need n in needs) {
				ISkill s = Resolve(n, claims, 0, ctx);
				if (stuck != null) {
					blocked = $"{n.Label}: {stuck}";
					stuck = null;
					return null;
				}
				if (s != null)
					return s;
			}
			return null;
		}
		finally {
			ErrandAsking = false;
		}
	}

	/// <summary>Set while an errand's needs are resolved: the resolvers answer with skills, never with a proposal of the errand itself.</summary>
	public bool ErrandAsking { get; private set; }

	public static IReadOnlyList<Need> StagesFor(string plan, GameIds ids)
	{
		if (plan == Plans.Crystals)
			return new Need[] { new LifeAtLeast(BossLife, "max life") };
		if (plan == Plans.Arena)
			return new Need[] { new ArenaBuilt("arena") };
		if (plan == Plans.Hellbridge)
			return new Need[] { new BridgeBlocks("bridge blocks"), new UnderworldReached("the Underworld"), new HellbridgeBuilt("hellbridge") };
		if (plan == Plans.Housing)
			return new Need[] { new StationPlaced(ids.WorkBenchTile, "work bench"), new HouseBuilt("house"), new NpcHousing("NPC housing"), new NpcsHoused(2, "two town NPCs moved in") };
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
		// Settled, the town NPCs' rooms, as a player houses the Guide, the Merchant and the Nurse before the first boss: one room
		// each for those without a home and those who can come, one spare at least. Checked on every pass (a survey kept for
		// NpcSurveyTicks), so a room is added when one moves in; never a stop: what it cannot do is left for a day.
		stages.Add(new NpcHousing("NPC housing"));
		// The bow and arrows before the crystals: the armoured nights go to the summon's lenses (NightPartsForTheSummon), and
		// demon eyes, fliers, are shot, not reached with a sword. Seen in game (measured run U5): the bow came at d9 after
		// max life 200; 46 hits taken from demon eyes on the night hunts, most of them last seen at full life, 1 lens of 6.
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.IronTierBow, 1, "iron-tier bow")));
		stages.Add(new PrepFor(ids.EyeBoss, new HaveItem(ids.WoodenArrow, BossArrows, "arrows") { RefillBelow = BossArrows * 4 / 5 }));
		stages.Add(new PrepFor(ids.EyeBoss, new LifeAtLeast(BossLife, "max life")));
		stages.Add(new PrepFor(ids.EyeBoss, new ArenaBuilt("arena")));
		stages.Add(new PrepFor(ids.EyeBoss, new HouseBuilt("heart lantern in the house", HeartLantern: true)));
		stages.Add(new BossDefeated(ids.EyeBoss, ids.SuspiciousEye, new[] { ids.EyeServant }, "Eye of Cthulhu") { NightOnly = true });
		if (plan == Plans.EvilBoss)
			stages.AddRange(WorldEvilBossStages(ids));
		if (plan == Plans.Skeletron)
			stages.AddRange(SkeletronStages(ids));
		if (plan == Plans.WallOfFlesh)
			stages.AddRange(WallOfFleshStages(ids));
		return stages;
	}

	/// <summary>Arrows into the Skeletron fight, wooden and unholy together (spec S1: 500, or 350 unholy; about 310-450 at 60 % hits).</summary>
	public const int SkeletronArrows = BossArrows;
	/// <summary>Healing potions into the Skeletron fight (spec S1: at least 5; at most 3 drunk in a 2-3 minute fight, 60 s sickness).</summary>
	public const int SkeletronPotions = 5;

	/// <summary>
	/// After the Eye: Skeletron (docs M11_SPEC.md section 6), in the spec's order.
	/// <list type="number">
	/// <item>S1: a bow (the Demon Bow when there is one), unholy arrows from the bag, 500 arrows in all (refilled below 4/5),
	/// max life 200, healing potions and a recall potion from the bag (the dawn and Guardian escape);</item>
	/// <item>S2: the Dungeon found (<see cref="DungeonFound"/>: the Old Man seen);</item>
	/// <item>S4: an arena by it (<see cref="ArenaBuilt.AtDungeon"/>);</item>
	/// <item>S5-S7: wait there for night, summon at the Old Man, fight (<see cref="BossDefeated.ByOldMan"/>).</item>
	/// </list>
	/// The Dungeon Guardian's ground (S3) is a rule of the paths (BotContext.GuardianGround) while Skeletron is up to be beaten.
	/// </summary>
	private static IEnumerable<Need> SkeletronStages(GameIds ids)
	{
		int boss = ids.SkeletronHead;
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.IronTierBow, ids.EvilBow), 1, "bow"));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.UnholyArrow, UnholyArrows, "unholy arrows")));
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), SkeletronArrows, "arrows") { RefillBelow = SkeletronArrows * 4 / 5 });
		yield return new PrepFor(boss, new LifeAtLeast(BossLife, "max life"));
		yield return new PrepFor(boss, new FromBag(new HaveItem(Tiers(ids.HealingPotion, ids.LesserHealingPotion), SkeletronPotions, "healing potions")));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.RecallPotion, 1, "recall potion")));
		yield return new PrepFor(boss, new DungeonFound("the Dungeon"));
		yield return new PrepFor(boss, new ArenaBuilt(DungeonArenaLabel) { AtDungeon = true });
		yield return new BossDefeated(boss, 0, new[] { ids.SkeletronHand }, "Skeletron") { NightOnly = true, ByOldMan = true };
	}

	/// <summary>
	/// After the Eye: the Eater of Worlds by Worm Food (docs M10_SPEC.md section 6, the Worm Food path), in the spec's order.
	/// <list type="number">
	/// <item>A bow and arrows, and max life 200, as for the Eye (its own prep stages are moot once it is down).</item>
	/// <item>(a) The Demon Bow, from the bag: the Eye's demonite (G2).</item>
	/// <item>(b) Unholy arrows, from the bag: worm teeth met on the way (G3).</item>
	/// <item>(c) 15 rotten chunks, hunted from eaters of souls in the corruption (G4).</item>
	/// <item>(f) 30 vile powder (G7), which its recipe resolves as (d) the vile mushrooms it still needs, picked from the
	/// corruption's grass (G5), then (e) a placed bottle, glass from sand at the furnace (G6), then the crafts: as stages of
	/// their own, each craft of 5 powder from 1 mushroom left the mushroom stage short again.</item>
	/// <item>(g) Worm Food at the demon altar (G8), (h) an arena on the corruption's surface (G9: built, or the Eye's if it lies
	/// in the zone), (i) summoned there, by day or night, and fought (G10).</item>
	/// </list>
	/// The summon's parts are each moot once what they go into is carried (<see cref="StepToward"/>), and all the prep once the
	/// worm is up (<see cref="BossUp"/>).
	/// </summary>
	private static IEnumerable<Need> EvilBossStages(GameIds ids)
	{
		int boss = ids.EvilBoss;
		var food = new[] { (ids.WormFood, 1) };
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.IronTierBow, ids.DemonBow), 1, "bow"));
		yield return new PrepFor(boss, new HaveItem(ids.WoodenArrow, WormArrows, "arrows") { RefillBelow = WormArrows * 4 / 5 });
		yield return new PrepFor(boss, new LifeAtLeast(BossLife, "max life"));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.DemonBow, 1, "demon bow")));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.UnholyArrow, UnholyArrows, "unholy arrows")));
		yield return new PrepFor(boss, new StepToward(food, new HaveItem(ids.RottenChunk, WormFoodChunks, "rotten chunks")));
		yield return new PrepFor(boss, new StepToward(food, new HaveItem(ids.VilePowder, WormFoodPowder, "vile powder")));
		yield return new PrepFor(boss, new StepToward(Array.Empty<(int, int)>(), new HaveItem(ids.WormFood, 1, "worm food")));
		yield return new PrepFor(boss, new ArenaBuilt(EvilArenaLabel, InEvilBiome: true));
		yield return new BossDefeated(boss, ids.WormFood, new[] { ids.EvilBossBody, ids.EvilBossTail }, "Eater of Worlds") { NightOnly = false, InEvilBiome = true };
	}

	/// <summary>The evil boss's stages for this world's evil: the Brain of Cthulhu's in a Crimson world, else the Eater of Worlds'.</summary>
	private static IEnumerable<Need> WorldEvilBossStages(GameIds ids) => ids.Crimson ? CrimsonBossStages(ids) : EvilBossStages(ids);

	/// <summary>Bloody Spine: 30 Vicious Powder + 15 Vertebrae at a Demon or Crimson Altar (Recipe.cs:14414-14420).</summary>
	public const int SpinePowder = 30, SpineVertebrae = 15;
	/// <summary>
	/// Wooden arrows to carry into the Brain of Cthulhu's fight: its 1250 life behind 20 Creepers of 100 (defence 14 and 10; NPC.cs:
	/// 7590-7622) is 460 hits of an Iron Bow's wooden arrow (13 - 7 and 13 - 5 a hit in Classic), 180 of the Tendon Bow's; at half of
	/// them landing about 900 at most: the worm's 1000 (<see cref="WormArrows"/>).
	/// </summary>
	public const int BrainArrows = WormArrows;
	/// <summary>The Brain of Cthulhu's arena stage (<see cref="ArenaBuilt.InEvilBiome"/>, in a Crimson world).</summary>
	public const string CrimsonArenaLabel = "arena in the crimson";

	/// <summary>
	/// After the Eye, in a Crimson world (<see cref="GameIds.Crimson"/>): the Brain of Cthulhu by a Bloody Spine, the Worm Food path's
	/// twin, every part verified in the decompile (BrainPattern for the fight):
	/// <list type="number">
	/// <item>A bow and arrows, and max life 200, as for the Eye.</item>
	/// <item>(a) The Tendon Bow, from the bag: 8 bars of the Eye's crimtane (30-90 ore in a Crimson world, ItemDropDatabase.cs:496; a
	/// bar of 3 ore at a furnace, Recipe.cs:10323; the bow at an anvil, Recipe.cs:10333). Damage 19 against the Iron Bow's 8.</item>
	/// <item>(b) 15 vertebrae, hunted from crimeras in the crimson (1 in 3, ItemDropDatabase.cs:1111; a crimera flies as an eater of
	/// souls does, AI style 5, NPC.cs:6321-6330): about 45 kills. They spawn over the crimson's ground only (the tile under the spawn
	/// crimson grass, crimstone... NPC.cs:89569), 7 in 16 of its surface spawns by day; face monsters (7 in 16) and blood crawlers drop
	/// them too, and are fought as they come (NPC.cs:89603-89616).</item>
	/// <item>(c) 30 vicious powder, which its recipe resolves as the vicious mushrooms it still needs (crimson plants of frame 270 on
	/// crimson grass, WorldGen.cs:54379 and 59644-59648) and a placed bottle (Recipe.cs:2512-2516).</item>
	/// <item>(d) The Bloody Spine at the altar (a crimson altar is a Demon Altar tile, 26, style 1: WorldGen.cs:10689).</item>
	/// <item>(e) Unholy arrows, from the bag: the vertebrae left over make them (5 wooden arrows and one, Recipe.cs:2283-2290). After
	/// the spine, not before (the corruption's place for them): from the bag first they took the vertebrae the spine wants.</item>
	/// <item>(f) An arena on the crimson's surface (the Corruption's arena code: where the Crimson zone holds at its middle, as the
	/// spine works only there, Player.cs:43549-43552, and the Brain sinks away once we leave it, NPC.cs:33385-33397), (g) summoned
	/// there by day or night (no time rule in the AI nor the item) and fought (<see cref="BossFightSkill.IsBrain"/>).</item>
	/// </list>
	/// The spine's parts are each moot once it is carried (<see cref="StepToward"/>), and all the prep once the Brain is up (<see cref="BossUp"/>).
	/// </summary>
	private static IEnumerable<Need> CrimsonBossStages(GameIds ids)
	{
		int boss = ids.BrainOfCthulhu;
		var spine = new[] { (ids.BloodySpine, 1) };
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.IronTierBow, ids.TendonBow), 1, "bow"));
		yield return new PrepFor(boss, new HaveItem(ids.WoodenArrow, BrainArrows, "arrows") { RefillBelow = BrainArrows * 4 / 5 });
		yield return new PrepFor(boss, new LifeAtLeast(BossLife, "max life"));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.TendonBow, 1, "tendon bow")));
		yield return new PrepFor(boss, new StepToward(spine, new HaveItem(ids.Vertebrae, SpineVertebrae, "vertebrae")));
		yield return new PrepFor(boss, new StepToward(spine, new HaveItem(ids.ViciousPowder, SpinePowder, "vicious powder")));
		yield return new PrepFor(boss, new StepToward(Array.Empty<(int, int)>(), new HaveItem(ids.BloodySpine, 1, "bloody spine")));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.UnholyArrow, UnholyArrows, "unholy arrows")));
		yield return new PrepFor(boss, new ArenaBuilt(CrimsonArenaLabel, InEvilBiome: true));
		yield return new BossDefeated(boss, ids.BloodySpine, new[] { ids.Creeper }, "Brain of Cthulhu") { NightOnly = false, InEvilBiome = true };
	}

	/// <summary>How long a <see cref="BestEffort"/> need given up on is left alone (a game day).</summary>
	public const int BestEffortRetryTicks = 24 * 3600;
	/// <summary>Per best-effort need (its label): when work on it began, and when it was last given up on.</summary>
	private readonly Dictionary<string, (long Began, long GaveUp)> bestEffort = new();

	private ISkill ResolveBestEffort(BestEffort be, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		var (began, gaveUp) = bestEffort.GetValueOrDefault(be.Label, (long.MinValue, long.MinValue / 2));
		if (ctx.Tick - gaveUp < BestEffortRetryTicks && ctx.Tick >= gaveUp)
			return null;
		ISkill s = Resolve(be.Inner, claims, depth, ctx);
		if (s == null || stuck != null) {
			bestEffort.Remove(be.Label); // met (or stuck: the plan's own trouble, left to it)
			if (stuck != null) {
				ctx.Log.Info($"Auto: {be.Label} is not to be had now ({stuck}); the plan goes on without it");
				stuck = null;
				bestEffort[be.Label] = (long.MinValue, ctx.Tick);
			}
			return null;
		}
		if (began == long.MinValue)
			began = ctx.Tick;
		if (ctx.Tick - began > be.MaxTicks) {
			ctx.Log.Info($"Auto: {be.Label}: {be.MaxTicks / 3600} game hours on it, not met; the plan goes on without it for a day");
			bestEffort[be.Label] = (long.MinValue, ctx.Tick);
			return null;
		}
		bestEffort[be.Label] = (began, gaveUp);
		return s;
	}

	/// <summary>Mining for this item found none around here (more travel, and a higher cost for it in recipes).</summary>
	public void NoteFruitless(int item) => fruitlessMines[item] = fruitlessMines.GetValueOrDefault(item) + 1;

	/// <summary>
	/// A mining job's tier group (MineSkill.TierGroup: its ore and the Also ores its explorations looked for as well) per ore,
	/// as the last job for it had it: a trip for one is a trip for all (<see cref="TravelForOre"/>).
	/// </summary>
	private readonly Dictionary<int, int[]> tierGroups = new();
	/// <summary>Ores whose last fruitless job explored down to the depth floor (MineSkill.ExploredToFloor).</summary>
	private readonly HashSet<int> fruitlessAtFloor = new();

	/// <summary>The known items of a list (0 = not in this game's ids), each once.</summary>
	private static int[] Tiers(params int[] items) => items.Where(i => i > 0).Distinct().ToArray();

	public void Reset(string plan = null)
	{
		if (plan != null)
			Plan = plan;
		Proposed = null;
		blockedShafts.Clear();
		ErrandOutcomes.Clear();
		lastDeeperTick = long.MinValue / 2;
		crystalNoTripAt = null;
		travelDest = null;
		failedTrips.Clear();
		leaveTarget = null;
		leaveWalk = null;
		exitTarget = null;
		KnownStations.Clear();
		notedAt.Clear();
		homeStationAt.Clear();
		stationHereAt.Clear();
		stocked.Clear();
		Status = "";
		Complete = false;
		stuck = null;
		lastShelterEnd = long.MinValue / 2;
		stayHomeFailedAt = long.MinValue / 2;
		homePlaceFailedAt = long.MinValue / 2;
		homePlaceWalk = null;
		homePlace = null;
		lastRestEnd = long.MinValue / 2;
		HouseSite = null;
		ResetFoundHome();
		eyeArena.Reset();
		evilArena.Reset();
		dungeonArena.Reset();
		dungeonArena.Width = Blueprint.DungeonArenaWidth;
		oldManAt = null;
		dungeonBricksAt = null;
		OldManMet = false;
		SkeletronPrepMet = false;
		ResetWallOfFlesh();
		ResetNpcHousing();
		dungeonSideForced = null;
		dungeonSide = null;
		dungeonSideAt = long.MinValue / 2;
		dungeonSideFlips = 0;
		dungeonHopFailedAt = long.MinValue / 2;
		searchedFor.Clear();
		badSites.Clear();
		badBridges.Clear();
		bridgeFailures = 0;
		Bridges.Clear();
		evilChasms = null;
		stationExplores = 0;
		stationHeading = null;
		siteFailures = 0;
		evilGround = null;
		evilGroundAt = long.MinValue / 2;
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
		if (HouseSite == null && FoundHome is TilePos home && LanternNear(ctx, home))
			return false;
		return true;
	}

	/// <summary>
	/// The heart lantern from a crystal to spare, not one hunted for on purpose: without one in the bag the house goes on
	/// without it (a crystal mined later, on sight, still goes into it). It hangs in the house, not the arena where the boss is
	/// fought. Seen in game (measured runs U2 and U4): the lantern's crystal was looked for from d5 +10h and d7 +13h to the
	/// end of the run, the boss never summoned; and one crystal was held back from max life for it all along.
	/// </summary>
	private bool LanternFromASpareCrystal(BotContext ctx) =>
		ctx.Inventory.Count(ctx.Ids.HeartLantern) > 0 || ctx.Inventory.Count(ctx.Ids.LifeCrystal) > 0 || !LanternStillNeeded(ctx);

	public static bool LifeCrystalUseful(BotContext ctx) => ctx.Ids.LifeCrystal > 0 && ctx.Combat.LifeMaxBase > 0 && ctx.Combat.LifeMaxBase < MaxCrystalLife;

	/// <summary>Max life one life crystal adds (vanilla Player.ItemCheck_UseLifeCrystal: UseHealthMaxIncreasingItem(20) while ConsumedLifeCrystals &lt; 15, Player.cs:45568-45576).</summary>
	public const int LifePerCrystal = 20;

	/// <summary>Life crystals still to use for max life <paramref name="life"/> (at most <see cref="MaxCrystalLife"/>), at least 1.</summary>
	public static int CrystalsFor(int life, BotContext ctx) =>
		Math.Max(1, (Math.Min(life, MaxCrystalLife) - ctx.Combat.LifeMaxBase + LifePerCrystal - 1) / LifePerCrystal);

	/// <summary>
	/// Journey, life crystals not yet researched: the ones found are kept until the bag holds what research takes, and then
	/// researched in one go and duplicated to the most max life crystals give, as the user plays Journey ("I wait until I have
	/// the number I need for researching"). Used one by one, they never reach that number: seen in game (Journey test run
	/// J1, The Aggressive Site, character Nenne), every crystal found was used at once and max life went 100, 120, 160, 200
	/// with none ever researched; held, 10 crystals give 400 max life (15 uses), used, 15 crystals give the same. Not when the
	/// duplication menu gives nothing (a research mod's checklist mode): research would only use them up.
	/// </summary>
	public static bool HoldCrystalsForResearch(BotContext ctx) => ctx.Journey.Enabled && ctx.Journey.DuplicationAllowed && ctx.Journey.ResearchLeft(ctx.Ids.LifeCrystal) > 0;

	public static bool CrystalsForResearch(BotContext ctx)
	{
		int crystal = ctx.Ids.LifeCrystal;
		int left = ctx.Journey.ResearchLeft(crystal);
		// No duplication (a research mod's checklist mode): research gives none back, so one more than it takes, kept for use.
		return left > 0 && (ctx.Journey.DuplicationAllowed ? ctx.Inventory.Count(crystal) >= left : ctx.Inventory.Count(crystal) > left);
	}

	/// <summary>
	/// The nearest life crystal (heart tile) the player has seen within <see cref="CrystalRadius"/> that can be mined, the
	/// times mining gave up on it lately counted as distance (BotContext.InMiningReach). Seen in game (audit of the 10-day
	/// world_eye runs, 2026-10-16): a crystal mining had given up on was "seen" again at once, and the replan for it started a
	/// fresh crystal job beside it, which went for the same crystal first.
	/// With <paramref name="remembered"/>, else the nearest crystal remembered from before, however far (BotContext.KnownCrystals,
	/// MineSkill.RememberedCrystal): for a job that wants a crystal anyway, not to break off other work. Seen in game (measured
	/// run U9): killed 26 tiles from a crystal at (2013,607), the bot looked for crystals from the spawn, 325 rows up, for 23 game
	/// hours (and 2 more deaths) before it came within the map lookup again.
	/// </summary>
	public static TilePos? SeenLifeCrystal(BotContext ctx, bool remembered = false)
	{
		int heart = ctx.Knowledge?.SourceOf(ctx.Ids.LifeCrystal).TileType ?? -1;
		if (heart <= 0)
			return null;
		TilePos node = ctx.Node;
		TilePos? best = null;
		float bestDist = float.MaxValue;
		foreach (TilePos c in ctx.SeeCrystals(heart, node, CrystalRadius)) {
			int d = c.ChebyshevTo(node);
			if (d >= bestDist)
				break; // nearest first: none further on can beat it
			if (!ctx.InMiningReach(c, node, CrystalRadius)) {
				if (ctx.CrystalNotes.Add((c.X >> 1, c.Y >> 1, "given up on")))
					ctx.Log.Info($"Life crystal at {c} seen, left alone for now: mining gave up on it {ctx.MineFailuresLately(c)} times lately");
				continue;
			}
			// Not deeper than the gear allows (mining skips those too): seen in game, a crystal 7 rows below the limit
			// interrupted mining every second, the crystal job failing at once each time.
			// As mining itself would judge it (MineSkill.WouldMine), so the job it gets is one it takes on.
			string why = !ctx.World.CanMineTarget(c.X, c.Y) ? "the pickaxe cannot break it" : MineSkill.WhyNotMine(ctx, ctx.Moves, c, valuable: true);
			if (why == null) {
				float dist = d + ctx.MineFailureCost(c, CrystalRadius);
				if (dist < bestDist) {
					bestDist = dist;
					best = c;
				}
				continue;
			}
			// Said once per crystal and reason: seen in game, the bot ran past crystals and nothing in the log said why.
			if (ctx.CrystalNotes.Add((c.X >> 1, c.Y >> 1, why)))
				ctx.Log.Info($"Life crystal at {c} seen, left alone: {why}");
		}
		return best ?? (remembered ? MineSkill.RememberedCrystal(ctx, heart, node) : null);
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
		NpcHousingFinished(skill, status, ctx);
		if (skill is BuildSkill { Blueprint.IsNpcRoom: true })
			return; // not the house (below): the NPC rooms keep their own list
		// A bench or furnace that would not go down here is not tried here again for a while: the walk to the one far away
		// instead. Seen in game (measured run S5): "placing a Work Bench here" failing ("no free floor") and started again,
		// 278 heartbeats (58% of the run).
		if (status == SkillStatus.Failed && skill is PlaceItemSkill failedPlace && ctx.Knowledge?.TilePlacedBy(failedPlace.ItemType) is int ft
			&& (ft == ctx.Ids.WorkBenchTile || ft == ctx.Ids.FurnaceTile)) {
			stationHereAt[ft] = ctx.Tick;
			homeStationAt[ft] = ctx.Tick;
		}
		if (status == SkillStatus.Succeeded && skill is PlaceItemSkill place && place.PlacedAt is TilePos at) {
			int tile = ctx.Knowledge.TilePlacedBy(place.ItemType);
			if (tile >= 0) {
				KnownStations[tile] = at;
				notedAt[tile] = ctx.Tick;
				if (ctx.IsNight && (tile == ctx.Ids.WorkBenchTile || tile == ctx.Ids.FurnaceTile))
					homeStationAt[tile] = ctx.Tick; // made at home tonight (StationAtHomeTonight): not again, wherever it ended up
				if (tile == ctx.Ids.WorkBenchTile || tile == ctx.Ids.FurnaceTile)
					stationHereAt[tile] = ctx.Tick;
			}
		}
		if (skill is ExploreSkill { JobKey: string key } && key.StartsWith("Explore station") && status != SkillStatus.Running)
			stationExplores++;
		if (skill is ShelterSkill)
			lastShelterEnd = ctx.Tick;
		// No way home to place a station (not a death on the way), or no room for it there: where the bot is, for a while.
		if (status == SkillStatus.Failed && (ReferenceEquals(skill, homePlaceWalk) && !string.IsNullOrEmpty(skill.FailureReason) || ReferenceEquals(skill, homePlace))) {
			homePlaceFailedAt = ctx.Tick;
			ctx.Log.Info($"Auto: no placing the station in the house ({skill.FailureReason}); where I am, for now");
		}
		// Not a death on the way (no failure reason): the house is still reachable, and after the respawn at the spawn it
		// is a few steps off (run 52: killed just below the house, then sheltered outside all night).
		if (skill is StayHomeSkill && status == SkillStatus.Failed && !string.IsNullOrEmpty(skill.FailureReason)) {
			stayHomeFailedAt = ctx.Tick;
			ctx.Log.Info($"Auto: cannot get home ({skill.FailureReason}); sheltering where I am tonight");
		}
		if (skill is WalkToSkill { Travel: true } && status != SkillStatus.Running && ctx.Combat.Life > 0
			&& (status == SkillStatus.Succeeded || !string.IsNullOrEmpty(skill.FailureReason))) {
			if (status == SkillStatus.Failed && travelDest is TilePos failedDest) {
				failedTrips[failedDest] = ctx.Tick;
				ctx.Log.Info($"Auto: no way to {failedDest} from {ctx.Node} ({skill.FailureReason}); not a trip again for a day");
			}
			travelDest = null; // there, or no way there (not a death on the way)
		}
		if (skill is WalkToSkill { ToSurface: true } && status == SkillStatus.Failed && !string.IsNullOrEmpty(skill.FailureReason))
			exitTarget = null; // no way there: work the way out again
		if (skill is WalkToSkill { Underground: true } && status == SkillStatus.Failed && !string.IsNullOrEmpty(skill.FailureReason)) {
			nightMineFailedAt = ctx.Tick; // no way down tonight: the house (seen in game, run G1: five rests, then auto mode stopped)
			ctx.Log.Info($"Auto: no way down for the night's mining ({skill.FailureReason}); the house instead tonight");
		}
		if (skill is MineSkill mine) {
			// Counted and cleared for the job's whole tier group (MineSkill.TierGroup): its explorations looked for all of it.
			// Seen in game (measured run U10): iron stayed "fruitless" from t41400 while 145+ iron ore was mined by silver
			// jobs, and the plan kept choosing "Silver Ore x40 (have 3)" for the pickaxe, 25.6 game hours. Measured run U22:
			// cleared for the ore found only, silver's count from t235652 stayed while iron's went, silver cost twice iron, and
			// the plan dropped "Silver Ore x68 (have 28)", 40 short, for "Iron Ore x60 (have 3)", 57 short (d4 +5.5h); 15
			// switches between the tiers in 47 game hours. With the counts equal, CheapestToFinish chooses on the shortfall.
			int[] group = mine.TierGroup.ToArray();
			foreach (int g in group)
				tierGroups[g] = group;
			if (status == SkillStatus.Succeeded) {
				exitTarget = null; // ore found after all: no trip
				foreach (int g in group) {
					fruitlessMines.Remove(g);
					travelledFor.Remove(g);
					fruitlessAtFloor.Remove(g);
				}
			}
			else if (status == SkillStatus.Failed && mine.FailureReason.Contains("explorations"))
				foreach (int g in group) {
					NoteFruitless(g);
					if (mine.ExploredToFloor)
						fruitlessAtFloor.Add(g);
					else
						fruitlessAtFloor.Remove(g);
				}
			if (status == SkillStatus.Failed && mine.FailureReason.Contains("would be off limits") && ctx.SpawnTile is TilePos spawn
				&& spawn.ChebyshevTo(ctx.Node) > TravelArrivedTiles) {
				leaveTarget = spawn;
				ctx.Log.Info($"Auto: standing on off-limits ground at {ctx.Node}; back towards the spawn at {spawn} before mining again");
			}
		}
		if (skill is CraftSkill && status == SkillStatus.Failed && skill.FailureReason.StartsWith(nameof(CraftResult.NeedsStation))) {
			// Walked right up to it and still none in range: the station is not where it is remembered (placed elsewhere,
			// or gone). Seen in game (measured run R1): "walking right up to it" and the craft failing, 20 times in a row.
			if (walkedUpToStation && ++stationMissesInARow >= MaxStationMisses && stationWalkType >= 0) {
				ctx.Log.Info($"Auto: no {ctx.Knowledge.TileName(stationWalkType)} in range at {KnownStations.GetValueOrDefault(stationWalkType)} after walking right up to it {stationMissesInARow} times; forgetting it");
				KnownStations.Remove(stationWalkType);
				stationMissesInARow = 0;
				stationMissed = false;
			}
			else {
				stationMissed = true;
				ctx.Log.Info("Auto: the game saw no station in range for that craft; walking right up to it first");
			}
			walkedUpToStation = false;
		}
		if (skill is CraftSkill && status == SkillStatus.Succeeded) {
			stationMissesInARow = 0;
			walkedUpToStation = false;
		}
		if (ReferenceEquals(skill, stationWalk) && status == SkillStatus.Succeeded && stationMissed)
			walkedUpToStation = true;
		if (skill is CraftSkill && status == SkillStatus.Succeeded || ReferenceEquals(skill, stationWalk) && status == SkillStatus.Succeeded)
			stationMissed = false;
		if (ReferenceEquals(skill, stationWalk) && stationWalkType >= 0) {
			if (status == SkillStatus.Succeeded)
				stationWalkFailures.Remove(stationWalkType);
			// A step that failed 3 times does not count (it is banned and the next walk routes round it): counting it
			// too rebuilt the furnace and anvil twice in one run (5 iron bars each time).
			else if (status == SkillStatus.Failed && !skill.FailureReason.Contains("same step") && !skill.FailureReason.Contains("died")
				&& !BlastFirst(stationWalk, ctx)) {
				int n = stationWalkFailures.TryGetValue(stationWalkType, out int f) ? f + 1 : 1;
				stationWalkFailures[stationWalkType] = n;
				bool placeable = ctx.Knowledge.ItemsPlacing(stationWalkType).Count > 0;
				// Not for a station nothing places (a demon altar): home is where the stations it can make again are, and the
				// altar is no nearer from there. Seen in game (measured run U30): "no way back to the DemonAltar (2 walks failed);
				// drinking a recall potion to go home" 5 times from t538000 to t561600, each walk from home back to the same lip at
				// (1779,341) or pocket at (1792,324) under the corruption, until no potion was left and the altar was given up.
				if (n >= MaxStationWalkFailures && placeable && ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0) {
					// Like a player: drink a recall potion and be home, rather than rebuild the stations (an anvil is 15 ore).
					ctx.Log.Info($"Auto: no way back to the {ctx.Knowledge.TileName(stationWalkType)} ({n} walks failed); drinking a recall potion to go home");
					recallNext = true;
					stationWalkFailures.Remove(stationWalkType);
				}
				else if (n >= MaxStationWalkFailures && !placeable) {
					TilePos site = KnownStations.GetValueOrDefault(stationWalkType);
					failedStationSites[site] = ctx.Tick;
					ctx.Log.Info($"Auto: no way to the {ctx.Knowledge.TileName(stationWalkType)} at {site} ({n} walks failed); not that one again for a day, another one");
					KnownStations.Remove(stationWalkType);
					stationWalkFailures.Remove(stationWalkType);
					searchedFor.Remove(stationWalkType);
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
		WallOfFleshFinished(skill, status, ctx);
		if (skill is WaitSkill { What: "life" })
			lastRestEnd = ctx.Tick; // one rest per summon attempt: a rest that timed out is not started again at once
		if (skill is BuildSkill bridge && bridge.Blueprint.IsBridge) {
			var o = new ChasmOpening(bridge.Origin.X, bridge.Origin.X + bridge.Blueprint.Width - 1, bridge.Origin.Y);
			if (status == SkillStatus.Succeeded) {
				bridgeFailures = 0;
				if (!Bridges.Contains(o))
					Bridges.Add(o);
				ctx.Log.Info($"Auto: the chasm at {o} is bridged ({Bridges.Count} bridged)");
			}
			else if (status == SkillStatus.Failed && ++bridgeFailures >= MaxBuildFailuresPerSite) {
				ctx.Log.Info($"Auto: giving up on the bridge over the chasm at {o}: {bridge.FailureReason}");
				badBridges.Add(o.X0);
				bridgeFailures = 0;
			}
			return;
		}
		if (skill is TravelUntilSkill { Seen: false } hop && status == SkillStatus.Failed && !string.IsNullOrEmpty(hop.FailureReason) && ctx.Combat.Life > 0) {
			dungeonHopFailedAt = ctx.Tick;
			ctx.Log.Info($"Auto: the walk to {hop.Walk.Target} looking for {hop.What} failed ({hop.FailureReason}); exploring that way for a while instead");
		}
		if (skill is BuildSkill arena && !arena.Blueprint.IsHouse) {
			ArenaSlot slot = arena.Blueprint.AtDungeon ? dungeonArena : arena.Blueprint.InEvilBiome ? evilArena : eyeArena;
			if (status == SkillStatus.Succeeded)
				slot.Failures = 0;
			else if (status == SkillStatus.Failed && ++slot.Failures >= MaxBuildFailuresPerSite) {
				ctx.Log.Info($"Auto: giving up on the {arena.Blueprint.Name} site {arena.Origin}{(arena.FailedCell is TilePos fc ? $" (nothing would go at {fc}: no site over it either)" : "")}");
				slot.BadSites.Add(arena.Origin);
				// The cell that failed, not just the origin: seen in game (measured run S16), the next site was one column
				// over, with the same cell in its floor, eleven times over (12 game hours).
				if (arena.FailedCell is TilePos cell)
					slot.BadCells.Add(cell);
				slot.Site = null;
				slot.Failures = 0;
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
		badNpcSlots.Clear();
		eyeArena.ForgetGivenUp();
		evilArena.ForgetGivenUp();
		dungeonArena.ForgetGivenUp();
		if (dungeonArena.Site == null)
			dungeonArena.Width = Blueprint.DungeonArenaWidth;
		dungeonSideFlips = 0;
		dungeonSideForced = null;
		badBridges.Clear();
		bridgeFailures = 0;
		siteFailures = 0;
		stationExplores = 0;
	}

	/// <summary>
	/// The boss plan, armoured, at night and not deep down: what the summon is made of that only the night has (lenses from
	/// demon eyes) is hunted now, whatever stage the plan is at, as a player spends the nights getting them. Seen in game
	/// (measured run S13): everything else ready by day 8, then 34 heartbeats waiting for night and 28 hunting, the
	/// lenses still short at day 10, while the nights before went to mining.
	/// </summary>
	public bool NightHuntDue(BotContext ctx) => NightPartFor(ctx) != null;

	private (int Item, int AiStyle, int Amount)? NightPartFor(BotContext ctx)
	{
		GameIds ids = ctx.Ids;
		// Settled first (a house to come back to): before it, the hunt's fights took the bot off, and the stations and house
		// went up where the arena should (boss_auto, which starts armoured at night with the house kit in the bag).
		if (Plan != Plans.Boss && Plan != Plans.EvilBoss && Plan != Plans.Skeletron || !ctx.IsNight || ctx.Combat.Defense < NightOutDefense || ids.SuspiciousEye <= 0 || ctx.World == null
			|| HomeInside(ctx) == null
			|| ctx.World.BossDefeated(ids.EyeBoss) || ctx.Inventory.Count(ids.SuspiciousEye) > 0 || ctx.Infinite(ids.SuspiciousEye)
			|| ctx.SurfaceY > 0 && ctx.Node.Y > ctx.SurfaceY + NightMineRows)
			return null;
		foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(ids.SuspiciousEye))
			foreach (Ingredient ing in r.Ingredients) {
				ItemSource src = ctx.Knowledge.SourceOf(ing.Item);
				// Journey: a researched part (lenses) is duplicated when the summon is made, not hunted at night.
				if (src.Kind == SourceKind.Hunt && NocturnalAiStyles.Contains(src.NpcAiStyle) && ctx.Inventory.Count(ing.Item) < ing.Stack && !ctx.Infinite(ing.Item))
					return (ing.Item, src.NpcAiStyle, ing.Stack);
			}
		return null;
	}

	/// <summary>
	/// A boss's summon item made at a station nothing places (the Suspicious Looking Eye, Worm Food: a demon altar), its
	/// makings all in the bag, and such an altar in sight (not one given up on): made now, whatever stage the plan is at, as a
	/// player at an altar with the lenses makes the eye there rather than come back for it. Seen in game (measured run U30): from
	/// t496800 to t516000, with 7 lenses and max life 200, the bot climbed from the caverns past the DemonAltar at (1748,411),
	/// 14 tiles off at (1734,398), "up to the surface before building the arena"; the walks back to that altar from home
	/// began at t534000 and the Suspicious Looking Eye was made at t722520.
	/// </summary>
	private ISkill SummonAtAnAltarInSight(BotContext ctx)
	{
		if (ctx.World == null || ctx.Seen == null)
			return null;
		TilePos from = ctx.Seen.Origin ?? ctx.Node;
		foreach (BossDefeated b in StagesFor(Plan, ctx.Ids).OfType<BossDefeated>()) {
			if (b.ByOldMan || b.SummonItem <= 0 || ctx.World.BossDefeated(b.Npc) || ctx.Inventory.Count(b.SummonItem) > 0)
				continue;
			RecipeInfo r = BestRecipe(b.SummonItem, ctx);
			if (r == null || r.Stations.Count == 0 || !r.Ingredients.All(i => ctx.Inventory.CountAny(i.Accepts) >= i.Stack))
				continue;
			bool atHand = r.Stations.All(st => ctx.Knowledge.ItemsPlacing(st).Count == 0 && ctx.Seen.Stations.TryGetValue(st, out TilePos at)
				&& at.ChebyshevTo(from) <= StationSightTiles && !GivenUpSite(at, ctx));
			if (!atHand)
				continue;
			costs.Clear();
			costEvaluations = 0;
			stuck = null;
			ISkill s = Resolve(new HaveItem(b.SummonItem, 1, ctx.Knowledge.ItemName(b.SummonItem)), new Dictionary<int, int>(), 0, ctx);
			stuck = null;
			if (s != null)
				return s;
		}
		return null;
	}

	private ISkill NightPartsForTheSummon(BotContext ctx)
	{
		if (NightPartFor(ctx) is not var (item, ai, amount))
			return null;
		string name = ctx.Knowledge.ItemName(item);
		return Do($"night: hunting for {amount - ctx.Inventory.Count(item)} more {name} while it is dark (for the {ctx.Knowledge.ItemName(ctx.Ids.SuspiciousEye)})",
			new HuntSkill(item, ai, amount) { SpawnsAboveRow = SurfaceSpawnRow(ctx) });
	}

	/// <summary>Night prey (demon eyes) spawns only above the world's surface row (vanilla NPC.SpawnNPC; see HuntSkill).</summary>
	private static int? SurfaceSpawnRow(BotContext ctx) => ctx.SurfaceY > 0 ? ctx.SurfaceY : null;

	/// <summary>The next skill to run, or null when the plan is complete or stuck.</summary>
	public ISkill Next(BotContext ctx)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		ctx.NightDescentFrom = null; // set again below while the night's way down is still the job
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
		// An errand in the evil biome is this pass's to choose again (ResolveItem, ResolveBoss): anything else, it is over.
		ctx.EvilErrand = false;
		// Likewise the Underworld's (the depth limit lifted for the shaft, the bridge and the fight), and the Wall of Flesh's rules.
		ctx.DepthErrand = false;
		DayWorkBeforeNight = false;
		WallOfFleshRules(ctx);
		RememberStations(ctx);
		NoteDungeon(ctx);
		ProtectBuilt(ctx);
		// A boss up comes before everything (a chest or a crystal in sight, the night's hunt): its fight is the job until it is
		// over, and a reflex (surfacing for breath) goes back to it. Seen in game (measured run M10j): the worm out of view
		// after the bot surfaced from a chasm's water, the plan went on to a work bench and arrows, then mushrooms for a new
		// Worm Food, with the worm still up.
		if (BossUpInThePlan(ctx) is BossDefeated up) {
			costs.Clear();
			costEvaluations = 0;
			stuck = null;
			if (Resolve(up, new Dictionary<int, int>(), 0, ctx) is ISkill fight)
				return fight;
		}
		// The expedition under way (the shaft dug, the bridge known) takes itself up again before the surface's stages: a bot back at the
		// spawn after a death goes back down, not to an NPC room first (seen in game, E4 2026-10-17: the errand ended and the plan built
		// NPC rooms for a game day before it was proposed again).
		if (Scheduler == Schedulers.Errands && WallOfFleshPlan(ctx.Ids) && Plan == Plans.WallOfFlesh && UnderworldReachedOnce && !WofLootCollected
			&& ProposeWallOfFlesh(ctx, Errands.WallOfFleshErrand.BridgePhase) == null && Proposed != null) {
			Status = $"the Wall of Flesh: the errand {Proposed.Id} taken up again";
			Complete = false;
			return null;
		}
		// The descent owns the bot (Schedulers.Operation): its own demand and stages, no optional loot, no stages from the top.
		if (WallOfFleshPlan(ctx.Ids) && DescentOwns(ctx)) {
			costs.Clear();
			costEvaluations = 0;
			stuck = null;
			ISkill owned = NextInDescent(StagesFor(Plan, ctx.Ids), ctx);
			if (owned != null || stuck != null) {
				if (stuck != null) {
					Status = $"stuck in the descent: {stuck}";
					Complete = false;
				}
				return owned;
			}
		}
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
			// Journey: as many as research takes are carried: researched, and then duplicated to max life (below).
			if (CrystalsForResearch(ctx))
				return Do($"researching {ctx.Inventory.Count(ctx.Ids.LifeCrystal)} life crystals (max life {ctx.Combat.LifeMaxBase})", new ResearchSkill(ctx.Ids.LifeCrystal));
			// Life first; a crystal is kept for the heart lantern only once max life is in (see LanternFromASpareCrystal).
			int keep = LanternStillNeeded(ctx) && ctx.Combat.LifeMaxBase >= BossLife ? 1 : 0;
			if (ctx.Inventory.Count(ctx.Ids.LifeCrystal) > keep && !HoldCrystalsForResearch(ctx))
				return Do($"using a life crystal (max life {ctx.Combat.LifeMaxBase})", new UseItemSkill(ctx.Ids.LifeCrystal));
			// Journey, researched: a click in the duplication menu, and every use up to the most crystals give (a stack is 9999).
			if (ctx.Infinite(ctx.Ids.LifeCrystal))
				return Do($"duplicating life crystals for max life {MaxCrystalLife} (max life {ctx.Combat.LifeMaxBase}, researched)",
					new DuplicateSkill(ctx.Ids.LifeCrystal, keep + CrystalsFor(MaxCrystalLife, ctx)));
			if (SeenLifeCrystal(ctx) is TilePos heart)
				return Do($"mining the life crystal seen at {heart}",
					new MineSkill(ctx.Ids.LifeCrystal, ctx.Knowledge.SourceOf(ctx.Ids.LifeCrystal).TileType, ctx.Inventory.Count(ctx.Ids.LifeCrystal) + 1));
		}
		if (NightPartsForTheSummon(ctx) is ISkill hunt)
			return hunt;
		if (SummonAtAnAltarInSight(ctx) is ISkill summon)
			return AtNight(summon, ctx);
		costs.Clear();
		costEvaluations = 0;
		stuck = null;
		var claims = new Dictionary<int, int>();
		IReadOnlyList<Need> stages = StagesFor(Plan, ctx.Ids);
		for (int i = 0; i < stages.Count; i++) {
			Need stage = stages[i];
			if (SkeletronStage(stage))
				SkeletronPrepMet = true; // every stage before met
			if (stage is PrepFor { Inner: TownNpcAlive })
				WofPrepMet = true; // S1 met: on to the Guide, the shaft and the bridge
			nightOnlyByDay = false;
			ISkill skill = Resolve(stage, claims, 0, ctx);
			if (stuck != null) {
				Status = $"stuck on {stage.Label}: {stuck}";
				Complete = false;
				return null;
			}
			// The stage's errand (Schedulers.Errands): the brain runs it in place of the plan from here.
			if (Proposed != null) {
				Status = $"{stage.Label}: the errand {Proposed.Id}";
				Complete = false;
				return null;
			}
			if (skill == null)
				continue;
			if (nightOnlyByDay && DayWorkBeforeTheNight(stages, i + 1, ctx) is ISkill day) {
				DayWorkBeforeNight = true;
				return AtNight(day, ctx);
			}
			return AtNight(skill, ctx);
		}
		Status = Plan switch {
			Plans.Early => "early-game chain complete: work bench placed, wooden sword, wood stockpiled",
			Plans.Iron => "iron-age chain complete: work bench, furnace and anvil placed, iron-tier pickaxe in hand",
			Plans.Crystals => $"max life {ctx.Combat.LifeMaxBase} from life crystals",
			Plans.Arena => ArenaOnOpenGround ? "no arena site: the boss is fought on open ground" : $"the arena at {ArenaSite} is ready",
			Plans.Boss => "the Eye of Cthulhu is defeated",
			Plans.EvilBoss => ctx.Ids.Crimson ? "the Brain of Cthulhu is defeated" : "the Eater of Worlds is defeated",
			Plans.Skeletron => "Skeletron is defeated",
			Plans.WallOfFlesh => "the Wall of Flesh is defeated: Hardmode",
			Plans.Hellbridge => $"the hellbridge is built ({HellbridgeText})",
			Plans.Housing => $"the house, {npcRooms.Count} NPC rooms and {HousedInOurRooms(ctx)} town NPCs living in them",
			_ => "settled: house built, iron-tier pickaxe, broadsword and armour",
		};
		Complete = true;
		return null;
	}

	/// <summary>
	/// The day's work for a craft whose ingredient waits for the night (a night-only hunt): the recipe's stations that need work
	/// (a demon altar to find, a station to place), as a player looks for the altar by day while the lenses come at night. Null
	/// when none does, or one is stuck (the night's wait stands then; the station's trouble comes up when it is its turn). Seen
	/// in game (Journey run J3, Journey Test, 2026-10-17): the Suspicious Looking Eye's ingredients first, the lenses waiting for
	/// the night, the altar was not looked at until the sixth lens, at day 9; the days went to "exploring for the corruption to
	/// hunt for Rotten Chunk", the next boss's prep (82 heartbeats), and the run ended "exploring for a DemonAltar".
	/// </summary>
	private ISkill StationsByDay(RecipeInfo recipe, string name, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		string waiting = Status;
		foreach (int station in recipe.Stations) {
			nightOnlyByDay = false;
			ISkill s = Resolve(new StationPlaced(station, ctx.Knowledge.TileName(station)), claims, depth + 1, ctx);
			if (stuck != null) {
				stuck = null;
				break;
			}
			if (s != null && !nightOnlyByDay) {
				Status = $"by day, the {ctx.Knowledge.TileName(station)} for {name} ({waiting}): {Status}";
				return s;
			}
		}
		Status = waiting;
		nightOnlyByDay = true;
		return null;
	}

	/// <summary>
	/// A night-only stage waiting for dark by day (the Eye's lenses from demon eyes, the Eye's summon: <see cref="nightOnlyByDay"/>):
	/// the day goes to the plan's later stages, in their order, and after the plan's last one to the next milestone's prep
	/// (<see cref="NextMilestonePrep"/>), as a player spends the day on the work the plan needs anyway. Null when none of it is
	/// to be done by day: then the wait (or the walk to the arena to wait there) as before. At nightfall the brain plans again
	/// (<see cref="DayWorkBeforeNight"/>) and the night-only stage, first in the plan's order, goes first again.
	/// Not a boss of a later stage (the bosses in the plan's order: the Eye's demonite is the Demon Bow for the worm, M10 G2),
	/// nor a stage that itself waits. Seen in game (measured runs U31 and U32, world_eye): 49 and 70 heartbeats of
	/// "Wait for night", in U32 from t518400 to t568800 for the sixth lens, every other stage of the plan met.
	/// The later stages count the bag afresh, not after the earlier stages' claims: what the night stage is fought with (the
	/// bow, the arrows it does not shoot) is theirs too. With the earlier claims, the worm's bow stage went to mining iron for
	/// a second iron bow while the Eye's waited (NightWaitTests).
	/// </summary>
	private ISkill DayWorkBeforeTheNight(IReadOnlyList<Need> stages, int from, BotContext ctx)
	{
		string waiting = Status;
		var claims = new Dictionary<int, int>();
		bool evilErrand = ctx.EvilErrand, depthErrand = ctx.DepthErrand;
		foreach (Need later in stages.Skip(from).Concat(NextMilestonePrep(Plan, ctx.Ids))) {
			if (later is BossDefeated or BossLoot)
				continue;
			nightOnlyByDay = false;
			ctx.EvilErrand = evilErrand;
			ctx.DepthErrand = depthErrand;
			ISkill s = Resolve(later, claims, 0, ctx);
			if (stuck != null) {
				// Not the plan stuck: its next stage is the night's, and that one is not. The plan's order holds: nothing after it.
				ctx.Log.Info($"Auto: nothing to do by day before the night: {later.Label} is stuck ({stuck})");
				stuck = null;
				break;
			}
			if (s == null || s is WaitSkill || nightOnlyByDay)
				continue;
			Status = $"by day, before the night ({waiting}): {Status}";
			nightOnlyByDay = false;
			return s;
		}
		Status = waiting;
		ctx.EvilErrand = evilErrand;
		ctx.DepthErrand = depthErrand;
		nightOnlyByDay = true;
		return null;
	}

	/// <summary>
	/// The prep of the milestone after this plan's last boss, for the days its night-only boss waits (<see cref="DayWorkBeforeTheNight"/>):
	/// after the Eye (the boss plan), this world's evil boss's (Milestone 10, the next one; its world_eow run starts from the Eye's
	/// checkpoint): the Eater of Worlds', or the Brain of Cthulhu's in a Crimson world. Prep only (<see cref="PrepFor"/>: moot once
	/// that boss is down), never its fight. Seen in game (measured run J2, world_eye on the Crimson world The Aggressive Site, log line
	/// 16832): "by day, before the night ...: exploring for the corruption (none seen yet) to hunt for Rotten Chunk", the worm's prep in
	/// a world without a corruption.
	/// </summary>
	private static IEnumerable<Need> NextMilestonePrep(string plan, GameIds ids) =>
		plan == Plans.Boss && ids.WorldEvilBoss > 0 && ids.WorldEvilSummon > 0 ? WorldEvilBossStages(ids).OfType<PrepFor>() : Enumerable.Empty<Need>();

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
			return FoundHomeValid(ctx);
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
		if (skill is WalkToSkill { Underground: true } or WalkToSkill { Home: true })
			return false; // on its way down for the night's mining, or home
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
			&& inside.ChebyshevTo(ctx.Node) <= NightHomeTiles && NightWorkOnTheSurface(skill, ctx)
			// A walk to the house's own stations is a walk home: seen in game (measured run S6), nightfall on the way to the
			// furnace 4 tiles from the house, with the ore for 8 bars; home, and 9 game hours in it, the ore unsmelted.
			&& !(skill is WalkToSkill { Target: var to } && to.ChebyshevTo(inside) <= HomeStationTiles)) {
			// Armed, a mining job goes on underground, as a player spends the night in the mine (the caves are the same
			// by night): straight down below the spawn first, then mining there. Seen in game (measured run E): 37% of the
			// run was nights at home, most of them instead of mining near the surface.
			// Armed or not: the caves are as by day, and the day's mining is done with whatever sword there is. Seen in game
			// (measured run R1): before the first ore sword, two whole nights at home, over a third of those two days.
			if (skill is MineSkill && ctx.SpawnTile is TilePos spawn && ctx.SurfaceY > 0
				&& ctx.Tick - nightMineFailedAt >= NightMineRetryTicks) {
				// Down to a depth, by whatever way down there is (Baritone's GoalYLevel), not to one tile: seen in game (measured
				// run S6), the tile under the spawn had "no path after 3 attempts", and so the night was spent in the house.
				int row = Math.Max(ctx.SurfaceY, spawn.Y) + NightMineRows;
				Status = $"night: down into the ground to row {row} to mine there ({Status})";
				ctx.NightDescentFrom = inside;
				return new WalkToSkill(new Core.Pathfinding.GoalRow(row, spawn.X, ctx.Physics) { Below = true, Accept = t => !ctx.OffLimits(t) }) { Quiet = true, Underground = true };
			}
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
		if (depth == 0)
			tierAlternatives = Array.Empty<int>(); // a stage's own tiers only: not a life crystal's mining taking silver ore
		return need switch {
			StationPlaced s => ResolveStation(s, claims, depth, ctx),
			PickaxeAtLeast p => ctx.PickPower >= p.Power ? null : Resolve(new HaveItem(p.Items, 1, p.Label), claims, depth + 1, ctx),
			HaveItem h => ResolveItem(h, claims, depth, ctx),
			HouseBuilt hb => ResolveHouse(hb.HeartLantern && LanternFromASpareCrystal(ctx), claims, depth, ctx),
			NpcHousing => ResolveNpcHousing(claims, depth, ctx),
			NpcsHoused nh => ResolveNpcsHoused(nh, ctx),
			ArenaBuilt { AtDungeon: true } ad => ResolveDungeonArena(ad, claims, depth, ctx),
			ArenaBuilt ab => ResolveArena(ab, claims, depth, ctx),
			DungeonFound => ResolveDungeon(ctx, "for Skeletron"),
			TownNpcAlive ta => ResolveTownNpc(ta, ctx),
			UnderworldReached => ResolveUnderworld(ctx),
			HellbridgeBuilt => ResolveHellbridge(ctx),
			BridgeBlocks bb => ResolveBridgeBlocks(bb, claims, depth, ctx),
			BossLoot bl => ResolveBossLoot(bl, ctx),
			BossDefeated b => ResolveBoss(b, claims, depth, ctx),
			// The prep for a boss is moot once it is down, and while it is up: the fight is on.
			PrepFor pf => ctx.World.BossDefeated(pf.BossNpc) || BossUp(ctx, pf.BossNpc) ? null : Resolve(pf.Inner, claims, depth, ctx),
			FromBag fb => ResolveFromBag(fb.Inner, depth, ctx),
			BeforeTheUnderworld bu => DownThereOnce(ctx) ? null : Resolve(bu.Inner, claims, depth, ctx),
			BestEffort be => ResolveBestEffort(be, claims, depth, ctx),
			StepToward st => st.Made.Any(m => m.Item > 0 && ctx.Inventory.Count(m.Item) >= m.Count) ? null
				: Resolve(st.Inner, new Dictionary<int, int>(claims), depth, ctx),
			LifeAtLeast la => ctx.Combat.LifeMaxBase >= la.Life ? null
				// Journey: crystals carried in the number research takes are researched first (then duplicated to the life wanted).
				: CrystalsForResearch(ctx) ? Do($"researching {ctx.Inventory.Count(ctx.Ids.LifeCrystal)} life crystals (max life {ctx.Combat.LifeMaxBase}/{la.Life})", new ResearchSkill(ctx.Ids.LifeCrystal))
				// Journey, not researched yet: more crystals until research takes them (HoldCrystalsForResearch).
				: HoldCrystalsForResearch(ctx) ? Resolve(new HaveItem(ctx.Ids.LifeCrystal, ctx.Journey.ResearchLeft(ctx.Ids.LifeCrystal), "life crystals to research"), claims, depth + 1, ctx)
				: ctx.Inventory.Count(ctx.Ids.LifeCrystal) > 0 ? Do($"using a life crystal (max life {ctx.Combat.LifeMaxBase}/{la.Life})", new UseItemSkill(ctx.Ids.LifeCrystal))
				: ctx.Infinite(ctx.Ids.LifeCrystal) ? Do($"duplicating life crystals for max life {la.Life} (max life {ctx.Combat.LifeMaxBase}, researched)",
					new DuplicateSkill(ctx.Ids.LifeCrystal, CrystalsFor(la.Life, ctx)))
				: Resolve(new HaveItem(ctx.Ids.LifeCrystal, 1, "life crystal"), claims, depth + 1, ctx),
			Wearing wr => wr.Items.Any(ctx.Inventory.IsEquipped) ? null
				: wr.Items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0) is int carried and > 0 ? Do($"putting on the {wr.Label}", new EquipSkill(carried))
				: Resolve(new HaveItem(wr.Items, 1, wr.Label), claims, depth + 1, ctx),
			_ => null,
		};
	}

	private ISkill ResolveStation(StationPlaced s, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (KnownStations.ContainsKey(s.TileType) || StationByResearch(ctx, s.TileType))
			return null;
		IGameKnowledge k = ctx.Knowledge;
		IReadOnlyList<int> items = k.ItemsPlacing(s.TileType);
		int carried = items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0, -1);
		if (carried >= 0) {
			string name = k.ItemName(carried);
			int bench = ctx.Ids.WorkBenchTile;
			if (s.TileType != bench && StationHomeFirst(name, carried, ctx) is ISkill home)
				return home;
			if (s.TileType != bench && KnownStations.TryGetValue(bench, out TilePos b) && b.ChebyshevTo(ctx.Node) > StationReach)
				return Do($"walking to the work bench to place a {name}", WalkToStation(b, ctx));
			return Do($"placing a {name}", new PlaceItemSkill(carried));
		}
		if (items.Count == 0) {
			// Nothing places it (a demon altar): find one in the world.
			TilePos? found = FindReachableUnplaceable(s.TileType, ctx);
			if (found == null) {
				// None seen yet (only what is revealed on the map counts): go and look, a new direction each time; that found
				// nothing, new ground, as for ore that ran out (TravelForOre: along the surface, farther each time). Seen in
				// game (measured run S17): all else ready at d5 +21h, then "no DemonAltar found after 10 explorations" and
				// auto mode stopped at d7 +17h.
				int tripKey = -s.TileType;
				// Where generation puts none (the altars: never in the world's middle tenth, where the spawn is), a trip first,
				// to the nearest ground it does (TripColumns keeps every trip for it there). Seen in game (Journey run J1, The
				// Aggressive Site, 6400 wide, spawn at x 3198): every exploration for a DemonAltar went over x 3050-3320 and every
				// trip to x 3078 or 3318, all within x 2880-3520 where the "Altars" pass puts none; 354 explorations, none found.
				GenArea? area = k.GenerationArea(s.TileType);
				// Once each time the bot is in those columns: with no trip possible, it explores there as before.
				if (area is GenArea ga && ga.HoldsColumn(ctx.Node.X))
					areaTripTried.Remove(tripKey);
				else if (area is GenArea gb && travelDest == null && areaTripTried.Add(tripKey)
					&& fruitlessMines.GetValueOrDefault(tripKey) == travelledFor.GetValueOrDefault(tripKey)) {
					ctx.Log.Info($"Auto: no {k.TileName(s.TileType)} is generated in columns {gb.SkipFromX}-{gb.SkipToX} (the world's middle tenth); travelling out of them to look");
					NoteFruitless(tripKey);
					stationHeading = null;
				}
				if (TravelForOre(ctx, tripKey, k.TileName(s.TileType)) is ISkill trip)
					return trip;
				if (stationExplores >= MaxStationExplores) {
					if (fruitlessMines.GetValueOrDefault(tripKey) < MaxStationTrips) {
						NoteFruitless(tripKey);
						stationExplores = 0;
						stationHeading = null; // new ground: the safer side there
						if (TravelForOre(ctx, tripKey, k.TileName(s.TileType)) is ISkill first)
							return first;
					}
					stuck = $"no {k.TileName(s.TileType)} found after {stationExplores} explorations, and nothing can place one";
					return null;
				}
				int tile = s.TileType;
				// One heading, kept (the safer side, chosen once), and one job that a fight pauses and resumes (its key): an
				// exploration counts when it ends, not each time the planner is asked. Seen in the code (audit after measured
				// run U8): every replan after a fight started a fresh exploration the other way from wherever the fight left the
				// bot and counted it, so the 10 explorations went to and fro over the same ground in about 10 fights (run S17:
				// "no DemonAltar found after 10 explorations").
				stationHeading ??= StationHeading(ctx, area);
				return Do($"exploring for a {k.TileName(s.TileType)} (none seen yet)",
					new ExploreSkill(k.TileName(s.TileType), c => c.World.FindTiles(tile, c.Node, StationSearchRadius).Count > 0, ExploreBias.Down, stationHeading.Value)
					{ JobKey = $"Explore station {tile}" });
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
			stocked.Add(StockKey(h));
		if (available >= h.Count || h.RefillBelow > 0 && available >= h.RefillBelow && stocked.Contains(StockKey(h))) {
			claims[h.Accepts[0]] = claimed + Math.Min(h.Count, available);
			return null;
		}
		stocked.Remove(StockKey(h));
		int more = h.Count - Math.Max(0, available);
		// Journey: a researched item the need takes is duplicated, not gathered or crafted: a click in the duplication menu for a
		// full stack (stations, blocks, arrows, potions, ingredients of a craft alike). Never on Classic (Infinite is false).
		foreach (int a in h.Accepts)
			if (ctx.Infinite(a)) {
				int to = ctx.Inventory.Count(a) + more;
				return Do($"need {more} more {k.ItemName(a)} (researched: duplicating to {to})", new DuplicateSkill(a, to));
			}
		int item = h.Accepts.Count > 1 ? CheapestToFinish(h.Accepts, more, ctx) : Cheapest(h.Accepts, ctx);
		// The other tiers of a tiered need (iron or silver gear): their ores do as well, if this one is not to be found.
		if (h.Accepts.Count > 1 && item >= 0)
			tierAlternatives = h.Accepts.Where(a => a != item).ToArray();
		if (item < 0) {
			stuck = $"no way to get {h.Label}" + MissingStationHint(h.Accepts, ctx);
			return null;
		}
		string name = k.ItemName(item);
		int target = ctx.Inventory.Count(item) + more;
		ItemSource src = k.SourceOf(item);
		if (src.EvilBiome && src.Kind is SourceKind.Hunt or SourceKind.Mine)
			return ResolveEvilSource(item, src, more, target, claims, depth, ctx);
		switch (src.Kind) {
			case SourceKind.Tree: {
				// Every other wood the need takes counts too (an any-wood recipe: Ebonwood from the Corruption's trees). Seen in
				// game (measured run U28): Wooden Arrows' wood gathered as Wood only beside Ebonwood trees, "need 16 more Wood".
				int[] woods = h.Accepts.Where(a => a != item && k.SourceOf(a).Kind == SourceKind.Tree).ToArray();
				int woodTarget = target + woods.Sum(ctx.Inventory.Count);
				return Do($"need {more} more {name}{(woods.Length > 0 ? " or other wood" : "")} (gathering to {woodTarget})",
					new GatherWoodSkill(item, woodTarget) { Also = woods });
			}
			case SourceKind.Mine:
				// Stone is also what the bot builds with on the way: seen in game, it mined exactly 20 stone for the furnace,
				// used one to climb out of its hole on the way back to the bench, and went down for one more, forever.
				if (ctx.Ids.BuildingBlocks.Contains(item))
					target += BlockMargin;
				if (LeaveOffLimits(ctx, name) is ISkill leave)
					return leave;
				// A crystal remembered from before, however far: back to it, not a trip to new ground or the spawn's (see
				// SeenLifeCrystal).
				if (item == ctx.Ids.LifeCrystal && SeenLifeCrystal(ctx, remembered: true) is TilePos known)
					return Do($"mining the life crystal remembered at {known} ({known.ChebyshevTo(ctx.Node)} tiles away)", new MineSkill(item, src.TileType, target));
				if (TravelForOre(ctx, item, name) is ISkill travel)
					return travel;
				return Do($"need {more} more {name} (mining to {target})", new MineSkill(item, src.TileType, target) { Also = AltOres(item, ctx) });
			case SourceKind.Hunt:
				if (NocturnalAiStyles.Contains(src.NpcAiStyle) && !ctx.IsNight) {
					nightOnlyByDay = true; // the day goes to the plan's later work, if it has any (DayWorkBeforeTheNight)
					return Do($"waiting for night to hunt for {name}", new WaitSkill("night", c => c.IsNight, NightWaitTicks));
				}
				return Do($"need {more} more {name} (hunting to {target})",
					new HuntSkill(item, src.NpcAiStyle, target) { SpawnsAboveRow = NocturnalAiStyles.Contains(src.NpcAiStyle) ? SurfaceSpawnRow(ctx) : null });
		}

		// What only a boss drops (shadow scales, tissue samples): the boss fought again for them, as a player farms it, with the summon
		// the plan knows how to make. The user (2026-10-17, E6): "You could just use a pickaxe that can mine hellstone? Why not be as
		// prepared as possible for the next step of the game": the Nightmare Pickaxe's 6 scales were trashed after the first kill.
		if (BossDropOf(item, ctx.Ids) is BossDefeated again) {
			Status = $"need {more} more {name}: the {again.Label} fought again for them";
			return Resolve(again, claims, depth + 1, ctx);
		}
		RecipeInfo recipe = BestRecipe(item, ctx);
		if (recipe == null) {
			stuck = $"no recipe or source for {name}";
			return null;
		}
		int crafts = (more + recipe.ResultStack - 1) / recipe.ResultStack;
		foreach (Ingredient ing in recipe.Ingredients) {
			ISkill sub = Resolve(new HaveItem(ing.Accepts, ing.Stack * crafts, k.ItemName(ing.Item)), claims, depth + 1, ctx);
			// An ingredient waiting for the night (the Eye's lenses): its stations first, by day.
			if (sub != null && nightOnlyByDay && stuck == null && StationsByDay(recipe, name, claims, depth, ctx) is ISkill station)
				return station;
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
				if (StationAtHomeTonight(station, claims, depth, ctx) is ISkill home)
					return home;
				if (StationHereInstead(station, claims, depth, ctx) is ISkill here)
					return here;
				if (RecallHome(ctx, KnownStations[station]) is ISkill recall)
					return recall;
				if (NightMiningInstead(station, item, ctx) is ISkill night)
					return night;
				if (blastFor is (int type, TilePos from, Core.Pathfinding.IGoal goal) && type == station && from == ctx.Node) {
					blastFor = null;
					return Do($"blasting a way on towards the {k.TileName(station)} at {KnownStations[station]} to craft {name}", new BlastOutSkill(goal));
				}
				blastFor = null;
				stationWalk = WalkToStation(KnownStations[station], ctx);
				stationWalkType = station;
				return Do($"walking to the {k.TileName(station)} to craft {name}", stationWalk);
			}
		string have = string.Join(", ", recipe.Ingredients.Select(i => $"{k.ItemName(i.Item)} {ctx.Inventory.CountAny(i.Accepts)}/{i.Stack * crafts}"));
		return Do($"crafting {more} {name} ({have})", new CraftSkill(item, target));
	}

	/// <summary>A work bench or furnace farther than this (tiles) is made anew where the bot is, if the bag holds what it takes.</summary>
	public const int FarStationTiles = 100;
	/// <summary>A station made where the bot is is not made there again for this long (wherever it ended up).</summary>
	public const int StationHereAgainTicks = 3600;
	private readonly Dictionary<int, long> stationHereAt = new();

	/// <summary>
	/// The work bench or furnace for a craft is far away, and the bag holds what one takes: make one here, as a player
	/// carries the makings of them down the mine. Seen in game (measured run R1): ore found 550 tiles from home, and a walk
	/// back through the caves to smelt it, nine game hours. Null when that is not the case.
	/// </summary>
	private ISkill StationHereInstead(int station, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (station != ctx.Ids.WorkBenchTile && station != ctx.Ids.FurnaceTile || !KnownStations.TryGetValue(station, out TilePos at)
			|| at.ChebyshevTo(ctx.Node) <= FarStationTiles || stationHereAt.TryGetValue(station, out long made) && ctx.Tick - made < StationHereAgainTicks)
			return null;
		IGameKnowledge k = ctx.Knowledge;
		IReadOnlyList<int> items = k.ItemsPlacing(station);
		if (items.Count == 0)
			return null;
		int carried = items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0, -1);
		if (carried >= 0)
			return Do($"placing a {k.ItemName(carried)} here (the one at {at} is {at.ChebyshevTo(ctx.Node)} tiles away)", new PlaceItemSkill(carried));
		// Journey: a researched station item is a click away wherever the bot is (duplicated, then placed here).
		int researched = items.FirstOrDefault(ctx.Infinite, -1);
		if (researched >= 0)
			return Resolve(new HaveItem(researched, 1, k.ItemName(researched)), claims, depth + 1, ctx);
		RecipeInfo r = BestRecipe(items[0], ctx);
		if (r == null || !r.Ingredients.All(i => ctx.Inventory.CountAny(i.Accepts) >= i.Stack))
			return null; // not without fetching things: the walk then
		return Resolve(new HaveItem(items[0], 1, k.ItemName(items[0])), claims, depth + 1, ctx);
	}

	/// <summary>A work bench or furnace farther than this (tiles) from the house's inside spot is not at home.</summary>
	public const int HomeStationTiles = 8;
	public const int HomeArrivedTiles = 3;
	/// <summary>A station made at home is not made again for this long (one a night, wherever it ended up).</summary>
	public const int HomeStationAgainTicks = 12 * 3600;
	private readonly Dictionary<int, long> homeStationAt = new();

	/// <summary>
	/// At night in weak gear, with work at a cheap station (a work bench, a furnace) that stands away from the house: home,
	/// and a new one made there, as a player keeps them in the house. Seen in game (measured run M2): nightfall with 21
	/// iron ore and the furnace 15 rows below the house (on a site given up); the walk to it became a night in the house
	/// doing nothing, and the anvil came a game day late. Null when that is not the case.
	/// </summary>
	private ISkill StationAtHomeTonight(int station, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (!ctx.IsNight || ctx.Combat.Defense >= NightOutDefense || station != ctx.Ids.WorkBenchTile && station != ctx.Ids.FurnaceTile
			|| HomeInside(ctx) is not TilePos inside || !KnownStations.TryGetValue(station, out TilePos at)
			|| at.ChebyshevTo(inside) <= HomeStationTiles || inside.ChebyshevTo(ctx.Node) > NightHomeTiles
			|| homeStationAt.TryGetValue(station, out long made) && ctx.Tick - made < HomeStationAgainTicks)
			return null;
		IGameKnowledge k = ctx.Knowledge;
		string tile = k.TileName(station);
		if (inside.ChebyshevTo(ctx.Node) > HomeArrivedTiles)
			return Do($"night: home first, to make a {tile} there (the one at {at} is outside the house)",
				new WalkToSkill(new Core.Pathfinding.GoalNearTile(inside, 1, ctx.Physics)) { Quiet = true, Home = true });
		IReadOnlyList<int> items = k.ItemsPlacing(station);
		if (items.Count == 0)
			return null;
		int carried = items.FirstOrDefault(i => ctx.Inventory.Count(i) > 0, -1);
		if (carried >= 0)
			return Do($"night: placing a {k.ItemName(carried)} in the house (the one at {at} is outside it)", new PlaceItemSkill(carried));
		return Resolve(new HaveItem(items[0], 1, k.ItemName(items[0])), claims, depth + 1, ctx);
	}

	/// <summary>
	/// At night in weak gear, with the house near and the station for a craft outside it, out of sight: the night's mining
	/// (AtNight takes it down into the ground), of the ore the item is made of, as much again as one takes; the craft
	/// waits for the day, the ore keeps. Not a night in the house: seen in game (measured run U10), all three nights began
	/// "staying in the house instead of WalkTo (walking to the Anvils...)", 29.5 game hours, the anvil 155 tiles from the
	/// house. Null when that is not the case, or the item is not made of ore (the house then, as before).
	/// </summary>
	private ISkill NightMiningInstead(int station, int item, BotContext ctx)
	{
		if (!ctx.IsNight || ctx.Combat.Defense >= NightOutDefense || ctx.Tick - stayHomeFailedAt < StayHomeRetryTicks
			|| HomeInside(ctx) is not TilePos inside || inside.ChebyshevTo(ctx.Node) > NightHomeTiles
			|| !KnownStations.TryGetValue(station, out TilePos at) || at.ChebyshevTo(inside) <= HomeStationTiles || at.ChebyshevTo(ctx.Node) <= StationSightTiles
			|| RawOre(item, ctx, 0) is not (int ore, int tile))
			return null;
		IGameKnowledge k = ctx.Knowledge;
		int target = ctx.Inventory.Count(ore) + Math.Max(1, OreIn(item, ore, ctx, 0));
		return Do($"night: mining {k.ItemName(ore)} to {target}; {k.ItemName(item)} at the {k.TileName(station)} at {at} ({at.ChebyshevTo(ctx.Node)} tiles away) by day",
			new MineSkill(ore, tile, target) { Also = AltOres(ore, ctx) });
	}

	/// <summary>
	/// A station other than the work bench (the anvil), carried, with a house: placed in the house, home first (by a recall
	/// potion if one is carried), as the bench and furnace are made there at night (<see cref="StationAtHomeTonight"/>). Not
	/// beside the work bench wherever that was last made (<see cref="StationHereInstead"/>). Seen in game: the anvil went 155
	/// tiles from the house in measured run U10 (2233,333), and 156 tiles west and 83 rows down in U9 (1922,365); every
	/// craft at it was a walk there, and in U10 all three nights began "staying in the house instead of WalkTo (walking to
	/// the Anvils...)", 29.5 game hours. Null when that is not the case, or a walk home to place one failed lately (then it
	/// goes down where the bot is, as before).
	/// </summary>
	private ISkill StationHomeFirst(string name, int carried, BotContext ctx)
	{
		if (HomeInside(ctx) is not TilePos inside || ctx.Tick - homePlaceFailedAt < StayHomeRetryTicks)
			return null;
		if (inside.ChebyshevTo(ctx.Node) <= HomeArrivedTiles)
			return Do($"placing a {name} in the house", homePlace = new PlaceItemSkill(carried));
		if (RecallHome(ctx, inside) is ISkill recall)
			return recall;
		homePlaceWalk = new WalkToSkill(new Core.Pathfinding.GoalNearTile(inside, 1, ctx.Physics)) { Quiet = true, Home = true };
		return Do($"home first, to place a {name} in the house ({inside.ChebyshevTo(ctx.Node)} tiles away)", homePlaceWalk);
	}

	private WalkToSkill homePlaceWalk;
	private PlaceItemSkill homePlace;
	private long homePlaceFailedAt = long.MinValue / 2;

	/// <summary>
	/// A valid house, or: pick a site (near the work bench), get the materials it still needs (blocks for the
	/// missing frame cells, walls for the bare ones, each furniture item not yet placed), then build.
	/// </summary>
	/// <summary>
	/// A house of this blueprint standing near <paramref name="anchor"/>: its door where the plan puts one, valid housing, and the
	/// room the plan's own (its bounds the plan's inside, IWorldInfo.RoomAt). Without that last a house of another shape with a door
	/// in the same place passed for the bot's own (a found house, ProgressionPlanner.FoundHome, is lived in by its rooms instead).
	/// </summary>
	private static TilePos? BuiltHouseNear(BotContext ctx, Blueprint bp, TilePos anchor)
	{
		int doorTile = ctx.Knowledge?.TilePlacedBy(ctx.Ids.Door) ?? -1;
		PlanItem door = bp.Items.FirstOrDefault(it => it.ItemType == ctx.Ids.Door);
		if (doorTile < 0 || door == null || ctx.World == null)
			return null;
		var tried = new HashSet<TilePos>();
		foreach (TilePos d in ctx.World.FindTiles(doorTile, anchor, SiteFinder.SearchRadius))
			for (int dy = 0; dy < door.Height; dy++) {
				var origin = new TilePos(d.X - door.X, d.Y - dy - door.Y);
				if (tried.Add(origin) && ctx.World.CheckHousing(origin.X + bp.Inside.X, origin.Y + bp.Inside.Y) == null
					&& (ctx.World.RoomAt(origin.X + bp.Inside.X, origin.Y + bp.Inside.Y) is not var (min, max)
						|| min == new TilePos(origin.X + 1, origin.Y + 1) && max == new TilePos(origin.X + bp.Width - 2, origin.Y + bp.Height - 2)))
					return origin;
			}
		return null;
	}

	private ISkill ResolveHouse(bool heartLantern, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		Blueprint bp = Blueprint.SmallHouse(ctx.Ids, heartLantern);
		if (HouseSite is TilePos built && ctx.World.CheckHousing(built.X + bp.Inside.X, built.Y + bp.Inside.Y) == null
			&& bp.Items.All(it => BuildSkill.IsPlaced(ctx, bp, built, it)))
			return null;
		if (HouseSite == null && FoundHomeValid(ctx) is TilePos home)
			return heartLantern ? LanternInFoundHome(home, claims, depth, ctx) : null;
		if (HouseSite == null) {
			// By the work bench, unless that is far from where the bot respawns: a bench put up 120 tiles off underground (no
			// way back to the old one) left no site within reach, and auto mode stopped. Then near the spawn, like a player.
			TilePos anchor = KnownStations.TryGetValue(ctx.Ids.WorkBenchTile, out TilePos bench) ? bench : ctx.Node;
			if (ctx.SpawnTile is TilePos spawn && anchor.ChebyshevTo(spawn) > HouseNearSpawnTiles)
				anchor = spawn;
			// A house of this kind already standing (built before this session: a saved world, a checkpoint) is the house:
			// found by its door, checked by the game's own housing check, not built again beside it.
			if (BuiltHouseNear(ctx, Blueprint.SmallHouse(ctx.Ids), anchor) is TilePos standing) {
				HouseSite = standing;
				ctx.Log.Info($"Auto: a house already stands at {standing}; living in it");
				return ResolveHouse(heartLantern, claims, depth, ctx);
			}
			// Else a house of any kind standing there, its rooms what the game takes for housing (a mod's base for a new world).
			if (LookForFoundHome(ctx, anchor))
				return ResolveHouse(heartLantern, claims, depth, ctx);
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
	/// the missing floor, the torches and campfire not yet placed), then build it with <see cref="BuildSkill"/>. With no site
	/// within <see cref="Blueprint.ArenaSearchRadius"/>, one up to <see cref="Blueprint.ArenaFarSearchRadius"/> away; with
	/// none there either, the boss is fought on open ground (<see cref="ArenaOnOpenGround"/>): the arena is a convenience,
	/// not a need. Seen in game (measured runs U8cp and U9): "no arena site" was a stop, five rests and auto mode off, the
	/// Eye never summoned, with max life 200, the bow and the arrows in hand.
	/// <para>
	/// The Eater of Worlds' arena (<see cref="ArenaBuilt.InEvilBiome"/>) is the same arena with its own memory
	/// (<see cref="EvilArenaSite"/>), looked for round <see cref="EvilGround"/> instead of the house, its site's evil-biome cost
	/// inverted (<see cref="Blueprint.InEvilBiome"/>): it is wanted in the Corruption zone, where Worm Food works and the worm
	/// stays (spec G9). The Eye's arena does if it is ready and lies in the zone (the M10 world's at (1948,278) did, before
	/// fix-arena2 made such sites dear). Seen in game (measured runs M10e and M10j): summoned where the Worm Food was made, in a
	/// pocket 130 rows down, then on the corruption's open ground at (1874,291); "no room to run from the worm's head" 20 and
	/// 11 times, the bot killed in the pocket, and on the open ground run down a chasm into water at (1921,358), the worm lost.
	/// </para>
	/// </summary>
	private ISkill ResolveArena(ArenaBuilt need, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		bool evil = need.InEvilBiome;
		ArenaSlot slot = evil ? evilArena : eyeArena;
		Blueprint bp = Blueprint.Arena(ctx.Ids, inEvilBiome: evil);
		string name = evil ? $"arena in the {ctx.Ids.EvilName}" : "arena";
		if (evil) {
			ctx.EvilErrand = true;
			if (slot.Site == null && !slot.OnOpenGround && EyeArenaInTheZone(ctx) is TilePos eye) {
				slot.Site = eye;
				ctx.Log.Info($"Auto: the arena at {eye} lies in the {ctx.Ids.EvilName} ({ctx.CorruptionAround(Middle(bp, eye))} {ctx.Ids.EvilName} tiles round its middle): "
					+ "the Eater of Worlds is fought there");
			}
		}
		if (slot.Site is TilePos done && ArenaReady(ctx, bp, done))
			return null;
		if (slot.Site == null && slot.OnOpenGround)
			return null;
		if (slot.Site == null) {
			TilePos anchor;
			if (evil) {
				if (EvilGround(ctx) is not TilePos ground)
					return ExploreForEvilBiome(ctx, "for the Eater of Worlds' arena");
				anchor = ground;
			}
			else
				anchor = HouseSite ?? (KnownStations.TryGetValue(ctx.Ids.WorkBenchTile, out TilePos bench) ? bench : ctx.Node);
			var keepOut = new List<(TilePos, TilePos)>();
			if (HouseSite is TilePos h) {
				Blueprint house = Blueprint.SmallHouse(ctx.Ids);
				keepOut.Add((new TilePos(h.X - ArenaHouseGap, h.Y - ArenaHouseGap),
					new TilePos(h.X + house.Width - 1 + ArenaHouseGap, h.Y + house.Height - 1 + ArenaHouseGap)));
			}
			// Nor over a house that stood: its rooms, the same gap as the house's.
			foreach (var (min, max, _) in foundRooms)
				keepOut.Add((new TilePos(min.X - 1 - ArenaHouseGap, min.Y - 1 - ArenaHouseGap), new TilePos(max.X + 1 + ArenaHouseGap, max.Y + 1 + ArenaHouseGap)));
			// Nor over the NPC rooms: the same gap as the house's.
			foreach (var (r, _) in npcRooms)
				keepOut.Add((new TilePos(r.X - ArenaHouseGap, r.Y - ArenaHouseGap),
					new TilePos(r.X + Blueprint.NpcRoomWidth - 1 + ArenaHouseGap, r.Y + Blueprint.SmallHouse(ctx.Ids).Height - 1 + ArenaHouseGap)));
			// Not over the Eye's arena (its floor is protected once ready): beside it, or on it when it lies in the zone (above).
			if (evil && eyeArena.Site is TilePos e)
				keepOut.Add((e, new TilePos(e.X + bp.Width - 1, e.Y + bp.Height - 1)));
			foreach (TilePos c in slot.BadCells)
				keepOut.Add((c, c));
			slot.Site = SiteFinder.Find(ctx, bp, anchor, slot.BadSites, keepOut);
			if (slot.Site == null) {
				ctx.Log.Info($"Auto: no {name} site ({bp.Width} wide, at most {bp.MaxEdits} edits) within {bp.SearchRadius} tiles of {anchor}: "
					+ $"{SiteFinder.Explain(ctx, bp, anchor, keepOut, slot.BadSites)}; looking within {Blueprint.ArenaFarSearchRadius}");
				Blueprint far = Blueprint.Arena(ctx.Ids, searchRadius: Blueprint.ArenaFarSearchRadius, inEvilBiome: evil);
				slot.Site = SiteFinder.Find(ctx, far, anchor, slot.BadSites, keepOut);
				if (slot.Site == null) {
					slot.OnOpenGround = true;
					ctx.Log.Info($"Auto: no {name} site within {far.SearchRadius} tiles of {anchor} either: "
						+ $"{SiteFinder.Explain(ctx, far, anchor, keepOut, slot.BadSites)}; the boss will be fought on open ground");
					return null;
				}
			}
			TilePos chosen = slot.Site.Value;
			bool inEvil = SiteFinder.InEvilBiome(ctx, bp, chosen);
			ctx.Log.Info($"Auto: {name} site {chosen} ({SiteFinder.Edits(ctx, bp, chosen)} edits"
				+ (evil ? $", floor row {chosen.Y + bp.Height - 1}, {ctx.CorruptionAround(Middle(bp, chosen))} {ctx.Ids.EvilName} tiles round its middle, {SiteFinder.CorruptionOnceBuilt(ctx, bp, chosen, Middle(bp, chosen))} once built"
					+ $" (the zone wants {BotContext.CorruptionZoneTiles}, {SiteFinder.ZoneMargin} over it for the kiting: {SiteFinder.ZoneShortfall(ctx, bp, chosen)} short)" : "")
				+ (inEvil != evil ? $", {(evil ? "off" : "in")} the evil biome: each step costs {SiteFinder.EvilStepFactor}x" : "") + ")");
		}
		return BuildArena(slot.Site.Value, bp, name, claims, depth, ctx);
	}

	/// <summary>An arena's site chosen: what it still needs (blocks for the floor, the torches and campfire), then its build.</summary>
	private ISkill BuildArena(TilePos site, Blueprint bp, string name, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		int blocks = 0;
		for (int x = 0; x < bp.Width; x++)
			for (int y = 0; y < bp.Height; y++)
				if (bp.At(x, y) == PlanCell.Block && !ctx.Grid.Get(site.X + x, site.Y + y).IsBlock())
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
		// From deep down, up to the surface first, as for a trip (UpFirst), not the build's walk to its first cell through the
		// caves. Seen in game (measured runs U19 and U22): max life 200 came 160+ rows down, and the build's first walk, to
		// fell a tree by the arena, went through the caves with fights for about 5 game hours.
		if (UpFirst(ctx, site) is WalkToSkill up)
			return Do($"up to the surface before building the {name} at {site}", up);
		return Do($"building the {name} at {site}", new BuildSkill(bp, site));
	}

	/// <summary>Where a player stands in the arena's middle (the fight's centre, and where the zone is counted).</summary>
	private static TilePos Middle(Blueprint bp, TilePos site) => new(site.X + bp.Width / 2, site.Y + bp.StandRow);

	/// <summary>The Eye's arena, if it is ready and the Corruption zone holds at its middle (<see cref="BotContext.CorruptionAround"/>).</summary>
	private TilePos? EyeArenaInTheZone(BotContext ctx)
	{
		if (eyeArena.Site is not TilePos s)
			return null;
		Blueprint bp = Blueprint.Arena(ctx.Ids);
		return ArenaReady(ctx, bp, s) && ctx.CorruptionAround(Middle(bp, s)) >= BotContext.CorruptionZoneTiles ? s : null;
	}

	private (TilePos?, TilePos?, bool, TilePos?, bool, int, TilePos?, bool, (int, int, int)) protectedFor = (null, null, false, null, false, 0, null, false, (0, 0, 0));
	private int protectedFoundRooms;
	private int protectedNpcRooms;

	/// <summary>
	/// Keep what was built off limits to path moves (<see cref="BotContext.Protected"/>): every cell of the house,
	/// the arenas' floors once each arena is ready, and the chasm bridges. Rebuilt only when a site changes, an arena gets
	/// ready (or not) or a bridge is built. Until it is ready an arena's floor is the build's to put back
	/// (<see cref="Blueprint.RebuildBlocks"/>): seen in game (measured run U23), the floor bridged over a dip, protected from
	/// the moment the site was picked, shut the walk in under it ("no path from (1950,251) ... rows 251..374").
	/// </summary>
	private void ProtectBuilt(BotContext ctx)
	{
		Blueprint arena = Blueprint.Arena(ctx.Ids), dungeon = DungeonArenaPlan(ctx.Ids, dungeonArena.Width);
		bool eyeReady = eyeArena.Site is TilePos ready && ArenaReady(ctx, arena, ready);
		bool evilReady = evilArena.Site is TilePos evilSite && ArenaReady(ctx, arena, evilSite);
		bool dungeonReady = dungeonArena.Site is TilePos dungeonSite && ArenaReady(ctx, dungeon, dungeonSite);
		var key = (HouseSite, eyeArena.Site, eyeReady, evilArena.Site, evilReady, Bridges.Count, dungeonArena.Site, dungeonReady, HellbridgeKey(ctx));
		if (protectedFor == key && protectedNpcRooms == npcRooms.Count && protectedFoundRooms == foundRooms.Count && ctx.Protected != null)
			return;
		protectedFor = key;
		protectedNpcRooms = npcRooms.Count;
		protectedFoundRooms = foundRooms.Count;
		var cells = new HashSet<TilePos>();
		if (HouseSite is TilePos h) {
			Blueprint house = Blueprint.SmallHouse(ctx.Ids);
			for (int x = 0; x < house.Width; x++)
				for (int y = 0; y < house.Height; y++)
					if (house.At(x, y) != PlanCell.None)
						cells.Add(new TilePos(h.X + x, h.Y + y));
		}
		// The NPC rooms built, as the house (the one being built is the build's own: BotContext.Reserved).
		foreach (var (r, shared) in npcRooms) {
			Blueprint room = Blueprint.NpcRoom(ctx.Ids, shared);
			for (int x = 0; x < room.Width; x++)
				for (int y = 0; y < room.Height; y++)
					if (room.At(x, y) != PlanCell.None)
						cells.Add(new TilePos(r.X + x, r.Y + y));
		}
		// A house that stood: its rooms and the frame round them (walls, floors, ceilings, doors), as the bot's own house.
		foreach (var (min, max, _) in foundRooms)
			for (int x = min.X - 1; x <= max.X + 1; x++)
				for (int y = min.Y - 1; y <= max.Y + 1; y++)
					cells.Add(new TilePos(x, y));
		foreach ((bool isReady, TilePos? at) in new[] { (eyeReady, eyeArena.Site), (evilReady, evilArena.Site) })
			if (isReady && at is TilePos a)
				for (int x = 0; x < arena.Width; x++)
					for (int y = 0; y < arena.Height; y++)
						if (arena.At(x, y) == PlanCell.Block)
							cells.Add(new TilePos(a.X + x, a.Y + y));
		if (dungeonReady && dungeonArena.Site is TilePos d)
			for (int x = 0; x < dungeon.Width; x++)
				for (int y = 0; y < dungeon.Height; y++)
					if (dungeon.At(x, y) == PlanCell.Block)
						cells.Add(new TilePos(d.X + x, d.Y + y));
		foreach (ChasmOpening o in Bridges)
			for (int x = o.X0; x <= o.X1; x++)
				cells.Add(new TilePos(x, o.Row));
		ProtectHellbridge(ctx, cells);
		ctx.Protected = cells;
	}

	/// <summary>Floor solid, open cells empty (no trees), torches and campfire placed.</summary>
	public static bool ArenaReady(BotContext ctx, Blueprint bp, TilePos site)
	{
		for (int x = 0; x < bp.Width; x++)
			for (int y = 0; y < bp.Height; y++) {
				int wx = site.X + x, wy = site.Y + y;
				PlanCell c = bp.At(x, y);
				if (c == PlanCell.Block && !ctx.Grid.Get(wx, wy).IsBlock())
					return false;
				if (c == PlanCell.Clear && !bp.Items.Any(it => it.Covers(x, y)) && (ctx.Grid.Get(wx, wy).IsBlock() || ctx.World.IsTreeTrunk(wx, wy)))
					return false;
			}
		return bp.Items.All(it => it.Optional || BuildSkill.IsPlaced(ctx, bp, site, it)); // a campfire left out: still an arena
	}

	/// <summary>The arena's columns and the feet row of a player standing on its floor, if there is one.</summary>
	public (int MinX, int MaxX, int StandY)? ArenaBounds(BotContext ctx) => Bounds(ctx, eyeArena.Site);

	/// <summary>The Eater of Worlds' arena in the corruption (<see cref="EvilArenaSite"/>), as <see cref="ArenaBounds"/>.</summary>
	public (int MinX, int MaxX, int StandY)? EvilArenaBounds(BotContext ctx) => Bounds(ctx, evilArena.Site);

	/// <summary>Skeletron's arena by the Dungeon (<see cref="DungeonArenaSite"/>), as <see cref="ArenaBounds"/>.</summary>
	public (int MinX, int MaxX, int StandY)? DungeonArenaBounds(BotContext ctx) => Bounds(ctx, dungeonArena.Site, dungeonArena.Width);

	private static (int MinX, int MaxX, int StandY)? Bounds(BotContext ctx, TilePos? site, int width = Blueprint.ArenaWidth)
	{
		if (site is not TilePos s)
			return null;
		Blueprint bp = Blueprint.Arena(ctx.Ids, width);
		return (s.X + 1, s.X + bp.Width - 2, s.Y + bp.StandRow); // inside the end posts
	}

	/// <summary>
	/// Onto the arena's floor first, anywhere along it (its ends are open to the world), then along the floor to the middle.
	/// Seen in game (measured run U7): from under the floor (protected from path moves), the walk to the 9 tiles over the
	/// middle made partial plans of 3,000-14,000 nodes straight up at it all night, and the summon waited a whole day.
	/// </summary>
	private static WalkToSkill ArenaWalk(BotContext ctx, int x0, int x1, int standY, int? toX = null)
	{
		TilePos n = ctx.Node;
		int mid = toX ?? (x0 + x1) / 2;
		bool onFloor = n.Y == standY && n.X >= x0 && n.X <= x1;
		return new WalkToSkill(onFloor
			? new Core.Pathfinding.GoalStandOnRow(standY, mid - ArenaCentreTolerance, mid + ArenaCentreTolerance, ctx.Physics)
			: new Core.Pathfinding.GoalStandOnRow(standY, x0, x1, ctx.Physics));
	}

	private static bool InArena(TilePos n, int x0, int x1, int standY) =>
		Math.Abs(n.X - (x0 + x1) / 2) <= ArenaCentreTolerance + 1 && n.Y == standY;

	/// <summary>
	/// Defeated, or: get the summon item (recursively), wait for night (<see cref="BossDefeated.NightOnly"/>), walk to the
	/// arena, then summon and fight. A boss of the evil biome (<see cref="BossDefeated.InEvilBiome"/>) is summoned and fought
	/// in its zone instead, at any time of day (<see cref="ResolveEvilBoss"/>).
	/// </summary>
	private ISkill ResolveBoss(BossDefeated b, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (ctx.World.BossDefeated(b.Npc) && !b.Again)
			return null;
		bool bossHere = BossUp(ctx, b.Npc);
		if (b.ByOldMan)
			return ResolveSkeletron(b, bossHere, claims, depth, ctx);
		if (b.ByVoodooDoll)
			return ResolveWallOfFlesh(b, bossHere, ctx);
		if (!bossHere) {
			ISkill sub = Resolve(new HaveItem(b.SummonItem, 1, ctx.Knowledge.ItemName(b.SummonItem)), claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		if (b.InEvilBiome)
			return ResolveEvilBoss(b, bossHere, claims, depth, ctx);
		if (!bossHere) {
			// The summon in hand, by day, a night boss: everything below by day is the wait for night (the walk to the arena to
			// wait there, the wait), and the day goes to the plan's later work if it has any (DayWorkBeforeTheNight).
			nightOnlyByDay = b.NightOnly && !ctx.IsNight;
			// To the arena first, by day, then wait there for night: seen in game (measured run S14), the summon made at an
			// altar 360 rows down, the bot waited there all day, set off at nightfall, and the walk took the night.
			if (ArenaBounds(ctx) is (int bx0, int bx1, int by) && !InArena(ctx.Node, bx0, bx1, by)) {
				return Do($"walking to the arena to summon the {b.Label}", ArenaWalk(ctx, bx0, bx1, by));
			}
			// No arena: open ground on the surface, not wherever the summon item was made. Seen in game (measured run U23,
			// world_eye): the arena given up ("the boss will be fought on open ground"), the Suspicious Looking Eye made at an
			// altar in a pocket at row 408, some 160 rows down; the night waited for and the Eye fought there, "forced to turn
			// (wall)" every few seconds in 17 tiles of room, 28800 ticks, the Eye at 1927/2800 when the fight gave up.
			if (ArenaBounds(ctx) == null && UnderTheSurface(ctx))
				return Do($"up to the surface to summon the {b.Label} on open ground (no arena)", new WalkToSkill(
					new Core.Pathfinding.GoalRow(SurfaceRow(ctx) + TravelFromSurfaceRows - 3, ctx.Node.X, ctx.Physics) { Accept = t => !ctx.OffLimits(t) }) { Quiet = true, ToSurface = true });
			if (b.NightOnly && !ctx.IsNight)
				return Do($"waiting for night to summon the {b.Label}", new WaitSkill("night", c => c.IsNight, NightWaitTicks));
			// The Eye leaves at dawn: seen in game, summoned late after a long fight with demon eyes, it flew off at
			// 1275/2800. Not enough night left: wait for the next one (through the day).
			if (b.NightOnly && ctx.NightTicksLeft > 0 && ctx.NightTicksLeft < MinNightForBoss)
				return Do($"too little night left for the {b.Label} ({ctx.NightTicksLeft / 60}s); waiting for the next night",
					new WaitSkill("next night", c => c.IsNight && c.NightTicksLeft >= MinNightForBoss, NightWaitTicks + 32400));
			if (ArenaBounds(ctx) is (int ax0, int ax1, int ay) && !InArena(ctx.Node, ax0, ax1, ay)) {
				return Do($"walking to the arena to summon the {b.Label}", ArenaWalk(ctx, ax0, ax1, ay));
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

	/// <summary>
	/// Is the boss up? A part of it in view (any segment of a worm: the head may be the part out of sight), or, for a worm,
	/// its health bar showing life (<see cref="IWorldInfo.BossBarLife"/>): a burrowed worm is out of view but still on the bar,
	/// as a player reads it. The Eye of Cthulhu fights in view (BossPerceptionRadiusPx, 200 tiles round the bot). The Brain of
	/// Cthulhu: itself or a Creeper in view, or its bar (its life and the Creepers', BrainOfCthuluBigProgressBar.cs:29-41).
	/// </summary>
	public static bool BossUp(BotContext ctx, int npc)
	{
		if (BrainPattern.IsBrain(npc))
			return ctx.Hostiles.Any(h => BrainPattern.IsPart(h.Type)) || ctx.World != null && ctx.World.BossBarLife(npc) > 0;
		int[] parts = WormPattern.SegmentTypes(npc);
		// The Wall of Flesh: its mouth or an eye in view (they share its life), or its bar (it is 75 tiles off when it spawns).
		if (WallOfFleshPattern.IsWallOfFlesh(npc))
			return WallOfFleshPattern.Wall(ctx.Hostiles) != null || ctx.World != null && ctx.World.BossBarLife(npc) > 0;
		return ctx.Hostiles.Any(h => parts.Contains(h.Type)) || WormPattern.IsWormBoss(npc) && ctx.World != null && ctx.World.BossBarLife(npc) > 0;
	}

	/// <summary>The plan's boss that is up and not yet defeated, if any (<see cref="BossUp"/>).</summary>
	private BossDefeated BossUpInThePlan(BotContext ctx) => ctx.World == null ? null
		: StagesFor(Plan, ctx.Ids).OfType<BossDefeated>().FirstOrDefault(b => !ctx.World.BossDefeated(b.Npc) && BossUp(ctx, b.Npc));

	// ---------- the Dungeon and Skeletron (Milestone 11, docs M11_SPEC.md) ----------

	/// <summary>Skeletron's arena stage (<see cref="ArenaBuilt.AtDungeon"/>).</summary>
	public const string DungeonArenaLabel = "dungeon arena";
	/// <summary>
	/// The Dungeon's bricks are seen from this far (tiles): its entrance towers some 30 rows over the ground round it (the M11
	/// world's to row 169 over snow at 202-225, docs M11_SPEC.md section 1c), on screen from about 60 tiles.
	/// </summary>
	public const int DungeonSightTiles = 60;
	/// <summary>Each walk along the surface looking for the Dungeon goes this far (a trip's length, <see cref="TravelTiles"/>).</summary>
	public const int DungeonHopTiles = TravelTiles;
	/// <summary>After a walk looking for the Dungeon failed, the surface that way is explored instead for this long (a game hour).</summary>
	public const int DungeonHopRetryTicks = 3600;
	/// <summary>
	/// How far (columns) each side of the spawn the surface is looked at for the snow and the jungle: the snow's middle lies at
	/// 25-40 % or 60-75 % of the world's width and the jungle's at 15-30 % or 70-85 % (WorldGen.cs:8224-8259), so from the
	/// spawn in the middle at most 35 % of the width, 2,940 of a large world's 8,400; the snow is 90-170 tiles wide each way
	/// (WorldGen.cs:8260-8283), so 2,400 reaches it in every size.
	/// </summary>
	public const int DungeonScanTiles = 2400;
	/// <summary>The side the surface's look found is kept this long (a game hour: the map fills in on the way).</summary>
	public const int DungeonSideRecheckTicks = 3600;
	/// <summary>The night's length (Main.nightLength, Main.cs:1468): Main.time at night is this less <see cref="BotContext.NightTicksLeft"/>.</summary>
	public const int NightLengthTicks = 32400;
	/// <summary>
	/// Summon Skeletron only until this Main.time of the night (10:30 PM): 6 minutes before dawn, when it turns to 1000 damage
	/// (NPC.cs:22206-22210), against a fight of 2-3 minutes with the Demon Bow and about 6 with the iron bow (docs M11_SPEC.md
	/// section 3e).
	/// </summary>
	public const int SummonByTime = 10800;
	/// <summary>Without a recall potion to escape dawn with, an hour and a half earlier (spec S1): the margin is the only escape.</summary>
	public const int SummonByTimeNoRecall = 5400;
	/// <summary>Walk to within this many tiles of the Old Man's feet for chat range (ChatButtonSkill: 80 px either side of our centre).</summary>
	public const int ChatWalkTiles = 2;
	/// <summary>
	/// With the Old Man not in view this near where he was, he is gone (summoned tonight, or killed): well inside the 50 tiles
	/// town NPCs are seen from (TerraBotPlayer.PerceptionRadiusPx). He comes back only by day (Main.cs:83484-83490, the daytime
	/// branch of UpdateTime): one summon a night.
	/// </summary>
	public const int OldManGoneTiles = 30;
	/// <summary>
	/// Skeletron's arena's near end within this many columns of the Old Man (spec S5: wait within about 20 tiles of him): the
	/// summon is at him, under his porch's roof, and the hands' first attack comes 150 ticks after the head spawns (the right
	/// hand's timer starts at 150, NPC.cs:22188), in which the player runs 150 × 3 px = 28 tiles; 20 leaves the jump over the
	/// arena's end post and a start.
	/// </summary>
	public const int DungeonArenaNearTiles = 20;
	/// <summary>With no site that near, as far as the spec's 60 tiles from the Old Man (docs M11_SPEC.md S4).</summary>
	public const int DungeonArenaFarTiles = 60;
	/// <summary>Columns of the arena's end by the Old Man where the bot waits for night (the arena's own centre tolerance, both ways).</summary>
	public const int WaitSpanTiles = 2 * ArenaCentreTolerance;

	private TilePos? oldManAt, dungeonBricksAt;
	private int? dungeonSide, dungeonSideForced;
	private string dungeonSideWhy = "";
	private long dungeonSideAt = long.MinValue / 2;
	private int dungeonSideFlips;
	private long dungeonHopFailedAt = long.MinValue / 2;

	/// <summary>Where the Old Man was last seen (feet tile): the Dungeon's entrance, his home (docs M11_SPEC.md section 1c).</summary>
	public TilePos? OldManAt => oldManAt;
	/// <summary>The Dungeon as found: the Old Man, else a dungeon brick seen on the surface.</summary>
	public TilePos? DungeonFoundAt => oldManAt ?? dungeonBricksAt;
	/// <summary>The bot has been in the Old Man's chat range.</summary>
	public bool OldManMet { get; private set; }
	/// <summary>Skeletron's preparation (spec S1) was met: the plan got to the Dungeon's stages.</summary>
	public bool SkeletronPrepMet { get; private set; }
	/// <summary>Top-left tile of Skeletron's arena by the Dungeon, being built or built.</summary>
	public TilePos? DungeonArenaSite => dungeonArena.Site;
	/// <summary>No arena site by the Dungeon: Skeletron is fought on the open ground there.</summary>
	public bool DungeonArenaOnOpenGround => dungeonArena.OnOpenGround;
	/// <summary>Skeletron's arena as planned (its width, <see cref="Blueprint.DungeonArenaWidth"/> or the standard one).</summary>
	public Blueprint DungeonArenaBlueprint(GameIds ids) => DungeonArenaPlan(ids, dungeonArena.Width);

	private static Blueprint DungeonArenaPlan(GameIds ids, int width, int radius = Blueprint.ArenaSearchRadius) =>
		Blueprint.Arena(ids, width, radius, atDungeon: true);

	private static bool SkeletronStage(Need stage) =>
		stage is PrepFor { Inner: DungeonFound or ArenaBuilt { AtDungeon: true } } || stage is BossDefeated { ByOldMan: true };

	/// <summary>The Old Man, if in view (a town NPC, drawn with his name).</summary>
	public static EntityInfo? OldManInView(BotContext ctx)
	{
		if (ctx.Ids.OldMan <= 0)
			return null;
		foreach (EntityInfo n in ctx.TownNpcs)
			if (n.Type == ctx.Ids.OldMan)
				return n;
		return null;
	}

	/// <summary>
	/// The nearest dungeon brick the map shows within <see cref="DungeonSightTiles"/> on or above the surface row: the Dungeon's
	/// entrance (its bricks below the surface are the Dungeon proper, docs M11_SPEC.md section 1b).
	/// </summary>
	public static TilePos? SeenDungeonBricks(BotContext ctx)
	{
		if (ctx.World == null)
			return null;
		TilePos? best = null;
		foreach (int tile in ctx.Ids.DungeonBrickTiles)
			foreach (TilePos c in ctx.World.FindTiles(tile, ctx.Node, DungeonSightTiles))
				if (ctx.SurfaceY <= 0 || c.Y <= ctx.SurfaceY) {
					if (best == null || c.ChebyshevTo(ctx.Node) < best.Value.ChebyshevTo(ctx.Node))
						best = c;
					break; // nearest first
				}
		return best;
	}

	private static bool DungeonInView(BotContext ctx) => OldManInView(ctx) != null || SeenDungeonBricks(ctx) != null;

	/// <summary>What the bot sees of the Dungeon this pass: the Old Man (remembered where he was), or its bricks.</summary>
	private void NoteDungeon(BotContext ctx)
	{
		if (Plan != Plans.Skeletron || ctx.Ids.OldMan <= 0 || ctx.World == null || ctx.World.BossDefeated(ctx.Ids.SkeletronHead))
			return;
		if (OldManInView(ctx) is EntityInfo om) {
			if (oldManAt == null)
				ctx.Log.Info($"Auto: the Old Man at {om.FeetTile}: the Dungeon is found ({om.FeetTile.ChebyshevTo(ctx.Node)} tiles away, us at {ctx.Node})");
			oldManAt = om.FeetTile;
			if (!OldManMet && ChatButtonSkill.InChatRange(ctx, om)) {
				OldManMet = true;
				ctx.Log.Info($"Auto: met the Old Man at {om.FeetTile}");
			}
		}
		else if (oldManAt == null && dungeonBricksAt == null && SeenDungeonBricks(ctx) is TilePos b) {
			dungeonBricksAt = b;
			ctx.Log.Info($"Auto: the Dungeon's bricks seen at {b} ({b.ChebyshevTo(ctx.Node)} tiles away, us at {ctx.Node})");
		}
	}

	private static string Way(int side) => side < 0 ? "west" : "east";

	/// <summary>
	/// Which way the Dungeon is (-1 west, 1 east), as a player without the map would judge it (docs M11_SPEC.md section 1c):
	/// toward it, once seen; else toward the snow seen on the surface, which world generation puts on the Dungeon's side; else
	/// away from the jungle, which it puts on the other (WorldGen.cs:8209-8259); else west, a coin flip (the spec's order), and
	/// the other way once the world's edge is reached with no Dungeon. The surface's look is kept a game hour.
	/// </summary>
	public int DungeonSide(BotContext ctx, out string because)
	{
		if (DungeonFoundAt is TilePos d && d.X != ctx.Node.X) {
			because = $"the Dungeon seen at {d}";
			return Math.Sign(d.X - ctx.Node.X);
		}
		if (dungeonSideForced is int forced) {
			because = $"the world's edge reached {Way(-forced)} with no Dungeon";
			return forced;
		}
		if (dungeonSide is int kept && ctx.Tick - dungeonSideAt < DungeonSideRecheckTicks) {
			because = dungeonSideWhy;
			return kept;
		}
		int originX = ctx.SpawnTile?.X ?? ctx.Node.X;
		var snow = new int[2];
		var jungle = new int[2];
		foreach (int dir in new[] { -1, 1 })
			for (int dx = RouteSampleTiles; dx <= DungeonScanTiles; dx += RouteSampleTiles) {
				if (SurfaceAt(ctx.Grid, originX + dir * dx) is not TilePos c)
					continue; // nowhere to stand (off the world, or open to the bottom)
				int x = c.X, y = c.Y + 1;
				if (ctx.World == null || !ctx.World.IsRevealed(x, y))
					continue;
				int type = ctx.World.TileTypeAt(x, y);
				if (Array.IndexOf(ctx.Ids.SnowTiles, type) >= 0)
					snow[dir < 0 ? 0 : 1]++;
				else if (Array.IndexOf(ctx.Ids.JungleTiles, type) >= 0)
					jungle[dir < 0 ? 0 : 1]++;
			}
		int side;
		if (snow[0] > 0 != snow[1] > 0) {
			side = snow[0] > 0 ? -1 : 1;
			because = $"the snow seen {Way(side)} ({snow[0] + snow[1]} surface spots): the Dungeon is on the snow's side";
		}
		else if (jungle[0] > 0 != jungle[1] > 0) {
			side = jungle[0] > 0 ? 1 : -1;
			because = $"the jungle seen {Way(-side)} ({jungle[0] + jungle[1]} surface spots): the Dungeon is on the other side";
		}
		else {
			side = -1;
			because = "no snow nor jungle seen: west first";
		}
		if (dungeonSide != side)
			ctx.Log.Info($"Auto: the Dungeon is looked for {Way(side)}: {because}");
		dungeonSide = side;
		dungeonSideWhy = because;
		dungeonSideAt = ctx.Tick;
		return side;
	}

	/// <summary>
	/// <see cref="DungeonFound"/>: the Old Man seen, or (spec S2): to the bricks seen and round them for him; else along the
	/// surface toward <see cref="DungeonSide"/>, <see cref="DungeonHopTiles"/> at a time, each walk ending as soon as the
	/// Dungeon shows (<see cref="TravelUntilSkill"/>). By day only: at night the bot digs in until morning (spec S2), as for
	/// the night's shelter. Null once he has been seen.
	/// </summary>
	private ISkill ResolveDungeon(BotContext ctx, string why)
	{
		if (oldManAt != null)
			return null;
		CrossingTheEvilBiome(ctx);
		if (ctx.IsNight && ctx.Tick - lastShelterEnd >= ShelterCooldownTicks)
			return Do($"night: sheltering until morning on the way to the Dungeon {why} (the walk is by day)", new ShelterSkill());
		TilePos node = ctx.Node;
		if (dungeonBricksAt is TilePos b) {
			if (b.ChebyshevTo(node) > DungeonSightTiles / 2)
				return Do($"walking to the Dungeon's bricks seen at {b} ({b.ChebyshevTo(node)} tiles away) {why}",
					new TravelUntilSkill("the Old Man", new WalkToSkill(new Core.Pathfinding.GoalNearTile(b, TravelArrivedTiles, ctx.Physics)) { Quiet = true, Travel = true },
						c => OldManInView(c) != null));
			int toward = Math.Sign(b.X - node.X);
			return Do($"looking round the Dungeon's bricks at {b} for the Old Man {why}",
				new ExploreSkill("the Old Man", c => OldManInView(c) != null, ExploreBias.Sideways, toward != 0 ? toward : -1) { JobKey = "Explore old man" });
		}
		int side = DungeonSide(ctx, out string because);
		if (ctx.Tick - dungeonHopFailedAt < DungeonHopRetryTicks)
			return Do($"exploring {Way(side)} for the Dungeon {why} ({because}; the walk that way failed)",
				new ExploreSkill("the Dungeon", DungeonInView, ExploreBias.Sideways, side) { JobKey = $"Explore dungeon {side}" });
		if (SurfaceAt(ctx.Grid, node.X + side * DungeonHopTiles) is not TilePos dest) {
			// The world's edge that way, and no Dungeon: the other way (the spec's "west" was a coin flip).
			if (++dungeonSideFlips > 1) {
				stuck = $"no Dungeon found either way along the surface (at {node})";
				return null;
			}
			dungeonSideForced = -side;
			ctx.Log.Info($"Auto: the world's edge {Way(side)} of {node} and no Dungeon: looking {Way(-side)} instead");
			return ResolveDungeon(ctx, why);
		}
		return Do($"looking for the Dungeon {why}: along the surface {Way(side)} to {dest} ({because})",
			new TravelUntilSkill("the Dungeon", new WalkToSkill(new Core.Pathfinding.GoalNearTile(dest, TravelArrivedTiles, ctx.Physics)) { Quiet = true, Travel = true },
				DungeonInView));
	}

	/// <summary>
	/// The way to the Dungeon and back crosses the evil biome's surface where it lies between (the M11 world's corruption, 700
	/// tiles of it west of the spawn, docs M11_SPEC.md section 0): an errand there as the M10 ones are
	/// (<see cref="BotContext.EvilErrand"/>), its surface opened in full armour (BotContext.EvilSurfaceOpen) and its ground below
	/// as before. Else, with stone the pickaxe cannot break, every step of the 700 cost the paths their off-limits cost.
	/// </summary>
	private static void CrossingTheEvilBiome(BotContext ctx) => ctx.EvilErrand = true;

	/// <summary>The side of the Old Man the Dungeon's entrance opens to: toward the world's middle, where the spawn is (WorldGen.DungeonEnt, WorldGen.cs:32549).</summary>
	private static int EntranceSide(BotContext ctx, TilePos home)
	{
		int s = Math.Sign((ctx.SpawnTile?.X ?? ctx.Node.X) - home.X);
		return s != 0 ? s : 1;
	}

	/// <summary>
	/// Skeletron's arena (spec S4): a site by the Old Man on the side his entrance opens to (the Dungeon's halls are on the other:
	/// WorldGen.DungeonEnt carves the doorway and clears the ground on the side facing the world's middle, WorldGen.cs:32027,
	/// 32549), its near end at him, the floor raised over the lake there if need be (<see cref="Blueprint.RaisedFloor"/>), the sky
	/// over it open (<see cref="Blueprint.SkyRows"/>), and not in the Guardian's ground (SiteFinder). <see cref="Blueprint.DungeonArenaWidth"/>
	/// wide, else the standard width; near, then farther out; with none, open ground.
	/// </summary>
	private bool FindDungeonArenaSite(BotContext ctx, TilePos home)
	{
		ArenaSlot slot = dungeonArena;
		int side = EntranceSide(ctx, home);
		var keepOut = new List<(TilePos, TilePos)> {
			side > 0 ? (new TilePos(int.MinValue / 4, int.MinValue / 4), new TilePos(home.X - 1, int.MaxValue / 4))
				: (new TilePos(home.X + 1, int.MinValue / 4), new TilePos(int.MaxValue / 4, int.MaxValue / 4)),
		};
		foreach (TilePos c in slot.BadCells)
			keepOut.Add((c, c));
		foreach (int width in new[] { Blueprint.DungeonArenaWidth, Blueprint.ArenaWidth })
			foreach (int near in new[] { DungeonArenaNearTiles, DungeonArenaFarTiles }) {
				// The near end from the Old Man's column to `near` columns out on the entrance's side: the search looks at the
				// middles within half that of the middle of those sites.
				int radius = near / 2;
				Blueprint bp = DungeonArenaPlan(ctx.Ids, width, radius);
				var anchor = new TilePos(home.X + side * (width / 2 + radius), home.Y);
				if (SiteFinder.Find(ctx, bp, anchor, slot.BadSites, keepOut) is TilePos site) {
					slot.Site = site;
					slot.Width = width;
					ctx.Log.Info($"Auto: arena site by the Dungeon {site}: x {site.X}-{site.X + width - 1}, floor row {site.Y + bp.Height - 1}, "
						+ $"{SiteFinder.Edits(ctx, bp, site)} edits, {SiteFinder.SkyBlocks(ctx, bp, site)} blocks in the {bp.SkyRows} rows of sky over the floor; the Old Man at {home}");
					return true;
				}
				ctx.Log.Info($"Auto: no {width}-wide arena site within {radius} of {anchor} by the Dungeon: {SiteFinder.Explain(ctx, bp, anchor, keepOut, slot.BadSites)}");
			}
		slot.OnOpenGround = true;
		ctx.Log.Info($"Auto: no arena site by the Dungeon (the Old Man at {home}): Skeletron will be fought on the open ground there");
		return false;
	}

	/// <summary><see cref="ArenaBuilt.AtDungeon"/>: Skeletron's arena ready, or its site (<see cref="FindDungeonArenaSite"/>) and build.</summary>
	private ISkill ResolveDungeonArena(ArenaBuilt need, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (oldManAt is not TilePos home)
			return ResolveDungeon(ctx, "for Skeletron's arena");
		ArenaSlot slot = dungeonArena;
		if (slot.Site is TilePos done && ArenaReady(ctx, DungeonArenaPlan(ctx.Ids, slot.Width), done))
			return null;
		if (slot.Site == null && (slot.OnOpenGround || !FindDungeonArenaSite(ctx, home)))
			return null;
		if (ArenaReady(ctx, DungeonArenaPlan(ctx.Ids, slot.Width), slot.Site.Value))
			return null; // built before (a saved world): found as the site with nothing to do
		return BuildArena(slot.Site.Value, DungeonArenaPlan(ctx.Ids, slot.Width), "arena by the Dungeon", claims, depth, ctx);
	}

	/// <summary>The fight, summoned at the Old Man's chat button (<see cref="ChatButtonSkill"/>) unless the head is up already.</summary>
	private static BossFightSkill SkeletronFight(BossDefeated b, (int, int, int)? arena, BotContext ctx)
	{
		int oldMan = ctx.Ids.OldMan;
		return new BossFightSkill(b.SummonItem, b.Npc, b.Minions) { Arena = arena, Summon = () => new ChatButtonSkill(oldMan, "Old Man") };
	}

	/// <summary>
	/// Skeletron (spec S5-S7): with the head up, the fight; else by day to the arena's end by the Old Man, rest, and wait for night
	/// there; at night, while it is early enough (<see cref="SummonByTime"/>, <see cref="SummonByTimeNoRecall"/> without a recall
	/// potion) and the Old Man is there (one summon a night), rest to <see cref="SummonLifeFraction"/>, walk up to him into chat
	/// range, and the fight, whose summon is his "Curse" button. The fight walks back onto the arena from his porch itself
	/// (BossFightSkill.BackToArena), out from under the porch's roof.
	/// </summary>
	private ISkill ResolveSkeletron(BossDefeated b, bool bossHere, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		CrossingTheEvilBiome(ctx);
		var arena = DungeonArenaBounds(ctx);
		if (bossHere)
			return Do($"fighting {b.Label}{(arena != null ? " on the arena by the Dungeon" : " by the Dungeon")}", SkeletronFight(b, arena, ctx));
		if (oldManAt is not TilePos home)
			return ResolveDungeon(ctx, $"to summon {b.Label}");
		TilePos node = ctx.Node;
		bool atWait;
		WalkToSkill toWait;
		if (arena is (int x0, int x1, int y)) {
			bool nearWest = Math.Abs(x0 - home.X) <= Math.Abs(x1 - home.X);
			int from = nearWest ? x0 : x1 - WaitSpanTiles, to = nearWest ? x0 + WaitSpanTiles : x1;
			atWait = node.Y == y && node.X >= from && node.X <= to;
			toWait = new WalkToSkill(new Core.Pathfinding.GoalStandOnRow(y, from, to, ctx.Physics)) { Quiet = true };
		}
		else {
			atWait = home.ChebyshevTo(node) <= 2 * ArenaCentreTolerance;
			toWait = new WalkToSkill(new Core.Pathfinding.GoalNearTile(home, ArenaCentreTolerance, ctx.Physics)) { Quiet = true };
		}
		bool rest = ctx.Combat.LifeFraction < SummonLifeFraction && ctx.Tick - lastRestEnd > MaxRestTicks;
		WaitSkill Rest() => new("life", c => c.Combat.LifeFraction >= SummonLifeFraction, MaxRestTicks);
		if (!ctx.IsNight) {
			if (!atWait)
				return Do($"to the arena's end by the Old Man at {home} to wait for night ({b.Label})", toWait);
			if (rest)
				return Do($"resting to {SummonLifeFraction:P0} life by the Dungeon before night ({ctx.Combat.Life}/{ctx.Combat.LifeMax})", Rest());
			return Do($"waiting by the Dungeon for night to summon {b.Label}", new WaitSkill("night", c => c.IsNight, NightWaitTicks));
		}
		int byTime = ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0 ? SummonByTime : SummonByTimeNoRecall;
		int time = NightLengthTicks - ctx.NightTicksLeft;
		if (time > byTime) {
			if (!atWait)
				return Do($"too late tonight for {b.Label} (night time {time} > {byTime}); to the arena's end by the Old Man to wait for the next night", toWait);
			return Do($"too late tonight for {b.Label} (night time {time} > {byTime}{(byTime == SummonByTimeNoRecall ? ", no recall potion" : "")}); waiting for the next night",
				new WaitSkill("next night", c => c.IsNight && NightLengthTicks - c.NightTicksLeft <= byTime, NightWaitTicks + NightLengthTicks));
		}
		if (OldManInView(ctx) is not EntityInfo om) {
			if (home.ChebyshevTo(node) > OldManGoneTiles)
				return Do($"to the Old Man's home at {home} to summon {b.Label}", new WalkToSkill(new Core.Pathfinding.GoalNearTile(home, ArenaCentreTolerance, ctx.Physics)) { Quiet = true });
			return Do($"no Old Man by the Dungeon tonight (summoned already, or gone): he is back by day; waiting for the morning",
				new WaitSkill("day", c => !c.IsNight, NightLengthTicks + 600));
		}
		if (rest)
			return Do($"resting to {SummonLifeFraction:P0} life before summoning {b.Label} ({ctx.Combat.Life}/{ctx.Combat.LifeMax})", Rest());
		if (!ChatButtonSkill.InChatRange(ctx, om))
			return Do($"walking up to the Old Man at {om.FeetTile} to summon {b.Label}",
				new WalkToSkill(new Core.Pathfinding.GoalNearTile(om.FeetTile, ChatWalkTiles, ctx.Physics)) { Quiet = true });
		return Do($"summoning {b.Label} at the Old Man's chat button (night time {time}, by {byTime})", SkeletronFight(b, arena, ctx));
	}

	// ---------- the evil biome (Milestone 10: the Eater of Worlds by Worm Food, docs M10_SPEC.md) ----------

	/// <summary>Within this many tiles of the summon spot, the summon is made there (the arena's own tolerance).</summary>
	public const int SummonSpotTiles = ArenaCentreTolerance;

	/// <summary>Did the game's flag say the zone did not hold, at the latest look within <see cref="SummonSpotTiles"/> of the spot?</summary>
	private static bool ZoneOutNear(BotContext ctx, TilePos spot) => ctx.Zone.Near(spot, SummonSpotTiles) == false;

	/// <summary>
	/// Where on the worm's arena (columns <paramref name="x0"/>-<paramref name="x1"/>, feet row <paramref name="standY"/>) the
	/// Worm Food is used: the middle of the longest stretch of the floor where the zone holds (<see cref="BotContext.InEvilZone"/>:
	/// the game's flag where the bot stands, the latest look where it stood, the build having walked the whole floor, else the
	/// count of the seen tiles), the nearer the arena's middle on a tie; null if it holds nowhere on it. The fight kites from
	/// there and keeps within it (BossFightSkill). Seen in game (measured runs M10m and M10n): the arena at (1872,239), the
	/// fight begun at its middle (1920) and east of it at (1933,245), where the game's count once built was 346 and 300; the
	/// zone held west of x 1933 only, and kiting east lost the worm both times.
	/// </summary>
	public static TilePos? EvilSummonSpot(BotContext ctx, int x0, int x1, int standY)
	{
		int mid = (x0 + x1) / 2, bestFrom = 0, bestLen = 0, run = 0;
		for (int x = x0; x <= x1 + 1; x++) {
			if (x <= x1 && ctx.InEvilZone(new TilePos(x, standY))) {
				run++;
				continue;
			}
			int from = x - run;
			if (run > bestLen || run == bestLen && run > 0 && Math.Abs(from + (run - 1) / 2 - mid) < Math.Abs(bestFrom + (bestLen - 1) / 2 - mid)) {
				bestFrom = from;
				bestLen = run;
			}
			run = 0;
		}
		return bestLen > 0 ? new TilePos(bestFrom + (bestLen - 1) / 2, standY) : null;
	}

	/// <summary>
	/// The evil biome's boss, its summon item in hand: to where the Corruption zone holds (its arena, <see cref="EvilArenaBounds"/>,
	/// or the Eye's if that lies in the zone; else <see cref="EvilGround"/>), rest to full, summon and fight, at any time of day.
	/// Worm Food does nothing outside the zone (Player.cs:43462), and the worm dives away and despawns once no player is in
	/// it (NPC.cs:55436). The Eye's night rules are its own (<see cref="BossDefeated.NightOnly"/>).
	/// </summary>
	private ISkill ResolveEvilBoss(BossDefeated b, bool bossHere, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		ctx.EvilErrand = true;
		ctx.NoteZone();
		var arena = EvilArenaBounds(ctx)
			?? (ArenaBounds(ctx) is (int x0, int x1, int y) a && ctx.CorruptionAround(new TilePos((x0 + x1) / 2, y)) >= BotContext.CorruptionZoneTiles ? a : ((int, int, int)?)null);
		string offArena = "";
		if (!bossHere) {
			// On the arena: where the zone holds now (EvilSummonSpot), the build having changed the count. Nowhere on its floor,
			// by what the bot saw standing there: the open ground of the corruption, as with no arena.
			TilePos? summonAt = null;
			if (arena is (int fx0, int fx1, int fy)) {
				summonAt = EvilSummonSpot(ctx, fx0, fx1, fy);
				if (summonAt == null && Enumerable.Range(fx0, fx1 - fx0 + 1).Any(x => ctx.Zone.At(new TilePos(x, fy)) == false)) {
					offArena = $" (off the arena: the zone held nowhere on its floor x {fx0}-{fx1} where the bot stood)";
					arena = null;
				}
				else
					summonAt ??= new TilePos((fx0 + fx1) / 2, fy);
			}
			TilePos? stretch = summonAt ?? (ctx.InEvilBiome(ctx.Node) ? ctx.Node : EvilGround(ctx));
			if (stretch is TilePos st) {
				ISkill bridgeFirst = BridgeTheChasms(st, claims, depth, ctx);
				if (bridgeFirst != null || stuck != null)
					return bridgeFirst;
			}
			if (arena is (int ax0, int ax1, int ay) && summonAt is TilePos at) {
				if (!InArena(ctx.Node, at.X, at.X, ay))
					return Do($"walking to {at} on the arena in the {ctx.Ids.EvilName}, where the zone holds, to summon the {b.Label}", ArenaWalk(ctx, ax0, ax1, ay, at.X));
			}
			// On the surface: the worm is fought from open ground, kiting sideways as it arcs out (spec G9-G10), not where
			// the Worm Food happened to be made. Seen in game (measured run M10e, world_eow): crafted at an altar in a pocket
			// at row 409, some 130 rows under the corruption's surface, and summoned there at once: "no room to run from the
			// worm's head" 20 times, the bot held in place at (1890,409) and killed, the worm at 9637 of 10050 life.
			// In the zone by the game's flag where there is one (the Worm Food works only there), else by the count.
			else if (!ctx.InEvilZone(ctx.Node) || UnderTheSurface(ctx)) {
				if (EvilGround(ctx) is not TilePos spot)
					return ExploreForEvilBiome(ctx, $"to summon the {b.Label}");
				if (spot.ChebyshevTo(ctx.Node) > SummonSpotTiles)
					return Do($"walking to the {ctx.Ids.EvilName} at {spot} to summon the {b.Label} ({ctx.CorruptionAround(spot)} {ctx.Ids.EvilName} tiles round it){offArena}",
						new WalkToSkill(new Core.Pathfinding.GoalNearTile(spot, SummonSpotTiles, ctx.Physics)) { Quiet = true });
				// There, and still too little seen round it to be sure: the summon is tried anyway (the game counts what is there).
			}
			if (ctx.Combat.LifeFraction < SummonLifeFraction && ctx.Tick - lastRestEnd > MaxRestTicks)
				return Do($"resting to {SummonLifeFraction:P0} life before summoning the {b.Label} ({ctx.Combat.Life}/{ctx.Combat.LifeMax})",
					new WaitSkill("life", c => c.Combat.LifeFraction >= SummonLifeFraction, MaxRestTicks));
		}
		return Do($"fighting the {b.Label} in the {ctx.Ids.EvilName}{(arena != null ? ", in the arena" : "")} ({(ctx.IsNight ? "night" : "day")}){offArena}",
			new BossFightSkill(b.SummonItem, b.Npc, b.Minions) { Arena = arena });
	}

	/// <summary>Below the ground's surface here by more than <see cref="TravelFromSurfaceRows"/> (as for a trip's "up first").</summary>
	private static bool UnderTheSurface(BotContext ctx)
	{
		int surface = SurfaceRow(ctx);
		return surface > 0 && ctx.Node.Y > surface + TravelFromSurfaceRows;
	}

	/// <summary>
	/// An item had only in the evil biome (<see cref="ItemSource.EvilBiome"/>): an errand there (<see cref="BotContext.EvilErrand"/>
	/// opens its surface in full armour). Rotten chunks are hunted on <see cref="EvilGround"/>: eaters of souls spawn only
	/// while the player is in the Corruption zone (spec G4). Vile mushrooms are picked where the map shows them (only those
	/// of their frame, <see cref="ItemSource.FrameX"/>), and looked for along the corruption's surface from there (spec G5):
	/// not a trip to new ground elsewhere, where none grow: the plants on the corrupt grass the map shows are cut where they
	/// are so new ones grow, some of them mushrooms, and with none to cut the known stretch is walked to and fro
	/// (MineSkill.PatrolEvilSurface, CutPlantsSkill; measured run M10b: 4 of 6 in 5 game days).
	/// </summary>
	/// <summary>
	/// Wanders round the evil biome's ground a hunt for worm teeth makes before it gives up: a devourer is 1 in 65 of the Corruption's
	/// spawns (NPC.cs:86707, 89094), the rest eaters of souls, at a spawn every 100-300 ticks with few NPCs near (NPC.cs:86680-86705):
	/// one every 2-4 game hours. A wander is a walk of 30-60 tiles and a wait of <see cref="HuntSkill.WaitTicks"/>, about 450 ticks;
	/// <see cref="HuntSkill.MaxWanders"/> (12, an hour and a half) would give up between two devourers; 60 is about 7.5 game hours.
	/// </summary>
	public const int RareHuntWanders = 60;

	private ISkill ResolveEvilSource(int item, ItemSource src, int more, int target, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		ctx.EvilErrand = true;
		string name = ctx.Knowledge.ItemName(item);
		if (src.Kind == SourceKind.Hunt) {
			if (EvilGround(ctx) is not TilePos ground)
				return ExploreForEvilBiome(ctx, $"to hunt for {name}");
			ISkill bridgeFirst = BridgeTheChasms(ground, claims, depth, ctx);
			if (bridgeFirst != null || stuck != null)
				return bridgeFirst;
			return Do($"need {more} more {name} (hunting to {target} in the {ctx.Ids.EvilName} round {ground})",
				new HuntSkill(item, src.NpcAiStyle, target) { Ground = ground, WanderLimit = src.NpcAiStyle == WormPattern.WormAiStyle ? RareHuntWanders : HuntSkill.MaxWanders });
		}
		var mine = new MineSkill(item, src.TileType, target) { FrameX = src.FrameX, Bias = ExploreBias.Sideways, PatrolEvilSurface = true };
		bool known = ctx.World.FindTiles(src.TileType, ctx.Node, MineSkill.MapSearchRadius)
			.Any(c => src.FrameX < 0 || ctx.World.TileFrameX(c.X, c.Y) == src.FrameX);
		if ((ctx.InEvilBiome(ctx.Node) ? ctx.Node : EvilGround(ctx)) is TilePos stretch) {
			ISkill bridgeFirst = BridgeTheChasms(stretch, claims, depth, ctx);
			if (bridgeFirst != null || stuck != null)
				return bridgeFirst;
		}
		if (!known && !ctx.InEvilBiome(ctx.Node)) {
			if (EvilGround(ctx) is not TilePos ground)
				return ExploreForEvilBiome(ctx, $"to look for {name}");
			if (ground.ChebyshevTo(ctx.Node) > TravelArrivedTiles)
				return Do($"no {name} seen; walking to the {ctx.Ids.EvilName} at {ground} to look for it there",
					new WalkToSkill(new Core.Pathfinding.GoalNearTile(ground, TravelArrivedTiles, ctx.Physics)) { Quiet = true });
		}
		return Do($"need {more} more {name} (picking to {target} in the {ctx.Ids.EvilName})", mine);
	}

	/// <summary>The chasm openings bridged so far (kept across re-plans; their blocks are <see cref="BotContext.Protected"/>).</summary>
	public List<ChasmOpening> Bridges { get; } = new();
	/// <summary>Chasm openings given up on, by their west column: a bridge that failed <see cref="MaxBuildFailuresPerSite"/> times.</summary>
	private readonly HashSet<int> badBridges = new();
	private int bridgeFailures;
	private List<ChasmOpening> evilChasms;
	private (int, int) evilChasmsSpan;
	private long evilChasmsAt = long.MinValue / 2;

	/// <summary>
	/// Before the errands on the evil biome's surface (the hunt, the mushroom patrol, the summon), once: a bridge over each
	/// chasm opening (<see cref="Chasms.Openings"/>) along the stretch of it the map shows round <paramref name="around"/>
	/// (PatrolSkill.EvilSurfaceSpan, the patrol's own), the nearest first, with the blocks in the bag (dirt and stone:
	/// <see cref="GameIds.BuildingBlocks"/>). As players bridge the corruption's chasms: the walks, the patrol and the fights
	/// then stay on the surface, and what a hit throws off a lip lands on the bridge. Seen in game (measured run M10c,
	/// world_eow): three deaths by falls into its chasms, 665-765 damage at (1702,364), (1701,362) and (1778,396), on the
	/// patrol's stretch x 1699-1967. Null when all are bridged or given up, with building off, or no stretch is known. The
	/// openings are looked for again every <see cref="EvilGroundRecheckTicks"/> (the map fills in).
	/// </summary>
	private ISkill BridgeTheChasms(TilePos around, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (!ctx.AllowPlace || ctx.Blocks == null || PatrolSkill.EvilSurfaceSpan(ctx, around) is not (int x0, int x1))
			return null;
		if (evilChasms == null || evilChasmsSpan != (x0, x1) || ctx.Tick - evilChasmsAt >= EvilGroundRecheckTicks) {
			evilChasms = Chasms.Openings(ctx.Grid, x0, x1, ctx.Physics.SafeFallTiles).Where(o => ctx.SurfaceY <= 0 || o.Row <= ctx.SurfaceY).ToList();
			evilChasmsSpan = (x0, x1);
			evilChasmsAt = ctx.Tick;
			if (evilChasms.Count > 0)
				ctx.Log.Info($"Auto: {evilChasms.Count} chasm opening(s) on the evil surface x {x0}-{x1}: {string.Join(", ", evilChasms)}");
		}
		int Missing(ChasmOpening o) => Enumerable.Range(o.X0, o.Width).Count(x => !ctx.Grid.Get(x, o.Row).IsBlock());
		TilePos node = ctx.Node;
		ChasmOpening? next = evilChasms.Where(o => !badBridges.Contains(o.X0) && Missing(o) > 0)
			.OrderBy(o => Math.Abs((o.X0 + o.X1) / 2 - node.X)).Cast<ChasmOpening?>().FirstOrDefault();
		if (next is not ChasmOpening c)
			return null;
		int blocks = Missing(c);
		ISkill sub = Resolve(new HaveItem(ctx.Ids.BuildingBlocks, blocks + BlockMargin, "building blocks"), claims, depth + 1, ctx);
		if (sub != null || stuck != null)
			return sub;
		return Do($"bridging the chasm at x {c.X0}-{c.X1} ({blocks} blocks at row {c.Row})", new BuildSkill(Blueprint.Bridge(c.Width), c.Origin));
	}

	/// <summary>The heading the last exploration for the evil biome went (alternated: the biome may be on either side).</summary>
	private int evilHeading = 1;

	/// <summary>No evil biome known: explore along the surface, a new heading each time, until standing in it.</summary>
	private ISkill ExploreForEvilBiome(BotContext ctx, string why)
	{
		evilHeading = -evilHeading;
		return Do($"exploring for the {ctx.Ids.EvilName} (none seen yet) {why}",
			new ExploreSkill($"the {ctx.Ids.EvilName}", c => c.InEvilBiome(c.Node), ExploreBias.Sideways, evilHeading) { JobKey = "Explore evil biome" });
	}

	/// <summary>
	/// A spot is worth standing on for the evil biome's errands with this much of the Corruption round it: twice the zone's
	/// threshold, so the zone holds while the fight kites across the spot (BossFightSkill.HardHalfWidth, 40 tiles each way,
	/// moves the scene scan by a quarter of its 169 columns).
	/// </summary>
	public const int EvilGroundTiles = 2 * BotContext.CorruptionZoneTiles;
	/// <summary>How long a found spot (or none found) is kept before looking again: a game hour (the map fills in).</summary>
	public const int EvilGroundRecheckTicks = 3600;
	private TilePos? evilGround;
	private long evilGroundAt = long.MinValue / 2;

	/// <summary>
	/// Where on the surface the bot stands in the Corruption zone for its errands there (hunting, picking, the summon): the
	/// surface spot nearest the spawn, every <see cref="RouteSampleTiles"/> columns out to <see cref="MaxTravelTiles"/> each
	/// side, that is in the evil biome and has <see cref="EvilGroundTiles"/> of the Corruption round it as the game counts
	/// it (<see cref="BotContext.CorruptionAround"/>); with none that much, the one with the most, at least the zone's
	/// <see cref="BotContext.CorruptionZoneTiles"/>. In the M10 world the corruption is at x 1880-1960 on the surface, 120-200
	/// tiles west of the spawn (spec section 0).
	/// <para>
	/// The count is of revealed tiles only, and the map shows a biome's surface (lit by day), not the ground under it: so
	/// where none reaches the zone's count, a surface spot the biome is seen at (<see cref="BotContext.InEvilBiome"/>) is the
	/// place to go, the one with the most of the Corruption round it, and failing that the surface above the nearest evil
	/// ground remembered anywhere (<see cref="BotContext.KnownEvil"/>), as a player walks to the corruption the map shows.
	/// The game's zone counts every tile, seen or not, and once there the bot sees the rest. Null only if the map shows no
	/// evil ground at all (explore for it). Seen in game (measured run M10a): from the spawn, with the corruption on the
	/// checkpoint's map, "exploring for the corruption (none seen yet)" for 12 game hours, 16-leg explorations underground
	/// at rows 340-565, before the hunting ground (1900,263) was found.
	/// </para>
	/// </summary>
	private TilePos? mappedEvilNoted;

	public TilePos? EvilGround(BotContext ctx)
	{
		// Kept a while: a spot while the biome is still seen there, none until the bot stands in the biome (an exploration for
		// it ended there, or the map filled in on the way).
		// Not a spot where the game's flag said the zone did not hold when the bot stood by it (ZoneOutNear): the count of the
		// seen tiles there was wrong, and the Worm Food would do nothing there.
		if (ctx.Tick - evilGroundAt < EvilGroundRecheckTicks
			&& (evilGround is TilePos kept ? (ctx.InEvilBiome(kept) || ctx.CorruptionAround(kept) >= BotContext.CorruptionZoneTiles) && !ZoneOutNear(ctx, kept)
				: !ctx.InEvilBiome(ctx.Node)))
			return evilGround;
		evilGroundAt = ctx.Tick;
		int originX = ctx.SpawnTile?.X ?? ctx.Node.X;
		TilePos? best = null, seen = null;
		int bestCount = BotContext.CorruptionZoneTiles - 1, seenCount = -1;
		for (int d = 0; d <= MaxTravelTiles; d += RouteSampleTiles)
			foreach (int x in d == 0 ? new[] { originX } : new[] { originX - d, originX + d }) {
				if (SurfaceAt(ctx.Grid, x) is not TilePos c || !ctx.InEvilBiome(c) || ZoneOutNear(ctx, c))
					continue;
				int n = ctx.CorruptionAround(c);
				if (n >= EvilGroundTiles) {
					evilGround = c;
					return c;
				}
				if (n > bestCount) {
					bestCount = n;
					best = c;
				}
				if (n > seenCount) {
					seenCount = n;
					seen = c;
				}
			}
		// The zone's count undercounts (seen tiles only): the surface the biome is seen at, then evil ground seen anywhere.
		best ??= seen;
		if (best == null && ctx.KnownEvil.Count > 0) {
			TilePos from = ctx.SpawnTile ?? ctx.Node;
			foreach (TilePos e in ctx.KnownEvil.All.OrderBy(e => Math.Abs(e.X - from.X)))
				if (Math.Abs(e.X - originX) <= MaxTravelTiles && SurfaceAt(ctx.Grid, e.X) is TilePos above && !ZoneOutNear(ctx, above)) {
					best = above;
					break;
				}
		}
		// Evil ground the map has revealed anywhere, not only at the surface nor this session (a cave's crimstone seen on the way
		// to an altar): the surface over the nearest, as a player heads for the biome their map shows. Seen in game (world_boc B1,
		// The Aggressive Site from the J2 checkpoint): the map held the Crimson's stone round the altar at (2579,873) from J2, no
		// surface spot of it was revealed and the remembered ground starts empty on a resume: "exploring for the crimson (none seen
		// yet) to hunt for Vertebra", 16-leg explorations turning back at x 2838 and 2927, 18 game hours and more.
		if (best == null) {
			var around = new TilePos(originX, ctx.SpawnTile?.Y ?? ctx.Node.Y);
			TilePos? nearest = null;
			foreach (int t in ctx.Ids.EvilTiles)
				// Twice a trip's reach: world_boc B2, the Crimson's stone by the altar 616 tiles off, just past one.
				foreach (TilePos e in ctx.World.FindTiles(t, around, 2 * MaxTravelTiles)) {
					if (nearest is not TilePos n || Math.Abs(e.X - originX) < Math.Abs(n.X - originX))
						nearest = e;
					break; // the nearest of its kind
				}
			if (nearest is TilePos found && SurfaceAt(ctx.Grid, found.X) is TilePos over && !ZoneOutNear(ctx, over)) {
				best = over;
				if (mappedEvilNoted != found) {
					mappedEvilNoted = found;
					ctx.Log.Info($"Auto: evil ground on the map at {found} ({ctx.Knowledge?.TileName(ctx.World.TileTypeAt(found.X, found.Y))}); to the surface over it at {over}");
				}
			}
		}
		evilGround = best;
		return best;
	}

	/// <summary>
	/// <see cref="FromBag"/>: as many of the item (up to the count, in whole crafts) as the bag makes now, through its recipes;
	/// null when none more can be made from it, or the count is in. Claims nothing: what the bag holds is used as it is.
	/// </summary>
	private ISkill ResolveFromBag(HaveItem h, int depth, BotContext ctx)
	{
		int item = h.Accepts[0];
		int have = ctx.Inventory.CountAny(h.Accepts);
		if (item <= 0 || have >= h.Count)
			return null;
		// Journey: researched, it is in the duplication menu as surely as in the bag (healing potions, a recall potion...).
		foreach (int a in h.Accepts)
			if (ctx.Infinite(a)) {
				int to = ctx.Inventory.Count(a) + h.Count - have;
				return Do($"need {h.Count - have} more {ctx.Knowledge.ItemName(a)} (researched: duplicating to {to})", new DuplicateSkill(a, to));
			}
		int step = ctx.Knowledge.RecipesFor(item).Select(r => Math.Max(1, r.ResultStack)).DefaultIfEmpty(1).Min();
		int carried = ctx.Inventory.Count(item);
		for (int want = h.Count - have; want > 0; want = want > step ? want - step : 0)
			if (MakeableFromBag(item, carried + want, ctx, new Dictionary<int, int>(), 0))
				return Resolve(new HaveItem(item, carried + want, h.Label), new Dictionary<int, int>(), depth + 1, ctx);
		return null;
	}

	/// <summary>
	/// Can <paramref name="n"/> of the item be had from what the bag holds (less what <paramref name="used"/> has taken for
	/// this check), crafting down its recipes (<see cref="MaxShortfallDepth"/> deep), with nothing gathered? Stations are
	/// left to the craft (walked to, as for any).
	/// </summary>
	private bool MakeableFromBag(int item, int n, BotContext ctx, Dictionary<int, int> used, int depth)
	{
		int have = Math.Max(0, ctx.Inventory.Count(item) - used.GetValueOrDefault(item));
		int take = Math.Min(have, n);
		used[item] = used.GetValueOrDefault(item) + take;
		int missing = n - take;
		if (missing <= 0)
			return true;
		if (depth >= MaxShortfallDepth)
			return false;
		foreach (RecipeInfo r in ctx.Knowledge.RecipesFor(item)) {
			var trial = new Dictionary<int, int>(used);
			int crafts = (missing + Math.Max(1, r.ResultStack) - 1) / Math.Max(1, r.ResultStack);
			bool ok = true;
			foreach (Ingredient ing in r.Ingredients) {
				int need = ing.Stack * crafts;
				foreach (int a in ing.Accepts.Skip(1)) { // a recipe group's other items, carried, before the listed one is made
					int got = Math.Min(need, Math.Max(0, ctx.Inventory.Count(a) - trial.GetValueOrDefault(a)));
					trial[a] = trial.GetValueOrDefault(a) + got;
					need -= got;
				}
				if (need > 0 && !MakeableFromBag(ing.Item, need, ctx, trial, depth + 1)) {
					ok = false;
					break;
				}
			}
			if (ok) {
				foreach (var kv in trial)
					used[kv.Key] = kv.Value;
				return true;
			}
		}
		return false;
	}

	// ---------- cost estimate (rough ticks; infinity = cannot be had) ----------

	private const float TreeCost = 30f, MineCost = 60f, HuntCost = 300f;
	private const float CraftStepCost = 5f;
	/// <summary>
	/// Journey: a researched item, a click in the duplication menu (DuplicateSkill, CraftSkill.ClickInterval ticks): cheaper than
	/// any gathering (<see cref="TreeCost"/>) and than a craft step, so a recipe of researched ingredients beats one to gather for.
	/// </summary>
	private const float DuplicateCost = 1f;

	/// <summary>The other tiers of the tiered need being resolved (set in ResolveItem), for <see cref="AltOres"/>.</summary>
	private int[] tierAlternatives = Array.Empty<int>();

	/// <summary>
	/// Ores of the other tiers of the need being resolved (silver-tier gear while mining for iron-tier, and back): what each
	/// is made of, followed down its recipes to something mined. A player takes whatever ore turns up. Seen in game
	/// (measured runs T0a/T0b): every mining job looked for one ore only, 164-199 "Mine" heartbeats a run, ore found in
	/// 13 of 42 and 16 of 60 explorations, while silver-tier ore was seen and left.
	/// </summary>
	private IReadOnlyList<(int Item, int Tile)> AltOres(int ore, BotContext ctx)
	{
		if (ctx.Ids.BuildingBlocks.Contains(ore) || tierAlternatives.Length == 0)
			return Array.Empty<(int, int)>();
		var list = new List<(int, int)>();
		foreach (int alt in tierAlternatives)
			if (RawOre(alt, ctx, 0) is (int item, int tile) && item != ore && !list.Any(l => l.Item1 == item))
				list.Add((item, tile));
		return list;
	}

	/// <summary>Recipe steps followed down from an item to the ore it is made of (gear, bar, ore).</summary>
	public const int RawOreDepth = 3;

	/// <summary>How many of <paramref name="ore"/> one <paramref name="item"/> takes, down its cheapest recipes (0 if none).</summary>
	private int OreIn(int item, int ore, BotContext ctx, int depth)
	{
		if (item == ore)
			return 1;
		if (depth >= RawOreDepth || BestRecipe(item, ctx) is not RecipeInfo r)
			return 0;
		int n = 0;
		foreach (Ingredient ing in r.Ingredients)
			n += ing.Stack * OreIn(ing.Item, ore, ctx, depth + 1);
		int stack = Math.Max(1, r.ResultStack);
		return (n + stack - 1) / stack;
	}

	private (int Item, int Tile)? RawOre(int item, BotContext ctx, int depth)
	{
		ItemSource src = ctx.Knowledge.SourceOf(item);
		if (src.Kind == SourceKind.Mine)
			return ctx.Ids.BuildingBlocks.Contains(item) ? null : (item, src.TileType);
		if (depth >= RawOreDepth || BestRecipe(item, ctx) is not RecipeInfo r)
			return null;
		foreach (Ingredient ing in r.Ingredients)
			if (RawOre(ing.Item, ctx, depth + 1) is (int, int) found)
				return found;
		return null;
	}

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
		if (ctx.Infinite(item))
			return DuplicateCost; // Journey: one click for the lot
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
			// Journey: a researched item costs a click, whatever made it; its recipes and sources are not looked into.
			if (ctx.Infinite(i)) {
				current[i] = DuplicateCost;
				continue;
			}
			ItemSource src = k.SourceOf(i);
			current[i] = src.Kind switch {
				// An ore that mining keeps coming back without costs more each time, so an item with another tier (armour)
				// switches to an ore that is still there.
				SourceKind.Tree => TreeCost, SourceKind.Mine => MineCost * (1 + fruitlessMines.GetValueOrDefault(i)), SourceKind.Hunt => HuntCost, _ => float.PositiveInfinity,
			};
			if (src.Kind != SourceKind.None)
				continue;
			// What nothing gathers nor makes (a boss's drop: shadow scales, tissue samples) is had as far as the bag holds it: free,
			// not out of reach. Else a craft from the bag (FromBag: the evil pickaxe's 6 scales carried) is "no way to get" though
			// MakeableFromBag found it makeable; a count the bag falls short of is said so where the ingredient is resolved.
			if (ctx.Inventory.Count(i) > 0 && k.RecipesFor(i).Count == 0) {
				current[i] = 0f;
				continue;
			}
			// A boss's drop the bag lacks: the boss fought again (BossDropOf), a fight's worth of ticks (a measured estimate: the Eater
			// of Worlds in M10s took 2800 ticks, plus the walk to the corruption).
			if (BossDropOf(i, ctx.Ids) != null) {
				current[i] = BossDropCost;
				continue;
			}
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
	/// <summary>The heading explorations for a station go in, chosen once (see ResolveStation).</summary>
	private int? stationHeading;
	/// <summary>Station trips forced out of the columns generation puts none in, since the bot was last in columns it does.</summary>
	private readonly HashSet<int> areaTripTried = new();
	public const int MaxStationExplores = 10;
	/// <summary>Trips to new ground for a station nothing can place (a demon altar), before the plan is stuck.</summary>
	public const int MaxStationTrips = 6;
	private readonly HashSet<int> searchedFor = new();

	/// <summary>
	/// Stations nothing places (a demon altar) that no walk got to, and when: not chosen again for <see cref="FailedTripTicks"/>
	/// (a fact about the place, like <see cref="failedTrips"/>); the next nearest is, or an exploration for one. Seen in game
	/// (measured run M10d, world_eow): with the Worm Food's all else in hand at d5 +19h, the altar at (1732,358) lay behind the
	/// ebonstone lining of a chasm (pick power 40 breaks none of it); "no way back to the DemonAltar (2 walks failed); making
	/// a new one here" removed it, and the search found the same altar again, 5 times until the run's 5 days were up.
	/// </summary>
	private readonly Dictionary<TilePos, long> failedStationSites = new();

	/// <summary>A cell of a station no walk got to lately (<see cref="failedStationSites"/>, within <see cref="FailedTripTicks"/>).</summary>
	private bool GivenUpSite(TilePos t, BotContext ctx) =>
		failedStationSites.Any(f => ctx.Tick - f.Value < FailedTripTicks && f.Key.ChebyshevTo(t) <= StationSiteTiles);

	/// <summary>
	/// A walk to a station nothing places (a demon altar) that ended "no path" where it got to, with explosives that break tiles in
	/// the bag: a blast towards the station from there first (BlastOutSkill), not a failed walk, once per spot and station for
	/// <see cref="FailedTripTicks"/>; the walk after it goes on through what the blast opened, or fails and counts. As a player
	/// with bombs blasts through the ebonstone they cannot mine, rather than leave the altar they have seen for one farther off.
	/// Seen in game (measured run U30): from the corruption chasm's foot at (1762,420) seven bombs, each "then the walk is
	/// Partial", took the bot west through the ebonstone to the DemonAltar at (1748,412) (t702000-t721800); before them, from
	/// t673200, the 2-walk count gave that altar up 42 times in 13 game hours ("not that one again for a day"), the sight of
	/// it bringing it back each time; the brain's own rule (3 failures in a row) started 19 blasts towards it, 7 of them threw.
	/// </summary>
	private bool BlastFirst(WalkToSkill walk, BotContext ctx)
	{
		if (ctx.Knowledge.ItemsPlacing(stationWalkType).Count > 0 || walk.LastPlan?.Status != Core.Pathfinding.PathStatus.NoPath || walk.Goal == null
			|| !ctx.AllowBreak || BlastOutSkill.Carried(ctx).Count == 0)
			return false;
		var key = (stationWalkType, ctx.Node);
		if (blastsTried.TryGetValue(key, out long at) && ctx.Tick - at < FailedTripTicks && ctx.Tick >= at)
			return false;
		blastsTried[key] = ctx.Tick;
		blastFor = (stationWalkType, ctx.Node, walk.Goal);
		ctx.Log.Info($"Auto: no path to the {ctx.Knowledge.TileName(stationWalkType)} at {KnownStations.GetValueOrDefault(stationWalkType)} from {ctx.Node}; "
			+ $"a blast on towards it from here before that counts as a failed walk ({string.Join(", ", BlastOutSkill.Carried(ctx).Select(e => $"{e.Name} x{ctx.Inventory.Count(e.ItemType)}"))})");
		return true;
	}

	private (int Type, TilePos From, Core.Pathfinding.IGoal Goal)? blastFor;
	private readonly Dictionary<(int Type, TilePos From), long> blastsTried = new();
	/// <summary>Cells of one placed station (an altar is 3×2): found again as another of its cells.</summary>
	public const int StationSiteTiles = 3;

	/// <summary>
	/// The nearest seen station of this type that no walk lately failed to reach: round the bot, or failing that anywhere on
	/// the map (what the player has seen, at any time). Seen in game (measured run M10e, world_eow): with the vile powder made
	/// at d3 +11h at home (2079,282), "exploring for a DemonAltar (none seen yet)", the altars at (1732,358) and (1439,360),
	/// on the map from runs before, being 350 and 640 tiles off, beyond <see cref="StationSearchRadius"/>.
	/// </summary>
	private TilePos? FindReachableUnplaceable(int tile, BotContext ctx)
	{
		bool Usable(TilePos t) => !GivenUpSite(t, ctx);
		TilePos? near = ctx.World.FindTiles(tile, ctx.Node, StationSearchRadius).Where(Usable).Cast<TilePos?>().FirstOrDefault();
		if (near != null)
			return near;
		// The whole map is a big scan (millions of cells): once a game hour, the answer kept till then.
		if (!wideStationSearch.TryGetValue(tile, out var w) || ctx.Tick - w.Tick >= WideStationSearchTicks || ctx.Tick < w.Tick) {
			w = (ctx.Tick, ctx.World.FindTiles(tile, ctx.Node, WideStationSearchRadius));
			wideStationSearch[tile] = w;
			if (w.Found.Count > 0)
				ctx.Log.Info($"Auto: none within {StationSearchRadius} tiles; {w.Found.Count} cell(s) of {ctx.Knowledge.TileName(tile)} on the map, the nearest at {w.Found[0]} ({w.Found[0].ChebyshevTo(ctx.Node)} tiles away)");
		}
		return w.Found.Where(Usable).OrderBy(t => t.ChebyshevTo(ctx.Node)).Cast<TilePos?>().FirstOrDefault();
	}

	private readonly Dictionary<int, (long Tick, IReadOnlyList<TilePos> Found)> wideStationSearch = new();
	/// <summary>The map-wide look for a station nothing places: the width of a large world either way.</summary>
	public const int WideStationSearchRadius = 8400;
	public const int WideStationSearchTicks = 3600;

	/// <summary>Look for a station nothing can place (once per planner reset); remember it when found.</summary>
	private bool FindUnplaceableStation(int tile, BotContext ctx)
	{
		if (KnownStations.ContainsKey(tile))
			return true;
		if (!searchedFor.Add(tile))
			return false;
		TilePos? found = FindReachableUnplaceable(tile, ctx);
		if (found == null)
			return false;
		KnownStations[tile] = found.Value;
		notedAt[tile] = ctx.Tick + 60 * 60 * 60; // far away: do not forget it before going there
		ctx.Log.Info($"Auto: found a {ctx.Knowledge.TileName(tile)} at {found.Value}");
		return true;
	}

	/// <summary>Ticks a boss's drop costs to get: the boss fought again (a measured estimate, see SolveCosts).</summary>
	public const float BossDropCost = 6000f;

	/// <summary>
	/// The boss fought again for an item only it drops (<see cref="BossDefeated.Again"/>): the Eater of Worlds for Shadow Scales, the
	/// Brain of Cthulhu for Tissue Samples; null for anything else.
	/// </summary>
	public static BossDefeated BossDropOf(int item, GameIds ids)
	{
		if (item <= 0)
			return null;
		if (item == ids.ShadowScale && ids.EvilBoss > 0 && !ids.Crimson)
			return new BossDefeated(ids.EvilBoss, ids.WormFood, new[] { ids.EvilBossBody, ids.EvilBossTail }, "Eater of Worlds") { NightOnly = false, InEvilBiome = true, Again = true };
		if (item == ids.TissueSample && ids.BrainOfCthulhu > 0 && ids.Crimson)
			return new BossDefeated(ids.BrainOfCthulhu, ids.BloodySpine, new[] { ids.Creeper }, "Brain of Cthulhu") { NightOnly = false, InEvilBiome = true, Again = true };
		return null;
	}

	private ISkill Do(string why, ISkill skill)
	{
		Status = why;
		Complete = false;
		return skill;
	}

	private void RememberStations(BotContext ctx)
	{
		// Not a station given up on (FindReachableUnplaceable's rule): seen in game (measured run U30), the DemonAltar at
		// (1748,411) was given up "not that one again for a day, another one" 42 times in 13 game hours from t673200, and each
		// time the next pass took it back from sight, as (1750,411), (1749,412) or (1748,412), 17 tiles off, over the farther
		// altar the search had just chosen.
		foreach (var kv in ctx.Seen.Stations)
			if (!GivenUpSite(kv.Value, ctx))
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
	/// <summary>
	/// The station in range wherever the bot is, by research (Upgraded Research's "Craft from Researched stations"): no station to
	/// place or walk to. Not after a craft the game refused for want of a station (the mod adds it only where some tile is in
	/// range, RecipeGlobalTile.AdjTiles): the station is then placed and walked to as without the mod.
	/// </summary>
	private bool StationByResearch(BotContext ctx, int tileType) => !stationMissed && ctx.Journey != null && ctx.Journey.Enabled && ctx.Journey.StationAtHand(tileType);

	private bool NearStation(BotContext ctx, int tileType)
	{
		if (StationByResearch(ctx, tileType))
			return true;
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
	/// <summary>
	/// Trips that found no way there, and when: not chosen again for <see cref="FailedTripTicks"/>, wherever the bot is (a
	/// fact about the place, kept through rests). Seen in game (measured run U8): from a pocket in a corruption chasm the
	/// trip to (1495,241) failed "no path" and was chosen again after every fruitless job, 13 heartbeats and more.
	/// </summary>
	private readonly Dictionary<TilePos, long> failedTrips = new();

	/// <summary>
	/// Where to walk to, out of off-limits ground a mining job cannot explore from (towards the spawn), until the ground
	/// under the bot is not off limits. Day or night: by night trips wait for the morning, and this did too, so every
	/// mining job failed at once where the bot stood. Seen in game (measured run U8): "need 4 more Iron Ore", the job
	/// failing "the explorations from here would be off limits" at (2172,457) for 6 game hours, 83 times over the run.
	/// </summary>
	private TilePos? leaveTarget;
	private WalkToSkill leaveWalk;

	private ISkill LeaveOffLimits(BotContext ctx, string name)
	{
		if (leaveTarget is not TilePos to)
			return null;
		if (!ctx.OffLimits(ctx.Node) || to.ChebyshevTo(ctx.Node) <= TravelArrivedTiles || leaveWalk != null && leaveWalk.FailureReason != "") {
			leaveTarget = null;
			leaveWalk = null;
			return null;
		}
		if (leaveWalk == null || leaveWalk.FailureReason != "")
			leaveWalk = UpFirst(ctx, to) ?? new WalkToSkill(new Core.Pathfinding.GoalNearTile(to, 4, ctx.Physics)) { Quiet = true };
		return Do($"no {name} to be found from off-limits ground; walking out of it towards the spawn at {to}", leaveWalk);
	}
	/// <summary>A game day, as for ground looked at before (see <see cref="ChooseTravelDest"/>: it is new to look at again).</summary>
	public const int FailedTripTicks = 86400;
	/// <summary>The way out to the surface (or the spawn) a trip set off on, held until reached.</summary>
	private TilePos? exitTarget;
	private bool exitIsRow;
	private string exitWhy = "";

	/// <summary>
	/// The walk out: up to the exit's row (anywhere not off limits), or to the exit tile; from deep down, to a tile it is
	/// up first (a walk to one far tile wandered the caves, segment by segment, and came to nothing: measured run R1).
	/// </summary>
	private WalkToSkill ExitWalk(BotContext ctx, TilePos exit)
	{
		if (exitIsRow)
			return new WalkToSkill(new Core.Pathfinding.GoalRow(exit.Y, exit.X, ctx.Physics) { Accept = t => !ctx.OffLimits(t) }) { Quiet = true, ToSurface = true };
		return UpFirst(ctx, exit) ?? new WalkToSkill(new Core.Pathfinding.GoalNearTile(exit, 4, ctx.Physics)) { Quiet = true, ToSurface = true };
	}

	/// <summary>Deep below a far target: a walk up to near the surface's height first (null if not needed).</summary>
	private static WalkToSkill UpFirst(BotContext ctx, TilePos target)
	{
		int surface = SurfaceRow(ctx);
		if (surface <= 0 || ctx.Node.Y <= surface + TravelFromSurfaceRows || target.Y > surface + TravelFromSurfaceRows
			|| target.ChebyshevTo(ctx.Node) < 40)
			return null;
		return new WalkToSkill(new Core.Pathfinding.GoalRow(surface + TravelFromSurfaceRows - 3, ctx.Node.X, ctx.Physics) { Accept = t => !ctx.OffLimits(t) }) { Quiet = true, ToSurface = true };
	}

	/// <summary>Columns either side (tiles, every <see cref="SurfaceRowStep"/>) looked at for the ground's height here.</summary>
	public const int SurfaceRowSpan = 8, SurfaceRowStep = 4;

	/// <summary>
	/// The surface's height here: the higher of the world's surface row and the ground seen from the sky in the columns
	/// round the bot (0 if neither is known). Seen in game (measured run U8): in a corruption chasm at row 342, below ground
	/// at about row 285 but above the world's surface row, "up first" never came; the trip west planned from the chasm's
	/// floor, where the only way out goes east and up (away from the goal), and found no path, 18 game hours.
	/// </summary>
	internal static int SurfaceRow(BotContext ctx)
	{
		int row = ctx.SurfaceY > 0 ? ctx.SurfaceY : int.MaxValue;
		for (int dx = -SurfaceRowSpan; dx <= SurfaceRowSpan; dx += SurfaceRowStep)
			if (SurfaceAt(ctx.Grid, ctx.Node.X + dx) is TilePos s && s.Y < row)
				row = s.Y;
		return row == int.MaxValue ? 0 : row;
	}
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
		// By night, in weak gear with a house: no trip or way out along the surface. The job stays a mining job, which the
		// night rule (AtNight) takes down below the spawn; the trip waits for the morning (travelDest and exitTarget are
		// kept). Seen in game (measured run U4): "no more Silver Ore found around here; nowhere to travel; back to the
		// spawn" at nightfall became a night in the house, 46 StayHome heartbeats between the chainmail and the greaves.
		if (ctx.IsNight && ctx.Combat.Defense < NightOutDefense && HomeInside(ctx) != null)
			return null;
		// The way out chosen before (back to the spawn, or up to the surface at a column) holds until the bot is there: worked
		// out again from wherever a fight left the bot, it came out different, and each time a new 3-second plan began
		// (user report, 2026-10-14: "changes its mind and jumps out of the hole to do something else"). Before anything else:
		// seen in game (measured run S7), the walk back to the spawn, broken off by a fight, became a mining job again that
		// failed at once in the off-limits spot, 40 times over.
		if (exitTarget is TilePos exit) {
			if (exitIsRow ? ctx.Node.Y <= exit.Y : exit.ChebyshevTo(ctx.Node) <= TravelArrivedTiles)
				exitTarget = null;
			else
				return Do($"no more {name} found around here; {exitWhy} at {(exitIsRow ? $"row {exit.Y}" : exit.ToString())}", ExitWalk(ctx, exit));
		}
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
			// A trip for one ore of a tier group is one for the others (the job looked for them all: MineSkill.TierGroup).
			foreach (int g in tierGroups.GetValueOrDefault(item) ?? new[] { item })
				travelledFor[g] = Math.Max(travelledFor.GetValueOrDefault(g), g == item ? fails : fruitlessMines.GetValueOrDefault(g));
			// The explorations ran at the depth floor (MineSkill.ExploredToFloor): the ground within it is looked through, and
			// the next step down is right below, so that before a trip. Seen in game (measured run U22): with the floor at row
			// 436 the whole armour phase (explorations at rows 419-436), the trip west and back took 10.2 game hours (d3 +9.5h to
			// +19.7h, as far as off-limits ground at (1858,295)) and found no ore, and the step came only with "nowhere to
			// travel" at d3 +23.4h; at +25 the bot went back for iron seen at rows 441-443 and had 33 in about 2.5 hours, at
			// +100 about 70 ore in 5 hours.
			if (fruitlessAtFloor.Contains(item)) {
				int floor = ctx.DeepestRow;
				if (StepDeeper(ctx)) {
					foreach (int g in tierGroups.GetValueOrDefault(item) ?? new[] { item })
						fruitlessAtFloor.Remove(g);
					ctx.Log.Info($"Auto: no more {name} found down to the depth floor (row {floor}); going {ctx.DepthBonus} rows deeper than the gear alone allows (down to row {ctx.DeepestRow}) before travelling to new ground");
					return null;
				}
			}
			// Nowhere to go (seen in game, world_eye run 68: between a corruption and a desert, every way crosses one):
			// no trip, and no recall potion drunk for one either (five were, in a minute, each followed by mining from the spawn).
			// Instead: back to the spawn to look from there, and from there deeper (run 70: auto mode stopped, every job failing).
			if (ChooseTravelDest(ctx, fails, failedTrips, TripColumns(ctx, item)) is not TilePos chosen)
				return NoTripPossible(ctx, item, name);
			travelDest = chosen;
			fresh = true;
			// New ground for crystals: the search there starts afresh, not walked back from to where it was (MineSkill.SearchToResume).
			if (item == ctx.Ids.LifeCrystal)
				ctx.CrystalSearch.Clear();
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
		int surfaceRow = SurfaceRow(ctx);
		if (surfaceRow > 0 && ctx.Node.Y > surfaceRow + TravelFromSurfaceRows && SurfaceAt(ctx.Grid, ctx.Node.X) is TilePos up && up.Y < ctx.Node.Y) {
			// Out of somewhere off limits, or under a desert's or evil biome's ground: back to the spawn instead. Seen in game
			// (world_eye run 61): "up to the surface" from the underground desert's edge, four times, each interrupted by a
			// fight and started again from wherever the bot was, drifted 100 tiles into the desert, where a charger killed it.
			if ((ctx.OffLimits(ctx.Node) || ctx.BiomeGround(up) || ctx.OffLimitsBelow(up)) && ctx.SpawnTile is TilePos spawn && spawn.ChebyshevTo(ctx.Node) > 8) {
				exitTarget = spawn;
				exitIsRow = false;
				exitWhy = "out of here and back to the spawn before travelling";
			}
			else {
				// Up, by the cheapest way, to near the surface's height (GoalRow), not to one tile on it.
				exitTarget = new TilePos(ctx.Node.X, surfaceRow + TravelFromSurfaceRows - 3);
				exitIsRow = true;
				exitWhy = "up to the surface before travelling";
			}
			return Do($"no more {name} found around here; {exitWhy} at {(exitIsRow ? $"row {exitTarget.Value.Y}" : exitTarget.ToString())}", ExitWalk(ctx, exitTarget.Value));
		}
		return Do($"no more {name} found around here; {(fresh ? "travelling" : "on")} to new ground at {dest}",
			new WalkToSkill(new Core.Pathfinding.GoalNearTile(dest, 4, ctx.Physics)) { Quiet = true, Travel = true });
	}

	/// <summary>Least time (2 game hours) between steps deeper (<see cref="BotContext.DepthBonus"/>).</summary>
	public const int DeeperEveryTicks = 2 * 3600;
	private long lastDeeperTick = long.MinValue / 2;

	/// <summary>Farther than this (tiles) from the spawn with nowhere to travel: walk back to it first.</summary>
	public const int NoTripBackTiles = 30;

	/// <summary>Where the last "nowhere to travel" for crystals looked on from the crystal band instead of the spawn.</summary>
	private TilePos? crystalNoTripAt;

	/// <summary>
	/// One step deeper than the gear alone allows (<see cref="BotContext.DepthBonus"/>), if the bonus has a step to go and the
	/// last step was <see cref="DeeperEveryTicks"/> ago: iron and silver each ran out at once and the bonus went from 25 to
	/// 50 rows within seconds (world_eye run 72). The fruitless explorations are forgotten: the ground below is new.
	/// </summary>
	private bool StepDeeper(BotContext ctx)
	{
		if (ctx.DepthBonus >= BotContext.MaxDepthBonus || ctx.Tick - lastDeeperTick < DeeperEveryTicks)
			return false;
		lastDeeperTick = ctx.Tick;
		ctx.DepthBonus += BotContext.DepthBonusStep;
		ctx.ExploresWithoutOre.Clear();
		ctx.ExploreLegsCut.Clear();
		return true;
	}

	private ISkill NoTripPossible(BotContext ctx, int item, string name)
	{
		// Deeper each time (wherever the bot is: mining from the spawn wanders far before it fails), but a step at a time
		// (StepDeeper).
		if (StepDeeper(ctx))
			ctx.Log.Info($"Auto: no more {name} within reach and nowhere to travel; going {ctx.DepthBonus} rows deeper than the gear alone allows (down to row {ctx.DeepestRow})");
		// Crystals, in the crystal band or with a search there to go back to (MineSkill.SearchToResume): on from there, not back
		// to the spawn and down through the rows above the band again. Unless the last time was here too (the search here got
		// nowhere since): then the spawn, as before. Seen in game (measured run U9): "back to the spawn to look from there" 11
		// times; 14 of 28 crystal explorations began within 5 tiles of the spawn, 25% of the 288 crystal legs above row 375.
		if (item == ctx.Ids.LifeCrystal && (ctx.InCrystalBand(ctx.Node.Y) || MineSkill.SearchToResume(ctx) != null)) {
			bool again = crystalNoTripAt is TilePos was && was.ChebyshevTo(ctx.Node) <= ExploreSkill.MaxLegTiles;
			crystalNoTripAt = ctx.Node;
			if (!again) {
				ctx.Log.Info($"Auto: no more {name} found around here and nowhere to travel; looking on in the crystal band (from {MineSkill.SearchToResume(ctx)?.ToString() ?? $"here, row {ctx.Node.Y}"}), not from the spawn");
				return null;
			}
		}
		if (ctx.SpawnTile is TilePos spawn && spawn.ChebyshevTo(ctx.Node) > NoTripBackTiles) {
			if (ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0 && spawn.ChebyshevTo(ctx.Node) > RecallDistance)
				return Do($"no more {name} found around here and nowhere to travel; drinking a recall potion to look again from the spawn",
					new UseItemSkill(ctx.Ids.RecallPotion));
			// Held like any way out (see TravelForOre).
			exitTarget = spawn;
			exitIsRow = false;
			exitWhy = "nowhere to travel; back to the spawn to look from there";
			return Do($"no more {name} found around here and nowhere to travel; back to the spawn at {spawn} to look from there", ExitWalk(ctx, spawn));
		}
		return null;
	}

	/// <summary>
	/// Where to go for new ore after <paramref name="fails"/> fruitless mining jobs: out from the spawn, farther each time
	/// (east 120, west 120, east 240...), not from wherever the bot is (from the bot, east then west went straight back to the
	/// ground just searched). Not onto somewhere off limits, nor onto or across a desert's or evil biome's ground (run 56: the
	/// spawn between a corruption and a desert, and every trip landed in one of them), nor above known off-limits ground
	/// (run 60: the surface by the spawn, over the underground desert's edge). Null if nowhere.
	/// <para>Nor within a leg's reach of where the bot stands (ExploreSkill.MaxLegTiles): that is the ground the fruitless job
	/// just looked through. Seen in game (measured run U29, d8 +8.5h to d10 +23h): standing at (2331,193), the new ground
	/// was (2335,193), 4 tiles off; the walk there ended at once, the explorations from there ended at once over ground
	/// explored already, and the same trip came again: 63,674 explorations in the run (24,517 from (2331,193)), runs of 757
	/// to 6,030 "travelling to new ground at (2335,193)" in a row, then the same at (2215,196).</para>
	/// </summary>
	private static TilePos? ChooseTravelDest(BotContext ctx, int fails, Dictionary<TilePos, long> failed, Func<int, bool> columns = null)
	{
		bool FailedLately(TilePos c) => failed.Any(f => ctx.Tick - f.Value < FailedTripTicks && f.Key.ChebyshevTo(c) <= TravelArrivedTiles);
		int dir = fails % 2 == 1 ? 1 : -1;
		int originX = ctx.SpawnTile?.X ?? ctx.Node.X;
		int reach = Math.Min(TravelTiles * ((fails + 1) / 2), MaxTravelTiles); // not across the world (1,900 tiles in run 47)
		// Farther each time, then round again from the nearest: seen in game (measured run U2), after ten fruitless crystal
		// jobs only the trips 600 tiles out were tried, both off limits, and "nowhere to travel" came 12 times though the
		// ground 240 and 480 tiles west had served before (it has been a day: it is new to look at again).
		for (int i = 0; i < MaxTravelTiles / TravelTiles; i++) {
			int r = (reach - TravelTiles + i * TravelTiles) % MaxTravelTiles + TravelTiles;
			foreach (int d in new[] { dir, -dir })
				if ((columns == null || columns(originX + d * r)) && SurfaceAt(ctx.Grid, originX + d * r) is TilePos c && c.ChebyshevTo(ctx.Node) > ExploreSkill.MaxLegTiles && !FailedLately(c) && !ctx.OffLimits(c) && !ctx.BiomeGround(c) && !ctx.OffLimitsBelow(c) && !RouteCrossesBiome(ctx, originX, c.X))
					return c;
		}
		return null;
	}

	/// <summary>
	/// The columns a trip may end in: for a station looked for in the world (its trip key, the tile negated), those
	/// generation puts it in (<see cref="IGameKnowledge.GenerationArea"/>); any column otherwise.
	/// </summary>
	private static Func<int, bool> TripColumns(BotContext ctx, int item) =>
		item < 0 && ctx.Knowledge?.GenerationArea(-item) is GenArea a ? a.HoldsColumn : null;

	/// <summary>
	/// The way to explore for a station looked for in the world: the safer side, unless the ground it is generated in runs out
	/// that way within a few legs (the world's edge, or the middle tenth it is never put in) and there is more the other way.
	/// </summary>
	private static int StationHeading(BotContext ctx, GenArea? area)
	{
		int safer = ctx.SaferSide(ctx.Node);
		if (area is not GenArea a || !a.HoldsColumn(ctx.Node.X))
			return safer;
		int room = a.RoomFrom(ctx.Node.X, safer), other = a.RoomFrom(ctx.Node.X, -safer);
		return room < StationRoomTiles && other > room ? -safer : safer;
	}

	/// <summary>Ground ahead (tiles) an exploration for a station wants on its side: a few legs (ExploreSkill.MaxLegTiles each).</summary>
	public const int StationRoomTiles = 4 * ExploreSkill.MaxLegTiles;

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
	public static TilePos? SurfaceAt(ITileGrid g, int x)
	{
		TilePos? floating = null;
		for (int y = 40; y < 900; y++) {
			if (!g.Get(x, y + 1).CanStandOn() || !Core.Pathfinding.Body.IsStandable(g, new TilePos(x, y)))
				continue;
			// Over open air deeper than FloatingGapRows under its support: a sky island, a cloud, a leaf canopy or a bridge of
			// placed blocks, not the ground; the ground is further down.
			int under = y + 2;
			while (under < 900 && g.Get(x, under).IsBlock())
				under++;
			int air = 0;
			while (under + air < 900 && air < FloatingGapRows && !g.Get(x, under + air).IsBlock())
				air++;
			if (air < FloatingGapRows)
				return new TilePos(x, y);
			floating ??= new TilePos(x, y);
			y = under + air - 1;
		}
		return floating; // nothing under it: what there is
	}

	/// <summary>
	/// Open rows under a standing spot's support that make it floating rather than the ground (<see cref="SurfaceAt"/>): twice
	/// the safe fall (PhysicsParams, 25 rows), as nothing a player walks the surface on hangs over that. Seen in game (measured
	/// run M11a, world_skeletron): "along the surface west to (1213,61)", (1139,84), (1085,65) - rain clouds at rows 62-70 and a
	/// sky island at rows 85-97 over ground at rows 184-218 (from the save); the bot climbed up, then bridged west at row ~100
	/// on its own blocks (each next "surface") 600 tiles past the Dungeon at x 871, row 201, never in sight of it.
	/// </summary>
	public const int FloatingGapRows = 50;

	private bool stationMissed;
	private bool walkedUpToStation;
	private int stationMissesInARow;
	public const int MaxStationMisses = 2;
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
	/// <summary>The stations in sight taken into what the planner knows, as each pass begins (tests).</summary>
	public void SeeStationsForTest(BotContext ctx) => RememberStations(ctx);

	public ISkill ResolveForTest(Need need, BotContext ctx)
	{
		ctx.EvilErrand = false;
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
		return (ctx.Combat.HasWeapon || ctx.Combat.HasRanged) && ctx.Hostiles.Any(h => ctx.Fightable(h.Id, h.DistanceTo(cx, cy), FightSkill.IgnoredDangerWithin) && !ctx.Spared(h) && h.DistanceTo(cx, cy) <= DangerRadiusTiles * 16f
			&& (!toFight || FightSkill.NotTooDeep(ctx, h, h.DistanceTo(cx, cy)))
			&& (!FightSkill.InWater(ctx, h) || h.Id == ctx.RecentAttacker)
			&& FightSkill.ClearShot(ctx, cx, cy, h.CenterX, h.CenterY));
	}
}
