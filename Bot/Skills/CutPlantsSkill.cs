using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// A round of cutting what grows on the corrupt grass the map shows: each corrupt plant that is not a vile mushroom, and
/// corrupt thorns rooted on the grass, is walked to (nearest first) and cut with one swing of the pickaxe, so its grass top
/// is free and grows again, some of it mushrooms; as a player keeps a patch of grass cut. Succeeds when none is left to cut
/// (or as soon as <see cref="Found"/> holds: a mushroom on the map); fails after <see cref="MineSkill.MaxUnreachableInARow"/>
/// plants in a row could not be got to. In a Crimson world the same on crimson grass (<see cref="GameIds.EvilGrass"/>): crimson
/// plants that are not vicious mushrooms, and crimson thorns (<see cref="GameIds.EvilThorns"/>), as the same plant growth puts them
/// there (WorldGen.cs:54335-54380: the plant of the grass under it, thorns 1 in 13, a mushroom 1 in 50 or 1 in 40 for both evils).
/// <para>
/// Why the plants, not the stretch: vanilla grows a plant only on a free grass top, and a grass top with a plant or thorns
/// on it grows nothing until they are cut (WorldGen.UpdateWorld_GrassGrowth, WorldGen.cs:68890: "!Main.tile[i, minJ].active()").
/// So the mushrooms a game hour are <see cref="MushroomsPerCellHour"/> times the grass tops kept free, and nothing else the
/// bot does (looking, walking the stretch) adds to them: a mushroom grown anywhere the map shows is seen there (MineSkill's
/// map lookup). Seen in game (measured runs M10d, M10e, M10f, world_eow): the passes along the evil surface (PatrolSkill)
/// walked x 1472-1967 end to end while the corrupt grass tops that grow plants lie in x 1769-1941 (63) and x 1269-1387
/// (38): 101 in all at the run's start, 91 of them under a plant, 5 vile mushrooms, 2 thorns, 3 free (read from the world
/// file; x 1388-1768 is ebonstone and ebonsand, and 115 more corrupt grass tops grow nothing: 82 sloped or half blocks, 33
/// under trees and rubble). The passes cut 0-12 plants each, 67 in 28 passes, 15 of them none: M10d had its 6 mushrooms at
/// d5 +19h, M10e at d3 +11h, M10f stood at 3 of 6 for 12 game hours. All 101 kept cut grow 4.0 mushrooms a game day.
/// </para>
/// <para>
/// Not underground corrupt grass: the world_eow world has none below the surface layer (row 337) at the run's start, and a
/// cell there is no better: UpdateWorld_UndergroundTile runs 1.5E-05 (not 2.5E-05, that is the remix world's) of the cells
/// a tick (WorldGen.cs:66301, 66356), 0.077 hits a cell an hour over rows 336-1180, and grows a plant 1 in 2
/// (WorldGen.cs:68645): 0.039 plants an hour, the surface's 0.40 x 1/10 = 0.040; and a plant grows only where the wall of
/// the cell and the one below allow it (WallID.Sets.AllowsPlantsToGrow, WorldGen.cs:54371: no wall, grass walls), not the
/// dirt and ebonstone walls (2, 3) of the caves and chasms.
/// </para>
/// </summary>
public sealed class CutPlantsSkill : ISkill
{
	/// <summary>
	/// Random tile updates a surface cell gets each game hour: maxTilesX * maxTilesY * 3E-05 a tick (WorldGen.cs:66300, 66325)
	/// over x in [10, maxTilesX - 10) and y in [10, worldSurface - 1) (WorldGen.cs:66334-66335): 151 a tick over 4180 x 326
	/// cells in the 4200 x 1200 world_eow world, 3600 ticks an hour.
	/// </summary>
	public const double SurfaceUpdatesPerCellHour = 4200 * 1200 * 3E-05 / (4180.0 * 326) * 3600;
	/// <summary>
	/// A free corrupt grass top grows a corrupt plant on 1 in 10 of its updates (WorldGen.cs:68894), corrupt thorns in its
	/// place 1 in 13 (WorldGen.cs:54339), else a vile mushroom 1 in 50, or 1 in 40 of the rest (WorldGen.cs:54373): 0.0016 a
	/// grass top each game hour, one in 25 game hours for 25 grass tops kept free.
	/// </summary>
	public const double MushroomsPerCellHour = SurfaceUpdatesPerCellHour / 10 * (12.0 / 13) * (1 / 50.0 + 49 / 50.0 / 40);
	/// <summary>A free grass top grows something (a plant or thorns) at this rate each game hour: once in 25 game hours.</summary>
	public const double GrowthsPerCellHour = SurfaceUpdatesPerCellHour / 10;

	/// <summary>How far round the bot plants are looked for on the map: the map lookup's reach (MineSkill.MapSearchRadius).</summary>
	public const int Radius = MineSkill.MapSearchRadius;

