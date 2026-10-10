using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 5: the recipe-driven planner, mining, hunting and the night shelter, in <see cref="FakeGame"/>.
/// </summary>
public class ProgressionTests
{
	private readonly ITestOutputHelper output;
	public ProgressionTests(ITestOutputHelper output) => this.output = output;

	/// <summary>Surface feet row 24 over 2 rows of dirt, stone below with an iron vein (rows 29-31, x 10-30), '@' floor.</summary>
	private static FakeGame Mine(int width = 90, params int[] treeColumns)
	{
		var rows = Enumerable.Repeat(new string('.', width), 25).ToList();
		char[] feet = rows[24].ToCharArray();
		feet[3] = 'S';
		foreach (int x in treeColumns)
			feet[x] = 'T';
		rows[24] = new string(feet);
		rows.Add(new string('#', width));
		rows.Add(new string('#', width));
		for (int y = 27; y < 37; y++) {
			char[] r = new string('s', width).ToCharArray();
			if (y is >= 29 and <= 31)
				for (int x = 10; x <= 30; x++)
					r[x] = 'i';
			rows.Add(new string(r));
		}
		rows.Add(new string('@', width));
		return new FakeGame(rows.ToArray());
	}

	private ISkill NextFor(FakeGame game, out ProgressionPlanner planner, out BotContext ctx, bool night = false)
	{
		game.IsNight = night;
		ctx = game.CreateContext(output);
		planner = new ProgressionPlanner();
		planner.Reset(ProgressionPlanner.Plans.Iron);
		ISkill next = planner.Next(ctx);
		output.WriteLine($"{planner.Status} -> {next?.Describe() ?? "null"}");
		return next;
	}

	private static TilePos Beside(FakeGame g, int dx) => new(g.Grid.Markers['S'].X + dx, 24);

	[Fact]
	public void EmptyInventory_GathersWoodForTheBench()
	{
		var skill = Assert.IsType<GatherWoodSkill>(NextFor(Mine(), out _, out _));
		Assert.Equal(10, skill.Amount);
	}

