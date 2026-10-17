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
/// With <c>-terrabotfast</c> the game runs as fast as the PC allows instead of 60 ticks a second (vanilla's unused
/// Main.superFast switch: Main.DoUpdate then skips its 1/60 s accumulator and turns vsync off; it only has an effect
/// when the Frame Skip setting is Off or Subtle; vsync needs <see cref="KeepVsyncOff"/>).
/// <c>-terrabotfast N</c> also draws only every N-th frame (Game.SuppressDraw), so a slow-drawing PC is not the limit.
/// </summary>
public sealed class SelfTestSystem : ModSystem
{
	public const string LaunchFlag = "-terrabotselftest";
	public const string FastFlag = "-terrabotfast";
	/// <summary>Debug experiment: finding things ignores the map (TerrariaGameInfo.XRay). Self-test runs only.</summary>
	public const string XRayFlag = "-terrabotxray";
	/// <summary>Real-world runs: save the world and player at these milestones (CheckpointMilestones), to start later runs from.</summary>
	public const string CheckpointFlag = "-terrabotcheckpoints";
	/// <summary>Real-world runs: a game-time budget in days instead of the scenario's own (a run from a checkpoint).</summary>
	public const string DaysFlag = "-terrabotdays";
	/// <summary>
	/// Milestones a checkpoint is saved at: the late stages can then be measured from there in a few minutes instead of a
	/// whole 10-day run from day 1 (user, 2026-10-16: "do we REALLY have to start from the beginning every time?").
	/// "Eye loot collected": after the Eye's demonite and unholy arrows are picked up (items on the ground are not saved with the
	/// world), the start of the world_eow runs.
	/// </summary>
	public static readonly string[] CheckpointMilestones = { "full armour", "max life 200", "arena site", "Eye of Cthulhu defeated", "Eye loot collected",
		// Milestone 10's: the Eater fight measured from the Worm Food in hand, not after the chunk hunt and the mushrooms again.
		"vile powder 30", "worm food" };
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
	private string stallStatus;
	private int stallSince;
	/// <summary>6 game hours (3600 ticks each): slow mining often holds one status for 3.</summary>
	private const int StallTicks = 6 * 3600;
	private IBotLog originalLog;
	private readonly HashSet<int> seenFalling = new();
	private readonly List<int> spawned = new();
	private bool spawnFailed;
	private int lifeAtStart;
	private int deathsAtStart;
	/// <summary>Ticks between heartbeat lines during a scenario (30 game seconds).</summary>
	/// <summary>Mode "surface": ticks under water before surfacing starts (breath about 140/200, as when the brain's reflex fires).</summary>
	public const int SurfaceAfterTicks = 420;
	public const int HeartbeatTicks = 1800;
	/// <summary>Enemy spawns stay on (real-world scenarios); otherwise the self-test turns them off.</summary>
	public static bool SpawnsOn { get; private set; }
	private TilePos? buildSite;
	private readonly List<string> summary = new();
	private int passed;
	/// <summary>In fast mode, draw only every this many updates.</summary>
	private int drawEvery = 1;
	/// <summary>Real time since the scenario started running, and since the last heartbeat: the speed against 60 ticks a second.</summary>
	private readonly System.Diagnostics.Stopwatch runClock = new(), beatClock = new();

	private static string Speed(int ticks, System.Diagnostics.Stopwatch clock) =>
		clock.Elapsed.TotalSeconds > 0 ? $"{ticks / 60.0 / clock.Elapsed.TotalSeconds:0.0}x" : "?";

