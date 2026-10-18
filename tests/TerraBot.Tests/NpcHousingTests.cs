using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Rooms for the town NPCs (<see cref="Blueprint.NpcRoom"/>, the planner's <see cref="NpcHousing"/> stage), in <see cref="FakeGame"/>.</summary>
public class NpcHousingTests
{
	private readonly ITestOutputHelper output;
	public NpcHousingTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The floor tile the game gives an NPC living in a plan at <paramref name="origin"/> (ScoreRoom's bestX/bestY).</summary>
	private static TilePos HomeIn(TilePos origin) => new(origin.X + 7, origin.Y + 5);

	private static TownNpcHome Living(int type, string name, TilePos origin) => new(type, name, false, HomeIn(origin));

	/// <summary>A field 90 wide with the house built near the start (column 3) and a work bench in it.</summary>
	private (FakeGame G, BotContext Ctx, BotBrain Brain, TilePos House) Settled()
	{
		FakeGame g = EarlyGameTests.Field(90);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Housing);
		BuildingTests.HouseAlreadyThere(g, brain.Planner, ctx);
		TilePos house = brain.Planner.HouseSite.Value;
		g.Stations[FakeGame.TileBench] = new TilePos(house.X + 2, house.Y + 4); // in the house, by the door
		g.Furniture[g.Stations[FakeGame.TileBench]] = FakeGame.TileBench;
		g.Furniture[new TilePos(house.X + 3, house.Y + 4)] = FakeGame.TileBench;
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx); // the fake sees stations anew every 20 refreshes, as the game adapter scans them
		output.WriteLine($"house at {house}, its housing check: {g.CheckHousing(house.X + 5, house.Y + 2) ?? "valid"}");
		return (g, ctx, brain, house);
	}

	private static void GiveRoomMaterials(FakeGame g, int rooms)
	{
		g.Items[FakeGame.Wood] = 40 * rooms;
		g.Items[FakeGame.Gel] = 2 * rooms;
		g.Items[FakeGame.DirtBlock] = 40 * rooms;
	}

	private int RunUntil(FakeGame g, BotContext ctx, BotBrain brain, System.Func<bool> done, int maxTicks)
	{
		int t = 0;
		for (; t < maxTicks && brain.Auto && !done(); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | {brain.Planner.Status}");
		return t;
	}

	[Fact]
	public void NpcRoom_SharesAWallWithItsNeighbour_AndEachSideIsTheGamesSize()
	{
		foreach (int shared in new[] { -1, 0, 1 }) {
			Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, shared);
			Assert.Equal(Blueprint.NpcRoomWidth, bp.Width);
			Assert.Equal(6, bp.Height);
			Assert.True(bp.IsNpcRoom && bp.IsHouse);
			// The frame with the shared column counts 72 tiles, as the house (vanilla wants 60 to 750, WorldGen.StartRoomCheck).
			Assert.Equal(40, bp.Count(PlanCell.Wall));
			int door = shared > 0 ? 0 : bp.Width - 1, sharedCol = shared < 0 ? 0 : shared > 0 ? bp.Width - 1 : -1;
			for (int y = 0; y < bp.Height; y++) {
				if (sharedCol >= 0)
					Assert.Equal(PlanCell.None, bp.At(sharedCol, y));
				Assert.Equal(y is >= 2 and <= 4 ? PlanCell.Clear : PlanCell.Block, bp.At(door, y));
			}
			PlanItem d = bp.Items.Single(it => it.ItemType == FakeGame.Door);
			Assert.Equal(door, d.X);
			Assert.True(d.PlaceLast);
			// Nothing in the columns beside the doors (an open door swings into one).
			Assert.DoesNotContain(bp.Items, it => it.ItemType != FakeGame.Door && (it.Covers(1, 3) || it.Covers(bp.Width - 2, 3)));
		}
	}

	[Fact]
	public void ARoomBuiltOntoTheHouse_AndOneOntoThat_AreAllValidHousing()
	{
		var (g, ctx, _, house) = Settled();
		g.Items[FakeGame.WoodWall] = 80;
		g.Items[FakeGame.Door] = 2;
		g.Items[FakeGame.Chair] = 2;
		g.Items[FakeGame.Table] = 2;
		g.Items[FakeGame.Torch] = 2;
		g.Items[FakeGame.DirtBlock] = 60;
		var first = new TilePos(house.X + Blueprint.NpcRoomStep, house.Y);
		var second = new TilePos(first.X + Blueprint.NpcRoomStep, house.Y);
		foreach (TilePos at in new[] { first, second }) {
			Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, -1);
			Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, at), 60 * 150));
			Assert.Null(g.CheckHousing(at.X + bp.Inside.X, at.Y + bp.Inside.Y));
		}
		// The house is still its own room (the shared wall is its right wall, untouched).
		Assert.Null(g.CheckHousing(house.X + 5, house.Y + 2));
	}

	[Fact]
	public void NobodyToHouse_TheEmptyHouseIsTheSpareRoom_NoRoomBuilt()
	{
		var (g, ctx, brain, _) = Settled();
		RunUntil(g, ctx, brain, () => brain.Planner.Complete && brain.Current == null, 600);
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		Assert.Empty(brain.Planner.NpcRooms);
		Assert.Contains(ctx.Log is TestLog l ? l.Lines : null, s => s.Contains("NPC housing: 1 rooms (1 valid), 0 occupied, 1 empty"));
	}

	[Fact]
	public void TheGuideInTheHouse_ARoomBesideIt_FromWoodAndGel()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		GiveRoomMaterials(g, 1);
		RunUntil(g, ctx, brain, () => brain.Planner.Complete && brain.Current == null, 60 * 60 * 20);
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		var room = Assert.Single(brain.Planner.NpcRooms);
		// Right of the house, its left column the house's right wall: the house's door side stays open to the outside.
		Assert.Equal(new TilePos(house.X + Blueprint.NpcRoomStep, house.Y), room.Origin);
		Assert.Equal(-1, room.Shared);
		Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, room.Shared);
		Assert.Null(g.CheckHousing(room.Origin.X + bp.Inside.X, room.Origin.Y + bp.Inside.Y));
		var log = ((TestLog)ctx.Log).Lines;
		Assert.Contains(log, s => s.Contains("NPC housing: 1 rooms (1 valid), 1 occupied (Guide), 0 empty") && s.Contains("building room 1 at"));
		Assert.Contains(log, s => s.Contains("NPC housing: room 1 at") && s.Contains("is valid housing"));
		// Built from wood and gel at the bench: the furniture and walls crafted.
		Assert.True(g.Count(FakeGame.Wood) < 40, $"wood left {g.Count(FakeGame.Wood)}");
	}

	/// <summary>world_wof E32 (2026-10-18): the Guide dead, his room taken by a homeless NPC; "waiting for the Guide" for two days.</summary>
	[Fact]
	public void TheGuideDead_AndNoRoomFree_ARoomIsBuiltRatherThanWaitedFor()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(18, "Nurse", house)); // the house taken
		g.AliveTownNpcs.Remove(FakeGame.GuideNpc);
		GiveRoomMaterials(g, 1);
		g.Refresh(ctx);
		ISkill next = brain.Planner.ResolveForTest(new TownNpcAlive(FakeGame.GuideNpc, "the Guide"), ctx);
		output.WriteLine($"{brain.Planner.Status} -> {next?.Describe()}");
		Assert.IsNotType<WaitSkill>(next);
		Assert.Contains("no free room", brain.Planner.Status);
	}

	/// <summary>
	/// world_wof E45 (2026-10-18): after a resume the planner knew the house only as one that stood (HouseSite null); the Guide
	/// dead, the Nurse in its one room, and "NPC rooms are built onto a house of the bot's own" left the town with no free room for
	/// six days. A house that stood gets rooms built near it too.
	/// </summary>
	[Fact]
	public void TheGuideDead_InAHouseThatStood_ARoomIsBuiltNearIt()
	{
		FakeGame g = EarlyGameTests.Field(90);
		BuildingTests.ForeignHouse(g);
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Housing);
		p.SeeStationsForTest(ctx);
		Assert.Null(p.ResolveForTest(new HouseBuilt("house"), ctx));
		TilePos home = p.FoundHome.Value;
		Assert.Null(p.HouseSite);
		g.Homes.Add(new TownNpcHome(18, "Nurse", false, home));
		g.AliveTownNpcs.Remove(FakeGame.GuideNpc);
		GiveRoomMaterials(g, 1);
		g.Refresh(ctx);
		ISkill next = p.ResolveForTest(new TownNpcAlive(FakeGame.GuideNpc, "the Guide"), ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsNotType<WaitSkill>(next);
		Assert.Contains(((TestLog)ctx.Log).Lines, s => s.Contains("building room 1 at"));
	}

	[Fact]
	public void WhenTheNextMovesIn_AnotherRoomGoesOnTheRow()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		GiveRoomMaterials(g, 2);
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 1 && brain.Current == null, 60 * 60 * 20);
		TilePos first = Assert.Single(brain.Planner.NpcRooms).Origin;
		// The Merchant moves into the new room: none empty now, so one more (the next NPC needs a free room to come).
		const int Merchant = 17;
		g.Homes.Add(Living(Merchant, "Merchant", first));
		ctx.Tick += ProgressionPlanner.NpcSurveyTicks; // the survey is kept a little while
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 2 && brain.Current == null, 60 * 60 * 20);
		Assert.Equal(2, brain.Planner.NpcRooms.Count);
		Assert.Equal(new TilePos(first.X + Blueprint.NpcRoomStep, house.Y), brain.Planner.NpcRooms[1].Origin);
		Assert.Contains(((TestLog)ctx.Log).Lines, s => s.Contains("2 rooms (2 valid), 2 occupied (Guide, Merchant), 0 empty") && s.Contains("building room 2"));
		foreach (var (o, shared) in brain.Planner.NpcRooms) {
			Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, shared);
			Assert.Null(g.CheckHousing(o.X + bp.Inside.X, o.Y + bp.Inside.Y));
		}
	}

	/// <summary>
	/// world_wof E46/E47 (2026-10-18): room 1 was one room with the house as the game floods it; the NPC living there had his home
	/// outside room 1's plan, so the survey called room 1 empty and the bot waited six days for a Guide with no free room. The
	/// game's room bounds count, and two cells in one game room are one room.
	/// </summary>
	[Fact]
	public void ARoomJoinedToTheHouse_IsOneRoom_AndItsOccupantCounts()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		GiveRoomMaterials(g, 2);
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 1 && brain.Current == null, 60 * 60 * 20);
		TilePos first = Assert.Single(brain.Planner.NpcRooms).Origin;
		// The shared wall opened: the house and room 1 are one room now, the Guide's home in the house part.
		for (int y = first.Y + 1; y < first.Y + 5; y++)
			g.Grid.Set(first.X, y, TerraBot.Core.Grid.CellKind.Empty);
		Assert.NotNull(g.RoomAt(house.X + 5, house.Y + 2));
		ctx.Tick += ProgressionPlanner.NpcSurveyTicks;
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 2 && brain.Current == null, 60 * 60 * 20);
		var log = ((TestLog)ctx.Log).Lines;
		foreach (string l in log.Where(l => l.StartsWith("NPC housing")))
			output.WriteLine(l);
		Assert.Contains(log, l => l.Contains("1 rooms (1 valid), 1 occupied (Guide), 0 empty") && l.Contains("building room 2"));
	}

	/// <summary>world_wof E48 (2026-10-18): 42 Wood Walls and 5 Wood carried, no tree in reach: the wood is crafted back from the walls.</summary>
	[Fact]
	public void WoodShort_NoTrees_TheCarriedWallsAreCraftedBackIntoWood()
	{
		var (g, ctx, brain, _) = Settled();
		g.Add(new RecipeInfo { Result = FakeGame.Wood, Ingredients = new[] { new Ingredient(4, FakeGame.WoodWall) }, Stations = new[] { FakeGame.TileBench } });
		g.Items[FakeGame.Wood] = 5;
		g.Items[FakeGame.WoodWall] = 42;
		g.Refresh(ctx);
		brain.Planner.SeeStationsForTest(ctx);
		ISkill next = brain.Planner.ResolveForTest(new HaveItem(FakeGame.Wood, 8, "Wood"), ctx);
		output.WriteLine($"{brain.Planner.Status} -> {next?.Describe()}");
		Assert.IsType<CraftSkill>(next);
		Assert.StartsWith("crafting 3 ", brain.Planner.Status);
		// Walls another need has claimed are not crafted away: with none to spare, the trees.
		g.Items[FakeGame.WoodWall] = 3;
		g.Refresh(ctx);
		next = brain.Planner.ResolveForTest(new HaveItem(FakeGame.Wood, 8, "Wood"), ctx);
		Assert.IsType<GatherWoodSkill>(next);
	}

	/// <summary>world_wof E51 (2026-10-18): 10 of 24 Wood made the room's walls, the door 4 Wood short. Stone in the bag makes them.</summary>
	[Fact]
	public void TheRoomsWalls_AreStone_WhenStoneIsCarried()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		g.Items[FakeGame.Wood] = 18; // the table, chair and door; none for walls
		g.Items[FakeGame.Gel] = 2;
		g.Items[FakeGame.DirtBlock] = 100;
		g.Items[FakeGame.StoneBlock] = 100; // the frame's block claim counts as stone first (claims are by the first accepted item)
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 1 && brain.Current == null, 60 * 60 * 20);
		var room = Assert.Single(brain.Planner.NpcRooms);
		Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, room.Shared);
		Assert.Null(g.CheckHousing(room.Origin.X + bp.Inside.X, room.Origin.Y + bp.Inside.Y));
		Assert.Equal(0, g.Count(FakeGame.WoodWall));
		Assert.True(g.Walls.Count > 0 && g.Count(FakeGame.StoneBlock) < 100, $"stone left {g.Count(FakeGame.StoneBlock)}");
	}

	/// <summary>
	/// world_wof E52 (2026-10-18): the room's walls (Stone or Wood Wall) claimed as Stone Wall only, so the Wood Walls they counted
	/// looked free: crafted into Wood for the door, then the walls crafted back, for six game days. Claims are by the item counted.
	/// </summary>
	[Fact]
	public void WallsCountedForTheRoom_AreNotCraftedIntoWood()
	{
		var (g, ctx, brain, _) = Settled();
		g.Add(new RecipeInfo { Result = FakeGame.Wood, Ingredients = new[] { new Ingredient(4, FakeGame.WoodWall) }, Stations = new[] { FakeGame.TileBench } });
		g.Items[FakeGame.Wood] = 4;
		g.Items[FakeGame.WoodWall] = 42;
		g.Refresh(ctx);
		brain.Planner.SeeStationsForTest(ctx);
		ISkill next = brain.Planner.ResolveNeeds(new Need[] {
			new HaveItem(new[] { FakeGame.StoneWall, FakeGame.WoodWall }, 38, "walls"),
			new HaveItem(FakeGame.Wood, 6, "Wood"),
		}, ctx, out _);
		output.WriteLine($"{brain.Planner.Status} -> {next?.Describe()}");
		Assert.False(next is CraftSkill c && c.Describe().Contains($"item {FakeGame.Wood} "), brain.Planner.Status);
		Assert.IsNotType<CraftSkill>(next);
	}

	/// <summary>world_wof E53 (2026-10-18): the Wooden Chair 4 Wood short for days, Ebonwood trees in sight. Any wood's chair does.</summary>
	[Fact]
	public void NoWoodForTheChair_AnEbonwoodChairIsMade()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		g.Items[FakeGame.Wood] = 14; // the table and the door; none for the chair
		g.Items[FakeGame.Ebonwood] = 4;
		g.Items[FakeGame.Gel] = 2;
		g.Items[FakeGame.DirtBlock] = 100;
		g.Items[FakeGame.StoneBlock] = 100;
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 1 && brain.Current == null, 60 * 60 * 20);
		var room = Assert.Single(brain.Planner.NpcRooms);
		Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, room.Shared);
		Assert.Null(g.CheckHousing(room.Origin.X + bp.Inside.X, room.Origin.Y + bp.Inside.Y));
		Assert.Equal(0, g.Count(FakeGame.Ebonwood));
	}

	/// <summary>world_wof E55 (2026-10-18): no Wood tree anywhere known, Ebonwood trees in sight: the chair is made of Ebonwood.</summary>
	[Fact]
	public void OnlyEbonwoodTreesKnown_TheChairIsEbonwood()
	{
		var (g, ctx, brain, house) = Settled();
		var tree = new TilePos(60, 24);
		g.Trees[tree] = 6;
		g.TreeWoods[tree] = FakeGame.Ebonwood;
		g.Refresh(ctx);
		brain.Planner.SeeStationsForTest(ctx);
		// The search for plain Wood came to nothing.
		brain.Planner.OnSkillFinished(new GatherWoodSkill(FakeGame.Wood, 4), SkillStatus.Failed, ctx);
		ISkill next = brain.Planner.ResolveForTest(new HaveItem(new[] { FakeGame.Chair, FakeGame.EbonwoodChair }, 1, "chair"), ctx);
		output.WriteLine($"{brain.Planner.Status} -> {next?.Describe()}");
		Assert.IsType<GatherWoodSkill>(next);
		Assert.Equal(FakeGame.Ebonwood, ((GatherWoodSkill)next).ItemType);
	}

	/// <summary>world_wof E57 (2026-10-18): every wood searched for in vain, 168 Sand carried: the chair is glass (Sand to Glass at a furnace).</summary>
	[Fact]
	public void NoWoodAnywhere_TheChairIsGlass_FromCarriedSand()
	{
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		var furnace = new TilePos(house.X + 6, house.Y + 4);
		g.Stations[FakeGame.TileFurnace] = furnace;
		g.Furniture[furnace] = FakeGame.TileFurnace;
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx);
		g.Items[FakeGame.Wood] = 14; // the table and the door
		g.Items[FakeGame.SandBlock] = 10;
		g.Items[FakeGame.Gel] = 2;
		g.Items[FakeGame.DirtBlock] = 100;
		g.Items[FakeGame.StoneBlock] = 100;
		RunUntil(g, ctx, brain, () => brain.Planner.NpcRooms.Count == 1 && brain.Current == null, 60 * 60 * 20);
		var room = Assert.Single(brain.Planner.NpcRooms);
		Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, room.Shared);
		Assert.Null(g.CheckHousing(room.Origin.X + bp.Inside.X, room.Origin.Y + bp.Inside.Y));
		Assert.True(g.Count(FakeGame.SandBlock) <= 2, $"sand left {g.Count(FakeGame.SandBlock)}");
	}

	[Fact]
	public void TwoCanCome_AndTheHouseIsEmpty_OneRoomMore()
	{
		var (g, ctx, brain, _) = Settled();
		g.Comers.Add((FakeGame.GuideNpc, "Guide"));
		g.Comers.Add((17, "Merchant"));
		GiveRoomMaterials(g, 1);
		ISkill next = null;
		for (int i = 0; i < 400 && next is not BuildSkill; i++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
			next = brain.Current;
		}
		output.WriteLine(brain.Planner.Status);
		Assert.Contains(((TestLog)ctx.Log).Lines, s => s.Contains("0 occupied, 1 empty; 0 homeless, 2 can come (Guide, Merchant)") && s.Contains("building room 1"));
	}

	[Fact]
	public void TheBossPlans_HouseTheTown_AfterTheArmour_BeforeTheBossPrep()
	{
		foreach (string plan in new[] { ProgressionPlanner.Plans.Boss, ProgressionPlanner.Plans.EvilBoss, ProgressionPlanner.Plans.Skeletron, ProgressionPlanner.Plans.WallOfFlesh }) {
			var stages = ProgressionPlanner.StagesFor(plan, FakeGame.Ids).ToList();
			int housing = stages.FindIndex(n => n is NpcHousing), greaves = stages.FindIndex(n => n.Label == "greaves"), prep = stages.FindIndex(n => n is PrepFor);
			Assert.True(greaves >= 0 && greaves < housing && housing < prep, $"{plan}: greaves {greaves}, NPC housing {housing}, first prep {prep}");
			Assert.Single(stages, n => n is NpcHousing);
		}
		Assert.DoesNotContain(ProgressionPlanner.StagesFor(ProgressionPlanner.Plans.Settle, FakeGame.Ids), n => n is NpcHousing);
	}

	[Fact]
	public void AStationBesideTheHouse_TheRoomGoesOnItsOtherSide()
	{
		// A work bench outside the house's right wall (where the room would go: SiteFinder rules out a station in a room):
		// the room goes on the left, onto the house's door wall (its door then serves both).
		FakeGame g = EarlyGameTests.FieldFrom(100, 45);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Housing);
		BuildingTests.HouseAlreadyThere(g, brain.Planner, ctx);
		TilePos house = brain.Planner.HouseSite.Value;
		var bench = new TilePos(house.X + 15, house.Y + 4);
		g.Stations[FakeGame.TileBench] = bench;
		g.Furniture[bench] = g.Furniture[new TilePos(bench.X + 1, bench.Y)] = FakeGame.TileBench;
		for (int i = 0; i < 20; i++)
			g.Refresh(ctx);
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		GiveRoomMaterials(g, 1);
		RunUntil(g, ctx, brain, () => brain.Planner.Complete && brain.Current == null, 60 * 60 * 20);
		var room = Assert.Single(brain.Planner.NpcRooms);
		Assert.Equal(new TilePos(house.X - Blueprint.NpcRoomStep, house.Y), room.Origin);
		Assert.Equal(1, room.Shared);
		Blueprint bp = Blueprint.NpcRoom(FakeGame.Ids, 1);
		Assert.Null(g.CheckHousing(room.Origin.X + bp.Inside.X, room.Origin.Y + bp.Inside.Y));
		Assert.Null(g.CheckHousing(house.X + 5, house.Y + 2));
	}

	[Fact]
	public void ARoomThatCannotBeHad_IsLeftForADay_AndThePlanGoesOn()
	{
		// No wood, no trees, no gel: the room's materials cannot be had here. The stage gives up for a day after its skills fail,
		// and the plan is not stuck on it (the boss plan must not wait on NPC housing).
		var (g, ctx, brain, house) = Settled();
		g.Homes.Add(Living(FakeGame.GuideNpc, "Guide", house));
		g.Items[FakeGame.Wood] = 0;
		RunUntil(g, ctx, brain, () => brain.Planner.Complete && brain.Current == null, 60 * 60 * 30);
		var log = ((TestLog)ctx.Log).Lines;
		Assert.Contains(log, s => s.Contains("NPC housing:") && s.Contains("left for a day"));
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		Assert.Empty(brain.Planner.NpcRooms);
	}
}
