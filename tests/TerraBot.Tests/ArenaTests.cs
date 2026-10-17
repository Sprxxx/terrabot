using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 8: the boss arena (flat floor, open air, torches, campfire) and the heart lantern in the house.</summary>
public class ArenaTests
{
	private readonly ITestOutputHelper output;
	public ArenaTests(ITestOutputHelper output) => this.output = output;

	/// <summary>A field 140 wide: ground row 25 (feet row 24), a dirt mound, a pit and two trees inside where the arena goes.</summary>
	private static FakeGame RoughField()
	{
		FakeGame g = EarlyGameTests.Field(140, 50, 75);
		for (int x = 40; x <= 44; x++)          // a mound 2 high
			for (int y = 23; y <= 24; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		for (int x = 60; x <= 63; x++)          // a pit 2 deep
			for (int y = 25; y <= 26; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		return g;
	}

	private static void GiveArenaKit(FakeGame g)
	{
		g.Items[FakeGame.DirtBlock] = 30;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
	}

	[Fact]
	public void Arena_Shape()
	{
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		Assert.Equal(Blueprint.ArenaWidth, bp.Width);
		Assert.Equal(Blueprint.ArenaClearance + 1, bp.Height);
		Assert.All(Enumerable.Range(0, bp.Width), x => Assert.Equal(PlanCell.Block, bp.At(x, bp.Height - 1)));
		Assert.All(Enumerable.Range(0, bp.Width), x => Assert.Equal(PlanCell.Clear, bp.At(x, 0)));
		PlanItem fire = Assert.Single(bp.Items, i => i.ItemType == FakeGame.Campfire);
		Assert.Equal(bp.Width / 2 - 1, fire.X);
		Assert.Equal(bp.Height - 3, fire.Y); // 2 tall, standing on the floor
		int torches = Enumerable.Range(0, bp.Width).Count(x => x % Blueprint.ArenaTorchSpacing == Blueprint.ArenaTorchSpacing / 2 && System.Math.Abs(x - bp.Width / 2) > 2);
		Assert.Equal(torches, bp.Items.Count(i => i.ItemType == FakeGame.Torch)); // every 12 tiles, none beside the campfire
		Assert.False(bp.IsHouse);
		Assert.Equal(bp.Height - 2, bp.StandRow);
	}

	[Fact]
	public void Arena_HasAPostAtEachEnd_AndTheFightStaysInsideThem()
	{
		// Measured run U17: knocked past the open east end at 234/2800, down a 41-row drop, killed there.
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		int floor = Blueprint.ArenaClearance;
		for (int y = floor - Blueprint.ArenaEndPostHeight; y < floor; y++) {
			Assert.Equal(PlanCell.Block, bp.At(0, y));
			Assert.Equal(PlanCell.Block, bp.At(bp.Width - 1, y));
		}
		Assert.NotEqual(PlanCell.Block, bp.At(1, floor - 1));
		Assert.NotEqual(PlanCell.Block, bp.At(0, floor - Blueprint.ArenaEndPostHeight - 1)); // jumped over
		var p = new ProgressionPlanner();
		typeof(ProgressionPlanner).GetProperty(nameof(ProgressionPlanner.ArenaSite)).SetValue(p, new TilePos(100, 10));
		var ctx = new BotContext { Ids = FakeGame.Ids };
		Assert.Equal((101, 100 + bp.Width - 2, 10 + bp.StandRow), p.ArenaBounds(ctx));
	}

	[Fact]
	public void SiteFinder_Arena_AcceptsTreesBumpsAndPits_ButNotTheHouse()
	{
		FakeGame g = RoughField();
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var house = (new TilePos(0, 15), new TilePos(30, 30));
		TilePos? site = SiteFinder.Find(ctx, bp, new TilePos(70, 24), keepOut: new[] { house });
		Assert.NotNull(site);
		output.WriteLine($"site {site}, {SiteFinder.Edits(ctx, bp, site.Value)} edits");
		Assert.Equal(25, site.Value.Y + bp.Height - 1); // floor on the ground row
		Assert.True(site.Value.X > 30, "overlaps the house");
		// Trees rule a house site out but not an arena: they get felled.
		Assert.True(SiteFinder.Edits(ctx, bp, new TilePos(40, 25 - bp.Height + 1)) > 0);
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(45, 25 - 5)));
	}

