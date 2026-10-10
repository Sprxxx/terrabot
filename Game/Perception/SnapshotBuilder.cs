using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Terraria;

namespace TerraBot.Game.Perception;

public static class SnapshotBuilder
{
	public const int TileRadiusX = 30;
	public const int TileRadiusY = 17;
	public const float EntityRadiusPx = 80 * 16;

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	public static string BuildJson(TerraBotPlayer bot) => JsonSerializer.Serialize(Build(bot), Json);

	public static WorldSnapshot Build(TerraBotPlayer bot)
	{
		Player p = bot.Player;
		TilePos node = Body.NodeOf(p.position.X, p.position.Y, bot.LivePhysics);
		TilePos feet = bot.FeetTile();

		var snap = new WorldSnapshot {
			Mod = $"TerraBot {bot.Mod.Version}",
			Tick = Main.GameUpdateCount,
			World = $"{Main.worldName} {Main.maxTilesX}x{Main.maxTilesY}",
			Physics = bot.LivePhysics,
			Player = new PlayerInfo {
				Name = p.name,
				PosX = p.position.X,
				PosY = p.position.Y,
				VelX = p.velocity.X,
				VelY = p.velocity.Y,
				TileX = feet.X,
				TileY = feet.Y,
				NodeX = node.X,
				NodeY = node.Y,
				Grounded = p.velocity.Y == 0f,
				Life = p.statLife,
				LifeMax = p.statLifeMax2,
				Mana = p.statMana,
				ManaMax = p.statManaMax2,
				Direction = p.direction,
				Wet = p.wet,
				LavaWet = p.lavaWet,
				Dead = p.dead,
				SelectedItem = p.selectedItem,
				Jump = p.jump,
				ReleaseJump = p.releaseJump,
			},
			Bot = BuildBotInfo(bot),
			Tiles = BuildTiles(bot, node),
		};

		Vector2Like center = new(p.Center.X, p.Center.Y);
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (!n.active || !center.Within(n.Center.X, n.Center.Y, EntityRadiusPx))
				continue;
			snap.Npcs.Add(new NpcInfo {
				WhoAmI = i,
				Type = n.type,
				Name = n.FullName,
				TileX = (int)(n.Center.X / 16f),
				TileY = (int)(n.Center.Y / 16f),
				Life = n.life,
				LifeMax = n.lifeMax,
				Friendly = n.friendly,
				TownNpc = n.townNPC,
				Boss = n.boss,
			});
		}

		for (int i = 0; i < Main.maxItems; i++) {
			Item it = Main.item[i];
			if (!it.active || it.IsAir || !center.Within(it.Center.X, it.Center.Y, EntityRadiusPx))
				continue;
			snap.Items.Add(new ItemInfo {
				WhoAmI = i,
				Type = it.type,
				Name = it.Name,
				Stack = it.stack,
				TileX = (int)(it.Center.X / 16f),
				TileY = (int)(it.Center.Y / 16f),
			});
		}

		for (int i = 0; i < p.inventory.Length; i++) {
			Item it = p.inventory[i];
			if (it == null || it.IsAir)
				continue;
			snap.Inventory.Add(new SlotInfo { Slot = i, Type = it.type, Name = it.Name, Stack = it.stack });
		}
		return snap;
	}

	private static BotInfo BuildBotInfo(TerraBotPlayer bot)
	{
		var info = new BotInfo {
			Enabled = bot.Enabled,
			Target = bot.Brain.Target?.ToString(),
			State = bot.Brain.Describe(),
			LastResult = bot.Brain.LastResult,
			LastInput = bot.LastOutput.ToString(),
			Tools = bot.Blocks.Describe(),
			AllowDig = bot.Context.AllowBreak,
			AllowBuild = bot.Context.AllowPlace,
		};
		if (bot.Brain.Current is WalkToSkill w) {
			info.LastPlan = w.LastPlan?.ToString();
			if (w.Executor != null) {
				info.StepIndex = w.Executor.StepIndex;
				info.Phase = w.Executor.Phase;
				info.Path = w.Executor.Steps.Select(s => s.ToString()).ToList();
			}
		}
		return info;
	}

	private static TileWindow BuildTiles(TerraBotPlayer bot, TilePos node)
	{
		var win = new TileWindow {
			Left = node.X - TileRadiusX,
			Top = node.Y - TileRadiusY,
			Width = 2 * TileRadiusX + 1,
			Height = 2 * TileRadiusY + 1,
		};
		TilePos? target = bot.Brain.Target;
		TilePos? goal = (bot.Brain.Current as WalkToSkill)?.Goal?.Feet;
		var grid = TerrariaTileGrid.Instance;
		var sb = new StringBuilder(win.Width);
		for (int y = win.Top; y < win.Top + win.Height; y++) {
			sb.Clear();
			for (int x = win.Left; x < win.Left + win.Width; x++) {
				char c = grid.Get(x, y).ToChar();
				if (x >= node.X && x <= node.X + 1 && y >= node.Y - 2 && y <= node.Y)
					c = 'P';
				if (goal is TilePos g && g.X == x && g.Y == y)
					c = 'g';
				if (target is TilePos t && t.X == x && t.Y == y)
					c = 'T';
				sb.Append(c);
			}
			win.Rows.Add(sb.ToString());
		}
		return win;
	}

	private readonly record struct Vector2Like(float X, float Y)
	{
		public bool Within(float x, float y, float r) => (x - X) * (x - X) + (y - Y) * (y - Y) <= r * r;
	}
}
