using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// Logs every Vile Mushroom destroyed, and by what: the local player's projectile (vanilla's friendly projectiles cut
/// tileCut tiles in their path, Projectile.CutTilesAt, and a vile mushroom is one), its swing, or something else.
/// Seen in game (measured run M10p): four mushrooms the map showed were gone each time the bot got there, and it picked
/// none in 1.75 game days.
/// </summary>
public sealed class PlantWatch : GlobalTile
{
	public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
	{
		if (fail || effectOnly || type != TileID.CorruptPlants || Main.tile[i, j].TileFrameX != TerrariaGameInfo.VileMushroomFrameX
			|| Main.gameMenu || Main.netMode == NetmodeID.Server)
			return;
		Player p = Main.LocalPlayer;
		var cell = new Rectangle(i * 16 - 8, j * 16 - 8, 32, 32);
		string by = "something else";
		foreach (Projectile pr in Main.projectile)
			if (pr.active && pr.friendly && pr.owner == Main.myPlayer && pr.Hitbox.Intersects(cell)) {
				by = $"the bot's projectile {Lang.GetProjectileName(pr.type).Value}";
				break;
			}
		if (by == "something else" && p.itemAnimation > 0 && Vector2.Distance(p.Center, new Vector2(i * 16 + 8, j * 16 + 8)) < 6 * 16)
			by = $"the bot's swing of {p.HeldItem.Name}";
		int dx = i - (int)(p.Center.X / 16), dy = j - (int)(p.Center.Y / 16);
		TerraBotMod.Instance.Logger.Info($"Plant: Vile Mushroom at ({i},{j}) destroyed by {by}{(noItem ? ", no item" : "")}; the bot {dx},{dy} tiles off");
	}
}
