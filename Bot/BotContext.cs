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
				|| !ReferenceEquals(moves.Blocks, PathBlocks) || moves.AllowBreak != AllowBreak || moves.AllowPlace != AllowPlace)
				moves = new MoveContext(Grid, Physics) { Blocks = PathBlocks, AllowBreak = AllowBreak, AllowPlace = AllowPlace, BadEdges = BadEdges };
			return moves;
		}
	}

	public TilePos Node => Core.Pathfinding.Body.NodeOf(Body, Physics);
}
