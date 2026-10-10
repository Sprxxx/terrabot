using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The early-game chain in a fake world: trees fall after 30 ticks of axe swings at their base and drop one
/// wood per trunk tile around the base; drops within 42 px are picked up; a work bench is 10 wood by hand and
/// a wooden sword 7 wood next to a placed bench.
/// </summary>
public class EarlyGameTests
{
	private readonly ITestOutputHelper output;
	public EarlyGameTests(ITestOutputHelper output) => this.output = output;

	private static readonly GameIds Ids = new() { Wood = 9, WorkBenchItem = 36, WorkBenchTile = 18, WoodenSword = 24, WoodenHammer = 196, Acorn = 27 };

	private sealed class FakeGame : IWorldInfo, IInventory, ICrafter
	{
		public readonly ArrayTileGrid Grid;
		public readonly SimWorld Sim;
		public readonly Dictionary<TilePos, int> Trees = new(); // base -> height
		public readonly Dictionary<int, int> Items = new();
		public readonly List<DropInfo> Drops = new();
		public readonly Dictionary<int, TilePos> Stations = new();
		private readonly Dictionary<TilePos, int> chopTicks = new();
		private int nextDropId = 1000;

		public FakeGame(ArrayTileGrid g)
		{
			Grid = g;
			Sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S']);
		}

		public bool IsTreeTrunk(int x, int y) => Trees.Any(t => t.Key.X == x && y <= t.Key.Y && y > t.Key.Y - t.Value);

		public bool CanPlaceItem(int itemType, int x, int y)
		{
			if (itemType != Ids.WorkBenchItem)
				return false;
			TilePos n = Sim.Node;
			bool overlapsBody = (x + 1 >= n.X && x <= n.X + 1) && y >= n.Y - 2 && y <= n.Y;
			return !overlapsBody && Grid.Get(x, y) == CellKind.Empty && Grid.Get(x + 1, y) == CellKind.Empty
				&& Grid.Get(x, y + 1).IsSolid() && Grid.Get(x + 1, y + 1).IsSolid() && !IsTreeTrunk(x, y) && !IsTreeTrunk(x + 1, y);
		}

		public int Count(int itemType) => Items.TryGetValue(itemType, out int n) ? n : 0;
		public bool HasRoomFor(int itemType) => true;

		public CraftResult TryCraft(int itemType)
		{
			int wood = itemType == Ids.WorkBenchItem ? 10 : itemType == Ids.WoodenSword ? 7 : -1;
			if (wood < 0)
				return CraftResult.NoRecipe;
			if (Count(Ids.Wood) < wood)
				return CraftResult.Missing;
			if (itemType == Ids.WoodenSword && !(Stations.TryGetValue(Ids.WorkBenchTile, out TilePos b) && b.ChebyshevTo(Sim.Node) <= 4))
				return CraftResult.NeedsStation;
			Items[Ids.Wood] -= wood;
			Items[itemType] = Count(itemType) + 1;
			return CraftResult.Crafted;
		}

		public string Describe(int itemType) => itemType == Ids.WoodenSword ? "7 Wood @ Work Bench" : "10 Wood";

		/// <summary>Applies this tick's controls to the fake world (axe, placing, pickup), then moves the body.</summary>
		public void Step(MoveInput o)
		{
			if (o.Tool == ToolRequest.Axe && o.UseItem && o.HasAim) {
				var aim = new TilePos(o.AimX, o.AimY);
				if (Trees.TryGetValue(aim, out int height) && Math.Abs(Sim.Node.X + 1 - aim.X) <= 4) {
					chopTicks[aim] = chopTicks.GetValueOrDefault(aim) + 1;
					if (chopTicks[aim] >= 30) {
						Trees.Remove(aim);
						for (int i = 0; i < height; i++)
							Drops.Add(new DropInfo(nextDropId++, Ids.Wood, "Wood", 1, (aim.X + (i % 5) - 2) * 16f + 4, aim.Y * 16f + 4, 8, 8));
					}
				}
			}
			if (o.Tool == ToolRequest.Item && o.ToolItem == Ids.WorkBenchItem && o.UseItem && o.HasAim
				&& Count(Ids.WorkBenchItem) > 0 && CanPlaceItem(Ids.WorkBenchItem, o.AimX, o.AimY)) {
				Grid.Set(o.AimX, o.AimY, CellKind.Platform);
				Grid.Set(o.AimX + 1, o.AimY, CellKind.Platform);
				Stations[Ids.WorkBenchTile] = new TilePos(o.AimX, o.AimY);
				Items[Ids.WorkBenchItem]--;
			}
			Sim.Step(o);
			float cx = Sim.State.CenterX(PhysicsParams.Default), cy = Sim.State.CenterY(PhysicsParams.Default);
			foreach (DropInfo d in Drops.ToList())
				if (MathF.Abs(d.CenterX - cx) <= 42 && MathF.Abs(d.CenterY - cy) <= 42) {
					Items[d.Type] = Count(d.Type) + d.Stack;
					Drops.Remove(d);
				}
		}

