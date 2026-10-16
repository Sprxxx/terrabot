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

	/// <summary>Surface feet row 24 over 2 rows of dirt, stone rows 27-36 with an iron vein (rows 29.., x 10..), '@' floor.</summary>
	public static FakeGame Mine(int width = 90, int oreRows = 3, int veinWidth = 21, int[] treeColumns = null, int sky = 25)
	{
		treeColumns ??= System.Array.Empty<int>();
		var rows = Enumerable.Repeat(new string('.', width), sky).ToList();
		char[] feet = rows[sky - 1].ToCharArray();
		feet[3] = 'S';
		foreach (int x in treeColumns)
			feet[x] = 'T';
		rows[sky - 1] = new string(feet);
		rows.Add(new string('#', width));
		rows.Add(new string('#', width));
		for (int y = sky + 2; y < sky + 12; y++) {
			char[] r = new string('s', width).ToCharArray();
			if (y >= sky + 4 && y < sky + 4 + oreRows)
				for (int x = 10; x < 10 + veinWidth; x++)
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
		// 20 for the furnace plus a margin: stone is also what the bot builds with on the way back (seen in game).
		Assert.Equal(20 + ProgressionPlanner.BlockMargin, skill.Amount);
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
	public void Night_Armoured_GathersWoodInsteadOfSheltering()
	{
		// The boss scenario: iron armour and 200 life, dug in for the whole night over 1 wood.
		FakeGame g = Mine();
		g.IsNight = true;
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.Defense = ProgressionPlanner.NightOutDefense + 1;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Iron);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsType<GatherWoodSkill>(next);
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
	public void CraftThatFindsNoStation_WalksUpToItFirst()
	{
		// Seen in game: in a hole below the furnace the planner thought it near, the game did not; craft, fail, repeat.
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -3);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		g.Items[FakeGame.Wood] = 3;
		g.StationRange = 1; // the game wants the bot closer than the planner thought
		var craft = Assert.IsType<CraftSkill>(NextFor(g, out ProgressionPlanner p, out BotContext ctx));
		craft.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int i = 0; i < 200 && s == SkillStatus.Running; i++) {
			g.Refresh(ctx);
			s = craft.Tick(ctx);
		}
		Assert.Equal(SkillStatus.Failed, s);
		p.OnSkillFinished(craft, s, ctx);
		Assert.IsType<WalkToSkill>(p.Next(ctx));
	}

	[Fact]
	public void StationItCannotGetBackTo_IsForgottenAndMadeAgain()
	{
		// world_eye run 22: 57 tiles below the furnace after a fight, every search for a way back failed.
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -40);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		g.Items[FakeGame.Wood] = 3;
		var walk = Assert.IsType<WalkToSkill>(NextFor(g, out ProgressionPlanner p, out BotContext ctx));
		p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		walk = Assert.IsType<WalkToSkill>(p.Next(ctx));
		p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		g.Stations.Remove(FakeGame.TileFurnace); // out of sight, as in game
		for (int i = 0; i < 21; i++)
			g.Refresh(ctx); // the fake rescans stations every 20 refreshes
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsNotType<WalkToSkill>(next); // working towards a new furnace (stone first), not another walk
	}

	[Fact]
	public void NoWayBack_WithARecallPotion_DrinksIt()
	{
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -40);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		g.Items[FakeGame.Wood] = 3;
		g.Items[FakeGame.RecallPotion] = 2;
		var walk = Assert.IsType<WalkToSkill>(NextFor(g, out ProgressionPlanner p, out BotContext ctx));
		p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		walk = Assert.IsType<WalkToSkill>(p.Next(ctx));
		p.OnSkillFinished(walk, SkillStatus.Failed, ctx);
		var use = Assert.IsType<UseItemSkill>(p.Next(ctx));
		Assert.Equal(FakeGame.RecallPotion, use.ItemType);
	}

	[Fact]
	public void ARecallRequested_AfterWalksKeptFailing_IsDrunkNext_OnlyIfCarried()
	{
		// world_eye run 62: 120 rows down, every walk to the surface failed for a game hour with 3 recall potions carried.
		FakeGame g = Mine();
		g.Items[FakeGame.WoodenSword] = 1;
		var first = NextFor(g, out ProgressionPlanner p, out BotContext ctx);
		Assert.False(p.RequestRecall(ctx)); // none carried
		g.Items[FakeGame.RecallPotion] = 1;
		g.Refresh(ctx);
		Assert.True(p.RequestRecall(ctx));
		var use = Assert.IsType<UseItemSkill>(p.Next(ctx));
		Assert.Equal(FakeGame.RecallPotion, use.ItemType);
	}

	[Fact]
	public void UsingARecallPotion_FinishesOnlyWhenTheUseIsOver()
	{
		// world_eye run 67: the potion was gone at the start of the swing, the teleport came later, and the next walk set off
		// from where the bot drank it (a second potion went the same way).
		FakeGame g = Mine();
		g.Items[FakeGame.RecallPotion] = 2;
		BotContext ctx = g.CreateContext(output);
		var use = new UseItemSkill(FakeGame.RecallPotion);
		use.Start(ctx);
		g.Items[FakeGame.RecallPotion] = 1;
		g.Refresh(ctx);
		ctx.Combat.ItemAnimation = 20; // still drinking
		Assert.Equal(SkillStatus.Running, use.Tick(ctx));
		ctx.Combat.ItemAnimation = 0;
		Assert.Equal(SkillStatus.Succeeded, use.Tick(ctx));
	}

	[Fact]
	public void FarFromTheStationsAtHome_DrinksARecallPotion()
	{
		// world_eye: the walk home from the mine took 4 game minutes every run; a player drinks a recall potion.
		FakeGame g = Mine();
		g.Stations[FakeGame.TileBench] = Beside(g, 2);
		g.Stations[FakeGame.TileFurnace] = Beside(g, -60);
		g.Stations[FakeGame.TileAnvil] = Beside(g, 5);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		g.Items[FakeGame.Wood] = 3;
		g.Items[FakeGame.RecallPotion] = 1;
		g.IsNight = false;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = Beside(g, -60); // the furnace is at home, by the spawn
		var planner = new ProgressionPlanner();
		planner.Reset(ProgressionPlanner.Plans.Iron);
		var use = Assert.IsType<UseItemSkill>(planner.Next(ctx));
		Assert.Equal(FakeGame.RecallPotion, use.ItemType);
		ctx.SpawnTile = Beside(g, 0); // the furnace is not by the spawn: walk
		Assert.IsType<WalkToSkill>(planner.Next(ctx));
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
		p.Reset(ProgressionPlanner.Plans.Iron); // the iron chain: no house before the anvil
		// Nothing places it and none is seen: explore for one a few times, then stuck.
		for (int i = 0; i < ProgressionPlanner.MaxStationExplores; i++)
			p.OnSkillFinished(Assert.IsType<ExploreSkill>(p.Next(ctx)), SkillStatus.Failed, ctx);
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
	public void MineSkill_DoesNotLoopOnAnUnreachableDrop()
	{
		// An ore drop sealed in an unbreakable pocket next to the vein: the collect gives up on it, and the mining
		// must not keep restarting that collect (a livelock seen in the settle-chain test).
		FakeGame g = Mine();
		for (int x = 4; x <= 6; x++)
			for (int y = 29; y <= 31; y++) {
				g.Grid.Set(x, y, CellKind.Solid);
				g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		g.Grid.Set(5, 30, CellKind.Empty);
		g.Drops.Add(new DropInfo(1, FakeGame.IronOre, "Iron Ore", 1, 5 * 16f + 4, 30 * 16f + 4, 8, 8));
		BotContext ctx = g.CreateContext(output);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 8), 60 * 120);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, ore {g.Count(FakeGame.IronOre)}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}

	[Fact]
	public void MineSkill_SkipsAnUnreachableCluster_AndRemembersIt()
	{
		// Ore sealed in unbreakable blocks right under the start scores better than the real vein (seen in game: ore
		// under the arena's unbreakable floor). Mining must skip the whole cluster and get the vein, and a fresh
		// MineSkill must not try the cluster again.
		FakeGame g = Mine();
		// An unbreakable block 0-13 x 25-32 with ore 6 rows down inside: pickaxes need no line of sight, but no
		// standable spot is within tool range of it (the vein proper starts at x 14).
		for (int x = 0; x <= 13; x++)
			for (int y = 25; y <= 32; y++) {
				g.Grid.Set(x, y, CellKind.Solid);
				g.Blocks.Unbreakable.Add(new TilePos(x, y));
			}
		foreach (int x in new[] { 5, 6, 7, 8 }) {
			g.Blocks.Unbreakable.Remove(new TilePos(x, 30));
			g.TileTypes[new TilePos(x, 30)] = FakeGame.TileIron;
		}
		BotContext ctx = g.CreateContext(output);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3), 60 * 120));
		Assert.Contains(new TilePos(5, 30), ctx.UnreachableTiles);
		Assert.Contains(new TilePos(8, 30), ctx.UnreachableTiles);
		int before = g.Sim.Ticks;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 6), 60 * 120));
		output.WriteLine($"second run took {g.Sim.Ticks - before} ticks");
	}

	/// <summary>
	/// Run <paramref name="mine"/> with its pickaxe swings at the target not landing (as when something keeps a tile from
	/// breaking), until <paramref name="skips"/> "skipping" give-ups are logged; the skill is stopped then.
	/// </summary>
	public static void MineUntilGivenUp(FakeGame g, BotContext ctx, MineSkill mine, int skips)
	{
		var log = (TestLog)ctx.Log;
		int before = log.Lines.Count(l => l.StartsWith("Mine: skipping"));
		mine.Start(ctx);
		for (int t = 0; t < 60 * 120 && log.Lines.Count(l => l.StartsWith("Mine: skipping")) < before + skips; t++) {
			g.Refresh(ctx);
			Assert.Equal(SkillStatus.Running, mine.Tick(ctx));
			if (mine.State.StartsWith("mining"))
				ctx.Output.UseItem = false;
			g.Step(ctx);
		}
		Assert.Equal(before + skips, log.Lines.Count(l => l.StartsWith("Mine: skipping")));
		mine.Stop(ctx);
	}

	[Fact]
	public void MineSkill_OreAnEarlierJobGaveUpOn_IsTriedAfterTheRest()
	{
		// Audit of the 10-day world_eye runs (2026-10-16): a give-up ("still there after 480 ticks") went only into the job's
		// own skip list, and the next job went for the same tile first.
		FakeGame g = Mine(oreRows: 0);
		var near = new TilePos(7, 25);
		var far = new TilePos(20, 25);
		g.TileTypes[near] = g.TileTypes[far] = FakeGame.TileIron;
		BotContext ctx = g.CreateContext(output);
		MineUntilGivenUp(g, ctx, new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 1), 1);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.StartsWith($"Mine: skipping {near}"));
		var next = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 1);
		next.Start(ctx);
		for (int t = 0; t < 5; t++) {
			g.Refresh(ctx);
			next.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(next.Describe());
		Assert.Contains(far.ToString(), next.Describe());
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
		FakeGame g = Mine(90, treeColumns: new[] { 40, 50, 60, 70, 80 });
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
