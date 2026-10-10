using System;
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

	/// <summary>Hits taken (any cause) and fall-damage hits, for the self-test harness. Reset by the harness.</summary>
	public int HurtCount { get; set; }
	public int FallDamageHits { get; set; }
	public int FallingBlockHits { get; set; }

	/// <summary>Tile the bot points at this tick; applied to Player.tileTargetX/Y in PreItemCheck.</summary>
	private TilePos? aim;

	private bool IsLocal => Player.whoAmI == Main.myPlayer && Main.netMode != NetmodeID.Server;

	public override void Initialize()
	{
		Context = new BotContext {
			Grid = TerrariaTileGrid.Instance,
			Blocks = Blocks,
			Log = new ModLog(Mod.Logger),
		};
	}

	public override void OnEnterWorld()
	{
		Enabled = false;
		Brain.Clear(Context);
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
		if (src.SourceOtherIndex == 0 && src.SourceNPCIndex < 0 && src.SourceProjectileType <= 0)
			FallDamageHits++;
		if (SelfTest.SelfTestSystem.IsFallingBlockProjectile(src.SourceProjectileType))
			FallingBlockHits++;
		Mod.Logger.Info($"Hurt {info.Damage} ({cause}), knockback {info.Knockback:0.#} dir {info.HitDirection} at {DescribePosition()}; bot {(Enabled ? "ON" : "OFF")}: {Brain.Describe()}");
	}

	public override void SetControls()
	{
		aim = null;
		if (!IsLocal)
			return;
		Blocks.Refresh(Player);
		if (!Enabled)
			return;
		try {
			RefreshContext();
			Brain.Tick(Context);
			Apply(Context.Output);
		}
		catch (Exception e) {
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
		if (IsLocal && Enabled && aim is TilePos a) {
			Player.tileTargetX = a.X;
			Player.tileTargetY = a.Y;
		}
		return true;
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
		Context.Trace = TraceTicksLeft > 0;
		if (TraceTicksLeft > 0 && --TraceTicksLeft == 0)
			Mod.Logger.Info("Trace finished");
	}

	private void Apply(in MoveInput o)
	{
		Player.controlLeft = o.Left;
		Player.controlRight = o.Right;
		Player.controlUp = o.Up;
		Player.controlDown = o.Down;
		Player.controlJump = o.Jump;
		// Without a tool request the mouse button stays the player's (handy for building obstacles to test
		// re-planning). With one, the bot owns item use: switch hotbar slot when no swing is in progress, then
		// hold use. Non-autoReuse items need the button released between swings (vanilla releaseUseItem).
		if (o.Tool != ToolRequest.None) {
			int slot = o.Tool == ToolRequest.Pickaxe ? Blocks.PickSlot : Blocks.BlockSlot;
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
		}
		LastOutput = o;
	}
}
