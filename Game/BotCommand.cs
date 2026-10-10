using System;
using System.Linq;
using TerraBot.Bot.Brain;
using Microsoft.Xna.Framework;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Game.Perception;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// /bot chat command. tModLoader swallows exceptions thrown from Action (it only shows the usage text), so
/// everything is wrapped and logged here. Arguments arrive lower-cased (IsCaseSensitive = false).
/// </summary>
public sealed class BotCommand : ModCommand
{
	public override CommandType Type => CommandType.Chat;
	public override string Command => "bot";
	public override string Description => "Control TerraBot";
	public override string Usage =>
		"/bot goto <x> <y>   (tile coords; ~dx ~dy = relative to you)\n" +
		"/bot stop | on | off | status | fight | auto [settle|iron|early]\n" +
		"/bot dig [on|off]   /bot build [on|off]   (allow mining / placing blocks)\n" +
		"/bot dump   (JSON snapshot to client.log)\n" +
		"/bot trace [on|off|<seconds>]   (per-tick log)";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		if (args.Length == 0)
			throw new UsageException();

		var bot = caller.Player.GetModPlayer<TerraBotPlayer>();
		try {
			switch (args[0]) {
				case "goto":
					Goto(caller, bot, args);
					break;
				case "auto": {
					string plan = args.Length > 1 ? args[1].ToLowerInvariant() : ProgressionPlanner.Plans.Settle;
					if (!ProgressionPlanner.Plans.All.Contains(plan)) {
						caller.Reply($"Unknown plan {plan}. Plans: {string.Join(", ", ProgressionPlanner.Plans.All)}", Color.OrangeRed);
						break;
					}
					bot.Brain.SetAuto(true, bot.Context, plan);
					bot.SetEnabled(true, "/bot auto");
					caller.Reply(plan switch {
						ProgressionPlanner.Plans.Early => "TerraBot auto (early): work bench, wooden sword, 30 wood. Fights anything that comes close.",
						ProgressionPlanner.Plans.Iron => "TerraBot auto (iron): work bench, wooden sword, furnace, anvil, iron-tier pickaxe. Fights, hides at night.",
						_ => "TerraBot auto (settle): the iron chain, then a house, an iron-tier broadsword and armour. Fights, hides at night.",
					}, Color.LightGray);
					break;
				}
				case "fight":
					bot.Brain.StartSkill(new Bot.Skills.FightSkill(), bot.Context);
					bot.SetEnabled(true, "/bot fight");
					caller.Reply($"TerraBot fighting nearby enemies. {bot.Blocks.Describe()}", Color.LightGray);
					break;
				case "stop":
					bot.StopAll("/bot stop");
					caller.Reply("TerraBot stopped (target cleared). Bot is still " + (bot.Enabled ? "ON" : "OFF") + ".", Color.LightGray);
					break;
				case "on":
					bot.SetEnabled(true, "/bot on");
					break;
				case "off":
					bot.SetEnabled(false, "/bot off");
					break;
				case "status":
					Status(caller, bot);
					break;
				case "dump": {
					string json = SnapshotBuilder.BuildJson(bot);
					Mod.Logger.Info("SNAPSHOT " + json);
					caller.Reply($"TerraBot snapshot written to client.log ({json.Length} chars).", Color.LightGray);
					break;
				}
				case "trace":
					Trace(caller, bot, args);
					break;
				case "dig": {
					bool on = Toggle(args, bot.Context.AllowBreak);
					bot.SetDig(on);
					caller.Reply($"TerraBot digging {(on ? "ON" : "OFF")}. {bot.Blocks.Describe()}", Color.LightGray);
					break;
				}
				case "build": {
					bool on = Toggle(args, bot.Context.AllowPlace);
					bot.SetBuild(on);
					caller.Reply($"TerraBot building {(on ? "ON" : "OFF")}. {bot.Blocks.Describe()}", Color.LightGray);
					break;
				}
				default:
					throw new UsageException($"Unknown subcommand '{args[0]}'.\n{Usage}");
			}
		}
		catch (UsageException) {
			throw;
		}
		catch (Exception e) {
			Mod.Logger.Error($"/bot {string.Join(' ', args)} failed", e);
			caller.Reply("TerraBot error: " + e.Message, Color.OrangeRed);
		}
	}

	private static void Goto(CommandCaller caller, TerraBotPlayer bot, string[] args)
	{
		if (args.Length < 3)
			throw new UsageException("Usage: /bot goto <x> <y>  or  /bot goto ~dx ~dy");
		TilePos me = bot.FeetTile();
		int x = ParseCoord(args[1], me.X);
		int y = ParseCoord(args[2], me.Y);
		bot.SetTarget(new TilePos(x, y), "/bot goto");
		bot.SetEnabled(true, "/bot goto");
	}

	/// <summary>"on"/"off" set the flag; no argument flips it.</summary>
	private static bool Toggle(string[] args, bool current) =>
		args.Length < 2 ? !current
		: args[1] == "on" ? true
		: args[1] == "off" ? false
		: throw new UsageException("Expected on or off.");

	private static int ParseCoord(string s, int relativeTo)
	{
		if (s.StartsWith('~'))
			return relativeTo + (s.Length == 1 ? 0 : int.Parse(s.Substring(1)));
		return int.Parse(s);
	}

	private void Status(CommandCaller caller, TerraBotPlayer bot)
	{
		string plan = bot.Brain.Current is WalkToSkill w && w.LastPlan != null ? " | plan " + w.LastPlan : "";
		string line = $"TerraBot {(bot.Enabled ? "ON" : "OFF")} | {bot.Brain.Describe()}{plan} | you: {bot.DescribePosition()}"
			+ $" | dig {(bot.Context.AllowBreak ? "on" : "off")}, build {(bot.Context.AllowPlace ? "on" : "off")} | {bot.Blocks.Describe()}";
		Mod.Logger.Info("STATUS " + line);
		caller.Reply(line, Color.LightGray);
	}

	private void Trace(CommandCaller caller, TerraBotPlayer bot, string[] args)
	{
		string arg = args.Length > 1 ? args[1] : "on";
		if (arg == "off")
			bot.TraceTicksLeft = 0;
		else if (arg == "on")
			bot.TraceTicksLeft = TerraBotPlayer.DefaultTraceTicks;
		else
			bot.TraceTicksLeft = Math.Max(1, int.Parse(arg)) * 60;
		Mod.Logger.Info($"Trace {(bot.TraceTicksLeft > 0 ? $"on for {bot.TraceTicksLeft} ticks" : "off")}");
		caller.Reply(bot.TraceTicksLeft > 0
			? $"TerraBot trace on for {bot.TraceTicksLeft / 60} s (logs every tick while walking)."
			: "TerraBot trace off.", Color.LightGray);
	}
}
