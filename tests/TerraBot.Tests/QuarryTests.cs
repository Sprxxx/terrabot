using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Bulk mining of a block that is everywhere (stone for the hellbridge): what is in the pickaxe's reach is mined from where
/// the bot stands, before any walk (MineSkill.InReach). Seen in game (measured run world_wof M12d, Silver Pickaxe, pick 45 at
/// 11 ticks a hit): 735 Stone Blocks in 72,000 ticks, about 98 ticks a block, each block a target of its own with a walk to
/// it and a walk to its drop ("WalkTo start: target (2406,257) -> StandAt(2406,257)" 4 times in a row, then (2406,256) 4
/// times), and the first 21,600 ticks of the job on the stair the bot had built over a valley, mined back block by block.
/// The fake's drops are pulled in and picked up as in the game here (FakeGame.VanillaPickup), not at once within 42 px.
/// </summary>
public class QuarryTests
{
	private readonly ITestOutputHelper output;
	public QuarryTests(ITestOutputHelper output) => this.output = output;

	/// <summary>
	/// Stone at pick 45 and 11 ticks a hit: 3 hits of 45 for its 100 (TerrariaBlockActions.BreakTicks: (hits - 1) * HitTicks + 1),
	/// the "pick 45 (slot 2, 11t/hit)" of run M12d.
	/// </summary>
	public const float StoneTicksPick45 = 23f;

	/// <summary>Sky over a solid stone field 30 rows deep, unbreakable rock under it; the bot on the stone at column 50.</summary>
	public static FakeGame StoneField(int width = 100, int sky = 20, int depth = 30)
	{
		var rows = Enumerable.Repeat(new string('.', width), sky).ToList();
		char[] feet = rows[sky - 1].ToCharArray();
		feet[width / 2] = 'S';
		rows[sky - 1] = new string(feet);
		for (int y = 0; y < depth; y++)
			rows.Add(new string('s', width));
		rows.Add(new string('@', width));
		FakeGame g = new(rows.ToArray());
		g.Blocks.TicksPerBlock = StoneTicksPick45;
		g.VanillaPickup = true;
		return g;
	}

	/// <summary>
	/// A stone bridge one block thick over a chasm 40 rows deep (deeper than a safe fall, 25), stone ground on both sides, the
	/// bot in the bridge's middle: as the stair run M12d's stone job began on, up to 48 rows over the valley's floor.
	/// </summary>
	public static FakeGame BridgeOverChasm(int width = 120, int sky = 20, int depth = 40, int chasmFrom = 40, int chasmTo = 80)
	{
		var rows = Enumerable.Repeat(new string('.', width), sky).ToList();
		char[] feet = rows[sky - 1].ToCharArray();
		feet[(chasmFrom + chasmTo) / 2] = 'S';
		rows[sky - 1] = new string(feet);
		for (int y = 0; y < depth; y++) {
			char[] r = new string('s', width).ToCharArray();
			if (y > 0)
				for (int x = chasmFrom; x <= chasmTo; x++)
					r[x] = '.';
			rows.Add(new string(r));
		}
		rows.Add(new string('@', width));
		FakeGame g = new(rows.ToArray());
		g.Blocks.TicksPerBlock = StoneTicksPick45;
		g.VanillaPickup = true;
		return g;
	}

	/// <summary>Counts the walks begun (WalkToSkill's "WalkTo start", a debug line for a sub-skill's quiet walk).</summary>
	private sealed class WalkCountingLog : IBotLog
	{
		private readonly ITestOutputHelper o;
		public int Walks;
		public WalkCountingLog(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o?.WriteLine("INFO  " + m);
		public void Warn(string m) => o?.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o?.WriteLine("ERROR " + m);
		public void Debug(string m)
		{
			if (m.StartsWith("WalkTo start"))
				Walks++;
		}
	}