	[Fact]
	public void Furnace_StartsWithStone()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Items[FakeGame.WoodenSword] = 1;
		var skill = Assert.IsType<MineSkill>(NextFor(g, out _, out _));
		Assert.Equal(FakeGame.StoneBlock, skill.ItemType);
		Assert.Equal(FakeGame.TileStone, skill.TileType);
		Assert.Equal(20, skill.Amount);
	}

	[Fact]
	public void Torches_HuntGel_ThenClaimWoodSeparately()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.StoneBlock] = 20;
		g.Items[FakeGame.Wood] = 4; // exactly the furnace's 4: the torch needs one more
		var hunt = Assert.IsType<HuntSkill>(NextFor(g, out _, out _));
		Assert.Equal(FakeGame.Gel, hunt.ItemType);
		Assert.Equal(FakeGame.SlimeAi, hunt.AiStyle);

		g.Items[FakeGame.Gel] = 1;
		var wood = Assert.IsType<GatherWoodSkill>(NextFor(g, out _, out _));
		Assert.Equal(5, wood.Amount);

		g.Items[FakeGame.Wood] = 5;
		var craft = Assert.IsType<CraftSkill>(NextFor(g, out _, out _));
		Assert.Equal(FakeGame.Torch, craft.ItemType);
	}

	[Fact]
	public void Night_SheltersInsteadOfSurfaceGathering_ButStillMines()
	{
		Assert.IsType<ShelterSkill>(NextFor(Mine(), out _, out _, night: true));

		FakeGame g = Mine(); // fresh: stations are only seen on the first refresh
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Items[FakeGame.WoodenSword] = 1;
		Assert.IsType<MineSkill>(NextFor(g, out _, out _, night: true));
	}

	[Fact]
	public void Anvil_ChoosesTheWorldsOre()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		var skill = Assert.IsType<MineSkill>(NextFor(g, out ProgressionPlanner p, out _));
		Assert.Equal(FakeGame.IronOre, skill.ItemType); // lead does not exist in this world
		Assert.Equal(15, skill.Amount);                // 5 bars × 3 ore
		Assert.Contains("item11", p.Status);
	}

	[Fact]
	public void Pickaxe_SmeltsBarsAtTheFurnace()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -3);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		g.Items[FakeGame.Wood] = 3;
		var craft = Assert.IsType<CraftSkill>(NextFor(g, out _, out _));
		Assert.Equal(FakeGame.IronBar, craft.ItemType);
		Assert.Equal(10, craft.Count);
	}

	[Fact]
	public void Complete_WithAnIronTierPickaxe()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -3);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		Assert.Null(NextFor(g, out ProgressionPlanner p, out _));
		Assert.True(p.Complete, p.Status);
	}

	[Fact]
	public void Stuck_WhenNothingPlacesTheStation()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -3);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.Ids = new GameIds {
			Wood = FakeGame.Wood, WorkBenchItem = FakeGame.WorkBench, WorkBenchTile = FakeGame.TileBench, WoodenSword = FakeGame.WoodenSword,
			FurnaceTile = FakeGame.TileFurnace, AnvilTile = 999, IronTierPickaxe = FakeGame.IronPickaxe,
		};
		var p = new ProgressionPlanner();
		Assert.Null(p.Next(ctx));
		Assert.True(p.Stuck);
		Assert.False(p.Complete);
		output.WriteLine(p.Status);
	}

	[Fact]
	public void MineSkill_DigsDownAndMinesOre_InToolRange()
	{
		FakeGame g = Mine();
		BotContext ctx = g.CreateContext(output);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 6), 60 * 120);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks: ore {g.Count(FakeGame.IronOre)}, broken {g.Sim.Broken}, out of range {g.Sim.OutOfRange}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Count(FakeGame.IronOre) >= 6);
		Assert.Equal(0, g.Sim.Undermined);
	}

	[Fact]
	public void MineSkill_GivesUpQuickly_WhenNothingIsReachable()
	{
		FakeGame g = Mine();
		BotContext ctx = g.CreateContext(output);
		ctx.AllowBreak = false; // the ore is under 2 rows of dirt and 2 of stone: out of reach without digging
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 6);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, mine, 60 * 60);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks: {mine.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("digging is off", mine.FailureReason);
		Assert.True(g.Sim.Ticks < 60 * 15, $"took {g.Sim.Ticks} ticks to give up");
	}

	[Fact]
	public void Shelter_DigsSealsWaitsAndOpens()
	{
		FakeGame g = Mine();
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		var shelter = new ShelterSkill();
		shelter.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int sealedAt = -1;
		for (int t = 0; t < 60 * 60 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = shelter.Tick(ctx);
			g.Step(ctx);
			if (shelter.Sealed && sealedAt < 0) {
				sealedAt = t;
				TilePos n = g.Sim.Node;
				output.WriteLine($"sealed at t{t}, node {n}");
				Assert.True(g.Grid.Get(n.X, n.Y - 3).IsSolid() && g.Grid.Get(n.X + 1, n.Y - 3).IsSolid());
				Assert.True(n.Y >= 28, "should be well below the surface");
			}
			if (sealedAt >= 0 && t == sealedAt + 300)
				g.IsNight = false;
		}
		Assert.True(sealedAt >= 0, "never sealed in");
		Assert.Equal(SkillStatus.Succeeded, s);
		TilePos end = g.Sim.Node;
		Assert.False(g.Grid.Get(end.X, end.Y - 3).IsSolid(), "seal not opened");
	}

	[Fact]
	public void AutoBrain_RunsTheIronChain()
	{
		FakeGame g = Mine(90, 40, 50, 60, 70, 80);
		g.AddSlime(7, 30, 24);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Iron);
		int t = 0;
		for (; t < 60 * 60 * 15 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		string inv = string.Join(", ", g.Items.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"));
		output.WriteLine($"after {t} ticks: {brain.Describe()} | stations {string.Join(",", g.Stations.Keys)} | inv {inv} | out of range {g.Sim.OutOfRange}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status + " / " + brain.LastResult);
		Assert.Equal(1, g.Count(FakeGame.IronPickaxe));
		Assert.True(g.Stations.ContainsKey(FakeGame.TileFurnace) && g.Stations.ContainsKey(FakeGame.TileAnvil));
	}
}
