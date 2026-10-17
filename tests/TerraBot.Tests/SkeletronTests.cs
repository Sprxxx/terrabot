using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 11 (docs M11_SPEC.md): the Skeletron plan, in a small world laid out as the M11 one is (section 0): the spawn
/// east, the Dungeon at the west end: its brick block with a hall and a shaft down past the surface row behind dungeon walls,
/// the entrance porch under a brick roof where the Old Man lives, a lake against it, then flat ground east to the spawn.
/// </summary>
public class SkeletronTests
{
	private readonly ITestOutputHelper output;
	public SkeletronTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The feet row on the ground (floor row 61), the world's surface row (Main.worldSurface), the spawn's column.</summary>
	private const int Width = 600, Feet = 60, Ground = 61, SurfaceRow = 70, SpawnX = 300;
	/// <summary>The Dungeon's bricks x 0-29 up to row 40, the porch x 30-49 (roof rows 44-48), the Old Man's home at x 40.</summary>
	private const int BrickTo = 29, PorchFrom = 30, PorchTo = 49, RoofFrom = 44, RoofTo = 48, OldManX = 40;
	/// <summary>The lake east of the porch, its water rows 66-75 (5 rows under the porch's floor), its bed from row 76.</summary>
	private const int LakeFrom = 50, LakeTo = 110, LakeTop = 66, LakeBed = 76;
	/// <summary>The shaft down from the hall (x 12-13), past the surface row: its floor at row 81.</summary>
	private const int ShaftX = 12, ShaftBottom = 80;

	internal static FakeGame DungeonWorld(bool lake = true)
	{
		const int height = 100;
		var rows = new char[height][];
		for (int y = 0; y < height; y++)
			rows[y] = new string(y < Ground ? '.' : y == height - 1 ? '@' : '#', Width).ToCharArray();
		for (int x = 0; x <= PorchTo; x++)
			for (int y = 40; y < Ground; y++)
				if (x <= BrickTo && !(x >= 6 && x <= 25 && y >= 50) || x >= PorchFrom && y >= RoofFrom && y <= RoofTo)
					rows[y][x] = '#';
		for (int y = Ground; y <= ShaftBottom; y++)
			rows[y][ShaftX] = rows[y][ShaftX + 1] = '.';
		if (lake)
			for (int x = LakeFrom; x <= LakeTo; x++)
				for (int y = Ground; y < LakeBed; y++)
					rows[y][x] = y >= LakeTop ? '~' : '.';
		rows[Feet][SpawnX] = 'S';
		var g = new FakeGame(rows.Select(r => new string(r)).ToArray());
		for (int x = 0; x <= PorchTo; x++)
			for (int y = 40; y <= Ground; y++)
				if (g.Grid.Get(x, y).IsSolid())
					g.TileTypes[new TilePos(x, y)] = FakeGame.TileBlueBrick;
		// The hall and the shaft have dungeon walls behind them, the porch too (above the surface they do no harm).
		for (int x = 6; x <= PorchTo; x++)
			for (int y = 50; y < Ground; y++)
				g.DungeonWalls.Add(new TilePos(x, y));
		for (int y = Ground; y <= ShaftBottom; y++)
			for (int x = ShaftX - 1; x <= ShaftX + 2; x++)
				g.DungeonWalls.Add(new TilePos(x, y));
		return g;
	}

	/// <summary>The Old Man (18 × 40) standing at his home on the porch's floor.</summary>
	private static EntityInfo OldMan(int x = OldManX, int id = 50) =>
		new(id, FakeGame.OldManNpc, "Old Man", x * 16f, (Feet + 1) * 16f - 40f, 18, 40, 0f, 0f, 250, 250, 10, 7);

	private BotContext Armoured(FakeGame g)
	{
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		ctx.Combat.HasRanged = true;
		ctx.SurfaceY = SurfaceRow;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Refresh(ctx);
		return ctx;
	}

