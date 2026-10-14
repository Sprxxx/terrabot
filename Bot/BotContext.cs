using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot;

/// <summary>Logging sink, so Bot/ and Core/ stay free of tModLoader types. In game it wraps Mod.Logger.</summary>
public interface IBotLog
{
	void Info(string message);
	void Warn(string message);
	void Error(string message, Exception ex = null);
	void Debug(string message);
}

public sealed class NullLog : IBotLog
{
	public static readonly NullLog Instance = new();
	public void Info(string message) { }
	public void Warn(string message) { }
	public void Error(string message, Exception ex = null) { }
	public void Debug(string message) { }
}

/// <summary>
/// Per-tick inputs (perception) and output (controls) shared by the brain and skills. Refreshed by the game
/// adapter right before the brain ticks.
/// </summary>
public sealed class BotContext
{
	private MoveContext moves;

	public ITileGrid Grid { get; set; }
	public PhysicsParams Physics { get; set; } = PhysicsParams.Default;
	public BodyState Body { get; set; }
	/// <summary>Non-null when the bot must not act this tick (dead, mounted, grappled, reversed gravity...).</summary>
	public string BodyIssue { get; set; }
	public long Tick { get; set; }
	/// <summary>Times the player died since the context was made (the bot does not tick while dead).</summary>
	public int Deaths { get; set; }
	/// <summary>
	/// Enemies (NPC whoAmI) given up on, until the tick given: not fought and not counted as danger meanwhile. Kept here,
	/// not in one fight: a new fight forgot, and chased the same slime back into the water again and again.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, long> IgnoredEnemies { get; } = new();

	public bool Ignored(int npcId) => IgnoredEnemies.TryGetValue(npcId, out long until) && Tick < until;

	public void Ignore(int npcId, int ticks) => IgnoredEnemies[npcId] = Tick + ticks;

	/// <summary>
	/// Enemies that swinging at in reach did not hurt (behind a slope or a block the line check missed), until the tick
	/// given: unlike <see cref="IgnoredEnemies"/>, not fought or counted as danger even right next to the bot. Seen in
	/// game: a red slime 3 tiles away was swung at for a whole game day.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, long> UnhittableEnemies { get; } = new();

	public bool IgnoredEvenClose(int npcId) => UnhittableEnemies.TryGetValue(npcId, out long until) && Tick < until;

	/// <summary>The NPC (whoAmI) that last hurt the bot by contact, and when; -1 if none.</summary>
	public int LastAttacker { get; private set; } = -1;
	public long LastAttackedTick { get; private set; } = -1;
	/// <summary>An attack this recent makes its NPC the one to fight.</summary>
	public const int AttackerMemoryTicks = 120;

	/// <summary>
	/// Something hurt the bot: whatever was given up on about it is forgotten (it can reach us, so we can reach it).
	/// Seen in game: a red slime given up on as unhittable kept biting while the bot chased another slime 20 tiles away.
	/// </summary>
	public void NoteAttacker(int npcId)
	{
		LastAttacker = npcId;
		LastAttackedTick = Tick;
		IgnoredEnemies.Remove(npcId);
		UnhittableEnemies.Remove(npcId);
	}

	/// <summary>The NPC that hurt the bot within <see cref="AttackerMemoryTicks"/>, or -1.</summary>
	public int RecentAttacker => LastAttacker >= 0 && Tick - LastAttackedTick <= AttackerMemoryTicks ? LastAttacker : -1;

	public void IgnoreEvenClose(int npcId, int ticks)
	{
		Ignore(npcId, ticks);
		UnhittableEnemies[npcId] = Tick + ticks;
	}

	/// <summary>
	/// Is this enemy one to fight at distance <paramref name="distance"/> px, given what was given up on? Ignored ones
	/// only within <paramref name="ignoredWithin"/> (px): the danger check uses a shorter distance than the fight, or an
	/// ignored slime sitting at the edge started and ended a fight every 2 s (seen in game: mining stalled for an hour).
	/// </summary>
	public bool Fightable(int npcId, float distance, float ignoredWithin = Skills.FightSkill.IgnoredStillFoughtWithin) =>
		!IgnoredEvenClose(npcId) && (!Ignored(npcId) || distance <= ignoredWithin);
	/// <summary>Chests already looted (or given up on), by top-left tile: not visited again.</summary>
	public System.Collections.Generic.HashSet<TilePos> OpenedChests { get; } = new();
	/// <summary>Where exploring legs went (kept across restarts, so a fight or replan does not send it back over old ground).</summary>
	public System.Collections.Generic.List<TilePos> Explored { get; } = new();
	/// <summary>Expert or Master world (Main.expertMode): bosses attack faster.</summary>
	public bool Expert { get; set; }
	public IBotLog Log { get; set; } = NullLog.Instance;
	/// <summary>When true, skills log a line per tick (pos, vel, step, input).</summary>
	public bool Trace { get; set; }

