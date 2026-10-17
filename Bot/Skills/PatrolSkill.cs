using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// One pass along a stretch of surface the bot knows, to its far end, looking as it goes, for what grows back there (vile
/// mushrooms on corrupt grass), as a player walks the corruption to and fro instead of off into new ground; the plants
/// it passes are cut (<see cref="CutTile"/>) so new ones, some of them mushrooms, can grow in their place. Succeeds at
/// the end of the pass or as soon as <see cref="Found"/> holds (checked every <see cref="ExploreSkill.FoundCheckEvery"/>
/// ticks); fails if the end cannot be walked to. MineSkill counts the passes (BotContext.PatrolPasses). Only with no plant
/// to cut on the map: those are gone to where they are first (<see cref="CutPlantsSkill"/>), the grass tops kept free being
/// what grows mushrooms, not the walking (measured runs M10d-M10f: 0-12 plants cut a pass, 15 of 28 passes none).
/// <para>
/// Vanilla regrowth (decompiled tML 2026.08, a 4200x1200 world as M10's): WorldGen.UpdateWorld_Inner makes
/// maxTilesX * maxTilesY * 3E-05 * the tile update rate (1) random updates a tick above the surface layer (WorldGen.cs:66300,
/// 66325), on x in [10, maxTilesX - 10) and y in [10, worldSurface - 1) (WorldGen.cs:66334-66335): 151 a tick over
/// 4180 x 326 cells, 0.40 hits a cell each game hour. A hit on corrupt grass with an empty, dry cell above places a corrupt
/// plant there 1 in 10 (WorldGen.cs:68894), which is corrupt thorns 1 in 13 (WorldGen.cs:54339) and else a vile mushroom
/// at 1/50 or 1/40 (WorldGen.cs:54373, 0.0445): 0.041 of the plants that grow are mushrooms. A free cell fills in about
/// 25 game hours (0.040 an hour), and grows a mushroom at 0.0016 an hour; an 80-tile stretch of free cells about 0.13 an
/// hour, one in 7.6 game hours. But world generation put a plant on every corrupt grass top (WorldGen.cs:15544-15548), and
/// a cell with a plant in it grows nothing: only what is cut grows back. Seen in game (measured run M10b, world_eow): with
/// the plants cut only by the fights there, 2 of the 6 mushrooms at day 2 +20h, 3 at day 3 +12h, 4 at day 5 +4h, while the
/// "Sideways" explorations for more went ever farther from the corruption (x 1765-1960), out to (1625,268).
/// </para>
/// </summary>
public sealed class PatrolSkill : ISkill
{
	/// <summary>The pass is over within this many tiles of its end (a walk's tolerance, as in the hunt's wanders).</summary>
	public const int ArriveTiles = 4;
	/// <summary>
	/// Known surface narrower than this (tiles) is too little to wait on: two chunks (BotContext.DesertChunk), 32 free cells
	/// grow a mushroom about every 19 game hours (0.0016 a cell an hour, see the class remarks); the job explores for more.
	/// </summary>
	public const int MinPatrolTiles = 2 * BotContext.DesertChunk;
	/// <summary>
	/// Chunks of other ground the stretch reaches across (a chasm's lip, a patch of plain dirt between corrupt grass): one.
	/// </summary>
	public const int GapChunks = 1;

	private readonly Func<BotContext, bool> found;
	private WalkToSkill walk;
	private int ticks;
	private TilePos? lastCut;