	private readonly Func<BotContext, bool> found;
	private readonly HashSet<TilePos> skipped = new();
	private WalkToSkill walk;
	private TilePos? target;
	private int ticks, swingTicks, unreachableInARow, arrivedOutOfReach;
	private bool swung;
	/// <summary>The plant swung at on the way (<see cref="CutOnTheWay"/>): counted cut once it is gone.</summary>
	private TilePos? onTheWay;

	public string What { get; }
	/// <summary>The plant tile (corrupt plants) cut, but not the frame column <see cref="KeepFrameX"/> (the vile mushroom's).</summary>
	public int PlantTile { get; init; } = -1;
	public int KeepFrameX { get; init; } = -1;
	/// <summary>Plants and thorns cut on this round.</summary>
	public int Cut { get; private set; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "CutPlants";

	public CutPlantsSkill(string what, Func<BotContext, bool> found)
	{
		What = what;
		this.found = found;
	}

	public bool Found(BotContext ctx) => found(ctx);

	public string Describe() => $"Cut plants for {What}, cut {Cut} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx) => State = "started";

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public void Pause(BotContext ctx) => walk?.Pause(ctx);

	public void Resume(BotContext ctx) => walk?.Resume(ctx);

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ticks++ % ExploreSkill.FoundCheckEvery == 0 && found(ctx)) {
			ctx.Log.Info($"Cut: {What} in sight at {ctx.Node}, cut {Cut} plant(s) on the round");
			State = "found";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (onTheWay is TilePos passed && !Cuttable(ctx, passed, PlantTile, KeepFrameX)) {
			Cut++;
			onTheWay = null;
		}
		if (target is TilePos was && !Cuttable(ctx, was, PlantTile, KeepFrameX)) {
			if (swung) {
				Cut++;
				unreachableInARow = 0;
			}
			target = null;
		}
		if (target == null && !NextTarget(ctx)) {
			ctx.Log.Info($"Cut: none left to cut within {Radius} tiles of {ctx.Node}; cut {Cut} plant(s) on the round");
			State = "done";
			return SkillStatus.Succeeded;
		}
		TilePos t = target.Value;
		var reach = new GoalReachTile(t, ctx.Physics, ctx.PickRangeBoost);
		TilePos node = ctx.Node;
		if ((ctx.Body.Grounded || Math.Abs(ctx.Body.VY) < MineSkill.HeldStillVY) && reach.IsGoal(node)) {
			walk?.Stop(ctx);
			walk = null;
			arrivedOutOfReach = 0;
			if (++swingTicks > MineSkill.MaxMineTicksPerTile)
				return GiveUp(ctx, t, $"still there after {swingTicks} ticks");
			swung = true;
			ctx.Output.Tool = ToolRequest.Pickaxe;
			ctx.Output.UseItem = true;
			ctx.Output.Aim(t.X, t.Y);
			State = $"cutting {t}";
			return SkillStatus.Running;
		}
		if (walk == null) {
			walk = new WalkToSkill(reach) { Quiet = true, MaxNoPathAttempts = 1, Limits = MineSkill.ReachLimits };
			walk.Start(ctx);
		}
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Failed) {
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			ctx.UnreachableTiles.Add(t);
			GiveUp(ctx, t, "unreachable: " + why);
			if (++unreachableInARow >= MineSkill.MaxUnreachableInARow)
				return Fail(ctx, $"{unreachableInARow} plants in a row unreachable; last: {why}");
			return SkillStatus.Running;
		}
		if (s == SkillStatus.Succeeded) {
			walk.Stop(ctx);
			walk = null;
			// Arrived but still out of reach (MineSkill's MaxArrivedOutOfReach): not round and round at it.
			if (++arrivedOutOfReach >= MineSkill.MaxArrivedOutOfReach)
				GiveUp(ctx, t, $"walked there {MineSkill.MaxArrivedOutOfReach} times and still out of reach from {ctx.Node}");
			return SkillStatus.Running;
		}
		State = $"walking to {t}";
		CutOnTheWay(ctx, t);
		return SkillStatus.Running;
	}

	private bool NextTarget(BotContext ctx)
	{
		target = Next(ctx, ctx.Node, PlantTile, KeepFrameX, skipped);
		swingTicks = 0;
		arrivedOutOfReach = 0;
		swung = false;
		walk?.Stop(ctx);
		walk = null;
		return target != null;
	}

	private SkillStatus GiveUp(BotContext ctx, TilePos t, string why)
	{
		skipped.Add(t);
		int n = ctx.NoteMineFailure(t);
		ctx.Log.Info($"Cut: skipping {t}: {why}" + (n > 1 ? $" ({n} times lately)" : ""));
		NextTarget(ctx);
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		Stop(ctx);
		return SkillStatus.Failed;
	}

	/// <summary>
	/// A swing at another plant in reach while walking to the target (as a player cuts the grass going by), while the walk
	/// itself is not using an item. Corrupt plants and thorns are tileNoFail (Main.cs:8037, 8066): one hit of any pickaxe
	/// (Player.cs:45913, ItemCheck_UseMiningTools_ActuallyUseMiningTool).
	/// </summary>
	private void CutOnTheWay(BotContext ctx, TilePos t)
	{
		if (!ctx.AllowBreak || ctx.PickPower <= 0 || ctx.Output.UseItem || ctx.Output.Tool != ToolRequest.None)
			return;
		TilePos node = ctx.Node;
		foreach (TilePos c in Candidates(ctx, node, MaxReachTiles + ctx.PickRangeBoost, PlantTile, KeepFrameX)) {
			if (c == t || !new GoalReachTile(c, ctx.Physics, ctx.PickRangeBoost).InRange(node))
				continue;
			onTheWay = c;
			ctx.Output.Tool = ToolRequest.Pickaxe;
			ctx.Output.UseItem = true;
			ctx.Output.Aim(c.X, c.Y);
			return;
		}
	}

	/// <summary>Vanilla tool reach from a node at most (GoalReachTile, before its slack): 5 tiles ahead and 5 up.</summary>
	private const int MaxReachTiles = 5;

	/// <summary>
	/// Is the cell something this round cuts: a corrupt plant not of <paramref name="keepFrameX"/>, or corrupt thorns on top
	/// of corrupt grass (the ones that take a grass top; thorns grown from thorns are cut with them in the next swings)?
	/// </summary>
	public static bool Cuttable(BotContext ctx, TilePos c, int plantTile, int keepFrameX)
	{
		int type = ctx.World.TileTypeAt(c.X, c.Y);
		if (type == plantTile && plantTile >= 0)
			return keepFrameX < 0 || ctx.World.TileFrameX(c.X, c.Y) != keepFrameX;
		return type == ctx.Ids.EvilThorns && ctx.Ids.EvilThorns > 0 && ctx.World.TileTypeAt(c.X, c.Y + 1) == ctx.Ids.EvilGrass;
	}

	/// <summary>The cells to cut the map shows within <paramref name="radius"/> of <paramref name="around"/>, nearest first.</summary>
	public static IEnumerable<TilePos> Candidates(BotContext ctx, TilePos around, int radius, int plantTile, int keepFrameX)
	{
		var all = new List<TilePos>();
		if (plantTile >= 0)
			all.AddRange(ctx.World.FindTiles(plantTile, around, radius));
		if (ctx.Ids.EvilThorns > 0)
			all.AddRange(ctx.World.FindTiles(ctx.Ids.EvilThorns, around, radius));
		all.Sort((a, b) => a.ChebyshevTo(around).CompareTo(b.ChebyshevTo(around)));
		foreach (TilePos c in all)
			if (Cuttable(ctx, c, plantTile, keepFrameX))
				yield return c;
	}

	/// <summary>
	/// The plant to cut next: the one the map shows within <see cref="Radius"/> that costs least to get to (sideways tiles,
	/// rows up or down 1.5 each, as MineSkill weighs ore), its give-ups lately counted as distance (BotContext.MineFailures),
	/// not one of <paramref name="skip"/>, not one found unreachable lately, not beside lava, not off limits. Null if none.
	/// </summary>
	public static TilePos? Next(BotContext ctx, TilePos from, int plantTile, int keepFrameX, ICollection<TilePos> skip = null)
	{
		TilePos? best = null;
		float bestScore = float.MaxValue;
		foreach (TilePos c in Candidates(ctx, from, Radius, plantTile, keepFrameX)) {
			if (c.ChebyshevTo(from) >= bestScore)
				break; // nearest first: none further on can beat it
			if (skip != null && skip.Contains(c) || ctx.UnreachableTiles.Contains(c) || !ctx.InMiningReach(c, from, Radius))
				continue;
			if (BlockRules.NextToLava(ctx.Grid, c) || ctx.OffLimitsOutright(c))
				continue;
			float score = Math.Abs(c.X - from.X) + 1.5f * Math.Abs(c.Y - from.Y) + ctx.MineFailureCost(c, Radius);
			if (score < bestScore) {
				bestScore = score;
				best = c;
			}
		}
		return best;
	}

	/// <summary>
	/// What the corrupt grass the map shows round <paramref name="around"/> holds: plants and thorns to cut, vile mushrooms,
	/// and free grass tops (some of these grow nothing: a sloped or half block, or a dirt wall behind, WorldGen.cs:52850 and
	/// 54371, which the map does not tell apart).
	/// </summary>
	public static (int ToCut, int Mushrooms, int Free) Survey(BotContext ctx, TilePos around, int plantTile, int keepFrameX)
	{
		int toCut = 0, mushrooms = 0, free = 0;
		if (ctx.Ids.EvilGrass <= 0)
			return (0, 0, 0);
		foreach (TilePos g in ctx.World.FindTiles(ctx.Ids.EvilGrass, around, Radius)) {
			var top = new TilePos(g.X, g.Y - 1);
			int type = ctx.World.TileTypeAt(top.X, top.Y);
			if (type == -1) {
				if (ctx.Grid.Get(top.X, top.Y) == CellKind.Empty)
					free++;
			}
			else if (Cuttable(ctx, top, plantTile, keepFrameX))
				toCut++;
			else if (type == plantTile)
				mushrooms++;
		}
		return (toCut, mushrooms, free);
	}
}
