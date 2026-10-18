using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot.Building;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Rooms for the town NPCs (<see cref="Blueprint.NpcRoom"/>), built onto the bot's house as a row with shared walls: enough that
/// every NPC without a home, and every NPC the game would let come now, finds an empty valid room, and at least one room empty
/// (the game moves an NPC in only where a room is free: WorldGen.SpawnTownNPC returns Blocked for an occupied one, ScoreRoom's
/// hiScore -1). A comfort, not a need: what it cannot do it leaves for a day (<see cref="ProgressionPlanner.NpcHousingRetryTicks"/>).
/// </summary>
public sealed record NpcHousing(string Label) : Need(Label);

/// <summary>
/// At least <see cref="Count"/> town NPCs living in the bot's rooms (its house and the NPC rooms): else wait for the next one
/// to move in, while one can come. The "housing" plan's end (and its self-test's), not part of the boss plans.
/// </summary>
public sealed record NpcsHoused(int Count, string Label) : Need(Label);

/// <summary>Town NPC housing (<see cref="NpcHousing"/>, <see cref="NpcsHoused"/>).</summary>
public sealed partial class ProgressionPlanner
{
	/// <summary>NPC rooms at most (the house besides): the pre-Hardmode town is about ten (wiki: terraria.wiki.gg/wiki/NPCs).</summary>
	public const int MaxNpcRooms = 10;
	/// <summary>
	/// Longest the stage works on one room (6 game hours, from the planner's first skill for it) before it is left for a day:
	/// a room is about 30 wood, a gel and two dozen blocks, an hour or two of a player's day; the boss plan must not wait on it.
	/// </summary>
	public const int NpcRoomMaxTicks = 6 * 3600;
	/// <summary>How long NPC housing is left alone once given up on (a game day).</summary>
	public const int NpcHousingRetryTicks = 24 * 3600;
	/// <summary>Skills for the stage (materials: wood, gel...) that may fail in a row before it is given up on for a day.</summary>
	public const int MaxNpcHousingFailures = 2;
	/// <summary>
	/// The rooms' survey (the game's housing check on each, ScoreRoom counting the tiles 46 columns and 44 rows round it) is
	/// kept this long: the planner runs it on every pass, and the NPCs move in and out on the game's own 7200-tick clock.
	/// </summary>
	public const int NpcSurveyTicks = 120;
	/// <summary>Extra cost (columns) of a room on the house's door side: its door then opens into the room, not outdoors.</summary>
	public const int NpcRoomDoorSidePenalty = 50;

	/// <summary>NPC rooms built (origin = top-left of the plan, the shared column included), in building order.</summary>
	private readonly List<(TilePos Origin, int Shared)> npcRooms = new();
	/// <summary>The NPC room being built, if any.</summary>
	private (TilePos Origin, int Shared)? npcRoomSite;
	private readonly HashSet<TilePos> badNpcSlots = new();
	private int npcRoomFailures, npcHousingFailures;
	private ISkill npcHousingSkill;
	private bool npcHousingActive;
	private long npcHousingStartedAt, npcHousingOffAt = long.MinValue / 2;
	private bool npcRoomsLookedFor;
	private string npcHousingNote;
	private (long Tick, NpcSurvey Survey)? npcSurvey;

	/// <summary>The NPC rooms built so far (top-left of each plan, and its <see cref="Blueprint.SharedSide"/>).</summary>
	public IReadOnlyList<(TilePos Origin, int Shared)> NpcRooms => npcRooms;

	/// <summary>The bot's rooms as the game sees them, and who wants one.</summary>
	private sealed class NpcSurvey
	{
		public int Rooms, Valid, Empty, Homeless, Want;
		public List<string> Occupants = new(), Invalid = new();
		public IReadOnlyList<(int Type, string Name)> Comers = Array.Empty<(int, string)>();
	}

	private void ResetNpcHousing()
	{
		npcRooms.Clear();
		npcRoomSite = null;
		badNpcSlots.Clear();
		npcRoomFailures = 0;
		npcHousingFailures = 0;
		npcHousingSkill = null;
		npcHousingActive = false;
		npcHousingOffAt = long.MinValue / 2;
		npcRoomsLookedFor = false;
		npcHousingNote = null;
		npcSurvey = null;
	}

