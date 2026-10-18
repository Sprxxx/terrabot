using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// Why a dead town NPC does not move back in, for the logs: each game hour while the Guide is dead, the game's own spawn gates
/// (WorldGen.UpdateWorld's spawnDelay is held at 0 by an invasion or an eclipse, WorldGen.cs:66305; UpdateTime_SpawnTownNPCs runs by
/// day only, Main.cs:82818; a homeless NPC takes a free room first, WorldGen.SpawnTownNPC) and each room the bot's survey checked:
/// the room check, its needs, ScoreRoom's score and home spot, and who lives at that spot. Seen in game (world_wof E46, 2026-10-18):
/// the Guide's room built and valid, 4 NPCs homeless, and nobody moved in for six game days.
/// </summary>
public sealed class TownSpawnWatch : ModSystem
{
	/// <summary>Room cells the bot's housing check looked at (TerrariaGameInfo.CheckHousing), newest last.</summary>
	internal static readonly List<(int X, int Y)> Checked = new();
	private const int MaxChecked = 12;
	private static long next;

	internal static void Note(int x, int y)
	{
		if (Checked.Contains((x, y)))
			return;
		Checked.Add((x, y));
		if (Checked.Count > MaxChecked)
			Checked.RemoveAt(0);
	}

	public override void PostUpdateWorld()
	{
		if (Main.gameMenu || Main.netMode == NetmodeID.MultiplayerClient || NPC.AnyNPCs(NPCID.Guide))
			return;
		long now = (long)Main.GameUpdateCount;
		if (now < next)
			return;
		next = now + 3600;
		var town = Main.npc.Where(n => n.active && n.townNPC).ToList();
		string homeless = string.Join(", ", town.Where(n => n.homeless).Select(n => $"{n.TypeName}{(WorldGen.CheckSpecialTownNPCSpawningConditions(n.type) ? "" : " (conditions not met)")}"));
		var log = TerraBotMod.Instance.Logger;
		log.Info($"Town spawn: Guide dead; day {Main.dayTime} time {Main.time:0}, update rate {WorldGen.GetWorldUpdateRate():0.##}, invasion {Main.invasionType} (size {Main.invasionSize}, x {Main.invasionX:0}), eclipse {Main.eclipse}, "
			+ $"prioritized {WorldGen.prioritizedTownNPCType}, can spawn Guide {Main.townNPCCanSpawn[NPCID.Guide]}; town {town.Count}, homeless: {(homeless.Length > 0 ? homeless : "none")}");
		foreach (var (x, y) in Checked.ToList()) {
			if (!WorldGen.InWorld(x, y, 10))
				continue;
			string r;
			if (!WorldGen.StartRoomCheck(x, y))
				r = $"room check fails ({WorldGen.roomCheckFailureReason})";
			else if (!WorldGen.RoomNeeds(NPCID.Guide))
				r = "room needs not met";
			else {
				WorldGen.ScoreRoom(-1, NPCID.Guide);
				int bx = WorldGen.bestX, by = WorldGen.bestY;
				var at = town.Where(n => !n.homeless && n.homeTileX == bx && n.homeTileY == by).Select(n => n.TypeName).ToList();
				r = $"score {WorldGen.hiScore}, home spot ({bx},{by}), wall {Main.tile[x, y].WallType} house wall {Main.wallHouse[Main.tile[x, y].WallType]}"
					+ (at.Count > 0 ? $", lived in by {string.Join(", ", at)}" : ", nobody's home");
			}
			log.Info($"Town spawn: room at ({x},{y}): {r}");
		}
	}
}
