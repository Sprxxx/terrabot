using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Measured run U22 (anvil to full armour 111 game hours, U19 57; 48.9 of them on exploring legs): 18 of 42 ore
/// explorations failed after all 16 legs. From d4+14.7 to d4+22.4, 7 explorations in a row bounced 16 legs each between
/// (2004,472) and (2033,472), a corridor between two death zones at the gear's depth floor; and an unreachable dig target,
/// (2180,425), was picked 9 times across 4 explorations because with every way barred the barred one was taken anyway.
/// </summary>
public class ExploreCorridorTests
{
	private readonly ITestOutputHelper output;
	public ExploreCorridorTests(ITestOutputHelper output) => this.output = output;

	/// <summary>
	/// U22's corridor moved by (-1900,-400): stone 200 wide, a level tunnel at rows 70-72, columns 104-135 (feet row 72),
	/// the bot at (108,72); the depth floor at row 86 (U22: 486, CavernY 451 - 15 + a depth bonus of 50), so no digging
	/// down; deaths at (60,69) and (173,87), 30 tiles round (U22: (1960,469) and (2073,487)); the tunnel's two ends earlier
	/// legs' targets. Everything seen but, if <paramref name="aboveUnseen"/>, the rock above row 61.
	/// </summary>
	private (FakeGame Game, BotContext Ctx) Corridor(bool aboveUnseen)
	{
		var rows = new List<string>();
		for (int y = 0; y < 100; y++) {
			char[] r = new string(y == 99 ? '@' : 's', 200).ToCharArray();
			if (y >= 70 && y <= 72)
				for (int x = 104; x <= 135; x++)
					r[x] = '.';
			if (y == 72)
				r[108] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		if (aboveUnseen)
			for (int x = 0; x < 200; x++)
				for (int y = 0; y <= 60; y++)
					g.Unrevealed.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 86 - BotContext.ShallowMargin;
		Assert.Equal(86, ctx.DeepestRow);
		ctx.NoteDanger(new TilePos(60, 69), BotContext.DangerZoneTicks);
		ctx.NoteDanger(new TilePos(173, 87), BotContext.DangerZoneTicks);
		ctx.Explored.Add(new TilePos(104, 72));
		ctx.Explored.Add(new TilePos(133, 72));
		return (g, ctx);
	}

	/// <summary>Runs the exploration until it ends or has made <paramref name="maxLegs"/> legs; the leg targets in order.</summary>
	private (SkillStatus Status, List<TilePos> Legs) Run(FakeGame g, BotContext ctx, ExploreSkill explore, int maxLegs = ExploreSkill.MaxLegs)
	{
		var log = (TestLog)ctx.Log;
		int before = log.Lines.Count;
		explore.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < ExploreSkill.MaxTicks + 1 && s == SkillStatus.Running && explore.Legs <= maxLegs; t++) {
			g.Refresh(ctx);
			s = explore.Tick(ctx);
			g.Step(ctx);
		}
		var legs = new List<TilePos>();
		foreach (string l in log.Lines.Skip(before)) {
			Match m = Regex.Match(l, @"^Explore: leg \d+ to \((\d+),(\d+)\) \(");
			if (m.Success)
				legs.Add(new TilePos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
		}
		output.WriteLine($"{s} ({explore.FailureReason}) after {explore.Legs} legs, at {g.Sim.Node}: {string.Join(" ", legs)}");
		return (s, legs);
	}

	/// <summary>Legs whose target is within 5 tiles of the leg before last's (U22: 126 of 395, 32%; U19: 13%).</summary>
	private static int Bounces(List<TilePos> legs) => Enumerable.Range(2, System.Math.Max(0, legs.Count - 2)).Count(i => legs[i].ChebyshevTo(legs[i - 2]) <= 5);

	[Fact]
	public void ACorridorBetweenDeathZones_AtTheDepthFloor_IsNotPingPonged_TheRockAboveIsDugInto()
	{
		var (g, ctx) = Corridor(aboveUnseen: true);
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: 1);
		var (_, legs) = Run(g, ctx, explore, maxLegs: 8);
		int bounces = Bounces(legs);
		Assert.True(bounces <= 1, $"{bounces} of {legs.Count} legs went back to the leg before last's target");
		Assert.Contains(legs, l => l.Y <= 72 - ExploreSkill.FallbackDepth + ExploreSkill.FrontierReach);
	}

