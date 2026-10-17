using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraBot.Game;

/// <summary>
/// Debug overlay: planned path, current step (with a jump's swept cells), target/goal markers in the world,
/// plus a HUD block at the top of the screen with the bot's on/off state, skill, step and inputs.
/// GameInterfaceLayer.Draw begins/ends the SpriteBatch itself (Game scale = world zoom, UI scale = UI zoom).
/// </summary>
public sealed class DebugOverlaySystem : ModSystem
{
	private const int T = 16;
	private bool drawErrorLogged;

	private static readonly Color WalkColor = new(255, 255, 255);
	private static readonly Color StepColor = new(80, 255, 120);
	private static readonly Color DropColor = new(80, 170, 255);
	private static readonly Color JumpColor = new(255, 170, 40);
	private static readonly Color PillarColor = new(200, 120, 255);
	private static readonly Color BreakColor = new(255, 70, 70);
	private static readonly Color PlaceColor = new(90, 255, 140);

	public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
	{
		int index = layers.FindIndex(l => l.Name.Equals("Vanilla: Mouse Text"));
		if (index < 0)
			index = layers.Count;
		layers.Insert(index, new LegacyGameInterfaceLayer("TerraBot: World Overlay", () => Guard(DrawWorld), InterfaceScaleType.Game));
		layers.Insert(index + 1, new LegacyGameInterfaceLayer("TerraBot: HUD", () => Guard(DrawHud), InterfaceScaleType.UI));
	}

	private bool Guard(Action draw)
	{
		if (Main.gameMenu)
			return true;
		try {
			draw();
		}
		catch (Exception e) {
			if (!drawErrorLogged) {
				drawErrorLogged = true;
				Mod.Logger.Error("Overlay draw failed (logged once)", e);
			}
		}
		return true;
	}

	private static TerraBotPlayer Bot => Main.LocalPlayer?.GetModPlayer<TerraBotPlayer>();

	private void DrawWorld()
	{
		TerraBotPlayer bot = Bot;
		if (bot == null)
			return;
		SpriteBatch sb = Main.spriteBatch;
		var walk = bot.Brain.Current as WalkToSkill;

		// Player node box (what the planner thinks the body occupies).
		TilePos node = Body.NodeOf(bot.Player.position.X, bot.Player.position.Y, bot.LivePhysics);
		DrawNodeBox(sb, node, Color.Magenta * 0.8f);

		if (walk?.Executor is { } exec) {
			IReadOnlyList<PathStep> steps = exec.Steps;
			for (int i = 0; i < steps.Count; i++) {
				PathStep s = steps[i];
				Color c = KindColor(s.Kind) * (i < exec.StepIndex ? 0.25f : i == exec.StepIndex ? 1f : 0.7f);
				Utils.DrawLine(sb, NodeCenter(s.From), NodeCenter(s.To), c, c, i == exec.StepIndex ? 3f : 2f);
				FillWorldRect(sb, (int)NodeCenter(s.To).X - 2, (s.To.Y + 1) * T - 4, 4, 4, c);
			}
			// Cells to mine (red) / build (green); the current step's are drawn bold.
			for (int i = exec.StepIndex; i < steps.Count; i++) {
				if (!steps[i].HasActions)
					continue;
				int thickness = i == exec.StepIndex ? 2 : 1;
				foreach (BlockAction a in steps[i].Actions) {
					Color ac = a.Kind == ActionKind.Break ? BreakColor : PlaceColor;
					DrawWorldRectOutline(sb, a.Cell.X * T, a.Cell.Y * T, T, T, ac * (i == exec.StepIndex ? 1f : 0.6f), thickness);
				}
			}
			if (exec.Current is PathStep cur) {
				DrawNodeBox(sb, cur.To, KindColor(cur.Kind));
				if (cur.Template != null) {
					foreach (TilePos cell in cur.Template.BodyCells)
						FillWorldRect(sb, (cur.From.X + cell.X) * T, (cur.From.Y + cell.Y) * T, T, T, JumpColor * 0.12f);
					foreach (TilePos cell in cur.Template.CrossCells)
						FillWorldRect(sb, (cur.From.X + cell.X) * T, (cur.From.Y + cell.Y) * T + T - 3, T, 3, Color.Red * 0.6f);
				}
			}
		}

		if (walk?.Goal is GoalStandAt goal)
			DrawWorldRectOutline(sb, (goal.Feet.X - 1) * T, (goal.Feet.Y - 2) * T, 3 * T, 3 * T, Color.Cyan * 0.7f, 1);
		if (bot.Brain.Target is TilePos target)
			DrawWorldRectOutline(sb, target.X * T, target.Y * T, T, T, Color.Yellow, 2);
	}

