using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>The user (2026-10-18): "monitor and fix until it's perfect economical movement". The waste the watch reports.</summary>
public class NavWatchTests
{
	private readonly ITestOutputHelper output;
	public NavWatchTests(ITestOutputHelper output) => this.output = output;

	private static string Feed(NavWatch w, ref long tick, int x0, int y0, int x1, int y1, int steps)
	{
		string last = null;
		for (int i = 1; i <= steps; i++) {
			tick += NavWatch.SampleTicks;
			int x = x0 + (x1 - x0) * i / steps, y = y0 + (y1 - y0) * i / steps;
			last = w.Sample(tick, new TilePos(x, y), "goal", "job") ?? last;
		}
		return last;
	}

	[Fact]
	public void UpTheStairsAndBackDown_IsReported()
	{
		var w = new NavWatch();
		long t = 0;
		Feed(w, ref t, 100, 300, 108, 284, 20); // up 16 rows
		string waste = Feed(w, ref t, 108, 284, 101, 300, 20); // and back down
		output.WriteLine(waste);
		Assert.Equal(1, w.UpAndBack);
		Assert.Matches(@"up \d+ rows and back down", waste); // at least ExcursionRows: reported as soon as it is back near a point of the way up
	}

	[Fact]
	public void ASteadyWalk_OrAStraightDescent_IsNotWaste()
	{
		var w = new NavWatch();
		long t = 0;
		Feed(w, ref t, 100, 300, 400, 300, 200); // a long walk east
		Feed(w, ref t, 400, 300, 400, 700, 200); // a straight shaft down
		Assert.Equal(0, w.UpAndBack + w.DownAndBack + w.Revisits);
	}

	[Fact]
	public void ToAndFroBetweenTwoPlaces_IsARevisit()
	{
		var w = new NavWatch();
		long t = 0;
		for (int i = 0; i < 3; i++) {
			Feed(w, ref t, 100, 300, 160, 300, 10);
			Feed(w, ref t, 160, 300, 100, 300, 10);
		}
		Assert.True(w.Revisits >= 1, $"revisits {w.Revisits}");
	}

	[Fact]
	public void TheSameDecision_ThreeTimesInTwoHours_IsAPlanLoop()
	{
		var w = new NavWatch();
		Assert.Null(w.Decision(0, "no more DemonAltar found around here; on to new ground at (2841,357)", "g"));
		Assert.Null(w.Decision(3000, "no more DemonAltar found around here; on to new ground at (2841,357)", "g"));
		string loop = w.Decision(6000, "no more DemonAltar found around here; on to new ground at (2841,357)", "g");
		output.WriteLine(loop);
		Assert.NotNull(loop);
		Assert.Equal(1, w.PlanLoops);
	}

	/// <summary>
	/// Journey run J2 (2026-10-18): "up to the surface before travelling" from a dip aimed at the highest ground round the bot (a hill),
	/// above the ground there: a pillar into the air toward the trees, then back down. The way up ends at the ground over the body.
	/// </summary>
	[Fact]
	public void UpToTheSurface_FromADip_IsTheGroundHere_NotTheHillsRow()
	{
		// 80 wide, the ground at row 100 (SurfaceAt looks from row 40 down), the body at x 3.
		var rows = new string[110];
		for (int y = 0; y < rows.Length; y++)
			rows[y] = new string(y <= 100 ? '.' : '#', 80);
		rows[100] = "...S" + new string('.', 76);
		FakeGame g = new FakeGame(rows);
		BotContext ctx = g.CreateContext(output);
		g.Refresh(ctx);
		TilePos n = ctx.Node;
		for (int x = n.X + 4; x <= n.X + 14; x++)
			for (int y = 80; y <= 100; y++)
				g.Grid.Set(x, y, TerraBot.Core.Grid.CellKind.Solid); // a hill 20 rows high beside the dip
		g.Refresh(ctx);
		output.WriteLine($"surface row {ProgressionPlanner.SurfaceRow(ctx)}, here {ProgressionPlanner.LocalSurfaceRow(ctx)}, body {n}");
		Assert.True(ProgressionPlanner.SurfaceRow(ctx) < n.Y - 15, "the hill reads as the surface round here");
		Assert.Equal(n.Y, ProgressionPlanner.LocalSurfaceRow(ctx));
	}
}