	public override void Load()
	{
		if (!Enabled)
			return;
		if (Program.LaunchParameters.ContainsKey(FastFlag))
			On_Main.SetResolution += KeepVsyncOff;
		focusField = typeof(Microsoft.Xna.Framework.Game).GetField("INTERNAL_isActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		if (focusField != null)
			On_Main.DoUpdate += RunInBackground;
		else
			Mod.Logger.Warn("SELFTEST: FNA focus field not found; the game will pause while its window is not focused");
	}

	private static System.Reflection.FieldInfo focusField;

	/// <summary>
	/// Singleplayer pauses while the window is not focused (Main.DoUpdate: hasFocus = Game.IsActive, then gamePaused).
	/// The self-test runs unattended, so it tells the game it still has focus at the start of every update. FNA only
	/// clears the flag on a focus-lost event, polled once per frame before Update. Keyboard input reaches only the
	/// focused window, so typing elsewhere does not reach the game.
	/// </summary>
	private static void RunInBackground(On_Main.orig_DoUpdate orig, Main self, ref Microsoft.Xna.Framework.GameTime gameTime)
	{
		focusField.SetValue(self, true);
		orig(self, ref gameTime);
	}

	/// <summary>
	/// Main.superFast clears the vsync flag every update but never applies it, and Main.SetResolution (called before
	/// every draw, via UpdateDisplaySettings) turns it back on and resets the device whenever the flag is off. So in
	/// fast mode: let SetResolution see it on (no reset), then switch the device itself to immediate presenting once,
	/// and again only if a resolution change put vsync back.
	/// </summary>
	private static void KeepVsyncOff(On_Main.orig_SetResolution orig, int width, int height)
	{
		if (!Main.superFast) {
			orig(width, height);
			return;
		}
		Main.graphics.SynchronizeWithVerticalRetrace = true;
		orig(width, height);
		var device = Main.graphics.GraphicsDevice;
		if (device != null && device.PresentationParameters.PresentationInterval != Microsoft.Xna.Framework.Graphics.PresentInterval.Immediate) {
			Main.graphics.SynchronizeWithVerticalRetrace = false;
			Main.graphics.ApplyChanges();
			ModContent.GetInstance<TerraBotMod>().Logger.Info("SELFTEST fast: vsync off");
		}
	}

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
		// Real-world runs take up to an hour: only when asked for by name.
		queue = Scenario.All.Where(s => names == null ? !s.RealWorld : names.Contains(s.Name)).ToList();
		Main.superFast = Program.LaunchParameters.TryGetValue(FastFlag, out string fast);
		drawEvery = int.TryParse(fast, out int n) && n > 1 ? n : 1;
		TerrariaGameInfo.XRay = Program.LaunchParameters.ContainsKey(XRayFlag);
		if (TerrariaGameInfo.XRay)
			Mod.Logger.Warn("SELFTEST X-RAY ON: ore, crystals, stations and chests are found without the map (debug experiment)");
		Mod.Logger.Info($"SELFTEST armed: {queue.Count} scenario(s): {string.Join(", ", queue.Select(s => s.Name))}; "
			+ $"fast {Main.superFast}, drawing every {drawEvery} frame(s) (frame skip {Main.FrameSkipMode})");
	}

