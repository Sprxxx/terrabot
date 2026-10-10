using System.Text;

namespace TerraBot.Core.Physics;

/// <summary>Which hotbar item the bot wants held. The game adapter maps it to a slot (best pickaxe, a cheap block).</summary>
public enum ToolRequest : byte
{
	/// <summary>Leave the held item (and the mouse button) to the player.</summary>
	None,
	Pickaxe,
	Block,
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
	/// <summary>Hold the use button (only honoured once <see cref="Tool"/> is the selected item).</summary>
	public bool UseItem;
	public bool HasAim;
	public int AimX;
	public int AimY;

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

	public override readonly string ToString()
	{
		var sb = new StringBuilder();
		if (Left) sb.Append('L');
		if (Right) sb.Append('R');
		if (Up) sb.Append('U');
		if (Down) sb.Append('D');
		if (Jump) sb.Append('J');
		if (Tool != ToolRequest.None) {
			sb.Append(Tool == ToolRequest.Pickaxe ? " pick" : " block");
			if (UseItem) sb.Append(" use");
			if (HasAim) sb.Append($"@({AimX},{AimY})");
		}
		return sb.Length == 0 ? "-" : sb.ToString().TrimStart();
	}
}
