using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// Glue between the game and the bot. The bot acts ONLY by overwriting Player.control* in SetControls, and only
/// while enabled; when disabled nothing is overwritten, so the player's own input applies on the same tick.
/// </summary>
public sealed class TerraBotPlayer : ModPlayer
{
	/// <summary>Default length of a /bot trace session, in ticks (60 = 1 s).</summary>
	public const int DefaultTraceTicks = 60 * 30;

	public bool Enabled { get; private set; }
	public BotBrain Brain { get; } = new();
	public BotContext Context { get; private set; }
	public PhysicsParams LivePhysics { get; private set; } = PhysicsParams.Default;
	public MoveInput LastOutput { get; private set; }
	public int TraceTicksLeft { get; set; }
	/// <summary>Hotbar-derived mining/placing capabilities, refreshed every tick.</summary>
	public TerrariaBlockActions Blocks { get; } = new();
	/// <summary>World queries, inventory counts and crafting for the skills.</summary>
	public TerrariaGameInfo GameInfo { get; } = new();
	/// <summary>Journey Mode's research and duplication (on only for a Journey character in a Journey world).</summary>
	public TerrariaJourney Journey { get; } = new();

	/// <summary>Hits taken (any cause) and fall-damage hits, for the self-test harness. Reset by the harness.</summary>
	public int HurtCount { get; set; }
	public int DamageTaken { get; set; }
	public int FallDamageHits { get; set; }
	public int FallingBlockHits { get; set; }

	/// <summary>Tile the bot points at this tick; applied to Player.tileTargetX/Y in PreItemCheck.</summary>
	private TilePos? aim;
	private TilePos? placeWatchCell;
	private int placeWatchTicks;
	/// <summary>World point the virtual mouse points at during this tick's ItemCheck.</summary>
	private Vector2? mouseAim;
	private (int, int)? savedMouse;
	/// <summary>How often (ticks) the bot moves better tools from the inventory onto the hotbar.</summary>
	public const int StockHotbarInterval = 30;
	private int stockTicks;
	/// <summary>
	/// Ticks the boss fight's ask for the best arrows (MoveInput.BestAmmo) still holds after it was last made: a tick whose
	/// output another layer replaced (a fall caught) does not send the unholy arrows back to the bag's end for one tick.
	/// </summary>
	public const int BestAmmoHold = 120;
	private int bestAmmoTicks;
	/// <summary>Scan trees and stations on the next tick instead of waiting for the 20-tick cycle.</summary>
	private bool rescanNow;

	private bool IsLocal => Player.whoAmI == Main.myPlayer && Main.netMode != NetmodeID.Server;

	public override void Initialize()
	{
		Context = new BotContext {
			Grid = CachedTileGrid.Instance,
			Blocks = Blocks,
			World = GameInfo,
			Inventory = GameInfo,
			Crafter = GameInfo,
			Knowledge = GameInfo,
			Journey = Journey,
			Ids = TerrariaGameInfo.CreateIds(),
			Log = new ModLog(Mod.Logger),
		};
	}

	/// <summary>
	/// Journey, the research menu open: a shift-click on an item already researched trashes it, as Upgraded Research did for the
	/// user (TerraBotConfig.ShiftClickTrashResearched); in the research slot it could only sit. Vanilla shows the shift-click
	/// cursor (9) on every bag item while the research menu shows (ItemSlot.OverrideHover, context 0). The duplication menu's
	/// shift-click (context 29, the bot's TerrariaJourney.Duplicate) is left alone.
	/// </summary>
	public override bool ShiftClickSlot(Item[] inventory, int context, int slot)
	{
		if (context != Terraria.UI.ItemSlot.Context.InventoryItem || !ResearchFeatures.Config.ShiftClickTrashResearched || !ResearchFeatures.JourneyLocal(Player)
			|| !Main.CreativeMenu.IsShowingResearchMenu() || Main.cursorOverride != 9 || inventory[slot].IsAir || inventory[slot].favorited
			|| !ResearchFeatures.Researched(inventory[slot].type))
			return false;
		Player.trashItem = inventory[slot].Clone();
		inventory[slot].TurnToAir();
		Terraria.Audio.SoundEngine.PlaySound(SoundID.Grab);
		Recipe.FindRecipes();
		return true;
	}

	/// <summary>
	/// The errand that owns the bot is saved with the player (docs ERRAND_DESIGN.md section 3: persistence), as strings (Bot knows no
	/// game types): a run resumed from a checkpoint goes on with the same shaft and frontier instead of recomputing them.
	/// </summary>
	public override void SaveData(Terraria.ModLoader.IO.TagCompound tag)
	{
		if (Brain.Errand is not Bot.Errands.Errand e)
			return;
		var lines = new List<string>();
		foreach (KeyValuePair<string, string> kv in e.Save())
			lines.Add(kv.Key + "=" + kv.Value);
		tag["errandId"] = e.Id;
		tag["errand"] = lines;
	}

