using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Bot.Brain;

/// <summary>The Wall of Flesh's plan (Milestone 12, docs M12_SPEC.md section 9), its stages S1-S8.</summary>
public sealed partial class ProgressionPlanner
{
	/// <summary>Arrows into the Wall of Flesh fight, wooden and unholy together (spec S1: 1000, refilled below 800).</summary>
	public const int WofArrows = 1400;
	/// <summary>Healing potions into the fight (spec S1: 10, from the bag).</summary>
	public const int WofPotions = 10;
	/// <summary>
	/// Arrows enough for the fight, the descent's remaining demand (<see cref="DescentDemand"/>): the two Classic wins of fight_wof_150
	/// shot 496 and 512 (2148 carried, 1652 and 1636 left), the Unholy arrows among them.
	/// </summary>
	public const int WofFightArrows = 1200;
	/// <summary>
	/// Arrows a bot already down there goes on to the fight with, short of <see cref="WofFightArrows"/>: the packing list's count is
	/// for the surface, where wood is; from the bridge, the resupply would climb 700 rows for it (E13, 2026-10-17). Seen in game
	/// (E14, 2026-10-17): 667 arrows took the Wall to 1,451 of 8,000 and ran out ("Boss fight failed: out of arrows"); the Hungry
	/// and the Leeches take their share, so the fight wants twice what the damage sum says.
	/// </summary>
	public const int WofFightArrowsMin = 600;
	/// <summary>
	/// The fight's arrows once <see cref="WofFightArrowsMin"/> was reached: topped up again only under this. Seen in game (world_wof E25,
	/// 2026-10-17): 600 made at the bench, some 200 shot at fliers on the walk to the summon column, and back for more under 600,
	/// nine times in three game days with the doll in hand.
	/// </summary>
	public const int WofFightArrowsRefill = WofFightArrowsMin * 2 / 3;

	/// <summary>The fight phase's arrows: <see cref="WofFightArrowsMin"/>, refilled under <see cref="WofFightArrowsRefill"/>.</summary>
	public static HaveItem WofFightArrowsNeed(GameIds ids) =>
		new(Tiers(ids.WoodenArrow, ids.UnholyArrow), WofFightArrowsMin, "arrows for the fight") { RefillBelow = WofFightArrowsRefill };
	/// <summary>Longest the Wall's plan works on max life past 200 (BestEffort): a game day.</summary>
	public const int WofLifeTicks = 24 * 3600;
	/// <summary>
	/// Unholy arrows got before the descent, hunted for if the bag's worm teeth (or vertebrae, in a Crimson world) do not make them: 5
	/// from 5 wooden arrows and one (Recipe.cs:2276-2290). Measured in game (Classic, the Demon Bow, m12_synthetic_classic):
	/// <list type="bullet">
	/// <item>fight_wof_unholy: the 59 arrows that took the Wall from 8000 to 5545, the 75 unholy ones shot first (AmmoOrder), did 41.6 of
	/// its life each, at 2.06 arrows a second (86 a second while they lasted). An unholy arrow pierces 5 (Projectile.cs:954) and goes
	/// on through the eyes and the mouth, which share one life (NPC.cs:92324-92331).</item>
	/// <item>fight_wof: a wooden arrow (pierce 1) did 10.1 (5954 life for 591 arrows), 21 a second; the fight lost when the bridge ran
	/// out. fight_wof_unholy, 32.5 a second over 161 s with its 75, died to the lasers with 2775 of 8000 left.</item>
	/// </list>
	/// With A unholy and the rest wooden the Wall's 8000 take A/2.06 + (8000 - 41.6 A)/(10.1 x 2.06) seconds: 32.5 a second from A = 92;
	/// 150 make it about 160 s at 50 a second, the whole fight over in the time fight_wof_unholy lived. The cheapest of what the bot can
	/// have by then: Hellfire arrows and Molten Fury need hellstone bars (3 hellstone, pick 65, and 1 obsidian at a Hellforge,
	/// Recipe.cs:10811-10817, and obsidian is nearly absent from the worlds), the Archery Potion herbs and bottled water the planner has
	/// no source for, Jester's Arrows fallen stars and fly about 54 tiles (projectile 5: timeLeft 120 at 2 updates a tick,
	/// Projectile.cs:966-978), short of the fight's 62-77 below half life.
	/// </summary>
	public const int WofUnholyArrows = 150;
	/// <summary>
	/// The hellbridge's length (tiles), the spec's rule (section 5a, S0): L = 1.25 x 62,175 / DPS, the Wall travelling 62,175 / DPS
	/// tiles while its 8000 life (Classic, NPC.cs:5483-5501) goes at DPS a second. Measured in game (fight_wof, m12_synthetic_3,
	/// 2026-10-17; the Demon Bow and wooden arrows, the band kiting): 20.3 a second, and the Wall went 1486 tiles for its 4000 life
	/// there (a Journey world's life multiplier, NPC.ScaleStats_ApplyGameMode), the formula's 1531 for half the life: so 3063 tiles
	/// for the Classic Wall, and L = 3829. Longer than one side of a small world (4200 wide): the bridge goes both ways from the
	/// shaft, its summon end toward the nearer edge (<see cref="BridgeEdgeRoom"/> from it) and the rest back past the shaft and the
	/// world's middle (the Wall never turns, NPC.cs:26384-26410). The spec's 1000-1750 assumed 37-50 a second.
	/// </summary>
	public const int DefaultBridgeLength = 3829;
	/// <summary>The hellbridge's length for this plan (<see cref="DefaultBridgeLength"/> unless a scenario sets it).</summary>
	public int BridgeLength { get; set; } = DefaultBridgeLength;
	/// <summary>Blocks carried over what is left of the bridge (spec S1: L + 100): ledges, pillars, a wall against a lava fall.</summary>
	public const int BridgeBlockMargin = 100;
	/// <summary>
	/// Blocks carried down the hellevator (<see cref="ResolveBridgeBlocks"/> before the bridge is known): its ledges (one every
	/// HellevatorSkill.LegRows of some 760 rows, 38), steps and walls on the way, and the bridge's first stretch; the shaft's own cells
	/// and the Underworld's ash give the rest.
	/// </summary>
	public const int BridgeDepartureBlocks = 300;
	/// <summary>
	/// The hellbridge's row below the Underworld's first row (Main.UnderworldLayer): 42, the spec's row 1042 in the M12 world
	/// (section 5b, measured on its save: the fewest cells to mine and no lava in the body's rows), 2 rows under the lowest the ash
	/// ceiling's generated lower edge reaches (maxTilesY - 160, WorldGen.cs:10125-10136) and 13 over the rows the Underworld pass
	/// floods (maxTilesY - 145, WorldGen.cs:10239-10248); 39 below row 1003, the highest whose body is clear of The Tongue.
	/// </summary>
	public const int BridgeRowsUnderTop = 42;
	/// <summary>
	/// The hellevator's column: this many tiles from the spawn the way the bridge goes (spec 1b: x 2155-2156, 60 tiles east of the
	/// house at 2095, the column pair whose shaft comes down through the ash ceiling onto the ruined tower's roof at row 1042,
	/// read from the M12 world's save). A shaft that finds no ground at the row moves on by <see cref="ShaftShiftTiles"/>.
	/// </summary>
	public const int ShaftOffsetTiles = 60;
	/// <summary>A shaft with no ground under it moves this far (the hellevator's last leg looked 40 either side of it).</summary>
	public const int ShaftShiftTiles = 2 * HellevatorSkill.SeekTiles + 1;
	/// <summary>Spec S5: Voodoo Demons are shot only from the bridge's last 100 tiles.</summary>
	public const int HuntTiles = 100;
	/// <summary>The hunt for a Voodoo Demon waits this far back from the landing columns opened at the bridge's end.</summary>
	public const int HuntBackTiles = 10;
	/// <summary>
	/// Never summon within this many columns of the world's middle (spec 3a, risk 9): the Wall comes from the doll's half of the
	/// world (NPC.SpawnWOF), and a doll near the middle could land on either side.
	/// </summary>
	public const int SummonCentreGap = 150;
	/// <summary>
	/// Room kept between the bridge's far end and the world's edge (tiles): the Wall spawns 75 tiles beyond the player (1200 px,
	/// NPC.cs:90944-90962) and The Tongue kills once it is within 38 tiles of an edge (Player.cs:20652-20656).
	/// </summary>
	public const int BridgeEdgeRoom = 75 + 38 + 7;
	/// <summary>
	/// The doll lands 3-6 columns beyond the bridge's end: thrown at 4 px/tick from the body's centre a tile short of it, slowed 5 %
	/// a tick in the air, it goes 80 px across in the ~100 ticks of its fall of 25 rows (Player.cs:6005-6006, Item.cs:50626-50632).
	/// Those columns' first cell under the bridge's row must be lava.
	/// </summary>
	public const int LandingFrom = 3, LandingTo = 6;
	/// <summary>Rows under the bridge looked down for the lava the doll falls into (the band's lava is 13-36 rows under row 1042).</summary>
	public const int LavaScanRows = 60;
	/// <summary>A bridge standing where a run resumes counts as the bridge (<see cref="KnowBridge"/>) when at least this share of <see cref="BridgeLength"/> stands.</summary>
	public const int ResumedBridgeShare = 2;
	/// <summary>The Wall's loot is looked for this far round where it died (its 9 x 9 brick box, NPC.cs:85023-85057).</summary>
	public const int WofLootRadiusTiles = 20;

