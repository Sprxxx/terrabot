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
