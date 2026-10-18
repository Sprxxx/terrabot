using System.Linq;
using TerraBot.Bot.Building;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 7 preparation in <see cref="FakeGame"/>: life crystals.</summary>
public class BossPrepTests
{
	private readonly ITestOutputHelper output;
	public BossPrepTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void MineSkill_BreaksLifeCrystals_InStonePockets()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(40, 29);
		g.AddHeart(52, 31);
		BotContext ctx = g.CreateContext(output);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 2), 60 * 120);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, crystals {g.Count(FakeGame.LifeCrystal)}, out of range {g.Sim.OutOfRange}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(2, g.Count(FakeGame.LifeCrystal));
	}

	[Fact]
	public void ALifeCrystalBesideWater_IsChosen_AndMiningTakesItOn()
	{
		// User report (2026-10-14): the bot ran past a life crystal. The planner chose "mine the life crystal" for one beside
		// water, and mining skipped it (no ore beside liquid), explored away, and was sent back, round and round.
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(16, 27);
		g.Grid.Fill(18, 27, 19, 28, CellKind.Water); // a pool beside it
		BotContext ctx = g.CreateContext(output);
		TilePos heart = Assert.IsType<TilePos>(ProgressionPlanner.SeenLifeCrystal(ctx));
		Assert.True(MineSkill.WouldMine(ctx, ctx.Moves, heart));
		SkillStatus st = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1), 60 * 120);
		output.WriteLine($"{st}, crystals {g.Count(FakeGame.LifeCrystal)}");
		Assert.Equal(SkillStatus.Succeeded, st);
	}

	[Fact]
	public void ALifeCrystalMiningGaveUpOn_IsNotSeenAgain_UntilTheFailuresFade()
	{
		// Audit of the 10-day world_eye runs (2026-10-16): a crystal job gave up on its crystal, and at once the crystal was
		// "seen" again, the brain replanned for it and a fresh job went for the same crystal first.
		FakeGame g = ProgressionTests.Mine();
		g.AddHeart(8, 23); // on the grass beside the bot
		BotContext ctx = g.CreateContext(output);
		Assert.NotNull(ProgressionPlanner.SeenLifeCrystal(ctx));
		ProgressionTests.MineUntilGivenUp(g, ctx, new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1), 2);
		Assert.Null(ProgressionPlanner.SeenLifeCrystal(ctx));
		// Facts that fade, not a ban: two game hours on it is gone for again.
		ctx.Tick += BotContext.StepFailureMemoryTicks;
		Assert.NotNull(ProgressionPlanner.SeenLifeCrystal(ctx));
	}

	[Fact]
	public void UseItem_ConsumesALifeCrystal()
	{
		FakeGame g = EarlyGameTests.Field(30);
		g.Items[FakeGame.LifeCrystal] = 2;
		BotContext ctx = g.CreateContext(output);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new UseItemSkill(FakeGame.LifeCrystal), 200));
		Assert.Equal(1, g.Count(FakeGame.LifeCrystal));
		Assert.Equal(120, g.LifeMax);
	}

	[Fact]
	public void Planner_UsesCarriedCrystals_BeforeMiningMore()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.LifeCrystal] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		ISkill next = Resolve(p, ctx, new LifeAtLeast(200, "max life"));
		Assert.IsType<UseItemSkill>(next);

		g.Items[FakeGame.LifeCrystal] = 0;
		var mine = Assert.IsType<MineSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Equal(FakeGame.TileHeart, mine.TileType);
	}

	/// <summary>
	/// A 260-wide field with a crystal on the grass, seen once from beside it; the bot then 230 columns off, beyond mining's
	/// 200-tile map lookup and the planner's 25.
	/// </summary>
	private (FakeGame, BotContext) CrystalSeenThenFarAway(TilePos heart)
	{
		FakeGame g = ProgressionTests.Mine(width: 260);
		g.AddHeart(heart.X, heart.Y);
		BotContext ctx = g.CreateContext(output);
		Assert.NotNull(ProgressionPlanner.SeenLifeCrystal(ctx)); // seen, as the bot passes by
		g.Sim.State.X += 230 * 16;
		g.Refresh(ctx);
		Assert.True(heart.ChebyshevTo(ctx.Node) > MineSkill.MapSearchRadius);
		return (g, ctx);
	}

	[Fact]
	public void ACrystalSeenBefore_BeyondTheMapLookup_IsGoneBackFor_NotExploredPast()
	{
		// Measured run U9: a crystal found at (2013,607) at d9 +11.0h; skeletons killed the bot 26 tiles from it and from the
		// respawn, 325 rows up, it was beyond the 200-tile map lookup. It came back into range at d10 +11.5h ("some at
		// (2012,607) on the map (138 tiles away)"): 23 game hours and 2 more deaths.
		var (g, ctx) = CrystalSeenThenFarAway(new TilePos(8, 23));
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			mine.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(mine.Describe());
		Assert.DoesNotContain("exploring", mine.Describe());
		Assert.Contains("walking to (", mine.Describe());
	}

	[Fact]
	public void ACrystalSeenBefore_FarAway_IsAMiningJob_NotATripToNewGround()
	{
		// Measured run U9: with the crystal at (2013,607) out of mind, the fruitless crystal jobs sent the bot back to the spawn
		// "to look from there". The planner knows of the crystal however far it is.
		var (_, ctx) = CrystalSeenThenFarAway(new TilePos(8, 23));
		ctx.SpawnTile = new TilePos(3, 24); // where the bot started, beside the crystal
		var p = new ProgressionPlanner();
		p.NoteFruitless(FakeGame.LifeCrystal); // the crystal search round here came to nothing (and nowhere to travel here)
		var mine = Assert.IsType<MineSkill>(Resolve(p, ctx, new LifeAtLeast(200, "max life")));
		Assert.Equal(FakeGame.TileHeart, mine.TileType);
	}

	[Fact]
	public void ARememberedCrystal_ThatIsGone_IsForgotten()
	{
		// Mined (or gone) since: not gone back for.
		var (g, ctx) = CrystalSeenThenFarAway(new TilePos(8, 23));
		foreach (TilePos c in g.Furniture.Where(kv => kv.Value == FakeGame.TileHeart).Select(kv => kv.Key).ToList())
			g.Furniture.Remove(c);
		var mine = new MineSkill(FakeGame.LifeCrystal, FakeGame.TileHeart, 1);
		mine.Start(ctx);
		g.Refresh(ctx);
		mine.Tick(ctx);
		output.WriteLine(mine.Describe());
		Assert.Contains("exploring", mine.Describe());
	}

	private ISkill Resolve(ProgressionPlanner p, BotContext ctx, Need need)
	{
		ISkill s = p.ResolveForTest(need, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}
}

