using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// A small fake Terraria for brain/skill tests, on top of <see cref="SimWorld"/>. Uses the vanilla item, tile and
/// recipe numbers the bot needs up to an iron pickaxe (recipes checked against the decompiled Recipe.SetupRecipes,
/// tML 2026.08), in an "iron world" (lead ore does not exist):
/// <list type="bullet">
/// <item>trees fall after 30 ticks of axe swings at their base and drop one wood per trunk tile around it;</item>
/// <item>mined stone/ore/dirt drops its item where the cell was; drops within 42 px are picked up;</item>
/// <item>crafting checks ingredients (recipe groups included) and stations within 4 tiles of the body;</item>
/// <item>stations are placed as 2×1 platform-like tiles (the real furnace is 3×2; footprints are the game
/// adapter's business);</item>
/// <item>slimes (AI style 1) sit still, take 10 damage per in-reach swing and drop 2 gel.</item>
/// </list>
/// </summary>
public sealed class FakeGame : IWorldInfo, IInventory, ICrafter, IGameKnowledge
{
	// Vanilla IDs (ItemID / TileID).
	public const int Wood = 9, WorkBench = 36, WoodenSword = 24, Gel = 23, Torch = 8, StoneBlock = 3, DirtBlock = 2;
	public const int Furnace = 33, IronOre = 11, IronBar = 22, IronAnvil = 35, IronPickaxe = 1;
	public const int LeadOre = 700, LeadBar = 704, LeadAnvil = 716, LeadPickaxe = 3497, WoodPlatform = 94, IronFence = 2333;
	public const int TileDirt = 0, TileStone = 1, TileIron = 6, TileLead = 167, TileAnvil = 16, TileFurnace = 17, TileBench = 18;
	public const int SlimeAi = 1;

	public static readonly GameIds Ids = new() {
		Wood = Wood, WorkBenchItem = WorkBench, WorkBenchTile = TileBench, WoodenSword = WoodenSword, WoodenHammer = 196, Acorn = 27,
		Gel = Gel, Torch = Torch, FurnaceTile = TileFurnace, AnvilTile = TileAnvil, IronTierPickaxe = IronPickaxe,
	};

	public readonly ArrayTileGrid Grid;
	public readonly TestBlockActions Blocks;
	public readonly SimWorld Sim;
	public readonly Dictionary<TilePos, int> Trees = new(); // base -> height
	public readonly Dictionary<TilePos, int> TileTypes = new(); // solid cells that are not dirt
	public readonly Dictionary<int, int> Items = new();
	public readonly List<DropInfo> Drops = new();
	public readonly Dictionary<int, TilePos> Stations = new();
	public readonly List<(int id, float x, float y, int life)> Slimes = new();
	public bool IsNight;
	public int Crafts;
	private readonly Dictionary<TilePos, int> chopTicks = new();
	private int nextDropId = 1000;
	private int swingTimer;
	private int facing = 1;

	private readonly Dictionary<int, List<RecipeInfo>> recipes = new();