	/// <summary>Where the pass goes: the stretch's far end, on its surface.</summary>
	public TilePos End { get; }
	/// <summary>Plants of this tile type in reach on the way are cut (-1: none): corrupt plants, one hit of the pickaxe each.</summary>
	public int CutTile { get; init; } = -1;
	/// <summary>Plants of this frame column are left for the job itself (the vile mushroom's, MineSkill.FrameX).</summary>
	public int KeepFrameX { get; init; } = -1;
	/// <summary>Plants cut on this pass.</summary>
	public int Cut { get; private set; }
	public string What { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Patrol";

	public PatrolSkill(string what, TilePos end, Func<BotContext, bool> found)
	{
		What = what;
		End = end;
		this.found = found;
	}

	public bool Found(BotContext ctx) => found(ctx);

	public string Describe() => $"Patrol for {What} to {End}, cut {Cut} [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		walk = new WalkToSkill(new GoalNearTile(End, ArriveTiles, ctx.Physics)) { Quiet = true };
		walk.Start(ctx);
		State = "walking";
	}

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
			ctx.Log.Info($"Patrol: {What} in sight at {ctx.Node}, cut {Cut} plant(s) on the way");
			State = "found";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (walk == null)
			Start(ctx);
		SkillStatus s = walk.Tick(ctx);
		if (s == SkillStatus.Succeeded) {
			ctx.Log.Info($"Patrol: at the end {End} ({ctx.Node}), no {What} on the way; cut {Cut} plant(s)");
			State = "at the end";
			Stop(ctx);
			return SkillStatus.Succeeded;
		}
		if (s == SkillStatus.Failed) {
			FailureReason = $"no way to {End}: {walk.FailureReason}";
			State = "failed: " + FailureReason;
			Stop(ctx);
			return SkillStatus.Failed;
		}
		CutOnTheWay(ctx);
		return SkillStatus.Running;
	}

	/// <summary>
	/// A swing of the pickaxe at the nearest plant in reach ahead (or straight above or below), while the walk itself is not
	/// using an item: as a player cuts the grass going by. Corrupt plants are tileNoFail (Main.cs:8037), so any pickaxe takes
	/// one in one hit (Player.cs:45896, ItemCheck_UseMiningTools_ActuallyUseMiningTool), as it takes the mushroom frame of
	/// the same tile. Not with digging switched off (BotContext.AllowBreak).
	/// </summary>
	private void CutOnTheWay(BotContext ctx)
	{
		if (CutTile < 0 || !ctx.AllowBreak || ctx.PickPower <= 0 || ctx.Output.UseItem || ctx.Output.Tool != ToolRequest.None)
			return;
		TilePos node = ctx.Node;
		int heading = Math.Sign(End.X - node.X);
		TilePos? best = null;
		// The thorns on the grass too (CutPlantsSkill.Cuttable): a grass top under them grows nothing.
		foreach (TilePos c in CutPlantsSkill.Candidates(ctx, node, ReachTiles + ctx.PickRangeBoost, CutTile, KeepFrameX)) {
			if ((c.X - node.X) * heading < 0)
				continue;
			if (!new GoalReachTile(c, ctx.Physics, ctx.PickRangeBoost).InRange(node))
				continue;
			best = c; // nearest first
			break;
		}
		if (best is not TilePos t)
			return;
		if (lastCut != t) {
			Cut++;
			lastCut = t;
		}
		ctx.Output.Tool = ToolRequest.Pickaxe;
		ctx.Output.UseItem = true;
		ctx.Output.Aim(t.X, t.Y);
	}

	/// <summary>Vanilla tool reach from a node at most (GoalReachTile, before its slack): 5 tiles ahead and 5 up.</summary>
	private const int ReachTiles = 5;

	/// <summary>
	/// The stretch of evil-biome surface the map shows round <paramref name="from"/>: the run of chunk columns of
	/// BotContext.KnownEvil (in the surface layer, BotContext.SurfaceY) holding the bot's column or the nearest to it,
	/// across gaps of <see cref="GapChunks"/>, within MineSkill.MapSearchRadius (the map lookup's reach) either side. The
	/// chunks along there are counted first, so the memory holds what the map shows (a look counts the 5x5 chunks round a
	/// tile, BotContext.BiomeChunkRadius). Null when it is narrower than <see cref="MinPatrolTiles"/>, or none is known.
	/// </summary>
	public static (int X0, int X1)? EvilSurfaceSpan(BotContext ctx, TilePos from)
	{
		if (ctx.World == null || ctx.Ids.EvilTiles.Length == 0)
			return null;
		int reach = MineSkill.MapSearchRadius;
		int lookStep = (2 * BotContext.BiomeChunkRadius + 1) * BotContext.DesertChunk;
		for (int x = from.X - reach; x <= from.X + reach + lookStep - 1; x += lookStep)
			ctx.InEvilBiome(new TilePos(x, from.Y));
		var cols = new HashSet<int>();
		foreach (TilePos e in ctx.KnownEvil.All)
			if (Math.Abs(e.X - from.X) <= reach && (ctx.SurfaceY <= 0 || e.Y <= ctx.SurfaceY))
				cols.Add(ChunkOf(e.X));
		if (cols.Count == 0)
			return null;
		int fc = ChunkOf(from.X);
		int start = cols.OrderBy(c => Math.Abs(c - fc)).First();
		if (Math.Abs(start - fc) > GapChunks + 1)
			return null; // the evil ground the map shows is elsewhere, not where the bot is
		int lo = start, hi = start;
		while (Enumerable.Range(1, GapChunks + 1).FirstOrDefault(d => cols.Contains(lo - d)) is int dl and > 0)
			lo -= dl;
		while (Enumerable.Range(1, GapChunks + 1).FirstOrDefault(d => cols.Contains(hi + d)) is int dh and > 0)
			hi += dh;
		int x0 = Math.Max(lo * BotContext.DesertChunk, from.X - reach);
		int x1 = Math.Min((hi + 1) * BotContext.DesertChunk - 1, from.X + reach);
		return x1 - x0 + 1 < MinPatrolTiles ? null : (x0, x1);
	}

	/// <summary>
	/// Where a pass to the stretch's end at column <paramref name="x"/> goes: the surface there (the first standing spot from
	/// the top, ProgressionPlanner.SurfaceAt), or, over a chasm or a hole below the surface layer, the next column inwards
	/// that has one in it, within a chunk. Null if none.
	/// </summary>
	public static TilePos? EndSpot(BotContext ctx, int x, int inward)
	{
		for (int k = 0; k < BotContext.DesertChunk; k++)
			if (Brain.ProgressionPlanner.SurfaceAt(ctx.Grid, x + inward * k) is TilePos s && (ctx.SurfaceY <= 0 || s.Y <= ctx.SurfaceY))
				return s;
		return null;
	}

	private static int ChunkOf(int x) => (int)Math.Floor(x / (double)BotContext.DesertChunk);
}