	[Fact]
	public void SiteFinder_Arena_SlopesOfTheGround_AreBlocks_NotPlatforms()
	{
		// Measured run S10: slopes and half blocks of the ground read as platforms ruled out 122 of 161 arena sites round
		// the house ("a platform in the room"), and the boss plan stopped at max life 200 with the bow made.
		FakeGame g = EarlyGameTests.Field(140, 50, 75);
		for (int x = 2; x < 130; x += 9) {
			g.Grid.Set(x, 24, CellKind.Platform | CellKind.Shaped); // a slope on the ground, in the room
			g.Grid.Set(x + 4, 25, CellKind.Platform | CellKind.Shaped); // a slope in the floor row
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, bp, new TilePos(70, 24));
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, new TilePos(70, 24))}");
		Assert.NotNull(site);
		Assert.Equal(25, site.Value.Y + bp.Height - 1);
		Assert.True(SiteFinder.Edits(ctx, bp, site.Value) > 0); // the slopes in the room get mined
	}

	[Fact]
	public void SiteFinder_Arena_FurnitureInItsOpenAir_DoesNotRuleItOut()
	{
		// A run from U7's arena checkpoint: the bot's own benches and tables (solid-topped, read as platforms) ruled out 94
		// of 161 arena sites.
		FakeGame g = EarlyGameTests.Field(160, 50, 75);
		for (int x = 10; x < 150; x += 20) {
			g.Grid.Set(x, 24, CellKind.Platform); // a work bench on the ground
			g.Furniture[new TilePos(x, 24)] = FakeGame.TileBench;
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint arena = Blueprint.Arena(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, arena, new TilePos(80, 24));
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, arena, new TilePos(80, 24))}");
		Assert.NotNull(site);
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(5, 24 - 4))); // a house's room must be clear
	}

	[Fact]
	public void SiteFinder_Arena_ATreeStandingInADip_IsFelledAndFilledIn_NotARuleOut()
	{
		// Measured run U9: trees standing in dips beside the house, their trunks up through the floor row ((1986,283) on
		// ground at row 284), ruled out 29 of the 47 arena sites clear of the house ("a tree in the floor"), the bot's
		// work benches on bumps the other 18, and auto mode stopped at max life 200.
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 20; x < 290; x += 40) {
			g.Grid.Set(x, 25, CellKind.Empty); // a dip one deep, a tree standing in it
			g.Trees[new TilePos(x, 25)] = 8;
		}
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var anchor = new TilePos(150, 24);
		TilePos? site = SiteFinder.Find(ctx, bp, anchor);
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, anchor)}");
		Assert.NotNull(site);
		Assert.Equal(25, site.Value.Y + bp.Height - 1);
		// A house's floor still may not have one (no axe in its plan).
		Assert.Equal(-1, SiteFinder.Edits(ctx, Blueprint.SmallHouse(FakeGame.Ids), new TilePos(15, 25 - 5)));
	}

	[Fact]
	public void SiteFinder_Explain_SaysWhereAndTheFewestEdits()
	{
		// Measured run U9: "a tree in the floor 29, unbreakable tile 2 under 18 18" said what ruled the sites out but not
		// where; finding the trees and the benches took a parse of the saved world.
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 100; x <= 120; x++)
			g.Grid.Set(x, 24, CellKind.Water); // a pond in most of the sites
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		string why = SiteFinder.Explain(ctx, bp, new TilePos(150, 24));
		output.WriteLine(why);
		Assert.Matches(@"liquid \d+ \(\d+ at \(\d+,24\)\)", why);
		Assert.Matches(@"fewest edits \d+ at \(\d+,18\)", why);
	}

	[Fact]
	public void BuildSkill_Arena_FellsATreeWhoseTopIsInTheFloor_ThenFillsItIn()
	{
		// A tree standing in a pit, all of it at or below the floor row: nothing of it in the open air to fell it by, and a
		// floor block cannot go into its trunk. The floor row's trees are felled too (SiteFinder counts them as felled).
		FakeGame g = EarlyGameTests.Field(140);
		for (int x = 58; x <= 62; x++)
			for (int y = 25; y <= 27; y++)
				g.Grid.Set(x, y, CellKind.Empty); // a pit 5 wide and 3 deep
		g.Trees[new TilePos(60, 27)] = 3; // its top in the floor row
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(20, 25 - bp.Height + 1);
		Assert.True(SiteFinder.Edits(ctx, bp, site) > 0);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks; placed {g.Sim.Placed}, broken {g.Sim.Broken}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.DoesNotContain(new TilePos(60, 27), g.Trees.Keys);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site));
	}

	[Fact]
	public void BuildSkill_Arena_ACampfireThatWillNotGoDown_IsLeftOut_TheArenaStillReady()
	{
		// Measured run M10l: the arena in the corruption, its campfire "failed (no effect after 90 ticks)" 3 times; the build
		// failed, the site was given up, no other site, and the worm was fought on open ground and killed the bot.
		FakeGame g = EarlyGameTests.Field(140);
		GiveArenaKit(g);
		g.RefusedItems.Add(FakeGame.Campfire);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(20, 25 - bp.Height + 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks; placed {g.Sim.Placed}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site));
		Assert.Equal(1, g.Count(FakeGame.Campfire)); // left out, still in the bag
	}

	[Fact]
	public void ArenaPlan_FromDeepDown_GoesUpToTheSurfaceFirst_NotThroughTheCaves()
	{
		// Measured runs U19 and U22: max life 200 came 160+ rows down, and the build's first walk (to fell a tree by the arena)
		// went through the caves with fights for about 5 game hours.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 25; y++)
			rows.Add(new string('.', 400));
		for (int y = 25; y < 90; y++) {
			char[] r = new string(y >= 86 ? '@' : '#', 400).ToCharArray();
			if (y <= 70)
				r[100] = r[101] = '.';
			rows.Add(new string(r));
		}
		char[] feet = rows[24].ToCharArray();
		feet[200] = 'S';
		rows[24] = new string(feet);
		var g = new FakeGame(rows.ToArray());
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = 30;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		ISkill first = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"on the surface: {p.Status} -> {first?.Describe()}; site {p.ArenaSite}");
		Assert.NotNull(p.ArenaSite);
		// Down a shaft 100 tiles off, 46 rows under the ground.
		g.Sim.State = new Core.Physics.BodyState { X = Core.Pathfinding.Body.CanonicalLeft(100, Core.Physics.PhysicsParams.Default), Y = Core.Pathfinding.Body.StandingTop(70, Core.Physics.PhysicsParams.Default), ReleaseJump = true };
		g.Refresh(ctx);
		ISkill next = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"at {ctx.Node}: {p.Status} -> {next?.Describe()}");
		Assert.Contains("up to the surface before building the arena", p.Status);
	}

	[Fact]
	public void ArenaPlan_NoSiteNearTheHouse_LooksFartherOut()
	{
		// Measured runs U8cp and U9: no site within 80 tiles was a stop ("stuck on arena: no arena site"), five rests and
		// auto mode off, with max life 200 and the bow made. A player walks a little further for good ground.
		FakeGame g = EarlyGameTests.FieldFrom(400, 200);
		for (int x = 120; x <= 280; x++)
			g.Grid.Set(x, 24, CellKind.Water); // a lake round the bot: every site within 80 has some of it
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		ISkill s = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}; site {p.ArenaSite}");
		Assert.Null(p.ResolveForTestStuck);
		Assert.NotNull(p.ArenaSite);
		int mid = p.ArenaSite.Value.X + Blueprint.ArenaWidth / 2;
		Assert.InRange(System.Math.Abs(mid - 200), Blueprint.ArenaSearchRadius + 1, Blueprint.ArenaFarSearchRadius);
		Assert.False(p.ArenaOnOpenGround);
	}

	/// <summary>
	/// A house on a hilltop, as in measured runs U14 and U23: its roof at row 30 and floor at 35, the hill x 175-225, the
	/// ground round it at row 52, 17 rows lower (U23: roof 263, floor 268, the ground round it at 283-285).
	/// </summary>
	private static FakeGame HouseOnAHill(out TilePos house, out (TilePos, TilePos) keepOut)
	{
		const int width = 420, plain = 52, hillTop = 35;
		var rows = new string[plain + 8];
		for (int y = 0; y < rows.Length; y++) {
			char[] r = new string(y >= plain ? '#' : '.', width).ToCharArray();
			if (y >= hillTop)
				for (int x = 175; x <= 225; x++)
					r[x] = '#';
			rows[y] = new string(r);
		}
		char[] feet = rows[hillTop - 1].ToCharArray();
		feet[195] = 'S';
		rows[hillTop - 1] = new string(feet);
		house = new TilePos(190, hillTop - 5);
		int gap = ProgressionPlanner.ArenaHouseGap;
		keepOut = (new TilePos(house.X - gap, house.Y - gap), new TilePos(house.X + 11 + gap, house.Y + 5 + gap));
		return new FakeGame(rows);
	}

	[Fact]
	public void SiteFinder_Arena_HouseOnAHill_FindsTheGroundRoundTheHill()
	{
		// Measured runs U14 and U23: the house on a hilltop (roof 263), the ground looked for in rows 248..278 round the roof
		// only: "no ground under the middle 128, over the house 33" within 80, 215 and 105 within 160, and the one site left
		// was on the corruption's edge. A player builds the arena on the ground below the hill.
		FakeGame g = HouseOnAHill(out TilePos house, out var keepOut);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, bp, house, keepOut: new[] { keepOut });
		string why = SiteFinder.Explain(ctx, bp, house, new[] { keepOut });
		output.WriteLine($"site {site}; {why}");
		Assert.NotNull(site);
		Assert.Equal(52, site.Value.Y + bp.Height - 1); // the floor on the ground round the hill
		Assert.DoesNotContain("no ground under the middle", why);
	}

	/// <summary>The arena's middle, where the fight stands, on the evil biome's ground or in the Corruption zone (SiteFinder.InEvilBiome).</summary>
	private static bool Evil(BotContext ctx, TilePos site)
	{
		var mid = new TilePos(site.X + Blueprint.ArenaWidth / 2, site.Y + Blueprint.ArenaClearance - 1);
		return ctx.InEvilBiome(mid) || ctx.CorruptionAround(mid) >= BotContext.CorruptionZoneTiles;
	}

	[Fact]
	public void SiteFinder_Arena_OnTheEvilBiome_CostsMoreThanAFartherSiteOffIt()
	{
		// Measured runs U14 and U23: the site (1896,241) on the corruption's edge, 186 and 196 edits, was still not built after
		// 91,800 ticks each (at least 468 ticks an edit; 47 off the evil biome, runs U2, U10, U20), fights pausing the build;
		// and the Eye fought there comes with the zone's eaters of souls.
		FakeGame g = EarlyGameTests.FieldFrom(520, 210);
		for (int x = 0; x <= 200; x++)
			for (int y = 25; y < 30; y++)
				g.TileTypes[new TilePos(x, y)] = y == 25 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		for (int x = 230; x <= 300; x++)
			g.Grid.Set(x, 24, CellKind.Water); // a pond east of the house: the nearest sites off the corruption are past it
		BotContext ctx = g.CreateContext(output);
		Blueprint far = Blueprint.Arena(FakeGame.Ids, searchRadius: Blueprint.ArenaFarSearchRadius);
		var anchor = new TilePos(210, 24);
		TilePos? site = SiteFinder.Find(ctx, far, anchor);
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, far, anchor)}");
		Assert.NotNull(site);
		Assert.False(Evil(ctx, site.Value), $"the site {site} is in the evil biome");
		Assert.True(site.Value.X > 300, "past the pond");
		// Built there anyway where nothing else will do: a cost, not a ban.
		var west = new TilePos(40, 25 - far.Height + 1);
		Assert.True(Evil(ctx, west));
		Assert.Equal(west, SiteFinder.Find(ctx, Blueprint.Arena(FakeGame.Ids, searchRadius: 10), new TilePos(88, 24)));
	}

	[Fact]
	public void SiteFinder_TheEaterOfWorldsArena_WantsTheEvilBiome_TheCostInverted()
	{
		// The same field as above: the corruption west of the house, a pond east. The Eater of Worlds' arena is wanted in the
		// Corruption zone (Worm Food works only there, and the worm despawns once no player is in it): the site off the biome
		// is the dear one now. Measured runs M10e and M10j: with no arena in the zone, the worm was fought in a pocket and on
		// the corruption's broken open ground, and lost both times.
		FakeGame g = EarlyGameTests.FieldFrom(520, 210);
		for (int x = 0; x <= 200; x++)
			for (int y = 25; y < 30; y++)
				g.TileTypes[new TilePos(x, y)] = y == 25 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		for (int x = 230; x <= 300; x++)
			g.Grid.Set(x, 24, CellKind.Water);
		BotContext ctx = g.CreateContext(output);
		var anchor = new TilePos(320, 24); // nearer the clean ground past the pond than the corruption
		Blueprint eye = Blueprint.Arena(FakeGame.Ids, searchRadius: Blueprint.ArenaFarSearchRadius);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, searchRadius: Blueprint.ArenaFarSearchRadius, inEvilBiome: true);
		Assert.True(eater.InEvilBiome);
		Assert.False(eye.InEvilBiome);
		TilePos? eyeSite = SiteFinder.Find(ctx, eye, anchor), eaterSite = SiteFinder.Find(ctx, eater, anchor);
		output.WriteLine($"the Eye's {eyeSite}, the worm's {eaterSite}; {SiteFinder.Explain(ctx, eater, anchor)}");
		Assert.False(Evil(ctx, eyeSite.Value), $"the Eye's site {eyeSite}");
		Assert.True(Evil(ctx, eaterSite.Value), $"the worm's site {eaterSite}");
		// A cost, not a ban: with no evil ground in reach, the worm's arena still has a site.
		Assert.NotNull(SiteFinder.Find(ctx, Blueprint.Arena(FakeGame.Ids, searchRadius: 20, inEvilBiome: true), new TilePos(420, 24)));
	}

	[Fact]
	public void SiteFinder_TheEaterOfWorldsArena_HasTheZoneAtBothEnds_NotJustItsMiddle()
	{
		// Measured run M10m: the arena at (1872,239), 306 corruption tiles round its middle; kiting east the bot left the zone
		// and the worm despawned ("its bar empty"). The corruption here is x 0-200; the anchor east of it.
		FakeGame g = EarlyGameTests.FieldFrom(520, 210);
		for (int x = 0; x <= 200; x++)
			for (int y = 25; y < 30; y++)
				g.TileTypes[new TilePos(x, y)] = y == 25 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		BotContext ctx = g.CreateContext(output);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, searchRadius: Blueprint.ArenaFarSearchRadius, inEvilBiome: true);
		TilePos site = SiteFinder.Find(ctx, eater, new TilePos(260, 24)).Value;
		int row = site.Y + eater.StandRow;
		int west = ctx.CorruptionAround(new TilePos(site.X + 1, row)), east = ctx.CorruptionAround(new TilePos(site.X + eater.Width - 2, row));
		output.WriteLine($"site {site}: corruption round its west end {west}, east end {east} (the zone wants {BotContext.CorruptionZoneTiles})");
		Assert.True(west >= BotContext.CorruptionZoneTiles && east >= BotContext.CorruptionZoneTiles, $"site {site}: ends {west}/{east}");
	}

	[Fact]
	public void SiteFinder_TheWormsArena_CountsTheZoneAsItWillBeOnceBuilt_NotWithTheTilesItsBuildClears()
	{
		// Measured run M10m (world_eow; the game's count read from its checkpoint worlds before and after the build): the arena
		// at (1872,239) took 12-15 off the Corruption count round its stand row, and the zone then held west of x 1933 only.
		// Here the corruption is two rows deep at x 0-300, and an ebonstone mound stands where the arena's open air goes
		// (x 130-157, rows 18-24): counted, the zone holds at both ends of the floor; once the build has mined it, not at its
		// west end.
		FakeGame g = EarlyGameTests.FieldFrom(520, 210);
		for (int x = 0; x <= 300; x++)
			for (int y = 25; y <= 26; y++)
				g.TileTypes[new TilePos(x, y)] = y == 25 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		for (int x = 130; x <= 157; x++)
			for (int y = 18; y <= 24; y++) {
				g.Grid.Set(x, y, CellKind.Solid);
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileEbonstone;
			}
		BotContext ctx = g.CreateContext(output);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, inEvilBiome: true);
		var origin = new TilePos(62, 18); // the floor on the ground at row 25, the middle at x 110
		int row = origin.Y + eater.StandRow;
		var west = new TilePos(origin.X + 1, row);
		var mid = SiteFinder.Middle(eater, origin);
		output.WriteLine($"west end: {ctx.CorruptionAround(west)} counted, {SiteFinder.CorruptionOnceBuilt(ctx, eater, origin, west)} once built; "
			+ $"middle: {ctx.CorruptionAround(mid)} counted, {SiteFinder.CorruptionOnceBuilt(ctx, eater, origin, mid)} once built");
		Assert.True(ctx.CorruptionAround(west) >= BotContext.CorruptionZoneTiles);
		// The mound less the two blocks of it in the east end post (x 157, rows 23-24), which the build keeps.
		Assert.Equal(ctx.CorruptionAround(mid) - (28 * 7 - 2), SiteFinder.CorruptionOnceBuilt(ctx, eater, origin, mid));
		Assert.True(SiteFinder.CorruptionOnceBuilt(ctx, eater, origin, west) < BotContext.CorruptionZoneTiles);
		Assert.False(SiteFinder.InEvilBiome(ctx, eater, origin), "the zone is counted with the mound the build mines");
	}

	[Fact]
	public void SiteFinder_TheWormsArena_WithNoSiteFullyInTheZone_PrefersTheThickestZone_NotTheNearestSite()
	{
		// Measured runs M10m and M10n (world_eow): no site had the zone at both ends of its floor, and the one chosen, at
		// (1872,239), had 306 corruption tiles round its middle by the bot's count, 6 over the zone's 300; the game's count
		// held the zone over its west part only, and the fights kited out of it. Here the corruption (x 100-259, two rows deep,
		// a third at x 150-210) is too thin for any floor's ends; sites off it all cost the same, and the nearest to the
		// anchor, east of the corruption, had none of it round its middle.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 25; y++)
			rows.Add(y == 24 ? new string('.', 330) + "S" + new string('.', 189) : new string('.', 520));
		for (int y = 25; y < 45; y++)
			rows.Add(new string('#', 520));
		var g = new FakeGame(rows.ToArray());
		for (int x = 100; x <= 259; x++)
			for (int y = 25; y <= (x >= 150 && x <= 210 ? 27 : 26); y++)
				g.TileTypes[new TilePos(x, y)] = y == 25 ? FakeGame.TileCorruptGrass : FakeGame.TileEbonstone;
		BotContext ctx = g.CreateContext(output);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, searchRadius: Blueprint.ArenaFarSearchRadius, inEvilBiome: true);
		var anchor = new TilePos(330, 24);
		TilePos site = SiteFinder.Find(ctx, eater, anchor).Value;
		TilePos mid = SiteFinder.Middle(eater, site);
		int count = SiteFinder.CorruptionOnceBuilt(ctx, eater, site, mid);
		output.WriteLine($"site {site}: {count} round its middle {mid}, {SiteFinder.ZoneShortfall(ctx, eater, site)} short of the margin; "
			+ SiteFinder.Explain(ctx, eater, anchor));
		Assert.False(SiteFinder.InEvilBiome(ctx, eater, site), "no site has the zone at both ends here");
		Assert.True(count >= BotContext.CorruptionZoneTiles, $"{count} round the middle of the site {site}");
		Assert.True(SiteFinder.ZoneShortfall(ctx, eater, site) < SiteFinder.ZoneMargin);
	}

	/// <summary>
	/// A corruption of ebonstone the pickaxe cannot break ('@'), its ground at row <see cref="HillsGround"/> (feet row 59) and
	/// hills <see cref="HillSpacing"/> apart, 3 wide, each a row taller than the one east of it: every 96-wide span holds a hill
	/// taller than the ground under its middle, so no floor on that ground has its open air clear. The start in the valley
	/// at x 250.
	/// </summary>
	private static FakeGame EbonstoneHills()
	{
		const int width = 480, height = 85;
		var rows = new char[height][];
		for (int y = 0; y < height; y++)
			rows[y] = new string(y < HillsGround ? '.' : '@', width).ToCharArray();
		for (int hx = 10; hx < width; hx += HillSpacing)
			for (int y = HillsGround - HillHeight(hx); y < HillsGround; y++)
				for (int x = hx; x < hx + 3; x++)
					rows[y][x] = '@';
		rows[HillsGround - 1][250] = 'S';
		var g = new FakeGame(rows.Select(r => new string(r)).ToArray());
		for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++)
				if (g.Grid.Get(x, y).IsBlock())
					g.TileTypes[new TilePos(x, y)] = FakeGame.TileEbonstone;
		return g;
	}

	private const int HillsGround = 60, HillSpacing = 30;
	private static int HillHeight(int hx) => 1 + (480 - hx) / HillSpacing;

	[Fact]
	public void SiteFinder_TheWormsArena_OverEbonstoneHills_RaisesItsFloorOverThem()
	{
		// Measured run M10o (world_eow, Silver Pickaxe, power 45; ebonstone wants 65): "no arena in the corruption site ...
		// unbreakable tile 25" for 132 of the 161 sites within 80 and 208 of the 321 within 160; the worm was fought in a gully
		// and lost. A player bridges the corruption with a floor in the air over its hills, nothing to break.
		FakeGame g = EbonstoneHills();
		BotContext ctx = g.CreateContext(output);
		var anchor = new TilePos(250, HillsGround - 1);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, inEvilBiome: true);
		var clock = System.Diagnostics.Stopwatch.StartNew();
		TilePos? site = SiteFinder.Find(ctx, eater, anchor);
		output.WriteLine($"site {site} in {clock.ElapsedMilliseconds} ms; {SiteFinder.Explain(ctx, eater, anchor)}");
		Assert.NotNull(site);
		TilePos s = site.Value;
		int floor = s.Y + eater.Height - 1;
		int highest = Enumerable.Range(s.X, eater.Width).Where(x => (x - 10) % HillSpacing < 3 && x >= 10)
			.Min(x => HillsGround - HillHeight(x - (x - 10) % HillSpacing));
		Assert.Equal(highest, floor); // on the top of the span's highest hill, the rest of the floor bridged over the others
		Assert.True(floor < HillsGround - 1, $"floor row {floor} raised over the ground at row {HillsGround}");
		int edits = SiteFinder.Edits(ctx, eater, s, out string why);
		Assert.True(edits >= 0, why);
		Assert.True(edits <= eater.Width + 2 * Blueprint.ArenaEndPostHeight, $"{edits} edits: only the floor and posts to place, nothing to mine");
		Assert.True(SiteFinder.InEvilBiome(ctx, eater, s), "the zone holds at both ends of the raised floor");
		// The ground's sites as before, with the raised ones apart in the reasons: none of the ground's is buildable.
		string explain = SiteFinder.Explain(ctx, eater, anchor);
		Assert.Contains("unbreakable tile", explain);
		Assert.Contains("raised: fine", explain);
		Assert.DoesNotMatch("(^|, )fine", explain);
	}

	[Fact]
	public void SiteFinder_TheWormsArena_IsNotRaisedOutOfTheWormsReach()
	{
		// Measured run M10r: the arena at (1814,239), floor row 246, an old floor of no edits, over ground at rows 285-297; in a
		// fight of 54,000 ticks the worm never got up to it (67 segments in the ground, none in a clear line), 14 arrows shot,
		// no hit either way. A head out of the ground falls at 0.11 px/tick² (WormPattern.ReachRows: 14 rows in Classic).
		FakeGame g = EbonstoneHills();
		int old = HillsGround - 40;
		for (int x = 150; x <= 350; x++)
			g.Grid.Set(x, old, CellKind.Solid); // an old floor, 40 rows over the ground: no edits at all
		BotContext ctx = g.CreateContext(output);
		var anchor = new TilePos(250, HillsGround - 1);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, inEvilBiome: true);
		TilePos? site = SiteFinder.Find(ctx, eater, anchor);
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, eater, anchor)}");
		Assert.NotNull(site);
		int floor = site.Value.Y + eater.Height - 1;
		int reach = WormPattern.ReachRows(ctx.Expert);
		for (int x = site.Value.X; x < site.Value.X + eater.Width; x++) {
			int below = floor + 1;
			while (below < HillsGround && !g.Grid.Get(x, below).IsBlock())
				below++;
			Assert.True(below - floor <= reach, $"column {x}: ground {below - floor} rows under the floor at row {floor}, the worm reaches {reach}");
		}
	}

	[Fact]
	public void BuildSkill_TheWormsRaisedArena_OverEbonstoneHills_IsBuiltFromTheGround()
	{
		// The raised floor must be reachable and buildable: from the valley floor, up by the hills (placing blocks to climb
		// where they are too tall to jump) and out along the floor, one block next to the last, over the lower hills.
		FakeGame g = EbonstoneHills();
		g.Items[FakeGame.DirtBlock] = 200;
		g.Items[FakeGame.Torch] = 10;
		g.Items[FakeGame.Campfire] = 1;
		BotContext ctx = g.CreateContext(output);
		// As the planner sends it there (ResolveArena: the errand into the evil biome, in full armour as the plan wants).
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		ctx.SurfaceY = HillsGround;
		g.Refresh(ctx);
		ctx.EvilErrand = true;
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, inEvilBiome: true);
		TilePos site = SiteFinder.Find(ctx, eater, new TilePos(250, HillsGround - 1)).Value;
		var build = new BuildSkill(eater, site);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks at {ctx.Node}; site {site}; placed {g.Sim.Placed}, broken {g.Sim.Broken}; {build.FailureReason}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, eater, site));
	}

	/// <summary>
	/// The corruption of the world_eow world as the M10o run began (its checkpoint M10m/eow_arena, read from the .wld file,
	/// x 1650..2069, y 200..419; Dumps/world_eow_M10m_corruption.txt): 'E' ebonstone, unbreakable at pick power 45, 'g' and
	/// 'n' the corruption's grass and ebonsand, 'v' its plants, '@' other stone power 45 cannot break, '~' liquid. All of it
	/// counted as seen (the bot's own count is of the map's revealed tiles, so it may count fewer). The start is the M10o
	/// anchor (1870,305), in a three-row gully under a hill; returns the region's origin in the world.
	/// </summary>
	private static FakeGame WorldEowCorruption(out TilePos worldOrigin, out TilePos anchor)
	{
		string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Dumps", "world_eow_M10m_corruption.txt"));
		worldOrigin = new TilePos(1650, 200);
		anchor = new TilePos(1870 - worldOrigin.X, 305 - worldOrigin.Y);
		string[] map = lines.Skip(1).Where(l => l.Length > 0).ToArray();
		var rows = map.Select(r => new string(r.Select(c => c switch { 'E' => '@', 'g' => '#', 'n' => '%', 'v' => '.', _ => c }).ToArray()).ToCharArray()).ToArray();
		rows[anchor.Y][anchor.X] = 'S';
		var g = new FakeGame(rows.Select(r => new string(r)).ToArray());
		for (int y = 0; y < map.Length; y++)
			for (int x = 0; x < map[y].Length; x++)
				if (map[y][x] is 'E' or 'g' or 'n' or 'v')
					g.TileTypes[new TilePos(x, y)] = map[y][x] switch { 'E' => FakeGame.TileEbonstone, 'v' => FakeGame.TileCorruptPlants, _ => FakeGame.TileCorruptGrass };
		return g;
	}

	[Fact]
	public void SiteFinder_TheWormsArena_InTheM10oWorld_IsSitedOverTheEbonstone()
	{
		// Measured run M10o: "no arena in the corruption site ... within 80 tiles of (1870,305)" and "within 160 either", the
		// worm fought in the gully and at the chasm's edge by (1706,262) (runs M10o, M10p), and lost each time.
		FakeGame g = WorldEowCorruption(out TilePos o, out TilePos anchor);
		BotContext ctx = g.CreateContext(output);
		Blueprint eater = Blueprint.Arena(FakeGame.Ids, inEvilBiome: true);
		TilePos site = Assert.NotNull(SiteFinder.Find(ctx, eater, anchor));
		int floor = site.Y + eater.Height - 1, row = site.Y + eater.StandRow;
		TilePos west = new(site.X + 1, row), east = new(site.X + eater.Width - 2, row), mid = SiteFinder.Middle(eater, site);
		output.WriteLine($"site ({site.X + o.X},{site.Y + o.Y}), floor row {floor + o.Y}, x {site.X + o.X}..{site.X + o.X + eater.Width - 1}; "
			+ $"{SiteFinder.Edits(ctx, eater, site)} edits; zone once built: west {SiteFinder.CorruptionOnceBuilt(ctx, eater, site, west)}, "
			+ $"middle {SiteFinder.CorruptionOnceBuilt(ctx, eater, site, mid)}, east {SiteFinder.CorruptionOnceBuilt(ctx, eater, site, east)}; "
			+ SiteFinder.Explain(ctx, eater, anchor));
		Assert.True(SiteFinder.InEvilBiome(ctx, eater, site), "the zone holds at both ends");
		Assert.True(SiteFinder.Edits(ctx, eater, site) <= eater.MaxEdits);
		// Away from the chasm's edge at x 1702-1703 the fights of M10o and M10p died by.
		Assert.True(site.X + o.X > 1730, $"site at x {site.X + o.X}");
	}

	[Fact]
	public void SiteFinder_TheEyesArena_OverEbonstoneHills_IsNotRaised()
	{
		// The raised floor is the worm's arena's alone: the Eye's site finding is unchanged, and in the same hills finds none.
		FakeGame g = EbonstoneHills();
		BotContext ctx = g.CreateContext(output);
		var anchor = new TilePos(250, HillsGround - 1);
		Blueprint eye = Blueprint.Arena(FakeGame.Ids);
		string why = SiteFinder.Explain(ctx, eye, anchor);
		output.WriteLine(why);
		Assert.Null(SiteFinder.Find(ctx, eye, anchor));
		Assert.DoesNotContain("raised", why);
	}

	[Fact]
	public void BuildSkill_Arena_UnderItsOwnFloor_TakesABlockOutToGetOnTop_AndPutsItBack()
	{
		// Measured run U23: the arena floor bridged over a dip at (1953..1991, 248); the walk from the house came up the dip
		// under it to (1950,251), and with the floor off limits found "no path ... no node closer than the start in 6000 (start
		// h 3, rows 251..374)" to a cell above it three times over, and the site was given up. Here the dip's walls cannot be
		// dug, so the only way up is through the floor; a player takes a block of it out and puts it back.
		const int floor = 40;
		var rows = new string[60];
		for (int y = 0; y < rows.Length; y++) {
			char[] r = new string(y < floor ? '.' : y == floor ? '#' : '@', 160).ToCharArray();
			if (y > floor && y <= floor + 5)
				for (int x = 60; x <= 100; x++)
					r[x] = '.'; // the dip under the floor
			if (y == floor - 2)
				r[90] = '#'; // a bump to mine in the open air
			if (y == floor + 5)
				r[80] = 'S';
			rows[y] = new string(r);
		}
		var g = new FakeGame(rows);
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(20, floor - bp.Height + 1);
		var build = new BuildSkill(bp, site);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 3);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks at {ctx.Node}; placed {g.Sim.Placed}, broken {g.Sim.Broken}; {build.FailureReason}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site)); // the block taken out is back
		Assert.Null(ctx.Reserved);
	}

	[Fact]
	public void ArenaPlan_TheFloorIsProtected_OnlyOnceTheArenaIsReady()
	{
		// Measured run U23: the floor was off limits to path moves from the moment the site was picked, and shut the walk in
		// under the part of it bridged over a dip. Once the arena is ready it is kept, as the house is.
		FakeGame g = RoughField();
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		var build = Assert.IsType<BuildSkill>(p.ResolveForTest(new ArenaBuilt("arena"), ctx));
		TilePos site = p.ArenaSite.Value;
		var floorCell = new TilePos(site.X + 10, site.Y + Blueprint.ArenaClearance);
		p.Next(ctx);
		Assert.DoesNotContain(floorCell, ctx.Protected);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 6));
		p.OnSkillFinished(build, SkillStatus.Succeeded, ctx);
		p.Next(ctx);
		Assert.Contains(floorCell, ctx.Protected);
	}

	[Fact]
	public void ArenaPlan_NoSiteAnywhere_TheBossIsFoughtOnOpenGround()
	{
		// Measured runs U8cp and U9: "no arena site" stopped auto mode before the summon. The arena is a convenience: the
		// fight does without one (BossFightSkill.Arena null), as a player fights the Eye on the surface.
		FakeGame g = EarlyGameTests.FieldFrom(500, 250);
		for (int x = 0; x < 500; x++)
			g.Grid.Set(x, 24, CellKind.Water); // water everywhere
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		ISkill s = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}");
		Assert.Null(p.ResolveForTestStuck);
		Assert.Null(s);
		Assert.True(p.ArenaOnOpenGround);
		Assert.Null(p.ArenaSite);
		Assert.Null(p.ArenaBounds(ctx));
		Assert.Null(p.ResolveForTest(new ArenaBuilt("arena"), ctx)); // not looked for again at every plan
		p.ForgetGivenUp();
		Assert.False(p.ArenaOnOpenGround); // after a rest it is looked for again
	}

	[Fact]
	public void SiteFinder_Arena_LooksLeftOfTheHouse_AsFarAsRight()
	{
		// Measured run S12: searched by its left edge within 80 tiles, a 96-wide arena could never go left of the house:
		// "over the house" for 92 of 161 sites, and with the right side no good, no site at all.
		FakeGame g = EarlyGameTests.Field(320, 50, 75);
		for (int x = 175; x < 320; x++) // a lake right of the house
			g.Grid.Set(x, 24, CellKind.Water);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var house = (new TilePos(155, 15), new TilePos(172, 30));
		var anchor = new TilePos(160, 24);
		TilePos? site = SiteFinder.Find(ctx, bp, anchor, keepOut: new[] { house });
		output.WriteLine($"site {site}; {SiteFinder.Explain(ctx, bp, anchor, new[] { house })}");
		Assert.NotNull(site);
		Assert.True(site.Value.X + bp.Width - 1 < 155, "left of the house");
	}

	[Fact]
	public void AnArenaSiteGivenUpOverACell_TheNextSiteIsNotOverItEither()
	{
		// Measured run S16: a floor block that would not go in failed the build; the next site was one column over, with
		// the same cell in its floor, eleven times over (12 game hours).
		FakeGame g = EarlyGameTests.Field(300, 50, 75);
		for (int x = 5; x < 300; x += 30)
			g.Grid.Set(x, 25, CellKind.Empty); // a hole in the floor every 30 tiles: every site has some
		GiveArenaKit(g);
		g.Items[FakeGame.DirtBlock] = 200;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Arena);
		var build = Assert.IsType<BuildSkill>(p.ResolveForTest(new ArenaBuilt("arena"), ctx));
		TilePos first = build.Origin;
		int hole = Enumerable.Range(first.X, Blueprint.ArenaWidth).First(x => (x - 5) % 30 == 0);
		var bad = new TilePos(hole, 25);
		g.Blocks.NoPlace.Add(bad); // nothing goes in that one
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, build, 60 * 60 * 3));
		output.WriteLine(build.FailureReason);
		for (int i = 0; i < ProgressionPlanner.MaxBuildFailuresPerSite; i++)
			p.OnSkillFinished(build, SkillStatus.Failed, ctx);
		ISkill next = p.ResolveForTest(new ArenaBuilt("arena"), ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}; site {p.ArenaSite}");
		Assert.NotNull(p.ArenaSite);
		Assert.False(bad.X >= p.ArenaSite.Value.X && bad.X <= p.ArenaSite.Value.X + Blueprint.ArenaWidth - 1, "the new site is over the cell again");
	}

	[Fact]
	public void BuildSkill_Arena_FellsTrees_LevelsTheGround_AndPlacesTheCampfire()
	{
		FakeGame g = RoughField();
		GiveArenaKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var site = new TilePos(35, 25 - bp.Height + 1); // over the mound (40-44), trees (50, 75) and pit (60-63)
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks; placed {g.Sim.Placed}, broken {g.Sim.Broken}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(ProgressionPlanner.ArenaReady(ctx, bp, site));
		Assert.DoesNotContain(g.Trees.Keys, t => t.X >= site.X && t.X < site.X + bp.Width);
		Assert.Equal(FakeGame.TileCampfire, g.TileTypeAt(site.X + bp.Width / 2, site.Y + bp.StandRow));
		Assert.Null(ctx.Reserved);
	}

	[Fact]
	public void ArenaPlan_CraftsTorchesAndTheCampfire_ThenBuilds()
	{
		FakeGame g = RoughField();
		g.Items[FakeGame.Wood] = 30;
		g.Items[FakeGame.Gel] = 6;
		g.Items[FakeGame.DirtBlock] = 30;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Arena);
		int t = 0;
		for (; t < 60 * 60 * 10 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | {brain.Planner.Status}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status + " / " + brain.LastResult);
		TilePos site = brain.Planner.ArenaSite.Value;
		Assert.True(ProgressionPlanner.ArenaReady(ctx, Blueprint.Arena(FakeGame.Ids), site));
	}

	[Fact]
	public void HeartLantern_ComesFromASpareCrystal_NotAHuntForOne()
	{
		// Measured runs U2 and U4: the lantern's crystal was looked for from d5 +10h and d7 +13h to the end of the run, and
		// the boss was never summoned. Without a crystal in the bag the house goes on without the lantern.
		FakeGame g = EarlyGameTests.Field(60);
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Boss);
		Blueprint house = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, house, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(house, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(house, site), SkillStatus.Succeeded, ctx);
		var need = new HouseBuilt("heart lantern", HeartLantern: true);
		ISkill without = p.ResolveForTest(need, ctx);
		output.WriteLine($"no crystal: {p.Status} -> {without?.Describe() ?? "nothing to do"}");
		Assert.Null(without);
		Assert.Null(p.ResolveForTestStuck);
		g.Items[FakeGame.LifeCrystal] = 1; // one turns up
		ISkill with = p.ResolveForTest(need, ctx);
		output.WriteLine($"a crystal: {p.Status} -> {with?.Describe()}");
		Assert.NotNull(with);
	}

	[Fact]
	public void HeartLantern_IsHungInTheBuiltHouse()
	{
		// The house first (as in the settle plan), then the boss prep adds the lantern under its roof.
		FakeGame g = EarlyGameTests.Field(60);
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
		g.Items[FakeGame.HeartLantern] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		Blueprint house = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, house, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(house, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(house, site), SkillStatus.Succeeded, ctx);

		var need = new HouseBuilt("heart lantern", HeartLantern: true);
		var build = Assert.IsType<BuildSkill>(p.ResolveForTest(need, ctx));
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, build, 60 * 60));
		Assert.Equal(FakeGame.TileLantern, g.TileTypeAt(site.X + 2, site.Y + 1));
		Assert.Null(g.CheckHousing(site.X + house.Inside.X, site.Y + house.Inside.Y));
		Assert.Null(p.ResolveForTest(need, ctx));
	}

	[Fact]
	public void HeartLantern_IsCraftedFromACrystalAndChains()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Items[FakeGame.LifeCrystal] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var need = new HaveItem(FakeGame.HeartLantern, 1, "heart lantern");
		ISkill s = p.ResolveForTest(need, ctx);
		output.WriteLine($"status [{p.Status}] stuck [{p.ResolveForTestStuck}] -> {s?.Describe()}");
		Assert.IsType<MineSkill>(s); // iron ore for the bar for the chains
		g.Items[FakeGame.Chain] = 4;
		Assert.IsType<CraftSkill>(p.ResolveForTest(need, ctx));
	}

	[Fact]
	public void BossPlan_BuildsTheArena_AndSummonsInIt()
	{
		Blueprint bp = Blueprint.Arena(FakeGame.Ids);
		var stages = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids);
		int arena = stages.ToList().FindIndex(n => n is PrepFor { Inner: ArenaBuilt });
		int boss = stages.ToList().FindIndex(n => n is BossDefeated);
		Assert.True(arena >= 0 && arena < boss);
		Assert.Contains(stages, n => n is PrepFor { Inner: HouseBuilt { HeartLantern: true } });
		Assert.True(bp.Width / 2 - BossFightSkill.ArenaSoftMargin >= 20);
	}
}