	public override void LoadData(Terraria.ModLoader.IO.TagCompound tag)
	{
		if (!tag.ContainsKey("errandId"))
			return;
		string id = tag.GetString("errandId");
		var state = new Dictionary<string, string>();
		foreach (string line in tag.GetList<string>("errand")) {
			int i = line.IndexOf('=');
			if (i > 0)
				state[line.Substring(0, i)] = line.Substring(i + 1);
		}
		Bot.Errands.Errand e = Bot.Errands.ErrandFactory.Create(id, state, Context?.Ids ?? TerrariaGameInfo.CreateIds());
		if (e == null) {
			Mod.Logger.Warn($"Errand '{id}' in the player's save is unknown; dropped");
			return;
		}
		Brain.RestoreErrand(e);
		Mod.Logger.Info($"Errand {id} loaded from the player's save: {e.Describe()} (taken up again by /bot auto under its plan)");
	}

	public override void OnEnterWorld()
	{
		Enabled = false;
		Brain.Clear(Context);
		Context.Ids = TerrariaGameInfo.CreateIds(); // the iron tier differs per world
		CachedTileGrid.Instance.Clear();
		LastOutput = MoveInput.None;
		Mod.Logger.Info($"Entered world '{Main.worldName}' ({Main.maxTilesX}x{Main.maxTilesY}) as '{Player.name}'. Physics: {LivePhysics}");
		Journey.Bind(Player);
		if (Player.whoAmI == Main.myPlayer && Player.difficulty == PlayerDifficultyID.Creative)
			Mod.Logger.Info($"Journey: a Journey character in a {(Main.GameModeInfo.IsJourneyMode ? "Journey" : "non-Journey")} world: research and duplication {(Journey.Enabled ? "on" : "off")}"
				+ $"; Upgraded Research {UpgradedResearchCompat.Describe()}");
	}

	public override void ProcessTriggers(TriggersSet triggersSet)
	{
		if (BotKeybinds.Toggle?.JustPressed == true)
			SetEnabled(!Enabled, "keybind");

		if (BotKeybinds.SetTarget?.JustPressed == true) {
			Point p = Main.MouseWorld.ToTileCoordinates();
			SetTarget(new TilePos(p.X, p.Y), "keybind");
		}
	}

	private bool haveDryPhysics;
	private bool haveOffSandPhysics;

	public override void PostUpdateRunSpeeds()
	{
		if (!IsLocal)
			return;
		// In liquid vanilla swaps gravity, fall and jump for water values. The planner does not model water (it only
		// costs it), and at a water edge the values flipped every few ticks, each flip a re-plan of over a second: the
		// bot stood still for a game hour. Keep the dry values while wet.
		if (Player.wet && haveDryPhysics)
			return;
		// Likewise sand with Dunerider Boots: every run value x1.75 while runningOnSand (Player.cs:25453), a speed the
		// planner's moves do not model per tile. Seen in game (measured run U28): at the desert's sand edges the values flipped
		// every tick, 7,353 "Physics params changed" and as many re-plans, 85,695 walk starts, and the bot stood in place for
		// stretches of 1.5 game hours ("Collect [fetching Copper Coin x84] got 0"). Keep the values off sand while on it.
		bool sandBoost = Player.desertBoots && Player.runningOnSand;
		if (sandBoost && haveOffSandPhysics)
			return;
		PhysicsParams p = PhysicsReader.Read(Player);
		haveDryPhysics = !Player.wet;
		haveOffSandPhysics = !sandBoost;
		if (p != LivePhysics) {
			Mod.Logger.Info($"Physics params changed: {p}");
			LivePhysics = p;
		}
	}

	/// <summary>Logs every hit so test runs show fall damage and knockback (which explains OffPath re-plans).</summary>
	/// <summary>SetControls (and so the bot) does not run while dead: count deaths so skills notice after the respawn.</summary>
	public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
	{
		if (!IsLocal)
			return;
		Context.Deaths++;
		// Enemies, and spots that hurt (thorns and spikes are "other" 3, lava 2, drowning 1); not falls (other 0): the
		// landing spot is not dangerous in itself. Seen in game: two deaths in the same thorn patch.
		if (damageSource.SourceNPCIndex >= 0 || damageSource.SourceProjectileType > 0 || damageSource.SourceOtherIndex > 0)
			Context.NoteDeathByEnemy(FeetTile());
		Mod.Logger.Warn($"Died ({damage:0} damage: {damageSource.GetDeathText(Player.name)}) at {DescribePosition()}; bot {(Enabled ? "ON" : "OFF")}: {Brain.Describe()}");
	}

