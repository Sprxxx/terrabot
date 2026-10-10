using System.Text;

namespace TerraBot.Core.Physics;

/// <summary>The movement controls the bot presses for one tick. Mapped 1:1 onto Player.control* in SetControls.</summary>
public struct MoveInput
{
	public bool Left;
	public bool Right;
	public bool Jump;
	public bool Up;
	public bool Down;

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

	public override readonly string ToString()
	{
		var sb = new StringBuilder();
		if (Left) sb.Append('L');
		if (Right) sb.Append('R');
		if (Up) sb.Append('U');
		if (Down) sb.Append('D');
		if (Jump) sb.Append('J');
		return sb.Length == 0 ? "-" : sb.ToString();
	}
}
