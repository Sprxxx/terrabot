using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Bot.Skills;
using TerraBot.Core.Pathfinding;

namespace TerraBot.Bot.Brain;

/// <summary>
/// A house that already stands where the bot would build its own, not of its own blueprint: a base a mod builds for a new
/// world (StartWithBase), a player's town. Its rooms are whatever the game's own housing check passes (StartRoomCheck,
/// RoomNeeds, ScoreRoom: IWorldInfo.CheckHousing), found round the bench or the spawn; the bot lives in the nearest and the
/// NPCs in the rest. Seen in game (the user's world Journey Test, 2026-10-17): a base of a dozen rooms over the spawn, a work
/// bench in it at (3214,417); the house stage looked for a site for its own house there and stopped auto mode, "stuck on
/// house: no building site within 40 tiles of (3214,417)", five rests in a row.
/// </summary>
public sealed partial class ProgressionPlanner
{
	/// <summary>Columns either side and rows above and below the anchor looked through for rooms that stand.</summary>
	public const int FoundHomeSearchX = 40, FoundHomeSearchY = 40;
	/// <summary>Housing checks at most in one look (each the game's room flood fill, at most 750 tiles).</summary>
	public const int MaxFoundHomeChecks = 400;
	/// <summary>How long a look that found no house holds before the next (a game hour).</summary>
	public const int FoundHomeRetryTicks = 3600;

	/// <summary>The standing node the bot lives at in a house that stood (null: none found, or the bot's own house).</summary>
	public TilePos? FoundHome { get; private set; }

	/// <summary>The rooms of the house that stood: the game's room bounds (inside, open cells) and a standing node in each.</summary>
	private readonly List<(TilePos Min, TilePos Max, TilePos Inside)> foundRooms = new();
	private long foundHomeLookedAt = long.MinValue / 2;

	public IReadOnlyList<(TilePos Min, TilePos Max, TilePos Inside)> FoundRooms => foundRooms;

	private void ResetFoundHome()
	{
		FoundHome = null;
		foundRooms.Clear();
		foundHomeLookedAt = long.MinValue / 2;
	}

	/// <summary>The home of the house that stood, while the game still takes it for housing.</summary>
	private TilePos? FoundHomeValid(BotContext ctx) =>
		FoundHome is TilePos h && ctx.World.CheckHousing(h.X, h.Y - 1) == null ? h : null;

	/// <summary>
	/// Look round <paramref name="anchor"/> for rooms that stand: standing nodes with background walls behind the body, nearest
	/// first, each a room the game's housing check passes, a room counted once (its bounds, IWorldInfo.RoomAt). The home is the
	/// room nearest the anchor. True when one was found.
	/// </summary>
	private bool LookForFoundHome(BotContext ctx, TilePos anchor)
	{
		if (ctx.Tick - foundHomeLookedAt < FoundHomeRetryTicks && ctx.Tick >= foundHomeLookedAt)
			return FoundHome != null;
		foundHomeLookedAt = ctx.Tick;
		foundRooms.Clear();
		FoundHome = null;
		ITileGrid g = ctx.Grid;
		var candidates = new List<TilePos>();
		for (int x = anchor.X - FoundHomeSearchX; x <= anchor.X + FoundHomeSearchX; x++)
			for (int y = anchor.Y - FoundHomeSearchY; y <= anchor.Y + FoundHomeSearchY; y++)
				if (Body.IsStandable(g, x, y) && ctx.World.HasWall(x, y - 1) && ctx.World.HasWall(x + 1, y - 1))
					candidates.Add(new TilePos(x, y));
		int checks = 0;
		foreach (TilePos c in candidates.OrderBy(c => c.ChebyshevTo(anchor))) {
			if (checks >= MaxFoundHomeChecks)
				break;
			if (foundRooms.Any(r => c.X >= r.Min.X && c.X <= r.Max.X && c.Y - 1 >= r.Min.Y && c.Y - 1 <= r.Max.Y))
				continue;
			checks++;
			if (ctx.World.CheckHousing(c.X, c.Y - 1) != null || ctx.World.RoomAt(c.X, c.Y - 1) is not var (min, max))
				continue;
			foundRooms.Add((min, max, c));
		}
		if (foundRooms.Count == 0)
			return false;
		FoundHome = foundRooms[0].Inside;
		ctx.Log.Info($"Auto: a house already stands round {anchor}: {foundRooms.Count} rooms the game takes for housing ({checks} checked), "
			+ $"living in the one at {foundRooms[0].Min}-{foundRooms[0].Max} (standing at {FoundHome}), not building one; rooms "
			+ string.Join(", ", foundRooms.Select(r => $"{r.Min}-{r.Max}")));
		return true;
	}

	/// <summary>Tiles round the home a heart lantern hung counts for the house (its buff reaches far further: Player.cs, HeartLantern).</summary>
	public const int FoundHomeLanternTiles = 12;

	private static bool LanternNear(BotContext ctx, TilePos home)
	{
		int tile = ctx.Knowledge?.TilePlacedBy(ctx.Ids.HeartLantern) ?? -1;
		return tile >= 0 && ctx.World.FindTiles(tile, home, FoundHomeLanternTiles).Count > 0;
	}

	/// <summary>
	/// The heart lantern in a house that stood: none of its blueprint's cells to hang it at, so from the home, the nearest cell the
	/// game takes it at (PlaceItemSkill), as a player hangs one from the ceiling of the room they stand in.
	/// </summary>
	private ISkill LanternInFoundHome(TilePos home, Dictionary<int, int> claims, int depth, BotContext ctx)
	{
		if (LanternNear(ctx, home))
			return null;
		if (ctx.Inventory.Count(ctx.Ids.HeartLantern) <= 0)
			return Resolve(new HaveItem(ctx.Ids.HeartLantern, 1, ctx.Knowledge.ItemName(ctx.Ids.HeartLantern)), claims, depth + 1, ctx);
		if (ctx.Node.ChebyshevTo(home) > 2)
			return Do($"walking home to {home} to hang the heart lantern", new WalkToSkill(new GoalStandAt(home, ctx.Physics)) { Home = true });
		return Do($"hanging the heart lantern in the house that stood (home {home})", new PlaceItemSkill(ctx.Ids.HeartLantern));
	}
}
