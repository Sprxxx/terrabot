using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;

namespace TerraBot.Tests;

/// <summary>Footing next to a deep shaft, and the danger/fight distances for ignored enemies (world_eye run 13).</summary>
public class FootingTests
{
	private static ArrayTileGrid Shaft(int depth)
	{
		// Column 3 is a shaft: node (3,1) covers columns 3 and 4 and is held up by column 4 only.
		var rows = new System.Collections.Generic.List<string> { "........", "........" };
		for (int i = 0; i < depth; i++)
			rows.Add("###.####");
		rows.Add("########");
		return ArrayTileGrid.Parse(rows.ToArray());
	}

	[Fact]
	public void OneColumnOverADeepShaft_IsPrecarious()
	{
		var deep = Shaft(30);
		Assert.True(Body.IsPrecarious(deep, new TilePos(3, 1), 24));
		Assert.False(Body.IsPrecarious(deep, new TilePos(4, 1), 24)); // both columns on rock
		var shallow = Shaft(3);
		Assert.False(Body.IsPrecarious(shallow, new TilePos(3, 1), 24)); // a short hole: landing in it is harmless
	}

	[Fact]
	public void PrecariousFooting_CostsExtra()
	{
		var deep = Shaft(30);
		var moves = new MoveContext(deep, PhysicsParams.Default);
		Assert.Equal(10f + moves.PrecariousTicks, moves.Adjust(10f, new TilePos(3, 1)));
		Assert.Equal(10f, moves.Adjust(10f, new TilePos(5, 1)));
	}

	[Fact]
	public void IgnoredEnemy_AtTheFightEdge_DoesNotStartAFight()
	{
		// Seen in game: an ignored slime at 2.9-3.0 tiles started a fight that dropped it at 3.01, every 2 s, for an hour.
		var g = ArrayTileGrid.Parse("....................", "....................", "....................", "..........S.........", "####################");
		var ctx = new BotContext { Grid = g, Combat = new CombatInfo { WeaponReach = 30f } };
		ctx.Body = new SimWorld(g, PhysicsParams.Default, g.Markers['S']).State;
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		var slime = new EntityInfo(7, 1, "Test Slime", cx + 2.6f * 16f - 12f, cy - 12f, 24, 24, 0, 0, 30, 45, 10);
		ctx.Hostiles = new[] { slime };
		Assert.True(ProgressionPlanner.InDanger(ctx));
		ctx.Ignore(7, 1800);
		Assert.False(ProgressionPlanner.InDanger(ctx)); // 2.6 tiles: beyond the danger distance for ignored enemies
		Assert.True(ctx.Fightable(7, slime.DistanceTo(cx, cy))); // but an ongoing fight keeps it
	}

	[Fact]
	public void StepHeldUpOnlyByAMinedCell_IsRejected()
	{
		// world_eye run 15: a staircase dug up through rock planned a walk back onto a block an earlier step mined.
		var g = ArrayTileGrid.Parse("......", "......", "######");
		var to = new TilePos(1, 1);
		var none = new System.Collections.Generic.HashSet<TilePos>();
		Assert.False(PathSearch.SupportOnlyFromBroken(g, to, none));
		var one = new System.Collections.Generic.HashSet<TilePos> { new(1, 2) };
		Assert.False(PathSearch.SupportOnlyFromBroken(g, to, one)); // the other column still holds it
		var both = new System.Collections.Generic.HashSet<TilePos> { new(1, 2), new(2, 2) };
		Assert.True(PathSearch.SupportOnlyFromBroken(g, to, both));
		var air = ArrayTileGrid.Parse("......", "......", "......");
		Assert.False(PathSearch.SupportOnlyFromBroken(air, to, both)); // not held by the grid anyway (mid-jump)
	}

