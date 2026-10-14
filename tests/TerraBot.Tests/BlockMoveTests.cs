using System;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Mining and placing: planner decisions on hand-made grids, then closed-loop runs that edit the grid.</summary>
public class BlockMoveTests
{
	private readonly ITestOutputHelper output;

	public BlockMoveTests(ITestOutputHelper output) => this.output = output;

	// Corridor 3 tall inside an unbreakable shell ('@'), blocked by a breakable 3-high wall at x = 10.
	private static readonly string[] Corridor = {
		"@@@@@@@@@@@@@@@@@@@@",
		"@.........#........@",
		"@.........#........@",
		"@.S.......#......G.@",
		"@@@@@@@@@@@@@@@@@@@@",
	};

	private PathResult Plan(ArrayTileGrid g, TestBlockActions b, bool allowBreak = true, bool allowPlace = true)
	{
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = allowBreak, AllowPlace = allowPlace };
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default));
		output.WriteLine(r.ToString());
		foreach (PathStep s in r.Steps)
			output.WriteLine("  " + s);
		return r;
	}

	private static int Count(PathResult r, ActionKind kind) => r.Steps.Sum(s => s.CountActions(kind));

	[Fact]
	public void Tunnel_ThroughWall()
	{
		var (g, b) = TestBlockActions.Parse(Corridor);
		PathResult r = Plan(g, b);
		Assert.Equal(PathStatus.Found, r.Status);
		var broken = r.Steps.Where(s => s.HasActions).SelectMany(s => s.Actions).Select(a => a.Cell).Distinct().ToList();
		Assert.Equal(new[] { new TilePos(10, 1), new TilePos(10, 2), new TilePos(10, 3) }, broken.OrderBy(c => c.Y));
	}

	[Fact]
	public void Tunnel_NotUsed_WithoutPermission()
	{
		var (g, b) = TestBlockActions.Parse(Corridor);
		Assert.Equal(PathStatus.NoPath, Plan(g, b, allowBreak: false).Status);
	}

	[Fact]
	public void SmallBump_IsWalkedNotDug()
	{
		var (g, b) = TestBlockActions.Parse(
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"..........................",
			"..S.......###.......G.....",
			"##########################");
		PathResult r = Plan(g, b);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.All(r.Steps, s => Assert.False(s.HasActions));
	}

	[Fact]
	public void UnbreakableWall_IsRespected()
	{
		string[] rows = (string[])Corridor.Clone();
		for (int y = 1; y <= 3; y++)
			rows[y] = rows[y].Remove(10, 1).Insert(10, "@");
		var (g, b) = TestBlockActions.Parse(rows);
		b.BlockCount = 0;
		Assert.Equal(PathStatus.NoPath, Plan(g, b).Status);
	}

	[Fact]
	public void SandAbove_IsNotUndermined()
	{
		string[] rows = (string[])Corridor.Clone();
		rows[0] = rows[0].Remove(10, 1).Insert(10, "%"); // sand resting on the wall's top cell
		var (g, b) = TestBlockActions.Parse(rows);
		b.BlockCount = 0;
		Assert.Equal(PathStatus.NoPath, Plan(g, b).Status);
	}

	[Fact]
	public void LavaAbove_IsNotOpened()
	{
		string[] rows = (string[])Corridor.Clone();
		rows[0] = rows[0].Remove(10, 1).Insert(10, "!"); // lava pocket right over the wall: it would pour in
		var (g, b) = TestBlockActions.Parse(rows);
		b.BlockCount = 0;
		Assert.Equal(PathStatus.NoPath, Plan(g, b).Status);
	}

	[Fact]
	public void LavaBelow_IsNoReasonNotToDig()
	{
		// Liquid never flows up: the wall over a lava pocket is opened (Baritone's rule). It used to count as a lava
		// neighbour, and ore and life crystals over lava were left alone (user, 2026-10-14: solve it, don't ban it).
		string[] rows = (string[])Corridor.Clone();
		rows[4] = rows[4].Remove(10, 1).Insert(10, "!"); // lava pocket right under the wall
		var (g, b) = TestBlockActions.Parse(rows);
		b.BlockCount = 0;
		Assert.Equal(PathStatus.Found, Plan(g, b).Status);
	}

	private static readonly string[] Shaft = {
		"..........",
		"..........",
		"..........",
		"..S.......",
		"##########",
		"##########",
		"##########",
		"#........#",
		"#........#",
		"#..G.....#",
		"##########",
	};

	[Fact]
	public void DigDown_IntoSealedRoom()
	{
		var (g, b) = TestBlockActions.Parse(Shaft);
		PathResult r = Plan(g, b, allowPlace: false);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.Contains(r.Steps, s => s.Kind == MoveKind.Drop && s.Dir == 0 && s.HasActions);
		Assert.Equal(6, r.Steps.SelectMany(s => s.Actions ?? Array.Empty<BlockAction>()).Select(a => a.Cell).Distinct().Count());
	}

	// A 9-high cliff: two tiles more than any jump can reach.
	private static readonly string[] Cliff = {
		"......................",
		"......................",
		"......................",
		"..............G.......",
		"..........############",
		"..........############",
		"..........############",
		"..........############",
		"..........############",
		"..........############",
		"..........############",
		"..........############",
		"..S.......############",
		"######################",
	};

	[Fact]
	public void Pillar_UpCliff_WhenDiggingIsOff()
	{
		var (g, b) = TestBlockActions.Parse(Cliff);
		PathResult r = Plan(g, b, allowBreak: false);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.Contains(r.Steps, s => s.Kind == MoveKind.Pillar);
		Assert.True(Count(r, ActionKind.Place) >= 3);
	}

	[Fact]
	public void Cliff_Unreachable_WithoutBlocksOrDigging()
	{
		var (g, b) = TestBlockActions.Parse(Cliff);
		b.BlockCount = 0;
		Assert.Equal(PathStatus.NoPath, Plan(g, b, allowBreak: false).Status);
	}

	// 12-wide pit with lava at the bottom: far beyond jump reach.
	private static readonly string[] Pit = {
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..............................",
		"..S.......................G...",
		"########............##########",
		"########............##########",
		"########............##########",
		"########!!!!!!!!!!!!##########",
		"##############################",
	};

	[Fact]
	public void Bridge_AcrossWidePit()
	{
		var (g, b) = TestBlockActions.Parse(Pit);
		PathResult r = Plan(g, b, allowBreak: false);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.True(Count(r, ActionKind.Place) >= 5, $"places {Count(r, ActionKind.Place)}");
		Assert.All(r.Steps, s => Assert.Equal(7, s.To.Y));
	}

	/// <summary>27 wide, 10 deep, dry: the shape from the first in-game Milestone 2 test.</summary>
	private static string[] WideDryPit()
	{
		var rows = new System.Collections.Generic.List<string>();
		for (int i = 0; i < 7; i++)
			rows.Add(new string('.', 40));
		rows.Add("..S" + new string('.', 33) + "G...");
		for (int i = 0; i < 10; i++)
			rows.Add("#####" + new string('.', 27) + "########");
		rows.Add(new string('#', 40));
		return rows.ToArray();
	}

	[Fact]
	public void WidePit_WithPlentyOfBlocks_IsBridged()
	{
		var (g, b) = TestBlockActions.Parse(WideDryPit());
		b.BlockCount = 999;
		PathResult r = Plan(g, b, allowBreak: false);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.All(r.Steps, s => Assert.Equal(7, s.To.Y)); // never goes down into the pit
		Assert.True(Count(r, ActionKind.Place) >= 15, $"places {Count(r, ActionKind.Place)}");
	}

	[Fact]
	public void WidePit_WithFewBlocks_SpendsFewerBlocks()
	{
		var (g, b) = TestBlockActions.Parse(WideDryPit());
		b.BlockCount = 20; // not even enough for a bridge
		PathResult r = Plan(g, b, allowBreak: false);
		Assert.Equal(PathStatus.Found, r.Status);
		Assert.True(Count(r, ActionKind.Place) < 15, $"places {Count(r, ActionKind.Place)}");
	}

	[Fact]
	public void SandInPath_IsMinedTopDown()
	{
		string[] rows = (string[])Corridor.Clone();
		rows[1] = rows[1].Remove(10, 1).Insert(10, "%"); // the wall's top block is sand, inside the corridor
		var (g, b) = TestBlockActions.Parse(rows);
		PathResult r = Plan(g, b, allowPlace: false);
		Assert.Equal(PathStatus.Found, r.Status);
		PathStep dig = r.Steps.First(s => s.HasActions);
		Assert.Equal(new TilePos(10, 1), dig.Actions[0].Cell);

		var (g2, b2) = TestBlockActions.Parse(rows);
		var (status, _, sim) = Run(g2, b2, allowPlace: false);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(0, sim.Undermined);
	}

	// ---------- closed loop: WalkToSkill + SimWorld editing the grid ----------

	private Log lastLog;

	private (SkillStatus status, WalkToSkill skill, SimWorld sim) Run(ArrayTileGrid g, TestBlockActions b, bool allowBreak = true, bool allowPlace = true, int maxTicks = 6000)
	{
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S'], b);
		lastLog = new Log(output);
		var ctx = new BotContext { Grid = g, Blocks = b, AllowBreak = allowBreak, AllowPlace = allowPlace, Log = lastLog };
		var skill = new WalkToSkill(g.Markers['G']);
		ctx.Body = sim.State;
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			status = skill.Tick(ctx);
			sim.Step(ctx.Output);
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks at {sim.Node}, replans {skill.Replans}, broken {sim.Broken}, placed {sim.Placed}");
		output.WriteLine(g.ToAscii());
		return (status, skill, sim);
	}

	[Fact]
	public void Sim_TunnelsThroughWall()
	{
		var (g, b) = TestBlockActions.Parse(Corridor);
		var (status, skill, sim) = Run(g, b);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(3, sim.Broken);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Sim_DigsDownIntoRoom()
	{
		var (g, b) = TestBlockActions.Parse(Shaft);
		var (status, skill, sim) = Run(g, b, allowPlace: false);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(6, sim.Broken);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Sim_PillarsUpCliff()
	{
		var (g, b) = TestBlockActions.Parse(Cliff);
		var (status, skill, sim) = Run(g, b, allowBreak: false);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(sim.Placed >= 3);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Sim_DigsUpThroughACeiling()
	{
		// world_eye run 49: a pocket whose only way on was straight up through the rock above (walls that cannot be mined).
		var (g, b) = TestBlockActions.Parse(
			"..........",
			"..........",
			"...G......",
			"@@@##@@@@@",
			"@@@##@@@@@",
			"@@@##@@@@@",
			"@@@..@@@@@",
			"@@@..@@@@@",
			"@@@S.@@@@@",
			"@@@@@@@@@@");
		b.BlockCount = 20;
		var (status, skill, sim) = Run(g, b);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(sim.Broken >= 6, $"broken {sim.Broken}");
	}

	[Fact]
	public void Sim_RunningOutOfBlocks_ReplansCleanly()
	{
		var (g, b) = TestBlockActions.Parse(Cliff);
		b.BlockCount = 2;
		var (status, skill, sim) = Run(g, b, allowBreak: false, maxTicks: 3000);
		Assert.Equal(SkillStatus.Failed, status);
		Assert.Equal(2, sim.Placed);
		Assert.Contains(lastLog.Lines, l => l.Contains("Blocked"));
		Assert.DoesNotContain(lastLog.Lines, l => l.Contains("OffPath"));
	}

	[Fact]
	public void Sim_BridgesWideDryPit()
	{
		var (g, b) = TestBlockActions.Parse(WideDryPit());
		b.BlockCount = 999;
		var (status, skill, sim) = Run(g, b, allowBreak: false);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(sim.Placed >= 15);
		Assert.Equal(0, skill.Replans);
	}

	[Fact]
	public void Sim_BridgesAcrossPit()
	{
		var (g, b) = TestBlockActions.Parse(Pit);
		var (status, skill, sim) = Run(g, b, allowBreak: false);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(sim.Placed >= 5);
		Assert.Equal(0, skill.Replans);
	}

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public System.Collections.Generic.List<string> Lines { get; } = new();
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) { Lines.Add(m); o.WriteLine("INFO  " + m); }
		public void Warn(string m) { Lines.Add(m); o.WriteLine("WARN  " + m); }
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m + " " + ex);
		public void Debug(string m) => o.WriteLine("DEBUG " + m);
	}
}