/// <summary>The planner's cost estimate on a recipe graph shaped like the real game's: many items, many ways.</summary>
public class PlannerCostTests
{
	private readonly ITestOutputHelper output;
	public PlannerCostTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void DeepWideRecipeGraph_StaysCheap()
	{
		// 40 crafted items, each with 3 recipes from two earlier items (and one back to a later item, a cycle):
		// without memoising crafted costs this is about 3^40 evaluations. Seen in game: a frozen game.
		FakeGame g = ProgressionTests.Mine();
		const int first = 20000, n = 40;
		for (int i = 0; i < n; i++) {
			int a = i == 0 ? FakeGame.Wood : first + i - 1, b = i < 2 ? FakeGame.StoneBlock : first + i - 2;
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(1, a), new Ingredient(1, b) } });
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(2, a) } });
			g.Add(new RecipeInfo { Result = first + i, Ingredients = new[] { new Ingredient(1, b), new Ingredient(1, first + System.Math.Min(n - 1, i + 1)) } });
		}
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		var watch = System.Diagnostics.Stopwatch.StartNew();
		ISkill s = p.ResolveForTest(new HaveItem(first + n - 1, 1, "top item"), ctx);
		output.WriteLine($"{watch.ElapsedMilliseconds} ms, {p.CostEvaluations} cost evaluations -> {s?.Describe()} {p.ResolveForTestStuck}");
		// Found obtainable (resolving all 40 levels then stops at the planner's depth limit, which is fine here).
		Assert.DoesNotContain("no way", p.ResolveForTestStuck ?? "");
		Assert.True(p.CostEvaluations < 20000, $"{p.CostEvaluations} evaluations");
	}
}

