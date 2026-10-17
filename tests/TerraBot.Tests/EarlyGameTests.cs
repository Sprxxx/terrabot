using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>The early-game chain (Milestone 4) in <see cref="FakeGame"/>: wood, work bench, wooden sword, stockpile.</summary>
public class EarlyGameTests
{
	private readonly ITestOutputHelper output;
	public EarlyGameTests(ITestOutputHelper output) => this.output = output;

	/// <summary>A flat field: feet row 24 on 5 rows of dirt, trees at the given columns, start at column 3.</summary>
	public static FakeGame Field(int width, params int[] treeColumns)
	{
		var rows = Enumerable.Repeat(new string('.', width), 25).ToArray();
		char[] feet = rows[24].ToCharArray();
		feet[3] = 'S';
		foreach (int x in treeColumns)
			feet[x] = 'T';
		rows[24] = new string(feet);
		return new FakeGame(rows.Concat(Enumerable.Repeat(new string('#', width), 5)).ToArray());
	}

	public static SkillStatus RunSkill(FakeGame game, BotContext ctx, ISkill skill, int maxTicks)
	{
		skill.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < maxTicks && s == SkillStatus.Running; t++) {
			game.Refresh(ctx);
			s = skill.Tick(ctx);
			game.Step(ctx);
		}
		return s;
	}

	[Fact]
	public void GatherWood_ChopsAndCollects()
	{
		FakeGame game = Field(80, 20, 35);
		BotContext ctx = game.CreateContext(output);
		SkillStatus s = RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 12), 6000);
		output.WriteLine($"{s}, wood {game.Count(FakeGame.Wood)}, trees left {game.Trees.Count}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(game.Count(FakeGame.Wood) >= 12);
	}

	[Fact]
	public void GatherWood_IgnoresFarDrops_InsteadOfLoopingOnThem()
	{
		// A wood drop 40 tiles away is out of the collect radius: chop the near tree instead of restarting a
		// collect that finishes at once (a livelock seen in the settle-chain test).
		FakeGame game = Field(80, 12);
		game.Drops.Add(new DropInfo(1, FakeGame.Wood, "Wood", 1, 70 * 16f, 24 * 16f, 8, 8));
		BotContext ctx = game.CreateContext(output);
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 5), 3000));
	}

	/// <summary>A flat field like <see cref="Field"/> but starting at column <paramref name="start"/>.</summary>
	public static FakeGame FieldFrom(int width, int start, params int[] treeColumns)
	{
		var rows = Enumerable.Repeat(new string('.', width), 25).ToArray();
		char[] feet = rows[24].ToCharArray();
		feet[start] = 'S';
		foreach (int x in treeColumns)
			feet[x] = 'T';
		rows[24] = new string(feet);
		return new FakeGame(rows.Concat(Enumerable.Repeat(new string('#', width), 5)).ToArray());
	}

	[Fact]
	public void GatherWood_WithNoTreeInSight_GoesBackToOneSeenEarlier()
	{
		// Seen in game (measured run U8): 250 tiles from the trees by the house, it knew of none and explored to and fro.
		FakeGame game = FieldFrom(300, 200, 40);
		game.TreeSight = 50;
		BotContext ctx = game.CreateContext(output);
		ctx.Seen = new Surroundings { Trees = new[] { new Core.Grid.TilePos(40, 24) } };
		SkillStatus s = RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 5), 12000);
		output.WriteLine($"{s}, wood {game.Count(FakeGame.Wood)}, at {game.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}

	[Fact]
	public void GatherWood_WithNoTreeKnown_ExploresFurtherOutEachTime()
	{
		// Trees 100 columns off and seen from 50: stepping 40 to and fro from where it stood never got there (U8).
		FakeGame game = FieldFrom(400, 150, 250);
		game.TreeSight = 50;
		BotContext ctx = game.CreateContext(output);
		SkillStatus s = RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 5), 20000);
		output.WriteLine($"{s}, wood {game.Count(FakeGame.Wood)}, at {game.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}

	[Fact]
	public void GatherWood_Underground_WithNoWayUp_GivesUp_InsteadOfTryingForever()
	{
		// A cave sealed in unbreakable blocks far under the surface: the walk up fails, and the next tick built the same walk
		// again, without end.
		var rows = Enumerable.Repeat(new string('@', 40), 30).ToArray();
		rows[26] = rows[27] = "@@@@@" + new string('.', 20) + new string('@', 15);
		rows[28] = "@@@@@....." + "S" + new string('.', 14) + new string('@', 15);
		FakeGame game = new FakeGame(rows);
		BotContext ctx = game.CreateContext(output);
		ctx.SurfaceY = 5;
		var wood = new GatherWoodSkill(FakeGame.Wood, 5);
		SkillStatus s = RunSkill(game, ctx, wood, 60 * 60);
		output.WriteLine($"{s} after {game.Sim.Ticks} ticks: {wood.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
	}

	[Fact]
	public void GatherWood_ARememberedTreeWithNoWayThere_IsNotWalkedToAgain_ByAFreshSkill()
	{
		// The bot shut in an unbreakable box, a tree it remembers 150 columns off (out of sight): no way there.
		FakeGame game = FieldFrom(300, 100, 250);
		game.TreeSight = 50;
		for (int x = 90; x <= 110; x++)
			for (int y = 17; y < 30; y++)
				if (x == 90 || x == 110 || y == 17 || y >= 25) { // walls, roof, and the ground under it (no digging out)
					game.Grid.Set(x, y, Core.Grid.CellKind.Solid);
					game.Blocks.Unbreakable.Add(new Core.Grid.TilePos(x, y));
				}
		var tree = new Core.Grid.TilePos(250, 24);
		BotContext ctx = game.CreateContext(output);
		ctx.Seen = new Surroundings { Trees = new[] { tree } };
		var first = new GatherWoodSkill(FakeGame.Wood, 5);
		first.Start(ctx);
		bool walked = false;
		for (int t = 0; t < 60 * 60 && first.Tick(ctx) == SkillStatus.Running; t++) {
			bool walking = first.State.StartsWith("walking to a tree");
			if (walked && !walking)
				break; // that walk is over
			walked |= walking;
			game.Step(ctx);
			game.Refresh(ctx);
		}
		first.Stop(ctx);
		output.WriteLine($"first: {first.Describe()}, at {game.Sim.Node}");
		Assert.True(walked);
		var fresh = new GatherWoodSkill(FakeGame.Wood, 5);
		fresh.Start(ctx);
		fresh.Tick(ctx);
		output.WriteLine($"fresh: {fresh.Describe()}");
		Assert.False(fresh.State.StartsWith("walking to a tree"), fresh.State);
	}

	[Fact]
	public void GatherWood_ARememberedTreeThatIsNotThere_IsForgotten_NotWalkedToEveryTick()
	{
		// Measured run U9: respawned on a remembered tree's spot with no tree there, "back to one seen at" every tick.
		FakeGame game = FieldFrom(300, 200, 40);
		game.TreeSight = 50;
		BotContext ctx = game.CreateContext(output);
		ctx.KnownTrees.Add(ctx.Node); // stale: no tree stands here
		var skill = new GatherWoodSkill(FakeGame.Wood, 5);
		skill.Start(ctx);
		for (int t = 0; t < 5; t++) {
			skill.Tick(ctx); // no refresh: the scan is stale, as in the 20 ticks between game scans
			game.Step(ctx);
		}
		Assert.DoesNotContain(ctx.Node, ctx.KnownTrees);
	}

	[Fact]
	public void KnownTrees_AreForgotten_WhenTheirPlaceIsInSightWithoutThem()
	{
		var ctx = new BotContext();
		ctx.Seen = new Surroundings { Trees = new[] { new Core.Grid.TilePos(10, 20), new Core.Grid.TilePos(200, 20) }, Origin = new Core.Grid.TilePos(10, 20) };
		ctx.Seen = new Surroundings { Trees = System.Array.Empty<Core.Grid.TilePos>(), Origin = new Core.Grid.TilePos(15, 22) };
		Assert.Equal(new[] { new Core.Grid.TilePos(200, 20) }, ctx.KnownTrees);
	}

	/// <summary>
	/// Measured run U28 (world_eye): Wooden Arrows' wood (any wood) gathered beside the Corruption's trees, which drop
	/// Ebonwood; only Wood counted and only Wood drops were fetched, "GatherWood start: have 4" for the last 30 game hours.
	/// </summary>
	[Fact]
	public void AnyWoodNeed_AmongEbonwoodTrees_GathersEbonwood_AndCountsIt()
	{
		FakeGame game = Field(80, 12, 20, 28);
		foreach (var t in game.Trees.Keys.ToList())
			game.TreeWoods[t] = FakeGame.Ebonwood;
		game.Items[FakeGame.StoneBlock] = 50;
		BotContext ctx = game.CreateContext(output);
		var planner = new ProgressionPlanner();
		ISkill skill = planner.ResolveForTest(new HaveItem(FakeGame.WoodenArrow, 100, "arrows"), ctx);
		output.WriteLine($"plan: {planner.Status}");
		var wood = Assert.IsType<GatherWoodSkill>(skill);
		SkillStatus s = RunSkill(game, ctx, wood, 6000);
		output.WriteLine($"{s}: {wood.Describe()}, wood {game.Count(FakeGame.Wood)}, ebonwood {game.Count(FakeGame.Ebonwood)}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(game.Count(FakeGame.Ebonwood) >= 4, $"ebonwood {game.Count(FakeGame.Ebonwood)}");
		// And the planner, asked again, has the arrows' wood: on to the work bench for the craft (its own 10 Wood, no group).
		planner.ResolveForTest(new HaveItem(FakeGame.WoodenArrow, 100, "arrows"), ctx);
		output.WriteLine($"then: {planner.Status}");
		Assert.DoesNotContain("or other wood", planner.Status);
	}

	/// <summary>
	/// A need only Wood does (a Wooden Door: no recipe group) beside Ebonwood trees: those are passed by for the Wood trees
	/// beyond, not felled for wood that does not count.
	/// </summary>
	[Fact]
	public void WoodOnlyNeed_PassesEbonwoodTrees_ForWoodOnes()
	{
		FakeGame game = Field(80, 8, 12, 40, 48);
		game.TreeWoods[new Core.Grid.TilePos(8, 24)] = FakeGame.Ebonwood;
		game.TreeWoods[new Core.Grid.TilePos(12, 24)] = FakeGame.Ebonwood;
		BotContext ctx = game.CreateContext(output);
		SkillStatus s = RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 12), 8000);
		output.WriteLine($"{s}, wood {game.Count(FakeGame.Wood)}, ebonwood {game.Count(FakeGame.Ebonwood)}, trees left {string.Join(" ", game.Trees.Keys)}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Contains(new Core.Grid.TilePos(8, 24), game.Trees.Keys);
		Assert.Contains(new Core.Grid.TilePos(12, 24), game.Trees.Keys);
	}

	/// <summary>
	/// A drop the game would not pick up (an Encumbering Stone carried: Player.CanAcceptItemIntoInventory) is not walked to
	/// and stood on. Measured run U28: "WalkTo reached StandAt(1805,286) at (1805,286)" 62 times in a row after a fight.
	/// </summary>
	[Fact]
	public void Collect_ADropThatWouldNotBePickedUp_IsNotWalkedTo()
	{
		FakeGame game = Field(80);
		game.Items[FakeGame.EncumberingStone] = 1;
		game.Drops.Add(new DropInfo(1, FakeGame.Gel, "Gel", 3, 10 * 16f, 24 * 16f, 8, 8));
		BotContext ctx = game.CreateContext(output);
		var collect = new CollectDropsSkill(_ => true);
		Core.Grid.TilePos start = game.Sim.Node;
		SkillStatus s = RunSkill(game, ctx, collect, 600);
		output.WriteLine($"{s} after {game.Sim.Ticks} ticks at {game.Sim.Node}: {collect.Describe()}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(start, game.Sim.Node);
		Assert.Single(game.Drops);
	}

	/// <summary>
	/// Measured run U28: an Encumbering Stone looted from a chest at t465000, and no count in the bag rose again for the
	/// 400000 ticks left. Carried, it is trashed, and the wood comes in.
	/// </summary>
	[Fact]
	public void AnEncumberingStone_IsTrashed_SoTheWoodIsPickedUp()
	{
		FakeGame game = Field(80, 12, 20, 28);
		game.Items[FakeGame.EncumberingStone] = 1;
		BotContext ctx = game.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		for (int t = 0; t < 60 * 30; t++) {
			game.Refresh(ctx);
			ctx.Tick = t;
			brain.Tick(ctx);
			game.Step(ctx);
		}
		output.WriteLine($"{brain.Describe()}; wood {game.Count(FakeGame.Wood)}, stone {game.Count(FakeGame.EncumberingStone)}");
		Assert.Equal(0, game.Count(FakeGame.EncumberingStone));
		Assert.True(game.Count(FakeGame.Wood) + game.Count(FakeGame.WorkBench) > 0);
	}

	[Fact]
	public void GatherWood_NoAxe_Fails()
	{
		FakeGame game = Field(80, 20);
		BotContext ctx = game.CreateContext(output);
		ctx.AxePower = 0;
		Assert.Equal(SkillStatus.Failed, RunSkill(game, ctx, new GatherWoodSkill(FakeGame.Wood, 5), 2000));
	}

	[Fact]
	public void PlaceItem_PutsBenchBesideTheBot()
	{
		FakeGame game = Field(40);
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.WorkBench] = 1;
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, new PlaceItemSkill(FakeGame.WorkBench), 600));
		Assert.True(game.Stations.ContainsKey(FakeGame.TileBench));
	}

	[Fact]
	public void PlaceItem_WalksToFreeFloor_WhenBesideIsTaken()
	{
		// Like the in-game failure: a station on each side of the bot and the floor beyond dug away.
		FakeGame game = Field(40);
		foreach (int x in new[] { 0, 1, 6, 7 })
			game.Grid.Set(x, 24, Core.Grid.CellKind.Platform);
		for (int x = 8; x <= 13; x++)
			game.Grid.Set(x, 25, Core.Grid.CellKind.Empty);
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.Furnace] = 1;
		var place = new PlaceItemSkill(FakeGame.Furnace);
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, place, 60 * 20));
		output.WriteLine($"placed at {place.PlacedAt} after {game.Sim.Ticks} ticks, out of range {game.Sim.OutOfRange}");
		Assert.True(game.Stations.ContainsKey(FakeGame.TileFurnace));
	}

	/// <summary>Bumpy ground (every other column a step up) for 45 columns: no 3-wide flat floor near the start.</summary>
	private static FakeGame Bumpy()
	{
		FakeGame game = Field(110);
		for (int x = 0; x <= 45; x += 2)
			game.Grid.Set(x, 24, Core.Grid.CellKind.Solid);
		game.Grid.Set(3, 24, Core.Grid.CellKind.Empty);
		game.Grid.Set(4, 24, Core.Grid.CellKind.Empty);
		game.Grid.Set(5, 24, Core.Grid.CellKind.Solid); // the start gap is 2 wide: the 3-wide furnace never fits in it
		return game;
	}

	[Fact]
	public void PlaceItem_LooksElsewhere_WhenNoRoomCanBeMadeNearby()
	{
		// The bumps cannot be mined (as with a weak pickaxe): no pocket; flat ground beyond them.
		FakeGame game = Bumpy();
		for (int x = 0; x <= 45; x++)
			game.Blocks.Unbreakable.Add(new Core.Grid.TilePos(x, 24));
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.Furnace] = 1;
		var place = new PlaceItemSkill(FakeGame.Furnace);
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, place, 60 * 60));
		output.WriteLine($"placed at {place.PlacedAt} after {game.Sim.Ticks} ticks");
		Assert.True(place.PlacedAt.Value.X > 45);
	}

	[Fact]
	public void PlaceItem_DigsRoom_WhenNoFloorIsFlatNearby()
	{
		// Seen in game (measured run L1): underground on uneven cave floor, "no free floor within 20 tiles" 75 times and the
		// furnace placed a game day later, after walking about. A player mines out room for it where they stand.
		FakeGame game = Bumpy();
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.Furnace] = 1;
		var place = new PlaceItemSkill(FakeGame.Furnace);
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, place, 60 * 20));
		output.WriteLine($"placed at {place.PlacedAt} after {game.Sim.Ticks} ticks, mined {game.Sim.Broken}");
		Assert.True(System.Math.Abs(place.PlacedAt.Value.X - 3) <= 6, $"placed at {place.PlacedAt}");
		Assert.True(game.Sim.Broken > 0);
	}

	[Fact]
	public void PlaceItem_FillsTheFloor_UnderTheRoomItMakes()
	{
		// A shaft in every other column of the ground (one wide, all the way down): no 3-wide floor but where the bot
		// stands; blocks go into the shaft tops under the furnace.
		FakeGame game = Field(40);
		for (int x = 0; x < 40; x += 2)
			if (x != 4)
				for (int y = 25; y < 30; y++)
					game.Grid.Set(x, y, Core.Grid.CellKind.Empty);
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.Furnace] = 1;
		game.Items[FakeGame.StoneBlock] = 10;
		var place = new PlaceItemSkill(FakeGame.Furnace);
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, place, 60 * 20));
		output.WriteLine($"placed at {place.PlacedAt} after {game.Sim.Ticks} ticks, blocks placed {game.Sim.Placed}");
		Assert.True(System.Math.Abs(place.PlacedAt.Value.X - 3) <= 6, $"placed at {place.PlacedAt}");
		Assert.True(game.Sim.Placed > 0);
	}

	[Fact]
	public void Craft_ReportsMissingStation()
	{
		FakeGame game = Field(40);
		BotContext ctx = game.CreateContext(output);
		game.Items[FakeGame.Wood] = 20;
		var craft = new CraftSkill(FakeGame.WoodenSword);
		Assert.Equal(SkillStatus.Failed, RunSkill(game, ctx, craft, 100));
		Assert.Equal(CraftResult.NeedsStation, craft.LastResult);
	}

	[Fact]
	public void AutoBrain_RunsTheEarlyGameChain()
	{
		FakeGame game = Field(110, 15, 25, 35, 45, 55, 65, 75);
		BotContext ctx = game.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Early);
		int t = 0;
		for (; t < 60 * 120 && !(brain.Planner.Complete && brain.Current == null); t++) {
			game.Refresh(ctx);
			brain.Tick(ctx);
			game.Step(ctx);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | wood {game.Count(FakeGame.Wood)}, sword {game.Count(FakeGame.WoodenSword)}, bench placed {game.Stations.ContainsKey(FakeGame.TileBench)}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		Assert.True(game.Stations.ContainsKey(FakeGame.TileBench));
		Assert.Equal(1, game.Count(FakeGame.WoodenSword));
		Assert.True(game.Count(FakeGame.Wood) >= ProgressionPlanner.WoodStockpile);
	}
}
