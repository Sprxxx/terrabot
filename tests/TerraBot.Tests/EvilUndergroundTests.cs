using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The evil biome's underground, under its known surface (BotContext.UnderEvilGround). Measured runs U25 and U27 (copper
/// gear, max life 100): iron-ore explorations from the house went west to the corruption's edge at (1963,282) and down under
/// it, where its share of seen evil tiles was low (vanilla leaves dirt as dirt under the corrupt grass), and Devourers killed
/// the bot at (1961,335), (1954,396) and (1927,353).
/// </summary>
public class EvilUndergroundTests
{
	private readonly ITestOutputHelper output;
	public EvilUndergroundTests(ITestOutputHelper output) => this.output = output;

	/// <summary>Corrupt grass on the surface row, columns 0 to this one; plain dirt under it, as vanilla leaves it.</summary>
	private const int CorruptTo = 119, Ground = 30, Surface = 80, Caverns = 190, Width = 260, Height = 200;
	/// <summary>A ramp down from the surface under the corruption, columns 82-103, into a cave at columns 50-82, rows 48-52.</summary>
	private const int RampFrom = 82, RampTo = 103;

	/// <summary>
	/// 260 wide: sky to row 29 (the bot at (125,29)), dirt from row 30 down to 198, corrupt grass on row 30 at columns
	/// 0-119 (but over the ramp); everything below row 37 unseen, as the map shows a surface the daylight lit. Surface layer
	/// to row 80 (Main.worldSurface, 50 rows under the ground as 337 under ~285 in U27's world), caverns from row 190.
	/// </summary>
	private static (FakeGame, BotContext) CorruptEdge(ITestOutputHelper output)
	{
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < Height; y++) {
			char[] r = (y < Ground ? new string('.', Width) : y == Height - 1 ? new string('@', Width) : new string('#', Width)).ToCharArray();
			for (int x = RampFrom; x <= RampTo; x++)
				if (y >= Ground && y <= Ground + (RampTo - x))
					r[x] = '.';
			if (y >= 48 && y <= 52)
				for (int x = 50; x <= RampFrom; x++)
					r[x] = '.';
			if (y == Ground - 1)
				r[125] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		for (int x = 0; x <= CorruptTo; x++)
			if (g.Grid.Get(x, Ground).IsSolid())
				g.TileTypes[new TilePos(x, Ground)] = FakeGame.TileCorruptGrass;
		for (int x = 0; x < Width; x++)
			for (int y = Ground + 8; y < Height; y++)
				g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = Surface;
		ctx.CavernY = Caverns;
		g.Refresh(ctx);
		return (g, ctx);
	}

	[Fact]
	public void UnderTheCorruptionsSurface_ItsUnderground_IsTheBiome_ThoughItsSeenGroundIsPlainDirt()
	{
		var (g, ctx) = CorruptEdge(output);
		var under = new TilePos(40, Ground + 45); // as (1932,339), 55 rows under the grass at row ~284
		for (int x = 20; x <= 60; x++) // the bot's own bubble round it: seen, and plain dirt
			for (int y = under.Y - 10; y <= under.Y + 10; y++)
				g.Unrevealed.Remove(new TilePos(x, y));
		// The share of seen evil tiles round it is nil: the grass row is two chunks up, out of the share's window.
		Assert.False(ctx.InEvilBiome(under));
		// Copper gear (no armour): the corruption's underground is the biome all the same, off limits as its surface is.
		Assert.True(ctx.OffLimitsOutright(under), $"{under} under the corruption's surface is not off limits in copper gear");
		Assert.True(ctx.OffLimitsOutright(new TilePos(100, Ground + 10)));   // under its edge's columns (a chunk's margin)
		Assert.True(ctx.OffLimitsOutright(new TilePos(110, Caverns - 1)));   // the chasms go on down to the caverns
		Assert.False(ctx.OffLimitsOutright(new TilePos(110, Caverns + 5)));  // below them it is the caverns' ground
		Assert.False(ctx.OffLimitsOutright(new TilePos(150, Ground + 45)));  // past the margin, under clean ground
		var top = new TilePos(40, Ground - 1); // its surface itself: the share's call, as before
		Assert.Equal(ctx.InEvilBiome(top), ctx.OffLimitsOutright(top));
		// Full armour and a pickaxe for its stone: open, as the surface's gate is.
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = BotContext.CavernDefense;
		ctx.PickPower = 40; // iron: ebonstone wants 65 (measured run S4)
		Assert.True(ctx.OffLimitsOutright(under));
		ctx.PickPower = BotContext.EvilStonePickPower;
		Assert.False(ctx.OffLimitsOutright(under));
		// An errand there in full armour opens its surface layer, not what is under the surface row (as for the chasms).
		ctx.PickPower = 40;
		ctx.EvilErrand = true;
		Assert.False(ctx.OffLimitsOutright(new TilePos(40, Surface - 5)));
		Assert.True(ctx.OffLimitsOutright(new TilePos(40, Surface + 5)));
	}

