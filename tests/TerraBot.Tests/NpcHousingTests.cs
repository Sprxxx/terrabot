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
