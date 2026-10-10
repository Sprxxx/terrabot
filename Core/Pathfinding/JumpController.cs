using System;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// The one jump controller, used by <see cref="JumpLibrary"/> to simulate templates and by the executor to
/// replay them, so planned and executed trajectories cannot drift apart.
/// Phases (tick 0 = the first tick after aligning at rest on the start node):
/// <list type="number">
/// <item>Run-up (running jumps only): hold the travel direction until the hitbox passes the take-off point,
/// <see cref="JumpTemplate.RunupTiles"/> tiles ahead of the start.</item>
/// <item>Take-off: press jump; hold it <see cref="JumpTemplate.HoldTicks"/> ticks.</item>
/// <item>Flight: after <see cref="JumpTemplate.DelayTicks"/> ticks, arrive-steer onto the destination column.
/// Before that, a standing jump presses nothing and a running jump keeps pressing forward.</item>
/// </list>
/// </summary>
public sealed class JumpController
{
	private readonly JumpTemplate template;
	private readonly float takeoffX;
	private readonly float targetX;
	private readonly int dir;

	public bool TookOff { get; private set; }
	public int TakeoffTick { get; private set; } = -1;

	/// <param name="startX">Canonical hitbox left of the start node (not the actual position).</param>
	/// <param name="targetX">Canonical hitbox left of the destination node.</param>
	public JumpController(JumpTemplate template, float startX, float targetX)
	{
		this.template = template;
		this.targetX = targetX;
		dir = Math.Sign(template.Dx);
		takeoffX = startX + dir * template.RunupTiles * Body.TileSize;
	}

	public MoveInput Next(in BodyState s, int tick, PhysicsParams p)
	{
		var input = new MoveInput();
		if (!TookOff) {
			bool runupDone = template.RunupTiles == 0 || dir * (s.X - takeoffX) >= 0f;
			if (!runupDone) {
				input.Horizontal = dir;
				return input;
			}
			TookOff = true;
			TakeoffTick = tick;
		}
		int k = tick - TakeoffTick;
		input.Jump = k < template.HoldTicks;
		input.Horizontal = k >= template.DelayTicks
			? Arrive.Steer(s.X, s.VX, targetX, p)
			: template.RunupTiles > 0 ? dir : 0;
		return input;
	}
}
