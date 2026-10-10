using System.Text;

namespace TerraBot.Core.Physics;

/// <summary>Which hotbar item the bot wants held. The game adapter maps it to a slot (best pickaxe, a cheap block).</summary>
public enum ToolRequest : byte
{
	/// <summary>Leave the held item (and the mouse button) to the player.</summary>
	None,
	Pickaxe,
	Block,
	/// <summary>Best melee weapon in the hotbar.</summary>
	Weapon,
	/// <summary>Best axe in the hotbar.</summary>
	Axe,
	/// <summary>Best bow (or other arrow-firing weapon) in the hotbar; fires carried arrows.</summary>
	Ranged,
	/// <summary>A specific item (<see cref="MoveInput.ToolItem"/>), e.g. a work bench to place.</summary>
	Item,
}

/// <summary>
/// The controls the bot presses for one tick. Mapped onto Player.control* in SetControls; the aim is applied to
/// Player.tileTargetX/Y in PreItemCheck (the equivalent of pointing the mouse at a tile).
/// </summary>
public struct MoveInput
{
	public bool Left;
	public bool Right;
	public bool Jump;
	public bool Up;
	public bool Down;

	/// <summary>Item to hold. With <see cref="ToolRequest.None"/> the bot does not touch item use at all.</summary>
	public ToolRequest Tool;
	/// <summary>Item type for <see cref="ToolRequest.Item"/>.</summary>
	public int ToolItem;
	/// <summary>Hold the use button (only honoured once <see cref="Tool"/> is the selected item).</summary>
	public bool UseItem;
	public bool HasAim;
	public int AimX;
	public int AimY;
	/// <summary>Point the (virtual) mouse at this world position during item use (ranged aim, facing tools).</summary>
	public bool HasMouseAim;
	public float MouseX;
	public float MouseY;
	/// <summary>Press quick-heal (vanilla H): drink the best healing potion.</summary>
	public bool QuickHeal;

	public static MoveInput None => default;

	/// <summary>-1 = left, 0 = none, +1 = right.</summary>
	public int Horizontal
	{
		readonly get => Right && !Left ? 1 : Left && !Right ? -1 : 0;
		set {
			Left = value < 0;
			Right = value > 0;
		}
	}

	public void Aim(int x, int y)
	{
		HasAim = true;
		AimX = x;
		AimY = y;
	}

	public void AimMouse(float worldX, float worldY)
	{
		HasMouseAim = true;
		MouseX = worldX;
		MouseY = worldY;
	}

	public override readonly string ToString()
	{
		var sb = new StringBuilder();
		if (Left) sb.Append('L');
		if (Right) sb.Append('R');
		if (Up) sb.Append('U');
		if (Down) sb.Append('D');
		if (Jump) sb.Append('J');
		if (QuickHeal) sb.Append(" heal");
		if (Tool != ToolRequest.None) {
			sb.Append(' ').Append(Tool.ToString().ToLowerInvariant());
			if (UseItem) sb.Append(" use");
			if (HasAim) sb.Append($"@({AimX},{AimY})");
		}
		return sb.Length == 0 ? "-" : sb.ToString().TrimStart();
	}
}
