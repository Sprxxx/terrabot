using Terraria.GameInput;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>Keybinds (rebind in Settings > Controls > Keybindings > Mod Controls). Read them in ProcessTriggers.</summary>
public sealed class BotKeybinds : ModSystem
{
	public static ModKeybind Toggle { get; private set; }
	public static ModKeybind SetTarget { get; private set; }

	public override void Load()
	{
		Toggle = KeybindLoader.RegisterKeybind(Mod, "ToggleBot", "P");
		SetTarget = KeybindLoader.RegisterKeybind(Mod, "SetTarget", "O");
	}

	public override void Unload()
	{
		Toggle = null;
		SetTarget = null;
	}

	/// <summary>Display name of the first key bound to <paramref name="bind"/>, for the HUD.</summary>
	public static string KeyName(ModKeybind bind)
	{
		var keys = bind?.GetAssignedKeys(InputMode.Keyboard);
		return keys == null || keys.Count == 0 ? "unbound" : keys[0];
	}
}