	public override void OnHurt(Player.HurtInfo info)
	{
		if (!IsLocal)
			return;
		PlayerDeathReason src = info.DamageSource;
		string cause =
			src.SourceNPCIndex >= 0 && src.SourceNPCIndex < Main.maxNPCs ? $"NPC {Main.npc[src.SourceNPCIndex].FullName}"
			: src.SourceProjectileType > 0 ? $"projectile type {src.SourceProjectileType}"
			: src.SourceOtherIndex == 0 ? "fall damage"
			: $"other {src.SourceOtherIndex}";
		if (src.SourceOtherIndex == 3) {
			// Touch damage: which tile, as the game finds it (Player.GetHurtTile).
			Collision.HurtTile ht = Collision.HurtTiles(Player.position, Player.width, Player.height, Player);
			if (ht.type >= 0)
				cause += $", tile {ht.type} {(TileID.Search.TryGetName(ht.type, out string n) ? n : "?")} at ({ht.x},{ht.y})";
		}
		HurtCount++;
		DamageTaken += info.Damage;
		Context.NoteHit(info.Damage, byEnemy: src.SourceNPCIndex >= 0 && src.SourceNPCIndex < Main.maxNPCs || src.SourceProjectileType > 0);
		if (src.SourceNPCIndex >= 0 && src.SourceNPCIndex < Main.maxNPCs)
			Context.NoteAttacker(src.SourceNPCIndex);
		if (src.SourceOtherIndex == 0 && src.SourceNPCIndex < 0 && src.SourceProjectileType <= 0)
			FallDamageHits++;
		if (SelfTest.SelfTestSystem.IsFallingBlockProjectile(src.SourceProjectileType))
			FallingBlockHits++;
		Mod.Logger.Info($"Hurt {info.Damage} ({cause}), knockback {info.Knockback:0.#} dir {info.HitDirection} at {DescribePosition()}; bot {(Enabled ? "ON" : "OFF")}: {Brain.Describe()}");
	}

	/// <summary>Real time spent in the bot's own tick (context refresh, brain, skills); the self-test heartbeat reads and resets it.</summary>
	public static readonly System.Diagnostics.Stopwatch BotTime = new();

	public override void SetControls()
	{
		aim = null;
		mouseAim = null;
		if (!IsLocal)
			return;
		Blocks.Refresh(Player);
		if (!Enabled)
			return;
		try {
			// Due every 30 ticks, but only possible between swings: when blocked, try again next tick rather than in
			// 30 (while chopping, the 30th tick kept landing mid-swing and a crafted sword stayed in the backpack).
			if (++stockTicks >= StockHotbarInterval && Player.itemAnimation == 0 && Main.mouseItem.IsAir) {
				stockTicks = 0;
				string moved = Blocks.StockHotbar(Player);
				if (moved != null)
					Context.Log.Info(moved);
			}
			string ammo = Blocks.OrderAmmo(Player, best: bestAmmoTicks > 0);
			if (ammo != null)
				Context.Log.Info(ammo);
			BotTime.Start();
			RefreshContext();
			Brain.Tick(Context);
			BotTime.Stop();
			Apply(Context.Output);
		}
		catch (Exception e) {
			BotTime.Stop();
			Mod.Logger.Error("Bot tick failed; disabling bot", e);
			Main.NewText("TerraBot crashed and was disabled. See client.log.", Color.OrangeRed);
			SetEnabled(false, "exception");
		}
	}

	/// <summary>
	/// Runs inside Player.ItemCheck, after vanilla has set tileTargetX/Y from the mouse (and smart cursor).
	/// Overriding them here is the bot's way of pointing the mouse at a tile; range, tool power and use speed
	/// are still enforced by vanilla.
	/// </summary>
	public override bool PreItemCheck()
	{
		if (!IsLocal || !Enabled)
			return true;
		if (aim is TilePos a) {
			Player.tileTargetX = a.X;
			Player.tileTargetY = a.Y;
			// Evidence for an axe that does nothing (world_wof E16 and E18, 2026-10-17: ten game hours at an ash tree, "could not mine
			// (3967,1037)"): what vanilla sees of the swing, once a second while a skill asks for the axe.
			if (LastOutput.Tool == ToolRequest.Axe && Main.GameUpdateCount % 60 == 0) {
				Item held = Player.inventory[Player.selectedItem];
				Tile t = Main.tile[a.X, a.Y];
				Context.Log.Info($"Axe swing: holding {held.Name} (slot {Player.selectedItem + 1}, axe {held.axe}, pick {held.pick}), anim {Player.itemAnimation}, itemTime {Player.itemTime}, "
					+ $"toolTime {Player.toolTime}, use {Player.controlUseItem}, target {a}: tile {(t.HasTile ? t.TileType : -1)} frame ({t.TileFrameX},{t.TileFrameY}), "
					+ $"in range {Player.IsTargetTileInItemRange(held)}, canKill {WorldGen.CanKillTile(a.X, a.Y)}, tileAxe {(t.HasTile && Main.tileAxe[t.TileType])}, noBuilding {Player.noBuilding}");
			}
		}
		// Evidence for a block that does not go in (world_wof E9 and E22, 2026-10-17: "placing at (833,1042) did nothing in 90 ticks:
		// the cell reads Empty", nothing the world knows of refusing it): what vanilla sees, once a second while the same cell is
		// aimed at with a block and nothing lands.
		if (aim is TilePos pa && LastOutput.Tool == ToolRequest.Block && LastOutput.UseItem) {
			if (pa == placeWatchCell)
				placeWatchTicks++;
			else {
				placeWatchCell = pa;
				placeWatchTicks = 0;
			}
			if (placeWatchTicks > 0 && placeWatchTicks % 60 == 0) {
				Item held = Player.inventory[Player.selectedItem];
				Tile t = Main.tile[pa.X, pa.Y];
				bool inRange = Player.position.X / 16f - Player.tileRangeX - held.tileBoost - Player.blockRange <= pa.X
					&& (Player.position.X + Player.width) / 16f + Player.tileRangeX + held.tileBoost - 1f + Player.blockRange >= pa.X
					&& Player.position.Y / 16f - Player.tileRangeY - held.tileBoost - Player.blockRange <= pa.Y
					&& (Player.position.Y + Player.height) / 16f + Player.tileRangeY + held.tileBoost - 2f + Player.blockRange >= pa.Y;
				string Near(int x, int y) { Tile n = Main.tile[x, y]; return n.HasTile ? $"t{n.TileType}" : n.WallType > WallID.None ? $"w{n.WallType}" : "-"; }
				Context.Log.Info($"Block swing: holding {held.Name} (slot {Player.selectedItem + 1}, createTile {held.createTile}, stack {held.stack}), anim {Player.itemAnimation}, "
					+ $"itemTime {Player.itemTime}, use {Player.controlUseItem}, cursor {(Main.mouseItem.IsAir ? "empty" : Main.mouseItem.Name)}, target {pa}: tile {(t.HasTile ? t.TileType : -1)} "
					+ $"liquid {t.LiquidAmount}/{t.LiquidType}, wall {t.WallType}, around L {Near(pa.X - 1, pa.Y)} R {Near(pa.X + 1, pa.Y)} U {Near(pa.X, pa.Y - 1)} D {Near(pa.X, pa.Y + 1)}, "
					+ $"in range {inRange}, empty of bodies {Collision.EmptyTile(pa.X, pa.Y, true)}, noBuilding {Player.noBuilding}, us ({Player.position.X:0},{Player.position.Y:0})");
			}
		}
		// Virtual mouse: vanilla reads Main.mouseX/Y (+ screenPosition = MouseWorld) for weapon aim and facing.
		// Point it at the bot's aim for the duration of ItemCheck only; PostItemCheck puts the real cursor back.
		if (mouseAim is Vector2 m) {
			savedMouse = (Main.mouseX, Main.mouseY);
			Main.mouseX = (int)(m.X - Main.screenPosition.X);
			Main.mouseY = (int)(m.Y - Main.screenPosition.Y);
		}
		return true;
	}