	/// <summary>Controls to press this tick. Reset to None by the brain before each tick.</summary>
	public MoveInput Output;

	/// <summary>Hostile NPCs near the player this tick (nearest first).</summary>
	public System.Collections.Generic.IReadOnlyList<EntityInfo> Hostiles { get; set; } = Array.Empty<EntityInfo>();
	/// <summary>Items lying near the player this tick (nearest first).</summary>
	public System.Collections.Generic.IReadOnlyList<DropInfo> Drops { get; set; } = Array.Empty<DropInfo>();
	public CombatInfo Combat { get; set; } = new();
	public Surroundings Seen { get; set; } = new();
	public IWorldInfo World { get; set; }
	public IInventory Inventory { get; set; }
	public ICrafter Crafter { get; set; }
	public GameIds Ids { get; set; } = GameIds.None;
	/// <summary>Best axe power in the hotbar (0 = none).</summary>
	public int AxePower { get; set; }
	/// <summary>Best pickaxe power in the hotbar (0 = none) and its tileBoost (tool range bonus).</summary>
	public int PickPower { get; set; }
	public int PickRangeBoost { get; set; }
	/// <summary>Recipes, raw-material sources and station items.</summary>
	public IGameKnowledge Knowledge { get; set; }
	/// <summary>Vanilla night (Main.dayTime == false): zombies and demon eyes on the surface.</summary>
	public bool IsNight { get; set; }
	/// <summary>Ticks of night left (Main.nightLength - Main.time at night; 0 by day).</summary>
	public int NightTicksLeft { get; set; }
	/// <summary>
	/// First row of the cavern layer (Main.rockLayer; a player sees the background change). 0 = unknown (no depth limit).
	/// </summary>
	public int CavernY { get; set; }
	/// <summary>Last row of the surface layer (Main.worldSurface); 0 = unknown.</summary>
	public int SurfaceY { get; set; }
	/// <summary>Where the player respawns (feet tile): the bed if set, else the world spawn. Null if unknown.</summary>
	public TilePos? SpawnTile { get; set; }
	/// <summary>Folder for debug dumps (the region of a search that found no path); null = none.</summary>
	public string DumpDir { get; set; }

	/// <summary>
	/// Where the bot was killed by enemies: exploring, ore and chests within <see cref="DangerZoneRadius"/> tiles are left
	/// alone for <see cref="DangerZoneTicks"/>, like a player who stays away from where they keep dying. Seen in game:
	/// antlions in the underground desert killed the bot in nearly every run.
	/// </summary>
	public System.Collections.Generic.List<(TilePos At, long Until)> DangerZones { get; } = new();
	public const int DangerZoneRadius = 30, DangerZoneTicks = 60 * 60 * 30;

	public void NoteDeathByEnemy(TilePos at) => NoteDanger(at, DangerZoneTicks);

	/// <summary>A danger zone at <paramref name="at"/> for <paramref name="ticks"/>.</summary>
	public void NoteDanger(TilePos at, long ticks)
	{
		DangerZones.RemoveAll(z => z.Until <= Tick);
		DangerZones.Add((at, Tick + ticks));
	}

	/// <summary>An evil biome seen by its monsters stays where it is: kept 5 game days (a death spot, 1.25). Run 50 went back after it lapsed.</summary>
	public const long BiomeZoneTicks = 5L * 86400;

	/// <summary>
	/// Somewhere to stay away from for now: where the bot was killed lately, or the underground desert while the gear
	/// is weak. Seen in game (world_eye run 40): antlion chargers, swarmers and rolling cacti killed the bot 6 times in
	/// one run in the underground desert east of the spawn, a place players leave until they have better gear.
	/// </summary>
	public bool OffLimits(TilePos t) => InDangerZone(t) || Combat.Defense < DeepDefense && InUndergroundDesert(t)
		|| Combat.Defense < CavernDefense && InEvilBiome(t);

	/// <summary>
	/// Is the tile where the seen solid tiles around it (48x48) are largely Corruption or Crimson? Seen in game (world_eye
	/// run 47): eaters of souls killed the bot there in copper gear. Players go in once they have armour (Milestone 10).
	/// </summary>
	public bool InEvilBiome(TilePos t) => World != null && Ids.EvilTiles.Length > 0 && Share(t, c => c.Evil) >= DesertShare;