	/// <summary>The house and the NPC rooms: each plan's top-left tile and the plan.</summary>
	private IEnumerable<(TilePos Origin, Blueprint Plan)> OurRooms(BotContext ctx)
	{
		if (HouseSite is TilePos h)
			yield return (h, Blueprint.SmallHouse(ctx.Ids));
		foreach (var (o, s) in npcRooms)
			yield return (o, Blueprint.NpcRoom(ctx.Ids, s));
	}

	/// <summary>
	/// Every room of the bot's as a cell for the game's housing check and the tiles it covers: the house and the NPC rooms by their
	/// plans, a house that stood by its rooms' bounds (<see cref="FoundRooms"/>, a row of frame round them for the floor an NPC
	/// is given).
	/// </summary>
	private IEnumerable<(TilePos Check, TilePos Min, TilePos Max)> RoomCells(BotContext ctx)
	{
		foreach (var (o, bp) in OurRooms(ctx))
			yield return (new TilePos(o.X + bp.Inside.X, o.Y + bp.Inside.Y), o, new TilePos(o.X + bp.Width - 1, o.Y + bp.Height - 1));
		if (HouseSite == null)
			foreach (var (min, max, inside) in foundRooms)
				yield return (new TilePos(inside.X, inside.Y - 1), new TilePos(min.X - 1, min.Y - 1), new TilePos(max.X + 1, max.Y + 1));
	}

	private static bool InRoom(TilePos min, TilePos max, TilePos t) => t.X >= min.X && t.X <= max.X && t.Y >= min.Y && t.Y <= max.Y;

	/// <summary>Town NPCs whose home the game put in one of the bot's rooms (the floor tile it gave them lies in the room).</summary>
	private int HousedInOurRooms(BotContext ctx)
	{
		var rooms = RoomCells(ctx).ToList();
		return ctx.World.TownNpcHomes().Count(n => !n.Homeless && rooms.Any(r => InRoom(r.Min, r.Max, n.Home)));
	}

	private NpcSurvey Survey(BotContext ctx)
	{
		if (npcSurvey is { } last && ctx.Tick - last.Tick < NpcSurveyTicks && ctx.Tick >= last.Tick)
			return last.Survey;
		var s = new NpcSurvey();
		IReadOnlyList<TownNpcHome> homes = ctx.World.TownNpcHomes();
		var seen = new HashSet<(TilePos, TilePos)>();
		foreach (var (check, planMin, planMax) in RoomCells(ctx)) {
			// The room as the game sees it (its flood fill), not the plan's bounds: a room built against another can be one room with
			// it, and the home the game gives lies on its floor, anywhere in it. Seen in game (world_wof E46/E47, 2026-10-18): room 1
			// "valid housing" and empty by its plan, ScoreRoom -1 (occupied) from inside it, the Dryad's home at (2059,258) outside
			// the plan; the bot waited six days for a Guide with no free room.
			TilePos min = planMin, max = planMax;
			if (ctx.World.RoomAt(check.X, check.Y) is var (gMin, gMax)) {
				if (!seen.Add((gMin, gMax)))
					continue; // the same room as one counted already
				min = new TilePos(Math.Min(planMin.X, gMin.X - 1), Math.Min(planMin.Y, gMin.Y - 1));
				max = new TilePos(Math.Max(planMax.X, gMax.X + 1), Math.Max(planMax.Y, gMax.Y + 2));
			}
			s.Rooms++;
			// A room someone lives in passes the check as well (TerrariaGameInfo.CheckHousing: ScoreRoom's -1 is "occupied").
			string why = ctx.World.CheckHousing(check.X, check.Y);
			var living = homes.Where(n => !n.Homeless && InRoom(min, max, n.Home)).Select(n => n.Name).ToList();
			s.Occupants.AddRange(living);
			if (why != null) {
				s.Invalid.Add($"{min}: {why}");
				continue;
			}
			s.Valid++;
			if (living.Count == 0)
				s.Empty++;
		}
		s.Homeless = homes.Count(n => n.Homeless);
		s.Comers = ctx.World.TownNpcsThatCanCome();
		// One spare room at least: the game moves the next NPC in only where one is free, and checks every 7200 ticks of day.
		s.Want = Math.Max(1, s.Homeless + s.Comers.Count);
		npcSurvey = (ctx.Tick, s);
		return s;
	}

