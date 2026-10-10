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

	private bool IsLocal => Player.whoAmI == Main.myPlayer && Main.netMode != NetmodeID.Server;

	public override void Initialize()
	{
		Context = new BotContext {
			Grid = TerrariaTileGrid.Instance,
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
		Mod.Logger.Info($"Hurt {info.Damage} ({cause}), knockback {info.Knockback:0.#} dir {info.HitDirection} at {DescribePosition()}; bot {(Enabled ? "ON" : "OFF")}: {Brain.Describe()}");
	}

	public override void SetControls()
	{
		if (!IsLocal || !Enabled)
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
		// Milestone 1 never uses items, so the mouse stays with the player (handy for building obstacles in
		// front of the bot to test re-planning). Once skills use items, set controlUseItem/selectedItem here too.
		LastOutput = o;
	}
}
