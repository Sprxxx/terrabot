using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Milestone 6: building a house, wearing armour, and the "settle" plan, in <see cref="FakeGame"/>.</summary>
public class BuildingTests
{
	private readonly ITestOutputHelper output;
	public BuildingTests(ITestOutputHelper output) => this.output = output;

	private static void GiveHouseKit(FakeGame g)
	{
		g.Items[FakeGame.DirtBlock] = 40;
		g.Items[FakeGame.WoodWall] = 40;
		g.Items[FakeGame.Door] = 1;
		g.Items[FakeGame.Chair] = 1;
		g.Items[FakeGame.Table] = 1;
		g.Items[FakeGame.Torch] = 3;
	}

	private SkillStatus Run(FakeGame g, BotContext ctx, ISkill skill, int maxTicks)
	{
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, skill, maxTicks);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks ({skill.Describe()}); placed {g.Sim.Placed}, broken {g.Sim.Broken}, out of range {g.Sim.OutOfRange}");
		return s;
	}

	[Fact]
	public void SmallHouse_IsBigEnoughForVanilla()
	{
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		Assert.Equal(12, bp.Width);
		Assert.Equal(6, bp.Height);
		Assert.True(bp.Width * bp.Height >= 60);
		Assert.Equal(40, bp.Count(PlanCell.Wall));
		Assert.Equal(PlanCell.Clear, bp.At(0, 2)); // doorway
		Assert.Equal(PlanCell.Block, bp.At(0, 1)); // the door's top anchor
	}

	[Fact]
	public void SiteFinder_AvoidsTrees_AndPrefersFlatGround()
	{
		FakeGame g = EarlyGameTests.Field(80, 10);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos? site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']);
		Assert.NotNull(site);
		output.WriteLine($"site {site}");
		Assert.Equal(25 - (bp.Height - 1), site.Value.Y); // floor row = ground row 25
		Assert.False(site.Value.X <= 10 && site.Value.X + bp.Width > 10, "house over the tree");
		Assert.Equal(0, SiteFinder.Edits(ctx, bp, site.Value) - CountMissingRoof(bp));
	}

	/// <summary>On flat open ground only the frame above the floor has to be placed.</summary>
	private static int CountMissingRoof(Blueprint bp) => bp.Count(PlanCell.Block) - bp.Width;

	[Fact]
	public void BuildSkill_BuildsValidHousing_OnFlatGround()
	{
		FakeGame g = EarlyGameTests.Field(60);
		GiveHouseKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value;
		var build = new BuildSkill(bp, site);
		Assert.Equal(SkillStatus.Succeeded, Run(g, ctx, build, 60 * 120));
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
		Assert.Null(ctx.Reserved);
		Assert.Equal(40, g.Walls.Count);
		Assert.True(g.Sim.Node.X < site.X, "the bot should end outside, by the door");
	}

	[Fact]
	public void BuildSkill_ClearsADirtMound_First()
	{
		FakeGame g = EarlyGameTests.Field(60);
		for (int x = 18; x <= 22; x++)
			for (int y = 22; y <= 24; y++)
				g.Grid.Set(x, y, CellKind.Solid);
		GiveHouseKit(g);
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		var site = new TilePos(15, 25 - bp.Height); // right over the mound
		Assert.True(SiteFinder.Edits(ctx, bp, site) > 0);
		Assert.Equal(SkillStatus.Succeeded, Run(g, ctx, new BuildSkill(bp, site), 60 * 150));
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
	}

	[Fact]
	public void BuildSkill_FailsCleanly_WithoutWalls()
	{
		FakeGame g = EarlyGameTests.Field(60);
		GiveHouseKit(g);
		g.Items[FakeGame.WoodWall] = 5;
		BotContext ctx = g.CreateContext(output);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		var build = new BuildSkill(bp, SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value);
		Assert.Equal(SkillStatus.Failed, Run(g, ctx, build, 60 * 120));
		Assert.Contains("out of walls", build.FailureReason);
		Assert.Null(ctx.Reserved);
	}

	[Fact]
	public void Planner_WearsCarriedArmour_AndCraftsMissingPieces()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.IronHelmet] = 1;
		g.Items[FakeGame.IronBar] = 25;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		HouseAlreadyThere(g, p, ctx);

		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		var equip = Assert.IsType<EquipSkill>(next);
		Assert.Equal(FakeGame.IronHelmet, equip.ItemType);

		g.Equipped.Add(FakeGame.IronHelmet);
		g.Items[FakeGame.IronHelmet] = 0;
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		// Chainmail next: crafted at the anvil (or walking there first).
		Assert.True(next is CraftSkill { ItemType: FakeGame.IronChainmail } || next is WalkToSkill && p.Status.Contains("item81"), p.Status);
	}

	[Fact]
	public void Night_WithAHouse_StaysHome_UntilGettingHomeFails()
	{
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		var home = Assert.IsType<StayHomeSkill>(next);

		// Seen in game: no path home, and the same failing walk again and again until the brain gave up. A real failure: a
		// walk home to a spot sealed off in solid rock.
		var sealedOff = new StayHomeSkill(new TilePos(40, 33));
		ctx.AllowBreak = false; // no digging there
		ctx.AllowPlace = false;
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, sealedOff, 60 * 30));
		p.OnSkillFinished(sealedOff, SkillStatus.Failed, ctx);
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsNotType<StayHomeSkill>(next);
	}

	[Fact]
	public void Night_WithTheFurnaceOutsideTheHouse_MakesANewOneAtHome()
	{
		// Measured run M2: nightfall with 21 iron ore and the furnace 15 rows below the house (on a site given up); the walk to
		// it became a night in the house doing nothing, and the anvil came a game day late. A player keeps them in the house.
		FakeGame g = ProgressionTests.Mine(110);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(80, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.Items[FakeGame.IronOre] = 21;
		g.Items[FakeGame.StoneBlock] = 40;
		g.Items[FakeGame.Wood] = 30;
		g.Items[FakeGame.Torch] = 10;
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.IsNotType<StayHomeSkill>(next);
		Assert.True(next is WalkToSkill { Home: true } || next is CraftSkill { ItemType: FakeGame.Furnace }, p.Status);
		Assert.Contains("night", p.Status);
	}

	[Fact]
	public void InTheMorning_WaitsInTheHouseForTheZombiesOutsideToGo()
	{
		// world_eye run 63: out of the house at first light among 6 zombies, killed below it.
		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(output);
		var stay = new StayHomeSkill(ctx.Node); // already there
		g.IsNight = true;
		g.Refresh(ctx);
		stay.Start(ctx);
		Assert.Equal(SkillStatus.Running, stay.Tick(ctx));
		Assert.True(stay.Home);
		g.IsNight = false;
		g.AddSlime(7, ctx.Node.X + 10, ctx.Node.Y);
		g.Refresh(ctx);
		Assert.Equal(SkillStatus.Running, stay.Tick(ctx));
		Assert.Contains("waiting for 1 hostile", stay.Describe());
		g.Slimes.Clear();
		g.Refresh(ctx);
		Assert.Equal(SkillStatus.Succeeded, stay.Tick(ctx));
	}

	[Fact]
	public void DyingOnTheWayHome_IsNotCannotGetHome()
	{
		// world_eye run 52: killed just below the house, the death counted as "cannot get home", and after the respawn a
		// few steps from the house the bot sheltered outside all night.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		var home = Assert.IsType<StayHomeSkill>(p.Next(ctx));
		p.OnSkillFinished(home, SkillStatus.Failed, ctx); // what the brain reports after a death (no failure reason)
		Assert.IsType<StayHomeSkill>(p.Next(ctx));
	}

	[Fact]
	public void ArmourTier_IsTheOneCheapestToFinishFromTheBag()
	{
		// Measured run C: the tier was costed from scratch, so it flipped between iron and silver with each fruitless mine,
		// and after two game days the bot held 41 iron and 57 silver ore, enough of neither.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.IronHelmet] = 1;
		g.Equipped.Add(FakeGame.IronHelmet);
		g.Items[FakeGame.IronOre] = 10;
		g.Items[FakeGame.SilverOre] = 95; // 5 short of the silver chainmail's 100, 65 short of the iron one's 75
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.SilverOre); // silver has run out once: each silver ore now costs twice as much
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.Contains($"item{FakeGame.SilverOre}", p.Status + next?.Describe()); // finishing the silver one, not starting iron over
	}

	[Fact]
	public void Armour_SwitchesToSilver_WhenIronKeepsRunningOut()
	{
		// world_eye run 38: 114 iron for anvil, sword, pickaxe and armour, and the iron near the spawn ran out.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.Contains($"item{FakeGame.IronOre}", p.Status); // iron first: 3 ore a bar, silver 4
		p.NoteFruitless(FakeGame.IronOre);
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.Contains($"item{FakeGame.SilverOre}", p.Status);

		g.Items[FakeGame.SilverHelmet] = 1; // a silver helmet is a helmet
		next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.Equal(FakeGame.SilverHelmet, Assert.IsType<EquipSkill>(next).ItemType);
	}

	[Fact]
	public void ATripForNewOre_StoppedByAFight_GoesOnToTheSamePlace()
	{
		// world_eye run 61: a fight stopped the trip in the desert, mining there failed at once, and the next trip went the other way.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill trip = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {trip?.Describe()}");
		Assert.True(trip is WalkToSkill { Travel: true });
		Assert.Contains("travelling to new ground at (123,", p.Status);
		// Interrupted (no finish reported), then the mining job fails again where the bot stands:
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill again = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {again?.Describe()}");
		Assert.True(again is WalkToSkill { Travel: true });
		Assert.Contains("on to new ground at (123,", p.Status);
	}

	[Fact]
	public void NowhereToTravel_NoTrip_ButDeeperAndBackToTheSpawn()
	{
		// world_eye run 68: every way from the spawn crossed a biome, and five recall potions went in a minute, each followed by
		// mining from the spawn again; run 70: with nowhere to go and nothing to mine, auto mode stopped.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.RecallPotion] = 3;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(200, 44);
		ctx.NoteDanger(new TilePos(150, 44), BotContext.BiomeZoneTicks, 10); // west of the spawn
		ctx.NoteDanger(new TilePos(250, 44), BotContext.BiomeZoneTicks, 10); // and east
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.False(next is WalkToSkill { Travel: true });
		Assert.Equal(BotContext.DepthBonusStep, ctx.DepthBonus); // deeper than the gear alone allows
		p.NoteFruitless(FakeGame.IronOre); // the other ore runs out at once too: no second step yet (run 72: 25 to 50 in seconds)
		p.NoteFruitless(FakeGame.SilverOre);
		p.Next(ctx);
		Assert.Equal(BotContext.DepthBonusStep, ctx.DepthBonus);
		Assert.Equal(FakeGame.RecallPotion, Assert.IsType<UseItemSkill>(next).ItemType); // far from the spawn: back there to look again
	}

	[Fact]
	public void ATripForNewOre_DoesNotCrossSomewhereOffLimits()
	{
		// world_eye run 67: a trip west across the corruption; knocked into a chasm there, the bot fell 66 tiles.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		ctx.NoteDanger(new TilePos(80, 44), BotContext.BiomeZoneTicks, 10); // on the way east (the only way: west is off the map)
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.False(next is WalkToSkill { Travel: true });
	}

	[Fact]
	public void ATripForNewOre_CrossesADesertsSurface_OnceArmed()
	{
		// Measured run C: the route across the desert's (or corruption's) surface stayed banned with a broadsword in hand,
		// and the bot had "nowhere to travel" while the ore round the spawn was gone.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		for (int x = 50; x < 100; x++) // a desert's surface on the way east (the only way: west is off the map)
			for (int y = 45; y < 53; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileSand;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ctx.Combat.WeaponDamage = 5; // unarmed: the desert is in the way
		ISkill weak = p.Next(ctx);
		output.WriteLine($"weak: {p.Status} -> {weak?.Describe()}");
		Assert.False(weak is WalkToSkill { Travel: true });
		var p2 = new ProgressionPlanner();
		p2.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p2, ctx);
		p2.NoteFruitless(FakeGame.IronOre);
		p2.NoteFruitless(FakeGame.SilverOre);
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage; // a broadsword: across it on the surface, by day
		ISkill armed = p2.Next(ctx);
		output.WriteLine($"armed: {p2.Status} -> {armed?.Describe()}");
		Assert.True(armed is WalkToSkill { Travel: true });
	}

	[Fact]
	public void TheWalkBackToTheSpawn_WithNowhereToTravel_HoldsAfterAFight()
	{
		// Measured run S7: "nowhere to travel; back to the spawn to look from there", broken off by a fight, became a
		// mining job again that failed at once (standing off limits), 40 times over.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		for (int x = 50; x < 100; x++) // a desert's surface on the way east (the only way: west is off the map)
			for (int y = 45; y < 53; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileSand;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.Sim.State.X += 40 * 16; // mining took it away from the spawn
		g.Refresh(ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ctx.Combat.WeaponDamage = 5; // unarmed: the desert is in the way, nowhere to travel
		ISkill back = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {back?.Describe()}");
		Assert.Contains("back to the spawn", p.Status);
		ISkill again = p.Next(ctx); // after a fight
		output.WriteLine($"{p.Status} -> {again?.Describe()}");
		Assert.Equal(back.Key, again.Key);
	}

	[Fact]
	public void AfterManyFruitlessJobs_TripsComeRoundToTheNearerGroundAgain()
	{
		// Measured run U2: after ten fruitless crystal jobs only the trips 600 tiles out were tried, both off limits, and
		// "nowhere to travel" came 12 times though nearer ground had served before.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		for (int i = 0; i < 9; i++)
		{
			p.NoteFruitless(FakeGame.IronOre); // the farthest ring (600 tiles) is off the 400-wide map
			p.NoteFruitless(FakeGame.SilverOre);
		}
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.True(next is WalkToSkill { Travel: true }, p.Status);
	}

	[Fact]
	public void ATripThatFoundNoWayThere_IsNotChosenAgain_TheNextGoesFarther()
	{
		// Measured run U8: from a pocket in a corruption chasm the trip to (1495,241) failed "no path" and was chosen again
		// after every fruitless job.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill first = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {first?.Describe()}");
		Assert.True(first is WalkToSkill { Travel: true }, p.Status);
		string firstStatus = p.Status;
		// No way there: a walk off the map fails "no path" as the trip did.
		var failed = new WalkToSkill(new TilePos(1000, 44)) { Travel = true };
		typeof(WalkToSkill).GetProperty(nameof(WalkToSkill.FailureReason)).SetValue(failed, "no path after 3 attempts");
		p.OnSkillFinished(failed, SkillStatus.Failed, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.NotEqual(firstStatus.Replace("travelling", "on"), p.Status.Replace("travelling", "on"));
		Assert.DoesNotContain("(123,", p.Status);
	}

	[Fact]
	public void DownAChasm_BelowTheGroundButAboveTheWorldsSurfaceRow_ATripGoesUpFirst()
	{
		// Measured run U8: at row 342 in a chasm, the ground about 57 rows above, but the world's surface row lower still:
		// the trip planned from the chasm's floor and found no path for 18 game hours.
		// Ground at row 45 (feet row 44), a shaft at columns 1-6 down to a floor under row 74.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 45; y++)
			rows.Add(new string('.', 400));
		for (int y = 45; y < 100; y++) {
			char[] r = new string(y >= 96 ? '@' : '#', 400).ToCharArray();
			if (y <= 74)
				for (int x = 1; x <= 6; x++)
					r[x] = y == 74 && x == 3 ? 'S' : '.';
			rows.Add(new string(r));
		}
		FakeGame g = new FakeGame(rows.ToArray());
		g.Stations[FakeGame.TileBench] = new TilePos(3, 74);
		g.Stations[FakeGame.TileFurnace] = new TilePos(4, 74);
		g.Stations[FakeGame.TileAnvil] = new TilePos(5, 74);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.StoneBlock] = 300;
		g.Items[FakeGame.Wood] = 300;
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		ctx.SpawnTile = new TilePos(200, 44);
		ctx.SurfaceY = 80; // the world's surface row, below the chasm's floor
		int row = ProgressionPlanner.SurfaceRow(ctx);
		output.WriteLine($"at {ctx.Node}: surface row {row}");
		// The ground beside the shaft (feet row 44), so the bot at row 74 is 30 below it: up first (TravelFromSurfaceRows 15).
		Assert.Equal(44, row);
		Assert.True(ctx.Node.Y > row + ProgressionPlanner.TravelFromSurfaceRows);
	}

	[Fact]
	public void AtNight_ATripForOre_WaitsForTheMorning_AndTheNightGoesToMining()
	{
		// Measured run U4: "no more Silver Ore found around here; back to the spawn" at nightfall became a night in the
		// house, 46 StayHome heartbeats between the chainmail and the greaves.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45, oreRows: 0);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		ctx.SurfaceY = 40;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill night = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {night?.Describe()}");
		Assert.IsNotType<StayHomeSkill>(night);
		Assert.True(night is WalkToSkill { Underground: true } || night is MineSkill, p.Status);
	}

	[Fact]
	public void AHouseAlreadyStanding_IsLivedIn_NotBuiltAgain()
	{
		// A run from a checkpoint (or a saved world) starts with a fresh planner: the house standing by the bench is the
		// house, not a reason to build another beside it.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		BotContext ctx = g.CreateContext(output);
		var first = new ProgressionPlanner();
		first.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, first, ctx);
		TilePos built = first.HouseSite.Value;
		var fresh = new ProgressionPlanner();
		fresh.Reset(ProgressionPlanner.Plans.Settle);
		g.Refresh(ctx);
		ISkill next = fresh.ResolveForTest(new HouseBuilt("house"), ctx);
		output.WriteLine($"{fresh.Status} -> {next?.Describe() ?? "nothing to do"}; site {fresh.HouseSite}");
		Assert.Null(next);
		Assert.Equal(built, fresh.HouseSite);
	}

	[Fact]
	public void ATripsWayOut_HoldsWhenAFightMovesTheBot()
	{
		// User report (2026-10-14): "changes its mind and jumps out of the hole to do something else". The way up to the
		// surface was worked out again from wherever a fight left the bot: another column, another plan from scratch.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 50; y++)
			rows.Add(new string('.', 200));
		for (int y = 50; y < 90; y++)
			rows.Add(new string('#', 200));
		for (int y = 80; y <= 82; y++)
			rows[y] = new string('#', 20) + new string('.', 40) + new string('#', 140); // a cave far below the surface
		rows[82] = new string('#', 25) + "S" + new string('.', 34) + new string('#', 140);
		rows.Add(new string('@', 200));
		var g = new FakeGame(rows.ToArray());
		g.Stations[FakeGame.TileBench] = new TilePos(30, 49);
		g.Stations[FakeGame.TileFurnace] = new TilePos(33, 49);
		g.Stations[FakeGame.TileAnvil] = new TilePos(36, 49);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.Wood] = 50;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(28, 49);
		ctx.SurfaceY = 50;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Iron);
		p.NoteFruitless(FakeGame.IronOre);
		ISkill first = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {first?.Describe()}");
		var up = Assert.IsType<WalkToSkill>(first);
		g.Sim.State.X += 20 * 16; // a fight carried it 20 tiles along the cave
		g.Refresh(ctx);
		ISkill again = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {again?.Describe()}");
		Assert.Equal(((ISkill)up).Key, ((ISkill)again).Key); // the same way out, so the same job goes on
	}

	[Fact]
	public void ATripForNewOre_FromFarDown_StartsWithARecallPotion()
	{
		// world_eye run 64: the walk back to the spawn from 120 rows down followed caves into the desert; a player recalls.
		FakeGame g = ProgressionTests.Mine(width: 400, sky: 45);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Stations[FakeGame.TileAnvil] = new TilePos(1, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronPickaxe] = 1;
		g.Items[FakeGame.IronBroadsword] = 1;
		g.Items[FakeGame.RecallPotion] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(303, 44 - 40); // far from the bot, the trip (west, 120 from the spawn) farther still from it
		ctx.SurfaceY = 20;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		p.NoteFruitless(FakeGame.IronOre);
		p.NoteFruitless(FakeGame.SilverOre);
		ISkill next = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {next?.Describe()}");
		Assert.Equal(FakeGame.RecallPotion, Assert.IsType<UseItemSkill>(next).ItemType);
	}

	[Fact]
	public void AtNight_AMiningJobGoesDownIntoTheGround_InsteadOfHome_ArmedOrNot()
	{
		// Measured run E: 37% of the run was nights at home, most instead of mining near the surface. A player spends the
		// night in the mine. Measured run R1: unarmed, two whole nights at home before the first ore sword.
		FakeGame g = ProgressionTests.Mine(width: 120, sky: 45, oreRows: 0);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		ctx.SurfaceY = 40;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		ctx.Combat.WeaponDamage = 5;
		ISkill weak = p.Next(ctx);
		output.WriteLine($"weak: {p.Status} -> {weak?.Describe()}");
		Assert.True(weak is WalkToSkill { Underground: true });
		ctx.Combat.WeaponDamage = BotContext.GoodWeaponDamage;
		ISkill armed = p.Next(ctx);
		output.WriteLine($"armed: {p.Status} -> {armed?.Describe()}");
		Assert.True(armed is WalkToSkill { Underground: true });
	}

	[Fact]
	public void AtNight_AWalkToAStationOutside_IsNotMade_TheBotGoesHome()
	{
		// world_eye run 65: respawned at night, sent to the furnace below the house, killed by zombies there five times.
		// Home instead: to stay, or to make a furnace there (Night_WithTheFurnaceOutsideTheHouse_MakesANewOneAtHome).
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(60, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		ISkill day = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {day?.Describe()}");
		Assert.IsType<WalkToSkill>(day); // to the furnace, by day
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill night = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {night?.Describe()}");
		Assert.True(night is StayHomeSkill || night is WalkToSkill { Home: true }, p.Status);
	}

	[Fact]
	public void AtNight_AWalkToTheHousesOwnFurnace_GoesOn()
	{
		// Measured run S6: nightfall on the way to the furnace 4 tiles from the house, with ore for 8 bars: home, and 9 game
		// hours in it with the ore unsmelted.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		g.Items[FakeGame.IronOre] = 30;
		BotContext ctx = g.CreateContext(output);
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		ISkill night = p.Next(ctx);
		output.WriteLine($"{p.Status} -> {night?.Describe()}");
		Assert.IsNotType<StayHomeSkill>(night);
		Assert.DoesNotContain("staying in the house", p.Status);
	}

	[Fact]
	public void AtNight_TheWayDownToMine_IsToADepth_NotToOneTile()
	{
		// Measured run S6: the night's walk to one tile under the spawn had "no path after 3 attempts": the night in the house.
		FakeGame g = ProgressionTests.Mine(width: 120, sky: 45, oreRows: 0);
		g.Stations[FakeGame.TileBench] = new TilePos(5, 44);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 44);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		ctx.SpawnTile = new TilePos(3, 44);
		ctx.SurfaceY = 40;
		var p = new ProgressionPlanner();
		p.Reset(ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, p, ctx);
		g.IsNight = true;
		g.Refresh(ctx);
		var down = Assert.IsType<WalkToSkill>(p.Next(ctx));
		down.Start(ctx);
		var row = Assert.IsType<Core.Pathfinding.GoalRow>(down.Goal);
		Assert.True(row.Below);
		Assert.True(row.IsGoal(new TilePos(90, row.Row + 3)));
		Assert.False(row.IsGoal(new TilePos(3, row.Row - 1)));
	}

	[Fact]
	public void AtNightfall_WorkBegunByDay_IsPlannedAgain_AndTheBotGoesHome()
	{
		// world_eye run 68: a walk begun by day came up at night among zombies, and one killed the bot.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, brain.Planner, ctx);
		for (int t = 0; t < 30; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		output.WriteLine(brain.Describe());
		Assert.IsNotType<StayHomeSkill>(brain.Current); // working by day
		g.IsNight = true;
		g.Refresh(ctx);
		brain.Tick(ctx);
		output.WriteLine(brain.Describe());
		Assert.IsType<StayHomeSkill>(brain.Current);
	}

	[Fact]
	public void WalkingHomeAtNight_DoesNotStopToFight()
	{
		// world_eye run 41: stopped at dusk to fight zombies with a copper sword on the way home, then dug in among them.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, brain.Planner, ctx);
		g.Sim.State.X += 30 * 16; // out mining, some way from the house
		g.IsNight = true;
		bool walkedHome = false, fought = false;
		for (int t = 0; t < 60 * 20 && !(brain.Current is StayHomeSkill { Home: true }); t++) {
			if (t == 5)
				g.AddSlime(9, (int)(g.Sim.State.X / 16) - 6, 24); // on the way home
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
			walkedHome |= brain.Current is StayHomeSkill;
			fought |= walkedHome && brain.Current is FightSkill;
		}
		output.WriteLine(brain.Describe());
		Assert.True(walkedHome);
		Assert.False(fought);
	}

	[Fact]
	public void BadlyHurt_NearTheHouse_RetreatsHome_NotIntoAHole()
	{
		// The night runs: most deaths came while digging in to recover, hit as it dug. A house nearby is the better hiding place.
		FakeGame g = ProgressionTests.Mine();
		g.Stations[FakeGame.TileBench] = new TilePos(5, 24);
		g.Stations[FakeGame.TileFurnace] = new TilePos(8, 24);
		g.Items[FakeGame.WoodenSword] = 1;
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Settle);
		HouseAlreadyThere(g, brain.Planner, ctx);
		g.Sim.State.X += 20 * 16;
		g.Life = 20;
		g.AddSlime(9, (int)(g.Sim.State.X / 16) + 3, 24);
		ISkill retreat = null;
		for (int t = 0; t < 120 && retreat == null; t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
			if (brain.Current is StayHomeSkill or ShelterSkill)
				retreat = brain.Current;
		}
		output.WriteLine(brain.Describe());
		var home = Assert.IsType<StayHomeSkill>(retreat);
		Assert.True(home.RecoverTo > 0);
	}

	/// <summary>Builds a valid house next to the start through the fake's own placement, and tells the planner about it.</summary>
	internal static void HouseAlreadyThere(FakeGame g, ProgressionPlanner p, BotContext ctx)
	{
		GiveHouseKit(g);
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		TilePos site = SiteFinder.Find(ctx, bp, g.Grid.Markers['S']).Value;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, new BuildSkill(bp, site), 60 * 120));
		p.OnSkillFinished(new BuildSkill(bp, site), SkillStatus.Succeeded, ctx);
		Assert.Equal(site, p.HouseSite);
	}

	[Fact]
	public void AutoBrain_RunsTheSettleChain()
	{
		FakeGame g = ProgressionTests.Mine(110, oreRows: 8, veinWidth: 40, treeColumns: new[] { 37, 44, 51, 58, 65, 72, 79, 86, 93, 100, 107 });
		g.AddSlime(7, 30, 24);
		BotContext ctx = g.CreateContext(output);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx, ProgressionPlanner.Plans.Settle);
		int t = 0;
		for (; t < 60 * 60 * 40 && brain.Auto && !(brain.Planner.Complete && brain.Current == null); t++) {
			g.Refresh(ctx);
			brain.Tick(ctx);
			g.Step(ctx);
		}
		string inv = string.Join(", ", g.Items.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"));
		output.WriteLine($"after {t} ticks: {brain.Describe()} | equipped {string.Join(",", g.Equipped)} | inv {inv} | out of range {g.Sim.OutOfRange}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status + " / " + brain.LastResult);
		Assert.True(new[] { FakeGame.IronHelmet, FakeGame.IronChainmail, FakeGame.IronGreaves }.All(g.Equipped.Contains));
		Assert.Equal(1, g.Count(FakeGame.IronBroadsword));
		TilePos site = brain.Planner.HouseSite.Value;
		Blueprint bp = Blueprint.SmallHouse(FakeGame.Ids);
		Assert.Null(g.CheckHousing(site.X + bp.Inside.X, site.Y + bp.Inside.Y));
	}
}