	private string Describe(NpcSurvey s) =>
		$"{s.Rooms} rooms ({s.Valid} valid), {s.Occupants.Count} occupied" + (s.Occupants.Count > 0 ? $" ({string.Join(", ", s.Occupants)})" : "")
		+ $", {s.Empty} empty; {s.Homeless} homeless, {s.Comers.Count} can come" + (s.Comers.Count > 0 ? $" ({string.Join(", ", s.Comers.Select(c => c.Name))})" : "")
		+ (s.Invalid.Count > 0 ? $"; not valid: {string.Join("; ", s.Invalid)}" : "");

	private void NoteNpcHousing(BotContext ctx, string line)
	{
		if (line == npcHousingNote)
			return;
		npcHousingNote = line;
		ctx.Log.Info("NPC housing: " + line);
	}

	private bool NpcHousingOff(BotContext ctx) => ctx.Tick - npcHousingOffAt < NpcHousingRetryTicks && ctx.Tick >= npcHousingOffAt;

	private void LeaveNpcHousing(BotContext ctx, string why)
	{
		npcHousingOffAt = ctx.Tick;
		npcHousingFailures = 0;
		npcHousingActive = false;
		npcHousingNote = null;
		ctx.Log.Info($"NPC housing: {why}; left for a day ({NpcHousingRetryTicks / 3600} game hours), the plan goes on");
	}

	/// <summary>
	/// <see cref="NpcHousing"/>: met while the bot's rooms have <see cref="NpcSurvey.Want"/> empty valid ones (or
	/// <see cref="MaxNpcRooms"/> NPC rooms stand); else the next room: a slot beside a room that stands (<see cref="NextNpcRoomSlot"/>),
	/// its materials as for the house, then its build. Never stuck: a stage that cannot go on is left for a day.
	/// </summary>
	private ISkill ResolveNpcHousing(Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		// A house that stood (a mod's base, or the bot's own from an earlier run once the planner's memory is fresh): its rooms are
		// the NPCs'; with too few, rooms are built near it (NextNpcRoomSlot searches round its home). Seen in game (world_wof E45,
		// 2026-10-18): the Guide dead, four NPCs homeless, "NPC rooms are built onto a house of the bot's own", no room in six days.
		TilePos house;
		if (ctx.World != null && HouseSite == null && FoundHomeValid(ctx) is TilePos foundHome && !NpcHousingOff(ctx)) {
			NpcSurvey found = Survey(ctx);
			if (found.Empty >= found.Want) {
				NoteNpcHousing(ctx, $"the house that stood: {Describe(found)}");
				return null;
			}
			house = foundHome;
		}
		// The bot's own house first (its stage comes before this one): the rooms are built onto it, and it is a room too.
		else if (ctx.World == null || HouseSite is not TilePos own || HomeInside(ctx) == null || NpcHousingOff(ctx))
			return null;
		else
			house = own;
		if (!npcRoomsLookedFor) {
			npcRoomsLookedFor = true;
			FindStandingNpcRooms(ctx, house);
		}
		NpcSurvey s = Survey(ctx);
		if (s.Empty >= s.Want || npcRooms.Count >= MaxNpcRooms) {
			npcHousingActive = false;
			NoteNpcHousing(ctx, Describe(s) + (s.Empty >= s.Want ? "" : $"; {MaxNpcRooms} NPC rooms, no more"));
			return null;
		}
		if (!npcHousingActive) {
			npcHousingActive = true;
			npcHousingStartedAt = ctx.Tick;
		}
		else if (ctx.Tick - npcHousingStartedAt > NpcRoomMaxTicks) {
			LeaveNpcHousing(ctx, $"room {npcRooms.Count + 1} not built after {NpcRoomMaxTicks / 3600} game hours ({Describe(s)})");
			return null;
		}
		if (npcRoomSite == null) {
			npcRoomSite = NextNpcRoomSlot(ctx, house);
			if (npcRoomSite == null) {
				Blueprint alone = Blueprint.NpcRoom(ctx.Ids, 0);
				LeaveNpcHousing(ctx, $"no site for room {npcRooms.Count + 1} beside the rooms that stand, nor within {alone.SearchRadius} of the house at {house}: "
					+ SiteFinder.Explain(ctx, alone, house, NpcKeepOut(ctx), badNpcSlots));
				return null;
			}
		}
		var (origin, shared) = npcRoomSite.Value;
		Blueprint bp = Blueprint.NpcRoom(ctx.Ids, shared);
		NoteNpcHousing(ctx, $"{Describe(s)}: building room {npcRooms.Count + 1} at {origin} ({bp.Name}, {SiteFinder.Edits(ctx, bp, origin)} edits)");
		ISkill skill = BuildRoom(bp, origin, $"building NPC room {npcRooms.Count + 1} at {origin}", claims, depth, ctx);
		if (stuck != null) {
			string why = stuck;
			stuck = null;
			LeaveNpcHousing(ctx, $"room {npcRooms.Count + 1}'s materials: {why}");
			return null;
		}
		npcHousingSkill = skill;
		return skill;
	}

