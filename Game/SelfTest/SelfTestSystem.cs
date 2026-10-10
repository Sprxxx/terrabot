using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game.SelfTest;

/// <summary>
/// In-game self-test runner. Active only when tModLoader is launched with <c>-terrabotselftest</c> (see
/// tools/selftest.ps1, which also uses an isolated save directory with copied player/world files). After entering
/// the world it runs every <see cref="Scenario"/> in an arena above the spawn point, logs one
/// <c>SELFTEST PASS|FAIL</c> line per scenario plus a summary, and quits the game.
/// Optional value: <c>-terrabotselftest name1,name2</c> runs only those scenarios.
/// </summary>
public sealed class SelfTestSystem : ModSystem
{
	public const string LaunchFlag = "-terrabotselftest";
	private const int StartDelayTicks = 180;
	private const int SettleTicks = 30;

	private enum Phase { Waiting, Setup, Settle, Running, Done }

	private static readonly HashSet<int> FallingBlockProjectiles = new() {
		ProjectileID.SandBallFalling, ProjectileID.EbonsandBallFalling, ProjectileID.PearlSandBallFalling,
		ProjectileID.CrimsandBallFalling, ProjectileID.SiltBall, ProjectileID.SlushBall,
	};

	public static bool Enabled => Program.LaunchParameters.ContainsKey(LaunchFlag);
	public static bool Active { get; private set; }

	public static bool IsFallingBlockProjectile(int type) => FallingBlockProjectiles.Contains(type);

	private Phase phase = Phase.Waiting;
	private int phaseTicks;
	private List<Scenario> queue;
	private int index;
	private Arena arena;
	private RunResult result;
	private bool[,] before;
	private TeeLog tee;
	private IBotLog originalLog;
	private readonly HashSet<int> seenFalling = new();
	private readonly List<string> summary = new();
	private int passed;