	private int? shaftX;
	private int bridgeDir;
	/// <summary>The descent to the Underworld as an operation (<see cref="Schedulers.Operation"/>), null when none is under way.</summary>
	private Operation descent;
	/// <summary>Hellevator failures in a row (not deaths) after which the descent is blocked and handed back to the plan.</summary>
	public const int DescentMaxFailures = 3;

	/// <summary>
	/// The descent's remaining demand: what the unfinished work and the fight after it take, resupplied only below it (the
	/// departure supplies are the stages' full counts, met before the shaft was begun). The fight's arrows
	/// (<see cref="WofFightArrows"/>) and the bridge's blocks (<see cref="BridgeBlocks"/>, its own count of what is left).
	/// </summary>
	private IEnumerable<Need> DescentDemand(GameIds ids)
	{
		yield return WofFightArrowsNeed(ids);
		yield return new BridgeBlocks("bridge blocks");
	}

	/// <summary>The descent owns the bot now: begun (the hellevator given) under the operation scheduler, no death since, not over.</summary>
	public bool DescentOwns(BotContext ctx)
	{
		if (descent == null)
			return false;
		if (ctx.Deaths != descent.StartedDeaths) {
			EndDescent(ctx, "interrupted by a death; the plan's stages from the top again (the surface's work comes first at home)");
			return false;
		}
		// Down: on the bridge's row the first time (the bridge known from there), or back in the Underworld on a later trip.
		if (bridgeKnown && ctx.UnderworldRow > 0 && ctx.Node.Y >= ctx.UnderworldRow) {
			EndDescent(ctx, "done: down in the Underworld, the hellbridge's row " + bridgeRow);
			return false;
		}
		return true;
	}

	private void EndDescent(BotContext ctx, string why)
	{
		ctx.Log.Info($"Operation {descent.Name} ({descent.Identity}) ended after {(ctx.Tick - descent.StartedTick) / 3600.0:0.0} game hours: {why}");
		descent = null;
	}

	/// <summary>
	/// The plan's pass while the descent owns the bot: its remaining demand first (a resupply only when the rest of the work is
	/// not feasible with what is carried), then the stages from the Underworld's on; the stages before it (the departure
	/// supplies, the surface's work: max life, stocks, NPC rooms) are not looked at again until the operation ends.
	/// </summary>
	private ISkill NextInDescent(IReadOnlyList<Need> stages, BotContext ctx)
	{
		var claims = new Dictionary<int, int>();
		foreach (Need demand in DescentDemand(ctx.Ids)) {
			ISkill s = Resolve(demand, claims, 0, ctx);
			if (stuck != null)
				return null;
			if (s != null) {
				ctx.Log.Info($"Operation {descent.Name}: resupply, {demand.Label} short of the remaining demand ({Status})");
				return s;
			}
		}
		int from = -1;
		for (int i = 0; i < stages.Count && from < 0; i++)
			if (stages[i] is PrepFor { Inner: UnderworldReached })
				from = i;
		for (int i = Math.Max(0, from); i < stages.Count; i++) {
			ISkill s = Resolve(stages[i], claims, 0, ctx);
			if (stuck != null)
				return null;
			if (s != null)
				return s;
		}
		return null;
	}
	/// <summary>
	/// The hellbridge: its row, the column it is begun from (where the bot came down), its summon end (<see cref="bridgeTo"/>, the
	/// far end the doll is thrown off, the Wall's side) and its flight end (<see cref="bridgeBack"/>, the far side of the shaft).
	/// </summary>
	private int bridgeRow, bridgeFrom, bridgeTo, bridgeBack;
	/// <summary>A hellbridge step that failed (not for want of blocks): where the bridge ended then, and how many times in a row.</summary>
	private int bridgeFailEnd = int.MinValue, bridgeFails;
	/// <summary>A failed hellbridge step at the same end this many times: the plan is stuck there (the brain rests, then stops).</summary>
	public const int MaxBridgeFails = 2;
	private bool bridgeKnown, hellbridgeDone, thrownByPlan, unplannedLogged, lootDone, huntOn;
	private (float X, float Y)? wofDiedAt;

	/// <summary>The Wall of Flesh's preparation (spec S1) was met: the plan got to the Guide, the shaft and the bridge.</summary>
	public bool WofPrepMet { get; private set; }
	/// <summary>The bot stood on the hellbridge's row in the Underworld (spec S3: "underworld reached").</summary>
	public bool UnderworldReachedOnce => bridgeKnown;
	public bool HellbridgeDone => hellbridgeDone;
	/// <summary>The Wall of Flesh's loot picked up where it died (spec S8).</summary>
	public bool WofLootCollected => lootDone;
	/// <summary>The hellbridge as planned: row, flight end and summon end (null before the bot stood on its row).</summary>
	public (int Row, int From, int To)? Hellbridge => bridgeKnown ? (bridgeRow, bridgeBack, bridgeTo) : null;
	/// <summary>The hellevator's column (null before one was chosen).</summary>
	public int? ShaftX => shaftX;
	/// <summary>The shaft's route as dug (HellevatorSkill.Route), kept across errands at the same shaft.</summary>
	private readonly List<(int Row, int Col)> shaftRoute = new();

	/// <summary>
	/// The shaft's column and route as a string for the player's save ("x;row:col;row:col"), and back: an errand saved without them
	/// (a new one, or one ended when auto mode stopped) still finds the dug way. Seen in game (world_wof E33, 2026-10-18): the route
	/// recorded on the trip down was not in the save the run ended with.
	/// </summary>
	public string ShaftMemory => shaftX is int x ? x + ";" + string.Join(";", shaftRoute.Select(r => $"{r.Row}:{r.Col}")) : "";

	/// <summary>The shaft memory from the save, put back after each reset (auto mode on resets the plan).</summary>
	private string savedShaft;

	public void RestoreShaftMemory(string memory)
	{
		savedShaft = memory;
		ApplyShaftMemory(memory);
	}

	private void ApplyShaftMemory(string memory)
	{
		string[] parts = (memory ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0 || !int.TryParse(parts[0], out int x))
			return;
		shaftX = x;
		shaftRoute.Clear();
		foreach (string p in parts.Skip(1)) {
			string[] rc = p.Split(':');
			if (rc.Length == 2 && int.TryParse(rc[0], out int r) && int.TryParse(rc[1], out int c))
				shaftRoute.Add((r, c));
		}
	}

	/// <summary>How far the hellbridge is built (0-1), as the tiles show it now.</summary>
	public double HellbridgeProgress(BotContext ctx)
	{
		int total = Math.Abs(bridgeTo - bridgeFrom) + Math.Abs(bridgeBack - bridgeFrom);
		if (!bridgeKnown || total == 0)
			return 0;
		var (toEnd, backEnd) = BridgeEnds(ctx);
		return Math.Clamp((Math.Abs(toEnd - bridgeFrom) + Math.Abs(backEnd - bridgeFrom)) / (double)total, 0.0, 1.0);
	}

	/// <summary>Where the two halves of the bridge end now (toward the summon end, toward the flight end).</summary>
	/// <summary>
	/// Where the two halves end, once a tick and from where they ended last (the scan goes on from there while that column still
	/// stands, as the bridge skill's does): a scan of the whole span every tick cost 5 ms a tick with 3,000 columns built and the game
	/// ran at 4 frames a second (world_wof E20, 2026-10-17).
	/// </summary>
	private (int To, int Back) BridgeEnds(BotContext ctx)
	{
		if (bridgeEndsTick == ctx.Tick)
			return bridgeEndsCache;
		bridgeEndsTick = ctx.Tick;
		int to = ScanEnd(ctx, bridgeTo, ref toEndKnown);
		int back = bridgeBack == bridgeFrom ? bridgeFrom : ScanEnd(ctx, bridgeBack, ref backEndKnown);
		return bridgeEndsCache = (to, back);
	}

	private long bridgeEndsTick = -1;
	private (int To, int Back) bridgeEndsCache;
	private int toEndKnown = int.MinValue, backEndKnown = int.MinValue;