	[Fact]
	public void DeepestRow_FollowsTheArmour()
	{
		// world_eye run 15: exploring for iron in no armour went 200 tiles down; skeletons and bats killed the bot.
		var ctx = new BotContext { Combat = new CombatInfo() };
		Assert.Equal(int.MaxValue, ctx.DeepestRow); // depth unknown: no limit
		ctx.CavernY = 400;
		Assert.Equal(400 + BotContext.ShallowMargin, ctx.DeepestRow);
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage; // an iron-tier sword, still no armour (run 48)
		Assert.Equal(400 + BotContext.ArmedMargin, ctx.DeepestRow);
		ctx.Combat.Defense = BotContext.CavernDefense; // e.g. full iron armour
		Assert.Equal(400 + BotContext.CavernDepth, ctx.DeepestRow);
		ctx.Combat.Defense = BotContext.DeepDefense;
		Assert.Equal(int.MaxValue, ctx.DeepestRow);
	}

	[Fact]
	public void DiagonalDigDown_ClearsTheHeadCellInFront()
	{
		// world_eye run 17: a dig down to the right left the block at head height in front; the bot pressed right into
		// it for 46 ticks. The body steps sideways at its old height before it drops.
		var (g, b) = TestBlockActions.Parse(
			"@@@@@@@@",
			"@...#@@@",
			"@......@",
			"@.S....@",
			"@@@##@@@",
			"@@@##@@@",
			"@@@@@@@@");
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = false };
		var edges = new System.Collections.Generic.List<PathStep>();
		foreach (IMoveType m in ctx.Moves)
			m.Expand(ctx, g.Markers['S'], edges);
		PathStep down = Assert.Single(edges, e => e.Kind == MoveKind.Drop && e.To == new TilePos(3, 4));
		Assert.Contains(down.Actions, a => a.Cell == new TilePos(4, 1));
	}

	[Fact]
	public void SlopeInTheFeetRow_HoldsTheBodyUp()
	{
		// world_eye run 19: resting on a slope with air under both columns, every plan failed with "invalid start".
		var g = ArrayTileGrid.Parse(
			"........",
			"........",
			"...S/...",
			"........",
			"########");
		Assert.True(Body.IsStartable(g, g.Markers['S']));
		Assert.False(Body.IsStandable(g, g.Markers['S'])); // not a landing spot for other moves
		var air = ArrayTileGrid.Parse("........", "........", "...S....", "........", "########");
		Assert.False(Body.IsStartable(air, air.Markers['S']));
	}

	[Fact]
	public void PlanStartsFromASlopeOverAir()
	{
		var (g, b) = TestBlockActions.Parse(
			"............",
			"............",
			"...S/.......",
			"............",
			"..........G.",
			"############");
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default));
		Assert.Equal(PathStatus.Found, r.Status);
	}

	[Fact]
	public void DangerZone_AroundWhereTheBotWasKilled_ExpiresLater()
	{
		// Antlions in the underground desert killed the bot in nearly every run: stay away from there for a while.
		var ctx = new BotContext { Tick = 100 };
		ctx.NoteDeathByEnemy(new TilePos(2276, 380));
		Assert.True(ctx.InDangerZone(new TilePos(2290, 400)));
		Assert.False(ctx.InDangerZone(new TilePos(2276, 300)));
		ctx.Tick = 100 + BotContext.DangerZoneTicks + 1;
		Assert.False(ctx.InDangerZone(new TilePos(2290, 400)));
	}

	[Fact]
	public void JumpThroughASlope_IsNotPlanned()
	{
		// world_eye run 30: jumps whose path crossed a slope landed a tile short (slopes are solid from below).
		string[] rows = {
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..........############",
			"..........############",
			"..S.......############",
			"######################" };
		PathResult open = TestUtil.Plan(ArrayTileGrid.Parse(rows));
		PathStep jump = Assert.Single(open.Steps, st => st.Kind == MoveKind.Jump);
		// A slope on a cell the jump sweeps on the way up, outside the start and landing bodies.
		TilePos mid = jump.Template.BodyCells.Select(c => new TilePos(jump.From.X + c.X, jump.From.Y + c.Y))
			.First(c => c.Y < jump.From.Y - 2 && !(c.X >= jump.To.X && c.X <= jump.To.X + 1 && c.Y >= jump.To.Y - 2) && rows[c.Y][c.X] == '.');
		string[] blocked = (string[])rows.Clone();
		blocked[mid.Y] = blocked[mid.Y].Remove(mid.X, 1).Insert(mid.X, "/");
		PathResult r = TestUtil.Plan(ArrayTileGrid.Parse(blocked));
		Assert.DoesNotContain(r.Steps, st => st.Kind == MoveKind.Jump && st.From == jump.From && st.To == jump.To && st.Template == jump.Template);
	}

	[Fact]
	public void PartialPlanThroughRock_NeverStandsOnWhatItDigs()
	{
		// world_eye run 35: climbing out of a mine through solid rock, the search ran out of nodes; the partial plan's
		// end was past a step held up only by blocks earlier steps dig, so it failed there and re-planned (40 times).
		var rows = Enumerable.Repeat(new string('#', 70), 60).ToList();
		for (int y = 54; y <= 56; y++)
			rows[y] = new string('#', 60) + "......" + "####";
		rows[56] = new string('#', 61) + "S" + "....####";
		rows[59] = new string('@', 70);
		var (g, b) = TestBlockActions.Parse(rows.ToArray());
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = false };
		TilePos start = g.Markers['S'];
		var search = new PathSearch(ctx, start, new GoalNearTile(new TilePos(start.X, 3), 2, PhysicsParams.Default), new SearchLimits { MaxExpansions = 2000 });
		search.Step(1e9);
		Assert.Equal(PathStatus.Partial, search.Result.Status);
		Assert.True(search.Result.Steps.Count > 3, search.Result.ToString());
		// Replay: every step valid on the grid as the earlier steps left it.
		for (int i = 0; i < search.Result.Steps.Count; i++) {
			PathStep st = search.Result.Steps[i];
			Assert.True(ctx.IsStepValid(st), $"step {i} {st} invalid after the earlier steps' digging");
			foreach (BlockAction a in st.Actions ?? System.Array.Empty<BlockAction>())
				g.Set(a.Cell.X, a.Cell.Y, a.Kind == ActionKind.Break ? CellKind.Empty : CellKind.Solid);
		}
	}

	[Fact]
	public void BelowTheDepthLimit_EdgesCostMore_AndTheContextFollowsTheGear()
	{
		// world_eye run 36: home from the depth limit through the open caverns below it; crawdads killed the bot twice.
		var flat = ArrayTileGrid.Parse("........", "........", "........", "########");
		var moves = new MoveContext(flat, PhysicsParams.Default) { AvoidBelowRow = 1 };
		Assert.Equal(10f, moves.Adjust(10f, new TilePos(2, 1)));
		Assert.Equal(10f + moves.BelowRowTicks, moves.Adjust(10f, new TilePos(2, 2)));

		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(null);
		ctx.CavernY = 30;
		Assert.Equal(ctx.DeepestRow, ctx.Moves.AvoidBelowRow);
		ctx.Combat.Defense = BotContext.DeepDefense; // full gear: no limit
		Assert.Equal(int.MaxValue, ctx.Moves.AvoidBelowRow);
	}

	[Fact]
	public void ThornsAndSpikes_AreNotWalkedThrough_NorStoodOn()
	{
		// world_eye run 38: crimson thorns read as open space; the bot died in the same patch twice.
		Assert.False(CellKind.Hazard.IsPassable());
		Assert.False((CellKind.Solid | CellKind.Hazard).CanStandOn());
		Assert.Equal('^', CellKind.Hazard.ToChar());
		Assert.Equal(CellKind.Hazard, CellKindExtensions.FromChar('^'));
		// A corridor walled by thorns, no digging: no way through. Without the thorns there is.
		string[] rows = {
			"##########",
			"....^.....",
			"....^.....",
			"S...^.....",
			"##########",
		};
		var thorns = ArrayTileGrid.Parse(rows);
		var clear = ArrayTileGrid.Parse(rows.Select(r => r.Replace('^', '.')).ToArray());
		var goal = new GoalNearTile(new TilePos(8, 3), 0, PhysicsParams.Default);
		var blocked = new PathSearch(new MoveContext(thorns, PhysicsParams.Default) { AllowBreak = false, AllowPlace = false }, thorns.Markers['S'], goal);
		blocked.Step(1e9);
		var open = new PathSearch(new MoveContext(clear, PhysicsParams.Default) { AllowBreak = false, AllowPlace = false }, clear.Markers['S'], goal);
		open.Step(1e9);
		Assert.Equal(PathStatus.Found, open.Result.Status);
		Assert.NotEqual(PathStatus.Found, blocked.Result.Status);
	}

	[Fact]
	public void PathsThroughADeathSpot_CostMore()
	{
		// world_eye run 40: rolling cacti killed the bot three times at one spot its walks kept passing through.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(null);
		TilePos spot = new(40, 24);
		Assert.Equal(10f, ctx.Moves.Adjust(10f, spot));
		ctx.NoteDeathByEnemy(spot);
		Assert.Equal(10f + ctx.Moves.AvoidTicks, ctx.Moves.Adjust(10f, spot));
		Assert.Equal(10f, ctx.Moves.Adjust(10f, new TilePos(spot.X + BotContext.DangerZoneRadius + 1, 24)));
	}

	[Fact]
	public void TheUndergroundDesert_IsOffLimits_UntilTheGearIsGood()
	{
		// world_eye run 40: antlions and rolling cacti killed the bot 6 times in the underground desert.
		FakeGame g = ProgressionTests.Mine();
		const int Sand = 53;
		for (int x = 56; x < 90; x++)
			for (int y = 16; y < 40; y++)
				if (g.Grid.Get(x, y).IsSolid())
					g.TileTypes[new TilePos(x, y)] = Sand;
		BotContext ctx = g.CreateContext(null);
		ctx.Ids = new GameIds { DesertTiles = new[] { Sand } };
		ctx.SurfaceY = 20;
		Assert.True(ctx.InUndergroundDesert(new TilePos(70, 28)));
		Assert.False(ctx.InUndergroundDesert(new TilePos(10, 28))); // plain stone
		Assert.False(ctx.InUndergroundDesert(new TilePos(70, 18))); // above the surface line
		Assert.True(ctx.OffLimits(new TilePos(70, 28)));
		Assert.True(ctx.Moves.Adjust(10f, new TilePos(70, 28)) > 10f);
		Assert.True(ctx.BiomeGround(new TilePos(70, 24))); // a desert's surface: not somewhere to travel to (run 56)
		Assert.False(ctx.BiomeGround(new TilePos(10, 24)));
		ctx.Combat.Defense = BotContext.DeepDefense;
		Assert.False(ctx.OffLimits(new TilePos(70, 28)));
	}

	[Fact]
	public void ADesertSeenAfterItWasFirstCounted_IsDesertSoon()
	{
		// world_eye run 59: chunks counted by a path search before the bot got there kept "nothing seen" for a game minute,
		// and the bot explored on into the underground desert until antlion chargers killed it.
		FakeGame g = ProgressionTests.Mine();
		const int Sand = 53;
		for (int x = 56; x < 90; x++)
			for (int y = 16; y < 40; y++) {
				if (g.Grid.Get(x, y).IsSolid())
					g.TileTypes[new TilePos(x, y)] = Sand;
				g.Unrevealed.Add(new TilePos(x, y));
			}
		BotContext ctx = g.CreateContext(null);
		ctx.Ids = new GameIds { DesertTiles = new[] { Sand } };
		ctx.SurfaceY = 20;
		Assert.False(ctx.InUndergroundDesert(new TilePos(70, 28))); // not seen yet
		g.Unrevealed.Clear();
		ctx.Tick += BotContext.PartlySeenRecheckTicks + 1;
		Assert.True(ctx.InUndergroundDesert(new TilePos(70, 28)));
	}

	[Fact]
	public void MiningFromInsideADangerZone_GivesUpRatherThanExploring()
	{
		// world_eye run 59: after an antlion charger was seen, explore legs went on through the desert zone, every way off limits.
		FakeGame g = ProgressionTests.Mine(oreRows: 0);
		BotContext ctx = g.CreateContext(null);
		ctx.NoteDanger(ctx.Node, BotContext.BiomeZoneTicks, BotContext.BiomeZoneRadius);
		var mine = new Bot.Skills.MineSkill(FakeGame.IronOre, FakeGame.TileIron, 6);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, mine, 600));
		Assert.Contains("explorations", mine.FailureReason); // the planner travels on, as when exploring found nothing
	}

	[Fact]
	public void Exploring_StartsOnTheSideAwayFromADangerZone_AndTravelSkipsGroundAboveIt()
	{
		// world_eye run 60: every mining job explored right first, into the underground desert by the spawn, and a trip for
		// new ore ended on the surface just above it.
		var ctx = new BotContext { Tick = 100 };
		var at = new TilePos(1000, 300);
		Assert.Equal(1, ctx.SaferSide(at)); // nothing known either way: alternates
		Assert.Equal(-1, ctx.SaferSide(at));
		ctx.NoteDanger(new TilePos(1050, 360), BotContext.BiomeZoneTicks, BotContext.BiomeZoneRadius);
		Assert.Equal(-1, ctx.SaferSide(at));
		Assert.Equal(-1, ctx.SaferSide(at));
		Assert.True(ctx.OffLimitsBelow(new TilePos(1040, 280)));
		Assert.False(ctx.OffLimitsBelow(new TilePos(900, 280)));
		// Out on a trip with nothing known either way: onwards, away from the spawn (run 67: back across the desert).
		ctx.DangerZones.Clear();
		ctx.SpawnTile = new TilePos(640, 280);
		Assert.Equal(1, ctx.SaferSide(at));
		Assert.Equal(1, ctx.SaferSide(at));
	}

	[Fact]
	public void ACrowdOfSlimes_InWeakGear_MeansRetreatingSooner_AndAZoneToAvoid()
	{
		// world_eye runs 61 and 63: 7-8 slimes in one cave; at 50% life the bot was dead before it got away.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(null);
		TilePos at = ctx.Node;
		for (int i = 0; i < 5; i++)
			g.AddSlime(10 + i, at.X + 2 + i, at.Y);
		g.Refresh(ctx);
		ctx.SurfaceY = 0; // as if underground
		Assert.Equal(BotBrain.MobRetreatBelow, BotBrain.RetreatThreshold(ctx));
		BotBrain.NoteCrowd(ctx);
		Assert.True(ctx.InDangerZone(at));
		ctx.DangerZones.Clear();
		ctx.Combat.Defense = BotContext.CavernDefense; // armour: no backing off from slimes
		BotBrain.NoteCrowd(ctx);
		Assert.False(ctx.InDangerZone(at));
		ctx.Combat.Defense = 0;
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage; // a broadsword: no backing off either (run 72)
		BotBrain.NoteCrowd(ctx);
		Assert.False(ctx.InDangerZone(at));
	}

	[Fact]
	public void AHostileBelowTheDepthLimit_DoesNotStartAFight_ButStillCountsForRetreating()
	{
		// world_eye run 66: at row 461 every walk up stopped for a fight that ended at once (nothing it would fight there).
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(null);
		TilePos at = ctx.Node;
		g.AddSlime(7, at.X + 5, at.Y);
		g.Refresh(ctx);
		Assert.True(ProgressionPlanner.InDanger(ctx, toFight: true));
		ctx.CavernY = at.Y - 20 - BotContext.ShallowMargin; // the depth limit 20 rows above the bot and the slime
		Assert.False(ProgressionPlanner.InDanger(ctx, toFight: true));
		Assert.True(ProgressionPlanner.InDanger(ctx));
	}

	[Fact]
	public void HitHard_TheBotRetreatsWhileAFewMoreSuchHitsCanBeTaken()
	{
		// world_eye run 68: zombies hit for 20 with no armour; at 30% the bot was two hits from death.
		var ctx = new BotContext { Tick = 1000 };
		ctx.Combat.LifeMax = 100;
		Assert.Equal(BotBrain.RetreatBelow, BotBrain.RetreatThreshold(ctx));
		ctx.NoteHit(20);
		Assert.Equal(0.5f, BotBrain.RetreatThreshold(ctx), 3);
		ctx.Tick += BotContext.HitMemoryTicks + 1;
		Assert.Equal(BotBrain.RetreatBelow, BotBrain.RetreatThreshold(ctx));
	}

	[Fact]
	public void HeadInWater_ReadsFromTheHitbox_NotTheNode()
	{
		// world_eye run 59: breath ran from 95 to 18 while the node's head cells read empty: the bot stood a few pixels low and
		// vanilla's head box (Collision.DrownCollision) reached the water in the row below them.
		var g = ArrayTileGrid.Parse(
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"..........",
			"~~~~~~~~~~",
			"~~~~~~~~~~",
			"##########");
		PhysicsParams p = PhysicsParams.Default;
		float left = 5 * 16 + 6, top = 11 * 16 - p.Height; // standing on row 11 at node (5,10): head in row 8
		Assert.False(Bot.Skills.SurfaceSkill.HeadUnder(g, left, top, p));
		Assert.True(Bot.Skills.SurfaceSkill.HeadUnder(g, left, top + 4, p));
	}

	[Fact]
	public void RelaxedSearchThroughRock_StillGivesAPartialPlan()
	{
		// world_eye run 46: the relaxed retry (steps onto what the path digs allowed) said "no path" after 40,000 nodes: every
		// node that got closer was past such a step, and a partial plan never ended there.
		var rows = Enumerable.Repeat(new string('#', 70), 60).ToList();
		for (int y = 54; y <= 56; y++)
			rows[y] = new string('#', 60) + "......" + "####";
		rows[56] = new string('#', 61) + "S" + "....####";
		rows[59] = new string('@', 70);
		var (g, b) = TestBlockActions.Parse(rows.ToArray());
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = false };
		TilePos start = g.Markers['S'];
		var search = new PathSearch(ctx, start, new GoalNearTile(new TilePos(start.X, 3), 2, PhysicsParams.Default),
			new SearchLimits { MaxExpansions = 2000, CheckBrokenSupport = false });
		search.Step(1e9);
		Assert.Equal(PathStatus.Partial, search.Result.Status);
		Assert.True(search.Result.Steps.Count > 0);
	}

	[Fact]
	public void TheEvilBiome_IsOffLimits_UntilThereIsArmour()
	{
		// world_eye run 47: an eater of souls killed the bot in the corruption in copper gear.
		FakeGame g = ProgressionTests.Mine();
		const int Ebonstone = 25;
		for (int x = 56; x < 90; x++)
			for (int y = 16; y < 40; y++)
				if (g.Grid.Get(x, y).IsSolid())
					g.TileTypes[new TilePos(x, y)] = Ebonstone;
		BotContext ctx = g.CreateContext(null);
		ctx.Ids = new GameIds { EvilTiles = new[] { Ebonstone } };
		Assert.True(ctx.InEvilBiome(new TilePos(70, 26)));
		Assert.False(ctx.InEvilBiome(new TilePos(10, 26)));
		Assert.True(ctx.OffLimits(new TilePos(70, 26)));
		ctx.Combat.Defense = BotContext.CavernDefense; // iron armour
		ctx.PickPower = 40; // iron pickaxe: ebonstone wants 65 (measured run S4: trapped in crimstone pockets)
		Assert.True(ctx.OffLimits(new TilePos(70, 26)));
		ctx.PickPower = BotContext.EvilStonePickPower;
		Assert.False(ctx.OffLimits(new TilePos(70, 26)));
	}

	[Fact]
	public void AnEvilBiomeMonsterInSight_MarksADangerZone_InWeakGear()
	{
		// Runs 47 and 49: eaters of souls and devourers killed the bot three times on the corruption's edge.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(null);
		const int ebonstone = 25;
		ctx.Ids = new GameIds { EvilEnemies = new[] { 6 }, EvilTiles = new[] { ebonstone } };
		var eater = new EntityInfo(3, 6, "Eater of Souls", 40 * 16f, 20 * 16f, 30, 30, 0, 0, 30, 30, 15);
		ctx.Hostiles = new[] { eater };
		// Measured run S15: over clean ground (eaters fly far from the biome) it marks nothing: three over the house had
		// made every mining job from the spawn fail "off limits" for days.
		BotBrain.NoteEvilEnemies(ctx);
		Assert.False(ctx.InDangerZone(eater.FeetTile));
		for (int x = 30; x < 50; x++) // the biome's ground there
			for (int y = 25; y < 35; y++)
				g.TileTypes[new TilePos(x, y)] = ebonstone;
		ctx.Tick += BotContext.DesertRecheckTicks + 1; // chunk counts are kept a while
		BotBrain.NoteEvilEnemies(ctx);
		Assert.True(ctx.InDangerZone(eater.FeetTile));
		ctx.DangerZones.Clear();
		ctx.Combat.Defense = BotContext.CavernDefense; // armoured: no need
		BotBrain.NoteEvilEnemies(ctx);
		Assert.False(ctx.InDangerZone(eater.FeetTile));
	}

	[Fact]
	public void AStepWithASlopeOnTop_IsNotAStepUp()
	{
		// world_eye run 51: a block with a slope on it is more than vanilla's one-tile step: pressing right, three times.
		bool HasStepUp(params string[] rows)
		{
			var g = ArrayTileGrid.Parse(rows);
			var es = new System.Collections.Generic.List<PathStep>();
			new Core.Pathfinding.Moves.StepUpMove().Expand(new MoveContext(g, PhysicsParams.Default), g.Markers['S'], es);
			return es.Any(e => e.To.X > e.From.X);
		}
		Assert.True(HasStepUp("........", "........", "........", ".S.#....", "########"));
		Assert.False(HasStepUp("........", "........", ".../....", ".S.#....", "########"));
	}

	[Fact]
	public void DigAnyway_BreaksUnderSand_ButNeverBesideLava()
	{
		// world_eye run 52: shut in by sand in the underground desert, the bot switched itself off after five rests.
		var (g, b) = TestBlockActions.Parse(
			"..%...!.",
			"..#...#.",
			"..S.....",
			"########");
		var normal = new MoveContext(g, PhysicsParams.Default) { Blocks = b };
		var anyway = new MoveContext(g, PhysicsParams.Default) { Blocks = b, DigAnyway = true };
		var none = System.Array.Empty<TilePos>();
		Assert.True(float.IsPositiveInfinity(BlockRules.BreakCost(normal, new TilePos(2, 1), none))); // sand on top
		Assert.False(float.IsPositiveInfinity(BlockRules.BreakCost(anyway, new TilePos(2, 1), none)));
		Assert.True(float.IsPositiveInfinity(BlockRules.BreakCost(anyway, new TilePos(6, 1), none))); // lava on top
	}

	[Fact]
	public void InsideABlock_TheWalkMinesOutOfItFirst()
	{
		// Seen in game (measured runs K1 and L2): a pillar step's block ended up in the bot's right feet column as it came
		// down; every plan began on top of that block and step 0 timed out 40-odd times at one spot.
		FakeGame game = EarlyGameTests.Field(40);
		game.Grid.Set(4, 24, CellKind.Solid);
		BotContext ctx = game.CreateContext(null);
		game.Refresh(ctx);
		Assert.Equal(new TilePos(4, 24), WalkToSkill.EmbeddedIn(ctx));
		var walk = new WalkToSkill(new TilePos(20, 24));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(game, ctx, walk, 60 * 20));
		Assert.False(game.Grid.Get(4, 24).IsSolid());
	}

	[Fact]
	public void KilledByTheSpawn_LeavesNoDangerZoneThere_ButDeepBelowItDoes()
	{
		// Measured run P1: two zombies killed the bot by the house at night; the spawn fell inside the death's zone, every job
		// from the respawn failed "off limits" at once, and auto mode stopped.
		FakeGame game = EarlyGameTests.Field(40);
		BotContext ctx = game.CreateContext(null);
		ctx.SpawnTile = new TilePos(100, 280);
		ctx.NoteDeathByEnemy(new TilePos(94, 288));
		Assert.False(ctx.OffLimits(new TilePos(100, 280)));
		ctx.NoteDeathByEnemy(new TilePos(100, 380)); // deep in the caves below it: stay away from there for now
		Assert.True(ctx.OffLimits(new TilePos(100, 380)));
	}

	[Fact]
	public void OnAHalfBlockInTheFeetRow_TheBodyIsAtThatNode_ARowUp()
	{
		// Measured run R1: walks and drops onto a half block in the feet row reached the column a row "too high" (the body
		// rides on top of it, 8 px up) and timed out there, 54 times.
		var g = ArrayTileGrid.Parse(
			"........",
			"........",
			"........",
			"....../.",
			"########");
		var planned = new TilePos(5, 3); // feet row 3 holds the slope at column 6
		Assert.True(Core.Execution.PathExecutor.SameNode(g, new TilePos(5, 2), planned));
		Assert.True(Core.Execution.PathExecutor.SameNode(g, planned, planned));
		Assert.False(Core.Execution.PathExecutor.SameNode(g, new TilePos(2, 2), new TilePos(2, 3))); // flat: a row up is not there
	}

	[Fact]
	public void AHalfBlockInTheFeetRow_IsNoPlaceToStand_TheBodyIsOnTopOfIt()
	{
		// Measured run S1: plans stood the body "in" a half block and dug down from there while it stood on top, 71 drops
		// waiting at the edge. The body on a half block is a row up; a walk steps up onto it.
		var g = ArrayTileGrid.Parse(
			"........",
			"........",
			"........",
			"....--..",
			"########");
		Assert.False(Body.IsStandable(g, new TilePos(4, 3))); // feet in the half blocks
		Assert.True(Body.IsStandable(g, new TilePos(4, 2)));  // on top of them
		var ctx = new MoveContext(g, PhysicsParams.Default);
		var edges = new System.Collections.Generic.List<PathStep>();
		new Core.Pathfinding.Moves.StepUpMove().Expand(ctx, new TilePos(2, 3), edges);
		Assert.Contains(edges, e => e.To == new TilePos(3, 2));
	}

	[Fact]
	public void HalfOverAFloodedHole_NoJumpIsPlanned_AWalkOffIs()
	{
		// Measured run S3: 220 jumps and pillars planned from spots half over a flooded hole, where the body bobs (vy -0.2)
		// and vanilla starts no jump; they waited to take off until they timed out.
		var g = ArrayTileGrid.Parse(
			"..........",
			"..........",
			"..........",
			"~~~~~~~~~~",
			"###~######");
		var bob = new TilePos(2, 3);
		Assert.True(Body.Bobbing(g, bob));
		var ctx = new MoveContext(g, PhysicsParams.Default);
		var edges = new System.Collections.Generic.List<PathStep>();
		foreach (IMoveType m in ctx.Moves)
			m.Expand(ctx, bob, edges);
		Assert.DoesNotContain(edges, e => e.Kind is MoveKind.Jump or MoveKind.Pillar);
		Assert.Contains(edges, e => e.Kind == MoveKind.Walk);
	}
}