	public override void OnWorldLoad()
	{
		if (!Enabled)
			return;
		phase = Phase.Waiting;
		phaseTicks = 0;
		index = 0;
		passed = 0;
		summary.Clear();
		string filter = Program.LaunchParameters[LaunchFlag];
		var names = string.IsNullOrWhiteSpace(filter) || filter == "all" ? null : new HashSet<string>(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		queue = Scenario.All.Where(s => names == null || names.Contains(s.Name)).ToList();
		Mod.Logger.Info($"SELFTEST armed: {queue.Count} scenario(s): {string.Join(", ", queue.Select(s => s.Name))}");
	}

	public override void PostUpdateEverything()
	{
		if (!Enabled || Main.gameMenu || queue == null || phase == Phase.Done)
			return;
		Player p = Main.LocalPlayer;
		if (p == null || !p.active)
			return;
		var bot = p.GetModPlayer<TerraBotPlayer>();
		phaseTicks++;
		try {
			Step(p, bot);
		}
		catch (Exception e) {
			Mod.Logger.Error("SELFTEST harness error", e);
			Finish(bot, "harness exception: " + e.Message);
		}
	}

	private void Step(Player p, TerraBotPlayer bot)
	{
		switch (phase) {
			case Phase.Waiting:
				if (phaseTicks < StartDelayTicks)
					return;
				Active = true;
				arena = new Arena(Main.spawnTileX, Main.spawnTileY - 2);
				double fullGravityRow = 60 + 10 * Math.Pow(Main.maxTilesX / 4200.0, 2) + Main.worldSurface / 6.0;
				Mod.Logger.Info($"SELFTEST arena x {arena.Left}..{arena.Left + Arena.Width} y {arena.Top}..{arena.FloorY}, " +
					$"full gravity below row {fullGravityRow:0} (arena top {arena.Top}), {bot.Blocks.Describe()}");
				Next(Phase.Setup);
				return;

			case Phase.Setup: {
				Scenario s = queue[index];
				bot.SetEnabled(false, "selftest setup");
				bot.StopAll("selftest setup");
				Main.dayTime = true;
				Main.time = 27000;
				for (int i = 0; i < Main.maxNPCs; i++)
					if (Main.npc[i].active && !Main.npc[i].friendly)
						Main.npc[i].active = false;
				for (int i = 0; i < Main.maxItems; i++)
					Main.item[i].active = false;
				arena.Build(s.Rows);
				Arena.SetHotbar(p, s.Pickaxe, s.Blocks);
				TilePos start = Arena.Find(s.Rows, 'S');
				Arena.PlacePlayer(p, arena.ToWorld(start.X, start.Y, s.Rows.Length));
				Next(Phase.Settle);
				return;
			}

			case Phase.Settle: {
				Scenario s = queue[index];
				TilePos start = Arena.Find(s.Rows, 'S');
				Arena.PlacePlayer(p, arena.ToWorld(start.X, start.Y, s.Rows.Length)); // hold still while physics/frames settle
				if (phaseTicks < SettleTicks)
					return;
				result = new RunResult { MaxFeetRow = int.MinValue };
				before = arena.SnapshotSolid(s.Rows, s.Width);
				seenFalling.Clear();
				bot.HurtCount = 0;
				bot.FallDamageHits = 0;
				bot.FallingBlockHits = 0;
				bot.SetDig(s.Dig);
				bot.SetBuild(s.Build);
				originalLog = bot.Context.Log;
				tee = new TeeLog(originalLog);
				bot.Context.Log = tee;
				TilePos goal = Arena.Find(s.Rows, 'G');
				Mod.Logger.Info($"SELFTEST start {s.Name} (dig {s.Dig}, build {s.Build}, pick {s.Pickaxe}, blocks {s.Blocks}); {bot.Blocks.Describe()}");
				bot.SetTarget(arena.ToWorld(goal.X, goal.Y, s.Rows.Length), "selftest");
				bot.SetEnabled(true, "selftest");
				Next(Phase.Running);
				return;
			}

			case Phase.Running: {
				Scenario s = queue[index];
				s.During?.Invoke(arena, phaseTicks);
				TrackRun(p, bot, s);
				bool finished = bot.Brain.Current == null && bot.Brain.Target == null && phaseTicks > 2;
				if (!finished && phaseTicks < s.TimeoutTicks)
					return;
				Evaluate(bot, s, finished);
				index++;
				Next(index < queue.Count ? Phase.Setup : Phase.Done);
				if (phase == Phase.Done)
					Finish(bot, null);
				return;
			}
		}
	}

	private void TrackRun(Player p, TerraBotPlayer bot, Scenario s)
	{
		TilePos node = Body.NodeOf(p.position.X, p.position.Y, bot.LivePhysics);
		int feetRowInGrid = node.Y - (arena.FloorY - (s.Rows.Length - 1));
		if (p.velocity.Y == 0f)
			result.MaxFeetRow = Math.Max(result.MaxFeetRow, feetRowInGrid);
		for (int i = 0; i < Main.maxProjectiles; i++) {
			Projectile pr = Main.projectile[i];
			if (pr.active && FallingBlockProjectiles.Contains(pr.type) && seenFalling.Add(i))
				result.FallingBlocksSeen++;
			else if (!pr.active)
				seenFalling.Remove(i);
		}
	}

	private void Evaluate(TerraBotPlayer bot, Scenario s, bool finished)
	{
		bool[,] after = arena.SnapshotSolid(s.Rows, s.Width);
		for (int x = 0; x < s.Width; x++)
			for (int y = 0; y < s.Rows.Length; y++) {
				if (before[x, y] && !after[x, y]) result.Broken++;
				if (!before[x, y] && after[x, y]) result.Placed++;
			}
		result.Ticks = phaseTicks;
		result.Outcome = finished ? bot.Brain.LastResult : $"TIMEOUT after {phaseTicks} ticks ({bot.Brain.Describe()})";
		result.Succeeded = finished && bot.Brain.LastResult.Contains("Succeeded");
		result.FallDamageHits = bot.FallDamageHits;
		result.FallingBlockHits = bot.FallingBlockHits;
		result.Log = tee.Lines;
		bot.SetEnabled(false, "selftest evaluate");
		bot.Context.Log = originalLog;

		string reason = null;
		if (!finished)
			reason = "timeout";
		else if (result.Succeeded != s.ExpectSuccess)
			reason = $"expected {(s.ExpectSuccess ? "success" : "failure")}, got: {result.Outcome}";
		else
			reason = s.Check?.Invoke(result);

		bool ok = reason == null;
		if (ok)
			passed++;
		int plans = result.Log.Count(l => l.StartsWith("Plan #"));
		int replans = result.Log.Count(l => l.StartsWith("Re-plan"));
		string line = $"SELFTEST {(ok ? "PASS" : "FAIL")} {s.Name}{(ok ? "" : " -- " + reason)} | {result.Outcome} | ticks {result.Ticks}, " +
			$"plans {plans}, replans {replans}, broken {result.Broken}, placed {result.Placed}, lowest feet row {result.MaxFeetRow}, " +
			$"fall hits {result.FallDamageHits}, falling blocks {result.FallingBlocksSeen}";
		summary.Add(line);
		Mod.Logger.Info(line);
	}

	private void Finish(TerraBotPlayer bot, string error)
	{
		phase = Phase.Done;
		Active = false;
		bot.SetEnabled(false, "selftest done");
		if (originalLog != null)
			bot.Context.Log = originalLog;
		Mod.Logger.Info($"SELFTEST SUMMARY {passed}/{queue.Count} passed{(error != null ? " -- aborted: " + error : "")}");
		foreach (string l in summary)
			Mod.Logger.Info("SELFTEST   " + l.Substring("SELFTEST ".Length));
		Mod.Logger.Info("SELFTEST EXIT");
		Main.instance.Exit();
	}

	private void Next(Phase p)
	{
		phase = p;
		phaseTicks = 0;
	}
}

/// <summary>Forwards bot log lines and keeps a copy for the self-test checks.</summary>
public sealed class TeeLog : IBotLog
{
	private readonly IBotLog inner;
	public List<string> Lines { get; } = new();

	public TeeLog(IBotLog inner) => this.inner = inner;

	public void Info(string message) { Lines.Add(message); inner.Info(message); }
	public void Warn(string message) { Lines.Add(message); inner.Warn(message); }
	public void Error(string message, Exception ex = null) { Lines.Add(message); inner.Error(message, ex); }
	public void Debug(string message) => inner.Debug(message);
}

/// <summary>No enemy spawns while a self-test runs (they would knock the bot around and make results flaky).</summary>
public sealed class SelfTestSpawns : GlobalNPC
{
	public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
	{
		if (SelfTestSystem.Enabled)
			maxSpawns = 0;
	}
}