	/// <summary>
	/// Chunk side for <see cref="InUndergroundDesert"/>, and the share of seen solid tiles in the 3x3 chunks around a tile
	/// that makes it desert. One chunk at 40% missed the desert's edges: an antlion swarmer killed the bot where the chunk
	/// itself was mostly stone (world_eye run 41).
	/// </summary>
	public const int DesertChunk = 16;
	public const float DesertShare = 0.25f;
	/// <summary>How long a chunk's counts are kept before it is counted again (more of it may have been seen).</summary>
	public const int DesertRecheckTicks = 60 * 60;
	private readonly System.Collections.Generic.Dictionary<(int, int), (long At, int Solid, int Desert, int Evil)> desertChunks = new();

	/// <summary>
	/// Is the tile below the surface where the seen solid tiles around it (48x48) are largely desert (sand, sandstone...)?
	/// Only seen tiles count, as a player knows the desert by the look of it.
	/// </summary>
	public bool InUndergroundDesert(TilePos t)
	{
		if (World == null || Ids.DesertTiles.Length == 0 || SurfaceY <= 0 || t.Y < SurfaceY)
			return false;
		return Share(t, c => c.Desert) >= DesertShare;
	}

	/// <summary>The share of seen solid tiles in the 3x3 chunks around the tile that <paramref name="count"/> picks (0 with too few seen).</summary>
	private float Share(TilePos t, Func<(int Solid, int Desert, int Evil), int> count)
	{
		int cx = (int)Math.Floor(t.X / (double)DesertChunk), cy = t.Y / DesertChunk;
		int solid = 0, some = 0;
		for (int dx = -1; dx <= 1; dx++)
			for (int dy = -1; dy <= 1; dy++) {
				var c = ChunkCounts(cx + dx, cy + dy);
				solid += c.Solid;
				some += count(c);
			}
		return solid >= 40 ? some / (float)solid : 0f;
	}

	private (int Solid, int Desert, int Evil) ChunkCounts(int cx, int cy)
	{
		if (desertChunks.TryGetValue((cx, cy), out var v) && Tick - v.At < DesertRecheckTicks)
			return (v.Solid, v.Desert, v.Evil);
		int solid = 0, desert = 0, evil = 0;
		for (int x = cx * DesertChunk; x < (cx + 1) * DesertChunk; x++)
			for (int y = cy * DesertChunk; y < (cy + 1) * DesertChunk; y++) {
				if (!World.IsRevealed(x, y))
					continue;
				int type = World.TileTypeAt(x, y);
				if (type < 0)
					continue;
				solid++;
				if (Array.IndexOf(Ids.DesertTiles, type) >= 0)
					desert++;
				else if (Array.IndexOf(Ids.EvilTiles, type) >= 0)
					evil++;
			}
		desertChunks[(cx, cy)] = (Tick, solid, desert, evil);
		return (solid, desert, evil);
	}

	/// <summary>Is the tile inside a zone where the bot was recently killed?</summary>
	public bool InDangerZone(TilePos t)
	{
		foreach (var (at, until) in DangerZones)
			if (until > Tick && at.ChebyshevTo(t) <= DangerZoneRadius)
				return true;
		return false;
	}

	/// <summary>Defense below this keeps exploring and mining above <see cref="CavernY"/> (+ <see cref="ShallowMargin"/>).</summary>
	public const int CavernDefense = 8;
	/// <summary>Defense below this keeps them within <see cref="CavernDepth"/> rows into the caverns.</summary>
	public const int DeepDefense = 16;
	/// <summary>Rows relative to <see cref="CavernY"/>: without armour stay above the caverns (in game, 15 rows into them
	/// was still skeletons and mother slimes); with some, this far in.</summary>
	public const int ShallowMargin = -20, CavernDepth = 150;
	/// <summary>
	/// With an iron-tier sword or better (10+ damage) but no armour: down to 10 rows above the caverns. At 20 rows above,
	/// the ore of that band near the spawn ran out and the bot travelled back and forth for a whole night (run 48).
	/// </summary>
	public const int ArmedMargin = -10, GoodWeaponDamage = 10; // -10: mining at the limit, skeletons from the caverns 10 rows down kept killing it (runs 36-47)

	/// <summary>
	/// The deepest row worth going to for ore and exploring with the armour worn, like a player who stays out of the
	/// caverns in copper gear. Seen in game: exploring for iron in no armour went 200 tiles down, and skeletons, cave
	/// bats and black slimes killed the bot 5 times in an hour. int.MaxValue = no limit.
	/// </summary>
	public int DeepestRow => CavernY <= 0 ? int.MaxValue
		: Combat.Defense < CavernDefense ? CavernY + (Combat.WeaponDamage >= GoodWeaponDamage ? ArmedMargin : ShallowMargin)
		: Combat.Defense < DeepDefense ? CavernY + CavernDepth
		: int.MaxValue;