	public override void PostItemCheck()
	{
		if (savedMouse is (int x, int y)) {
			Main.mouseX = x;
			Main.mouseY = y;
			savedMouse = null;
		}
	}

	public void SetDig(bool on)
	{
		Context.AllowBreak = on;
		Mod.Logger.Info($"Digging {(on ? "allowed" : "disabled")}; {Blocks.Describe()}");
	}

	public void SetBuild(bool on)
	{
		Context.AllowPlace = on;
		Mod.Logger.Info($"Building {(on ? "allowed" : "disabled")}; {Blocks.Describe()}");
	}

	public void SetEnabled(bool on, string source)
	{
		if (on == Enabled)
			return;
		Enabled = on;
		stockTicks = StockHotbarInterval; // tidy the hotbar on the first tick, before any skill reads the tools
		rescanNow = true; // the world may have changed while the bot was off: don't plan from an old scan
		if (!on) {
			Brain.Interrupt(Context); // keeps the target; re-enabling plans afresh
			LastOutput = MoveInput.None;
		}
		Mod.Logger.Info($"Bot {(on ? "ENABLED" : "DISABLED")} ({source}) at {DescribePosition()}; {Brain.Describe()}");
		Main.NewText($"TerraBot {(on ? "ON" : "OFF")}", on ? Color.LightGreen : Color.LightGray);
	}

	public void SetTarget(TilePos target, string source)
	{
		Brain.SetTarget(target, Context);
		Mod.Logger.Info($"Target set to {target} ({source}); tile there: {TerrariaTileGrid.Instance.Get(target.X, target.Y)}; player at {DescribePosition()}");
		Main.NewText($"TerraBot target: {target.X}, {target.Y}{(Enabled ? "" : " (bot is OFF)")}", Color.Yellow);
	}

	public void StopAll(string source)
	{
		rescanNow = true;
		Brain.Clear(Context);
		LastOutput = MoveInput.None;
		Mod.Logger.Info($"Stopped ({source})");
	}

	/// <summary>The tile under the player's centre column, on the feet row.</summary>
	public TilePos FeetTile()
	{
		TilePos node = Core.Pathfinding.Body.NodeOf(Player.position.X, Player.position.Y, LivePhysics);
		return new TilePos((int)(Player.Center.X / 16f), node.Y);
	}

	public string DescribePosition() =>
		$"tile {FeetTile()} px ({Player.position.X:0.#},{Player.position.Y:0.#}) vel ({Player.velocity.X:0.##},{Player.velocity.Y:0.##})";

	/// <summary>
	/// Perceive now, as the next tick the bot is on would (the hotbar's tools and bow, life, the NPCs round it): for a skill started
	/// before the bot is switched on (the self-test's setup). Seen in game (fight_wof, 2026-10-17): the first scenario of a batch
	/// started its fight with "with 0 arrows, life 100/100", the context still the one from before the setup.
	/// </summary>
	public void PerceiveNow()
	{
		if (!IsLocal)
			return;
		Blocks.Refresh(Player);
		RefreshContext();
	}

