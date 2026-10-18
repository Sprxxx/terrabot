using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>world_wof E59 (2026-10-18): four deaths on the hellbridge, each reproduced here.</summary>
public class BridgeFightTests
{
	private readonly ITestOutputHelper output;
	public BridgeFightTests(ITestOutputHelper output) => this.output = output;

	private const int BatAi = 14;

	private (FakeGame, BotContext) Field()
	{
		FakeGame g = EarlyGameTests.Field(60);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.WeaponDamage = 12;
		g.Items[FakeGame.WoodenSword] = 1;
		g.Refresh(ctx);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new ProjectileModel(9f, 15, 0.1f, 16f);
		return (g, ctx);
	}

	private static EntityInfo Bat(BotContext ctx, float dxTiles, float dyTiles) =>
		new(9, 60, "Hellbat", ctx.Body.CenterX(ctx.Physics) + dxTiles * 16f - 8f, ctx.Body.CenterY(ctx.Physics) + dyTiles * 16f - 8f,
			16, 16, 0f, 0f, 46, 46, 30, BatAi);

	/// <summary>Deaths 2 and 4: a Hellbat or Demon in reach was swung at with the glove ("kills 0"), 664 arrows carried.</summary>
	[Fact]
	public void AFlierInReach_IsShot_WhenABowIsCarried()
	{
		var (g, ctx) = Field();
		g.ExtraHostiles.Add(Bat(ctx, 1.2f, -0.5f));
		g.Refresh(ctx);
		ctx.Combat.HasRanged = true;
		var fight = new FightSkill();
		fight.Start(ctx);
		fight.Tick(ctx);
		output.WriteLine(fight.Describe());
		Assert.Equal(ToolRequest.Ranged, ctx.Output.Tool);
	}

	[Fact]
	public void AFlierInReach_IsSwungAt_WithNoBow()
	{
		var (g, ctx) = Field();
		g.ExtraHostiles.Add(Bat(ctx, 1.2f, -0.5f));
		g.Refresh(ctx);
		ctx.Combat.HasRanged = false;
		var fight = new FightSkill();
		fight.Start(ctx);
		fight.Tick(ctx);
		Assert.NotEqual(ToolRequest.Ranged, ctx.Output.Tool);
	}

	/// <summary>Death 3: in lava under the bridge's end, the fight shot and waited "in the open" while the lava burned it to death.</summary>
	[Fact]
	public void InLava_TheFightStepsOutFirst()
	{
		var (g, ctx) = Field();
		TilePos n = ctx.Node;
		for (int x = n.X - 3; x <= n.X + 2; x++)
			for (int y = n.Y - 1; y <= n.Y; y++)
				g.Grid.Set(x, y, CellKind.Lava);
		g.ExtraHostiles.Add(Bat(ctx, 8f, -2f));
		g.Refresh(ctx);
		ctx.Combat.HasRanged = true;
		var fight = new FightSkill();
		fight.Start(ctx);
		fight.Tick(ctx);
		output.WriteLine(fight.Describe());
		Assert.Equal(1, ctx.Output.Horizontal); // the dry side is nearer to the right
		Assert.True(ctx.Output.Jump);
		Assert.False(ctx.Output.UseItem);
	}

	/// <summary>
	/// Death 1: "dodge right + jump 15" from (4074,1041), the deck's end at 4073: a dodge may run to the floor's last column (the old
	/// bounds) and the jump carries the body on off it. The bounds keep the last columns free; from inside them, only inward.
	/// </summary>
	[Fact]
	public void ADodge_KeepsOffTheDecksLastColumns()
	{
		var (g, ctx) = Field();
		TilePos n = ctx.Node;
		for (int x = n.X + 2; x < n.X + 12; x++)
			g.Grid.Set(x, n.Y + 1, CellKind.Empty); // the deck's floor ends at n.X + 1
		var deck = (n.X - 20, n.X + 1, n.Y);
		float w = ctx.Physics.Width;
		// From the floor's last columns: may not move right at all.
		var (_, maxX) = ShotDodge.DeckBounds(g.Grid, n.X, n.X * 16f, w, deck);
		output.WriteLine($"from x {n.X}: max {maxX / 16f:0.0}");
		Assert.True(maxX <= n.X * 16f + 0.01f, $"may move right to {maxX / 16f:0.0}");
		// Ten columns in: free to move, but not into the last three columns.
		var (lo, hi) = ShotDodge.DeckBounds(g.Grid, n.X - 10, (n.X - 10) * 16f, w, deck);
		Assert.True(hi <= (n.X + 2 - ShotDodge.DeckEdgeKeep) * 16f - w + 0.01f && hi > (n.X - 10) * 16f, $"{lo / 16f:0.0}..{hi / 16f:0.0}");
	}

	/// <summary>
	/// E60: thrown from x 4073, two columns past the stand, the doll lay on ground at x 4079 past the lava, and the walk to pick it up
	/// left the deck (a Hellbat killed the bot under it). The summon column wants lava 2 columns past the landing window too, and the
	/// throw is made only where the columns it lands in from the body's column are lava.
	/// </summary>
	[Fact]
	public void TheSummonColumn_HasLavaPastTheLandingWindow_AndAThrowFromTooFarOnIsRefused()
	{
		var (g, ctx) = Field();
		int row = 20;
		for (int x = 0; x < 60; x++)
			for (int y = row + 1; y <= row + 6; y++)
				g.Grid.Set(x, y, CellKind.Empty);
		for (int x = 10; x <= 16; x++)
			g.Grid.Set(x, row + 6, CellKind.Lava); // the lava sea under x 10-16
		for (int x = 17; x < 30; x++)
			g.Grid.Set(x, row + 6, CellKind.Solid); // ground past it
		Assert.Equal(10, ProgressionPlanner.LastLavaColumn(ctx, 20, 0, 1, row)); // the old pick: lands 13-16, no margin
		Assert.Equal(8, ProgressionPlanner.LastLavaColumn(ctx, 20, 0, 1, row, ProgressionPlanner.ThrowSlack));
		Assert.True(ProgressionPlanner.LavaBeyond(ctx, 8, 1, row));
		Assert.False(ProgressionPlanner.LavaBeyond(ctx, 12, 1, row)); // from two columns on: lands 15-18, onto the ground
	}
}