	/// <summary>Mining/placing capabilities (null = the bot may not change the world).</summary>
	public IBlockActions Blocks { get; set; }
	/// <summary>Player-controlled switches (/bot dig, /bot build).</summary>
	public bool AllowBreak { get; set; } = true;
	public bool AllowPlace { get; set; } = true;
	/// <summary>
	/// Tiles a skill found it cannot get to (behind unbreakable blocks, out of the world...), shared across skill
	/// restarts so a fresh skill does not spend its time on them again. Cleared when auto mode starts.
	/// </summary>
	public System.Collections.Generic.HashSet<TilePos> UnreachableTiles { get; } = new();
	/// <summary>Per ore item: explorations in a row that found none (MineSkill), across restarts of the skill.</summary>
	public System.Collections.Generic.Dictionary<int, int> ExploresWithoutOre { get; } = new();
	/// <summary>Cells path moves must neither mine nor fill (a building in progress); null or empty = none.</summary>
	public System.Collections.Generic.IReadOnlySet<TilePos> Reserved { get; set; }
	private ReservedBlocks reservedBlocks;

	/// <summary>
	/// Cells of what the bot has built (the house, the arena floor), kept off limits to path moves for good: walking,
	/// mining and sheltering never break them or fill them. Set by the planner. Seen in game: a retreat dug its
	/// hole through the house floor, and the house had to be rebuilt.
	/// </summary>
	public System.Collections.Generic.IReadOnlySet<TilePos> Protected { get; set; }
	private ReservedBlocks protectedBlocks;
	private VetoedBlocks vetoedBlocks;
	/// <summary>Cells where placing a block failed twice in the world: path moves do not plan to place there again.</summary>
	public System.Collections.Generic.HashSet<TilePos> NoPlace { get; } = new();
	/// <summary>Path edges that failed <see cref="Skills.WalkToSkill.MaxSameStuck"/> times in a row: the planner skips them.</summary>
	public System.Collections.Generic.HashSet<(TilePos From, TilePos To)> BadEdges { get; } = new();
	/// <summary>Times each step got stuck, over all walks; at <see cref="Skills.WalkToSkill.MaxSameStuck"/> it joins <see cref="BadEdges"/>.</summary>
	public System.Collections.Generic.Dictionary<(TilePos From, TilePos To), int> StepFailures { get; } = new();
	/// <summary>Cells where breaking failed twice in the world: path moves do not plan to break them again.</summary>
	public System.Collections.Generic.HashSet<TilePos> NoBreak { get; } = new();
	/// <summary>Failures so far per (cell, place?) before a veto.</summary>
	public System.Collections.Generic.Dictionary<(TilePos, bool), int> ActionFailures { get; } = new();

	/// <summary>
	/// <see cref="Blocks"/> as path moves see it, with <see cref="Protected"/> and <see cref="Reserved"/> applied
	/// (cached, so the move context stays stable).
	/// </summary>
	public IBlockActions PathBlocks
	{
		get {
			if (Blocks == null)
				return null;
			if (vetoedBlocks == null || !ReferenceEquals(vetoedBlocks.Inner, Blocks))
				vetoedBlocks = new VetoedBlocks(Blocks, NoPlace, NoBreak);
			IBlockActions inner = vetoedBlocks;
			if (Protected != null && Protected.Count > 0) {
				if (protectedBlocks == null || !ReferenceEquals(protectedBlocks.Inner, vetoedBlocks) || !ReferenceEquals(protectedBlocks.Reserved, Protected))
					protectedBlocks = new ReservedBlocks(vetoedBlocks, Protected);
				inner = protectedBlocks;
			}
			if (Reserved == null || Reserved.Count == 0)
				return inner;
			if (reservedBlocks == null || !ReferenceEquals(reservedBlocks.Inner, inner) || !ReferenceEquals(reservedBlocks.Reserved, Reserved))
				reservedBlocks = new ReservedBlocks(inner, Reserved);
			return reservedBlocks;
		}
	}

	/// <summary>Move context for the current grid, physics and settings; rebuilt (with jump templates) on change.</summary>
	public MoveContext Moves
	{
		get {
			if (moves == null || !ReferenceEquals(moves.Grid, Grid) || moves.Physics != Physics
				|| !ReferenceEquals(moves.Blocks, PathBlocks) || moves.AllowBreak != AllowBreak || moves.AllowPlace != AllowPlace
				|| moves.AvoidBelowRow != DeepestRow)
				moves = new MoveContext(Grid, Physics) { Blocks = PathBlocks, AllowBreak = AllowBreak, AllowPlace = AllowPlace, BadEdges = BadEdges, AvoidBelowRow = DeepestRow, Avoid = OffLimits };
			return moves;
		}
	}

	public TilePos Node => Core.Pathfinding.Body.NodeOf(Body, Physics);
}