	/// <summary>
	/// What a house-like plan at <paramref name="site"/> still needs (blocks for the missing frame cells, walls for the bare ones,
	/// each furniture item not yet placed), resolved in turn, then its build.
	/// </summary>
	private ISkill BuildRoom(Blueprint bp, TilePos site, string why, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		int blocks = 0, walls = 0;
		for (int y = 0; y < bp.Height; y++)
			for (int x = 0; x < bp.Width; x++) {
				int wx = site.X + x, wy = site.Y + y;
				if (bp.At(x, y) == PlanCell.Block && !ctx.Grid.Get(wx, wy).IsSolid())
					blocks++;
				else if (bp.At(x, y) == PlanCell.Wall && !ctx.World.HasWall(wx, wy))
					walls++;
			}
		var needs = new List<Need>();
		if (blocks > 0)
			needs.Add(new HaveItem(ctx.Ids.BuildingBlocks, blocks + BlockMargin, "building blocks"));
		// With stone to spare, the walls are Stone Wall only: carried Wood Walls stay free to be crafted back into the furniture's Wood.
		// Seen in game (world_wof E52, 2026-10-18): 42 Wood Walls counted for the walls, the door 2 Wood short, for six game days.
		IReadOnlyList<int> wallItems = bp.WallItems;
		if (walls > 0 && ctx.Ids.StoneWall > 0 && wallItems.Contains(ctx.Ids.StoneWall)
			&& ctx.Inventory.Count(ctx.Ids.StoneBlock) >= walls / 4 + 1 + blocks + BlockMargin)
			wallItems = new[] { ctx.Ids.StoneWall };
		if (walls > 0)
			needs.Add(new HaveItem(wallItems, walls, ctx.Knowledge.ItemName(wallItems[0])));
		foreach (PlanItem it in bp.Items)
			if (!BuildSkill.IsPlaced(ctx, bp, site, it))
				needs.Add(new HaveItem(FurnitureChoices(it.ItemType, ctx), 1, ctx.Knowledge.ItemName(it.ItemType)));
		foreach (Need n in needs) {
			ISkill sub = Resolve(n, claims, depth + 1, ctx);
			if (sub != null || stuck != null)
				return sub;
		}
		return Do(why, new BuildSkill(bp, site));
	}

	/// <summary>
	/// The items that do for a plan's piece of furniture: the one it names first, then every other item placing the same tile with the
	/// same footprint whose recipe takes only wood from trees or what the bag holds (an Ebonwood Chair, a Shadewood Door). The housing
	/// check counts the tile (TileID.Sets.RoomNeeds), not the item. Seen in game (world_wof E53, 2026-10-18): room 2's Wooden Chair 4
	/// Wood short with no Wood tree in reach for days, while the trees in sight gave Ebonwood.
	/// </summary>
	private static IReadOnlyList<int> FurnitureChoices(int item, BotContext ctx)
	{
		IGameKnowledge k = ctx.Knowledge;
		int tile = k.TilePlacedBy(item);
		if (tile < 0)
			return new[] { item };
		var size = ctx.World.Footprint(item);
		// Made from the bag without plain Wood (4 Ebonwood carried for a chair): first, so the Wood goes to the pieces only Wood makes.
		var fromBag = new List<int>();
		var rest = new List<int>();
		foreach (int other in k.ItemsPlacing(tile)) {
			if (other == item || fromBag.Count + rest.Count + 1 >= MaxFurnitureChoices || ctx.World.Footprint(other) != size)
				continue;
			if (ctx.Inventory.Count(other) > 0 || k.RecipesFor(other).Any(r => r.Ingredients.Count > 0
				&& r.Ingredients.All(ing => !ing.Accepts.Contains(ctx.Ids.Wood) && ctx.Inventory.CountAny(ing.Accepts) >= ing.Stack)))
				fromBag.Add(other);
			else if (k.RecipesFor(other).Any(r => r.Ingredients.Count > 0 && r.Ingredients.All(ing =>
				ing.Accepts.Any(a => k.SourceOf(a).Kind == SourceKind.Tree) || ctx.Inventory.CountAny(ing.Accepts) >= ing.Stack)))
				rest.Add(other);
		}
		return fromBag.Append(item).Concat(rest).ToList();
	}

