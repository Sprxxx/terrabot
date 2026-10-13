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
}
