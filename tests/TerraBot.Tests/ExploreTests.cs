using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Only what the player has seen counts; exploring reveals the rest.</summary>
public class ExploreTests
{
	private readonly ITestOutputHelper output;
	public ExploreTests(ITestOutputHelper output) => this.output = output;

	/// <summary>140 wide: grass at row 25, stone below, an iron vein at columns 100-115 (rows 29-31), all of it past column 50 unseen.</summary>
	private static FakeGame FarOre()
	{
		FakeGame g = ProgressionTests.Mine(140, oreRows: 0);
		for (int x = 100; x <= 115; x++)
			for (int y = 29; y <= 31; y++)
				g.TileTypes[new TilePos(x, y)] = FakeGame.TileIron;
		g.HideFrom(50);
		return g;
	}

	[Fact]
	public void UnseenOre_IsNotKnown()
	{
		FakeGame g = FarOre();
		Assert.Empty(g.FindTiles(FakeGame.TileIron, new TilePos(105, 30), 20));
	}

	[Fact]
	public void Explore_RevealsTheFarOre()
	{
		FakeGame g = FarOre();
		BotContext ctx = g.CreateContext(output);
		var explore = new ExploreSkill("iron ore", c => c.World.FindTiles(FakeGame.TileIron, c.Node, MineSkill.SearchRadius).Count > 0, ExploreBias.Sideways, heading: 1);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, explore, 60 * 60 * 3);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks, {explore.Legs} legs, at {g.Sim.Node}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}

	[Fact]
	public void Mine_ExploresForOreItHasNotSeen()
	{
		FakeGame g = FarOre();
		BotContext ctx = g.CreateContext(output);
		var mine = new MineSkill(FakeGame.IronOre, FakeGame.TileIron, 3);
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, mine, 60 * 60 * 6);
		output.WriteLine($"{s} after {g.Sim.Ticks} ticks ({mine.Describe()}), ore {g.Count(FakeGame.IronOre)}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Count(FakeGame.IronOre) >= 3);
	}
}
