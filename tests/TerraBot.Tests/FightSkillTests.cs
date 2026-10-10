using System;
using System.Collections.Generic;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// FightSkill against a fake enemy: it loses 10 life per tick of a swing that happens in reach while the bot faces
/// it. Crude, but enough to check targeting, chasing over terrain, turning around and finishing.
/// </summary>
public class FightSkillTests
{
	private readonly ITestOutputHelper output;

	public FightSkillTests(ITestOutputHelper output) => this.output = output;

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m);
		public void Debug(string m) { }
	}

	/// <summary>Runs a fight; <paramref name="enemyStartsAt"/> is the enemy's feet tile; it walks 0.5 px/tick if <paramref name="walker"/>.</summary>
	private (SkillStatus status, FightSkill skill, int enemyLife) Fight(ArrayTileGrid g, TilePos enemyStartsAt, int facing = 1, bool walker = false, int maxTicks = 3000, bool fleeing = false)
	{
		PhysicsParams p = PhysicsParams.Default;
		var sim = new SimWorld(g, p, g.Markers['S']);
		float ex = enemyStartsAt.X * 16f, ey = (enemyStartsAt.Y + 1) * 16f - 24f;
		int life = 60;
		var ctx = new BotContext {
			Grid = g,
			Log = new Log(output),
			Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "test sword", Facing = facing },
		};
		var skill = new FightSkill();
		ctx.Body = sim.State;
		ctx.Hostiles = new[] { Enemy(ex, ey, life) };
		skill.Start(ctx);
		SkillStatus status = SkillStatus.Running;
		int swingTimer = 0;
		for (int t = 0; t < maxTicks && status == SkillStatus.Running; t++) {
			ctx.Body = sim.State;
			ctx.Hostiles = life > 0 ? new[] { Enemy(ex, ey, life) } : Array.Empty<EntityInfo>();
			ctx.Combat.ItemAnimation = swingTimer;
			status = skill.Tick(ctx);
			MoveInput o = ctx.Output;
			// Facing changes like vanilla HorizontalMovement: only between swings.
			if (swingTimer == 0 && o.Horizontal != 0)
				ctx.Combat.Facing = o.Horizontal;
			if (o.UseItem && o.Tool == ToolRequest.Weapon) {
				if (swingTimer == 0)
					swingTimer = 20;
				float cx = sim.State.CenterX(p), cy = sim.State.CenterY(p);
				float dx = ex + 12f - cx, dy = ey + 12f - cy;
				bool hit = Math.Abs(dx) <= 10f + 30f + 12f && Math.Abs(dy) <= 21f + 12f + 8f && Math.Sign(dx) == ctx.Combat.Facing;
				if (hit && life > 0)
					life -= 10;
			}
			if (swingTimer > 0)
				swingTimer--;
			if (walker && life > 0)
				ex -= 0.5f;
			if (fleeing && life > 0 && ex < 58 * 16f)
				ex += 1f;
			sim.Step(o);
		}
		output.WriteLine($"{status} after {sim.Ticks} ticks, enemy life {life}, kills {skill.Kills}");
		return (status, skill, life);
	}

	private static EntityInfo Enemy(float x, float y, int life) =>
		new(7, 1, "Test Slime", x, y, 24, 24, 0, 0, life, 60, 10);

	[Fact]
	public void KillsEnemyAhead_OnFlatGround()
	{
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..S...........................",
			"##############################");
		var (status, skill, life) = Fight(g, new TilePos(15, 4));
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(life <= 0);
		Assert.Equal(1, skill.Kills);
	}

	[Fact]
	public void TurnsAround_ForEnemyBehind()
	{
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............S...............",
			"##############################");
		var (status, _, life) = Fight(g, new TilePos(11, 4), facing: 1);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(life <= 0);
	}

	[Fact]
	public void ChasesEnemy_UpALedge()
	{
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..............############....",
			"..............############....",
			"..S...........############....",
			"##############################");
		var (status, _, life) = Fight(g, new TilePos(22, 7));
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(life <= 0);
	}

	[Fact]
	public void IgnoresEnemyBehindAWall()
	{
		// Seen in game: a demon eye on the far side of a solid wall interrupted a build and got swung and shot at.
		var g = ArrayTileGrid.Parse(
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"..........@...................",
			"......S...@...................",
			"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@");
		var ctx = new BotContext { Grid = g, Combat = new CombatInfo { WeaponReach = 30f } };
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		ctx.Body = sim.State;
		ctx.Hostiles = new[] { Enemy(13 * 16f, 9 * 16f, 60) };
		Assert.False(TerraBot.Bot.Brain.ProgressionPlanner.InDanger(ctx));
		var (status, _, life) = Fight(g, new TilePos(13, 10));
		Assert.Equal(SkillStatus.Succeeded, status); // gave up on it and calmed down
		Assert.Equal(60, life);
	}

	[Fact]
	public void ChasesWalkingEnemy()
	{
		var g = ArrayTileGrid.Parse(
			"................................................",
			"................................................",
			"................................................",
			"................................................",
			"..S.............................................",
			"################################################");
		var (status, _, life) = Fight(g, new TilePos(30, 4), walker: true);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(life <= 0);
	}

	[Fact]
	public void EngagedEnemy_IsChasedBeyondEngageRadius()
	{
		var g = ArrayTileGrid.Parse(
			"..............................................................",
			"..............................................................",
			"..............................................................",
			"..............................................................",
			"..S...........................................................",
			"##############################################################");
		// Starts 20 tiles away (inside the engage radius) and walks away at 0.5 px/tick: must still be caught.
		var (status, _, life) = Fight(g, new TilePos(22, 4), walker: false, maxTicks: 3000, fleeing: true);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.True(life <= 0);
	}

	[Fact]
	public void NoWeapon_Fails()
	{
		var g = ArrayTileGrid.Parse("..S.......", "##########");
		var ctx = new BotContext { Grid = g, Log = new Log(output), Combat = new CombatInfo() };
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		ctx.Body = sim.State;
		ctx.Hostiles = new[] { Enemy(100, 0, 60) };
		var skill = new FightSkill();
		skill.Start(ctx);
		Assert.Equal(SkillStatus.Failed, skill.Tick(ctx));
	}
}
