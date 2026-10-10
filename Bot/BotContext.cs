using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot;

/// <summary>Logging sink, so Bot/ and Core/ stay free of tModLoader types. In game it wraps Mod.Logger.</summary>
public interface IBotLog
{
	void Info(string message);
	void Warn(string message);
	void Error(string message, Exception ex = null);
	void Debug(string message);
}

public sealed class NullLog : IBotLog
{
	public static readonly NullLog Instance = new();
	public void Info(string message) { }
	public void Warn(string message) { }
	public void Error(string message, Exception ex = null) { }
	public void Debug(string message) { }
}

/// <summary>
/// Per-tick inputs (perception) and output (controls) shared by the brain and skills. Refreshed by the game
/// adapter right before the brain ticks.
/// </summary>
public sealed class BotContext
{
	private MoveContext moves;

	public ITileGrid Grid { get; set; }
	public PhysicsParams Physics { get; set; } = PhysicsParams.Default;
	public BodyState Body { get; set; }
	/// <summary>Non-null when the bot must not act this tick (dead, mounted, grappled, reversed gravity...).</summary>
	public string BodyIssue { get; set; }
	public long Tick { get; set; }
	public IBotLog Log { get; set; } = NullLog.Instance;
	/// <summary>When true, skills log a line per tick (pos, vel, step, input).</summary>
	public bool Trace { get; set; }

	/// <summary>Controls to press this tick. Reset to None by the brain before each tick.</summary>
	public MoveInput Output;

	/// <summary>Move context for the current grid and physics; rebuilt (with jump templates) when physics change.</summary>
	public MoveContext Moves
	{
		get {
			if (moves == null || !ReferenceEquals(moves.Grid, Grid) || moves.Physics != Physics)
				moves = new MoveContext(Grid, Physics);
			return moves;
		}
	}

	public TilePos Node => Core.Pathfinding.Body.NodeOf(Body, Physics);
}
