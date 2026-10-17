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
	public const int WofArrows = 1000;
	/// <summary>Healing potions into the fight (spec S1: 10, from the bag).</summary>
	public const int WofPotions = 10;
	/// <summary>
	/// Arrows enough for the fight, the floor on the way down (<see cref="HaveItem.OnTheWayDownAtLeast"/>): the two Classic wins of
	/// fight_wof_150 shot 496 and 512 (2148 carried, 1652 and 1636 left), the Unholy arrows among them.
	/// </summary>
	public const int WofFightArrows = 600;
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
	/// <summary>With no lava under the landing columns, the bridge goes on by this much and is looked at again.</summary>
	public const int BridgeExtendTiles = 8;
	/// <summary>The Wall's loot is looked for this far round where it died (its 9 x 9 brick box, NPC.cs:85023-85057).</summary>
	public const int WofLootRadiusTiles = 20;

	private int? shaftX;
	private int bridgeDir;
	/// <summary>The death count the way down began at (the hellevator given), -1 before: on the way down until the next death.</summary>
	private int wayDownDeaths = -1;
	/// <summary>The hellevator was begun since the last death (down the shaft, or the bridge's work, until a death sends the bot home).</summary>
	private bool OnTheWayDown(BotContext ctx) => wayDownDeaths >= 0 && wayDownDeaths == ctx.Deaths;
	private int wayDownNoted = -1;

	/// <summary>
	/// On the way down (<see cref="OnTheWayDown"/>), the errand owns the bot: the stages before the Underworld's are skipped but for
	/// what the errand itself takes (<see cref="ForTheWayDown"/>); the index of the Underworld's stage then, else -1. The surface's
	/// work (a stock topped up, an NPC room, max life) waits for the next time the bot is home, after a death. Seen in game
	/// (world_wof M12d-M12f, Peppe): the hellevator, once begun, left for "need 3 more Wood (gathering to 8)", "need 4 more Wood
	/// (gathering to 27)", "building NPC room 1", the crystal search: each time back up hundreds of rows, and down again.
	/// </summary>
	private int WayDownErrandFrom(IReadOnlyList<Need> stages, BotContext ctx)
	{
		if (!OnTheWayDown(ctx))
			return -1;
		for (int i = 0; i < stages.Count; i++)
			if (stages[i] is PrepFor { Inner: UnderworldReached }) {
				if (wayDownNoted != ctx.Deaths) {
					wayDownNoted = ctx.Deaths;
					ctx.Log.Info("Auto: on the way down to the Underworld: the surface's work waits until a death sends the bot home");
				}
				return i;
			}
		return -1;
	}

	/// <summary>What the way down takes, and so is still worked on during it: the bridge's blocks, the fight's arrows (their own floor).</summary>
	private static bool ForTheWayDown(Need stage) =>
		stage is PrepFor { Inner: BridgeBlocks } or PrepFor { Inner: HaveItem { OnTheWayDownAtLeast: > 0 } };
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
	private (int To, int Back) BridgeEnds(BotContext ctx) => (HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo),
		bridgeBack == bridgeFrom ? bridgeFrom : HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeBack));

	private string HellbridgeText => bridgeKnown ? $"row {bridgeRow}, x {Math.Min(bridgeBack, bridgeTo)}-{Math.Max(bridgeBack, bridgeTo)}" : "none";

	private void ResetWallOfFlesh()
	{
		WofPrepMet = false;
		shaftX = null;
		wayDownDeaths = -1;
		bridgeDir = 0;
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
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), WofArrows, "arrows") { RefillBelow = WofArrows * 4 / 5, OnTheWayDownAtLeast = WofFightArrows });
		// The most life crystals give (400), not the earlier bosses' 200: self-test fight_wof_150 on Classic (Peppe, the Wall at 8000),
		// at 200 life the bot died to an eye laser with the Wall at 1961, its damage 40.1 a second (enough), 19 hits for 336.
		// Surface prep (BeforeTheUnderworld): a run resumed down there, or a bridge built, is not sent back up for crystals; a death
		// there respawns it at the spawn, and the crystals come then. The earlier bosses' 200 is a must; the rest up to 400 for a
		// game day at most, then not again until a death (BestEffort): fight_wof_150 on Classic won 2 of 3 at 400, 0 of 1 at 200 and at 300 (the Wall at 331 and
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
	private ISkill ResolveTownNpc(TownNpcAlive need, BotContext ctx)
	{
		if (need.Npc <= 0 || ctx.World == null || ctx.World.TownNpcAlive(need.Npc))
			return null;
		return Do($"waiting for {need.Label} (none alive: the doll would be wasted; he moves back in by day)",
			new WaitSkill(need.Label, c => c.World.TownNpcAlive(need.Npc), NightWaitTicks + NightLengthTicks));
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
		if (shaftX == null) {
			int from = ctx.SpawnTile?.X ?? n.X;
			shaftX = from + BridgeDirection(ctx, from) * ShaftOffsetTiles;
			ctx.Log.Info($"Auto: the hellevator goes down at x {shaftX} ({ShaftOffsetTiles} tiles {(bridgeDir > 0 ? "east" : "west")} of the spawn), to row {stand}");
		}
		wayDownDeaths = ctx.Deaths;
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
		int dir = BridgeDirection(ctx, n.X);
		int from = dir > 0 ? n.X + 1 : n.X;
		var (to, back) = BridgeSpan(from, dir, BridgeLength, ctx.WorldWidth);
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
	public static (int To, int Back) BridgeSpan(int from, int dir, int length, int worldWidth)
	{
		int east = worldWidth > 0 ? Math.Max(from, worldWidth - BridgeEdgeRoom) : int.MaxValue / 4;
		int west = worldWidth > 0 ? Math.Min(from, BridgeEdgeRoom) : int.MinValue / 4;
		int to = dir > 0 ? Math.Min(from + length, east) : Math.Max(from - length, west);
		int rest = length - Math.Abs(to - from);
		int back = dir > 0 ? Math.Max(from - rest, west) : Math.Min(from + rest, east);
		return (to, back);
	}

	/// <summary>S4: the hellbridge built to its far end (latched: the fight's loot box may take a cell out later).</summary>
	private ISkill ResolveHellbridge(BotContext ctx)
	{
		if (hellbridgeDone)
			return null;
		if (!bridgeKnown)
			return ResolveUnderworld(ctx);
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
		int left = BridgeLength;
		if (bridgeKnown) {
			var (toEnd, backEnd) = BridgeEnds(ctx);
			left = Math.Abs(bridgeTo - toEnd) + Math.Abs(bridgeBack - backEnd);
			// Down there already (more blocks to get on the way): the gear's depth limit does not hold on the errand.
			ctx.DepthErrand = true;
		}
		return Resolve(new HaveItem(BridgeBlockItems(ctx.Ids), left + BridgeBlockMargin, need.Label), claims, depth + 1, ctx);
	}

	/// <summary>The fight's arena: the bridge's columns and the feet row on it.</summary>
	private (int MinX, int MaxX, int StandY)? HellbridgeBounds() =>
		bridgeKnown ? (Math.Min(bridgeBack, bridgeTo), Math.Max(bridgeBack, bridgeTo), bridgeRow - 1) : null;

	private BossFightSkill WallOfFleshFight(BossDefeated b, BotContext ctx)
	{
		GameIds ids = ctx.Ids;
		int dir = bridgeDir != 0 ? bridgeDir : 1;
		return new BossFightSkill(b.SummonItem, b.Npc, b.Minions) { Arena = HellbridgeBounds(), Summon = () => WallOfFleshPattern.Summon(ids, dir) };
	}

	/// <summary>
	/// S5-S7. With the Wall up: the fight along the bridge (an unplanned one, from a Voodoo Demon killed over lava, said so). Else a
	/// doll lying in sight is picked up; with none carried, the hunt at the bridge's far end (S5: Voodoo Demons are fought there, the
	/// doll drops on the bridge or burns in the lava, which is the summon); with one: the summon's checks (S6: the Guide alive, the
	/// end away from the world's middle, lava under where the doll lands, at the end, full life with no potion sickness), and the
	/// fight, whose summon throws it (<see cref="WallOfFleshPattern.Summon"/>).
	/// </summary>
	private ISkill ResolveWallOfFlesh(BossDefeated b, bool bossHere, BotContext ctx)
	{
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
			return Do($"fighting the {b.Label} along the hellbridge", WallOfFleshFight(b, ctx));
		}
		if (!hellbridgeDone)
			return ResolveHellbridge(ctx);
		int doll = ids.GuideVoodooDoll;
		if (ctx.Drops.Any(d => d.Type == doll))
			return Do("picking up the Guide Voodoo Doll lying in sight", new CollectDropsSkill(d => d.Type == doll, BotBrain.LootRadiusTiles));
		int dir = bridgeDir != 0 ? bridgeDir : 1, row = bridgeRow, stand = row - 1;
		var endStand = new TilePos(dir > 0 ? bridgeTo - 1 : bridgeTo, stand);
		// Journey: the doll researched (1 is its research, Sacrifices.tsv "D"), one is duplicated for each try, no hunt.
		if (ctx.Inventory.Count(doll) <= 0 && ctx.Infinite(doll))
			return Do("duplicating a Guide Voodoo Doll (researched)", new DuplicateSkill(doll, 1));
		if (ctx.Inventory.Count(doll) <= 0) {
			if (!NearBridgeEnd(ctx, HuntTiles)) {
				int lo = Math.Min(bridgeTo, bridgeTo - dir * HuntTiles), hi = Math.Max(bridgeTo, bridgeTo - dir * HuntTiles);
				return Do($"to the hellbridge's far end to hunt a Voodoo Demon (x {bridgeTo})", new WalkToSkill(new GoalStandOnRow(stand, lo, hi - 1, ctx.Physics)) { Quiet = true });
			}
			huntOn = true;
			return Do($"hunting a Voodoo Demon at the hellbridge's far end (x {bridgeTo}) for its Guide Voodoo Doll",
				new WaitSkill("a Guide Voodoo Doll", c => c.Inventory.Count(doll) > 0 || BossUp(c, b.Npc) || c.Drops.Any(d => d.Type == doll), NightWaitTicks));
		}
		if (!ctx.World.TownNpcAlive(ids.Guide))
			return ResolveTownNpc(new TownNpcAlive(ids.Guide, "the Guide"), ctx);
		if (ctx.WorldWidth > 0 && Math.Abs(bridgeTo - ctx.WorldWidth / 2) <= SummonCentreGap) {
			stuck = $"the hellbridge's far end x {bridgeTo} is within {SummonCentreGap} columns of the world's middle: the Wall could come from either side";
			return null;
		}
		if (!LavaBeyond(ctx, bridgeTo, dir, row)) {
			int was = bridgeTo;
			if (ctx.WorldWidth > 0 && (bridgeTo + dir * BridgeExtendTiles < BridgeEdgeRoom || bridgeTo + dir * BridgeExtendTiles > ctx.WorldWidth - BridgeEdgeRoom)) {
				stuck = $"no lava under where the doll would land past the hellbridge's end at x {bridgeTo}, and no room to go on toward the world's edge";
				return null;
			}
			bridgeTo += dir * BridgeExtendTiles;
			hellbridgeDone = false;
			ctx.Log.Info($"Auto: no lava under where the doll would land ({LandingFrom}-{LandingTo} columns past x {was}); the hellbridge goes on to {bridgeTo}");
			return ResolveHellbridge(ctx);
		}
		// At the end, or a column short of it (the doll lands 3-6 columns past the end from either, LandingFrom).
		if (ctx.Node.Y != stand || Math.Abs(ctx.Node.X - endStand.X) > 1)
			return Do($"to the hellbridge's far end at {endStand} to summon the {b.Label}", new WalkToSkill(new GoalStandAt(endStand, ctx.Physics)) { Quiet = true });
		if ((ctx.Combat.LifeFraction < 1f || ctx.Combat.PotionSickTicks > 0) && ctx.Tick - lastRestEnd > MaxRestTicks)
			return Do($"resting to full life before summoning the {b.Label} ({ctx.Combat.Life}/{ctx.Combat.LifeMax})",
				new WaitSkill("life", c => c.Combat.LifeFraction >= 1f && c.Combat.PotionSickTicks == 0, MaxRestTicks));
		thrownByPlan = true;
		return Do($"summoning the {b.Label}: a Guide Voodoo Doll into the lava off the hellbridge's end at x {bridgeTo}", WallOfFleshFight(b, ctx));
	}

	/// <summary>
	/// Lava the first thing under the bridge's row in every column the doll may land in (<see cref="LandingFrom"/>-<see cref="LandingTo"/>
	/// past the end): what a player sees glowing below. The doll lies on anything else (items rest on blocks, Item.cs:50828).
	/// </summary>
	public static bool LavaBeyond(BotContext ctx, int endX, int dir, int row)
	{
		for (int k = LandingFrom; k <= LandingTo; k++) {
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

	/// <summary>S8: the loot where the Wall died, its brick box opened by the walk (any pickaxe breaks demonite brick): picked up, then done.</summary>
	private ISkill ResolveBossLoot(BossLoot need, BotContext ctx)
	{
		if (lootDone || ctx.World == null || !ctx.World.BossDefeated(need.Npc))
			return null;
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
			else if (status == SkillStatus.Failed && shaft.FailureReason.StartsWith("no way down") && shaftX is int x) {
				shaftX = x + (bridgeDir != 0 ? bridgeDir : 1) * ShaftShiftTiles;
				ctx.Log.Info($"Auto: no ground on row {shaft.StandRow + 1} under the shaft at x {x}; the hellevator moves to x {shaftX}");
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