/// <summary>Milestone 7: the boss stage of the planner (summon item, altar, night).</summary>
public class BossPlannerTests
{
	private readonly ITestOutputHelper output;
	public BossPlannerTests(ITestOutputHelper output) => this.output = output;

	private static readonly BossDefeated Eye = new(FakeGame.EyeNpc, FakeGame.SuspiciousEye, new[] { FakeGame.ServantNpc }, "Eye of Cthulhu");

	private ISkill Next(FakeGame g, bool night, out ProgressionPlanner p)
	{
		g.IsNight = night;
		BotContext ctx = g.CreateContext(output);
		p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{(night ? "night" : "day")}: {p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void Lenses_WaitForNight_ThenHuntDemonEyes_WithoutSheltering()
	{
		FakeGame day = ProgressionTests.Mine();
		day.AddAltar(30, 23);
		var wait = Assert.IsType<WaitSkill>(Next(day, night: false, out _));
		Assert.Equal("night", wait.What);
		FakeGame night = ProgressionTests.Mine();
		night.AddAltar(30, 23);
		var hunt = Assert.IsType<HuntSkill>(Next(night, night: true, out _));
		Assert.Equal(FakeGame.DemonEyeAi, hunt.AiStyle);
		Assert.Equal(6, hunt.Amount);
	}

	[Fact]
	public void ArmouredNights_GoToTheLenses_WhateverStageThePlanIsAt()
	{
		// Measured run S13: all else ready by day 8, then 34 heartbeats waiting for night and 28 hunting, the lenses still
		// short at day 10, while the armoured nights before went to mining.
		FakeGame g = ProgressionTests.Mine();
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 25;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		Assert.False(p.NightHuntDue(ctx)); // not settled yet: no house to come back to
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		ctx.SurfaceY = 25;
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		ISkill night = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {night?.Describe()}");
		var hunt = Assert.IsType<HuntSkill>(night);
		Assert.Equal(FakeGame.DemonEyeAi, hunt.AiStyle);
		Assert.Equal(6, hunt.Amount);
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense - 1; // not armoured: not out at night for them
		Assert.False(p.NightHuntDue(ctx));
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense;
		g.Items[FakeGame.SuspiciousEye] = 1; // the summon in hand: no more lenses
		Assert.False(p.NightHuntDue(ctx));
	}

	[Fact]
	public void AHuntInTown_WalksOutOfIt_OneWay_NotBackAndForth()
	{
		// Measured runs U2/U3: night hunts by the house (the Guide's: vanilla spawns nothing hostile there) failed "no NPC with
		// AI style 2 found after 12 wanders", wandering 30 tiles out and back.
		FakeGame g = EarlyGameTests.Field(400);
		g.IsNight = true;
		var lines = new System.Collections.Generic.List<string>();
		BotContext ctx = g.CreateContext(output);
		var hunt = new HuntSkill(FakeGame.Lens, FakeGame.DemonEyeAi, 6);
		hunt.Start(ctx);
		int startX = ctx.Node.X;
		var xs = new System.Collections.Generic.List<int>();
		for (int t = 0; t < 60 * 60 * 2 && xs.Count < 4; t++) {
			g.Refresh(ctx);
			ctx.Combat.InTown = System.Math.Abs(ctx.Node.X - startX) < 100; // the town's range round the house
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			if (hunt.State == "wandering" && (xs.Count == 0 || xs[^1] != ctx.Node.X / 25))
				xs.Add(ctx.Node.X / 25);
			g.Step(ctx);
		}
		output.WriteLine($"{hunt.Describe()}; went from {startX} to {ctx.Node.X}");
		Assert.True(System.Math.Abs(ctx.Node.X - startX) >= 60, $"stayed within {System.Math.Abs(ctx.Node.X - startX)} tiles of the house");
	}

	[Fact]
	public void AHunt_WithNothingInSight_SweepsFurtherOutEachTime_BothWays()
	{
		// Measured runs U2/U3: the wanders went 30 tiles one way from where the bot stood, then 30 back, between the same two
		// places: no new ground, and none of its spawns, was ever reached.
		FakeGame g = EarlyGameTests.FieldFrom(500, 250);
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var hunt = new HuntSkill(FakeGame.Lens, FakeGame.DemonEyeAi, 6);
		hunt.Start(ctx);
		int startX = ctx.Node.X, east = 0, west = 0;
		int far = HuntSkill.OpenWanderSteps * HuntSkill.WanderTiles - 6; // the sweep's reach on each side, within the walk's tolerance (4) and a column of rounding
		for (int t = 0; t < 60 * 60 * 3 && (east < far || west < far); t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
			east = System.Math.Max(east, ctx.Node.X - startX);
			west = System.Math.Max(west, startX - ctx.Node.X);
		}
		output.WriteLine($"{hunt.Describe()}; reached {east} east and {west} west of {startX}");
		Assert.True(east >= far && west >= far, $"reached {east} east and {west} west");
		// and no farther than that: vanilla spawns round the player wherever it stands (measured run U20: 400-600 tiles out)
		int limit = HuntSkill.OpenWanderSteps * HuntSkill.WanderTiles + 8;
		Assert.True(east <= limit && west <= limit, $"wandered {east} east and {west} west, past {limit}");
	}

	[Fact]
	public void ALensHunt_DownAShaft_GoesUpAboveTheSurfaceRow_WhereDemonEyesSpawn()
	{
		// Vanilla NPC.SpawnNPC: demon eyes spawn only above Main.worldSurface. Measured run U3: a lens hunt wandered at row 345,
		// below it, for 13 heartbeats.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 45; y++)
			rows.Add(new string('.', 300));
		for (int y = 45; y < 100; y++) {
			char[] r = new string(y >= 96 ? '@' : '#', 300).ToCharArray();
			if (y <= 74)
				for (int x = 141; x <= 146; x++)
					r[x] = y == 74 && x == 143 ? 'S' : '.';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { IsNight = true };
		BotContext ctx = g.CreateContext(output);
		var hunt = new HuntSkill(FakeGame.Lens, FakeGame.DemonEyeAi, 6) { SpawnsAboveRow = 50 };
		hunt.Start(ctx);
		int deepestWander = 0, wanders = 0;
		for (int t = 0; t < 60 * 60 * 2; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
			var m = System.Text.RegularExpressions.Regex.Match(hunt.Describe(), @"\[wandering\] > WalkTo \((\d+),(\d+)\)");
			if (m.Success) {
				wanders++;
				deepestWander = System.Math.Max(deepestWander, int.Parse(m.Groups[2].Value));
			}
		}
		output.WriteLine($"{hunt.Describe()}; wander goals down to row {deepestWander}");
		Assert.True(wanders > 0);
		Assert.True(deepestWander <= 50, $"wandered down to row {deepestWander}");
	}

	[Fact]
	public void AHuntsWander_FromHighGround_GoesToTheGroundBelow_NotABridgeIntoTheSky()
	{
		// Measured run U29 (night of d3): the lens hunt, from (1984,233) on a corruption hill, wandered to (2014,223), (2039,217),
		// (2064,211), (2089,205), (2114,199), (2139,193): each goal 10 rows over where the last walk ended and more than 40
		// rows over the ground, so the walk stood at the goal itself (GoalResolver.StandNear scans 40 rows) and placed 31
		// blocks a wander, a bridge rising into the sky to row 193. The bridge then was "the surface" (ProgressionPlanner.
		// SurfaceAt): trips "up to the surface before travelling at row 205-221" climbed to it from the spawn (row 282), the
		// crystal trips went to new ground on it at (2335,193) and (2215,196), and the bot was knocked off it at row 198 and
		// killed by a 79-row fall.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 120; y++) {
			char[] r = new string(y >= 100 ? '#' : '.', 300).ToCharArray();
			if (y >= 40 && y < 100)
				for (int x = 148; x <= 152; x++)
					r[x] = '#'; // a column of rock, the bot on top of it, 60 rows over the ground
			if (y == 39)
				r[150] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray()) { IsNight = true };
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 110;
		var hunt = new HuntSkill(FakeGame.Lens, FakeGame.DemonEyeAi, 6);
		hunt.Start(ctx);
		var log = (TestLog)ctx.Log;
		for (int t = 0; t < 600 && !log.Lines.Any(l => l.Contains("wandering towards")); t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			if (hunt.Tick(ctx) != SkillStatus.Running)
				break;
			g.Step(ctx);
		}
		string wander = log.Lines.First(l => l.Contains("wandering towards"));
		output.WriteLine(wander);
		var m = System.Text.RegularExpressions.Regex.Match(wander, @"towards \((\d+),(\d+)\)");
		Assert.Equal(99, int.Parse(m.Groups[2].Value));
	}

	[Fact]
	public void TheBowComesBeforeTheCrystals_SoTheNightHuntsCanShootDemonEyes()
	{
		// Measured run U5: the bow came at d9, after max life 200; the night hunts took 46 hits from demon eyes, most of them
		// last seen at full life, and got 1 lens of 6.
		var stages = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids).ToList();
		int bow = stages.FindIndex(n => n is PrepFor { Inner: HaveItem h } && h.Accepts.Contains(FakeGame.IronBow));
		int life = stages.FindIndex(n => n is PrepFor { Inner: LifeAtLeast });
		Assert.True(bow >= 0 && life >= 0);
		Assert.True(bow < life, $"bow at {bow}, crystals at {life}");
	}

	[Fact]
	public void SummonItem_IsMadeAtAFoundAltar()
	{
		FakeGame g = ProgressionTests.Mine();
		g.AddAltar(30, 23);
		g.Items[FakeGame.Lens] = 6;
		ISkill s = Next(g, night: true, out ProgressionPlanner p);
		Assert.True(s is WalkToSkill || s is CraftSkill { ItemType: FakeGame.SuspiciousEye }, p.Status);
		Assert.True(p.KnownStations.ContainsKey(FakeGame.TileDemonAltar));
	}

	[Fact]
	public void TheLensesInTheBag_AndAnAltarInSight_TheEyeIsMadeThere_BeforeTheStagesBeforeIt()
	{
		// Measured run U30: from t496800 to t516000, 7 lenses in the bag and max life 200, the bot climbed past the DemonAltar at
		// (1748,411), 14 tiles off at (1734,398), "up to the surface before building the arena"; the walks back to that altar
		// from home began at t534000, and the Suspicious Looking Eye was made at t722520.
		FakeGame g = ProgressionTests.Mine();
		g.AddAltar(18, 23); // on the grass, 15 tiles from the bot
		g.Items[FakeGame.Lens] = 7;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		ISkill s = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.True(s is WalkToSkill || s is CraftSkill { ItemType: FakeGame.SuspiciousEye }, p.Status);
		Assert.Contains("item43", p.Status); // the Suspicious Looking Eye (the fake names items by id)
		// Not without the lenses: then the plan's own order (nothing to make there yet).
		g.Items[FakeGame.Lens] = 5;
		var q = new ProgressionPlanner();
		q.Reset(ProgressionPlanner.Plans.Boss);
		ISkill t = q.Next(ctx);
		output.WriteLine($"{q.Status} -> {t?.Describe()}");
		Assert.DoesNotContain("item43", q.Status);
	}

	/// <summary>
	/// Journey run J1 (The Aggressive Site, 6400 wide, spawn at x 3198): 354 explorations for a DemonAltar over x 3050-3320 and
	/// trips to x 3078 and 3318, all in x 2880-3520, the middle tenth WorldGen's "Altars" pass never puts one in. Standing there,
	/// the search travels out first, to ground generation does put altars in; with no such rule known, it explores as before.
	/// </summary>
	[Fact]
	public void InTheColumnsNoAltarIsGeneratedIn_TheSearchTravelsOutFirst()
	{
		FakeGame g = ProgressionTests.Mine(width: 600, sky: 60);
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		g.AltarArea = new GenArea(0, 599, 0, 150, 0, 80); // the bot at x 3, in the skipped columns
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.IsType<WalkToSkill>(s);
		var log = ((TestLog)ctx.Log).Lines;
		Assert.Contains(log, l => l.Contains("no tile26 is generated in columns 0-150"));
		string trip = p.Status;
		int x = int.Parse(System.Text.RegularExpressions.Regex.Match(trip, @"new ground at \((\d+),").Groups[1].Value);
		Assert.True(x > 150, trip);

		// No rule known: explored for from where it stands, as before.
		FakeGame h = ProgressionTests.Mine(width: 600, sky: 60);
		h.Items[FakeGame.Lens] = 6;
		h.IsNight = true;
		BotContext hc = h.CreateContext(output);
		h.Refresh(hc);
		Assert.IsType<ExploreSkill>(new ProgressionPlanner().ResolveForTest(Eye, hc));
	}

	/// <summary>
	/// Journey run J3 (Journey Test): by day, 2 of the 6 lenses, no altar seen: the Eye's stage waited for the night (its lenses
	/// first), and the altar was not looked for until the sixth lens; the run ended "exploring for a DemonAltar". By day the
	/// summon's station is the work: the altar looked for.
	/// </summary>
	[Fact]
	public void ByDay_LensesShort_TheAltarIsLookedFor_NotTheNightWaitedFor()
	{
		FakeGame g = ProgressionTests.Mine(width: 200, sky: 60);
		g.Items[FakeGame.Lens] = 2;
		g.IsNight = false;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.IsType<ExploreSkill>(s);
		Assert.Contains("exploring for a tile26", p.Status); // the fake names the altar tile26
		// With the altar known, the day's wait for the night as before.
		g.AddAltar(30, 59);
		g.Refresh(ctx);
		var q = new ProgressionPlanner();
		ISkill t = q.ResolveForTest(Eye, ctx);
		output.WriteLine($"{q.Status} -> {t?.Describe()}");
		Assert.IsNotType<ExploreSkill>(t);
	}

	/// <summary>
	/// Journey run J2 (2026-10-18): out of the middle tenth to (2841,357), just west of it, the safer side (east) took each search
	/// back into the middle and the trip out began again, over and over. Outside the middle, the search heads away from it.
	/// </summary>
	[Fact]
	public void OutOfTheMiddle_TheAltarSearch_HeadsAwayFromIt()
	{
		FakeGame g = ProgressionTests.Mine(width: 600, sky: 60);
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		int x = ctx.Node.X; // x 3: west of the skipped columns below
		var area = new GenArea(0, 599, x + 20, x + 200, 0, 80);
		Assert.True(area.HoldsColumn(x) || x < area.SkipFromX);
		var west = new GenArea(-400, 599, x + 20, x + 200, 0, 80); // room to the west
		Assert.Equal(-1, ProgressionPlanner.StationHeading(ctx, west));
		var east = new GenArea(-400, 599, x - 200, x - 20, 0, 80); // the middle west of us: east
		Assert.Equal(1, ProgressionPlanner.StationHeading(ctx, east));
	}

	[Fact]
	public void GenArea_ColumnsAndRoom()
	{
		var a = new GenArea(281, 6116, 2880, 3520, 493, 1180); // The Aggressive Site's altars
		Assert.False(a.HoldsColumn(3198)); // the spawn
		Assert.True(a.HoldsColumn(2575)); // where the Crimson's edge was
		Assert.False(a.HoldsColumn(100)); // the ocean side
		Assert.Equal(2879 - 2575, a.RoomFrom(2575, 1)); // up to the middle tenth
		Assert.Equal(2575 - 281, a.RoomFrom(2575, -1)); // to the world's edge side
		Assert.Equal(0, a.RoomFrom(3198, 1));
	}

	[Fact]
	public void AnAltarOnTheMap_FarBeyondTheSearchRound_IsWalkedTo_NotExploredFor()
	{
		// Measured run M10e (world_eow): at home with the vile powder, "exploring for a DemonAltar (none seen yet)", the altars
		// on the map 350 and 640 tiles off being beyond the 150 tiles looked at round the bot.
		FakeGame g = ProgressionTests.Mine(width: 500);
		g.AddAltar(400, 23);
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		Assert.True(400 - ctx.Node.X > ProgressionPlanner.StationSearchRadius);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		Assert.IsNotType<ExploreSkill>(s);
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos altar), p.Status);
		Assert.True(altar.X >= 400, $"{altar}");
	}

	[Fact]
	public void NoArena_TheSummonItemMadeDeepDown_TheEyeIsSummonedOnTheSurface()
	{
		// Measured run U23: no arena (given up), the summon item made at an altar 160 rows down; the Eye was waited for and
		// fought there in 17 tiles of room, and the fight gave up after 28800 ticks.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 45; y++)
			rows.Add(y == 44 ? "...S" + new string('.', 196) : new string('.', 200));
		for (int y = 45; y < 125; y++) {
			char[] r = new string('#', 200).ToCharArray();
			if (y >= 110 && y <= 113)
				for (int x = 40; x <= 56; x++)
					r[x] = '.'; // the altar's pocket, its floor at row 114
			rows.Add(new string(r));
		}
		rows.Add(new string('@', 200));
		FakeGame g = new FakeGame(rows.ToArray());
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var p0 = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(48, p0), Y = Core.Pathfinding.Body.StandingTop(113, p0), ReleaseJump = true };
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"at {ctx.Node}: {p.Status} -> {s?.Describe()}");
		Assert.IsNotType<BossFightSkill>(s);
		Assert.IsType<WalkToSkill>(s);
		Assert.Contains("surface", p.Status);
	}

	[Fact]
	public void AnAltarNoWalkGetsTo_IsNotFoundAgain_TheNextOneIs()
	{
		// Measured run M10d (world_eow): the altar at (1732,358) lay behind a chasm's ebonstone lining (pick 40 breaks none);
		// after 2 failed walks it was dropped, and the search found the same altar again, 5 times until the run's end.
		FakeGame g = ProgressionTests.Mine(width: 120);
		for (int x = 26; x <= 36; x++)
			for (int y = 27; y <= 35; y++) {
				bool inside = x > 27 && x < 35 && y > 28 && y < 34;
				g.Grid.Set(x, y, inside ? CellKind.Empty : CellKind.Solid);
				if (!inside)
					g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		g.AddAltar(30, 32); // sealed in, on the shell's floor (row 34)
		g.AddAltar(80, 23); // out in the open, farther
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationWalkFailures; i++) {
			ISkill s = p.ResolveForTest(Eye, ctx);
			output.WriteLine($"{p.Status} -> {s?.Describe()}");
			var walk = Assert.IsType<WalkToSkill>(s);
			Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, walk, 6000)); // the walk goes as near as it can first (a partial plan), then fails
			p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		}
		ISkill next = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}; known {string.Join(", ", p.KnownStations)}");
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos altar), p.Status);
		Assert.True(altar.X >= 80, $"the sealed altar again: {altar}");
	}

	[Fact]
	public void AnAltarGivenUp_InSightAsAnotherOfItsCells_IsNotTakenBackOverTheOneChosen()
	{
		// Measured run U30: the altar at (1748,411) given up 42 times in 13 game hours, each time taken back from sight as
		// (1750,411), (1749,412) or (1748,412) over the farther one just chosen.
		FakeGame g = ProgressionTests.Mine(width: 120);
		for (int x = 26; x <= 36; x++)
			for (int y = 27; y <= 35; y++) {
				bool inside = x > 27 && x < 35 && y > 28 && y < 34;
				g.Grid.Set(x, y, inside ? CellKind.Empty : CellKind.Solid);
				if (!inside)
					g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		g.AddAltar(30, 32);
		g.AddAltar(80, 23);
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationWalkFailures; i++) {
			var walk = Assert.IsType<WalkToSkill>(p.ResolveForTest(Eye, ctx));
			Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, walk, 6000));
			p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		}
		Assert.IsType<WalkToSkill>(p.ResolveForTest(Eye, ctx));
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos chosen) && chosen.X >= 80, $"{chosen}");
		// The next pass begins with what is in sight: the given-up altar, by another of its cells.
		ctx.Seen = new Surroundings { Stations = new System.Collections.Generic.Dictionary<int, TilePos> { [FakeGame.TileDemonAltar] = new TilePos(32, 33) } };
		p.SeeStationsForTest(ctx);
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos known) && known.X >= 80, $"taken back: {known}");
	}

	[Fact]
	public void AnAltarNoWalkGetsTo_WithRecallPotionsCarried_IsNotWalkedToAgainFromHome_TheNextOneIs()
	{
		// Measured run U30 (world_eye): "no way back to the DemonAltar (2 walks failed); drinking a recall potion to go home",
		// 5 times from d6 +18h (t538000-t561600), each walk from home back to the same lip at (1779,341) or pocket at
		// (1792,324) under the corruption; only the 5th failure, no potion left, went on to "another one". A recall takes the
		// bot home to stations it can make again; an altar nothing places is no nearer from home.
		FakeGame g = ProgressionTests.Mine(width: 120);
		for (int x = 26; x <= 36; x++)
			for (int y = 27; y <= 35; y++) {
				bool inside = x > 27 && x < 35 && y > 28 && y < 34;
				g.Grid.Set(x, y, inside ? CellKind.Empty : CellKind.Solid);
				if (!inside)
					g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		g.AddAltar(30, 32); // sealed in
		g.AddAltar(80, 23); // out in the open, farther
		g.Items[FakeGame.Lens] = 6;
		g.Items[FakeGame.RecallPotion] = 5;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationWalkFailures; i++) {
			ISkill s = p.ResolveForTest(Eye, ctx);
			output.WriteLine($"{p.Status} -> {s?.Describe()}");
			var walk = Assert.IsType<WalkToSkill>(s);
			Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, walk, 6000)); // the walk goes as near as it can first (a partial plan), then fails
			p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		}
		ISkill next = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}; known {string.Join(", ", p.KnownStations)}");
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos altar), p.Status);
		Assert.True(altar.X >= 80, $"the sealed altar again: {altar}");
	}

	[Fact]
	public void AnAltarBehindRockThePickaxeCannotBreak_WithBombsCarried_IsBlastedTo_NotGivenUp()
	{
		// Measured run U30: from the corruption chasm's foot the altar at (1748,412) lay behind ebonstone (pick 40); seven bombs,
		// each "then the walk is Partial", took the bot to it at t721800, after 42 "not that one again for a day" from t673200.
		FakeGame g = ProgressionTests.Mine(width: 120);
		for (int x = 26; x <= 36; x++)
			for (int y = 27; y <= 35; y++) {
				bool inside = x > 27 && x < 35 && y > 28 && y < 34;
				g.Grid.Set(x, y, inside ? CellKind.Empty : CellKind.Solid);
				if (!inside)
					g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		g.AddAltar(30, 32); // sealed in, on the shell's floor (row 34)
		g.AddAltar(80, 23); // out in the open, farther
		g.Items[FakeGame.Lens] = 6;
		g.Items[FakeGame.Bomb] = 5;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		var p = new ProgressionPlanner();
		ISkill s = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe()}");
		var walk = Assert.IsType<WalkToSkill>(s);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, walk, 6000));
		p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		ISkill next = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		var blast = Assert.IsType<BlastOutSkill>(next);
		Assert.True(p.KnownStations.TryGetValue(FakeGame.TileDemonAltar, out TilePos altar) && altar.X < 40, $"given up for {altar}");
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, blast, 3000));
		p.OnSkillFinished(blast, SkillStatus.Succeeded, ctx);
		ISkill after = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {after?.Describe()}; {g.Blasts} blast(s), {g.BlastHits} on the bot");
		var again = Assert.IsType<WalkToSkill>(after);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, again, 6000));
		Assert.True(ctx.Node.ChebyshevTo(new TilePos(31, 32)) <= ProgressionPlanner.StationReach, $"at {ctx.Node}");
	}

	[Fact]
	public void NoAltarFoundAround_TravelsToNewGround_NotStuck()
	{
		// Measured run S17: all else ready at d5 +21h, then "no DemonAltar found after 10 explorations" and auto mode
		// stopped. As for ore that ran out: new ground, farther each time.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Items[FakeGame.Lens] = 6;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = ctx.Node;
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage;
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationExplores; i++)
			p.OnSkillFinished(Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx)), SkillStatus.Failed, ctx);
		ISkill next = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()} (stuck: {p.ResolveForTestStuck})");
		Assert.Null(p.ResolveForTestStuck);
		Assert.True(next is WalkToSkill, p.Status);
		Assert.Contains("new ground", p.Status);
	}

	[Fact]
	public void TheAltarSearch_AfterAFight_GoesOnTheSameWay_AndDoesNotCountTheReplan()
	{
		// Audit after measured run U8: each replan (after every fight) started a fresh exploration the other way and counted
		// it; the 10 explorations went to and fro in about 10 fights (run S17: "no DemonAltar found after 10 explorations").
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var first = Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx));
		var second = Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx)); // the planner asked again, the first unfinished
		Assert.Equal(first.Heading, second.Heading);
		Assert.NotNull(first.Key);
		Assert.Equal(first.Key, second.Key);
		for (int i = 0; i < 3 * ProgressionPlanner.MaxStationExplores; i++)
			Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx)); // replans alone never use the explorations up
	}

	[Fact]
	public void NoAltarSeen_Explores_ThenIsStuck_WithAClearReason()
	{
		// Only what is revealed counts: none seen means go and look, a few times, before giving up.
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.Lens] = 6;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		for (int i = 0; i < ProgressionPlanner.MaxStationExplores; i++)
			p.OnSkillFinished(Assert.IsType<ExploreSkill>(p.ResolveForTest(Eye, ctx)), SkillStatus.Failed, ctx);
		Assert.Null(p.ResolveForTest(Eye, ctx));
		Assert.Contains("nothing can place one", p.ResolveForTestStuck);
	}

	[Fact]
	public void WithTheSummonItem_WaitsForNight_ThenFights()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		Assert.IsType<WaitSkill>(Next(g, night: false, out _));
		FakeGame g2 = ProgressionTests.Mine();
		g2.Items[FakeGame.SuspiciousEye] = 1;
		var fight = Assert.IsType<BossFightSkill>(Next(g2, night: true, out _));
		Assert.Equal(FakeGame.EyeNpc, fight.BossType);
	}

	[Fact]
	public void WithTheSummonItem_ByDay_GoesToTheArenaFirst_ThenWaitsThere()
	{
		// Measured run S14: the summon made at an altar 360 rows down, the bot waited there all day, set off at nightfall,
		// and the walk to the arena took the night.
		FakeGame g = EarlyGameTests.Field(160, 50, 75);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.ResolveForTest(new ArenaBuilt("arena"), ctx); // picks the site
		Assert.NotNull(p.ArenaSite);
		g.Items[FakeGame.SuspiciousEye] = 1;
		ISkill day = p.ResolveForTest(Eye, ctx);
		output.WriteLine($"{p.Status} -> {day?.Describe()}");
		var walk = Assert.IsType<WalkToSkill>(day);
		Assert.Contains("walking to the arena", p.Status);
		// On the arena's floor, not just near its middle: measured run U6, "near" was met four rows under the floor, where the
		// fight's "in the arena" check was not, and the walk began again 87,000 times.
		walk.Start(ctx);
		var (x0, x1, standY) = p.ArenaBounds(ctx).Value;
		Assert.True(walk.Goal.IsGoal(new TilePos((x0 + x1) / 2, standY)));
		Assert.False(walk.Goal.IsGoal(new TilePos((x0 + x1) / 2 + 4, standY + 4)), "under the floor counted as there");
	}

	[Fact]
	public void Hurt_RestsBeforeSummoning_OnceOnly()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.Life = ctx.Combat.LifeMax / 2;
		var p = new ProgressionPlanner();
		var rest = Assert.IsType<WaitSkill>(p.ResolveForTest(Eye, ctx));
		Assert.Equal("life", rest.What);
		// A rest that timed out (still hurt) is not started again straight away: summon anyway.
		p.OnSkillFinished(rest, SkillStatus.Failed, ctx);
		Assert.IsType<BossFightSkill>(p.ResolveForTest(Eye, ctx));
	}

	[Fact]
	public void LateInTheNight_WaitsForTheNextOne()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.SuspiciousEye] = 1;
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.NightTicksLeft = 60 * 60; // a minute: the Eye would fly off at dawn mid-fight
		var p = new ProgressionPlanner();
		var wait = Assert.IsType<WaitSkill>(p.ResolveForTest(Eye, ctx));
		Assert.Equal("next night", wait.What);
		ctx.NightTicksLeft = 60 * 60 * 8;
		Assert.IsType<BossFightSkill>(p.ResolveForTest(Eye, ctx));
	}

	[Fact]
	public void AfterTheBoss_PrepStagesAreMoot()
	{
		// Seen in game: right after winning, the plan went back to crafting arrows for a boss already dead.
		FakeGame g = ProgressionTests.Mine();
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		Assert.Null(p.ResolveForTest(new PrepFor(FakeGame.EyeNpc, new HaveItem(FakeGame.WoodenArrow, 500, "arrows")), ctx));
		Assert.NotNull(p.ResolveForTest(new HaveItem(FakeGame.WoodenArrow, 500, "arrows"), ctx));
	}

	[Fact]
	public void Arrows_AreNotToppedUpAfterEveryShot()
	{
		// The boss scenario: each arrow shot at a demon eye topped the 500 up again, until the wood ran out.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var arrows = new HaveItem(FakeGame.WoodenArrow, 500, "arrows") { RefillBelow = 400 };
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.NotNull(p.ResolveForTest(arrows, ctx)); // never had the 500 yet
		g.Items[FakeGame.WoodenArrow] = 500;
		Assert.Null(p.ResolveForTest(arrows, ctx));
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.Null(p.ResolveForTest(arrows, ctx)); // a few shot: still enough
		g.Items[FakeGame.WoodenArrow] = 399;
		Assert.NotNull(p.ResolveForTest(arrows, ctx)); // running low: back up to 500
		g.Items[FakeGame.WoodenArrow] = 450;
		Assert.NotNull(p.ResolveForTest(arrows, ctx));
	}

	[Fact]
	public void AlreadyDefeated_IsDone()
	{
		FakeGame g = ProgressionTests.Mine();
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		Assert.Null(Next(g, night: true, out ProgressionPlanner p));
		Assert.False(p.Stuck);
	}
}