	private void DrawHud()
	{
		TerraBotPlayer bot = Bot;
		if (bot == null)
			return;
		SpriteBatch sb = Main.spriteBatch;
		float x = Main.screenWidth / Main.UIScale / 2f;
		float y = 84f;
		string keys = $"[{BotKeybinds.KeyName(BotKeybinds.Toggle)}] toggle  [{BotKeybinds.KeyName(BotKeybinds.SetTarget)}] target  /bot";

		if (!bot.Enabled) {
			string idle = bot.Brain.Target is TilePos t ? $"  target {t}" : "";
			Line(sb, $"TerraBot OFF{idle}   {keys}", x, ref y, Color.LightGray * 0.85f);
			return;
		}

		Line(sb, $"TerraBot ON   {keys}", x, ref y, Color.LightGreen);
		Line(sb, bot.Brain.Describe(), x, ref y, Color.White);
		if (bot.Brain.Current is WalkToSkill w) {
			if (w.Executor?.Current is PathStep cur)
				Line(sb, $"step: {cur}  phase: {w.Executor.Phase}", x, ref y, KindColor(cur.Kind));
			if (w.LastPlan != null)
				Line(sb, $"plan: {w.LastPlan}", x, ref y, Color.LightGray);
		}
		Line(sb, $"dig {(bot.Context.AllowBreak ? "on" : "off")}  build {(bot.Context.AllowPlace ? "on" : "off")}  |  {bot.Blocks.Describe()}", x, ref y, Color.LightGray);
		TilePos node = Body.NodeOf(bot.Player.position.X, bot.Player.position.Y, bot.LivePhysics);
		Line(sb, $"tile {bot.FeetTile()} node {node} vel ({bot.Player.velocity.X:0.00},{bot.Player.velocity.Y:0.00}) input {bot.LastOutput}"
			+ (bot.TraceTicksLeft > 0 ? $"  TRACE {bot.TraceTicksLeft / 60}s" : ""), x, ref y, Color.LightGray);
	}

	private static void Line(SpriteBatch sb, string text, float x, ref float y, Color color)
	{
		Utils.DrawBorderString(sb, text, new Vector2(x, y), color, 0.85f, 0.5f, 0f);
		y += 20f;
	}

	private static Color KindColor(MoveKind k) => k switch {
		MoveKind.StepUp => StepColor,
		MoveKind.Drop or MoveKind.ArrestedDrop => DropColor,
		MoveKind.Jump => JumpColor,
		MoveKind.Pillar => PillarColor,
		_ => WalkColor,
	};

	/// <summary>World-space centre of the body when standing at <paramref name="n"/>.</summary>
	private static Vector2 NodeCenter(TilePos n) => new(n.X * T + T, (n.Y + 1) * T - 21);

	private static void DrawNodeBox(SpriteBatch sb, TilePos n, Color c) =>
		DrawWorldRectOutline(sb, n.X * T, (n.Y - 2) * T, 2 * T, 3 * T, c, 1);

	private static void FillWorldRect(SpriteBatch sb, int wx, int wy, int w, int h, Color c)
	{
		var r = new Rectangle((int)(wx - Main.screenPosition.X), (int)(wy - Main.screenPosition.Y), w, h);
		sb.Draw(TextureAssets.MagicPixel.Value, r, c);
	}

	private static void DrawWorldRectOutline(SpriteBatch sb, int wx, int wy, int w, int h, Color c, int thickness)
	{
		FillWorldRect(sb, wx, wy, w, thickness, c);
		FillWorldRect(sb, wx, wy + h - thickness, w, thickness, c);
		FillWorldRect(sb, wx, wy, thickness, h, c);
		FillWorldRect(sb, wx + w - thickness, wy, thickness, h, c);
	}
}
