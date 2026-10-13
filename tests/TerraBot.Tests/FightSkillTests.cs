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
	/// <summary>The context of the last <see cref="Fight"/>.</summary>
	private BotContext lastCtx;

	private (SkillStatus status, FightSkill skill, int enemyLife) Fight(ArrayTileGrid g, TilePos enemyStartsAt, int facing = 1, bool walker = false, int maxTicks = 3000, bool fleeing = false, bool unhittable = false)
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
		lastCtx = ctx;
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
			ctx.Tick = t;
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
				if (hit && life > 0 && !unhittable)
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
	public void BadlyHurt_WithoutPotions_RetreatsInsteadOfFighting()
	{
		FakeGame g = EarlyGameTests.Field(60);
		g.Life = 20; // of 100
		g.AddSlime(7, 8, 24);
		g.Items[FakeGame.DirtBlock] = 10;
		BotContext ctx = g.CreateContext(output);
		var brain = new Bot.Brain.BotBrain();
		brain.SetAuto(true, ctx, Bot.Brain.ProgressionPlanner.Plans.Early);
		g.Refresh(ctx);
		brain.Tick(ctx);
		output.WriteLine(brain.Describe());
		Assert.IsType<ShelterSkill>(brain.Current);
		Assert.True(((ShelterSkill)brain.Current).Retreat);
		// Full life again: the same slime is just fought.
		var brain2 = new Bot.Brain.BotBrain();
		g.Life = -1;
		brain2.SetAuto(true, ctx, Bot.Brain.ProgressionPlanner.Plans.Early);
		g.Refresh(ctx);
		brain2.Tick(ctx);
		Assert.IsType<FightSkill>(brain2.Current);
	}

	[Fact]
	public void Retreat_DigsBesideBlocksItCannotMine()
	{
		// Seen in game: straight down was under the work bench (not mineable from below); the dig found no path.
		FakeGame g = EarlyGameTests.Field(60);
		TilePos s = g.Grid.Markers['S'];
		for (int x = s.X; x <= s.X + 1; x++)
			for (int y = s.Y + 1; y <= s.Y + 5; y++)
				g.Blocks.Unbreakable.Add(new TilePos(x, y));
		BotContext ctx = g.CreateContext(output);
		var shelter = ShelterSkill.Recover(0.8f);
		shelter.Start(ctx);
		output.WriteLine(shelter.Describe());
		Assert.DoesNotContain($"WalkTo ({s.X},", shelter.Describe());
		Assert.DoesNotContain($"WalkTo ({s.X + 1},", shelter.Describe());
	}

	[Fact]
	public void SwordOnly_HoldsGroundAgainstAFlier_AndSwingsAsItComes()
	{
		// Classic demon eyes cost 150 life while the bot chased their "feet tile"; it now stands and meets them.
		ArrayTileGrid g = ArrayTileGrid.Parse(new string('.', 60), new string('.', 60), new string('.', 60), new string('.', 60),
			new string('.', 60), "..........S" + new string('.', 49), new string('#', 60));
		var ctx = new BotContext { Grid = g, Log = new Log(output), Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "test sword", Facing = 1 } };
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		ctx.Body = sim.State;
		float cx = ctx.Body.CenterX(PhysicsParams.Default), cy = ctx.Body.CenterY(PhysicsParams.Default);
		EntityInfo Eye(float dx) => new(3, 2, "Demon Eye", cx + dx - 15, cy - 40, 30, 32, -2f, 0.5f, 60, 60, 18, AiStyle: 2);
		var skill = new FightSkill();
		ctx.Hostiles = new[] { Eye(8 * 16f) };
		skill.Start(ctx);
		skill.Tick(ctx);
		output.WriteLine($"far: {skill.Describe()} horizontal {ctx.Output.Horizontal} use {ctx.Output.UseItem}");
		Assert.Equal(0, ctx.Output.Horizontal);
		Assert.False(ctx.Output.UseItem);
		ctx.Hostiles = new[] { Eye(3 * 16f) };
		skill.Tick(ctx);
		output.WriteLine($"near: {skill.Describe()} use {ctx.Output.UseItem}");
		Assert.True(ctx.Output.UseItem);
	}

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
	public void GivesUpOnEnemyItCannotHurtEvenInReach()
	{
		// Seen in game: a red slime 2.9 tiles away (behind a slope) was swung at for a whole game day: in reach, so the
		// "no closer" give-up never fired, and ignored enemies within 3 tiles were still fought.
		var g = ArrayTileGrid.Parse(
			"..............................",
			"..............................",
			"..............................",
			"..............................",
			"..........S...................",
			"@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@");
		var (status, _, life) = Fight(g, new TilePos(12, 4), unhittable: true);
		Assert.Equal(SkillStatus.Succeeded, status);
		Assert.Equal(60, life);
		Assert.True(lastCtx.IgnoredEvenClose(7));
		lastCtx.Hostiles = new[] { Enemy(12 * 16f, 5 * 16f - 24f, 60) };
		Assert.False(TerraBot.Bot.Brain.ProgressionPlanner.InDanger(lastCtx)); // not a reason to start another fight
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
	public void DefendLeash_IsMeasuredFromWhereTheFightStarted()
	{
		// Seen in game (fight_two_slimes_both_sides): chasing the slime on the left took the bot 28 tiles from the one on
		// the right, which had been 15 tiles away at the start; measured from the bot it was beyond the leash and left alive.
		string row = new('.', 60), floor = new('#', 60);
		var start = ArrayTileGrid.Parse(row, row, row, row, row.Remove(30, 1).Insert(30, "S"), floor);
		var later = ArrayTileGrid.Parse(row, row, row, row, row.Remove(17, 1).Insert(17, "S"), floor);
		var ctx = new BotContext { Grid = start, Log = new Log(output), Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "test sword" } };
		ctx.Body = new SimWorld(start, PhysicsParams.Default, start.Markers['S']).State;
		EntityInfo left = Enemy(18 * 16f, 5 * 16f - 24f, 60), right = new(8, 1, "Test Slime", 45 * 16f, 5 * 16f - 24f, 24, 24, 0, 0, 60, 60, 10);
		ctx.Hostiles = new[] { left, right };
		var skill = new FightSkill();
		skill.Start(ctx);
		skill.Tick(ctx);
		// The left one is dead, the bot stands where it was: the right one is 28 tiles off, 15 from the start.
		ctx.Body = new SimWorld(later, PhysicsParams.Default, later.Markers['S']).State;
		ctx.Hostiles = new[] { right };
		skill.Tick(ctx);
		Assert.NotEqual("watching", skill.State);
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