	/// <summary>Runs the job to its end: its status, the ticks it took, the ticks in each state (mining, walking, collecting) and the walks begun.</summary>
	private (SkillStatus Status, int Ticks, Dictionary<string, int> States, int Walks) Run(FakeGame g, MineSkill mine, int maxTicks, out BotContext ctx)
	{
		ctx = g.CreateContext(null);
		var log = new WalkCountingLog(null);
		ctx.Log = log;
		var states = new Dictionary<string, int>();
		mine.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		int t = 0;
		for (; t < maxTicks && s == SkillStatus.Running; t++) {
			g.Refresh(ctx);
			s = mine.Tick(ctx);
			string state = mine.State.Split(' ')[0];
			states[state] = states.GetValueOrDefault(state) + 1;
			g.Step(ctx);
		}
		return (s, t, states, log.Walks);
	}

	/// <summary>
	/// 300 Stone Blocks from a solid field. Measured on this fake (break 23 ticks): one target and one pickup at a time (the
	/// code before InReach), 9,972 ticks, 33.2 ticks a block, 4,272 of them collecting (14 a block) and 881 walks begun; in
	/// reach from where it stands, 8,692 ticks, 29.0 a block, 1,491 collecting (5 a block) and 52 walks. The bounds sit between.
	/// </summary>
	[Fact]
	public void BulkStone_MinesWhatIsInReach_NotAWalkPerBlock()
	{
		FakeGame g = StoneField();
		const int need = 300;
		var mine = new MineSkill(FakeGame.StoneBlock, FakeGame.TileStone, need);
		var (s, ticks, states, walks) = Run(g, mine, 60 * 60 * 10, out BotContext ctx);
		float perBlock = ticks / (float)need;
		output.WriteLine($"{s} after {ticks} ticks: {perBlock:0.0} ticks a block (break {StoneTicksPick45}); mined {mine.Mined}, broken {g.Sim.Broken}, "
			+ $"placed {g.Sim.Placed}, stone {g.Count(FakeGame.StoneBlock)}, {walks} walks begun, at {ctx.Node}");
		output.WriteLine(string.Join(", ", states.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(g.Count(FakeGame.StoneBlock) >= need);
		Assert.True(perBlock <= 31f, $"{perBlock:0.0} ticks a block (33.2 one at a time, 29.0 in reach)");
		Assert.True(states.GetValueOrDefault("collecting") <= need * 8, $"{states.GetValueOrDefault("collecting")} ticks collecting (14 a block one at a time, 5 in reach)");
		Assert.True(walks <= need / 2, $"{walks} walks begun for {need} blocks (881 one at a time, 52 in reach)");
	}

	/// <summary>
	/// 100 Stone Blocks with the bot on a bridge over a chasm: the bridge is left (it is over a fall), the ground mined. Measured
	/// on this fake: the code before, 4,153 ticks, 41.5 a block, 14 of the bridge's 41 blocks mined, 341 walks begun; now 2,879
	/// ticks, 28.8 a block, the bridge whole, 21 walks.
	/// </summary>
	[Fact]
	public void BulkStone_LeavesTheBridgeOverAChasm_ForTheGround()
	{
		FakeGame g = BridgeOverChasm();
		const int need = 100;
		var mine = new MineSkill(FakeGame.StoneBlock, FakeGame.TileStone, need);
		var (s, ticks, states, walks) = Run(g, mine, 60 * 60 * 10, out BotContext ctx);
		int bridge = Enumerable.Range(40, 41).Count(x => g.Grid.Get(x, 20).IsSolid());
		float perBlock = ticks / (float)need;
		output.WriteLine($"{s} after {ticks} ticks: {perBlock:0.0} ticks a block; mined {mine.Mined}, broken {g.Sim.Broken}, placed {g.Sim.Placed}, "
			+ $"stone {g.Count(FakeGame.StoneBlock)}, bridge {bridge}/41, {walks} walks begun, at {ctx.Node}");
		output.WriteLine(string.Join(", ", states.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(41, bridge);
		Assert.True(perBlock <= 36f, $"{perBlock:0.0} ticks a block (41.5 before, 28.8 now)");
	}

	[Fact]
	public void OverAFall_IsNoFloorWithinASafeFall()
	{
		var grid = ArrayTileGrid.Parse("#..", "...", "...", "..#", "###");
		Assert.True(MineSkill.OverAFall(grid, new TilePos(0, 0), 2)); // the floor 4 rows down
		Assert.False(MineSkill.OverAFall(grid, new TilePos(0, 0), 4));
	}
}