/// <summary>Kiting inside the arena: brake before its ends, walk back when off it.</summary>
public class ArenaFightTests
{
	private readonly ITestOutputHelper output;
	public ArenaFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly Core.Physics.PhysicsParams P = Core.Physics.PhysicsParams.Default;

	/// <summary>Ground row 25 (feet row 24), an arena over columns 50-145, the Eye hovering far above the middle.</summary>
	private (BotContext ctx, BossFightSkill fight) Setup(float bodyX, float vx)
	{
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new Core.Physics.BodyState { X = bodyX, Y = 25 * 16f - P.Height, VX = vx, ReleaseJump = true };
		ctx.Hostiles = new[] { new EntityInfo(1, FakeGame.EyeNpc, "Eye of Cthulhu", 97 * 16f, 2 * 16f, 100, 110, 0, 0, 2800, 2800, 15,
			EyePattern.EyeAiStyle, 0, 0, 10, 0) };
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc) { Arena = (50, 145, 24) };
		fight.Start(ctx);
		return (ctx, fight);
	}

	[Fact]
	public void RunningAtTheEnd_BrakesInTime()
	{
		// 3 px/tick needs 3² / (2 × 0.28) ≈ 16 px to stop; 12 px from the end it must already be braking.
		float maxX = 146 * 16f - P.Width;
		var (ctx, fight) = Setup(maxX - 12f, 3f);
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void UnderTheArenaNearItsMiddle_TheWalkBackGoesOntoTheFloor_NotDoneAtOnce()
	{
		// Measured run U11cp: knocked to 5 rows under the floor and within 6 tiles of the middle, the walk back was "reached"
		// at once and began again every tick, 6460 times, while the Eye of Cthulhu killed the bot.
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Body = new Core.Physics.BodyState { X = 97 * 16f, Y = 25 * 16f - P.Height, ReleaseJump = true };
		var lines = new System.Collections.Generic.List<string>();
		ctx.Log = new CountLog(lines);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Hostiles = new[] { new EntityInfo(1, FakeGame.EyeNpc, "Eye of Cthulhu", 97 * 16f, 2 * 16f, 100, 110, 0, 0, 2800, 2800, 15,
			EyePattern.EyeAiStyle, 0, 0, 10, 0) };
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc) { Arena = (50, 145, 19) }; // the floor 5 rows up
		fight.Start(ctx);
		for (int t = 0; t < 30; t++) {
			ctx.Tick = t;
			fight.Tick(ctx);
		}
		int starts = lines.Count(l => l.Contains("off the arena"));
		output.WriteLine($"{fight.Describe()}; {starts} walk(s) back begun");
		Assert.Equal(1, starts);
	}

	private sealed class CountLog : IBotLog
	{
		private readonly System.Collections.Generic.List<string> lines;
		public CountLog(System.Collections.Generic.List<string> lines) => this.lines = lines;
		public void Info(string m) => lines.Add(m);
		public void Warn(string m) => lines.Add(m);
		public void Error(string m, System.Exception ex = null) => lines.Add(m);
		public void Debug(string m) { }
	}

	[Fact]
	public void SlidAFewPixelsPastTheEnd_IsStillOnTheArena_NoWalkBack()
	{
		// Checkpoint run U16cp: "off the arena at (2030,266)" with the floor from 2031, four walks back without dodging,
		// and the Eye of Cthulhu killed the bot during one.
		var (ctx, fight) = Setup(50 * 16f - 12f, 0f); // 12 px past the west end: the node reads column 49
		Assert.Equal(49, ctx.Node.X);
		fight.Tick(ctx);
		output.WriteLine(fight.State);
		Assert.DoesNotContain("walking back", fight.State);
	}

	[Fact]
	public void OffTheArena_WalksBack()
	{
		var (ctx, fight) = Setup(160 * 16f, 0f);
		fight.Tick(ctx);
		Assert.StartsWith("walking back to the arena", fight.State);
	}
}
