using System.Linq;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Brain;

/// <summary>
/// First autonomous goal chain (Altoclef-style "what is the next thing I need?"), re-evaluated whenever the
/// previous skill ends:
/// <list type="number">
/// <item>wood for a work bench (10), unless a bench is already placed nearby or carried;</item>
/// <item>craft the work bench (by hand) and place it;</item>
/// <item>craft a wooden sword at the bench (7 wood) if there is no wooden sword yet;</item>
/// <item>stockpile <see cref="WoodStockpile"/> wood;</item>
/// <item>done.</item>
/// </list>
/// Danger (hostiles within <see cref="DangerRadiusTiles"/>) is handled by <see cref="BotBrain"/>, which
/// pre-empts any of these with a fight.
/// </summary>
public sealed class EarlyGamePlanner
{
	public const int BenchWood = 10;
	public const int SwordWood = 7;
	public const int WoodStockpile = 30;
	public const int DangerRadiusTiles = 12;
	/// <summary>A remembered station is forgotten only if the bot is this close and no longer sees it.</summary>
	public const int StationSightTiles = 20;

	/// <summary>Stations the bot has seen (tile type -> top-left tile), kept while out of sight.</summary>
	public System.Collections.Generic.Dictionary<int, TilePos> KnownStations { get; } = new();

	public string Status { get; private set; } = "";
	public bool Complete { get; private set; }

	private readonly System.Collections.Generic.Dictionary<int, long> notedAt = new();
	/// <summary>A station we placed ourselves is trusted this long even if perception has not caught up yet.</summary>
	public const int PlacedGraceTicks = 120;

	/// <summary>Tell the planner about a finished skill (so a just-placed bench counts before the next scan).</summary>
	public void OnSkillFinished(ISkill skill, SkillStatus status, BotContext ctx)
	{
		if (status == SkillStatus.Succeeded && skill is PlaceItemSkill place && place.PlacedAt is TilePos at
			&& place.ItemType == ctx.Ids.WorkBenchItem) {
			KnownStations[ctx.Ids.WorkBenchTile] = at;
			notedAt[ctx.Ids.WorkBenchTile] = ctx.Tick;
		}
	}

	public void Reset()
	{
		KnownStations.Clear();
		notedAt.Clear();
		Status = "";
		Complete = false;
	}

	/// <summary>The next skill to run, or null when the chain is complete (or stuck on something it cannot do).</summary>
	public ISkill Next(BotContext ctx)
	{
		GameIds ids = ctx.Ids;
		IInventory inv = ctx.Inventory;
		RememberStations(ctx);
		int wood = inv.Count(ids.Wood);
		bool benchPlaced = KnownStations.ContainsKey(ids.WorkBenchTile);
		bool benchCarried = inv.Count(ids.WorkBenchItem) > 0;
		bool haveSword = inv.Count(ids.WoodenSword) > 0;

		if (!benchPlaced && !benchCarried) {
			if (wood < BenchWood)
				return Do($"need {BenchWood} wood for a work bench (have {wood})", new GatherWoodSkill(ids.Wood, BenchWood));
			return Do("crafting a work bench", new CraftSkill(ids.WorkBenchItem));
		}
		if (!benchPlaced)
			return Do("placing the work bench", new PlaceItemSkill(ids.WorkBenchItem));
		if (!haveSword) {
			if (wood < SwordWood)
				return Do($"need {SwordWood} wood for a wooden sword (have {wood})", new GatherWoodSkill(ids.Wood, SwordWood));
			if (!NearStation(ctx, ids.WorkBenchTile))
				return Do("walking back to the work bench", new WalkToSkill(KnownStations[ids.WorkBenchTile]) { Tolerance = 2 });
			return Do("crafting a wooden sword", new CraftSkill(ids.WoodenSword));
		}
		if (wood < WoodStockpile)
			return Do($"stockpiling wood ({wood}/{WoodStockpile})", new GatherWoodSkill(ids.Wood, WoodStockpile));

		Status = "early-game chain complete: work bench placed, wooden sword, wood stockpiled";
		Complete = true;
		return null;
	}

	private ISkill Do(string why, ISkill skill)
	{
		Status = why;
		Complete = false;
		return skill;
	}

	private void RememberStations(BotContext ctx)
	{
		foreach (var kv in ctx.Seen.Stations)
			KnownStations[kv.Key] = kv.Value;
		TilePos node = ctx.Node;
		foreach (int type in KnownStations.Keys.ToList())
			if (KnownStations[type].ChebyshevTo(node) <= StationSightTiles && !ctx.Seen.Stations.ContainsKey(type)
				&& !(notedAt.TryGetValue(type, out long t) && ctx.Tick - t < PlacedGraceTicks))
				KnownStations.Remove(type); // in sight but gone (broken, or never really there)
	}

	/// <summary>Close enough for the station to count for crafting (vanilla adjTile range is about 4-5 tiles).</summary>
	private bool NearStation(BotContext ctx, int tileType) =>
		KnownStations.TryGetValue(tileType, out TilePos at) && at.ChebyshevTo(ctx.Node) <= 3;

	/// <summary>Hostile close enough that the brain should drop everything and fight.</summary>
	public static bool InDanger(BotContext ctx)
	{
		Core.Physics.PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		return ctx.Combat.HasWeapon && ctx.Hostiles.Any(h => h.DistanceTo(cx, cy) <= DangerRadiusTiles * 16f);
	}
}