	/// <summary>
	/// Rows like the unit-test grids plus 's' stone and 'i' iron ore (solid, typed) and 'T' tree base (a tree of
	/// height 8 standing on the cell below). 'S' is the start node.
	/// </summary>
	public FakeGame(params string[] rows)
	{
		var plain = new string[rows.Length];
		for (int y = 0; y < rows.Length; y++) {
			var chars = rows[y].ToCharArray();
			for (int x = 0; x < chars.Length; x++) {
				char c = chars[x];
				if (c is 's' or 'i') {
					TileTypes[new TilePos(x, y)] = c == 's' ? TileStone : TileIron;
					chars[x] = '#';
				}
				else if (c == 'T') {
					Trees[new TilePos(x, y)] = 8;
					chars[x] = '.';
				}
			}
			plain[y] = new string(chars);
		}
		(Grid, Blocks) = TestBlockActions.Parse(plain);
		Blocks.BlockCount = 0;
		Sim = new SimWorld(Grid, PhysicsParams.Default, Grid.Markers['S'], Blocks);
		Sim.Mined += OnMined;

		Add(new RecipeInfo { Result = WorkBench, Ingredients = new[] { new Ingredient(10, Wood) } });
		Add(new RecipeInfo { Result = WoodenSword, Ingredients = new[] { new Ingredient(7, Wood) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = Torch, ResultStack = 3, Ingredients = new[] { new Ingredient(1, Gel), new Ingredient(1, Wood) } });
		Add(new RecipeInfo { Result = Furnace, Ingredients = new[] { new Ingredient(20, StoneBlock), new Ingredient(4, Wood), new Ingredient(3, Torch) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = IronBar, Ingredients = new[] { new Ingredient(3, IronOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = LeadBar, Ingredients = new[] { new Ingredient(3, LeadOre) }, Stations = new[] { TileFurnace } });
		Add(new RecipeInfo { Result = IronAnvil, Ingredients = new[] { new Ingredient(5, IronBar) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = LeadAnvil, Ingredients = new[] { new Ingredient(5, LeadBar) }, Stations = new[] { TileBench } });
		Add(new RecipeInfo { Result = IronPickaxe, Ingredients = new[] { new Ingredient(10, IronBar), new Ingredient(3, Wood) }, Stations = new[] { TileAnvil } });
		Add(new RecipeInfo { Result = LeadPickaxe, Ingredients = new[] { new Ingredient(10, LeadBar), new Ingredient(3, Wood) }, Stations = new[] { TileAnvil } });
		// A cycle like vanilla's: platforms from wood and wood back from platforms.
		Add(new RecipeInfo { Result = WoodPlatform, ResultStack = 2, Ingredients = new[] { new Ingredient(1, Wood) } });
		Add(new RecipeInfo { Result = Wood, Ingredients = new[] { new Ingredient(2, WoodPlatform) } });
		// And one between crafted items (tML's reverse wall recipes), listed before the ore recipe on purpose: iron fences
		// come from iron bars, so "bars from fences" is never a way to get bars.
		Add(new RecipeInfo { Result = IronFence, ResultStack = 4, Ingredients = new[] { new Ingredient(1, IronBar) }, Stations = new[] { TileAnvil } });
		recipes[IronBar].Insert(0, new RecipeInfo { Result = IronBar, Ingredients = new[] { new Ingredient(4, IronFence) } });
	}

	private void Add(RecipeInfo r)
	{
		if (!recipes.TryGetValue(r.Result, out List<RecipeInfo> list))
			recipes[r.Result] = list = new List<RecipeInfo>();
		list.Add(r);
	}

	public BotContext CreateContext(ITestOutputHelper output, string plan = null)
	{
		var ctx = new BotContext {
			Grid = Grid, Log = new TestLog(output), World = this, Inventory = this, Crafter = this, Knowledge = this,
			Ids = Ids, AxePower = 35, Blocks = Blocks, Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "test sword" },
		};
		Refresh(ctx);
		return ctx;
	}

	// ---------- IGameKnowledge ----------

	public IReadOnlyList<RecipeInfo> RecipesFor(int itemType) => recipes.TryGetValue(itemType, out List<RecipeInfo> l) ? l : Array.Empty<RecipeInfo>();

	public ItemSource SourceOf(int itemType) => itemType switch {
		Wood => new ItemSource(SourceKind.Tree),
		Gel => new ItemSource(SourceKind.Hunt, NpcAiStyle: SlimeAi),
		StoneBlock => new ItemSource(SourceKind.Mine, TileStone),
		DirtBlock => new ItemSource(SourceKind.Mine, TileDirt),
		IronOre => new ItemSource(SourceKind.Mine, TileIron),
		_ => ItemSource.None, // lead ore: not in this world
	};

	public IReadOnlyList<int> ItemsPlacing(int tileType) => tileType switch {
		TileBench => new[] { WorkBench },
		TileFurnace => new[] { Furnace },
		TileAnvil => new[] { IronAnvil, LeadAnvil },
		_ => Array.Empty<int>(),
	};

	public int TilePlacedBy(int itemType) => itemType switch {
		WorkBench => TileBench, Furnace => TileFurnace, IronAnvil or LeadAnvil => TileAnvil, _ => -1,
	};

	public string ItemName(int itemType) => $"item{itemType}";
	public string TileName(int tileType) => $"tile{tileType}";

	// ---------- IWorldInfo ----------

	public bool IsTreeTrunk(int x, int y) => Trees.Any(t => t.Key.X == x && y <= t.Key.Y && y > t.Key.Y - t.Value);

	public bool CanPlaceItem(int itemType, int x, int y)
	{
		if (TilePlacedBy(itemType) < 0)
			return false;
		TilePos n = Sim.Node;
		bool overlapsBody = (x + 1 >= n.X && x <= n.X + 1) && y >= n.Y - 2 && y <= n.Y;
		return !overlapsBody && Grid.Get(x, y) == CellKind.Empty && Grid.Get(x + 1, y) == CellKind.Empty
			&& Grid.Get(x, y + 1).IsSolid() && Grid.Get(x + 1, y + 1).IsSolid() && !IsTreeTrunk(x, y) && !IsTreeTrunk(x + 1, y);
	}

	public int TileTypeAt(int x, int y)
	{
		if (!Grid.Get(x, y).IsSolid())
			return -1;
		return TileTypes.TryGetValue(new TilePos(x, y), out int t) ? t : TileDirt;
	}

	public IReadOnlyList<TilePos> FindTiles(int tileType, TilePos around, int radius)
	{
		var found = new List<TilePos>();
		for (int x = around.X - radius; x <= around.X + radius; x++)
			for (int y = around.Y - radius; y <= around.Y + radius; y++)
				if (Grid.InBounds(x, y) && TileTypeAt(x, y) == tileType && !Blocks.Unbreakable.Contains(new TilePos(x, y)))
					found.Add(new TilePos(x, y));
		return found.OrderBy(c => c.ChebyshevTo(around)).ToList();
	}

	// ---------- IInventory / ICrafter ----------

	public int Count(int itemType) => Items.TryGetValue(itemType, out int n) ? n : 0;
	public bool HasRoomFor(int itemType) => true;

	public CraftResult TryCraft(int itemType)
	{
		// Like the crafting menu: any recipe for the item that is craftable right now.
		IReadOnlyList<RecipeInfo> all = RecipesFor(itemType);
		if (all.Count == 0)
			return CraftResult.NoRecipe;
		bool HasItems(RecipeInfo x) => x.Ingredients.All(i => this.CountAny(i.Accepts) >= i.Stack);
		bool HasStations(RecipeInfo x) => x.Stations.All(s => Stations.TryGetValue(s, out TilePos at) && at.ChebyshevTo(Sim.Node) <= 4);
		RecipeInfo r = all.FirstOrDefault(x => HasItems(x) && HasStations(x));
		if (r == null)
			return all.Any(HasItems) ? CraftResult.NeedsStation : CraftResult.Missing;
		foreach (Ingredient ing in r.Ingredients) {
			int left = ing.Stack;
			foreach (int a in ing.Accepts) {
				int take = Math.Min(left, Count(a));
				Items[a] = Count(a) - take;
				left -= take;
			}
		}
		Items[itemType] = Count(itemType) + r.ResultStack;
		Crafts++;
		return CraftResult.Crafted;
	}

	public string Describe(int itemType) => RecipesFor(itemType).FirstOrDefault() is RecipeInfo r
		? string.Join(", ", r.Ingredients.Select(i => $"{i.Stack} {ItemName(i.Item)}")) + (r.Stations.Count > 0 ? " @ " + string.Join(", ", r.Stations.Select(TileName)) : "")
		: "no recipe";

	// ---------- simulation ----------

	private void OnMined(TilePos cell)
	{
		int tile = TileTypeAt(cell.X, cell.Y);
		TileTypes.Remove(cell);
		int item = tile switch { TileStone => StoneBlock, TileIron => IronOre, _ => DirtBlock };
		Drops.Add(new DropInfo(nextDropId++, item, ItemName(item), 1, cell.X * 16f + 4, cell.Y * 16f + 4, 8, 8));
		if (item == DirtBlock || item == StoneBlock)
			Blocks.BlockCount++; // throwaway blocks the bot may place (picked up right away in practice)
	}

	/// <summary>Applies this tick's controls to the fake world (axe, placing, swings, pickup), then moves the body.</summary>
	public void Step(BotContext ctx)
	{
		MoveInput o = ctx.Output;
		if (o.Tool == ToolRequest.Axe && o.UseItem && o.HasAim) {
			var aim = new TilePos(o.AimX, o.AimY);
			if (Trees.TryGetValue(aim, out int height) && Math.Abs(Sim.Node.X + 1 - aim.X) <= 4) {
				chopTicks[aim] = chopTicks.GetValueOrDefault(aim) + 1;
				if (chopTicks[aim] >= 30) {
					Trees.Remove(aim);
					for (int i = 0; i < height; i++)
						Drops.Add(new DropInfo(nextDropId++, Wood, "Wood", 1, (aim.X + (i % 5) - 2) * 16f + 4, aim.Y * 16f + 4, 8, 8));
				}
			}
		}
		if (o.Tool == ToolRequest.Item && o.UseItem && o.HasAim && Count(o.ToolItem) > 0 && CanPlaceItem(o.ToolItem, o.AimX, o.AimY)) {
			Grid.Set(o.AimX, o.AimY, CellKind.Platform);
			Grid.Set(o.AimX + 1, o.AimY, CellKind.Platform);
			Stations[TilePlacedBy(o.ToolItem)] = new TilePos(o.AimX, o.AimY);
			Items[o.ToolItem]--;
		}
		PhysicsParams p = PhysicsParams.Default;
		float cx = Sim.State.CenterX(p), cy = Sim.State.CenterY(p);
		if (swingTimer == 0 && o.Horizontal != 0)
			facing = o.Horizontal;
		if (o.UseItem && o.Tool == ToolRequest.Weapon) {
			if (swingTimer == 0)
				swingTimer = 20;
			for (int i = 0; i < Slimes.Count; i++) {
				var s = Slimes[i];
				float dx = s.x + 12f - cx, dy = s.y + 12f - cy;
				if (Math.Abs(dx) <= 10f + 30f + 12f && Math.Abs(dy) <= 21f + 12f + 8f && Math.Sign(dx) == facing)
					Slimes[i] = (s.id, s.x, s.y, s.life - 10);
			}
			foreach (var dead in Slimes.Where(s => s.life <= 0).ToList()) {
				Slimes.Remove(dead);
				Drops.Add(new DropInfo(nextDropId++, Gel, "Gel", 2, dead.x + 8, dead.y + 8, 8, 8));
			}
		}
		if (swingTimer > 0)
			swingTimer--;
		Sim.Step(o);
		cx = Sim.State.CenterX(p);
		cy = Sim.State.CenterY(p);
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
		ctx.IsNight = IsNight;
		ctx.PickPower = Count(IronPickaxe) > 0 ? 40 : 35;
		ctx.Combat.ItemAnimation = swingTimer;
		ctx.Combat.Facing = facing;
		if (refreshes++ % 20 == 0)
			seenStations = new Dictionary<int, TilePos>(Stations);
		TilePos n = Sim.Node;
		ctx.Seen = new Surroundings {
			Trees = Trees.Keys.OrderBy(t => Math.Abs(t.X - n.X)).ToList(),
			Stations = seenStations,
		};
		ctx.Drops = Drops.ToList();
		ctx.Hostiles = Slimes.Select(s => new EntityInfo(s.id, 1, "Slime", s.x, s.y, 24, 24, 0, 0, s.life, 40, 7, SlimeAi)).ToList();
	}

	/// <summary>Adds a slime standing on the floor under tile (x, y).</summary>
	public void AddSlime(int id, int x, int feetRow) => Slimes.Add((id, x * 16f, (feetRow + 1) * 16f - 24f, 40));
}

public sealed class TestLog : IBotLog
{
	private readonly ITestOutputHelper o;
	public readonly List<string> Lines = new();
	public TestLog(ITestOutputHelper o) => this.o = o;
	public void Info(string m) { Lines.Add(m); o?.WriteLine("INFO  " + m); }
	public void Warn(string m) { Lines.Add(m); o?.WriteLine("WARN  " + m); }
	public void Error(string m, Exception ex = null) { Lines.Add(m); o?.WriteLine("ERROR " + m); }
	public void Debug(string m) { }
}
