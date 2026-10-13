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

	/// <summary>Hits taken (any cause) and fall-damage hits, for the self-test harness. Reset by the harness.</summary>
	public int HurtCount { get; set; }
	public int DamageTaken { get; set; }
	public int FallDamageHits { get; set; }
	public int FallingBlockHits { get; set; }

	/// <summary>Tile the bot points at this tick; applied to Player.tileTargetX/Y in PreItemCheck.</summary>
	private TilePos? aim;
	/// <summary>World point the virtual mouse points at during this tick's ItemCheck.</summary>
	private Vector2? mouseAim;
	private (int, int)? savedMouse;
	/// <summary>How often (ticks) the bot moves better tools from the inventory onto the hotbar.</summary>
	public const int StockHotbarInterval = 30;
	private int stockTicks;
	/// <summary>Scan trees and stations on the next tick instead of waiting for the 20-tick cycle.</summary>
	private bool rescanNow;

	private bool IsLocal => Player.whoAmI == Main.myPlayer && Main.netMode != NetmodeID.Server;

	public override void Initialize()
	{
		Context = new BotContext {
			Grid = TerrariaTileGrid.Instance,
			Blocks = Blocks,
			World = GameInfo,
			Inventory = GameInfo,
			Crafter = GameInfo,
			Knowledge = GameInfo,
			Ids = TerrariaGameInfo.CreateIds(),
			Log = new ModLog(Mod.Logger),
		};
	}

	public override void OnEnterWorld()
	{
		Enabled = false;
		Brain.Clear(Context);
		Context.Ids = TerrariaGameInfo.CreateIds(); // the iron tier differs per world
		LastOutput = MoveInput.None;
		Mod.Logger.Info($"Entered world '{Main.worldName}' ({Main.maxTilesX}x{Main.maxTilesY}) as '{Player.name}'. Physics: {LivePhysics}");
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

	public override void PostUpdateRunSpeeds()
	{
		if (!IsLocal)
			return;
		PhysicsParams p = PhysicsReader.Read(Player);
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
		HurtCount++;
		DamageTaken += info.Damage;
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

	private void RefreshContext()
	{
		Context.Tick = Main.GameUpdateCount;
		Context.Physics = LivePhysics;
		Context.Body = new BodyState {
			X = Player.position.X,
			Y = Player.position.Y,
			VX = Player.velocity.X,
			VY = Player.velocity.Y,
			Jump = Player.jump,
			ReleaseJump = Player.releaseJump,
		};
		Context.BodyIssue =
			Player.dead ? "dead"
			: Player.mount.Active ? "mounted"
			: Player.gravDir != 1f ? "gravity reversed"
			: Player.grappling[0] >= 0 ? "grappling"
			: Player.pulley ? "on a rope"
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
	// Arrow flight (Projectile.AI_001 for wooden arrows): PENDING verification against the decompile.
	public const int ArrowGravityDelay = 15;
	public const float ArrowGravity = 0.1f;
	public const float ArrowMaxFallSpeed = 16f;

	/// <summary>Hostile NPCs, dropped items and combat state for the skills (nearest first).</summary>
	private void RefreshPerception()
	{
		GameInfo.Bind(Player);
		Context.AxePower = Blocks.AxePower;
		Context.PickPower = Blocks.PickPower;
		Context.PickRangeBoost = Blocks.PickSlot >= 0 ? Player.inventory[Blocks.PickSlot].tileBoost : 0;
		Context.IsNight = !Main.dayTime;
		Context.NightTicksLeft = Main.dayTime ? 0 : (int)Math.Max(0, Main.nightLength - Main.time);
		Context.Expert = Main.expertMode;
		if (rescanNow || Main.GameUpdateCount % 20 == 0 || Context.Seen.Trees.Count == 0 && Main.GameUpdateCount % 5 == 0) {
			rescanNow = false;
			TilePos feet = FeetTile();
			Context.Seen = new Surroundings { Trees = GameInfo.ScanTrees(feet), Stations = GameInfo.ScanStations(feet) };
		}
		Vector2 c = Player.Center;
		var hostiles = new List<EntityInfo>();
		for (int i = 0; i < Main.maxNPCs; i++) {
			NPC n = Main.npc[i];
			if (!n.active || !n.CanBeChasedBy() || Vector2.Distance(n.Center, c) > (n.boss ? BossPerceptionRadiusPx : PerceptionRadiusPx))
				continue;
			hostiles.Add(new EntityInfo(i, n.type, n.FullName, n.position.X, n.position.Y, n.width, n.height,
				n.velocity.X, n.velocity.Y, n.life, n.lifeMax, n.damage, n.aiStyle, n.ai[0], n.ai[1], n.ai[2], n.ai[3]));
		}
		hostiles.Sort((a, b) => a.DistanceTo(c.X, c.Y).CompareTo(b.DistanceTo(c.X, c.Y)));
		Context.Hostiles = hostiles;

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
		k.CanQuickHeal = Player.potionDelay == 0 && Player.QuickHeal_GetItemToUse() != null;
		k.Breath = Player.breath;
		k.BreathMax = Player.breathMax;
		k.WeaponReach = Blocks.WeaponSlot >= 0 ? Blocks.WeaponReach : 0f;
		k.WeaponName = Blocks.WeaponName;
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
				ToolRequest.Axe => Blocks.AxeSlot,
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
		LastOutput = o;
	}
}