	private static void MoveTo(FakeGame g, BotContext ctx, int x, int feetRow)
	{
		var p = Core.Physics.PhysicsParams.Default;
		g.Sim.State = new Core.Physics.BodyState { X = Body.CanonicalLeft(x, p), Y = Body.StandingTop(feetRow, p), ReleaseJump = true };
		g.Refresh(ctx);
	}

	/// <summary>The boss plan done (the Eye defeated, settled in a house by the spawn, armoured, max life 200, the bow and 500 arrows), by day.</summary>
	private (BotContext, ProgressionPlanner) AfterTheEye(FakeGame g)
	{
		BotContext ctx = Armoured(g);
		g.Stations[FakeGame.TileBench] = new TilePos(SpawnX + 2, Feet);
		g.Stations[FakeGame.TileFurnace] = new TilePos(SpawnX + 5, Feet);
		g.Stations[FakeGame.TileAnvil] = new TilePos(SpawnX - 2, Feet);
		foreach (int item in new[] { FakeGame.WoodenSword, FakeGame.IronBroadsword, FakeGame.IronBow })
			g.Items[item] = 1;
		g.Items[FakeGame.WoodenArrow] = ProgressionPlanner.SkeletronArrows;
		g.Equipped.UnionWith(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves });
		g.LifeMax = ProgressionPlanner.BossLife;
		g.BossesDefeated.Add(FakeGame.EyeNpc);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Skeletron);
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		g.Refresh(ctx);
		return (ctx, p);
	}

	private ISkill Next(ProgressionPlanner p, BotContext ctx)
	{
		ISkill s = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {s?.Describe() ?? "null"}");
		return s;
	}

	[Fact]
	public void TheSkeletronPlan_IsTheBossPlan_ThenSkeleton_InTheSpecsOrder()
	{
		var boss = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Boss, FakeGame.Ids).ToList();
		var sk = ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Skeletron, FakeGame.Ids).ToList();
		Assert.Contains(ProgressionPlanner.Plans.Skeletron, ProgressionPlanner.Plans.All);
		// The Eye's plan as it was, first; not the Eater of Worlds' (nothing in Skeletron's chain needs it, spec section 6).
		Assert.Equal(boss.Select(n => (n.GetType(), n.Label)), sk.Take(boss.Count).Select(n => (n.GetType(), n.Label)));
		Assert.DoesNotContain(sk, n => n is BossDefeated { Npc: FakeGame.EowHead });
		// S1 the prep, S2 the Dungeon, S4 its arena, S5-S7 the summon and the fight.
		Assert.Equal(new[] { "bow", "unholy arrows", "arrows", "max life", "healing potions", "recall potion", "the Dungeon",
			ProgressionPlanner.DungeonArenaLabel, "Skeletron" }, sk.Skip(boss.Count).Select(n => n.Label));
		Assert.All(sk.Skip(boss.Count).SkipLast(1), n => Assert.Equal(FakeGame.SkeletronHeadNpc, Assert.IsType<PrepFor>(n).BossNpc));
		var prep = sk.Skip(boss.Count).Cast<PrepFor>().Take(8).Select(n => n.Inner).ToList();
		Assert.Equal(new[] { FakeGame.IronBow, FakeGame.DemonBow }, Assert.IsType<HaveItem>(prep[0]).Accepts);
		Assert.Equal(FakeGame.UnholyArrow, Assert.IsType<FromBag>(prep[1]).Inner.Accepts[0]);
		var arrows = Assert.IsType<HaveItem>(prep[2]);
		Assert.Equal(new[] { FakeGame.WoodenArrow, FakeGame.UnholyArrow }, arrows.Accepts); // wooden and unholy together
		Assert.Equal((500, 400), (arrows.Count, arrows.RefillBelow));
		Assert.Equal(new[] { FakeGame.HealingPotion, FakeGame.LesserHealingPotion }, Assert.IsType<FromBag>(prep[4]).Inner.Accepts);
		Assert.Equal(FakeGame.RecallPotion, Assert.IsType<FromBag>(prep[5]).Inner.Accepts[0]);
		Assert.IsType<DungeonFound>(prep[6]);
		Assert.True(Assert.IsType<ArenaBuilt>(prep[7]).AtDungeon);
		var skeletron = Assert.IsType<BossDefeated>(sk[^1]);
		Assert.Equal((FakeGame.SkeletronHeadNpc, 0), (skeletron.Npc, skeletron.SummonItem));
		Assert.Equal(new[] { FakeGame.SkeletronHandNpc }, skeletron.Minions);
		Assert.True(skeletron.NightOnly);
		Assert.True(skeletron.ByOldMan);
	}

	/// <summary>The planner's walk looking for the Dungeon: a trip along the surface that stops when it shows.</summary>
	private TravelUntilSkill Hop(ProgressionPlanner p, BotContext ctx)
	{
		var hop = Assert.IsType<TravelUntilSkill>(Next(p, ctx));
		Assert.True(hop.Walk.Travel);
		Assert.Contains("Dungeon", p.Status);
		return hop;
	}

	[Fact]
	public void LookingForTheDungeon_GoesWestWithNothingSeen_TowardTheSnow_AndAwayFromTheJungle()
	{
		// Spec 1a/1c: the snow is on the Dungeon's side of the world, the jungle on the other (WorldGen.cs:8209-8259); with
		// neither seen, west first (the spec's order).
		FakeGame g = DungeonWorld();
		var (ctx, p) = AfterTheEye(g);
		Assert.False(p.SkeletronPrepMet);
		var hop = Hop(p, ctx);
		Assert.True(p.SkeletronPrepMet, p.Status); // bow, arrows, life, (potions from the bag): on to the Dungeon
		Assert.True(ctx.EvilErrand); // the way crosses the evil biome's surface where it lies between: open in full armour
		int from = ctx.Node.X;
		Assert.Equal(from - ProgressionPlanner.DungeonHopTiles, hop.Walk.Target.X);
		Assert.Contains("west first", p.Status);

		// Snow seen on the surface east of the spawn: east.
		for (int x = 400; x <= 420; x++)
			g.TileTypes[new TilePos(x, Ground)] = FakeGame.TileSnow;
		p.Reset(ProgressionPlanner.Plans.Skeletron);
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		hop = Hop(p, ctx);
		Assert.Equal(ctx.Node.X + ProgressionPlanner.DungeonHopTiles, hop.Walk.Target.X);
		Assert.Contains("snow", p.Status);

		// The jungle east instead, no snow: west.
		for (int x = 400; x <= 420; x++)
			g.TileTypes[new TilePos(x, Ground)] = FakeGame.TileJungleGrass;
		p.Reset(ProgressionPlanner.Plans.Skeletron);
		BuildingTests.HouseAlreadyThere(g, p, ctx);
		hop = Hop(p, ctx);
		Assert.Equal(ctx.Node.X - ProgressionPlanner.DungeonHopTiles, hop.Walk.Target.X);
		Assert.Contains("jungle", p.Status);
	}

	[Fact]
	public void TheWalkLookingForTheDungeon_StopsWhenItsBricksShow_AndGoesOnToThem()
	{
		FakeGame g = DungeonWorld();
		var (ctx, p) = AfterTheEye(g);
		var hop = Hop(p, ctx);
		hop.Start(ctx);
		Assert.Equal(SkillStatus.Running, hop.Tick(ctx)); // nothing in view at the spawn
		// Within sight of the Dungeon's bricks (spec 1c: seen from about 60 tiles), the Old Man not yet in view.
		MoveTo(g, ctx, BrickTo + 50, Feet);
		Assert.NotNull(ProgressionPlanner.SeenDungeonBricks(ctx));
		for (int t = 0; t < TravelUntilSkill.CheckEvery; t++)
			if (hop.Tick(ctx) != SkillStatus.Running)
				break;
		Assert.True(hop.Seen);
		ISkill s = Next(p, ctx);
		Assert.NotNull(p.DungeonFoundAt);
		Assert.Null(p.OldManAt);
		Assert.Contains("Dungeon's bricks", p.Status);
		Assert.True(s is ExploreSkill or TravelUntilSkill, s?.Describe());
	}

	[Fact]
	public void TheOldManSeen_IsTheDungeonFound_AndTheArenaGoesOnHisEntranceSide_OutFromUnderThePorchRoof_OverTheLake()
	{
		FakeGame g = DungeonWorld();
		var (ctx, p) = AfterTheEye(g);
		MoveTo(g, ctx, LakeTo + 20, Feet);
		g.TownNpcs.Add(OldMan());
		g.Items[FakeGame.DirtBlock] = 400;
		g.Items[FakeGame.Torch] = 20;
		g.Items[FakeGame.Campfire] = 1;
		g.Refresh(ctx);
		var build = Assert.IsType<BuildSkill>(Next(p, ctx));
		Assert.Equal(new TilePos(OldManX, Feet), p.OldManAt);
		Assert.True(build.Blueprint.AtDungeon);
		Assert.Equal(Blueprint.DungeonArenaWidth, build.Blueprint.Width);
		TilePos site = build.Origin;
		output.WriteLine($"site {site}, x {site.X}-{site.X + build.Blueprint.Width - 1}");
		// On the entrance's side of the Old Man (toward the spawn), not over the Dungeon; past the porch's roof (the head would
		// hover in its bricks, arrows stop on them); its floor the porch's, across the lake.
		Assert.InRange(site.X, PorchTo + 1, OldManX + ProgressionPlanner.DungeonArenaNearTiles);
		Assert.Equal(Ground, site.Y + build.Blueprint.Height - 1);
		Assert.Equal(0, SiteFinder.SkyBlocks(ctx, build.Blueprint, site));
		Assert.True(SiteFinder.SkyBlocks(ctx, build.Blueprint, new TilePos(OldManX, site.Y)) > 0);
		Assert.True(site.X <= LakeTo && site.X + build.Blueprint.Width - 1 > LakeTo, "the floor goes over the lake to the ground beyond");
		Assert.Equal(site, p.DungeonArenaSite);
	}

	[Fact]
	public void TheDungeonGuardiansGround_IsNoPlaceForAPath_UntilSkeletronIsBeaten()
	{
		// Spec section 4: below the surface row with a dungeon wall behind, every spawn is a Dungeon Guardian.
		FakeGame g = DungeonWorld();
		BotContext ctx = Armoured(g);
		MoveTo(g, ctx, ShaftX + 4, Feet); // in the hall, over the shaft
		var deep = new TilePos(ShaftX, ShaftBottom);
		Assert.True(ctx.GuardianGround(deep));
		Assert.True(ctx.GuardianGround(new TilePos(ShaftX, SurfaceRow + 1 - BotContext.GuardianMarginRows)));
		Assert.False(ctx.GuardianGround(new TilePos(ShaftX, SurfaceRow - BotContext.GuardianMarginRows)));
		Assert.False(ctx.GuardianGround(new TilePos(ShaftX + 4, Feet))); // the hall is above the surface: no Guardians there
		var goal = new GoalStandAt(deep, ctx.Physics);
		PathResult r = new AStarPathfinder().FindPath(ctx.Moves, ctx.Node, goal, new SearchLimits { MaxExpansions = 20000 });
		output.WriteLine(r.ToString());
		Assert.NotEqual(PathStatus.Found, r.Status);
		Assert.DoesNotContain(r.Steps, s => ctx.GuardianGround(s.To));

		// Skeletron beaten: the Dungeon is open (a flag of the game, not a place banned).
		g.BossesDefeated.Add(FakeGame.SkeletronHeadNpc);
		Assert.False(ctx.GuardianGround(deep));
		r = new AStarPathfinder().FindPath(ctx.Moves, ctx.Node, goal, new SearchLimits { MaxExpansions = 20000 });
		output.WriteLine(r.ToString());
		Assert.Equal(PathStatus.Found, r.Status);
	}

	[Fact]
	public void ADungeonGuardianInSight_IsEscapedByARecallPotion_NeverFought()
	{
		FakeGame g = DungeonWorld();
		BotContext ctx = Armoured(g);
		g.Items[FakeGame.RecallPotion] = 2;
		ctx.Combat.WeaponReach = 30f;
		var guardian = new EntityInfo(77, FakeGame.GuardianNpc, "Dungeon Guardian", (SpawnX + 6) * 16f, (Feet - 4) * 16f, 80, 102, -8f, 0f, 9999, 9999, 1000,
			SkeletronPattern.HeadAiStyle);
		g.ExtraHostiles.Add(guardian);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Skeletron);
		g.Refresh(ctx);
		brain.Tick(ctx);
		output.WriteLine(brain.Describe());
		Assert.True(brain.Current is UseItemSkill { ItemType: FakeGame.RecallPotion }, brain.Describe());
		Assert.False(ProgressionPlanner.InDanger(ctx)); // left out of every fight
	}

	/// <summary>A world with flat ground east of the porch and the arena's floor there already (its end posts): ready at once.</summary>
	private (FakeGame, BotContext, ProgressionPlanner) ArenaReadyByTheDungeon()
	{
		FakeGame g = DungeonWorld(lake: false);
		int x0 = PorchTo + 1, x1 = x0 + Blueprint.DungeonArenaWidth - 1;
		foreach (int x in new[] { x0, x1 })
			for (int y = Ground - Blueprint.ArenaEndPostHeight; y < Ground; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		var (ctx, p) = AfterTheEye(g);
		g.Items[FakeGame.RecallPotion] = 1;
		g.TownNpcs.Add(OldMan());
		MoveTo(g, ctx, x0 + 60, Feet);
		// By day: to the arena's end by the Old Man (spec S5: within about 20 tiles of him), to wait for night there.
		Assert.IsType<WalkToSkill>(Next(p, ctx));
		Assert.Contains("wait for night", p.Status);
		Assert.Equal(new TilePos(x0, Ground - Blueprint.ArenaClearance), p.DungeonArenaSite);
		return (g, ctx, p);
	}

	[Fact]
	public void BySkeletronsArena_TheBotWaitsForNightAtItsEndByTheOldMan_ThenWalksUpToHim_AndTheFightSummonsByCurse()
	{
		var (g, ctx, p) = ArenaReadyByTheDungeon();
		var (x0, _, y) = Assert.IsType<(int, int, int)>(p.DungeonArenaBounds(ctx));
		MoveTo(g, ctx, x0 + 2, y);
		var wait = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("night", wait.What);
		Assert.InRange(ctx.Node.X - OldManX, 0, 20);

		// Night, early: up to the Old Man, into chat range.
		g.IsNight = true;
		g.Refresh(ctx);
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - 1000;
		var up = Assert.IsType<WalkToSkill>(Next(p, ctx));
		Assert.Contains("walking up to the Old Man", p.Status);
		MoveTo(g, ctx, OldManX + 1, Feet);
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - 1000;
		Assert.True(ChatButtonSkill.InChatRange(ctx, OldMan()));
		var fight = Assert.IsType<BossFightSkill>(Next(p, ctx));
		Assert.True(p.OldManMet);
		Assert.Equal((0, FakeGame.SkeletronHeadNpc), (fight.SummonItem, fight.BossType));
		Assert.True(fight.IsSkeletron);
		Assert.Equal(p.DungeonArenaBounds(ctx), fight.Arena);
		Assert.NotNull(fight.Summon);

		// The fight presses "Curse": Skeletron spawns at the Old Man, and the fight walks out onto the arena from his porch.
		fight.Start(ctx);
		var states = new List<string>();
		for (int t = 0; t < 60; t++) {
			g.Refresh(ctx);
			ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - 1000 - t;
			ctx.Tick = 5000 + t;
			Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
			states.Add(fight.State);
			g.Step(ctx);
		}
		output.WriteLine(string.Join(" | ", states.Distinct()));
		Assert.Equal(1, g.ChatPresses);
		Assert.Contains(g.ExtraHostiles, h => h.Type == FakeGame.SkeletronHeadNpc);
		Assert.Contains(states, st => st.Contains("walking back to the arena"));
		// With the head up the plan stays on the fight.
		Assert.IsType<BossFightSkill>(Next(p, ctx));
	}

	[Fact]
	public void TooLateInTheNight_OrWithNoRecallPotionAfterAnHourAndAHalf_TheSummonWaitsForTheNextNight()
	{
		var (g, ctx, p) = ArenaReadyByTheDungeon();
		var (x0, _, y) = Assert.IsType<(int, int, int)>(p.DungeonArenaBounds(ctx));
		MoveTo(g, ctx, x0 + 2, y);
		g.IsNight = true;
		g.Refresh(ctx);
		// After 10:30 PM (Main.time 10800): six minutes before dawn's enrage is the least the fight is given (spec 3e).
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - (ProgressionPlanner.SummonByTime + 1);
		var wait = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("next night", wait.What);
		Assert.Contains("too late tonight", p.Status);
		// With no recall potion, the window closes at 5400.
		g.Items[FakeGame.RecallPotion] = 0;
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - (ProgressionPlanner.SummonByTimeNoRecall + 1);
		Assert.Equal("next night", Assert.IsType<WaitSkill>(Next(p, ctx)).What);
		Assert.Contains("no recall potion", p.Status);
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - (ProgressionPlanner.SummonByTimeNoRecall - 1);
		Assert.IsType<WalkToSkill>(Next(p, ctx)); // early enough: up to the Old Man
		Assert.Contains("Old Man", p.Status);
	}

	[Fact]
	public void TheOldManGoneAtNight_TheBotWaitsForTheMorning_HeIsBackOnlyByDay()
	{
		// Main.cs:83484-83490: the Old Man respawns only in the daytime branch of the town NPC check, one summon a night.
		var (g, ctx, p) = ArenaReadyByTheDungeon();
		var (x0, _, y) = Assert.IsType<(int, int, int)>(p.DungeonArenaBounds(ctx));
		MoveTo(g, ctx, x0 + 2, y);
		Next(p, ctx); // the Old Man seen and remembered
		g.TownNpcs.Clear();
		g.IsNight = true;
		g.Refresh(ctx);
		ctx.NightTicksLeft = ProgressionPlanner.NightLengthTicks - 1000;
		var wait = Assert.IsType<WaitSkill>(Next(p, ctx));
		Assert.Equal("day", wait.What);
		Assert.Contains("no Old Man", p.Status);
	}

	[Fact]
	public void TheCurseButton_IsThereOnlyAtNight_AndOnlyInChatRange()
	{
		FakeGame g = DungeonWorld(lake: false);
		BotContext ctx = Armoured(g);
		g.TownNpcs.Add(OldMan());
		MoveTo(g, ctx, OldManX + 9, Feet);
		g.Refresh(ctx);
		// Out of the 80 x 64 px box round our centre (Main.cs:56054-56063).
		var chat = new ChatButtonSkill(FakeGame.OldManNpc, "Old Man");
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, chat, 5));
		Assert.Contains("out of chat range", chat.FailureReason);
		// In range by day: no Curse button (Main.cs:51819-51825).
		MoveTo(g, ctx, OldManX + 2, Feet);
		chat = new ChatButtonSkill(FakeGame.OldManNpc, "Old Man");
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, chat, 5));
		Assert.Contains("by day", chat.FailureReason);
		Assert.Empty(g.ExtraHostiles);
		// At night: pressed, and Skeletron is up.
		g.IsNight = true;
		chat = new ChatButtonSkill(FakeGame.OldManNpc, "Old Man");
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, chat, 5));
		Assert.Contains(g.ExtraHostiles, h => h.Type == FakeGame.SkeletronHeadNpc);
	}
}