	private void RefreshContext()
	{
		// On a mount (a minecart picked up off a track, a mount item used): off it again, as a player presses the mount key.
		// Seen in game (measured run H1): "paused: mounted" for hours, every skill waiting, until the watchdog gave the job
		// up, four times. Mount.Dismount is what the mount key and the mount buff's click do.
		if (Enabled && Player.mount.Active) {
			Mod.Logger.Info($"Dismounting (mount type {Player.mount.Type}, holding {Player.HeldItem?.Name ?? "nothing"}) at {FeetTile()}");
			Player.mount.Dismount(Player);
		}
		// Likewise hanging on a rope (walked into one) or held by a grappling hook: let go, as a player does. Not on a climb
		// step's rope (last tick's controls held it: MoveInput.Climb).
		bool climbing = Context.Output.Climb;
		if (Enabled && Player.pulley && !climbing) {
			Mod.Logger.Info($"Letting go of a rope at {FeetTile()}");
			Player.pulley = false;
		}
		if (Enabled && Player.grappling[0] >= 0) {
			Mod.Logger.Info($"Releasing a grappling hook at {FeetTile()}");
			Player.RemoveAllGrapplingHooks();
		}
		Context.Tick = Main.GameUpdateCount;
		CachedTileGrid.Instance.Now = Main.GameUpdateCount;
		Context.Physics = LivePhysics;
		Context.Body = new BodyState {
			X = Player.position.X,
			Y = Player.position.Y,
			VX = Player.velocity.X,
			VY = Player.velocity.Y,
			Jump = Player.jump,
			ReleaseJump = Player.releaseJump,
			OnRope = Player.pulley,
		};
		Context.BodyIssue =
			Player.dead ? "dead"
			: Player.mount.Active ? "mounted"
			: Player.gravDir != 1f ? "gravity reversed"
			: Player.grappling[0] >= 0 ? "grappling"
			: Player.pulley && !climbing ? "on a rope"
			: Player.CCed ? "crowd-controlled"
			: null;
		RefreshPerception();
		Context.Trace = TraceTicksLeft > 0;
		if (TraceTicksLeft > 0 && --TraceTicksLeft == 0)
			Mod.Logger.Info("Trace finished");
	}

	public const float PerceptionRadiusPx = 50 * 16f;
	/// <summary>Bosses roam far between attacks (the Eye of Cthulhu hovers and circles well off screen): keep them in view.</summary>
	public const float BossPerceptionRadiusPx = 200 * 16f;

	/// <summary>
	/// A boss, or a segment of one: the Eater of Worlds' head, bodies and tail are separate NPCs, none with NPC.boss set
	/// (SetDefaults, NPC.cs:4007-4065; only the last one alive gets it as it dies, DropEoWLoot, NPC.cs:84405), so they are
	/// named here. Each segment keeps its width (38 px) behind the one ahead (NPC.cs:55293-55327), so the 67 trail some
	/// 160 tiles behind the head as it dives: kept in view like a boss.
	/// </summary>
	private static bool IsBossPart(NPC n) => n.boss || n.type is NPCID.EaterofWorldsHead or NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail
		// The Wall of Flesh's eyes, The Hungry and the leeches (NPC.cs:5502-5600): none has NPC.boss, and the fight keeps 30-77 tiles
		// ahead of the Wall (docs M12_SPEC.md section 6), beyond the 50 tiles other NPCs are seen from; the eyes are what it shoots.
		|| n.type is NPCID.WallofFleshEye or NPCID.TheHungry or NPCID.TheHungryII or NPCID.LeechHead or NPCID.LeechBody or NPCID.LeechTail
		// The Brain of Cthulhu's Creepers (NPC.cs:7609-7622): no NPC.boss either; they orbit the Brain, which jumps to 12-40 tiles off
		// the player on each axis (NPC.cs:33316-33383), up to 57 tiles away, past the 50.
		|| n.type == NPCID.Creeper;

	/// <summary>
	/// Seen as a hostile: what NPC.CanBeChasedBy takes (NPC.cs:107202-107209), and the Brain of Cthulhu while it hides behind its
	/// Creepers, which that leaves out (dontTakeDamage until they are dead, NPC.cs:7592 and 33157): it still hurts on a touch
	/// (damage 30), and where it is is where they orbit. EntityInfo.DontTakeDamage tells the fight not to shoot it.
	/// </summary>
	/// <summary>
	/// What the bot sees as a hostile: what vanilla lets be chased (NPC.CanBeChasedBy), the Brain while it hides, and the spell NPCs
	/// (NPCAIStyleID.Spell: a Fire Imp's Burning Spheres, the Eater's Vile Spit, Chaos Balls), which vanilla keeps minions off
	/// (not chaseable) but which fly at the player through blocks for 30 damage, 1 life each. Unseen, they killed the bot in a dug-in
	/// shelter under the hellbridge (world_wof E8, 2026-10-17: "Hurt 25 (NPC Burning Sphere)" nine times, "Shelter [Waiting]", dead)
	/// and in two crystal searches ("deboned by Poison Spit", E1 and E2). They are shots too (<see cref="ShotInfo"/>, for the dodge).
	/// </summary>
	private static bool Perceived(NPC n) => n.CanBeChasedBy() || n.type == NPCID.BrainofCthulhu && n.CanBeChasedBy(null, ignoreDontTakeDamage: true) || SpellNpc(n);