	/// <summary>At most this many items are weighed for one piece of furniture (the plain one and the other woods').</summary>
	public const int MaxFurnitureChoices = 8;

	/// <summary>Rectangles (min, max) a new NPC room keeps off: the arenas, <see cref="ArenaHouseGap"/> round them (bosses kill town NPCs).</summary>
	private List<(TilePos Min, TilePos Max)> NpcKeepOut(BotContext ctx)
	{
		var keep = new List<(TilePos, TilePos)>();
		Blueprint arena = Blueprint.Arena(ctx.Ids);
		foreach (TilePos? a in new[] { eyeArena.Site, evilArena.Site })
			if (a is TilePos at)
				keep.Add((new TilePos(at.X - ArenaHouseGap, at.Y - ArenaHouseGap), new TilePos(at.X + arena.Width - 1 + ArenaHouseGap, at.Y + arena.Height - 1 + ArenaHouseGap)));
		foreach (var (o, bp) in OurRooms(ctx))
			keep.Add((o, new TilePos(o.X + bp.Width - 1, o.Y + bp.Height - 1)));
		return keep;
	}

	/// <summary>The columns and rows a room's plan builds on, its shared column left out (that one stands already).</summary>
	private static (TilePos Min, TilePos Max) OwnCells(TilePos origin, int shared) =>
		(new TilePos(origin.X + (shared < 0 ? 1 : 0), origin.Y),
		 new TilePos(origin.X + Blueprint.NpcRoomWidth - 1 - (shared > 0 ? 1 : 0), origin.Y + 5));

	/// <summary>
	/// Where the next NPC room goes: beside a room that stands (the house or an NPC room), sharing its side wall, on the same rows,
	/// as a player adds rooms to a house; the one with the fewest edits (<see cref="SiteFinder.Edits"/>, as for the house: no
	/// liquid, unbreakable tile, platform or station in it; trees felled), nearest the house, the house's door side dearer.
	/// With none (a slope, the arena, the house's stations beside it), a room on its own near the house (<see cref="SiteFinder"/>).
	/// </summary>
	private (TilePos Origin, int Shared)? NextNpcRoomSlot(BotContext ctx, TilePos house)
	{
		List<(TilePos Min, TilePos Max)> keepOut = NpcKeepOut(ctx);
		(TilePos, int)? best = null;
		long bestScore = long.MaxValue;
		foreach (var (o, bp) in OurRooms(ctx).ToList()) {
			foreach (int side in new[] { 1, -1 }) {
				// Right of it: the new plan's left column is its right wall; left of it, the new plan's right column its left wall.
				var origin = new TilePos(side > 0 ? o.X + bp.Width - 1 : o.X - Blueprint.NpcRoomStep, o.Y);
				int shared = -side;
				if (badNpcSlots.Contains(origin))
					continue;
				var (min, max) = OwnCells(origin, shared);
				if (keepOut.Any(r => min.X <= r.Max.X && max.X >= r.Min.X && min.Y <= r.Max.Y && max.Y >= r.Min.Y))
					continue;
				Blueprint room = Blueprint.NpcRoom(ctx.Ids, shared);
				int edits = SiteFinder.Edits(ctx, room, origin);
				if (edits < 0 || edits > room.MaxEdits)
					continue;
				bool doorSide = o == house && side < 0;
				long score = edits * (long)SiteFinder.StepScore + Math.Abs(origin.X - house.X) + (doorSide ? NpcRoomDoorSidePenalty : 0);
				if (score < bestScore) {
					bestScore = score;
					best = (origin, shared);
				}
			}
		}
		if (best != null)
			return best;
		Blueprint alone = Blueprint.NpcRoom(ctx.Ids, 0);
		return SiteFinder.Find(ctx, alone, house, badNpcSlots, keepOut) is TilePos site ? (site, 0) : null;
	}

