using System.Linq;
using TerraBot.Bot.Brain;
using TerraBot.Core.Grid;
using Xunit;

namespace TerraBot.Tests;

/// <summary>The ground's surface (<see cref="ProgressionPlanner.SurfaceAt"/>), not a sky island, a cloud or a bridge of blocks.</summary>
public class SurfaceTests
{
	/// <summary>140 rows: the ground from row 130; a sky island (rows 60-63, x 10-30) and a one-row bridge (row 70, x 40-60).</summary>
	private static FakeGame SkyWorld()
	{
		var rows = Enumerable.Range(0, 140).Select(y => (y >= 130 ? new string('#', 80) : new string('.', 80)).ToCharArray()).ToArray();
		for (int y = 60; y <= 63; y++)
			for (int x = 10; x <= 30; x++)
				rows[y][x] = '#';
		for (int x = 40; x <= 60; x++)
			rows[70][x] = '#';
		rows[129][70] = 'S';
		return new FakeGame(rows.Select(r => new string(r)).ToArray());
	}

	[Fact]
	public void TheSurface_IsTheGround_NotASkyIslandNorABridgeOverIt()
	{
		// Measured run M11a: "along the surface west to (1213,61)", (1139,84), (1085,65): rain clouds and a sky island over
		// ground 120 rows down; the bot climbed up and bridged west on its own blocks past the Dungeon.
		FakeGame g = SkyWorld();
		Assert.Equal(129, ProgressionPlanner.SurfaceAt(g.Grid, 20)?.Y); // under the island
		Assert.Equal(129, ProgressionPlanner.SurfaceAt(g.Grid, 50)?.Y); // under the bridge
		Assert.Equal(129, ProgressionPlanner.SurfaceAt(g.Grid, 5)?.Y);  // open ground
	}
}
