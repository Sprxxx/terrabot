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
	/// The hellbridge's length (tiles): the spec's plan for an effective 40 a second on the eyes (section 5a: the Wall travels
	/// 62,175 / DPS tiles while it dies, 1,555 at 40) with room to spare, "a bridge of about 1600, built to 1700". The synthetic
	/// fight (S0, fight_wof) re-derives it: L = 1.25 x 62,175 / DPS, 1000-1750.
	/// </summary>
	public const int DefaultBridgeLength = 1700;
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
	/// <summary>The hellbridge: its row, first column and last (once the bot stood on its row).</summary>
	private int bridgeRow, bridgeFrom, bridgeTo;
	private bool bridgeKnown, hellbridgeDone, thrownByPlan, unplannedLogged, lootDone, huntOn;
	private (float X, float Y)? wofDiedAt;

	/// <summary>The Wall of Flesh's preparation (spec S1) was met: the plan got to the Guide, the shaft and the bridge.</summary>
	public bool WofPrepMet { get; private set; }
	/// <summary>The bot stood on the hellbridge's row in the Underworld (spec S3: "underworld reached").</summary>
	public bool UnderworldReachedOnce => bridgeKnown;
	public bool HellbridgeDone => hellbridgeDone;
	/// <summary>The Wall of Flesh's loot picked up where it died (spec S8).</summary>
	public bool WofLootCollected => lootDone;
	/// <summary>The hellbridge as planned: row, first and last column (null before the bot stood on its row).</summary>
	public (int Row, int From, int To)? Hellbridge => bridgeKnown ? (bridgeRow, bridgeFrom, bridgeTo) : null;
	/// <summary>The hellevator's column (null before one was chosen).</summary>
	public int? ShaftX => shaftX;

	/// <summary>How far the hellbridge is built (0-1), as the tiles show it now.</summary>
	public double HellbridgeProgress(BotContext ctx)
	{
		if (!bridgeKnown || bridgeTo == bridgeFrom)
			return 0;
		int end = HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo);
		return Math.Clamp((end - bridgeFrom) / (double)(bridgeTo - bridgeFrom), 0.0, 1.0);
	}

	private string HellbridgeText => bridgeKnown ? $"row {bridgeRow}, x {bridgeFrom}-{bridgeTo}" : "none";

	private void ResetWallOfFlesh()
	{
		WofPrepMet = false;
		shaftX = null;
		bridgeDir = 0;
		bridgeKnown = hellbridgeDone = thrownByPlan = unplannedLogged = lootDone = huntOn = false;
		wofDiedAt = null;
	}

	/// <summary>
	/// After the Eye: the Wall of Flesh (docs M12_SPEC.md section 9), in the spec's order.
	/// <list type="number">
	/// <item>S1: a bow, unholy arrows from the bag, 1000 arrows in all (refilled below 800), max life 200, 10 healing potions from the
	/// bag, and blocks for the bridge (<see cref="BridgeBlocks"/>);</item>
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
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.IronTierBow, ids.DemonBow), 1, "bow"));
		yield return new PrepFor(boss, new FromBag(new HaveItem(ids.UnholyArrow, WofArrows, "unholy arrows")));
		yield return new PrepFor(boss, new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), WofArrows, "arrows") { RefillBelow = WofArrows * 4 / 5 });
		yield return new PrepFor(boss, new LifeAtLeast(BossLife, "max life"));
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
			case BossDefeated b when b.SummonItem > 0:
				into.Add(b.SummonItem);
				break;
		}
	}

	/// <summary>The hunt for a Voodoo Demon is on: the bridge built, no doll carried, the bot on its last <see cref="HuntTiles"/>.</summary>
	private bool VoodooHuntOn(BotContext ctx) =>
		Plan == Plans.WallOfFlesh && hellbridgeDone && ctx.Inventory.Count(ctx.Ids.GuideVoodooDoll) <= 0 && NearBridgeEnd(ctx, HuntTiles);

	/// <summary>On the bridge's row, within <paramref name="tiles"/> of its far end.</summary>
	private bool NearBridgeEnd(BotContext ctx, int tiles)
	{
		if (!bridgeKnown)
			return false;
		TilePos n = ctx.Node;
		int front = bridgeDir > 0 ? n.X + 1 : n.X;
		return n.Y == bridgeRow - 1 && Math.Abs(bridgeTo - front) <= tiles && (bridgeTo - front) * bridgeDir >= 0;
	}

	/// <summary>The hellbridge's row in this world (<see cref="BridgeRowsUnderTop"/> under the Underworld's first row).</summary>
	private static int BridgeRowFor(BotContext ctx) => ctx.UnderworldRow + BridgeRowsUnderTop;

	/// <summary>
	/// The way the bridge goes: east if the world has room for it there (its end <see cref="BridgeEdgeRoom"/> from the edge), else
	/// west. Either way its far end is a bridge's length from the shaft, and the shaft near the spawn, the world's middle.
	/// </summary>
	private int BridgeDirection(BotContext ctx, int fromX)
	{
		if (bridgeDir != 0)
			return bridgeDir;
		bridgeDir = ctx.WorldWidth <= 0 || fromX + BridgeLength <= ctx.WorldWidth - BridgeEdgeRoom ? 1 : -1;
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
		return Do(bridgeKnown ? $"back down the hellevator at x {shaftX} to the hellbridge on row {row}" : $"down a hellevator at x {shaftX} to row {stand} in the Underworld",
			new HellevatorSkill(shaftX.Value, stand));
	}

	/// <summary>
	/// The bridge from where the bot stands on its row: its first column is the start of the solid run under it going the other way
	/// (a bridge built before, from a saved world, is found whole), its last a bridge's length the bridge's way, kept off the edge.
	/// </summary>
	private void KnowBridge(BotContext ctx, TilePos n, int row)
	{
		int dir = BridgeDirection(ctx, n.X);
		int from = dir > 0 ? n.X + 1 : n.X;
		for (int k = 0; k < BridgeLength && HellbridgeSkill.Built(ctx.Grid, row, from - dir); k++)
			from -= dir;
		int to = from + dir * BridgeLength;
		if (ctx.WorldWidth > 0)
			to = Math.Clamp(to, BridgeEdgeRoom, ctx.WorldWidth - BridgeEdgeRoom);
		bridgeKnown = true;
		bridgeRow = row;
		bridgeFrom = from;
		bridgeTo = to;
		ctx.Log.Info($"Auto: underworld reached at {n}: the hellbridge goes on row {row} from x {from} to {to} ({Math.Abs(to - from)} tiles {(dir > 0 ? "east" : "west")})");
	}

	/// <summary>S4: the hellbridge built to its far end (latched: the fight's loot box may take a cell out later).</summary>
	private ISkill ResolveHellbridge(BotContext ctx)
	{
		if (hellbridgeDone)
			return null;
		if (!bridgeKnown)
			return ResolveUnderworld(ctx);
		ctx.DepthErrand = true;
		int end = HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo);
		if (end == bridgeTo) {
			hellbridgeDone = true;
			ctx.Log.Info($"Auto: the hellbridge is built: row {bridgeRow}, x {bridgeFrom}-{bridgeTo}");
			return null;
		}
		return Do($"building the hellbridge on row {bridgeRow} from x {bridgeFrom} to {bridgeTo} (built to {end}, {HellbridgeProgress(ctx):P0})",
			new HellbridgeSkill(bridgeRow, bridgeFrom, bridgeTo));
	}

	/// <summary>The blocks the bridge takes: stone, dirt or the ash its digging gives (BuildingBlocks and the Ash Block).</summary>
	private static int[] BridgeBlockItems(GameIds ids) => Tiers(ids.BuildingBlocks.Concat(new[] { ids.AshBlock }).ToArray());

	/// <summary>S1: blocks for what is left of the bridge and <see cref="BridgeBlockMargin"/> (all of it before it is begun).</summary>
	private ISkill ResolveBridgeBlocks(BridgeBlocks need, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (hellbridgeDone)
			return null;
		int left = bridgeKnown ? Math.Abs(bridgeTo - HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo)) : BridgeLength;
		return Resolve(new HaveItem(BridgeBlockItems(ctx.Ids), left + BridgeBlockMargin, need.Label), claims, depth + 1, ctx);
	}

	/// <summary>The fight's arena: the bridge's columns and the feet row on it.</summary>
	private (int MinX, int MaxX, int StandY)? HellbridgeBounds() =>
		bridgeKnown ? (Math.Min(bridgeFrom, bridgeTo), Math.Max(bridgeFrom, bridgeTo), bridgeRow - 1) : null;

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
		if (skill is HellbridgeSkill && status == SkillStatus.Succeeded)
			hellbridgeDone = true;
		if (skill is BossFightSkill { IsWallOfFlesh: true } fight) {
			if (status == SkillStatus.Succeeded)
				wofDiedAt = fight.WallLastSeen;
			else
				thrownByPlan = huntOn = unplannedLogged = false; // the next Wall is a new summon (or an unplanned one)
		}
	}

	/// <summary>What the protected cells depend on: the bridge's row, first column and built end.</summary>
	private (int, int, int) HellbridgeKey(BotContext ctx) =>
		bridgeKnown ? (bridgeRow, bridgeFrom, HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo)) : (0, 0, 0);

	/// <summary>The hellbridge's built blocks: no walk, mine or shelter breaks them (the fight's only way, spec section 5).</summary>
	private void ProtectHellbridge(BotContext ctx, HashSet<TilePos> cells)
	{
		if (!bridgeKnown)
			return;
		int end = HellbridgeSkill.BridgeEnd(ctx.Grid, bridgeRow, bridgeFrom, bridgeTo), dir = bridgeTo >= bridgeFrom ? 1 : -1;
		for (int x = bridgeFrom; ; x += dir) {
			cells.Add(new TilePos(x, bridgeRow));
			if (x == end)
				break;
		}
	}
}