		/// <summary>Like the game adapter, stations are only rescanned every 20 ticks.</summary>
		private Dictionary<int, TilePos> seenStations = new();
		private int refreshes;

		public void Refresh(BotContext ctx)
		{
			ctx.Body = Sim.State;
			ctx.Tick = refreshes;
			if (refreshes++ % 20 == 0)
				seenStations = new Dictionary<int, TilePos>(Stations);
			TilePos n = Sim.Node;
			ctx.Seen = new Surroundings {
				Trees = Trees.Keys.OrderBy(t => Math.Abs(t.X - n.X)).ToList(),
				Stations = seenStations,
			};
			ctx.Drops = Drops.ToList();
		}
	}

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m);
		public void Debug(string m) { }
	}

	private static ArrayTileGrid Field(int width)
	{
		var g = new ArrayTileGrid(width, 30);
		g.Fill(0, 25, width - 1, 29, CellKind.Solid);
		g.Markers['S'] = new TilePos(3, 24);
		return g;
	}

	private (FakeGame game, BotContext ctx) Setup(ArrayTileGrid g, params int[] treeColumns)
	{
		var game = new FakeGame(g);
		foreach (int x in treeColumns)
			game.Trees[new TilePos(x, 24)] = 8;
		var ctx = new BotContext {
			Grid = g, Log = new Log(output), World = game, Inventory = game, Crafter = game, Ids = Ids, AxePower = 35,
			Combat = new CombatInfo(),
		};
		game.Refresh(ctx);
		return (game, ctx);
	}

	private SkillStatus RunSkill(FakeGame game, BotContext ctx, ISkill skill, int maxTicks)
	{
		skill.Start(ctx);
		SkillStatus s = SkillStatus.Running;
		for (int t = 0; t < maxTicks && s == SkillStatus.Running; t++) {
			game.Refresh(ctx);
			s = skill.Tick(ctx);
			game.Step(ctx.Output);
		}
		return s;
	}

	[Fact]
	public void GatherWood_ChopsAndCollects()
	{
		var (game, ctx) = Setup(Field(80), 20, 35);
		SkillStatus s = RunSkill(game, ctx, new GatherWoodSkill(Ids.Wood, 12), 6000);
		output.WriteLine($"{s}, wood {game.Count(Ids.Wood)}, trees left {game.Trees.Count}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(game.Count(Ids.Wood) >= 12);
	}

	[Fact]
	public void GatherWood_NoAxe_Fails()
	{
		var (game, ctx) = Setup(Field(80), 20);
		ctx.AxePower = 0;
		Assert.Equal(SkillStatus.Failed, RunSkill(game, ctx, new GatherWoodSkill(Ids.Wood, 5), 2000));
	}

	[Fact]
	public void PlaceItem_PutsBenchBesideTheBot()
	{
		var (game, ctx) = Setup(Field(40));
		game.Items[Ids.WorkBenchItem] = 1;
		Assert.Equal(SkillStatus.Succeeded, RunSkill(game, ctx, new PlaceItemSkill(Ids.WorkBenchItem), 600));
		Assert.True(game.Stations.ContainsKey(Ids.WorkBenchTile));
	}

	[Fact]
	public void Craft_ReportsMissingStation()
	{
		var (game, ctx) = Setup(Field(40));
		game.Items[Ids.Wood] = 20;
		var craft = new CraftSkill(Ids.WoodenSword);
		Assert.Equal(SkillStatus.Failed, RunSkill(game, ctx, craft, 100));
		Assert.Equal(CraftResult.NeedsStation, craft.LastResult);
	}

	[Fact]
	public void AutoBrain_RunsTheEarlyGameChain()
	{
		var (game, ctx) = Setup(Field(110), 15, 25, 35, 45, 55, 65, 75);
		var brain = new BotBrain();
		brain.SetAuto(true, ctx);
		int t = 0;
		for (; t < 60 * 120 && !(brain.Planner.Complete && brain.Current == null); t++) {
			game.Refresh(ctx);
			brain.Tick(ctx);
			game.Step(ctx.Output);
		}
		output.WriteLine($"after {t} ticks: {brain.Describe()} | wood {game.Count(Ids.Wood)}, sword {game.Count(Ids.WoodenSword)}, bench placed {game.Stations.ContainsKey(Ids.WorkBenchTile)}");
		Assert.True(brain.Planner.Complete, brain.Planner.Status);
		Assert.True(game.Stations.ContainsKey(Ids.WorkBenchTile));
		Assert.Equal(1, game.Count(Ids.WoodenSword));
		Assert.True(game.Count(Ids.Wood) >= EarlyGamePlanner.WoodStockpile);
	}
}
