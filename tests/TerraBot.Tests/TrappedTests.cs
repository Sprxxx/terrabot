using System;
using System.IO;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Measured runs M10j and M10k (world_eow): after crafting the Worm Food at the demon altar near (1890,408), a partial segment
/// of the walk to the corruption (1915,310) dug a staircase up from (1867,371) to (1872,366), under ebonstone the Silver
/// Pickaxe (45) cannot break. From there every walk ended "no node closer than the start in 6000 (start h 166, rows
/// 366..496)" for the last 12 game hours of the run, with 7 bombs and 2 dynamite carried and no recall potion left.
/// </summary>
public class TrappedTests
{
	private readonly ITestOutputHelper output;
	public TrappedTests(ITestOutputHelper o) => output = o;

	/// <summary>The bot's physics in M10k ("Physics params changed: run 3.33/6 acc 0.089 ... fall 10.01"): with the 6 px/tick
	/// run, a tile across costs less than a tile up, and the start's h is the climb's, 166 as the log has it.</summary>
	public static readonly PhysicsParams M10k = new() { MaxRunSpeed = 3.33f, AccRunSpeed = 6f, RunAcceleration = 0.089f, MaxFallSpeed = 10.01f };

	/// <summary>
	/// The M10k region dump (origin (1860,298); F at (1872,366), T at (1915,310)) over a fake world: 'X' (what the pickaxe
	/// cannot break) as unbreakable ebonstone, every solid cell an evil-biome tile (in game every step there cost the 150-tick
	/// off-limits extra, "Walk (1894,422)->(1893,422) 154.8t"), mud breaking in 12 ticks (the log's "StepUp ... 260.8t" with 4
	/// breaks), 1044 blocks. The game searched down to row 496; the dump stops at row 374, so the cave the bot came up from is
	/// stood in for by 200 rows of open cave under ledges the pickaxe can break, as the mud there is.
	/// </summary>
	public static (FakeGame game, BotContext ctx, TilePos goal) M10kPocket(ITestOutputHelper output)
	{
		string file = Path.Combine(AppContext.BaseDirectory, "Dumps", "nopath_1872_366_to_1915_310.txt");
		var rows = File.ReadAllLines(file).Skip(1).Where(l => l.Trim().Length > 0 && !l.StartsWith("bad ")).Select(l => l.Trim()).ToList();
		int w = rows[0].Length;
		for (int r = 0; r < 200; r++) {
			char[] row = new string(r % 4 == 3 ? '#' : '.', w).ToCharArray();
			if (r % 4 == 3)
				for (int k = 0; k < 3; k++)
					row[(r * 13) % (w - 4) + k] = '.';
			rows.Add(new string(row));
		}
		TilePos goal = default;
		string[] clean = rows.Select((r, y) => new string(r.Select((c, x) => {
			if (c == 'T')
				goal = new TilePos(x, y);
			return c switch { 'F' => 'S', 'T' => '#', 'X' => '@', 'U' => '#', ',' => '.', _ => c };
		}).ToArray())).ToArray();
		var game = new FakeGame(clean);
		for (int y = 0; y < game.Grid.Height; y++)
			for (int x = 0; x < game.Grid.Width; x++)
				if (game.Grid.Get(x, y).IsSolid())
					game.TileTypes[new TilePos(x, y)] = FakeGame.TileEbonstone;
		game.Blocks.TicksPerBlock = 12;
		game.Items[FakeGame.DirtBlock] = 1044;
		BotContext ctx = game.CreateContext(output);
		ctx.Physics = M10k;
		game.Refresh(ctx);
		return (game, ctx, goal);
	}

