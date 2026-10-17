using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// What the bot's fights really kill and what drops, for the logs: every hostile NPC's death near the bot (GlobalNPC.OnKill), and
/// every item an NPC's loot spawns (GlobalItem.OnSpawn with an EntitySource_Loot), with where it lies from the bot. The bot's own
/// "gone" counts a target that left its sight as a kill. Seen in game (world_boc B3, The Aggressive Site): 34 crimeras "gone"
/// (kill #1 each) and not one Vertebra seen (they drop 1 in 3, ItemDropDatabase.cs:1111), the hunt at 0 of 15 for 30 game hours.
/// </summary>
public sealed class KillWatch : GlobalNPC
{
	/// <summary>Tiles from the bot within which deaths are logged.</summary>
	public const int NearTiles = 120;

	public override void OnKill(NPC npc)
	{
		if (Main.gameMenu || Main.netMode == NetmodeID.Server || npc.friendly || npc.townNPC || npc.CountsAsACritter)
			return;
		Player p = Main.LocalPlayer;
		Vector2 d = (npc.Center - p.Center) / 16f;
		if (d.Length() > NearTiles)
			return;
		bool ours = npc.playerInteraction[Main.myPlayer];
		TerraBotMod.Instance.Logger.Info($"Kill: {npc.FullName} died at ({(int)(npc.Center.X / 16)},{(int)(npc.Center.Y / 16)}), {d.X:0},{d.Y:0} tiles from the bot"
			+ (ours ? "" : ", not hit by the bot (no loot for it)"));
	}
}

/// <summary>The loot side of <see cref="KillWatch"/>: each item an NPC drops.</summary>
public sealed class LootWatch : GlobalItem
{
	public override void OnSpawn(Item item, IEntitySource source)
	{
		if (Main.gameMenu || Main.netMode == NetmodeID.Server || source is not EntitySource_Loot loot || loot.Entity is not NPC npc)
			return;
		Player p = Main.LocalPlayer;
		Vector2 d = (item.Center - p.Center) / 16f;
		if (d.Length() > KillWatch.NearTiles)
			return;
		TerraBotMod.Instance.Logger.Info($"Loot: {item.Name} x{item.stack} from {npc.FullName} at ({(int)(item.Center.X / 16)},{(int)(item.Center.Y / 16)}), {d.X:0},{d.Y:0} tiles from the bot");
	}
}
