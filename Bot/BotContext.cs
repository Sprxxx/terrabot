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
	/// Fliers waited for in the open that did not come (FightSkill.MaxFarWaitTicks), until the tick given: not waited for
	/// again while out of sight beyond the distance a fight starts at (FightSkill.ThreatTiles); shot when in sight, and
	/// fought when they come that close, as ever.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, long> WaitedOutEnemies { get; } = new();

	public bool WaitedOut(int npcId) => WaitedOutEnemies.TryGetValue(npcId, out long until) && Tick < until;

	public void WaitOut(int npcId, int ticks) => WaitedOutEnemies[npcId] = Tick + ticks;

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

	/// <summary>The biggest hit taken (damage, as the game shows it) within <see cref="HitMemoryTicks"/>; 0 if none.</summary>
	public int BiggestRecentHit => Tick - biggestHitTick <= HitMemoryTicks ? biggestHit : 0;
	public const int HitMemoryTicks = 600;
	private int biggestHit;
	private long biggestHitTick = long.MinValue / 2;

	/// <summary>
	/// Ticks a hit leaves the player immune to the next (vanilla Player.Hurt: immuneTime 40, 80 with the Cross Necklace's
	/// longInvince): an enemy still on the bot lands its next hit within this long once the last one has worn off.
	/// </summary>
	public const int HurtImmunityTicks = 40;

	/// <summary>When an enemy (an NPC's touch or a projectile) last hurt the bot; not falls or tiles.</summary>
	public long LastEnemyHitTick { get; private set; } = long.MinValue / 2;

	/// <summary>The bot took a hit of <paramref name="damage"/>; <paramref name="byEnemy"/>: from an NPC or a projectile.</summary>
	public void NoteHit(int damage, bool byEnemy = true)
	{
		if (byEnemy)
			LastEnemyHitTick = Tick;
		if (damage >= BiggestRecentHit) {
			biggestHit = damage;
			biggestHitTick = Tick;
		}
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
	public PlaceMemory Explored { get; } = new();
	/// <summary>Set by the planner while the night's walk down for mining is the job (the house's spot): see OffLimitsForPaths.</summary>
	public TilePos? NightDescentFrom { get; set; }
	/// <summary>Columns either side of the house the way down may start in at no extra cost (the house is 10 wide).</summary>
	public const int NightDescentShaftTiles = 6;
	/// <summary>Where explore legs ended: what is still unseen near these was in sight and is not to be seen by going there.</summary>
	public PlaceMemory LookedFrom { get; } = new();
	/// <summary>Expert or Master world (Main.expertMode): bosses attack faster.</summary>
	public bool Expert { get; set; }
	/// <summary>Journey: trash researched items nothing needs (the game adapter's TerraBot setting; ResearchHabit.TrashResearched).</summary>
	public bool TrashResearched { get; set; }
	public IBotLog Log { get; set; } = NullLog.Instance;
	/// <summary>When true, skills log a line per tick (pos, vel, step, input).</summary>
	public bool Trace { get; set; }

	/// <summary>Controls to press this tick. Reset to None by the brain before each tick.</summary>
	public MoveInput Output;

	/// <summary>
	/// The block a walk's ArrestedDrop step lands on, while it can still go in (PathExecutor.CatchCell): set by the walk each
	/// tick it drives, cleared by the brain before the skills tick. The fall reflex (FallArrest) leaves such a fall to the step.
	/// </summary>
	public TilePos? PlannedCatch { get; set; }

	/// <summary>
	/// Where the walk's step in progress lands (a jump's, a drop's, an ArrestedDrop's node: PathExecutor.PlannedLanding), set and
	/// cleared as <see cref="PlannedCatch"/>: a fall the step steers onto it within a safe fall is left to the step.
	/// </summary>
	public TilePos? PlannedLanding { get; set; }

	/// <summary>Hostile NPCs near the player this tick (nearest first).</summary>
	public System.Collections.Generic.IReadOnlyList<EntityInfo> Hostiles { get; set; } = Array.Empty<EntityInfo>();
	/// <summary>
	/// Town NPCs in sight this tick (nearest first): friendly, so not among <see cref="Hostiles"/>. A player sees them drawn
	/// with their names; the Old Man at the Dungeon's entrance is how the Dungeon is found (docs M11_SPEC.md section 1c).
	/// </summary>
	public System.Collections.Generic.IReadOnlyList<EntityInfo> TownNpcs { get; set; } = Array.Empty<EntityInfo>();
	/// <summary>Hostile projectiles in sight this tick (nearest first): the Wall of Flesh's eye lasers, for the dodge.</summary>
	public System.Collections.Generic.IReadOnlyList<ShotInfo> Shots { get; set; } = Array.Empty<ShotInfo>();
	/// <summary>
	/// NPC types not to be fought nor shot, nor counted as danger, while the plan says so: the Voodoo Demon while the Guide lives
	/// and the Wall of Flesh is not to be summoned yet (one killed over lava drops the Guide Voodoo Doll into it, and the Wall
	/// spawns at once, Item.cs:50396-50425; docs M12_SPEC.md section 2d, G7). Set by the planner each pass.
	/// </summary>
	public System.Collections.Generic.HashSet<int> SparedTypes { get; } = new();

	/// <summary>
	/// Is this NPC spared now: of a spared type (<see cref="SparedTypes"/>) and over lava, where what it drops would burn? Over
	/// ground (the hellbridge's deck, an ash knoll) it is fought as any other: the doll drops where it dies and falls straight down
	/// (NPC loot, Item.NewItem at the NPC; items rest on blocks, Item.cs:50828), onto the ground to be picked up. The task's rule:
	/// no Voodoo Demon killed over lava except as the summon. Seen in game (voodoo_rule, 2026-10-17): spared everywhere, one hit
	/// the bot off the bridge's end into the lava (knockback 4.5) while it built on.
	/// </summary>
	public bool Spared(in EntityInfo e) => SparedTypes.Count > 0 && SparedTypes.Contains(e.Type) && OverLava(e);

	/// <summary>
	/// Columns either side of the NPC's centre its drop may land in: loot spawns at up to 3 px/tick sideways (Item.cs:51718), slowed
	/// 5 % a tick in the air, 60 px at most.
	/// </summary>
	public const int SpareDropSlackTiles = 4;
	/// <summary>
	/// Ticks an arrow takes to reach a flier the fight shoots (the danger distance, 12 tiles, at 6.6-10 px/tick: 20-30): the NPC
	/// moves on meanwhile at its speed, and dies there. Seen in game (voodoo_rule, m12_synthetic_3): a Voodoo Demon judged over the
	/// bridge's end by one column either side was killed over the lava past it, and the Wall came.
	/// </summary>
	public const int SpareArrowTicks = 30;
	/// <summary>Rows under the NPC looked down for what its drop would land on: the band's lava is at most ~40 rows under a flier.</summary>
	public const int SpareDropRows = 80;

	/// <summary>
	/// Would a drop from the NPC meet lava before ground, in any of the columns round its centre (<see cref="SpareDropSlackTiles"/>)?
	/// Nothing found within <see cref="SpareDropRows"/> counts as lava (the void over the lava sea).
	/// </summary>
	public bool OverLava(in EntityInfo e)
	{
		if (Grid == null)
			return true;
		int cx = (int)MathF.Floor(e.CenterX / 16f), top = (int)MathF.Floor(e.Bottom / 16f);
		int slack = SpareDropSlackTiles + (int)MathF.Ceiling(MathF.Max(MathF.Abs(e.VX), MathF.Abs(e.VY)) * SpareArrowTicks / 16f);
		for (int x = cx - slack; x <= cx + slack; x++) {
			bool ground = false;
			for (int y = top; y <= top + SpareDropRows; y++) {
				CellKind k = Grid.Get(x, y);
				if (k.HasLava())
					return true;
				if (k.CanStandOn() || k.IsSolid()) {
					ground = true;
					break;
				}
			}
			if (!ground)
				return true;
		}
		return false;
	}

	/// <summary>
	/// Is a spared NPC (<see cref="SparedTypes"/>) in the line of fire from (x0, y0) through (x1, y1) and on past it to
	/// <paramref name="reachPx"/> from the shooter? An arrow hits what it meets first, and an unholy one pierces 5 (Projectile.cs:954)
	/// and flies on past its target: a shot along that line could kill it.
	/// </summary>
	public bool SparedInLine(float x0, float y0, float x1, float y1, float reachPx)
	{
		if (SparedTypes.Count == 0)
			return false;
		float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
		if (len < 1f)
			return false;
		dx /= len;
		dy /= len;
		foreach (EntityInfo e in Hostiles) {
			if (!Spared(e))
				continue;
			// The nearest point of the ray (out to the reach) to the NPC's centre, against its half-size and an arrow's (10 px).
			float t = Math.Clamp((e.CenterX - x0) * dx + (e.CenterY - y0) * dy, 0f, reachPx);
			float px = x0 + dx * t, py = y0 + dy * t;
			if (Math.Abs(px - e.CenterX) <= e.Width * 0.5f + SpareMarginPx && Math.Abs(py - e.CenterY) <= e.Height * 0.5f + SpareMarginPx)
				return true;
		}
		return false;
	}

	/// <summary>Margin (px) round a spared NPC's hitbox for <see cref="SparedInLine"/>: about an arrow's own width (Projectile.SetDefaults 1: 10 px).</summary>
	public const float SpareMarginPx = 10f;

	/// <summary>
	/// Items a stage of the plan wants (the Accepts of its item needs), set by the planner each pass: never trashed to make room
	/// (BotBrain.KeepRoom). Seen in game (measured run M10u): Shadow Scales trashed at log lines 7864-7876; the Guide Voodoo Doll
	/// is worth 1000 copper, cheap enough to be the stack trashed (docs M12_SPEC.md G10).
	/// </summary>
	public System.Collections.Generic.HashSet<int> NeededItems { get; } = new();

	/// <summary>Items lying near the player this tick (nearest first).</summary>
	public System.Collections.Generic.IReadOnlyList<DropInfo> Drops { get; set; } = Array.Empty<DropInfo>();
	public CombatInfo Combat { get; set; } = new();
	public Surroundings Seen {
		get => seen;
		set {
			seen = value;
			RememberTrees(value);
		}
	}
	private Surroundings seen = new();

	/// <summary>
	/// Trees seen anywhere (base trunk tiles), kept after they go out of sight, as a player remembers where the trees were.
	/// Seen in game (measured run U8): out in the desert with 11 of 14 wood, the bot knew of no tree at all (only those on
	/// screen) and explored to and fro for 19 game hours, though it had felled trees by its house all game.
	/// </summary>
	public System.Collections.Generic.HashSet<TilePos> KnownTrees { get; } = new();
	/// <summary>A remembered tree this near a scan's origin would be in it if it still stood: it is forgotten.</summary>
	public const int TreeForgetX = 40, TreeForgetY = 12;

	private void RememberTrees(Surroundings s)
	{
		if (s.Origin is TilePos o)
			KnownTrees.RemoveWhere(t => Math.Abs(t.X - o.X) <= TreeForgetX && Math.Abs(t.Y - o.Y) <= TreeForgetY && !System.Linq.Enumerable.Contains(s.Trees, t));
		foreach (TilePos t in s.Trees)
			KnownTrees.Add(t);
	}
	public IWorldInfo World { get; set; }
	public IInventory Inventory { get; set; }
	public ICrafter Crafter { get; set; }
	/// <summary>Journey Mode's research and duplication; <see cref="NoJourney"/> (off) on Classic, Expert and Master.</summary>
	public IJourney Journey { get; set; } = NoJourney.Instance;

	/// <summary>
	/// Can the item be had at will: Journey on and the item researched, so a click in the duplication menu gives a stack of it.
	/// Always false on Classic, where every path below it is the Classic one.
	/// </summary>
	public bool Infinite(int itemType) => itemType > 0 && Journey != null && Journey.Enabled && Journey.DuplicationAllowed && Journey.IsResearched(itemType);

	/// <summary>Items the research slot refused lately, and when (Brain.ResearchHabit: not offered again for a while).</summary>
	public System.Collections.Generic.Dictionary<int, long> ResearchRefused { get; } = new();

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
	/// <summary>Rows in the world (Main.maxTilesY); 0 = unknown.</summary>
	public int WorldHeight { get; set; }
	/// <summary>Columns in the world (Main.maxTilesX, the map's width); 0 = unknown.</summary>
	public int WorldWidth { get; set; }
	/// <summary>
	/// The Underworld's first row (Main.UnderworldLayer = maxTilesY - 200, Main.cs:3295), or as set (tests); 0 = unknown. While the
	/// Wall of Flesh lives, a player whose top is above this row is grabbed by The Tongue (Player.WOFTongue, Player.cs:20618-20621).
	/// </summary>
	public int UnderworldRow
	{
		get => underworldRow > 0 ? underworldRow : WorldHeight > UnderworldRows ? WorldHeight - UnderworldRows : 0;
		set => underworldRow = value;
	}
	private int underworldRow;
	/// <summary>The Underworld's depth in rows above the world's bottom (Main.UnderworldLayer, Main.cs:3295).</summary>
	public const int UnderworldRows = 200;

	/// <summary>
	/// An errand to the Underworld (the Wall of Flesh's shaft, bridge and fight, docs M12_SPEC.md G3): no depth limit from the
	/// gear (<see cref="DeepestRow"/>) while it is on. Set by the planner for its pass; the gear's limit keeps the bot out of the
	/// caverns otherwise (defence below 16: about row 700 in the M12 world, BotContext.cs:753-786).
	/// </summary>
	public bool DepthErrand { get; set; }

	/// <summary>
	/// The rows life crystals are put in (vanilla WorldGen "Life Crystals" pass, tML 2026.08): each starts at a random row from
	/// (2·worldSurface + rockLayer)/3 down to maxTilesY − 300 and sits on the first solid tile there or below. Not remix worlds
	/// (worldSurface to maxTilesY − 400). Top 0 = unknown; bottom int.MaxValue = unknown. In measured run U9's world (surface
	/// 337, caverns 451, 1200 rows) rows 375 to 900.
	/// </summary>
	public int CrystalBandTop => SurfaceY > 0 && CavernY > 0 ? (2 * SurfaceY + CavernY) / 3 : 0;
	public int CrystalBandBottom => WorldHeight > CrystalBandFloorRows ? WorldHeight - CrystalBandFloorRows : int.MaxValue;
	/// <summary>No crystal's search row starts lower than this many rows above the world's bottom (the underworld and the rows over it).</summary>
	public const int CrystalBandFloorRows = 300;
	public bool InCrystalBand(int row) => CrystalBandTop > 0 && row >= CrystalBandTop && row <= CrystalBandBottom;

	/// <summary>
	/// Where the crystal explorations stood in the crystal band lately (leg by leg, oldest first, at most
	/// <see cref="CrystalSearchKept"/>): a new crystal job walks back to the deepest of them it may go to before exploring
	/// (MineSkill), instead of looking again from the spawn and through the rows above the band. Forgotten when a trip to new
	/// ground starts. Seen in game (measured run U9): 14 of 28 crystal explorations began within 5 tiles of the spawn (after
	/// deaths, recalls and 11 "back to the spawn to look from there"), and 25% of the 288 crystal legs were above row 375, the
	/// top of the band: the shaft dug again from the spawn each time, the cell at (2090,300) a leg target 11 times.
	/// </summary>
	public System.Collections.Generic.List<TilePos> CrystalSearch { get; } = new();
	/// <summary>The legs of one crystal job's explorations (<see cref="Skills.ExploreSkill.MaxLegs"/> × <see cref="Skills.MineSkill.MaxExplores"/>).</summary>
	public const int CrystalSearchKept = Skills.ExploreSkill.MaxLegs * Skills.MineSkill.MaxExplores;
	/// <summary>Where the player respawns (feet tile): the bed if set, else the world spawn. Null if unknown.</summary>
	public TilePos? SpawnTile { get; set; }
	/// <summary>Folder for debug dumps (the region of a search that found no path); null = none.</summary>
	public string DumpDir { get; set; }

	/// <summary>
	/// Where the bot was killed by enemies: exploring, ore and chests within <see cref="DangerZoneRadius"/> tiles are left
	/// alone for <see cref="DangerZoneTicks"/>, like a player who stays away from where they keep dying. Seen in game:
	/// antlions in the underground desert killed the bot in nearly every run. Also where it saw monsters its gear is too weak
	/// for (<see cref="DangerZone.Seen"/>: a biome's, a crowd): those scale with the gear (<see cref="ZoneRadius"/>).
	/// </summary>
	public System.Collections.Generic.List<DangerZone> DangerZones { get; } = new();
	public const int DangerZoneRadius = 30, DangerZoneTicks = 60 * 60 * 30;

	public void NoteDeathByEnemy(TilePos at)
	{
		// In the Underworld on its errand (the shaft, the hellbridge, the Wall of Flesh): the bridge is the only way there and the
		// fight's arena, and a zone over it would bar the walk back along it after the respawn (docs M12_SPEC.md risk 6).
		if (DepthErrand && UnderworldRow > 0 && at.Y >= UnderworldRow) {
			Log.Info($"Killed at {at}, in the Underworld on its errand: no danger zone there (the hellbridge is the way)");
			return;
		}
		// Not round the spawn on the surface: that is where the bot comes back to, and living there is answered by the house
		// at night, not by staying away. Seen in game (measured run P1): two zombies killed it by the house at night, the
		// spawn fell inside the zone, every job from the respawn failed "off limits" at once, and auto mode stopped.
		if (SpawnTile is TilePos spawn && Math.Abs(at.X - spawn.X) <= HomeGroundTiles && at.Y - spawn.Y <= HomeGroundRows) {
			Log.Info($"Killed at {at}, by the spawn: no danger zone there");
			return;
		}
		NoteDanger(at, DangerZoneTicks);
	}

	/// <summary>Round the spawn (columns either side, rows below it) where a death marks no danger zone.</summary>
	public const int HomeGroundTiles = 45, HomeGroundRows = 20;

	/// <summary>
	/// A danger zone at <paramref name="at"/> for <paramref name="ticks"/>, <paramref name="radius"/> tiles round; made from
	/// monsters in sight rather than a death if <paramref name="seen"/> (smaller and shorter in full armour, see <see cref="ZoneRadius"/>).
	/// </summary>
	public void NoteDanger(TilePos at, long ticks, int radius = DangerZoneRadius, bool seen = false)
	{
		DangerZones.RemoveAll(z => z.Until <= Tick);
		DangerZones.Add(new DangerZone(at, Tick, Tick + ticks, radius, seen));
	}

	/// <summary>A biome seen by its monsters spans more than a death spot: run 58 died to an antlion charger 30 tiles past the zones.</summary>
	public const int BiomeZoneRadius = 60;

	/// <summary>An evil biome seen by its monsters stays where it is: kept 5 game days (a death spot, 1.25). Run 50 went back after it lapsed.</summary>
	public const long BiomeZoneTicks = 5L * 86400;

	/// <summary>
	/// A zone from monsters seen, in full armour (<see cref="CombatInfo.FullArmour"/>): this many tiles round at most, not
	/// <see cref="BiomeZoneRadius"/>. Seen in game (measured run U9): in iron and silver armour a lone antlion charger,
	/// targeted 12 tiles off, hurt the bot 4 and 8 tiles from where it was seen (19 and 21 damage) and died to the first
	/// arrows; 20 is those 12 and its 8-tile charge. The 60-tile zones closed the ground under the spawn (x 2082-2202,
	/// y 411-531) and kept 74% of the exploration legs at row 412 or above, and the two deaths to desert monsters in the
	/// armour came inside them anyway (41 and 36 tiles from the sightings); those deaths mark zones of their own.
	/// </summary>
	public const int ArmouredSightingRadius = 20;

	/// <summary>
	/// How long a zone from monsters seen lasts in full armour, not <see cref="BiomeZoneTicks"/>: half a game day (a day
	/// is 86400 ticks), less than a death's <see cref="DangerZoneTicks"/>. Seen in game (measured run U9): every hit from a desert
	/// monster in the armour (25 of them) came within 1.5 game hours of a sighting, but each sighting held its ground for 5
	/// days; 152 of the 175 crystal-seeking hours went on the last crystal, found within 21 hours of the zone under the
	/// spawn lapsing.
	/// </summary>
	public const long ArmouredSightingTicks = 86400 / 2;

	/// <summary>How far the zone reaches with the gear worn: a sighting's, in full armour, <see cref="ArmouredSightingRadius"/> at most.</summary>
	public int ZoneRadius(DangerZone z) => z.Seen && Combat.FullArmour ? Math.Min(z.Radius, ArmouredSightingRadius) : z.Radius;

	/// <summary>When the zone lapses with the gear worn: a sighting's, in full armour, <see cref="ArmouredSightingTicks"/> after it was seen at most.</summary>
	public long ZoneUntil(DangerZone z) => z.Seen && Combat.FullArmour ? Math.Min(z.Until, z.Since + ArmouredSightingTicks) : z.Until;

	/// <summary>
	/// Somewhere to stay away from for now: where the bot was killed lately, or the underground desert while the gear
	/// is weak. Seen in game (world_eye run 40): antlion chargers, swarmers and rolling cacti killed the bot 6 times in
	/// one run in the underground desert east of the spawn, a place players leave until they have better gear.
	/// </summary>
	/// <summary>
	/// <see cref="OffLimitsOutright"/> for path costs (a sighting's zone in full armour is the exploring's to weigh, not the
	/// walk's: at <see cref="MoveContext.AvoidTicks"/> an edge, the path to a leg's target in it would cost more than a walk
	/// round the whole region), remembered per 8x8 block for <see cref="OffLimitsMemoTicks"/>: every edge
	/// of every search asked it, about 50 chunk lookups each (the biome shares), tens of thousands of times a search.
	/// </summary>
	public bool OffLimitsForPaths(TilePos t)
	{
		// The night's way down for mining, in weak gear: the surface away from the house costs (MoveContext.AvoidTicks a step),
		// so the way down is dug from the house rather than walked to along the surface. Not memoised: it is only on while
		// that walk is the job. Seen in game (measured run U17): "night: down into the ground to row 362" from the spawn went
		// 40 tiles west along the surface to a way down, among the zombies; three deaths in one night at (2062..2066,283).
		if (NightDescentFrom is TilePos home && SurfaceY > 0 && t.Y < SurfaceY && Math.Abs(t.X - home.X) > NightDescentShaftTiles)
			return true;
		long k = ((long)(t.X >> 3) << 32) | (uint)(t.Y >> 3);
		if (offLimitsMemo.TryGetValue(k, out var v) && Tick - v.At < OffLimitsMemoTicks && v.Zones == DangerZones.Count)
			return v.Off;
		bool off = OffLimitsOutright(t);
		offLimitsMemo[k] = (Tick, DangerZones.Count, off);
		if (offLimitsMemo.Count > 20_000)
			offLimitsMemo.Clear();
		return off;
	}

	public const int OffLimitsMemoTicks = 120;
	private readonly System.Collections.Generic.Dictionary<long, (long At, int Zones, bool Off)> offLimitsMemo = new();

	/// <summary>The gear gates of <see cref="OffLimits"/> alone (the underground desert, the evil biome), without the danger zones.</summary>
	public bool BiomeOffLimits(TilePos t) => Combat.Defense < DeepDefense && InUndergroundDesert(t)
		|| (Combat.Defense < CavernDefense || EvilStoneTooHard) && !EvilSurfaceOpen(t) && (UnderEvilGround(t) || InEvilBiome(t));

	/// <summary>
	/// Is the tile in the evil biome's underground, under its known surface: in a chunk column within
	/// <see cref="BiomeChunkRadius"/> of one whose surface layer (rows above <see cref="SurfaceY"/>) the map shows as evil
	/// ground (a chunk with <see cref="EvilChunkTiles"/> seen evil tiles, as <see cref="KnownEvil"/>), below that chunk, and
	/// not deeper than the caverns (<see cref="CavernY"/>)? A player who has seen the purple ground knows the corruption goes
	/// down under it, seen or not: vanilla's Corruption pass converts the biome's columns from the surface down to 30-50 rows
	/// below Main.worldSurface (WorldGen.cs:10838-10900) and its chasms carve on down to Main.rockLayer (ChasmRunner,
	/// WorldGen.cs:70266), and Devourers spawn wherever the player is in the Corruption zone (NPC.cs:89094). The share of
	/// evil tiles (<see cref="InEvilBiome"/>) misses that ground: the pass leaves dirt as dirt, so under one layer of corrupt
	/// grass the seen ground is mostly plain dirt, and two chunks down the grass is out of the share's window.
	/// <para>Seen in game (measured runs U25 and U27, copper gear, max life 100; the corruption's surface to x ~1960 at rows
	/// ~280, worldSurface 337, rockLayer 451): iron-ore explorations went from its edge at (1963,282) down under it, legs
	/// (1935,292) (1913,310) (1932,339) (1957,334), and Devourers killed the bot at (1961,335) and (1954,396) (U27) and at
	/// (1927,353) (U25). The zone of the first sighting, 60 tiles round (1921,335), ended a row above the second death: a
	/// fact about the place, not a radius round a sighting. In full armour with an iron pickaxe (U27, d7) a trip west past
	/// the corruption was routed under it, (1924,356) to (1866,453), and ended in an ebonstone pocket at (1872,366). The
	/// margin is the share's own window (<see cref="BiomeChunkRadius"/>): those deaths were up to a tile past the surface's
	/// edge, and the worms burrow; the spawn at x 2075-2095 is 7 chunks off it.</para>
	/// </summary>
	public bool UnderEvilGround(TilePos t)
	{
		if (World == null || Ids.EvilTiles.Length == 0 || SurfaceY <= 0 || CavernY > 0 && t.Y > CavernY)
			return false;
		int cx = (int)Math.Floor(t.X / (double)DesertChunk);
		for (int dx = -BiomeChunkRadius; dx <= BiomeChunkRadius; dx++)
			if (EvilGroundBelow(cx + dx) is int top && t.Y >= top)
				return true;
		return false;
	}

	/// <summary>
	/// The first row under the topmost chunk of the surface layer in chunk column <paramref name="cx"/> the map shows as
	/// evil ground (<see cref="EvilChunkTiles"/> seen evil tiles), or null; counted again as often as a chunk not all seen.
	/// </summary>
	private int? EvilGroundBelow(int cx)
	{
		if (evilColumns.TryGetValue(cx, out var v) && Tick - v.At < PartlySeenRecheckTicks)
			return v.Below;
		int? below = null;
		for (int cy = 0; cy * DesertChunk < SurfaceY; cy++)
			if (ChunkCounts(cx, cy).Evil >= EvilChunkTiles) {
				below = (cy + 1) * DesertChunk;
				break;
			}
		evilColumns[cx] = (Tick, below);
		return below;
	}
	private readonly System.Collections.Generic.Dictionary<int, (long At, int? Below)> evilColumns = new();

	/// <summary>
	/// Set by the planner for its pass when the work it chose is an errand in the evil biome: hunting eaters of souls for
	/// rotten chunks, picking vile mushrooms, summoning and fighting the Eater of Worlds (Milestone 10). All three are had
	/// only there: the chunks' eaters spawn only while the player is in the Corruption zone, the mushrooms grow only on its
	/// grass, and Worm Food works only in it (Player.cs:43462, docs M10_SPEC.md). Cleared by the next pass that chose
	/// something else.
	/// </summary>
	public bool EvilErrand
	{
		get => evilErrand;
		set {
			if (value != evilErrand)
				offLimitsMemo.Clear(); // the biome gate changed: the remembered answers are stale
			evilErrand = value;
		}
	}
	private bool evilErrand;

	/// <summary>
	/// The evil biome's surface, opened for an <see cref="EvilErrand"/> in a full set of armour: the gate's reason, stone the
	/// pickaxe cannot break (<see cref="EvilStoneTooHard"/>: pockets in ebonstone the bot cannot dig out of, measured run S4),
	/// is underground. Its surface is corrupt grass and dirt any pickaxe breaks; the danger there is eaters of souls, which
	/// iron armour and a bow take on (spec: 40 life, 22 contact damage). Not below the surface row (Main.worldSurface): the
	/// chasms' floors and their orbs' ebonstone shells stay a cost to paths (MoveContext.AvoidTicks a step), so a walk keeps
	/// out of them. A scoped opening, not the gate lifted: with the errand over, or in weaker armour, it is as before.
	/// </summary>
	public bool EvilSurfaceOpen(TilePos t) => EvilErrand && Combat.FullArmour && (SurfaceY <= 0 || t.Y <= SurfaceY);

	/// <summary>
	/// Half the size of the game's scene scan (tiles), round the player's centre: vanilla Main.buffScanAreaWidth and Height
	/// (Main.cs:882-884) are (maxScreenW + 800) / 16 - 1 = 169 and (maxScreenH + 800) / 16 - 1 = 124 at the smallest
	/// maxScreenW/H, 1920 x 1200; a larger screen only widens it, so counting this box undercounts, never over.
	/// </summary>
	public const int SceneScanHalfWidth = 84, SceneScanHalfHeight = 62;
	/// <summary>Weighted Corruption tiles in the scene scan for Player.ZoneCorrupt (SceneMetrics.CorruptionTileThreshold, SceneMetrics.cs:14).</summary>
	public const int CorruptionZoneTiles = 300;

	/// <summary>
	/// The Corruption's weight (<see cref="GameIds.CorruptionWeights"/>: TileID.Sets.CorruptBiome, a sunflower -10) of the
	/// seen tiles in the game's scene scan round the tile, as Player.ZoneCorrupt counts it: only the 16x16 chunks wholly
	/// inside the box (<see cref="SceneScanHalfWidth"/>), and only tiles the map has revealed, so it never counts more than
	/// the game does. Worm Food works where this is <see cref="CorruptionZoneTiles"/> or more.
	/// </summary>
	public int CorruptionAround(TilePos t)
	{
		if (World == null || Ids.CorruptionWeights.Count == 0)
			return 0;
		var (x0, x1, y0, y1) = CorruptionChunks(t);
		int sum = 0;
		for (int cx = x0; cx <= x1; cx++)
			for (int cy = Math.Max(0, y0); cy <= y1; cy++)
				sum += ChunkCounts(cx, cy).Corrupt;
		return sum;
	}

	/// <summary>The chunks <see cref="CorruptionAround"/> counts round the tile: those wholly inside the scene scan.</summary>
	private static (int X0, int X1, int Y0, int Y1) CorruptionChunks(TilePos t) => (
		(int)Math.Ceiling((t.X - SceneScanHalfWidth) / (double)DesertChunk), (int)Math.Floor((t.X + SceneScanHalfWidth + 1) / (double)DesertChunk) - 1,
		(int)Math.Ceiling((t.Y - SceneScanHalfHeight) / (double)DesertChunk), (int)Math.Floor((t.Y + SceneScanHalfHeight + 1) / (double)DesertChunk) - 1);

	/// <summary>The tiles (first and last column, first and last row) <see cref="CorruptionAround"/> counts round the tile.</summary>
	public static (int X0, int X1, int Y0, int Y1) CorruptionCounted(TilePos t)
	{
		var (x0, x1, y0, y1) = CorruptionChunks(t);
		return (x0 * DesertChunk, (x1 + 1) * DesertChunk - 1, Math.Max(0, y0) * DesertChunk, (y1 + 1) * DesertChunk - 1);
	}

	/// <summary>What one seen tile counts for the Corruption zone (<see cref="CorruptionAround"/>): 0 unseen or none.</summary>
	public int CorruptionWeightAt(int x, int y) =>
		World != null && World.IsRevealed(x, y) && Ids.CorruptionWeights.TryGetValue(World.TileTypeAt(x, y), out int w) ? w : 0;

	/// <summary>
	/// The tiles in this rectangle changed (a build): the chunks over it are counted again at their next look, not kept for
	/// <see cref="DesertRecheckTicks"/>. The Corruption zone's count round an arena just built otherwise still held the tiles
	/// its build cleared for a game minute.
	/// </summary>
	public void RecountChunks(int x0, int y0, int x1, int y1)
	{
		for (int cx = (int)Math.Floor(x0 / (double)DesertChunk); cx <= (int)Math.Floor(x1 / (double)DesertChunk); cx++)
			for (int cy = Math.Max(0, y0) / DesertChunk; cy <= Math.Max(0, y1) / DesertChunk; cy++)
				desertChunks.Remove((cx, cy));
	}

	/// <summary>
	/// Where the bot has stood and whether the evil biome's zone held there (<see cref="CombatInfo.InEvilZone"/>, as a player
	/// sees it by the background and the music), the latest look at each node: the part of an arena's floor the zone holds
	/// over is known once the bot has walked it (its build walks all of it). Seen in game (measured runs M10m and M10n,
	/// world_eow): the arena at (1872,239) had 306 corruption tiles round its middle by the bot's count; the game's count,
	/// read from the M10m checkpoints' worlds, was 346 at the middle (1920) once built and fell some 4 a column east of it,
	/// under the zone's 300 at x 1933, and the fights kited east past it to (1935,245): the worm dove and despawned.
	/// </summary>
	public ZoneMemory Zone { get; } = new();
	private long zoneNotedAt = long.MinValue;

	/// <summary>
	/// Notes the zone (<see cref="Zone"/>) where the bot stands this tick, once a tick: only standing (a jump moves the scan's
	/// centre up), only with the flag perceived, and only in the zone or on an errand in the evil biome (<see cref="EvilErrand"/>),
	/// not all over the world.
	/// </summary>
	public void NoteZone()
	{
		if (zoneNotedAt == Tick || Combat.InEvilZone is not bool z || !Body.Grounded || !z && !EvilErrand)
			return;
		zoneNotedAt = Tick;
		Zone.Note(Node, z, Tick);
	}

	/// <summary>
	/// Is the evil biome's zone here: the flag as the game shows it where the bot stands, else (another tile, or no flag) the
	/// latest look at the tile (<see cref="Zone"/>), else the count of the seen tiles round it (<see cref="CorruptionAround"/>).
	/// </summary>
	public bool InEvilZone(TilePos t) =>
		t == Node && Combat.InEvilZone is bool here ? here : Zone.At(t) ?? CorruptionAround(t) >= CorruptionZoneTiles;

	/// <summary>
	/// The evil biome's stone (ebonstone, crimstone) wants pick power 65 (vanilla Player.PickTile): with less, the bot cannot
	/// dig its way back out of a pocket it drops into. Seen in game (measured run S4): in full armour the gate opened, and
	/// 1,706 explore legs failed from crimstone pockets ringed by stone the iron pickaxe could not break.
	/// </summary>
	public bool EvilStoneTooHard => PickPower < EvilStonePickPower;
	public const int EvilStonePickPower = 65;

	/// <summary>Life crystals and reasons already logged (each skip of a crystal is said once).</summary>
	public System.Collections.Generic.HashSet<(int, int, string)> CrystalNotes { get; } = new();

	/// <summary>
	/// Life crystal cells the map has shown, kept all game wherever the bot goes, as Baritone caches the ore positions it has
	/// seen and AltoClef's block tracker the blocks: filled by every crystal lookup (<see cref="SeeCrystals"/>), a cell dropped
	/// when a lookup over it no longer shows it (mined). Seen in game (measured run U9): a crystal found at (2013,607) at d9
	/// +11.0h was lost when skeletons killed the bot 26 tiles from it; from the spawn, 325 rows up, it was beyond mining's
	/// 200-tile map lookup and the planner's 25, and only came back into range at d10 +11.5h, 23 game hours and 2 deaths later.
	/// </summary>
	public PlaceMemory KnownCrystals { get; } = new();

	/// <summary>
	/// The cells of <paramref name="crystalTile"/> the map shows within <paramref name="radius"/> of <paramref name="around"/>
	/// (<see cref="IWorldInfo.FindTiles"/>: revealed cells only, nearest first), remembered in <see cref="KnownCrystals"/>.
	/// Remembered cells in that square it does not show any more are gone (a revealed cell stays revealed) and are forgotten.
	/// </summary>
	public System.Collections.Generic.IReadOnlyList<TilePos> SeeCrystals(int crystalTile, TilePos around, int radius)
	{
		System.Collections.Generic.IReadOnlyList<TilePos> found = World.FindTiles(crystalTile, around, radius);
		if (KnownCrystals.Count > 0)
			foreach (TilePos c in KnownCrystals.Within(around.X, around.Y, radius, radius))
				if (!System.Linq.Enumerable.Contains(found, c))
					KnownCrystals.Remove(c);
		foreach (TilePos c in found)
			KnownCrystals.AddOnce(c);
		return found;
	}

	public bool OffLimits(TilePos t) => InDangerZone(t) || BiomeOffLimits(t);

	/// <summary>
	/// Do Dungeon Guardians spawn in the Dungeon? Until Skeletron is beaten (NPC.downedBoss3): with it down, every spawn in the
	/// Dungeon was a Guardian (NPC.cs:88648-88673, flag34 = !downedBoss3).
	/// </summary>
	public bool GuardiansSpawn => World != null && Ids.SkeletronHead > 0 && !World.BossDefeated(Ids.SkeletronHead);

	/// <summary>
	/// Rows of margin above the Guardian's ground. The body's centre is in the zone only below Main.worldSurface (Player.cs:15313:
	/// Center.Y > worldSurface * 16): with the feet on row y the centre is at 16y - 5 px (42 px tall, its bottom at 16(y + 1)),
	/// so the centre's row is y - 1 and the zone starts at the feet row <see cref="SurfaceY"/> + 1. Two rows over that for a knock
	/// or a fall that lowers the body, as the spec's rule has it ("below row worldSurface - 2", docs M11_SPEC.md section 4).
	/// </summary>
	public const int GuardianMarginRows = 2;

	/// <summary>
	/// Is the cell, as the body's centre cell, in the Dungeon Guardian's ground: on or below the surface row less
	/// <see cref="GuardianMarginRows"/>, with a dungeon wall
	/// behind it (seen), while Guardians spawn (<see cref="GuardiansSpawn"/>)? There Player.ZoneDungeon holds (Player.cs:15312-
	/// 15321: 250 dungeon bricks in the scene, the centre below the surface, a dungeon wall at the centre), every spawn is a
	/// Guardian at spawn rate 10, almost at once (NPC.cs:86809-86812), and a Guardian kills: 1000 contact damage, 9999 defence,
	/// flying through blocks at 8 px/tick (NPC.cs:4796-4810, 22462-22475). A game rule gated on a flag, not a place banned:
	/// with Skeletron down the Dungeon is open.
	/// </summary>
	public bool InGuardianZone(int x, int y) =>
		SurfaceY > 0 && y >= SurfaceY - GuardianMarginRows && GuardiansSpawn && World.IsDungeonWall(x, y);

	/// <summary>
	/// Would the body standing at this node (feet row) have its centre in the Guardian's ground (<see cref="InGuardianZone"/>)?
	/// The centre is in the node's right column and the row above the feet (Body: left edge x*16+6, 20 px wide, 42 tall), the
	/// left column checked too for a body off its canonical spot. Never a path's node (MoveContext.Deadly).
	/// </summary>
	public bool GuardianGround(TilePos node) =>
		SurfaceY > 0 && node.Y >= SurfaceY + 1 - GuardianMarginRows && (InGuardianZone(node.X + 1, node.Y - 1) || InGuardianZone(node.X, node.Y - 1));

	/// <summary>
	/// <see cref="OffLimits"/> as a ban, with nothing to weigh: where the bot was killed, the biome gates, and in weaker gear
	/// where it saw monsters too. In full armour a sighting's zone is a cost instead (<see cref="SightingDepth"/>), as
	/// Baritone weighs ground near mobs rather than banning it. Seen in game (measured run U9): 22 jobs failed at once in
	/// sighting zones, 17 "the explorations from here would be off limits" and 5 "every way from here is off limits".
	/// </summary>
	public bool OffLimitsOutright(TilePos t) => InZone(t, sightings: !Combat.FullArmour) || BiomeOffLimits(t);

	/// <summary>
	/// How deep the tile lies in sighting zones that full armour lets exploring cross: 0 outside them, rising to 1 at a
	/// zone's centre (summed where they overlap); 0 in weaker gear, where they are off limits outright.
	/// </summary>
	public float SightingDepth(TilePos t)
	{
		if (!Combat.FullArmour)
			return 0f;
		float depth = 0f;
		foreach (DangerZone z in DangerZones) {
			int r = ZoneRadius(z), d = z.At.ChebyshevTo(t);
			if (z.Seen && ZoneUntil(z) > Tick && d <= r)
				depth += (r + 1 - d) / (float)(r + 1);
		}
		return depth;
	}

	/// <summary>
	/// Is the tile where the seen solid tiles around it (48x48) are largely Corruption or Crimson? Seen in game (world_eye
	/// run 47): eaters of souls killed the bot there in copper gear. Players go in once they have armour (Milestone 10).
	/// </summary>
	/// <summary>Any evil-biome ground seen in the chunks around the tile (the biome is there, not just one of its fliers).</summary>
	public bool EvilGroundNear(TilePos t) => World != null && Ids.EvilTiles.Length > 0 && Share(t, c => c.Evil) > 0f;

	public bool InEvilBiome(TilePos t) => World != null && Ids.EvilTiles.Length > 0 && Share(t, c => c.Evil) >= EvilShare;

	/// <summary>Has the map shown enough of the ground round the tile to say what biome it is (the shares' own minimum)?</summary>
	public bool SeenAround(TilePos t) => World != null && Share(t, c => c.Solid) > 0f;

	/// <summary>
	/// Share of seen solid tiles that makes the evil biome: lower than a desert's, as its surface is mostly plain dirt under
	/// one layer of corrupt grass. At the desert's 15% the bot explored 70 tiles into the corruption from a trip's end at
	/// its edge, and a devourer killed it (world_eye run 70).
	/// </summary>
	public const float EvilShare = 0.07f;

	/// <summary>
	/// Chunk side for <see cref="InUndergroundDesert"/>, and the share of seen solid tiles in the 3x3 chunks around a tile
	/// that makes it desert. One chunk at 40% missed the desert's edges: an antlion swarmer killed the bot where the chunk
	/// itself was mostly stone (world_eye run 41).
	/// </summary>
	public const int DesertChunk = 16;
	public const float DesertShare = 0.15f;
	/// <summary>
	/// Chunks either side counted (2: the 80x80 around the tile). With 1 (48x48 at 25%) the desert's edge was not desert,
	/// and that edge is where most of the desert deaths were, and where run 55 got boxed in by tiles it could not mine.
	/// </summary>
	public const int BiomeChunkRadius = 2;
	/// <summary>How long a chunk's counts are kept before it is counted again (more of it may have been seen).</summary>
	public const int DesertRecheckTicks = 60 * 60;
	/// <summary>How soon a chunk not all seen yet is counted again.</summary>
	public const int PartlySeenRecheckTicks = 60;
	private readonly System.Collections.Generic.Dictionary<(int, int), (long At, bool AllSeen, int Solid, int Desert, int Evil, int Corrupt)> desertChunks = new();

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
	private float Share(TilePos t, Func<(int Solid, int Desert, int Evil, int Corrupt), int> count)
	{
		int cx = (int)Math.Floor(t.X / (double)DesertChunk), cy = t.Y / DesertChunk;
		int solid = 0, some = 0;
		for (int dx = -BiomeChunkRadius; dx <= BiomeChunkRadius; dx++)
			for (int dy = -BiomeChunkRadius; dy <= BiomeChunkRadius; dy++) {
				var c = ChunkCounts(cx + dx, cy + dy);
				solid += c.Solid;
				some += count(c);
			}
		return solid >= 80 ? some / (float)solid : 0f;
	}

	/// <summary>The chunk's seen tiles: all of them, the desert's, the evil biome's, and the Corruption zone's weight (<see cref="CorruptionAround"/>).</summary>
	private (int Solid, int Desert, int Evil, int Corrupt) ChunkCounts(int cx, int cy)
	{
		// A chunk not yet all seen is counted again soon: counted from afar (a path search) before the bot got there, the
		// underground desert stayed "not desert" for a game minute while the bot explored it (world_eye run 59).
		if (desertChunks.TryGetValue((cx, cy), out var v) && Tick - v.At < (v.AllSeen ? DesertRecheckTicks : PartlySeenRecheckTicks))
			return (v.Solid, v.Desert, v.Evil, v.Corrupt);
		int solid = 0, desert = 0, evil = 0, corrupt = 0, seen = 0;
		for (int x = cx * DesertChunk; x < (cx + 1) * DesertChunk; x++)
			for (int y = cy * DesertChunk; y < (cy + 1) * DesertChunk; y++) {
				if (!World.IsRevealed(x, y))
					continue;
				seen++;
				int type = World.TileTypeAt(x, y);
				if (type < 0)
					continue;
				solid++;
				if (Array.IndexOf(Ids.DesertTiles, type) >= 0)
					desert++;
				else if (Array.IndexOf(Ids.EvilTiles, type) >= 0)
					evil++;
				if (Ids.CorruptionWeights.TryGetValue(type, out int weight))
					corrupt += weight;
			}
		desertChunks[(cx, cy)] = (Tick, seen == DesertChunk * DesertChunk, solid, desert, evil, corrupt);
		var centre = new TilePos(cx * DesertChunk + DesertChunk / 2, cy * DesertChunk + DesertChunk / 2);
		if (evil >= EvilChunkTiles)
			KnownEvil.AddOnce(centre);
		else if (KnownEvil.Count > 0)
			KnownEvil.Remove(centre);
		return (solid, desert, evil, corrupt);
	}

	/// <summary>
	/// The evil biome's ground the map has shown, kept all game wherever the bot goes, as <see cref="KnownCrystals"/>: the
	/// centre of every chunk counted (by any look at the biome round a tile, a path's gate or the planner's) with at least
	/// <see cref="EvilChunkTiles"/> seen evil tiles in it, dropped when a count of it no longer shows them. Seen in game
	/// (measured run M10a): the checkpoint's map showed the corruption at x 1880-1960, 130-210 tiles west of the spawn,
	/// and the bot explored underground for it for 12 game hours ("exploring for the corruption (none seen yet)").
	/// </summary>
	public PlaceMemory KnownEvil { get; } = new();
	/// <summary>Seen evil tiles that make a chunk evil ground for <see cref="KnownEvil"/>: a chunk's width, one layer of corrupt grass across it.</summary>
	public const int EvilChunkTiles = DesertChunk;

	/// <summary>
	/// Is the ground under this surface spot (13 wide, 8 deep) largely desert or evil-biome tiles the bot has seen, while
	/// its gear is too weak for them (as <see cref="OffLimits"/>)? For choosing where to travel.
	/// </summary>
	public bool BiomeGround(TilePos t)
	{
		if (World == null)
			return false;
		int solid = 0, desert = 0, evil = 0;
		for (int x = t.X - 6; x <= t.X + 6; x++)
			for (int y = t.Y + 1; y <= t.Y + 8; y++) {
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
		if (solid < 20)
			return false;
		return Combat.Defense < DeepDefense && desert >= 0.3f * solid || Combat.Defense < CavernDefense && evil >= 0.3f * solid;
	}

	/// <summary>Is the tile inside a danger zone (where the bot was recently killed, or saw monsters), as far as it reaches with the gear worn?</summary>
	public bool InDangerZone(TilePos t) => InZone(t, sightings: true);

	/// <summary>
	/// Was the bot killed within <paramref name="tiles"/> of the tile lately (a death's danger zone still standing, however
	/// far that zone itself reaches)? Not where monsters were only seen.
	/// </summary>
	public bool KilledWithin(TilePos t, int tiles)
	{
		foreach (DangerZone z in DangerZones)
			if (!z.Seen && ZoneUntil(z) > Tick && z.At.ChebyshevTo(t) <= tiles)
				return true;
		return false;
	}

	private bool InZone(TilePos t, bool sightings)
	{
		foreach (DangerZone z in DangerZones)
			if ((sightings || !z.Seen) && ZoneUntil(z) > Tick && z.At.ChebyshevTo(t) <= ZoneRadius(z))
				return true;
		return false;
	}

	/// <summary>Defense below this keeps exploring and mining above <see cref="CavernY"/> (+ <see cref="ShallowMargin"/>).</summary>
	public const int CavernDefense = 8;
	/// <summary>Defense below this keeps them within <see cref="CavernDepth"/> rows into the caverns.</summary>
	public const int DeepDefense = 16;
	/// <summary>
	/// Rows relative to <see cref="CavernY"/>: without armour stay above the caverns (in game, 15 rows into them was still
	/// skeletons and mother slimes; at 10 rows above, mining at the limit kept dropping the bot into them, runs 36-47);
	/// with some armour, <see cref="CavernDepth"/> rows into them.
	/// </summary>
	public const int ShallowMargin = -20, CavernDepth = 150;
	/// <summary>
	/// With an iron-tier sword or better (10+ damage) but no armour: down to 15 rows above the caverns. At 20 rows above,
	/// the ore of that band near the spawn ran out and the bot travelled back and forth for a whole night (run 48); at 10,
	/// mining at the limit dropped it into the caverns again (run 58: a salamander at row 460).
	/// </summary>
	public const int ArmedMargin = -15, GoodWeaponDamage = 10;

	/// <summary>
	/// The deepest row worth going to for ore and exploring with the armour worn, like a player who stays out of the
	/// caverns in copper gear. Seen in game: exploring for iron in no armour went 200 tiles down, and skeletons, cave
	/// bats and black slimes killed the bot 5 times in an hour. int.MaxValue = no limit.
	/// </summary>
	public int DeepestRow => CavernY <= 0 || DepthErrand ? int.MaxValue
		: Combat.Defense < CavernDefense ? CavernY + (Combat.WeaponDamage >= GoodWeaponDamage ? ArmedMargin : ShallowMargin) + DepthBonus
		: Combat.Defense < DeepDefense ? CavernY + CavernDepth + DepthBonus
		: int.MaxValue;

	/// <summary>
	/// Rows deeper than the gear alone allows, taken on when the ground within reach has nothing left and there is nowhere to
	/// travel (as a player goes deeper once the shallow ore is gone). Seen in game (world_eye run 70): between a corruption
	/// and a desert, with the near caves full of slimes, every mining job failed until auto mode stopped.
	/// </summary>
	public int DepthBonus { get; set; }
	public const int DepthBonusStep = 25, MaxDepthBonus = 100;

	/// <summary>Mining/placing capabilities (null = the bot may not change the world).</summary>
	public IBlockActions Blocks { get; set; }
	/// <summary>
	/// Where mining out of a block the body overlaps came to nothing (the grid says solid, the body stands there): from
	/// here the walk plans from where the body is instead of mining at it again.
	/// </summary>
	public TilePos? UnstickFailedAt { get; set; }
	/// <summary>Player-controlled switches (/bot dig, /bot build).</summary>
	public bool AllowBreak { get; set; } = true;
	public bool AllowPlace { get; set; } = true;
	/// <summary>
	/// Tiles a skill found it cannot get to (behind unbreakable blocks, out of the world...), shared across skill
	/// restarts so a fresh skill does not spend its time on them again. For <see cref="UnreachableTicks"/> only, not for
	/// ever: the world changes, the bot gets better tools, and a crystal in such a set was passed by with no reason given.
	/// </summary>
	public ExpiringSet<TilePos> UnreachableTiles { get; } = new(UnreachableTicks);
	public const long UnreachableTicks = 3600; // a game hour
	/// <summary>
	/// Per ore item: explorations in a row that found none (MineSkill), across restarts of the skill. Counted for every ore
	/// of the job's tier group (MineSkill.TierGroup): the exploration looked for all of them.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, int> ExploresWithoutOre { get; } = new();
	/// <summary>
	/// Per ore item: legs walked by explorations that something else cut off (a fight's aftermath, a chest, a recall),
	/// short of a whole exploration's worth (Skills.ExploreSkill.MaxLegs): such an exploration is taken off
	/// <see cref="ExploresWithoutOre"/> again until its legs and the others' add up to one (MineSkill.Stop). Cleared with it.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, int> ExploreLegsCut { get; } = new();
	/// <summary>
	/// Per item that grows back on the evil biome's surface: passes along it in a row that found none (MineSkill,
	/// Skills.PatrolSkill), across restarts of the skill, as <see cref="ExploresWithoutOre"/>.
	/// </summary>
	public System.Collections.Generic.Dictionary<int, int> PatrolPasses { get; } = new();

	/// <summary>Which way the last exploration with no side to prefer went (alternated across mining jobs).</summary>
	public int LastExploreSide { get; set; } = -1;
	/// <summary>Farther than this (tiles) from the spawn, exploring with no safer side goes on outwards.</summary>
	public const int OnwardFromSpawnTiles = 60;

	/// <summary>
	/// The side (-1 left, 1 right) with less known off-limits ground ahead and below <paramref name="from"/>; with neither
	/// worse, the other side from last time. Seen in game (world_eye run 60): every mining job (restarted after each fight)
	/// explored right first, into the underground desert by the spawn, and antlion chargers killed the bot twice.
	/// </summary>
	public int SaferSide(TilePos from)
	{
		int left = OffLimitsAhead(from, -1), right = OffLimitsAhead(from, 1);
		// Out on a trip (far from the spawn), neither side worse: onwards, away from the spawn, not back over the ground the
		// trip crossed. Seen in game (world_eye run 67): 360 tiles out, every exploration headed back west across the desert.
		int onward = SpawnTile is TilePos home && Math.Abs(from.X - home.X) > OnwardFromSpawnTiles ? Math.Sign(from.X - home.X) : 0;
		int side = left < right ? -1 : right < left ? 1 : onward != 0 ? onward : -LastExploreSide;
		LastExploreSide = side;
		return side;
	}

	/// <summary>
	/// Where an exploration last was (a leg's start or end) and which way it was heading there (ExploreSkill keeps it, turns
	/// included). Seen in game (10-day world_eye runs, audit of 2026-10-16: 138-262 mining heartbeats a run, ore found in
	/// 15-28% of explorations): each exploration went the other way from the one before, so at the gear's depth floor, where
	/// legs are level tunnels, one tunnelled east and the next walked back west through it from its far end.
	/// </summary>
	public (TilePos At, int Heading)? LastExplore { get; set; }
	/// <summary>A new exploration this close (tiles) to where the last one was carries on its heading: one leg's reach.</summary>
	public const int KeepHeadingTiles = Skills.ExploreSkill.MaxLegTiles;

	/// <summary>
	/// The heading for a new exploration from <paramref name="from"/>: on the way the last one was going, if it was near
	/// here and that side has no more off-limits ground ahead than the other (a player, like Baritone, keeps on into new
	/// ground); else (the first one, or somewhere else now) <see cref="SaferSide"/>.
	/// </summary>
	public int ExploreSide(TilePos from)
	{
		if (LastExplore is { } last && last.Heading != 0 && last.At.ChebyshevTo(from) <= KeepHeadingTiles
			&& OffLimitsAhead(from, last.Heading) <= OffLimitsAhead(from, -last.Heading))
			return LastExploreSide = last.Heading;
		return SaferSide(from);
	}

	/// <summary>How many of the spots ahead on one side (out to 60 tiles, down to 60 rows) are off limits.</summary>
	public int OffLimitsAhead(TilePos from, int side)
	{
		int n = 0;
		for (int dx = 15; dx <= 60; dx += 15)
			for (int dy = -10; dy <= 60; dy += 14)
				if (OffLimits(new TilePos(from.X + side * dx, from.Y + dy)))
					n++;
		return n;
	}

	/// <summary>Known off-limits ground under a surface spot (the underground desert, a biome zone): not somewhere to mine from.</summary>
	public bool OffLimitsBelow(TilePos surface)
	{
		for (int dx = -30; dx <= 30; dx += 15)
			for (int dy = 20; dy <= 100; dy += 20)
				if (OffLimits(new TilePos(surface.X + dx, surface.Y + dy)))
					return true;
		return false;
	}
	/// <summary>Cells path moves must neither mine nor fill (a building in progress); null or empty = none.</summary>
	public System.Collections.Generic.IReadOnlySet<TilePos> Reserved { get; set; }
	private ReservedBlocks reservedBlocks;
	/// <summary>
	/// Blocks of a build in progress that path moves may mine at the cost of placing them again (<see cref="RebuiltBlocks"/>):
	/// the arena's floor. Seen in game (measured run U23): under the arena floor it had bridged over a dip, the walk to a
	/// cell above it found "no path ... (start h 3, rows 251..374)" three times over, and the site was given up.
	/// </summary>
	public System.Collections.Generic.IReadOnlySet<TilePos> Rebuilt { get; set; }
	private RebuiltBlocks rebuiltBlocks;

	/// <summary>
	/// Cells of what the bot has built (the house, the arena floor), kept off limits to path moves for good: walking,
	/// mining and sheltering never break them or fill them. Set by the planner. Seen in game: a retreat dug its
	/// hole through the house floor, and the house had to be rebuilt.
	/// </summary>
	public System.Collections.Generic.IReadOnlySet<TilePos> Protected { get; set; }
	private ReservedBlocks protectedBlocks;
	private VetoedBlocks vetoedBlocks;
	/// <summary>Cells where placing a block failed twice in the world: path moves do not plan to place there, for <see cref="VetoTicks"/>.</summary>
	public ExpiringSet<TilePos> NoPlace { get; } = new(VetoTicks);
	/// <summary>Path edges that failed <see cref="Skills.WalkToSkill.MaxSameStuck"/> times: the planner skips them for <see cref="VetoTicks"/>.</summary>
	public ExpiringSet<(TilePos From, TilePos To)> BadEdges { get; } = new(VetoTicks);
	/// <summary>Times each step got stuck lately (within <see cref="StepFailureMemoryTicks"/>), over all walks: each costs the planner more.</summary>
	public System.Collections.Generic.Dictionary<(TilePos From, TilePos To), (int Count, long At)> StepFailures { get; } = new();
	/// <summary>Cells where breaking failed twice in the world: path moves do not plan to break them, for <see cref="VetoTicks"/>.</summary>
	public ExpiringSet<TilePos> NoBreak { get; } = new(VetoTicks);
	/// <summary>How long a vetoed step or cell stays vetoed: two game hours, then it is tried again.</summary>
	public const long VetoTicks = 2 * 3600;
	/// <summary>A step's failures are forgotten this long after the last one; each recent one adds <see cref="StepFailureTicks"/> to its cost.</summary>
	public const long StepFailureMemoryTicks = 2 * 3600;
	public const float StepFailureTicks = 150f;

	/// <summary>One more failure of the step; returns how many there have been lately.</summary>
	public int NoteStepFailure(TilePos from, TilePos to)
	{
		int n = StepFailures.TryGetValue((from, to), out var f) && Tick - f.At < StepFailureMemoryTicks ? f.Count + 1 : 1;
		StepFailures[(from, to)] = (n, Tick);
		return n;
	}

	/// <summary>Extra planner cost of a step that failed lately (a decaying cost, not a ban: Baritone keeps none).</summary>
	public float StepPenalty(TilePos from, TilePos to) =>
		StepFailures.Count > 0 && StepFailures.TryGetValue((from, to), out var f) && Tick - f.At < StepFailureMemoryTicks ? f.Count * StepFailureTicks : 0f;

	/// <summary>
	/// Times mining gave up on each tile lately (within <see cref="MineFailureMemoryTicks"/>), over all mining jobs: still
	/// there after hacking at it, walked to and still out of reach, no path to it. Seen in game (audit of the 10-day world_eye
	/// runs, 2026-10-16): the give-up went only into the job's own skip list, so the next job (and each "a life crystal came
	/// into sight" replan) went for the same tile first. Facts that fade with time, as <see cref="StepFailures"/>: the brain's
	/// rest after failures and the planner's ForgetGivenUp leave them be.
	/// </summary>
	public System.Collections.Generic.Dictionary<TilePos, (int Count, long At)> MineFailures { get; } = new();
	/// <summary>A tile's mining failures are forgotten this long after the last one: two game hours, as a failed step's.</summary>
	public const long MineFailureMemoryTicks = StepFailureMemoryTicks;
	/// <summary>
	/// Each failure lately takes 1/this of a search's reach off a tile (<see cref="InMiningReach"/>): after one it is gone for
	/// only within half the reach, and after fresh tiles; after two, not until they are forgotten. A cost, not a ban.
	/// </summary>
	public const int MineFailuresToForgo = 2;

	/// <summary>One more give-up on mining the tile; returns how many there have been lately.</summary>
	public int NoteMineFailure(TilePos t)
	{
		int n = MineFailuresLately(t) + 1;
		MineFailures[t] = (n, Tick);
		return n;
	}

	public int MineFailuresLately(TilePos t) =>
		MineFailures.Count > 0 && MineFailures.TryGetValue(t, out var f) && Tick - f.At < MineFailureMemoryTicks ? f.Count : 0;

	/// <summary>Tiles of distance a tile's failures lately add to it, for a search reaching <paramref name="reach"/> tiles.</summary>
	public float MineFailureCost(TilePos t, int reach) => MineFailuresLately(t) * reach / (float)MineFailuresToForgo;

	/// <summary>Is the tile within <paramref name="reach"/> of <paramref name="from"/>, its failures lately counted as distance?</summary>
	public bool InMiningReach(TilePos t, TilePos from, int reach) => from.ChebyshevTo(t) + MineFailureCost(t, reach) <= reach;

	/// <summary>
	/// Nodes a walk's deep search (WalkToSkill: no "nothing closer than the start" end) started from lately and found nothing:
	/// not searched deep again from there for <see cref="VetoTicks"/> (each such search is up to a whole search's nodes).
	/// </summary>
	public ExpiringSet<TilePos> DeepSearchedFrom { get; } = new(VetoTicks);

	public BotContext()
	{
		UnreachableTiles.Clock = NoPlace.Clock = NoBreak.Clock = DeepSearchedFrom.Clock = () => Tick;
		BadEdges.Clock = () => Tick;
	}
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
			if (Rebuilt != null && Rebuilt.Count > 0) {
				if (rebuiltBlocks == null || !ReferenceEquals(rebuiltBlocks.Inner, inner) || !ReferenceEquals(rebuiltBlocks.Rebuilt, Rebuilt))
					rebuiltBlocks = new RebuiltBlocks(inner, Rebuilt);
				inner = rebuiltBlocks;
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
				|| moves.AvoidBelowRow != DeepestRow || moves.DigAnyway != DigAnyway)
				moves = new MoveContext(Grid, Physics) { Blocks = PathBlocks, AllowBreak = AllowBreak, AllowPlace = AllowPlace, BadEdges = BadEdges, EdgePenalty = StepPenalty, AvoidBelowRow = DeepestRow, Avoid = OffLimitsForPaths, DigAnyway = DigAnyway, Deadly = GuardianGround };
			return moves;
		}
	}

	public TilePos Node => Core.Pathfinding.Body.NodeOf(Body, Physics);

	/// <summary>Boxed in after all the rests: dig through sand and beside water anyway (MoveContext.DigAnyway) until a skill succeeds.</summary>
	public bool DigAnyway { get; set; }
}

/// <summary>
/// A place to keep away from for a while (<see cref="BotContext.DangerZones"/>), noted at <see cref="Since"/>: where the bot
/// was killed, or, if <see cref="Seen"/>, where it saw monsters its gear is too weak for (a biome's, a crowd). The radius and
/// end are those for weak gear; <see cref="BotContext.ZoneRadius"/> and <see cref="BotContext.ZoneUntil"/> scale a sighting's
/// to the gear worn.
/// </summary>
public readonly record struct DangerZone(TilePos At, long Since, long Until, int Radius, bool Seen);