	/// <summary>
	/// NPC rooms built before this session (a saved world, a checkpoint): beside the house and each other in the slots
	/// <see cref="NextNpcRoomSlot"/> builds in, their door where the plan puts it and the game's housing check passing.
	/// </summary>
	private void FindStandingNpcRooms(BotContext ctx, TilePos house)
	{
		for (bool more = true; more && npcRooms.Count < MaxNpcRooms;) {
			more = false;
			foreach (var (o, bp) in OurRooms(ctx).ToList())
				foreach (int side in new[] { 1, -1 }) {
					var origin = new TilePos(side > 0 ? o.X + bp.Width - 1 : o.X - Blueprint.NpcRoomStep, o.Y);
					int shared = -side;
					if (npcRooms.Any(r => r.Origin == origin) || origin == house)
						continue;
					Blueprint room = Blueprint.NpcRoom(ctx.Ids, shared);
					PlanItem door = room.Items.First(it => it.ItemType == ctx.Ids.Door);
					if (!BuildSkill.IsPlaced(ctx, room, origin, door) || ctx.World.CheckHousing(origin.X + room.Inside.X, origin.Y + room.Inside.Y) != null)
						continue;
					npcRooms.Add((origin, shared));
					ctx.Log.Info($"NPC housing: an NPC room already stands at {origin} ({room.Name}); counting it");
					more = true;
				}
		}
	}

	/// <summary>A finished skill of the NPC housing stage: rooms built or given up, and the stage's failures counted.</summary>
	private void NpcHousingFinished(ISkill skill, SkillStatus status, BotContext ctx)
	{
		if (skill is BuildSkill room && room.Blueprint.IsNpcRoom) {
			npcSurvey = null;
			if (status == SkillStatus.Succeeded) {
				npcRooms.Add((room.Origin, room.Blueprint.SharedSide));
				npcRoomSite = null;
				npcRoomFailures = 0;
				npcHousingFailures = 0;
				npcHousingActive = false;
				ctx.Log.Info($"NPC housing: room {npcRooms.Count} at {room.Origin} is valid housing ({npcRooms.Count} NPC rooms and the house)");
			}
			else if (status == SkillStatus.Failed && ++npcRoomFailures >= MaxBuildFailuresPerSite) {
				ctx.Log.Info($"NPC housing: giving up on the room site {room.Origin} ({room.FailureReason})");
				badNpcSlots.Add(room.Origin);
				npcRoomSite = null;
				npcRoomFailures = 0;
			}
			return;
		}
		if (!ReferenceEquals(skill, npcHousingSkill))
			return;
		npcHousingSkill = null;
		if (status == SkillStatus.Succeeded)
			npcHousingFailures = 0;
		// Not a death on the way (no reason given): the same work goes on after the respawn.
		else if (status == SkillStatus.Failed && !string.IsNullOrEmpty(skill.FailureReason) && ++npcHousingFailures >= MaxNpcHousingFailures)
			LeaveNpcHousing(ctx, $"{npcHousingFailures} of its skills failed in a row (the last: {skill.Name}: {skill.FailureReason})");
	}

	/// <summary>
	/// <see cref="NpcsHoused"/>: met with that many town NPCs in the bot's rooms; else wait for the next to move in (the game's
	/// check comes every 7200 ticks of day), and plan again when one has: the next room may be wanted. Moot when no NPC is
	/// homeless and none can come.
	/// </summary>
	private ISkill ResolveNpcsHoused(NpcsHoused need, BotContext ctx)
	{
		if (ctx.World == null || HouseSite == null)
			return null;
		int housed = HousedInOurRooms(ctx);
		if (housed >= need.Count)
			return null;
		NpcSurvey s = Survey(ctx);
		if (s.Homeless == 0 && s.Comers.Count == 0) {
			NoteNpcHousing(ctx, $"{housed}/{need.Count} town NPCs housed, and none homeless or able to come: not waiting");
			return null;
		}
		return Do($"waiting for a town NPC to move in ({housed}/{need.Count} housed; {Describe(s)})",
			new WaitSkill("a town NPC to move in", c => HousedInOurRooms(c) != housed, NightWaitTicks + NightLengthTicks));
	}
}