	private static bool SpellNpc(NPC n) => n.aiStyle == NPCAIStyleID.Spell && !n.friendly && n.damage > 0;

	/// <summary>
	/// Hostile projectiles are seen within this many px: half a 1920-wide screen (60 tiles), the smallest screen the scene scan
	/// assumes (BotContext.SceneScanHalfWidth). An eye laser at 27 px/tick (NPC.cs:26668-26697, extraUpdates 2) crosses it in 35
	/// ticks, within the dodge's 50-tick look-ahead (Dodge.Horizon).
	/// </summary>
	public const float ShotPerceptionRadiusPx = 60 * 16f;

	// Arrow flight (Projectile.AI_001 for wooden arrows): PENDING verification against the decompile.
	public const int ArrowGravityDelay = 15;
	public const float ArrowGravity = 0.1f;
	public const float ArrowMaxFallSpeed = 16f;

	/// <summary>Hostile NPCs, dropped items and combat state for the skills (nearest first).</summary>
	private void RefreshPerception()
	{
		GameInfo.Bind(Player);
		Journey.Bind(Player);
		Context.AxePower = Blocks.AxePower;
		Context.PickPower = Blocks.PickPower;
		Context.PickRangeBoost = Blocks.PickSlot >= 0 ? Player.inventory[Blocks.PickSlot].tileBoost : 0;
		Context.IsNight = !Main.dayTime;
		Context.NightTicksLeft = Main.dayTime ? 0 : (int)Math.Max(0, Main.nightLength - Main.time);
		Context.Expert = Main.expertMode;
		Context.TrashResearched = ModContent.GetInstance<TerraBotConfig>()?.TrashResearchedItems ?? false;
		Context.CavernY = (int)Main.rockLayer;
		Context.SurfaceY = (int)Main.worldSurface;
		Context.WorldHeight = Main.maxTilesY;
		Context.WorldWidth = Main.maxTilesX;
		Context.UnderworldRow = Main.UnderworldLayer;
		Context.DumpDir ??= System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Logging.LogDir, "TerraBot")).FullName;
		Context.SpawnTile = Player.SpawnX >= 0 && Player.SpawnY >= 0 ? new TilePos(Player.SpawnX, Player.SpawnY - 1) : new TilePos(Main.spawnTileX, Main.spawnTileY - 1);
		if (rescanNow || Main.GameUpdateCount % 20 == 0 || Context.Seen.Trees.Count == 0 && Main.GameUpdateCount % 5 == 0) {
			rescanNow = false;
			TilePos feet = FeetTile();
			Context.Seen = new Surroundings { Trees = GameInfo.ScanTrees(feet), Stations = GameInfo.ScanStations(feet), Origin = feet };
		}
		Vector2 c = Player.Center;
		var hostiles = new List<EntityInfo>();
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (!n.active || !Perceived(n) || Vector2.Distance(n.Center, c) > (IsBossPart(n) ? BossPerceptionRadiusPx : PerceptionRadiusPx))
				continue;
			hostiles.Add(new EntityInfo(i, n.type, n.FullName, n.position.X, n.position.Y, n.width, n.height,
				n.velocity.X, n.velocity.Y, n.life, n.lifeMax, n.damage, n.aiStyle, n.ai[0], n.ai[1], n.ai[2], n.ai[3], n.noTileCollide, n.dontTakeDamage));
		}
		hostiles.Sort((a, b) => a.DistanceTo(c.X, c.Y).CompareTo(b.DistanceTo(c.X, c.Y)));
		Context.Hostiles = hostiles;
		// Town NPCs, drawn with their names: the Old Man at the Dungeon's entrance (docs M11_SPEC.md section 1c).
		var town = new List<EntityInfo>();
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (n.active && n.townNPC && Vector2.Distance(n.Center, c) <= PerceptionRadiusPx)
				town.Add(new EntityInfo(i, n.type, n.FullName, n.position.X, n.position.Y, n.width, n.height,
					n.velocity.X, n.velocity.Y, n.life, n.lifeMax, n.damage, n.aiStyle, n.ai[0], n.ai[1], n.ai[2], n.ai[3], n.noTileCollide));
		}
		town.Sort((a, b) => a.DistanceTo(c.X, c.Y).CompareTo(b.DistanceTo(c.X, c.Y)));
		Context.TownNpcs = town;

		// Hostile projectiles in sight (the Wall of Flesh's lasers), each tick's move as the game makes it: velocity times
		// 1 + extraUpdates (Projectile.Update).
		var shots = new List<ShotInfo>();
		for (int i = 0; i < Main.maxProjectiles; i++) {
			Projectile pr = Main.projectile[i];
			if (!pr.active || !pr.hostile || pr.damage <= 0 || Vector2.Distance(pr.Center, c) > ShotPerceptionRadiusPx)
				continue;
			int moves = 1 + pr.extraUpdates;
			shots.Add(new ShotInfo(i, pr.type, pr.position.X, pr.position.Y, pr.width, pr.height, pr.velocity.X * moves, pr.velocity.Y * moves, pr.damage, pr.tileCollide));
		}
		// Spell NPCs (Burning Spheres, Vile Spit) fly as shots do: in the shots too, for the dodge (ids past the projectiles').
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (n.active && SpellNpc(n) && Vector2.Distance(n.Center, c) <= ShotPerceptionRadiusPx)
				shots.Add(new ShotInfo(Main.maxProjectiles + i, -n.type, n.position.X, n.position.Y, n.width, n.height, n.velocity.X, n.velocity.Y, n.damage, !n.noTileCollide));
		}
		shots.Sort((a, b) => a.DistanceTo(c.X, c.Y).CompareTo(b.DistanceTo(c.X, c.Y)));
		Context.Shots = shots;

		var drops = new List<DropInfo>();
		for (int i = 0; i < Main.maxItems; i++) {
			Item it = Main.item[i];
			if (!it.active || it.IsAir || Vector2.Distance(it.Center, c) > PerceptionRadiusPx)
				continue;
			drops.Add(new DropInfo(i, it.type, it.Name, it.stack, it.position.X, it.position.Y, it.width, it.height));
		}
		drops.Sort((a, b) => Vector2.DistanceSquared(new Vector2(a.CenterX, a.CenterY), c).CompareTo(Vector2.DistanceSquared(new Vector2(b.CenterX, b.CenterY), c)));
		Context.Drops = drops;

		CombatInfo k = Context.Combat;
		k.Life = Player.statLife;
		k.LifeMax = Player.statLifeMax2;
		k.LifeMaxBase = Player.statLifeMax;
		k.Facing = Player.direction;
		k.ItemAnimation = Player.itemAnimation;
		k.HeldItemType = Player.HeldItem?.type ?? 0;
		k.CanQuickHeal = Player.potionDelay == 0 && Player.QuickHeal_GetItemToUse() != null;
		int bestHeal = 0;
		for (int i = 0; i < 58; i++) {
			Item it = Player.inventory[i];
			if (it.stack > 0 && it.type > ItemID.None && it.potion && it.healLife > 0)
				bestHeal = Math.Max(bestHeal, Player.GetHealLife(it, quickHeal: true));
		}
		k.BestHealLife = bestHeal;
		k.PotionSickTicks = Player.potionDelay;
		k.Breath = Player.breath;
		k.BreathMax = Player.breathMax;
		k.Defense = Player.statDefense;
		k.InTown = Player.townNPCs > 0f;
		k.InEvilZone = Player.ZoneCorrupt || Player.ZoneCrimson;
		k.ArmourPieces = (Player.armor[0].IsAir ? 0 : 1) + (Player.armor[1].IsAir ? 0 : 1) + (Player.armor[2].IsAir ? 0 : 1);
		k.WeaponReach = Blocks.WeaponSlot >= 0 ? Blocks.WeaponReach : 0f;
		k.WeaponName = Blocks.WeaponName;
		k.WeaponDamage = Blocks.WeaponSlot >= 0 ? Blocks.WeaponDamage : 0;
		k.HasRanged = Blocks.RangedSlot >= 0 && Blocks.AmmoCount > 0;
		k.RangedName = Blocks.RangedName;
		k.AmmoCount = Blocks.AmmoCount;
		k.Projectile = new ProjectileModel(Blocks.ShotSpeed, ArrowGravityDelay, ArrowGravity, ArrowMaxFallSpeed);
	}

	/// <summary>
	/// Hotbar slot holding this item. If it is elsewhere in the inventory, swap it onto the hotbar first, like
	/// dragging it there (only between swings), into a slot that holds none of the bot's tools.
	/// </summary>
	private int ItemSlotForUse(int itemType)
	{
		GameInfo.Bind(Player);
		if (GameInfo.FindItem(itemType, out int slot) == null)
			return -1;
		if (slot < TerrariaBlockActions.HotbarSlots)
			return slot;
		if (Player.itemAnimation != 0 || !Main.mouseItem.IsAir)
			return -1;
		int to = Blocks.HotbarSlotFor(Player);
		if (to < 0)
			return -1;
		Utils.Swap(ref Player.inventory[slot], ref Player.inventory[to]);
		Context.Log.Info($"Hotbar: moved {Player.inventory[to].Name} from inventory slot {slot + 1} to hotbar slot {to + 1}");
		Blocks.Refresh(Player);
		return to;
	}

	private void Apply(in MoveInput o)
	{
		bestAmmoTicks = o.BestAmmo ? BestAmmoHold : System.Math.Max(0, bestAmmoTicks - 1);
		Player.controlLeft = o.Left;
		Player.controlRight = o.Right;
		Player.controlUp = o.Up;
		Player.controlDown = o.Down;
		Player.controlJump = o.Jump;
		// Vanilla reads controlQuickHeal before PlayerLoader.SetControls runs (Player.Update) and ResetControls clears
		// it next tick, so setting it here never heals. Call what the quick-heal key calls instead (a UI action:
		// same checks, same potion sickness).
		if (o.QuickHeal && Player.potionDelay == 0) {
			int before = Player.statLife;
			Player.QuickHeal();
			Context.Log.Info($"Quick-heal: life {before} -> {Player.statLife}, potion sickness {Player.potionDelay} ticks");
		}
		// Without a tool request the mouse button stays the player's (handy for building obstacles to test
		// re-planning). With one, the bot owns item use: switch hotbar slot when no swing is in progress, then
		// hold use. Non-autoReuse items need the button released between swings (vanilla releaseUseItem).
		if (o.Tool != ToolRequest.None) {
			int slot = o.Tool switch {
				ToolRequest.Pickaxe => Blocks.PickSlot,
				ToolRequest.Block => Blocks.BlockSlot,
				ToolRequest.Weapon => Blocks.WeaponSlot,
				ToolRequest.Axe => Blocks.AxeSlot >= 0 ? Blocks.AxeSlot : AxeFromTheBag(),
				ToolRequest.Ranged => Blocks.RangedSlot,
				ToolRequest.Item => ItemSlotForUse(o.ToolItem),
				_ => -1,
			};
			bool use = false;
			if (slot >= 0) {
				if (Player.selectedItem != slot) {
					if (Player.itemAnimation == 0)
						Player.selectedItem = slot;
				}
				else if (o.UseItem) {
					Item held = Player.inventory[slot];
					use = held.autoReuse || Player.itemAnimation > 0 || Player.releaseUseItem;
				}
			}
			Player.controlUseItem = use;
			Player.controlTorch = false; // smart-select would swap our item
			if (o.HasAim)
				aim = new TilePos(o.AimX, o.AimY);
			if (o.HasMouseAim)
				mouseAim = new Vector2(o.MouseX, o.MouseY);
		}
		// The Throw key, held one tick (docs M12_SPEC.md section 2c): vanilla's dropItemCheck runs after SetControls in this same
		// update (Player.cs:23960, 23983) and drops the selected stack when controlThrow is down and was up the tick before
		// (releaseThrow, set at Player.cs:24277 from this tick's key). Written every tick while the bot drives, so the key is up
		// again the next.
		Player.controlThrow = o.Throw && o.ThrowItem > 0 && HoldOneToThrow(o.ThrowItem);
		LastOutput = o;
	}

	/// <summary>A skill asks for the axe with none on the hotbar: brought from the bag at once (TerrariaBlockActions.BringToHotbar); -1 with none.</summary>
	private int AxeFromTheBag()
	{
		int slot = Blocks.BringToHotbar(Player, "axe", out string moved);
		if (moved != null)
			Context.Log.Info(moved);
		return slot;
	}

	/// <summary>
	/// Make the selected hotbar slot hold exactly one <paramref name="itemType"/>, not favourited, with nothing on the cursor, so
	/// that the Throw key drops that one (Player.DropSelectedItem drops the whole selected stack, Player.cs:5937-5947, and with a
	/// cursor item it drops that instead). A bigger stack is split first: one into an empty slot, as a right-click on the stack
	/// (one onto the cursor) and a click on an empty slot do; then onto the hotbar like dragging it there; a favourite mark is
	/// cleared as an alt-click does (PLAN.md, "Actions that stand in for UI clicks"). Only between swings (dropItemCheck runs only
	/// then, Player.cs:23981). False if it cannot now.
	/// </summary>
	private bool HoldOneToThrow(int itemType)
	{
		if (Player.itemAnimation != 0 || !Player.ItemTimeIsZero || Player.reuseDelay != 0 || !Main.mouseItem.IsAir || Main.drawingPlayerChat || Player.noThrow > 0)
			return false;
		const int slots = TerrariaGameInfo.MainInventorySlots;
		int one = -1, any = -1, empty = -1;
		for (int i = 0; i < slots; i++) {
			Item it = Player.inventory[i];
			if (it.IsAir) {
				if (empty < 0 || empty >= TerrariaBlockActions.HotbarSlots && i < TerrariaBlockActions.HotbarSlots)
					empty = i;
				continue;
			}
			if (it.type != itemType)
				continue;
			if (it.stack == 1 && (one < 0 || one >= TerrariaBlockActions.HotbarSlots && i < TerrariaBlockActions.HotbarSlots))
				one = i;
			if (any < 0)
				any = i;
		}
		if (one < 0) {
			if (any < 0 || empty < 0)
				return false;
			Item stack = Player.inventory[any];
			Item single = stack.Clone();
			single.stack = 1;
			stack.stack--;
			Player.inventory[empty] = single;
			one = empty;
			Context.Log.Info($"Throw: split one {single.Name} off the stack of {stack.stack + 1} in slot {any + 1} into slot {one + 1}");
		}
		if (one >= TerrariaBlockActions.HotbarSlots) {
			int to = Blocks.HotbarSlotFor(Player);
			if (to < 0)
				return false;
			Utils.Swap(ref Player.inventory[one], ref Player.inventory[to]);
			Context.Log.Info($"Hotbar: moved {Player.inventory[to].Name} from inventory slot {one + 1} to hotbar slot {to + 1} to throw it");
			one = to;
			Blocks.Refresh(Player);
		}
		Player.inventory[one].favorited = false;
		Player.selectedItem = one;
		Player.controlUseItem = false;
		return true;
	}
}