	[Fact]
	public void ExploringDown_FromTheCorruptionsEdge_DoesNotGoUnderIt_InCopperGear()
	{
		// U27: "leg 2 to (1963,282) (frontier)", then legs (1952,287) (1935,292) (1922,298) (1913,310) (1930,317) (1932,339)
		// (1937,347) (1957,334) down under the corruption, and a Devourer killed the bot at (1961,335).
		var (g, ctx) = CorruptEdge(output);
		var explore = new ExploreSkill("Iron Ore", c => c.Node.Y >= 70, ExploreBias.Down, heading: -1);
		explore.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		TilePos? under = null;
		int deepest = ctx.Node.Y;
		for (int t = 0; t < ExploreSkill.MaxTicks && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			s = explore.Tick(ctx);
			g.Step(ctx);
			TilePos n = ctx.Node;
			deepest = System.Math.Max(deepest, n.Y);
			// Under the corruption's columns (0-79 hold a chunk's width of its grass) or the two chunks either side.
			if (under == null && n.X < 112 && n.Y > Ground + 1)
				under = n;
		}
		var legs = ((TestLog)ctx.Log).Lines.Where(l => l.Contains("Explore: leg")).ToList();
		foreach (string l in legs)
			output.WriteLine(l);
		output.WriteLine($"{s} ({explore.FailureReason}) after {explore.Legs} legs at {ctx.Node}, deepest row {deepest}");
		Assert.True(under == null, $"under the corruption at {under}");
		Assert.Equal(SkillStatus.Succeeded, s); // down east of it, where the ground is clean
	}

	[Fact]
	public void ATripAcrossTheCorruption_InFullArmourWithAnIronPickaxe_KeepsToItsSurface()
	{
		// Measured run U27, d7 (full armour, max life 160, pick power 40): "WalkTo (1615,264)", new ground west past the
		// corruption, went under it: heartbeats at (1924,356), (1934,356), (1905,440), (1896,445), (1892,449), and it ended
		// in an ebonstone pocket at (1872,366). Here a tunnel under the corruption (rows 50-52, shafts down to it at both
		// ends, outside it) is the cheap way; on the surface a hill (columns 80-100, up to the sky) has to be dug through.
		const int width = 200, height = 120, tunnelFeet = 52;
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < height; y++) {
			char[] r = (y < Ground ? new string('.', width) : y == height - 1 ? new string('@', width) : new string('#', width)).ToCharArray();
			if (y >= Ground && y <= tunnelFeet) // shafts down to the tunnel at both ends, outside the corruption's margin
				r[30] = r[31] = r[150] = r[151] = '.';
			if (y >= tunnelFeet - 2 && y <= tunnelFeet)
				for (int x = 30; x <= 151; x++)
					r[x] = '.';
			if (y < Ground)
				for (int x = 80; x <= 100; x++)
					r[x] = '#';
			if (y == Ground - 1)
				r[185] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		for (int x = 60; x <= 120; x++) // a chunk's width of it in columns 64-111: under it, and two chunks either side, x 32-143
			if (g.Grid.Get(x, Ground).IsSolid())
				g.TileTypes[new TilePos(x, Ground)] = FakeGame.TileCorruptGrass;
		BotContext ctx = g.CreateContext(output);
		ctx.SurfaceY = Surface;
		ctx.CavernY = height - 10;
		ctx.Combat.ArmourPieces = 3;
		ctx.Combat.Defense = 9;
		ctx.PickPower = 40;
		g.Refresh(ctx);
		var walk = new WalkToSkill(new TilePos(5, Ground - 1));
		walk.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		TilePos? under = null;
		for (int t = 0; t < 60 * 60 * 3 && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			ctx.Tick = t;
			s = walk.Tick(ctx);
			g.Step(ctx);
			if (under == null && ctx.Node.Y > Ground + 1 && ctx.Node.X >= 32 && ctx.Node.X <= 143)
				under = ctx.Node;
		}
		output.WriteLine($"{s} ({walk.FailureReason}) at {ctx.Node}");
		Assert.True(under == null, $"under the corruption at {under}");
		Assert.Equal(SkillStatus.Succeeded, s);
	}
}