	[Fact]
	public void ACorridorDugAndSeenAllRound_EndsTheExplorationAtOnce_NotAfter16Legs()
	{
		var (g, ctx) = Corridor(aboveUnseen: false);
		ctx.Explored.Add(new TilePos(108, 52)); // the rock above dug and seen as well
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: 1);
		var (s, legs) = Run(g, ctx, explore);
		Assert.Equal(SkillStatus.Failed, s);
		Assert.True(legs.Count <= 1, $"{legs.Count} legs over ground explored already");
		Assert.Contains("explored already", explore.FailureReason);
	}

	[Fact]
	public void AtAZoneEdge_TheDigStopsShortOfIt_RatherThanGoingBackOverOldGround()
	{
		// The other way from the tunnel, the rock is unseen from column 96 on, but the death zone at (60,69) starts at
		// column 90: a step of 25 (to 83) is in it, one to its edge (91) is not.
		var (g, ctx) = Corridor(aboveUnseen: false);
		for (int x = 0; x <= 96; x++)
			for (int y = 0; y < 100; y++)
				g.Unrevealed.Add(new TilePos(x, y));
		ctx.Explored.Add(new TilePos(108, 52));
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: 1);
		explore.Start(ctx);
		g.Refresh(ctx);
		explore.Tick(ctx);
		string leg = ((TestLog)ctx.Log).Lines.First(l => l.StartsWith("Explore: leg 1 to"));
		output.WriteLine(leg);
		Match m = Regex.Match(leg, @"leg 1 to \((\d+),(\d+)\)");
		int x1 = int.Parse(m.Groups[1].Value);
		Assert.True(x1 > 90 && x1 < 108 - ExploreSkill.MinLegTiles + 1, $"leg 1 to column {x1}, not to the zone's edge");
		Assert.False(ctx.OffLimitsOutright(new TilePos(x1, int.Parse(m.Groups[2].Value))));
	}

	/// <summary>
	/// A level tunnel at rows 23-25, columns 30-60, the bot at (55,25) heading right, at the depth floor (row 30); a death
	/// 25 tiles round (25,25) reaching to column 50; the dig on to the right, and up both ways, found unreachable.
	/// </summary>
	private (FakeGame Game, BotContext Ctx) EveryWayBarred(TilePos unreachableAhead)
	{
		var rows = new List<string>();
		for (int y = 0; y < 50; y++) {
			char[] r = new string(y == 49 ? '@' : 's', 100).ToCharArray();
			if (y >= 23 && y <= 25)
				for (int x = 30; x <= 60; x++)
					r[x] = '.';
			if (y == 25)
				r[55] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.CavernY = 30 - BotContext.ShallowMargin;
		ctx.NoteDanger(new TilePos(25, 25), BotContext.DangerZoneTicks, radius: 25);
		ctx.UnreachableTiles.Add(unreachableAhead);
		ctx.UnreachableTiles.Add(new TilePos(57, 5));
		ctx.UnreachableTiles.Add(new TilePos(53, 5));
		return (g, ctx);
	}

	[Fact]
	public void EveryDigBarred_TheUnreachableOneIsNotTakenAnyway_TheExplorationEnds()
	{
		// U22, from (2155,425): (2180,425) found unreachable, (2130,425) in the zone of a crowd at (2105,424); leg 9, 13, 1, 5,
		// 9... to (2180,425), each "no path" after 4001 nodes.
		var (g, ctx) = EveryWayBarred(new TilePos(80, 25));
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: 1);
		var (s, legs) = Run(g, ctx, explore, maxLegs: 2);
		Assert.DoesNotContain(new TilePos(80, 25), legs);
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("barred", explore.FailureReason);
	}

	[Fact]
	public void ADigTargetBesideOneFoundUnreachable_IsBarredToo()
	{
		// U22: leg 1 to (2181,425) "no path", then leg 5 to (2180,425), one column off, "no path" the same way: the leg's walk
		// stops within 3 tiles of its target, so the two are one target.
		var (g, ctx) = EveryWayBarred(new TilePos(81, 25));
		var explore = new ExploreSkill("ore", _ => false, ExploreBias.Down, heading: 1);
		var (s, legs) = Run(g, ctx, explore, maxLegs: 2);
		Assert.DoesNotContain(legs, l => l.ChebyshevTo(new TilePos(81, 25)) <= 3);
		Assert.Equal(SkillStatus.Failed, s);
	}
}