	private int ScanEnd(BotContext ctx, int toward, ref int known)
	{
		int from = bridgeFrom;
		if (known != int.MinValue && (known - bridgeFrom) * Math.Sign(toward - bridgeFrom) > 0
			&& (HellbridgeSkill.Built(ctx.Grid, bridgeRow, known) || HellbridgeSkill.Passable(ctx, ctx.Grid, bridgeRow, known)))
			from = known;
		known = HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, from, toward, ctx);
		return known;
	}

	private string HellbridgeText => bridgeKnown ? $"row {bridgeRow}, x {Math.Min(bridgeBack, bridgeTo)}-{Math.Max(bridgeBack, bridgeTo)}" : "none";

	private void ResetWallOfFlesh()
	{
		WofPrepMet = false;
		shaftX = null;
		shaftRoute.Clear();
		if (savedShaft != null)
			ApplyShaftMemory(savedShaft);
		descent = null;
		bridgeDir = 0;
		toEndKnown = backEndKnown = int.MinValue;
		bridgeEndsTick = -1;
		bridgeKnown = hellbridgeDone = thrownByPlan = unplannedLogged = lootDone = huntOn = false;
		bridgeFailEnd = int.MinValue;
		bridgeFails = 0;
		wofDiedAt = null;
	}

	/// <summary>
	/// After the Eye: the Wall of Flesh (docs M12_SPEC.md section 9), in the spec's order.
	/// <list type="number">
	/// <item>S1: a bow, the evil bow and the evil pickaxe from the bag, unholy arrows from the bag and then hunted for to
	/// <see cref="WofUnholyArrows"/>, 1000 arrows in all (refilled below 800), max life 200, 10 healing potions from the bag, and
	/// blocks for the bridge (<see cref="BridgeBlocks"/>); what is made on the surface only before the descent
	/// (<see cref="BeforeTheUnderworld"/>);</item>
	/// <item>S2: the Guide alive (<see cref="TownNpcAlive"/>);</item>
	/// <item>S3: a hellevator down to the bridge's row (<see cref="UnderworldReached"/>);</item>
	/// <item>S4: the hellbridge (<see cref="HellbridgeBuilt"/>);</item>
	/// <item>S5-S7: a Guide Voodoo Doll from a Voodoo Demon at the bridge's far end, thrown into the lava there, and the fight
	/// (<see cref="BossDefeated.ByVoodooDoll"/>);</item>
	/// <item>S8: its loot (<see cref="BossLoot"/>).</item>
	/// </list>
	/// The Voodoo Demon is spared until the hunt (<see cref="BotContext.SparedTypes"/>, G7), and the depth limit is lifted on the
	/// errand (<see cref="BotContext.DepthErrand"/>, G3).
	/// </summary>
	private static IEnumerable<Need> WallOfFleshStages(GameIds ids)
	{
		int boss = ids.WallOfFlesh;
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.IronTierBow, ids.EvilBow), 1, "bow"));
		// The evil bow from the Eye's ore (the Demon Bow 14 a shot at useTime 25, the Tendon Bow 19 at 30, against the Iron Bow's 8
		// at 28; Item.cs:4133-4150, 13511-13527, 4801-4810), as the evil boss's plan has it: the Wall's plan needs neither that boss
		// nor Skeletron, and the bow stage above takes the Iron Bow.
		yield return new PrepFor(boss, new BeforeTheUnderworld(new FromBag(new HaveItem(ids.EvilBow, 1, "evil bow"))));
		// The evil pickaxe (pick 65 or 70) from the evil boss's drops when they are carried (KeepRoom keeps the scales and samples):
		// past what the Silver Pickaxe cannot mine in the bridge's way. Hellstone (58) itself is generated only from 140 rows over
		// the world's bottom (WorldGen.cs:10251-10255, runners of strength 2-6), 18 under the bridge's row; on the saves of both test
		// worlds the highest is 13 and 15 rows under it, and the hot tiles in the bridge's rows are the ruined houses' hellstone
		// bricks (76), which any pickaxe mines (HellbridgeSkill puts a block in their place). What 45 cannot mine there is a
		// Hellforge standing in the bridge's cell (77, pick 65 below the Underworld's first row; no bomb breaks it there before
		// Hardmode, nor hellstone: Projectile.CanExplodeTile, Projectile.cs:92125-92136) or obsidian where lava met water (56, pick
		// 55; Player.cs:55024-55045).
		yield return new PrepFor(boss, new BeforeTheUnderworld(new FromBag(new HaveItem(ids.EvilPickaxe, 1, "evil pickaxe"))));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.UnholyArrow, WofArrows, "unholy arrows")));
		yield return new PrepFor(boss, new BeforeTheUnderworld(new HaveItem(ids.UnholyArrow, WofUnholyArrows, "unholy arrows for the wall")));
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), WofArrows, "arrows") { RefillBelow = WofArrows * 4 / 5 });
		// The most life crystals give (400), not the earlier bosses' 200: self-test fight_wof_150 on Classic (Peppe, the Wall at 8000),
		// at 200 life the bot died to an eye laser with the Wall at 1961, its damage 40.1 a second (enough), 19 hits for 336.
		// Surface prep (BeforeTheUnderworld): a run resumed down there, or a bridge built, is not sent back up for crystals; a death
		// there respawns it at the spawn, and the crystals come then. The earlier bosses' 200 is a must; the rest up to 400 for a
		// game day at most (BestEffort): fight_wof_150 on Classic won 2 of 3 at 400, 0 of 1 at 200 and at 300 (the Wall at 331 and
		// 332 when the bot died), but world_wof M12c found no crystal near home in three game days and the Wall was never reached.
		yield return new PrepFor(boss, new BeforeTheUnderworld(new LifeAtLeast(BossLife, "max life")));
		yield return new PrepFor(boss, new BeforeTheUnderworld(new BestEffort(new LifeAtLeast(MaxCrystalLife, "max life for the wall"), WofLifeTicks)));
		yield return new PrepFor(boss, new FromBag(new HaveItem(Tiers(ids.HealingPotion, ids.LesserHealingPotion), WofPotions, "healing potions")));
		yield return new PrepFor(boss, new BridgeBlocks("bridge blocks"));
		yield return new PrepFor(boss, new TownNpcAlive(ids.Guide, "the Guide"));
		yield return new PrepFor(boss, new UnderworldReached("the Underworld"));
		yield return new PrepFor(boss, new HellbridgeBuilt("hellbridge"));
		yield return new BossDefeated(boss, ids.GuideVoodooDoll, ids.WallOfFleshMinions, "Wall of Flesh") { NightOnly = false, ByVoodooDoll = true };
		yield return new BossLoot(boss, "the Wall of Flesh's loot");
	}

	/// <summary>Is the Wall of Flesh in this plan (the "wof" plan, or the bridge alone)?</summary>
	private bool WallOfFleshPlan(GameIds ids) => ids.WallOfFlesh > 0 && (Plan == Plans.WallOfFlesh || Plan == Plans.Hellbridge);

	/// <summary>
	/// The plan's rules for this pass: the Voodoo Demon spared (not fought, not shot, not counted as danger) while the Guide lives,
	/// the Wall is not beaten and the hunt at the bridge's far end is not on (G7: one killed over lava drops the doll into it, and the
	/// Wall spawns wherever the bot is, spec 2d); the items the stages want kept from the trash (G10).
	/// </summary>
	private void WallOfFleshRules(BotContext ctx)
	{
		GameIds ids = ctx.Ids;
		ctx.NeededItems.Clear();
		foreach (Need n in StagesFor(Plan, ids).Concat(NextMilestonePrep(Plan, ids)))
			NoteNeeded(n, ctx.NeededItems);
		if (ids.GuideVoodooDoll > 0)
			ctx.NeededItems.Add(ids.GuideVoodooDoll);
		SpareRules(ctx);
	}

	/// <summary>
	/// The spared NPCs (G7), every tick in auto mode (BotBrain), before its danger check: a fight started on the tick before the
	/// first planning pass, or between passes as the bot walks out of the hunt's stretch, would shoot the Voodoo Demon.
	/// </summary>
	public void SpareRules(BotContext ctx)
	{
		GameIds ids = ctx.Ids;
		ctx.SparedTypes.Clear();
		if (!WallOfFleshPlan(ids) || ctx.World == null || ids.VoodooDemon <= 0 || ctx.World.BossDefeated(ids.WallOfFlesh))
			return;
		if (ctx.World.TownNpcAlive(ids.Guide) && !VoodooHuntOn(ctx))
			ctx.SparedTypes.Add(ids.VoodooDemon);
	}

	private static void NoteNeeded(Need n, HashSet<int> into)
	{
		switch (n) {
			case HaveItem h:
				foreach (int a in h.Accepts)
					if (a > 0)
						into.Add(a);
				break;
			case PrepFor pf:
				NoteNeeded(pf.Inner, into);
				break;
			case FromBag fb:
				NoteNeeded(fb.Inner, into);
				break;
			case StepToward st:
				NoteNeeded(st.Inner, into);
				break;
			case BeforeTheUnderworld bu:
				NoteNeeded(bu.Inner, into);
				break;
			case BestEffort be:
				NoteNeeded(be.Inner, into);
				break;
			case BossDefeated b when b.SummonItem > 0:
				into.Add(b.SummonItem);
				break;
		}
	}

	/// <summary>The hunt for a Voodoo Demon is on: the bridge built, no doll carried, the bot on its last <see cref="HuntTiles"/>.</summary>
	private bool VoodooHuntOn(BotContext ctx) =>
		Plan == Plans.WallOfFlesh && hellbridgeDone && ctx.Inventory.Count(ctx.Ids.GuideVoodooDoll) <= 0 && !ctx.Infinite(ctx.Ids.GuideVoodooDoll) && NearBridgeEnd(ctx, HuntTiles);

	/// <summary>On the bridge's row, within <paramref name="tiles"/> of its far end.</summary>
	private bool NearBridgeEnd(BotContext ctx, int tiles)
	{
		if (!bridgeKnown)
			return false;
		TilePos n = ctx.Node;
		int front = bridgeDir > 0 ? n.X + 1 : n.X;
		return n.Y == bridgeRow - 1 && Math.Abs(bridgeTo - front) <= tiles && (bridgeTo - front) * bridgeDir >= 0;
	}

	/// <summary>
	/// The bot has been down (stood on the hellbridge's row) or is in the Underworld now: the surface's prep (<see cref="BeforeTheUnderworld"/>)
	/// is moot. A run resumed from the "underworld reached" checkpoint starts down there with the bridge not yet known.
	/// </summary>
	private bool DownThereOnce(BotContext ctx) => bridgeKnown || ctx.UnderworldRow > 0 && ctx.Node.Y >= ctx.UnderworldRow;

	/// <summary>The hellbridge's row in this world (<see cref="BridgeRowsUnderTop"/> under the Underworld's first row).</summary>
	private static int BridgeRowFor(BotContext ctx) => ctx.UnderworldRow + BridgeRowsUnderTop;

	/// <summary>
	/// The summon end's side: east (the spec's, section 5b: it starts at the shaft), unless the world has no room there for the end
	/// to lie <see cref="SummonCentreGap"/> past its middle, <see cref="BridgeEdgeRoom"/> short of its edge; then west.
	/// </summary>
	private int BridgeDirection(BotContext ctx, int fromX)
	{
		if (bridgeDir != 0)
			return bridgeDir;
		bridgeDir = ctx.WorldWidth <= 0 || ctx.WorldWidth - BridgeEdgeRoom > Math.Max(fromX, ctx.WorldWidth / 2 + SummonCentreGap) ? 1 : -1;
		return bridgeDir;
	}

	/// <summary>S2: a town NPC alive (the Guide): else wait for him; he moves back in by day (Main.cs:82805-82818, 83255-83260).</summary>
	/// <summary>Farther than this from the spawn (tiles), a room for a dead town NPC is gone home for first.</summary>
	public const int HomeForRoomTiles = 80;

	private ISkill ResolveTownNpc(TownNpcAlive need, BotContext ctx)
	{
		if (need.Npc <= 0 || ctx.World == null || ctx.World.TownNpcAlive(need.Npc))
			return null;
		// The game moves a town NPC in only where a valid room is free (WorldGen.SpawnTownNPC). With none (a homeless NPC took the dead
		// one's room) the wait never ends: a room first. Seen in game (world_wof E32, 2026-10-18): "waiting for the Guide" for over two
		// game days, the Nurse and the Zoologist homeless before the summon took him.
		// A room wanted and the bot far from home (the Underworld): home first, the room is built there (its wood, its stations).
		// Seen in game (world_wof E39, 2026-10-18): asked from the bridge's east end with no recall potion, the room's wood was
		// gathered from there, a climb to row 372 at x 4111 and "no way up to the surface"; the housing stage gave up for a day.
		bool roomWanted = HouseSite == null || Survey(ctx).Empty == 0;
		if (roomWanted && ctx.SpawnTile is TilePos spawn && ctx.Node.ChebyshevTo(spawn) > HomeForRoomTiles) {
			if (ctx.Ids.RecallPotion > 0 && ctx.Inventory.Count(ctx.Ids.RecallPotion) > 0)
				return Do($"{need.Label} not alive and no free room: a recall potion home to build one", new UseItemSkill(ctx.Ids.RecallPotion));
			return Do($"{need.Label} not alive and no free room: home to build one ({ctx.Node.ChebyshevTo(spawn)} tiles away)",
				new WalkToSkill(new GoalNearTile(spawn, HomeArrivedTiles, ctx.Physics)) { ToSurface = true });
		}
		// The house first if the planner does not know it (a run resumed down there: the house stage never ran): found as it stands.
		if (HouseSite == null && Resolve(new HouseBuilt("house"), new Dictionary<int, int>(), 1, ctx) is ISkill house) {
			Status = $"{need.Label} not alive: the house first, for a room he can move into; {Status}";
			return house;
		}
		// (Not only onto a house the bot built: one found standing has its rooms too, ResolveNpcHousing. E44: with the house found,
		// not built, this was skipped, and the bot waited 7835 times.)
		if (Survey(ctx).Empty == 0 && ResolveNpcHousing(new Dictionary<int, int>(), 0, ctx) is ISkill room) {
			Status = $"{need.Label} cannot move back in: no free room; {Status}";
			return room;
		}
		// The wait ends too once no room is free (a homeless NPC took it): the next room then. Seen in game (world_wof E43,
		// 2026-10-18): each room built stood free a moment, a day's wait for the Guide began, a homeless NPC moved in, and the bot
		// waited out the day with no room for him; three rooms in six game days.
		// With a room free, the wait ends too once it is taken (the next room then); with none free and none to build, the day's wait.
		bool roomFree = Survey(ctx).Empty > 0;
		return Do($"waiting for {need.Label} (none alive: the doll would be wasted; he moves back in by day)",
			new WaitSkill(need.Label, c => c.World.TownNpcAlive(need.Npc) || roomFree && c.Tick % 60 == 0 && Survey(c).Empty == 0,
				NightWaitTicks + NightLengthTicks));
	}

	/// <summary>
	/// S3: on the bridge's row in the Underworld. The first time, a hellevator from near the spawn (<see cref="ShaftOffsetTiles"/> the
	/// bridge's way); once there, the bridge is known from where the bot stands; later (after a death) back down the same shaft.
	/// </summary>
	private ISkill ResolveUnderworld(BotContext ctx)
	{
		int row = BridgeRowFor(ctx), stand = row - 1;
		if (ctx.UnderworldRow <= 0)
			return null; // no Underworld known (a test world without one)
		TilePos n = ctx.Node;
		if (!bridgeKnown && HellevatorSkill.Arrived(ctx, stand))
			KnowBridge(ctx, n, row);
		if (bridgeKnown && n.Y >= ctx.UnderworldRow)
			return null;
		// The bridge alone (the synthetic scenarios' plan) works from where the bot stands down there, never a shaft from the surface:
		// seen in game (voodoo_rule, 2026-10-17), knocked into the lava and respawned at the world's spawn, it went down a real one.
		if (Plan == Plans.Hellbridge) {
			stuck = $"the hellbridge plan starts on the bridge's row in the Underworld (row {stand}), not at {n}";
			return null;
		}
		ctx.DepthErrand = true;
		if (Scheduler == Schedulers.Errands && !ErrandAsking)
			return ProposeWallOfFlesh(ctx, Errands.WallOfFleshErrand.PreparePhase);
		if (shaftX == null) {
			int from = ctx.SpawnTile?.X ?? n.X;
			shaftX = from + BridgeDirection(ctx, from) * ShaftOffsetTiles;
			ctx.Log.Info($"Auto: the hellevator goes down at x {shaftX} ({ShaftOffsetTiles} tiles {(bridgeDir > 0 ? "east" : "west")} of the spawn), to row {stand}");
		}
		// Every trip down is the operation, the first and each one back after a death (seen in X2B: the trip back, not owned,
		// went to a work bench and arrows "instead of going back to Hellevator").
		if (Scheduler == Schedulers.Operation && descent == null) {
			descent = new Operation(bridgeKnown ? "descent (back down)" : "descent", $"the hellevator at x {shaftX}", ctx.Deaths, ctx.Tick);
			ctx.Log.Info($"Operation {descent.Name} start: {descent.Identity} to row {stand}; owns the bot until on the row, blocked, or a death");
		}
		return Do(bridgeKnown ? $"back down the hellevator at x {shaftX} to the hellbridge on row {row}" : $"down a hellevator at x {shaftX} to row {stand} in the Underworld",
			new HellevatorSkill(shaftX.Value, stand));
	}

	/// <summary>
	/// The bridge from where the bot stands on its row: begun from the column under the body's front, its summon end a bridge's
	/// length the summon side's way or as far as the world allows (<see cref="BridgeEdgeRoom"/> from the edge), and the rest of its
	/// length the other way past the shaft (the flight end). With the summon end against the edge, a run resumed anywhere on the
	/// bridge (a saved world) plans the same bridge.
	/// </summary>
	private void KnowBridge(BotContext ctx, TilePos n, int row)
	{
		// Standing on a bridge already built (a run resumed from a saved world with the planner's memory fresh: E12, 2026-10-17, at
		// the far end with the doll in hand): that bridge as it stands, the summon side the shorter one from here (the flight side
		// wants the length).
		if (HellbridgeSkill.Built(ctx.Grid, row, n.X)) {
			int eastLimit = ctx.WorldWidth > 0 ? ctx.WorldWidth - BridgeEdgeRoom : n.X + 4 * BridgeLength;
			int westLimit = ctx.WorldWidth > 0 ? BridgeEdgeRoom : Math.Max(1, n.X - 4 * BridgeLength);
			int east = HellbridgeSkill.BridgeEnd(ctx.Grid, row, n.X, eastLimit, ctx);
			int west = HellbridgeSkill.BridgeEnd(ctx.Grid, row, n.X, westLimit, ctx);
			if (east - west >= BridgeLength / ResumedBridgeShare) {
				bridgeDir = east - n.X <= n.X - west ? 1 : -1;
				int dir0 = bridgeDir;
				int stTo = dir0 > 0 ? east : west, stBack = dir0 > 0 ? west : east;
				// Shorter than the plan's length (a run resumed mid-way: E21, 2026-10-17, 2,255 of 3,829 columns taken as the whole bridge,
				// "done" at once, the fight's run short by half): the summon end goes on outward, over the lava (the last column with lava
				// under where the doll lands), and what that cannot cover the flight end takes.
				int shortfall = BridgeLength - (east - west);
				if (shortfall > 0) {
					int toLimit = dir0 > 0 ? eastLimit : westLimit;
					int toWanted = dir0 > 0 ? Math.Min(stTo + shortfall, toLimit) : Math.Max(stTo - shortfall, toLimit);
					int lavaEnd = LastLavaColumn(ctx, toWanted, stTo, dir0, row) ?? stTo;
					shortfall -= Math.Abs(lavaEnd - stTo);
					stTo = lavaEnd;
					if (shortfall > 0) {
						int backLimit = dir0 > 0 ? westLimit : eastLimit;
						stBack = dir0 > 0 ? Math.Max(stBack - shortfall, backLimit) : Math.Min(stBack + shortfall, backLimit);
					}
				}
				toEndKnown = backEndKnown = int.MinValue;
				bridgeEndsTick = -1;
				bridgeKnown = true;
				bridgeRow = row;
				bridgeFrom = n.X;
				bridgeTo = stTo;
				bridgeBack = stBack;
				ctx.Log.Info($"Auto: on a hellbridge already built on row {row}, x {west}-{east}: the summon end is x {bridgeTo}"
					+ (stTo != (dir0 > 0 ? east : west) || stBack != (dir0 > 0 ? west : east) ? $"; short of the plan's {BridgeLength}: on to x {bridgeTo} and x {bridgeBack}" : ""));
				return;
			}
		}
		int dir = BridgeDirection(ctx, n.X);
		int from = dir > 0 ? n.X + 1 : n.X;
		var (to, back) = BridgeSpan(from, dir, BridgeLength, ctx.WorldWidth);
		// The summon end over the lava: the sea stops short of the world's edge (E12, 2026-10-17: "no lava under where the doll would land
		// past the hellbridge's end at x 4080"), so the end is drawn back to the last column with lava under where the doll lands, and
		// the flight side takes the rest of the length.
		if (LastLavaColumn(ctx, to, from, dir, row) is int lavaTo && lavaTo != to)
			(to, back) = BridgeSpan(from, dir, BridgeLength, ctx.WorldWidth, lavaTo);
		toEndKnown = backEndKnown = int.MinValue;
		bridgeEndsTick = -1;
		bridgeKnown = true;
		bridgeRow = row;
		bridgeFrom = from;
		bridgeTo = to;
		bridgeBack = back;
		ctx.Log.Info($"Auto: underworld reached at {n}: the hellbridge goes on row {row} from x {from}, {Math.Abs(to - from)} tiles {(dir > 0 ? "east" : "west")} to the summon end at x {to}"
			+ (back != from ? $" and {Math.Abs(back - from)} tiles {(dir > 0 ? "west" : "east")} to x {back}" : "") + $" ({Math.Abs(to - back)} tiles in all)");
	}

	/// <summary>
	/// The bridge's two ends from where it is begun (<paramref name="from"/>), the summon side <paramref name="dir"/>: the summon end a
	/// <paramref name="length"/> that way or as far as the world allows (<see cref="BridgeEdgeRoom"/> from its edge; no edge with
	/// <paramref name="worldWidth"/> 0), the flight end the rest of the length the other way, as far as the world allows that way.
	/// </summary>
	public static (int To, int Back) BridgeSpan(int from, int dir, int length, int worldWidth, int? toLimit = null)
	{
		int east = worldWidth > 0 ? Math.Max(from, worldWidth - BridgeEdgeRoom) : int.MaxValue / 4;
		int west = worldWidth > 0 ? Math.Min(from, BridgeEdgeRoom) : int.MinValue / 4;
		// The summon end held back to a column (the last with lava under where the doll lands).
		if (toLimit is int lim) {
			if (dir > 0)
				east = Math.Max(from, Math.Min(east, lim));
			else
				west = Math.Min(from, Math.Max(west, lim));
		}
		int to = dir > 0 ? Math.Min(from + length, east) : Math.Max(from - length, west);
		int rest = length - Math.Abs(to - from);
		int back = dir > 0 ? Math.Max(from - rest, west) : Math.Min(from + rest, east);
		return (to, back);
	}

	/// <summary>
	/// The Wall of Flesh expedition as one errand (docs ERRAND_DESIGN.md, migration steps 3 and 6; Schedulers.Errands): proposed from
	/// whichever of its stages the plan reaches unmet, starting at the phase the world is at. It chooses the shaft's column from the
	/// known terrain near the preferred offset, owns the bot through deaths to the Wall's loot, and ends blocked with the reason: a
	/// column blocked on the way down is left out next time (MaxBlockedShafts of them: the plan is stuck), the same block twice in a
	/// later phase too.
	/// </summary>
	private ISkill ProposeWallOfFlesh(BotContext ctx, string phaseAsked)
	{
		if (Proposed != null)
			return null;
		// The phase from what the world and the plan know, whichever stage asked (seen in game, E3 2026-10-17: the bridge blocked, and the
		// errand was proposed again from the top, "[prepare]", choosing a new column with the shaft dug and the bridge begun).
		string phase = ctx.World != null && ctx.World.BossDefeated(ctx.Ids.WallOfFlesh) ? Errands.WallOfFleshErrand.LootPhase
			: hellbridgeDone ? Errands.WallOfFleshErrand.FightPhase
			: bridgeKnown ? Errands.WallOfFleshErrand.BridgePhase
			: phaseAsked == Errands.WallOfFleshErrand.PreparePhase ? phaseAsked : Errands.WallOfFleshErrand.DescendPhase;
		if (blockedShafts.Count >= MaxBlockedShafts) {
			stuck = $"no way to the Underworld: the shaft blocked at x {string.Join(", ", blockedShafts)}";
			return null;
		}
		if (BlockedTwice(Errands.WallOfFleshErrand.ErrandId) && LastOutcome(Errands.WallOfFleshErrand.ErrandId) is Errands.Outcome last) {
			stuck = $"the Wall of Flesh errand blocked twice: {last}";
			return null;
		}
		int row = BridgeRowFor(ctx), stand = row - 1;
		int fromX = ctx.SpawnTile?.X ?? ctx.Node.X;
		int preferred = shaftX ?? fromX + BridgeDirection(ctx, fromX) * ShaftOffsetTiles;
		var errand = new Errands.WallOfFleshErrand(preferred, stand, ctx.Ids, phase);
		if (shaftX == preferred && shaftRoute.Count > 0)
			errand.UseRoute(shaftRoute); // the shaft's route from the errand before (E30: a new errand began a fresh column)
		foreach (int blockedX in blockedShafts)
			errand.Exclude(blockedX);
		Proposed = errand;
		return null;
	}

	/// <summary>S4: the hellbridge built to its far end (latched: the fight's loot box may take a cell out later).</summary>
	private ISkill ResolveHellbridge(BotContext ctx)
	{
		if (hellbridgeDone)
			return null;
		if (Scheduler == Schedulers.Errands && Plan != Plans.Hellbridge && !ErrandAsking)
			return ProposeWallOfFlesh(ctx, bridgeKnown ? Errands.WallOfFleshErrand.BridgePhase : Errands.WallOfFleshErrand.DescendPhase);
		if (!bridgeKnown) {
			// Down there already with the planner's memory fresh (a checkpoint resumed: world_wof E11, 2026-10-17), ResolveUnderworld
			// learns where the bridge goes and has nothing to do; the bridge itself is still to build, below. (Before: the need read as
			// met, the errand went to its fight phase at the start, and the fight's deck was the whole span, not the part standing.)
			ISkill down = ResolveUnderworld(ctx);
			if (down != null || !bridgeKnown)
				return down;
		}
		ctx.DepthErrand = true;
		var (toEnd, backEnd) = BridgeEnds(ctx);
		if (toEnd == bridgeTo && backEnd == bridgeBack) {
			hellbridgeDone = true;
			ctx.Log.Info($"Auto: the hellbridge is built: row {bridgeRow}, x {HellbridgeText}");
			return null;
		}
		// The summon side first (spec S4), then the flight side past the shaft.
		bool toward = toEnd != bridgeTo;
		int end = toward ? toEnd : backEnd, target = toward ? bridgeTo : bridgeBack;
		// A step that failed there before (not for want of blocks: BridgeBlocks sees to that) is not started again and again.
		if (end == bridgeFailEnd && bridgeFails >= MaxBridgeFails) {
			stuck = $"the hellbridge failed {bridgeFails} times in a row at x {end} on row {bridgeRow}";
			return null;
		}
		return Do($"building the hellbridge on row {bridgeRow} from x {bridgeFrom} to {target} (built to {end}, {HellbridgeProgress(ctx):P0} of it all)",
			new HellbridgeSkill(bridgeRow, bridgeFrom, target));
	}

	/// <summary>The blocks the bridge takes: stone, dirt or the ash its digging gives (BuildingBlocks and the Ash Block).</summary>
	private static int[] BridgeBlockItems(GameIds ids) => Tiers(ids.BuildingBlocks.Concat(new[] { ids.AshBlock }).ToArray());

	/// <summary>S1: blocks for what is left of the bridge and <see cref="BridgeBlockMargin"/> (all of it before it is begun).</summary>
	private ISkill ResolveBridgeBlocks(BridgeBlocks need, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (hellbridgeDone)
			return null;
		// Before the descent, only what the way down takes (BridgeDepartureBlocks): the shaft itself gives the rest (two cells a row,
		// some 1,500 for the 760 rows to the Underworld), and the Underworld its ash. Seen in game (X2B, 2026-10-17): all 3,929
		// before leaving, the stone near home ran out under the gear's depth limit, and the bot went for "new ground at (2455,208)",
		// the top of a big tree, building stone stairs up to it.
		int left = bridgeKnown ? BridgeLength : Math.Min(BridgeLength, BridgeDepartureBlocks - BridgeBlockMargin);
		if (bridgeKnown) {
			var (toEnd, backEnd) = BridgeEnds(ctx);
			left = Math.Abs(bridgeTo - toEnd) + Math.Abs(bridgeBack - backEnd);
			// Down there already (more blocks to get on the way): the gear's depth limit does not hold on the errand.
			ctx.DepthErrand = true;
		}
		int count = left + BridgeBlockMargin;
		if (need.Batch > 0 && left > 0)
			count = Math.Min(count, need.Batch);
		// Down there the blocks are the ash underfoot, a quarry a few columns off the deck, never a stone deposit up in the caverns
		// (the cheapest-to-finish choice, a stack of stone short by a few, sent the bot 55 rows up to one and into lava: E23,
		// 2026-10-17, "need 22 more Stone Block ... Peppe tried to swim in lava"). What is carried of any block counts.
		if (bridgeKnown && Errands.WallOfFleshErrand.DownThere(ctx)) {
			int shortfall = count - ctx.Inventory.CountAny(BridgeBlockItems(ctx.Ids));
			if (shortfall <= 0)
				return null;
			return Resolve(new HaveItem(ctx.Ids.AshBlock, ctx.Inventory.Count(ctx.Ids.AshBlock) + shortfall, need.Label), claims, depth + 1, ctx);
		}
		return Resolve(new HaveItem(BridgeBlockItems(ctx.Ids), count, need.Label), claims, depth + 1, ctx);
	}

	/// <summary>The wall the bridge stopped at was blasted (WallOfFleshErrand): the bridge's failures at that end start afresh.</summary>
	public void BridgeBlasted()
	{
		bridgeFails = 0;
		bridgeFailEnd = int.MinValue;
	}

	/// <summary>The hellbridge as a fight's deck (FightSkill.Deck): its columns and the feet row on it, once known.</summary>
	public (int MinX, int MaxX, int StandY)? HellbridgeArena => HellbridgeBounds();

	/// <summary>
	/// The bridge as it stands: from its start to each end as the grid reads them (<see cref="BridgeEnds"/>), and the feet row on it.
	/// The fight's deck while the bridge is built (WallOfFleshErrand.FightDeck), so no fight follows a target along the Underworld's
	/// floor past the end.
	/// </summary>
	public (int MinX, int MaxX, int StandY)? HellbridgeStanding(BotContext ctx)
	{
		if (!bridgeKnown)
			return null;
		var (to, back) = BridgeEnds(ctx);
		return (Math.Min(Math.Min(to, back), bridgeFrom), Math.Max(Math.Max(to, back), bridgeFrom), bridgeRow - 1);
	}

	/// <summary>The fight's arena: the bridge's columns and the feet row on it.</summary>
	private (int MinX, int MaxX, int StandY)? HellbridgeBounds() =>
		bridgeKnown ? (Math.Min(bridgeBack, bridgeTo), Math.Max(bridgeBack, bridgeTo), bridgeRow - 1) : null;

	private BossFightSkill WallOfFleshFight(BossDefeated b, BotContext ctx, bool up = false)
	{
		GameIds ids = ctx.Ids;
		int dir = bridgeDir != 0 ? bridgeDir : 1;
		return new BossFightSkill(b.SummonItem, b.Npc, b.Minions) { Arena = HellbridgeBounds(), Summon = () => WallOfFleshPattern.Summon(ids, dir), BossAlreadyUp = up };
	}

	/// <summary>
	/// S5-S7. With the Wall up: the fight along the bridge (an unplanned one, from a Voodoo Demon killed over lava, said so). Else a
	/// doll lying in sight is picked up; with none carried, the hunt at the bridge's far end (S5: Voodoo Demons are fought there, the
	/// doll drops on the bridge or burns in the lava, which is the summon); with one: the summon's checks (S6: the Guide alive, the
	/// end away from the world's middle, lava under where the doll lands, at the end, full life with no potion sickness), and the
	/// fight, whose summon throws it (<see cref="WallOfFleshPattern.Summon"/>).
	/// </summary>
	/// <summary>
	/// At the summon column: every Wooden Arrow the bag's makings allow, up to <see cref="WofFightArrows"/>, crafted there (a bench made
	/// beside it if the one known is far), and nothing fetched: the fight goes on with what is carried. Seen in game (world_wof E26,
	/// 2026-10-17): summoned with 500 arrows (the fight phase's top-up line is 400, for the walk), 120 wood and 12 stone in the bag;
	/// out at 30 arrows with the Wall at 1384/8000.
	/// </summary>
	private ISkill ArrowsBeforeTheSummon(BotContext ctx)
	{
		int arrow = ctx.Ids.WoodenArrow;
		RecipeInfo r = arrow > 0 ? BestRecipe(arrow, ctx) : null;
		if (r == null || r.Ingredients.Count == 0)
			return null;
		int[] all = Tiers(arrow, ctx.Ids.UnholyArrow);
		int have = ctx.Inventory.CountAny(all);
		// (Below the fight's least the arrows are got at the base, after a death, with the trees near home: WallOfFleshErrand's
		// demand. Down here only the bag's makings: E37, 2026-10-18, a gate here sent the bot gathering wood in the Underworld for
		// two game hours.)
		int crafts = r.Ingredients.Min(i => ctx.Inventory.CountAny(i.Accepts) / Math.Max(1, i.Stack));
		int target = Math.Min(WofFightArrows, have + crafts * r.ResultStack);
		if (crafts <= 0 || target <= have)
			return null;
		string before = stuck;
		// Wooden Arrows alone (the tiered need would cost Unholy Arrows' worm teeth, a hunt).
		ISkill s = Resolve(new HaveItem(arrow, ctx.Inventory.Count(arrow) + target - have, "arrows for the fight (made at the summon column)"),
			new Dictionary<int, int>(), 1, ctx);
		if (s is CraftSkill or PlaceItemSkill || s != null && ReferenceEquals(s, stationWalk)
			&& KnownStations.TryGetValue(stationWalkType, out TilePos bench) && bench.ChebyshevTo(ctx.Node) <= FarStationTiles)
			return s;
		stuck = before; // nothing more to be had here without fetching: summoned with what is carried
		return null;
	}

	private ISkill ResolveWallOfFlesh(BossDefeated b, bool bossHere, BotContext ctx)
	{
		if (Scheduler == Schedulers.Errands && Plan == Plans.WallOfFlesh && !ErrandAsking && !bossHere)
			return ProposeWallOfFlesh(ctx, hellbridgeDone ? Errands.WallOfFleshErrand.FightPhase : bridgeKnown ? Errands.WallOfFleshErrand.BridgePhase : Errands.WallOfFleshErrand.DescendPhase);
		ctx.DepthErrand = true;
		GameIds ids = ctx.Ids;
		if (bossHere) {
			if (!thrownByPlan && !unplannedLogged) {
				unplannedLogged = true;
				if (huntOn)
					ctx.Log.Info($"Auto: the Wall of Flesh summoned by the hunt (a Voodoo Demon died over the lava at the bridge's end, its doll burnt), us at {ctx.Node}");
				else
					ctx.Log.Warn($"Auto: unplanned wof: the Wall of Flesh is up with no doll thrown by the plan (a Voodoo Demon killed over lava?), us at {ctx.Node}");
			}
			return Do($"fighting the {b.Label} along the hellbridge", WallOfFleshFight(b, ctx, up: true));
		}
		if (!hellbridgeDone)
			return ResolveHellbridge(ctx);
		int doll = ids.GuideVoodooDoll;
		if (ctx.Drops.Any(d => d.Type == doll))
			return Do("picking up the Guide Voodoo Doll lying in sight", new CollectDropsSkill(d => d.Type == doll, BotBrain.LootRadiusTiles));
		int dir = bridgeDir != 0 ? bridgeDir : 1, row = bridgeRow, stand = row - 1;
		// Journey: the doll researched (1 is its research, Sacrifices.tsv "D"), one is duplicated for each try, no hunt.
		if (ctx.Inventory.Count(doll) <= 0 && ctx.Infinite(doll))
			return Do("duplicating a Guide Voodoo Doll (researched)", new DuplicateSkill(doll, 1));
		if (ctx.Inventory.Count(doll) <= 0) {
			// Not at the very end: the columns opened for the doll are a fall into the lava beside it, and the hunt waits for hours.
			// Seen in game (world_wof E36, 2026-10-18): waiting there, Hellbats' knockbacks took the bot past the end, and the walk back
			// jumped the opened columns and fell 37 rows.
			int back = LandingTo + HuntBackTiles;
			if (!NearBridgeEnd(ctx, HuntTiles) || NearBridgeEnd(ctx, back - 2)) {
				int near = bridgeTo - dir * back, far = bridgeTo - dir * HuntTiles;
				int lo = Math.Min(near, far), hi = Math.Max(near, far);
				return Do($"to the hellbridge's far end to hunt a Voodoo Demon (x {bridgeTo})", new WalkToSkill(new GoalStandOnRow(stand, lo, hi - 1, ctx.Physics)) { Quiet = true });
			}
			huntOn = true;
			return Do($"hunting a Voodoo Demon at the hellbridge's far end (x {bridgeTo}) for its Guide Voodoo Doll",
				new WaitSkill("a Guide Voodoo Doll", c => c.Inventory.Count(doll) > 0 || BossUp(c, b.Npc) || c.Drops.Any(d => d.Type == doll), NightWaitTicks));
		}
		if (!ctx.World.TownNpcAlive(ids.Guide))
			return ResolveTownNpc(new TownNpcAlive(ids.Guide, "the Guide"), ctx);
		// The summon column: the end with lava under where the doll lands, else the last column back from it with lava below (the lava
		// sea stops short of the world's edge: E12, 2026-10-17, "no lava under where the doll would land past the hellbridge's end at
		// x 4080, and no room to go on toward the world's edge", the errand blocked with the whole bridge built and the doll in hand).
		// A deck over the landing columns (a bridge that went on past the lava) is opened for the doll to fall through.
		if (SummonColumn(ctx, dir, row) is not int sx) {
			stuck = $"no lava under the hellbridge's row anywhere from its end at x {bridgeTo} back to x {bridgeFrom}: nowhere for the doll to fall";
			return null;
		}
		if (ctx.WorldWidth > 0 && Math.Abs(sx - ctx.WorldWidth / 2) <= SummonCentreGap) {
			stuck = $"the summon column x {sx} is within {SummonCentreGap} columns of the world's middle: the Wall could come from either side";
			return null;
		}
		var endStand = new TilePos(dir > 0 ? sx - 1 : sx, stand);
		List<TilePos> landing = LandingDeck(ctx, sx, dir, row);
		if (landing.Count > 0) // from the deck's last column that stays: every landing cell within the pickaxe's reach
			return Do($"opening the hellbridge's deck over the lava at x {landing[0].X}-{landing[^1].X} for the doll to fall through",
				FarCellsFirst(landing, sx, dir, stand));
		// Near the summon column: the arrows first (a bench within FarStationTiles walked to, else made here).
		if (ctx.Node.ChebyshevTo(endStand) <= FarStationTiles + HomeArrivedTiles && ArrowsBeforeTheSummon(ctx) is ISkill arrows) {
			// Not at the summon end itself: a bench is placed and arrows made where the body stands, and the end is a stub of deck between
			// the doll's opened columns and the deck's last column, lava under both. Seen in game (world_wof E61, 2026-10-18): "placing a
			// Ash Wood Work Bench here" at (4073,1041), the walk to the spot stepped off the end, 35 rows into lava.
			// (A bench already standing near the end is walked to and used where it is: only a new one placed "here" is placed back.)
			if (arrows is PlaceItemSkill && (ctx.Node.X - (endStand.X - dir * ArrowsBackTiles)) * dir > 0)
				return Do($"back {ArrowsBackTiles} columns from the summon end to make the arrows there",
					new WalkToSkill(new GoalStandOnRow(stand, Math.Min(endStand.X - dir * (ArrowsBackTiles + 4), endStand.X - dir * ArrowsBackTiles),
						Math.Max(endStand.X - dir * (ArrowsBackTiles + 4), endStand.X - dir * ArrowsBackTiles), ctx.Physics)) { Quiet = true });
			return arrows;
		}
		// At the summon column, or a column short of it (the doll lands 3-6 columns past it from either, LandingFrom), and only where
		// the columns it lands in from where the body stands are lava. Seen in game (world_wof E60, 2026-10-18): thrown east from x 4073,
		// two columns past the stand, the doll lay on the ground at x 4079 beyond the lava; the walk to pick it up left the deck and a
		// Hellbat killed the bot under it.
		if (ctx.Node.Y != stand || Math.Abs(ctx.Node.X - endStand.X) > 1 || !LavaBeyond(ctx, ctx.Node.X, dir, row))
			return Do($"to the hellbridge's summon column at {endStand} to summon the {b.Label}", new WalkToSkill(new GoalStandAt(endStand, ctx.Physics)) { Quiet = true });
		if ((ctx.Combat.LifeFraction < 1f || ctx.Combat.PotionSickTicks > 0) && ctx.Tick - lastRestEnd > MaxRestTicks)
			return Do($"resting to full life before summoning the {b.Label} ({ctx.Combat.Life}/{ctx.Combat.LifeMax})",
				new WaitSkill("life", c => c.Combat.LifeFraction >= 1f && c.Combat.PotionSickTicks == 0, MaxRestTicks));
		thrownByPlan = true;
		ctx.Log.Info($"Auto: the doll's landing columns from {ctx.Node} (first thing under the deck): {LandingScan(ctx, ctx.Node.X, dir, row)}");
		return Do($"summoning the {b.Label}: a Guide Voodoo Doll into the lava off the hellbridge at x {sx}", WallOfFleshFight(b, ctx));
	}

	/// <summary>The column the doll is thrown from: the summon end with lava under where it lands, else the last column back toward the start with it; null with none.</summary>
	public int? SummonColumn(BotContext ctx, int dir, int row) => LastLavaColumn(ctx, bridgeTo, bridgeBack, dir, row, ThrowSlack, standOnDeck: true);

	/// <summary>Columns of lava past the landing window the summon column also wants: the throw may come from a column or two off.</summary>
	public const int ThrowSlack = 2;

	/// <summary>
	/// Where the body stands to open the landing cells (from <see cref="LandingFrom"/> past the summon column): next to the first one,
	/// but not on it. The node is the body's left column and the body is two wide, so going east it stands one column further back.
	/// Seen in game (world_wof E62, E63, 2026-10-18): mining the deck's last cell (x 4073) from (4072,1041), the body half on it, it went
	/// through with the cell and fell 35 rows into the lava, twice.
	/// </summary>
	public static TilePos MiningStand(int sx, int dir, int stand) => new(sx + dir * (LandingFrom - 1) - (dir > 0 ? 1 : 0), stand);

	/// <summary>
	/// The landing cells opened far ones first, from beside the first cell (the body may stand on it while it is there: the far cells
	/// are within reach from there), then the first one from <see cref="MiningStand"/>, where the body is off it.
	/// </summary>
	public static MineCellsSkill FarCellsFirst(List<TilePos> landing, int sx, int dir, int stand)
	{
		int first = sx + dir * LandingFrom;
		var far = landing.Where(c => c.X != first).ToList();
		return far.Count > 0
			? new MineCellsSkill(far, new TilePos(sx + dir * (LandingFrom - 1), stand))
			: new MineCellsSkill(landing, MiningStand(sx, dir, stand));
	}

	/// <summary>The arrows before the summon (and the bench they may need) are made at least this many columns back from the summon column.</summary>
	public const int ArrowsBackTiles = 10; // back to the far end: on a bridge taken as it stands, "from" is only where the bot stood

	/// <summary>The last column from <paramref name="end"/> back toward <paramref name="start"/> with lava under where the doll lands past it (<see cref="LavaBeyond"/>), or null.</summary>
	public static int? LastLavaColumn(BotContext ctx, int end, int start, int dir, int row, int slack = 0, bool standOnDeck = false)
	{
		for (int x = end; (x - start) * dir >= 0; x -= dir)
			if (LavaBeyond(ctx, x, dir, row, slack) && (!standOnDeck || StandHasFloor(ctx, x, dir, row)))
				return x;
		return null;
	}

	/// <summary>
	/// The deck under where the body stands to throw from summon column <paramref name="sx"/> (two columns, the stand's and the next
	/// toward the end): a column chosen over the columns already opened for the doll has nowhere to stand. Seen in game (world_wof E62,
	/// 2026-10-18): the summon column moved back to x 4070 (ThrowSlack), its stand 4069 inside the opened 4068-4071; the walk there from
	/// 4072 went off the deck's end into the lava.
	/// </summary>
	public static bool StandHasFloor(BotContext ctx, int sx, int dir, int row)
	{
		int standX = dir > 0 ? sx - 1 : sx;
		return ctx.Grid.Get(standX, row).CanStandOn() && ctx.Grid.Get(standX + 1, row).CanStandOn();
	}

	/// <summary>The deck cells over the landing columns past the summon column, where built: the doll would lie on them.</summary>
	/// <summary>Rows over the deck the thrown doll flies through (it leaves at the body's height with vy -2, Player.DropSelectedItem).</summary>
	public const int FlightRows = 3;

	public static List<TilePos> LandingDeck(BotContext ctx, int sx, int dir, int row)
	{
		var cells = new List<TilePos>();
		// The doll's flight first: a block over the deck between the stand and the landing columns (a lip the bridge raised at its
		// end against knockback, a block placed in a fight) stops it on the deck. Seen in game (world_wof E29, 2026-10-18): thrown
		// east from x 4070 seventy times in three game days, it stopped at (4071,1041) each time ("did not reach lava").
		for (int k = 1; k <= LandingTo; k++)
			for (int y = row - FlightRows; y < row; y++)
				if (ctx.Grid.Get(sx + dir * k, y).IsBlock())
					cells.Add(new TilePos(sx + dir * k, y));
		for (int k = LandingFrom; k <= LandingTo; k++)
			if (ctx.Grid.Get(sx + dir * k, row).CanStandOn())
				cells.Add(new TilePos(sx + dir * k, row));
		return cells;
	}

	/// <summary>
	/// Lava the first thing under the bridge's row in every column the doll may land in (<see cref="LandingFrom"/>-<see cref="LandingTo"/>
	/// past the end): what a player sees glowing below. The doll lies on anything else (items rest on blocks, Item.cs:50828).
	/// </summary>
	public static bool LavaBeyond(BotContext ctx, int endX, int dir, int row, int slack = 0)
	{
		for (int k = LandingFrom; k <= LandingTo + slack; k++) {
			int x = endX + dir * k;
			bool lava = false;
			for (int y = row + 1; y <= row + LavaScanRows; y++) {
				CellKind c = ctx.Grid.Get(x, y);
				if (c.HasLava()) {
					lava = true;
					break;
				}
				if (c.IsSolid() || c.IsPlatform() || c.IsLiquid())
					break;
			}
			if (!lava)
				return false;
		}
		return true;
	}

	/// <summary>Per landing column (<see cref="LandingFrom"/>-<see cref="LandingTo"/>+<see cref="ThrowSlack"/>): the first non-empty cell under the row, for the logs.</summary>
	public static string LandingScan(BotContext ctx, int fromX, int dir, int row)
	{
		var parts = new List<string>();
		for (int k = LandingFrom; k <= LandingTo + ThrowSlack; k++) {
			int x = fromX + dir * k;
			string what = "nothing";
			for (int y = row + 1; y <= row + LavaScanRows; y++) {
				CellKind c = ctx.Grid.Get(x, y);
				if (c != CellKind.Empty) {
					what = $"{c}@{y}";
					break;
				}
			}
			parts.Add($"{x}:{what}");
		}
		return string.Join(" ", parts);
	}

	/// <summary>S8: the loot where the Wall died, its brick box opened by the walk (any pickaxe breaks demonite brick): picked up, then done.</summary>
	private ISkill ResolveBossLoot(BossLoot need, BotContext ctx)
	{
		if (lootDone || ctx.World == null || !ctx.World.BossDefeated(need.Npc))
			return null;
		if (Scheduler == Schedulers.Errands && Plan == Plans.WallOfFlesh && !ErrandAsking)
			return ProposeWallOfFlesh(ctx, Errands.WallOfFleshErrand.LootPhase);
		if (wofDiedAt is not (float X, float Y) at) {
			lootDone = true;
			return null;
		}
		var spot = new TilePos((int)(at.X / 16f), (int)(at.Y / 16f));
		bool Near(DropInfo d) => Math.Abs(d.Tile.X - spot.X) <= WofLootRadiusTiles && Math.Abs(d.Tile.Y - spot.Y) <= WofLootRadiusTiles;
		if (CollectDropsSkill.AnyInRange(ctx, Near, PerceivedLootTiles))
			return Do($"picking up the {need.Label} round {spot}", new CollectDropsSkill(Near, PerceivedLootTiles));
		if (spot.ChebyshevTo(ctx.Node) > WofLootRadiusTiles / 2)
			return Do($"to where the Wall of Flesh died ({spot}) for its loot", new WalkToSkill(new GoalNearTile(spot, 3, ctx.Physics)) { Quiet = true });
		lootDone = true;
		ctx.Log.Info($"Auto: wof loot collected round {spot}");
		return null;
	}

	/// <summary>Drops are seen up to 50 tiles off (TerraBotPlayer.PerceptionRadiusPx): the loot is fetched from that far.</summary>
	public const int PerceivedLootTiles = 50;

	private void WallOfFleshFinished(ISkill skill, SkillStatus status, BotContext ctx)
	{
		if (skill is HellevatorSkill shaft) {
			if (status == SkillStatus.Succeeded && shaft.Landed is TilePos landed && !bridgeKnown)
				KnowBridge(ctx, landed, shaft.StandRow + 1);
			// Under the errand scheduler the column is the access errand's (chosen from the terrain, changed once by it): never shifted here.
			else if (Scheduler != Schedulers.Errands && status == SkillStatus.Failed && shaft.FailureReason.StartsWith("no way down") && shaftX is int x) {
				shaftX = x + (bridgeDir != 0 ? bridgeDir : 1) * ShaftShiftTiles;
				ctx.Log.Info($"Auto: no ground on row {shaft.StandRow + 1} under the shaft at x {x}; the hellevator moves to x {shaftX}");
				if (descent != null)
					ctx.Log.Info($"Operation {descent.Name}: target moved from x {x} to x {shaftX} ({shaft.FailureReason})");
			}
			if (descent != null && status == SkillStatus.Succeeded)
				descent.Failures = 0;
			else if (descent != null && status == SkillStatus.Failed && shaft.FailureReason != "died" && ++descent.Failures >= DescentMaxFailures) {
				descent.Blocker = shaft.FailureReason;
				EndDescent(ctx, $"blocked: {descent.Failures} hellevator failures in a row, the last '{shaft.FailureReason}'; the plan's stages from the top again");
			}
		}
		if (skill is HellbridgeSkill hb) {
			if (status == SkillStatus.Succeeded)
				bridgeFails = 0;
			else if (status == SkillStatus.Failed && !hb.FailureReason.StartsWith("out of blocks") && !hb.FailureReason.StartsWith("died")) {
				bridgeFails = hb.End == bridgeFailEnd ? bridgeFails + 1 : 1;
				bridgeFailEnd = hb.End;
			}
		}
		if (skill is BossFightSkill { IsWallOfFlesh: true } fight) {
			if (status == SkillStatus.Succeeded)
				wofDiedAt = fight.WallLastSeen;
			else
				thrownByPlan = huntOn = unplannedLogged = false; // the next Wall is a new summon (or an unplanned one)
		}
	}

	/// <summary>What the protected cells depend on: the bridge's row, first column and built end.</summary>
	private (int, int, int) HellbridgeKey(BotContext ctx)
	{
		if (!bridgeKnown)
			return (0, 0, 0);
		var (toEnd, backEnd) = BridgeEnds(ctx);
		return (bridgeRow, toEnd, backEnd);
	}

	/// <summary>The hellbridge's built blocks: no walk, mine or shelter breaks them (the fight's only way, spec section 5).</summary>
	private void ProtectHellbridge(BotContext ctx, HashSet<TilePos> cells)
	{
		if (!bridgeKnown)
			return;
		var (toEnd, backEnd) = BridgeEnds(ctx);
		for (int x = Math.Min(toEnd, backEnd); x <= Math.Max(toEnd, backEnd); x++)
			cells.Add(new TilePos(x, bridgeRow));
	}
}