	public override void PostUpdateEverything()
	{
		if (!Enabled || Main.gameMenu || queue == null || phase == Phase.Done)
			return;
		Player p = Main.LocalPlayer;
		if (p == null || !p.active)
			return;
		var bot = p.GetModPlayer<TerraBotPlayer>();
		if (Main.superFast && drawEvery > 1 && Main.GameUpdateCount % drawEvery != 0) {
			Main.instance.SuppressDraw();
			// The lighting pass that a drawn frame would run (vanilla Main.DoLightTiles), so the map is revealed and light
			// is measured as often per game tick as in real play: vanilla does both only while drawing, and drawing every
			// 16th frame left the bot seeing 1/16 as much as a player would (Main.Map.IsRevealed, Lighting.Brightness).
			Main.GetAreaToLight(out int x0, out int x1, out int y0, out int y1);
			Lighting.LightTiles(x0, x1, y0, y1);
		}
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
				arena = new Arena(Main.spawnTileX, Main.spawnTileY - 2, queue.Max(q => q.Width));
				double fullGravityRow = 60 + 10 * Math.Pow(Main.maxTilesX / 4200.0, 2) + Main.worldSurface / 6.0;
				Mod.Logger.Info($"SELFTEST world: surface row {Main.worldSurface:0}, caverns from row {Main.rockLayer:0}, {Main.maxTilesX}x{Main.maxTilesY}");
				Mod.Logger.Info($"SELFTEST arena x {arena.Left}..{arena.Left + arena.Width} y {arena.Top}..{arena.FloorY}, " +
					$"full gravity below row {fullGravityRow:0} (arena top {arena.Top}), {bot.Blocks.Describe()}");
				Next(Phase.Setup);
				return;

			case Phase.Setup: {
				Scenario s = queue[index];
				bot.SetEnabled(false, "selftest setup");
				bot.StopAll("selftest setup");
				Main.dayTime = true;
				Main.time = s.DayTime;
				if (s.Night) {
					Main.dayTime = false;
					Main.time = 0;
				}
				SpawnsOn = s.RealWorld;
				if (s.RealWorld) {
					// The world as it is: its town NPCs, its enemies, the character's own inventory, at the spawn.
					s.Inventory?.Invoke(p);
					Arena.StripHelpers(p);
					Arena.PlacePlayer(p, new TilePos(Main.spawnTileX - 1, Main.spawnTileY - 1));
					Next(Phase.Settle);
					return;
				}
				// Every NPC goes, town NPCs included: the Guide shoots arrows at hostiles and would help the bot.
				// (This is the throw-away copy of the world in the self-test save folder.)
				for (int i = 0; i < Main.maxNPCs; i++)
					if (Main.npc[i].active)
						Main.npc[i].active = false;
				for (int i = 0; i < Main.maxItems; i++)
					Main.item[i].active = false;
				arena.Build(s.Rows);
				CachedTileGrid.Instance.Clear();
				Arena.SetHotbar(p, s.Pickaxe, s.Blocks, s.Weapon, s.Axe);
				s.Inventory?.Invoke(p);
				if (s.ResetLife) {
					p.ConsumedLifeCrystals = 0;
					p.ConsumedLifeFruit = 0;
					p.statLifeMax = 100;
				}
				if (s.MaxLife > 0) {
					p.ConsumedLifeCrystals = (s.MaxLife - 100) / 20;
					p.ConsumedLifeFruit = 0;
					p.statLifeMax = s.MaxLife;
				}
				if (s.ResetBosses) {
					NPC.downedBoss1 = false;
					NPC.downedSlimeKing = false;
				}
				for (int i = 0; i < s.Wear.Length && i < 3; i++)
					p.armor[i] = new Item(s.Wear[i]);
				Arena.StripHelpers(p);
				TilePos start = Arena.Find(s.Rows, 'S');
				Arena.PlacePlayer(p, arena.ToWorld(start.X, start.Y, s.Rows.Length));
				Next(Phase.Settle);
				return;
			}

			case Phase.Settle: {
				Scenario s = queue[index];
				if (!s.RealWorld) {
					TilePos start = Arena.Find(s.Rows, 'S');
					Arena.PlacePlayer(p, arena.ToWorld(start.X, start.Y, s.Rows.Length)); // hold still while physics/frames settle
				}
				if (phaseTicks < SettleTicks)
					return;
				result = new RunResult { MaxFeetRow = int.MinValue };
				milestones.Clear();
				milestoneSeen.Clear();
				before = s.RealWorld ? null : arena.SnapshotSolid(s.Rows, s.Width);
				seenFalling.Clear();
				bot.HurtCount = 0;
				bot.DamageTaken = 0;
				deathsAtStart = bot.Context.Deaths;
				bot.FallDamageHits = 0;
				bot.FallingBlockHits = 0;
				bot.SetDig(s.Dig);
				bot.SetBuild(s.Build);
				originalLog = bot.Context.Log;
				tee = new TeeLog(originalLog);
				bot.Context.Log = tee;
				Mod.Logger.Info($"SELFTEST start {s.Name} (mode {s.Mode}, dig {s.Dig}, build {s.Build}, pick {s.Pickaxe}, blocks {s.Blocks}); {bot.Blocks.Describe()}");
				Mod.Logger.Info("SELFTEST inventory: " + string.Join(", ", Enumerable.Range(0, 58).Where(i => !p.inventory[i].IsAir).Select(i => $"{i}:{p.inventory[i].Name} x{p.inventory[i].stack}")));
				spawned.Clear();
				spawnFailed = false;
				if (s.EnemyType != 0 && s.SpawnAtTick == 0)
					SpawnEnemies(s);
				lifeAtStart = p.statLife;
				bot.Context.DigAnyway = false; // a last resort from the scenario before (seen: it let a walk test undermine sand)
				bot.Context.DepthBonus = 0;
				if (s.Mode == "fight") {
					bot.Brain.StartSkill(new Bot.Skills.FightSkill(), bot.Context);
				}
				else if (s.Mode == "chop") {
					bot.Brain.StartSkill(new Bot.Skills.GatherWoodSkill(Terraria.ID.ItemID.Wood, 5), bot.Context);
				}
				else if (s.Mode == "boss") {
					bot.Brain.StartSkill(new Bot.Skills.BossFightSkill(ItemID.SuspiciousLookingEye, NPCID.EyeofCthulhu, NPCID.ServantofCthulhu), bot.Context);
				}
				else if (s.Mode == "build") {
					Bot.Building.Blueprint bp = Bot.Building.Blueprint.SmallHouse(bot.Context.Ids);
					TilePos? site = Bot.Building.SiteFinder.Find(bot.Context, bp, bot.FeetTile());
					buildSite = site;
					if (site != null)
						bot.Brain.StartSkill(new Bot.Building.BuildSkill(bp, site.Value), bot.Context);
					else
						Mod.Logger.Warn("SELFTEST no building site found");
				}
				else if (s.Mode == "blast") {
					TilePos g = Arena.Find(s.Rows, 'G');
					var goalAt = new Core.Pathfinding.GoalStandAt(arena.ToWorld(g.X, g.Y, s.Rows.Length), bot.Context.Physics);
					bot.Brain.StartSkill(new Bot.Skills.BlastOutSkill(goalAt), bot.Context);
				}
				else if (s.Mode == "auto") {
					bot.Brain.SetAuto(true, bot.Context, s.AutoPlan);
				}
				else if (s.Mode == "pathbench" || s.Mode == "arenasite") {
					// Run once things have settled (Running phase, tick 120): PathBench.
				}
				else if (s.Mode == "surface") {
					// Started once breath is down, in the Running phase (SurfaceAfterTicks), as the brain's reflex would.
				}
				else {
					TilePos goal = Arena.Find(s.Rows, 'G');
					bot.SetTarget(arena.ToWorld(goal.X, goal.Y, s.Rows.Length), "selftest");
				}
				bot.SetEnabled(true, "selftest");
				runClock.Restart();
				beatClock.Restart();
				Next(Phase.Running);
				return;
			}

			case Phase.Running: {
				Scenario s = queue[index];
				s.During?.Invoke(arena, phaseTicks);
				if (s.Mode == "pathbench" && phaseTicks == 120)
					PathBench.Run(bot.Context, Mod);
				if (s.Mode == "arenasite" && phaseTicks == 120) {
					var bp = Bot.Building.Blueprint.Arena(bot.Context.Ids);
					Mod.Logger.Info($"ARENASITE round {bot.Context.Node}: {Bot.Building.SiteFinder.Explain(bot.Context, bp, bot.Context.Node)}; "
						+ $"found {Bot.Building.SiteFinder.Find(bot.Context, bp, bot.Context.Node)?.ToString() ?? "none"} done");
				}
				if (s.Mode == "surface" && phaseTicks == SurfaceAfterTicks)
					bot.Brain.StartSkill(new Bot.Skills.SurfaceSkill(), bot.Context);
				if (s.EnemyType != 0 && s.SpawnAtTick > 0 && phaseTicks == s.SpawnAtTick)
					SpawnEnemies(s);
				if (spawned.Count > 0 && phaseTicks % 30 == 0)
					Mod.Logger.Info($"SELFTEST t{phaseTicks} npcs: " + string.Join("; ", spawned.Where(i => i >= 0 && i < Main.maxNPCs).Select(i => {
						NPC n = Main.npc[i];
						return $"#{i} {n.FullName} active {n.active} life {n.life} at ({n.Center.X / 16f:0},{n.Center.Y / 16f:0}) chase {n.CanBeChasedBy()}";
					})) + $" | bot sees {bot.Context.Hostiles.Count} | {bot.Brain.Describe()}");
				TrackRun(p, bot, s);
				if (s.RealWorld && phaseTicks % 60 == 0)
					TrackMilestones(p, bot);
				// Heartbeat, so a watcher can tell a frozen game (no lines at all) from a quiet one.
				if (phaseTicks > 0 && phaseTicks % HeartbeatTicks == 0) {
					Mod.Logger.Info($"SELFTEST heartbeat {s.Name} t{phaseTicks} speed {Speed(HeartbeatTicks, beatClock)} ({Main.frameRate} fps, present {Main.instance.GraphicsDevice.PresentationParameters.PresentationInterval}, "
						+ $"{beatClock.Elapsed.TotalMilliseconds / HeartbeatTicks:0.00} ms/tick of which bot {TerraBotPlayer.BotTime.Elapsed.TotalMilliseconds / HeartbeatTicks:0.00}) at {bot.FeetTile()}, life {p.statLife}/{p.statLifeMax2}: {bot.Brain.Describe()}");
					beatClock.Restart();
					TerraBotPlayer.BotTime.Reset();
				}
				// Real-world runs: a plan status unchanged for game hours is a stall (seen: one fight for 7 game days, found
				// late because nothing else was logged at INFO).
				if (s.RealWorld && phaseTicks % HeartbeatTicks == 0) {
					string st = bot.Brain.Planner.Status;
					// Waiting out the night (at home or dug in) is not a stall.
					if (st != stallStatus || bot.Brain.Current is Bot.Skills.StayHomeSkill or Bot.Skills.ShelterSkill) {
						stallStatus = st;
						stallSince = phaseTicks;
					}
					else if (phaseTicks - stallSince >= StallTicks && (phaseTicks - stallSince) % StallTicks < HeartbeatTicks)
						Mod.Logger.Warn($"SELFTEST stall: plan [{st}] unchanged for {(phaseTicks - stallSince) / 3600} game hours; {bot.Brain.Describe()}");
				}
				// Real-world runs: every 2 minutes, what the bot has and where the plan is.
				if (s.RealWorld && phaseTicks % (HeartbeatTicks * 4) == 0)
					Mod.Logger.Info($"SELFTEST status {s.Name} t{phaseTicks} {(Main.dayTime ? "day" : "night")} {Main.time:0}: plan [{bot.Brain.Planner.Status}] "
						+ $"max life {p.statLifeMax}, deaths {bot.Context.Deaths - deathsAtStart}, "
						+ "inventory " + string.Join(", ", Enumerable.Range(0, 50).Where(i => !p.inventory[i].IsAir).Select(i => $"{p.inventory[i].Name} x{p.inventory[i].stack}")));
				bool finished = s.Mode == "auto"
					? phaseTicks > 2 && (!bot.Brain.Auto || (bot.Brain.Planner.Complete && bot.Brain.Current == null))
					: bot.Brain.Current == null && bot.Brain.Target == null && phaseTicks > (s.Mode == "surface" ? SurfaceAfterTicks + 2 : s.Mode is "pathbench" or "arenasite" ? int.MaxValue : 2);
				if (!finished && s.FinishOnLog != null && phaseTicks % 30 == 0)
					finished = tee.Lines.Any(l => l.Contains(s.FinishOnLog));
				// Switched off by an exception: over now (world_eye run 71 went on for a game day of deaths at the spawn after it).
				if (!finished && !bot.Enabled && phaseTicks > 2) {
					Mod.Logger.Warn($"SELFTEST {s.Name}: the bot was switched off (exception); ending the scenario");
					finished = true;
				}
				if (!finished && phaseTicks < Timeout(s))
					return;
				// Milestones are looked at every 60 ticks, and the run ends the tick its goal is met: once more, or the last one
				// (the boss defeated, and its checkpoint) was never noted. Seen (measured run U17): PASS, no "Eye of Cthulhu
				// defeated" milestone and no checkpoint of it.
				if (s.RealWorld)
					TrackMilestones(p, bot);
				Mod.Logger.Info($"SELFTEST speed {s.Name}: {phaseTicks} ticks in {runClock.Elapsed.TotalSeconds:0} s = {Speed(phaseTicks, runClock)} normal");
				Evaluate(bot, s, finished);
				index++;
				Next(index < queue.Count ? Phase.Setup : Phase.Done);
				if (phase == Phase.Done)
					Finish(bot, null);
				return;
			}
		}
	}

	/// <summary>Spawns the scenario's enemy type at every 'E' marker (net-ID variants via SetDefaults).</summary>
	private void SpawnEnemies(Scenario s)
	{
		foreach (TilePos e in Arena.FindAll(s.Rows, 'E')) {
			TilePos w = arena.ToWorld(e.X, e.Y, s.Rows.Length);
			int baseType = s.EnemyType < 0 ? NPCID.BlueSlime : s.EnemyType;
			int idx = NPC.NewNPC(new Terraria.DataStructures.EntitySource_DebugCommand("TerraBot selftest"), w.X * 16 + 8, (w.Y + 1) * 16, baseType);
			if (idx >= 0 && idx < Main.maxNPCs && s.EnemyType < 0)
				Main.npc[idx].SetDefaults(s.EnemyType);
			spawned.Add(idx);
		}
		spawnFailed = spawned.Any(i => i < 0 || i >= Main.maxNPCs || !Main.npc[i].active);
		Mod.Logger.Info($"SELFTEST spawned {string.Join(", ", spawned.Where(i => i >= 0 && i < Main.maxNPCs).Select(i => $"{Main.npc[i].FullName} #{i}"))}{(spawnFailed ? " (SPAWN FAILED)" : "")}");
	}

	private readonly List<string> milestones = new();
	private readonly HashSet<string> milestoneSeen = new();
	private static readonly int[] OreBroadswords = { ItemID.IronBroadsword, ItemID.LeadBroadsword, ItemID.SilverBroadsword, ItemID.TungstenBroadsword, ItemID.GoldBroadsword, ItemID.PlatinumBroadsword };
	private static readonly int[] OrePickaxes = { ItemID.IronPickaxe, ItemID.LeadPickaxe, ItemID.SilverPickaxe, ItemID.TungstenPickaxe, ItemID.GoldPickaxe, ItemID.PlatinumPickaxe };
	private static readonly int[] OreBows = { ItemID.IronBow, ItemID.LeadBow, ItemID.SilverBow, ItemID.TungstenBow, ItemID.GoldBow, ItemID.PlatinumBow };

	/// <summary>In-game time since the scenario started, as "day N +H h" (a day is 86,400 ticks, an hour 3,600).</summary>
	private static string GameClock(int ticks) => $"day {ticks / 86400 + 1} +{ticks % 86400 / 3600}h{ticks % 3600 / 60:00}";

	/// <summary>
	/// Real-world runs: log each progression milestone the first time it is reached, with the game time and deaths so far,
	/// so that commits can be compared by how far a fixed game-time budget gets (not by watching, nor by wall-clock time,
	/// which the fast mode stretches between 4x and 35x).
	/// </summary>
	/// <summary>The scenario's budget, or for a real-world run the days given with <see cref="DaysFlag"/>.</summary>
	private static int Timeout(Scenario s) =>
		s.RealWorld && Program.LaunchParameters.TryGetValue(DaysFlag, out string d) && double.TryParse(d, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out double days) && days > 0 ? (int)(days * 86400) : s.TimeoutTicks;

	/// <summary>
	/// Save the world and the player as the game does (WorldFile.SaveWorld, Player.SavePlayer), then copy the files into
	/// Checkpoints/&lt;milestone&gt; in the test save folder, named as the originals, for selftest.ps1 -FromCheckpoint.
	/// </summary>
	private void SaveCheckpoint(string milestone)
	{
		try {
			Terraria.IO.WorldFile.SaveWorld();
			Player.SavePlayer(Main.ActivePlayerFileData);
			// The map too (MapHelper.SaveMap: Players/<player>/<world id>.map): the bot sees only what the map has revealed, and
			// without it a run from a checkpoint knew nothing it had explored. Seen (checkpoint run U15cp): "exploring for a
			// DemonAltar (none seen yet)" from U14's arena site, though U14 had made the summon item at the altar.
			Terraria.Map.MapHelper.SaveMap();
			string dir = System.IO.Path.Combine(Main.SavePath, "Checkpoints", milestone.Replace(' ', '_'));
			System.IO.Directory.CreateDirectory(dir);
			foreach (string file in new[] { Main.worldPathName, System.IO.Path.ChangeExtension(Main.worldPathName, ".twld"),
				Main.ActivePlayerFileData.Path, System.IO.Path.ChangeExtension(Main.ActivePlayerFileData.Path, ".tplr") })
				if (System.IO.File.Exists(file))
					System.IO.File.Copy(file, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file)), true);
			string mapDir = System.IO.Path.ChangeExtension(Main.ActivePlayerFileData.Path, null);
			if (System.IO.Directory.Exists(mapDir)) {
				string mapOut = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "map")).FullName;
				foreach (string map in System.IO.Directory.GetFiles(mapDir, "*.map"))
					System.IO.File.Copy(map, System.IO.Path.Combine(mapOut, System.IO.Path.GetFileName(map)), true);
			}
			Mod.Logger.Info($"SELFTEST checkpoint {milestone} saved to {dir}");
		}
		catch (Exception e) {
			Mod.Logger.Warn($"SELFTEST checkpoint {milestone} could not be saved: {e.Message}");
		}
	}

	private void TrackMilestones(Player p, TerraBotPlayer bot)
	{
		var plan = bot.Brain.Planner;
		bool Has(int[] types) => Enumerable.Range(0, 58).Any(i => !p.inventory[i].IsAir && Array.IndexOf(types, p.inventory[i].type) >= 0);
		void Note(string name, bool reached)
		{
			if (!reached || !milestoneSeen.Add(name))
				return;
			milestones.Add($"{name} t{phaseTicks}");
			Mod.Logger.Info($"SELFTEST milestone {name} at t{phaseTicks} ({GameClock(phaseTicks)}), deaths {bot.Context.Deaths - deathsAtStart}");
			if (Program.LaunchParameters.ContainsKey(CheckpointFlag) && Array.IndexOf(CheckpointMilestones, name) >= 0)
				SaveCheckpoint(name);
		}
		Note("work bench", plan.KnownStations.ContainsKey(TileID.WorkBenches));
		Note("furnace", plan.KnownStations.ContainsKey(TileID.Furnaces));
		Note("anvil", plan.KnownStations.ContainsKey(TileID.Anvils));
		Note("house", plan.HouseSite != null);
		Note("ore broadsword", Has(OreBroadswords));
		Note("ore pickaxe", Has(OrePickaxes));
		Note("ore bow", Has(OreBows));
		Note("helmet worn", !p.armor[0].IsAir);
		Note("chest armour worn", !p.armor[1].IsAir);
		Note("leg armour worn", !p.armor[2].IsAir);
		Note("full armour", !p.armor[0].IsAir && !p.armor[1].IsAir && !p.armor[2].IsAir);
		Note("first life crystal", p.statLifeMax > 100);
		Note("max life 200", p.statLifeMax >= 200);
		Note("summon item", Has(new int[] { ItemID.SuspiciousLookingEye }));
		Note("arena site", plan.ArenaSite != null);
		Note("Eye fight", NPC.AnyNPCs(NPCID.EyeofCthulhu));
		Note("Eye of Cthulhu defeated", NPC.downedBoss1);
		// The boss's loot picked up (BotBrain.BossLootRadiusTiles) or none to pick up: the fight and the pickup are over.
		Note("Eye loot collected", NPC.downedBoss1 && bot.Brain.Current is not (Bot.Skills.BossFightSkill or Bot.Skills.CollectDropsSkill));
		// Milestone 10 (docs M10_SPEC.md): the Worm Food path, in the plan's order.
		int Count(int type) => Enumerable.Range(0, 58).Where(i => p.inventory[i].type == type).Sum(i => p.inventory[i].stack);
		bool food = Count(ItemID.WormFood) > 0;
		Note("demon bow", Has(new int[] { ItemID.DemonBow }));
		Note("rotten chunks 15", Count(ItemID.RottenChunk) >= Bot.Brain.ProgressionPlanner.WormFoodChunks || food);
		Note("vile powder 30", Count(ItemID.VilePowder) >= Bot.Brain.ProgressionPlanner.WormFoodPowder || food);
		Note("worm food", food);
		Note("Eater fight", NPC.AnyNPCs(NPCID.EaterofWorldsHead));
		Note("Eater of Worlds defeated", NPC.downedBoss2);
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
		bool[,] after = before == null ? null : arena.SnapshotSolid(s.Rows, s.Width);
		for (int x = 0; after != null && x < s.Width; x++)
			for (int y = 0; y < s.Rows.Length; y++) {
				if (before[x, y] && !after[x, y]) result.Broken++;
				if (!before[x, y] && after[x, y]) result.Placed++;
			}
		result.Ticks = phaseTicks;
		result.Outcome = finished ? bot.Brain.LastResult : $"TIMEOUT after {phaseTicks} ticks ({bot.Brain.Describe()})";
		result.Succeeded = finished && (s.Mode == "auto" ? bot.Brain.Planner.Complete : bot.Brain.LastResult.Contains("Succeeded"));
		if (s.Mode == "auto")
			result.Outcome = bot.Brain.Planner.Complete ? "auto: " + bot.Brain.Planner.Status : $"auto stopped: {bot.Brain.LastResult} ({bot.Brain.Planner.Status})";
		bot.GameInfo.Bind(Main.LocalPlayer);
		result.Wood = bot.GameInfo.Count(ItemID.Wood);
		result.Swords = bot.GameInfo.Count(ItemID.WoodenSword);
		result.EyeDefeated = NPC.downedBoss1;
		result.EvilBossDefeated = NPC.downedBoss2;
		result.Milestones = string.Join(", ", milestones);
		result.SwordOnHotbar = Enumerable.Range(0, 10).Any(i => Main.LocalPlayer.inventory[i].type == ItemID.WoodenSword);
		// The hotbar is restocked every 30 ticks, one swap at a time: a pickaxe crafted on the last tick can still be in
		// the backpack, behind another swap.
		for (int i = 0; i < 5 && bot.Blocks.StockHotbar(Main.LocalPlayer) != null; i++) { }
		bot.Blocks.Refresh(Main.LocalPlayer);
		result.PickPower = bot.Blocks.PickPower;
		{
			GameIds ids = bot.Context.Ids;
			TilePos? house = s.Mode == "build" ? buildSite : bot.Brain.Planner.HouseSite;
			Bot.Building.Blueprint bp = Bot.Building.Blueprint.SmallHouse(ids);
			if (house is TilePos h)
				result.Housing = bot.GameInfo.CheckHousing(h.X + bp.Inside.X, h.Y + bp.Inside.Y);
			result.ArmourWorn = new[] { ids.IronTierHelmet, ids.IronTierChainmail, ids.IronTierGreaves }.Count(bot.GameInfo.IsEquipped);
			if (house is TilePos hl) {
				Bot.Building.Blueprint withLantern = Bot.Building.Blueprint.SmallHouse(ids, heartLantern: true);
				result.HeartLanternHung = withLantern.Items.Where(it => it.ItemType == ids.HeartLantern)
					.All(it => Bot.Building.BuildSkill.IsPlaced(bot.Context, withLantern, hl, it));
			}
			if (bot.Brain.Planner.ArenaSite is TilePos a) {
				Bot.Building.Blueprint arenaBp = Bot.Building.Blueprint.Arena(ids);
				result.Arena = Bot.Brain.ProgressionPlanner.ArenaReady(bot.Context, arenaBp, a) ? null : $"arena at {a} not finished";
			}
		}
		result.BenchPlaced = bot.GameInfo.ScanStations(bot.FeetTile()).ContainsKey(TileID.WorkBenches) || bot.Brain.Planner.KnownStations.ContainsKey(TileID.WorkBenches);
		result.FallDamageHits = bot.FallDamageHits;
		result.FallingBlockHits = bot.FallingBlockHits;
		result.EnemiesLeft = spawned.Count(i => i >= 0 && i < Main.maxNPCs && Main.npc[i].active && Main.npc[i].life > 0);
		result.LifeLost = Math.Max(0, lifeAtStart - Main.LocalPlayer.statLife);
		result.Hits = bot.HurtCount;
		result.DamageTaken = bot.DamageTaken;
		result.Deaths = bot.Context.Deaths - deathsAtStart;
		result.Log = tee.Lines;
		bot.SetEnabled(false, "selftest evaluate");
		bot.Context.Log = originalLog;

		string reason = null;
		if (spawnFailed)
			reason = "fixture could not spawn the enemies";
		else if (!finished)
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
			$"fall hits {result.FallDamageHits}, falling blocks {result.FallingBlocksSeen}, life lost {result.LifeLost}, hits {result.Hits} ({result.DamageTaken} damage), deaths {result.Deaths}, enemies left {result.EnemiesLeft}, " +
			$"wood {result.Wood}, swords {result.Swords}, bench {result.BenchPlaced}" + (s.RealWorld ? $", milestones [{result.Milestones}]" : "");
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
		if (SelfTestSystem.Enabled && !SelfTestSystem.SpawnsOn)
			maxSpawns = 0;
	}
}
