using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;

namespace TerraBot.Tests;

/// <summary>Footing next to a deep shaft, and the danger/fight distances for ignored enemies (world_eye run 13).</summary>
public class FootingTests
{
	private static ArrayTileGrid Shaft(int depth)
	{
		// Column 3 is a shaft: node (3,1) covers columns 3 and 4 and is held up by column 4 only.
		var rows = new System.Collections.Generic.List<string> { "........", "........" };
		for (int i = 0; i < depth; i++)
			rows.Add("###.####");
		rows.Add("########");
		return ArrayTileGrid.Parse(rows.ToArray());
	}

	[Fact]
	public void OneColumnOverADeepShaft_IsPrecarious()
	{
		var deep = Shaft(30);
		Assert.True(Body.IsPrecarious(deep, new TilePos(3, 1), 24));
		Assert.False(Body.IsPrecarious(deep, new TilePos(4, 1), 24)); // both columns on rock
		var shallow = Shaft(3);
		Assert.False(Body.IsPrecarious(shallow, new TilePos(3, 1), 24)); // a short hole: landing in it is harmless
	}

	[Fact]
	public void PrecariousFooting_CostsExtra()
	{
		var deep = Shaft(30);
		var moves = new MoveContext(deep, PhysicsParams.Default);
		Assert.Equal(10f + moves.PrecariousTicks, moves.Adjust(10f, new TilePos(3, 1)));
		Assert.Equal(10f, moves.Adjust(10f, new TilePos(5, 1)));
	}

	[Fact]
	public void IgnoredEnemy_AtTheFightEdge_DoesNotStartAFight()
	{
		// Seen in game: an ignored slime at 2.9-3.0 tiles started a fight that dropped it at 3.01, every 2 s, for an hour.
		var g = ArrayTileGrid.Parse("....................", "....................", "....................", "..........S.........", "####################");
		var ctx = new BotContext { Grid = g, Combat = new CombatInfo { WeaponReach = 30f } };
		ctx.Body = new SimWorld(g, PhysicsParams.Default, g.Markers['S']).State;
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		var slime = new EntityInfo(7, 1, "Test Slime", cx + 2.6f * 16f - 12f, cy - 12f, 24, 24, 0, 0, 30, 45, 10);
		ctx.Hostiles = new[] { slime };
		Assert.True(ProgressionPlanner.InDanger(ctx));
		ctx.Ignore(7, 1800);
		Assert.False(ProgressionPlanner.InDanger(ctx)); // 2.6 tiles: beyond the danger distance for ignored enemies
		Assert.True(ctx.Fightable(7, slime.DistanceTo(cx, cy))); // but an ongoing fight keeps it
	}

	[Fact]
	public void StepHeldUpOnlyByAMinedCell_IsRejected()
	{
		// world_eye run 15: a staircase dug up through rock planned a walk back onto a block an earlier step mined.
		var g = ArrayTileGrid.Parse("......", "......", "######");
		var to = new TilePos(1, 1);
		var none = new System.Collections.Generic.HashSet<TilePos>();
		Assert.False(PathSearch.SupportOnlyFromBroken(g, to, none));
		var one = new System.Collections.Generic.HashSet<TilePos> { new(1, 2) };
		Assert.False(PathSearch.SupportOnlyFromBroken(g, to, one)); // the other column still holds it
		var both = new System.Collections.Generic.HashSet<TilePos> { new(1, 2), new(2, 2) };
		Assert.True(PathSearch.SupportOnlyFromBroken(g, to, both));
		var air = ArrayTileGrid.Parse("......", "......", "......");
		Assert.False(PathSearch.SupportOnlyFromBroken(air, to, both)); // not held by the grid anyway (mid-jump)
	}

	[Fact]
	public void DeepestRow_FollowsTheArmour()
	{
		// world_eye run 15: exploring for iron in no armour went 200 tiles down; skeletons and bats killed the bot.
		var ctx = new BotContext { Combat = new CombatInfo() };
		Assert.Equal(int.MaxValue, ctx.DeepestRow); // depth unknown: no limit
		ctx.CavernY = 400;
		Assert.Equal(400 + BotContext.ShallowMargin, ctx.DeepestRow);
		ctx.Combat.Defense = BotContext.CavernDefense; // e.g. full iron armour
		Assert.Equal(400 + BotContext.CavernDepth, ctx.DeepestRow);
		ctx.Combat.Defense = BotContext.DeepDefense;
		Assert.Equal(int.MaxValue, ctx.DeepestRow);
	}