	/// <summary>Ticks a walk standing still until it has a plan to follow, or ends.</summary>
	private static SkillStatus PlanOnly(FakeGame game, BotContext ctx, WalkToSkill walk, int maxTicks)
	{
		walk.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < maxTicks && s == SkillStatus.Running && walk.Executor == null; t++) {
			game.Refresh(ctx);
			s = walk.Tick(ctx);
		}
		return s;
	}

	[Fact]
	public void M10k_TopOfAStaircaseDugUnderEbonstone_TheWalkStillFindsTheTunnelOut()
	{
		var (game, ctx, goal) = M10kPocket(output);
		var walk = new WalkToSkill(new GoalNearTile(goal, 4, ctx.Physics));
		SkillStatus s = PlanOnly(game, ctx, walk, 3000);
		output.WriteLine($"{s}: {walk.LastPlan} {walk.LastPlan?.Why}");
		var log = (TestLog)ctx.Log;
		// As in game: the segmented search gives up early, from the start of the staircase's top.
		Assert.Contains(log.Lines, l => l.Contains("no node closer than the start in 6000 (start h 166"));
		Assert.NotNull(walk.Executor);
		Assert.Equal(PathStatus.Found, walk.LastPlan.Status);
		Assert.True(walk.LastPlan.Steps.Count > 0 && walk.Goal.IsGoal(walk.LastPlan.Steps[^1].To), "the plan reaches the corruption");
		Assert.True(walk.LastPlan.Steps.Sum(st => st.CountActions(ActionKind.Break)) > 0, "out through the mud");
		// Once per start: a walk from there again goes no deeper (the cave would be flooded again for nothing).
		Assert.True(ctx.DeepSearchedFrom.Contains(walk.LastPlan.Start));
	}

	// ---------- blasting out ----------

	/// <summary>
	/// A pocket 14 wide sealed by what the pickaxe cannot break and a blast can; the goal past a wall 2 thick on the right.
	/// </summary>
	private static readonly string[] SealedPocket = {
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@..............@@............@@",
		"@@..............@@............@@",
		"@@..............@@............@@",
		"@@..............@@............@@",
		"@@.S............@@...........G@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
		"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@",
	};

	private static SkillStatus Run(FakeGame game, BotContext ctx, ISkill skill, int maxTicks)
	{
		skill.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < maxTicks && s == SkillStatus.Running; t++) {
			game.Refresh(ctx);
			s = skill.Tick(ctx);
			game.Step(ctx);
		}
		return s;
	}

	/// <summary>
	/// Upgraded Research's "Researched Consumables are infinite" (the user's game, 2026-10-17): a researched bomb stays in the
	/// bag when thrown. The throw is seen by the swing in hand, so one bomb opens the wall as with bombs used up, not one a swing
	/// until the wait for the stack to shrink runs out.
	/// </summary>
	[Fact]
	public void SealedPocket_BombsKeptByAResearchMod_OneThrowSeen_AndWalksOut()
	{
		var game = new FakeGame(SealedPocket);
		game.KeepsConsumables = true;
		game.Items[FakeGame.Bomb] = 3;
		BotContext ctx = game.CreateContext(output);
		var goal = new GoalStandAt(game.Grid.Markers['G'], ctx.Physics);
		var blast = new BlastOutSkill(goal);
		Assert.Equal(SkillStatus.Succeeded, Run(game, ctx, blast, 2000));
		Assert.Equal(1, game.Blasts);
		Assert.Equal(3, game.Count(FakeGame.Bomb));
		Assert.Equal(0, game.BlastHits);
		Assert.Equal(SkillStatus.Succeeded, Run(game, ctx, new WalkToSkill(goal), 3000));
	}

	[Fact]
	public void SealedPocket_WithBombs_ThrowsOneAtTheWallTowardsTheGoal_KeepsClearOfTheBlast_AndWalksOut()
	{
		var game = new FakeGame(SealedPocket);
		game.Items[FakeGame.Bomb] = 3;
		game.Items[FakeGame.Dynamite] = 1;
		BotContext ctx = game.CreateContext(output);
		var goal = new GoalStandAt(game.Grid.Markers['G'], ctx.Physics);

		// The trap: no walk gets out (the walls are all beyond the pickaxe).
		Assert.Equal(SkillStatus.Failed, Run(game, ctx, new WalkToSkill(goal), 2000));

		var blast = new BlastOutSkill(goal);
		SkillStatus s = Run(game, ctx, blast, 2000);
		output.WriteLine($"after the blast: body {game.Sim.State} at {game.Sim.Node}");
		output.WriteLine(game.Grid.ToAscii());
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(FakeGame.Bomb, blast.Plan.Explosive.ItemType); // the smaller blast does it
		Assert.Equal(2, game.Count(FakeGame.Bomb));
		Assert.Equal(1, game.Count(FakeGame.Dynamite));
		Assert.Equal(16, blast.Plan.Wall.X); // the wall between the bot and the goal, not the ceiling or the floor
		Assert.Equal(1, game.Blasts);
		Assert.Equal(0, game.BlastHits); // out of the blast's box when it went off
		Assert.False(game.Grid.Get(16, 6).IsSolid());
		var log = (TestLog)ctx.Log;
		Assert.Contains(log.Lines, l => l.StartsWith("BlastOut: Bomb at (16,"));
		Assert.Contains(log.Lines, l => l.StartsWith("BlastOut: threw a Bomb"));
		Assert.Contains(log.Lines, l => l.StartsWith("BlastOut: clear of the blast"));

		// And the walk goes through.
		Assert.Equal(SkillStatus.Succeeded, Run(game, ctx, new WalkToSkill(goal), 3000));
	}

	[Fact]
	public void PocketTooSmallToGetClear_ThrowsNothing() => TooSmall();

	/// <summary>
	/// Self-test blast_out_ebonstone (2026-10-17): the pocket is ebonstone, so the evil biome's off-limits extra (150 ticks a
	/// step) was in the planner's cost of the way out, and "the way out of the blast takes 932 ticks" (6 steps of 5.3 + 150)
	/// ruled out every throw. The fuse is measured in the ticks the moves take, not in what the route is worth avoiding.
	/// </summary>
	[Fact]
	public void SealedPocketOfEbonstone_TheWayOutIsTimedInTicks_NotInTheBiomesPenalty()
	{
		// The self-test's own rows (Scenarios.cs blast_out_ebonstone), ebonstone as unbreakable ebonstone-typed cells.
		string full = new('@', 34), row = "@@..............@@@.............@@", feet = "@@.S............@@@...........G.@@";
		var game = new FakeGame(full, full, full, row, row, row, row, feet, full, full, full, full);
		for (int y = 0; y < game.Grid.Height; y++)
			for (int x = 0; x < game.Grid.Width; x++)
				if (game.Grid.Get(x, y).IsSolid())
					game.TileTypes[new TilePos(x, y)] = FakeGame.TileEbonstone;
		game.Items[FakeGame.Bomb] = 3;
		BotContext ctx = game.CreateContext(output);
		Assert.True(ctx.OffLimitsForPaths(ctx.Node), "the pocket counts as the evil biome, off limits in weak gear");
		var goal = new GoalStandAt(game.Grid.Markers['G'], ctx.Physics);
		var blast = new BlastOutSkill(goal);
		Assert.Equal(SkillStatus.Succeeded, Run(game, ctx, blast, 2000));
		Assert.True(blast.Plan.Retreat.Cost < 100, $"way out {blast.Plan.Retreat.Cost:0} ticks");
		Assert.Equal(2, game.Blasts); // the wall is 3 thick: one bomb opens ground nearer, a second the way
		Assert.Equal(1, game.Count(FakeGame.Bomb));
		Assert.Equal(0, game.BlastHits);
		Assert.Equal(SkillStatus.Succeeded, Run(game, ctx, new WalkToSkill(goal), 3000));
	}

	/// <summary>
	/// Measured run U30: at the corruption chasm's flooded foot, (1767..1773, 425..429), blasts out towards the DemonAltar were
	/// refused "the way out of the blast takes 13005 ticks, more than the 180-tick fuse less the 25-tick throw" (and 9766,
	/// 11405, 5205): a few steps out, each priced at the head-under-water 200x, a price on drowning, not the ticks the steps take.
	/// </summary>
	[Fact]
	public void SealedPocketUnderWater_TheWayOutIsTimedAsTheBodyMoves_NotAtTheDrowningPrice()
	{
		string[] rows = SealedPocket.Select((r, y) => y >= 2 && y <= 6 ? new string(r.Select((c, x) => c == '.' && x < 16 ? '~' : c).ToArray()) : r).ToArray();
		var game = new FakeGame(rows);
		game.Items[FakeGame.Bomb] = 3;
		BotContext ctx = game.CreateContext(output);
		Assert.True(Body.HeadInLiquid(game.Grid, ctx.Node), "the head under");
		var goal = new GoalStandAt(game.Grid.Markers['G'], ctx.Physics);
		var blast = new BlastOutSkill(goal);
		SkillStatus s = Run(game, ctx, blast, 2000);
		output.WriteLine($"{s}: {blast.FailureReason}; way out {blast.Plan?.Retreat.Cost:0} ticks");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(blast.Plan.Retreat.Cost <= FakeGame.BombInfo.FuseTicks - FakeGame.BombInfo.UseTicks, $"way out {blast.Plan.Retreat.Cost:0} ticks");
		Assert.Equal(0, game.BlastHits);
	}

	private void TooSmall()
	{
		// 6 wide: anywhere in it is inside the bomb's 128 px box round a blast at the wall.
		string[] rows = {
			"@@@@@@@@@@@@@@@@@@@@",
			"@@@@@@@@@@@@@@@@@@@@",
			"@@......@@........@@",
			"@@......@@........@@",
			"@@.S....@@.......G@@",
			"@@@@@@@@@@@@@@@@@@@@",
			"@@@@@@@@@@@@@@@@@@@@",
		};
		var game = new FakeGame(rows);
		game.Items[FakeGame.Bomb] = 3;
		BotContext ctx = game.CreateContext(output);
		var blast = new BlastOutSkill(new GoalStandAt(game.Grid.Markers['G'], ctx.Physics));
		Assert.Equal(SkillStatus.Failed, Run(game, ctx, blast, 600));
		Assert.Contains("no way out of the blast", blast.FailureReason);
		Assert.Equal(3, game.Count(FakeGame.Bomb));
		Assert.Equal(0, game.Blasts);
	}

	[Fact]
	public void WallsNoBlastBreaks_ThrowsNothing()
	{
		var game = new FakeGame(SealedPocket);
		for (int y = 0; y < game.Grid.Height; y++)
			for (int x = 0; x < game.Grid.Width; x++)
				if (game.Grid.Get(x, y).IsSolid())
					game.NoExplode.Add(new TilePos(x, y)); // dungeon bricks, say
		game.Items[FakeGame.Bomb] = 3;
		BotContext ctx = game.CreateContext(output);
		var blast = new BlastOutSkill(new GoalStandAt(game.Grid.Markers['G'], ctx.Physics));
		Assert.Equal(SkillStatus.Failed, Run(game, ctx, blast, 600));
		Assert.Contains("no wall", blast.FailureReason);
		Assert.Equal(3, game.Count(FakeGame.Bomb));
	}

	[Fact]
	public void ABombThrownAtAWall_BouncesBackAndRolls_ThrownStraightUp_ItComesDownAtTheFeet()
	{
		var game = new FakeGame(SealedPocket);
		float cx = 14 * 16 + 16, cy = 7 * 16 - 42 + 21; // the body's centre standing at (14,6), the wall at x 16
		var (ax, _) = ExplosiveFlight.Rest(game.Grid, cx, cy, 16 * 16 + 8, 6 * 16 + 8, FakeGame.BombInfo, 180);
		output.WriteLine($"at the wall: comes to rest at x {ax:0}");
		Assert.True(ax < cx - 3 * 16, "off the wall at 0.4 of its speed, then rolling at 0.97 a tick: tiles back past the thrower");
		var (ux, uy) = ExplosiveFlight.Rest(game.Grid, cx, cy, cx, cy - 48, FakeGame.BombInfo, 180);
		Assert.Equal(cx, ux, 1);
		Assert.Equal(7 * 16 - 11, uy, 1); // on the floor (row 7), its 22 px box resting on it
	}

	[Fact]
	public void Retreat_IsClearOfTheBlastBox()
	{
		var p = PhysicsParams.Default;
		// A blast at the centre of cell (15,4) (248,72), box ±64 px: the hitbox standing at x must end left of 184.
		var clear = new GoalClearOfBlast(248f, 72f, 64f, p);
		Assert.True(clear.IsGoal(new TilePos(9, 4)));   // hitbox 150..170
		Assert.False(clear.IsGoal(new TilePos(10, 4))); // 166..186
		Assert.Equal(5 * 5 + 25 / (2 * 0.2f), BlastOutSkill.ThrowReachPx(FakeGame.BombInfo), 3);
	}
}