	[Fact]
	public void DiagonalDigDown_ClearsTheHeadCellInFront()
	{
		// world_eye run 17: a dig down to the right left the block at head height in front; the bot pressed right into
		// it for 46 ticks. The body steps sideways at its old height before it drops.
		var (g, b) = TestBlockActions.Parse(
			"@@@@@@@@",
			"@...#@@@",
			"@......@",
			"@.S....@",
			"@@@##@@@",
			"@@@##@@@",
			"@@@@@@@@");
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = false };
		var edges = new System.Collections.Generic.List<PathStep>();
		foreach (IMoveType m in ctx.Moves)
			m.Expand(ctx, g.Markers['S'], edges);
		PathStep down = Assert.Single(edges, e => e.Kind == MoveKind.Drop && e.To == new TilePos(3, 4));
		Assert.Contains(down.Actions, a => a.Cell == new TilePos(4, 1));
	}

	[Fact]
	public void SlopeInTheFeetRow_HoldsTheBodyUp()
	{
		// world_eye run 19: resting on a slope with air under both columns, every plan failed with "invalid start".
		var g = ArrayTileGrid.Parse(
			"........",
			"........",
			"...S/...",
			"........",
			"########");
		Assert.True(Body.IsStartable(g, g.Markers['S']));
		Assert.False(Body.IsStandable(g, g.Markers['S'])); // not a landing spot for other moves
		var air = ArrayTileGrid.Parse("........", "........", "...S....", "........", "########");
		Assert.False(Body.IsStartable(air, air.Markers['S']));
	}

	[Fact]
	public void PlanStartsFromASlopeOverAir()
	{
		var (g, b) = TestBlockActions.Parse(
			"............",
			"............",
			"...S/.......",
			"............",
			"..........G.",
			"############");
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = true };
		PathResult r = new AStarPathfinder().FindPath(ctx, g.Markers['S'], new GoalStandAt(g.Markers['G'], PhysicsParams.Default));
		Assert.Equal(PathStatus.Found, r.Status);
	}

	[Fact]
	public void DangerZone_AroundWhereTheBotWasKilled_ExpiresLater()
	{
		// Antlions in the underground desert killed the bot in nearly every run: stay away from there for a while.
		var ctx = new BotContext { Tick = 100 };
		ctx.NoteDeathByEnemy(new TilePos(2276, 380));
		Assert.True(ctx.InDangerZone(new TilePos(2290, 400)));
		Assert.False(ctx.InDangerZone(new TilePos(2276, 300)));
		ctx.Tick = 100 + BotContext.DangerZoneTicks + 1;
		Assert.False(ctx.InDangerZone(new TilePos(2290, 400)));
	}

	[Fact]
	public void JumpThroughASlope_IsNotPlanned()
	{
		// world_eye run 30: jumps whose path crossed a slope landed a tile short (slopes are solid from below).
		string[] rows = {
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"......................",
			"..............G.......",
			"..........############",
			"..........############",
			"..S.......############",
			"######################" };
		PathResult open = TestUtil.Plan(ArrayTileGrid.Parse(rows));
		PathStep jump = Assert.Single(open.Steps, st => st.Kind == MoveKind.Jump);
		// A slope on a cell the jump sweeps on the way up, outside the start and landing bodies.
		TilePos mid = jump.Template.BodyCells.Select(c => new TilePos(jump.From.X + c.X, jump.From.Y + c.Y))
			.First(c => c.Y < jump.From.Y - 2 && !(c.X >= jump.To.X && c.X <= jump.To.X + 1 && c.Y >= jump.To.Y - 2) && rows[c.Y][c.X] == '.');
		string[] blocked = (string[])rows.Clone();
		blocked[mid.Y] = blocked[mid.Y].Remove(mid.X, 1).Insert(mid.X, "/");
		PathResult r = TestUtil.Plan(ArrayTileGrid.Parse(blocked));
		Assert.DoesNotContain(r.Steps, st => st.Kind == MoveKind.Jump && st.From == jump.From && st.To == jump.To && st.Template == jump.Template);
	}

	[Fact]
	public void PartialPlanThroughRock_NeverStandsOnWhatItDigs()
	{
		// world_eye run 35: climbing out of a mine through solid rock, the search ran out of nodes; the partial plan's
		// end was past a step held up only by blocks earlier steps dig, so it failed there and re-planned (40 times).
		var rows = Enumerable.Repeat(new string('#', 70), 60).ToList();
		for (int y = 54; y <= 56; y++)
			rows[y] = new string('#', 60) + "......" + "####";
		rows[56] = new string('#', 61) + "S" + "....####";
		rows[59] = new string('@', 70);
		var (g, b) = TestBlockActions.Parse(rows.ToArray());
		var ctx = new MoveContext(g, PhysicsParams.Default) { Blocks = b, AllowBreak = true, AllowPlace = false };
		TilePos start = g.Markers['S'];
		var search = new PathSearch(ctx, start, new GoalNearTile(new TilePos(start.X, 3), 2, PhysicsParams.Default), new SearchLimits { MaxExpansions = 2000 });
		search.Step(1e9);
		Assert.Equal(PathStatus.Partial, search.Result.Status);
		Assert.True(search.Result.Steps.Count > 3, search.Result.ToString());
		// Replay: every step valid on the grid as the earlier steps left it.
		for (int i = 0; i < search.Result.Steps.Count; i++) {
			PathStep st = search.Result.Steps[i];
			Assert.True(ctx.IsStepValid(st), $"step {i} {st} invalid after the earlier steps' digging");
			foreach (BlockAction a in st.Actions ?? System.Array.Empty<BlockAction>())
				g.Set(a.Cell.X, a.Cell.Y, a.Kind == ActionKind.Break ? CellKind.Empty : CellKind.Solid);
		}
	}

	[Fact]
	public void BelowTheDepthLimit_EdgesCostMore_AndTheContextFollowsTheGear()
	{
		// world_eye run 36: home from the depth limit through the open caverns below it; crawdads killed the bot twice.
		var flat = ArrayTileGrid.Parse("........", "........", "........", "########");
		var moves = new MoveContext(flat, PhysicsParams.Default) { AvoidBelowRow = 1 };
		Assert.Equal(10f, moves.Adjust(10f, new TilePos(2, 1)));
		Assert.Equal(10f + moves.BelowRowTicks, moves.Adjust(10f, new TilePos(2, 2)));

		FakeGame g = ProgressionTests.Mine();
		BotContext ctx = g.CreateContext(null);
		ctx.CavernY = 30;
		Assert.Equal(ctx.DeepestRow, ctx.Moves.AvoidBelowRow);
		ctx.Combat.Defense = BotContext.DeepDefense; // full gear: no limit
		Assert.Equal(int.MaxValue, ctx.Moves.AvoidBelowRow);
	}
}
