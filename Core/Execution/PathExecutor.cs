using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Pathfinding.Moves;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Execution;

public enum ExecStatus
{
	Running,
	/// <summary>Every step completed.</summary>
	Finished,
	/// <summary>A step took far longer than planned (no progress).</summary>
	Stuck,
	/// <summary>Landed/standing somewhere the current step does not explain.</summary>
	OffPath,
	/// <summary>An upcoming step is no longer valid in the current world.</summary>
	Blocked,
}

/// <summary>
/// Follows a planned path one step at a time, producing the controls for the current tick from the live
/// <see cref="BodyState"/>. Pure logic: the same class runs in game and in the closed-loop test simulator.
/// Call <see cref="Tick"/> exactly once per game tick, before the game applies physics.
/// </summary>
public sealed class PathExecutor
{
	private enum JumpPhase { Align, Air }

	/// <summary>Re-validate upcoming steps every this many ticks.</summary>
	public const int RevalidateInterval = 15;
	/// <summary>How many upcoming steps are re-validated.</summary>
	public const int RevalidateLookahead = 8;
	/// <summary>Pressing a direction for this many ticks without the body moving means we are stuck.</summary>
	public const int NoMovementTicks = 45;
	/// <summary>How far ahead an unexpected landing node is looked up before declaring OffPath.</summary>
	public const int ResyncLookahead = 12;

	private readonly MoveContext ctx;
	private readonly List<PathStep> steps;
	private int ticksInStep;
	private int totalTicks;
	private JumpPhase jumpPhase;
	private int jumpTick;
	private int jumpRetries;
	private bool lastJumpPressed;
	private int lastHorizontal;
	private bool prevGrounded;
	/// <summary>Grounded this tick and last tick. A ceiling bonk zeroes velocity.Y for a single tick mid-air,
	/// so landing decisions use this instead of <see cref="BodyState.Grounded"/>.</summary>
	private bool settled;
	private float prevX, prevY;
	private int pushingStillTicks;
	private int resyncIndex = -1;
	private string notice;
	private int actionIndex;
	private bool actionsStarted;

	public IReadOnlyList<PathStep> Steps => steps;
	public int StepIndex { get; private set; }
	public ExecStatus Status { get; private set; } = ExecStatus.Running;
	/// <summary>Human-readable reason for the last non-Running status.</summary>
	public string Reason { get; private set; } = "";
	/// <summary>Short description of what the current step is doing, for the overlay and traces.</summary>
	public string Phase { get; private set; } = "";
	/// <summary>Times the executor skipped ahead after landing on a later node of the path.</summary>
	public int Resyncs { get; private set; }
	/// <summary>Number of steps fully completed.</summary>
	public int Completed => StepIndex;

	public PathStep? Current => StepIndex < steps.Count ? steps[StepIndex] : null;

	public PathExecutor(MoveContext ctx, IReadOnlyList<PathStep> path)
	{
		this.ctx = ctx;
		steps = new List<PathStep>(path);
	}

	public MoveInput Tick(in BodyState s)
	{
		if (Status != ExecStatus.Running)
			return MoveInput.None;

		totalTicks++;
		settled = s.Grounded && prevGrounded;
		prevGrounded = s.Grounded;
		bool moved = totalTicks == 1 || Math.Abs(s.X - prevX) > 0.25f || Math.Abs(s.Y - prevY) > 0.25f;
		prevX = s.X;
		prevY = s.Y;
		if (moved)
			pushingStillTicks = 0;
		else if (lastHorizontal != 0 && ++pushingStillTicks > NoMovementTicks)
			return Finish(ExecStatus.Stuck, $"pressing {(lastHorizontal > 0 ? "right" : "left")} for {pushingStillTicks} ticks without moving at {Body.NodeOf(s, ctx.Physics)} (step {StepIndex} {Current})");
		if (totalTicks % RevalidateInterval == 0 && !Revalidate())
			return Finish(ExecStatus.Blocked, Reason);

		// A completed step hands over to the next one within the same tick.
		for (int guard = 0; guard < 4; guard++) {
			if (StepIndex >= steps.Count)
				return Finish(ExecStatus.Finished, "all steps done");

			PathStep step = steps[StepIndex];
			ticksInStep++;
			if (ticksInStep > Budget(step))
				return Finish(ExecStatus.Stuck, $"step {StepIndex} {step.Kind} {step.From}->{step.To} timed out after {ticksInStep} ticks at {Body.NodeOf(s, ctx.Physics)}");

			MoveInput input;
			bool advanced;
			bool ok = step.Kind switch {
				MoveKind.Jump => TickJump(s, step, out input, out advanced),
				MoveKind.Pillar => TickPillar(s, step, out input, out advanced),
				_ => TickGroundMove(s, step, out input, out advanced),
			};
			if (!ok)
				return MoveInput.None; // Finish() already called
			if (advanced) {
				if (resyncIndex >= 0) {
					StepIndex = resyncIndex - 1;
					resyncIndex = -1;
				}
				Advance();
				continue;
			}
			lastJumpPressed = input.Jump;
			lastHorizontal = input.Horizontal;
			return input;
		}
		lastJumpPressed = false;
		lastHorizontal = 0;
		return MoveInput.None;
	}

	private bool Revalidate()
	{
		int end = Math.Min(steps.Count, StepIndex + RevalidateLookahead);
		for (int i = StepIndex; i < end; i++) {
			// The step in progress is only re-checked before it really starts (its own body may sit in the swept area).
			if (i == StepIndex && ticksInStep > 2)
				continue;
			if (!ctx.IsStepValid(steps[i])) {
				Reason = $"step {i} {steps[i]} no longer valid";
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// The body settled on a node the current step does not explain. If that node is on the path a few steps
	/// ahead (typical after overshooting a short drop at running speed), skip forward instead of re-planning.
	/// </summary>
	private bool TryResync(TilePos node)
	{
		int end = Math.Min(steps.Count, StepIndex + ResyncLookahead);
		for (int i = StepIndex + 1; i < end; i++) {
			int target = steps[i].From == node ? i : steps[i].To == node ? i + 1 : -1;
			if (target < 0)
				continue;
			Resyncs++;
			notice = $"resync: settled at {node} during step {StepIndex} {steps[StepIndex]}; skipping to step {target}";
			resyncIndex = target;
			return true;
		}
		return false;
	}

	/// <summary>Returns and clears the latest informational message (e.g. a resync) for the caller to log.</summary>
	public string PopNotice()
	{
		string n = notice;
		notice = null;
		return n;
	}

	private void Advance()
	{
		StepIndex++;
		ticksInStep = 0;
		actionIndex = 0;
		actionsStarted = false;
		jumpPhase = JumpPhase.Align;
		jumpTick = 0;
		jumpRetries = 0;
	}

	private MoveInput Finish(ExecStatus status, string reason)
	{
		Status = status;
		Reason = reason;
		Phase = status.ToString();
		lastJumpPressed = false;
		return MoveInput.None;
	}

	private int Budget(in PathStep step) => step.Kind switch {
		MoveKind.Jump => 150 + (step.Template?.Ticks ?? 60) * 2,
		MoveKind.Drop => 120 + (int)(step.Cost * 3),
		MoveKind.Pillar => 180 + (int)(step.Cost * 3),
		_ => 60 + (int)(step.Cost * 4),
	};

	/// <summary>Moves that can be taken at running speed. A one-tile drop is vanilla StepDown: it snaps while
	/// walking. Longer drops must approach the edge slowly so the fall stays inside the destination columns.</summary>
	private static bool IsRunnable(in PathStep s) =>
		!s.HasActions && s.Dir != 0
		&& (s.Kind == MoveKind.Walk || s.Kind == MoveKind.StepUp || (s.Kind == MoveKind.Drop && s.To.Y - s.From.Y == 1));

	/// <summary>Can we keep running through the end of this step into the next one without stopping?</summary>
	private bool ChainsIntoNext(in PathStep step)
	{
		if (StepIndex + 1 >= steps.Count || !IsRunnable(step))
			return false;
		PathStep next = steps[StepIndex + 1];
		return step.Dir != 0 && next.Dir == step.Dir && IsRunnable(next);
	}

	private bool TickGroundMove(in BodyState s, in PathStep step, out MoveInput input, out bool advanced)
	{
		PhysicsParams p = ctx.Physics;
		input = MoveInput.None;
		advanced = false;
		TilePos node = Body.NodeOf(s, p);

		// Break/place actions come first; the movement starts once they are all done.
		int actions = TickActions(s, step, ref input);
		if (actions < 0)
			return false;
		if (actions == 0)
			return true;
		if (actions == 2) {
			advanced = true;
			return true;
		}

		// Walking passes through To while grounded every tick; a drop must actually have landed.
		bool landed = step.Kind == MoveKind.Drop ? settled : s.Grounded;
		if (landed && node == step.To) {
			advanced = true;
			return true;
		}
		if (settled && ticksInStep > 3 && node.ChebyshevTo(step.From) > 1 && node.ChebyshevTo(step.To) > 1) {
			if (TryResync(node)) {
				advanced = true;
				return true;
			}
			Finish(ExecStatus.OffPath, $"step {StepIndex} {step.Kind} {step.From}->{step.To}: standing at {node}");
			return false;
		}

		float targetX = Body.CanonicalLeft(step.To.X, p);
		if (ChainsIntoNext(step)) {
			input.Horizontal = step.Dir;
			Phase = $"{step.Kind} run";
		}
		else {
			input.Horizontal = Arrive.Steer(s.X, s.VX, targetX, p, 2f);
			Phase = step.Kind == MoveKind.Drop ? (s.Grounded ? "Drop edge" : "Drop fall") : $"{step.Kind} arrive";
		}
		// Never hold Down: it disables StepUp and drops through platforms.
		return true;
	}

	private bool TickJump(in BodyState s, in PathStep step, out MoveInput input, out bool advanced)
	{
		PhysicsParams p = ctx.Physics;
		JumpTemplate t = step.Template;
		input = MoveInput.None;
		advanced = false;
		TilePos node = Body.NodeOf(s, p);

		if (jumpPhase == JumpPhase.Align) {
			float startX = Body.CanonicalLeft(step.From.X, p);
			if (settled && node.ChebyshevTo(step.From) > 1) {
				if (TryResync(node)) {
					advanced = true;
					return true;
				}
				Finish(ExecStatus.OffPath, $"step {StepIndex} jump: expected to start at {step.From}, standing at {node}");
				return false;
			}
			bool aligned = settled && node == step.From && Math.Abs(s.X - startX) <= JumpLibrary.AlignTolerance
				&& Math.Abs(s.VX) <= 0.05f && !lastJumpPressed && s.ReleaseJump; // templates assume take-off from rest
			if (!aligned) {
				input.Horizontal = Arrive.Steer(s.X, s.VX, startX, p, 1f);
				Phase = "Jump align";
				return true;
			}
			jumpPhase = JumpPhase.Air;
			jumpTick = 0;
		}

		if (jumpTick == 1 && s.Grounded && node == step.From && jumpRetries < 3) {
			// The jump did not fire (releaseJump was still false). Release for a tick and try again.
			jumpRetries++;
			jumpPhase = JumpPhase.Align;
			Phase = "Jump retry";
			return true;
		}
		if (jumpTick > 1 && settled) {
			if (node == step.To || TryResync(node)) {
				advanced = true;
				return true;
			}
			Finish(ExecStatus.OffPath, $"step {StepIndex} jump {step.From}->{step.To} [{t.Variant}] landed at {node} after {jumpTick} ticks");
			return false;
		}

		input = JumpLibrary.ControllerInput(s, jumpTick, t.HoldTicks, t.DelayTicks, Body.CanonicalLeft(step.To.X, p), p);
		Phase = $"Jump t{jumpTick}/{t.Ticks}";
		jumpTick++;
		return true;
	}

	/// <summary>
	/// Performs the step's break/place actions in order. Returns 1 when all are done (or there are none),
	/// 0 while working (<paramref name="input"/> filled in), 2 after resyncing to a later step, -1 after Finish().
	/// Before the first action the bot settles on the step's start node; after that it keeps working wherever
	/// it ends up (e.g. when mining its own floor drops it a row), because every target cell stays in reach.
	/// </summary>
	private int TickActions(in BodyState s, in PathStep step, ref MoveInput input)
	{
		if (!step.HasActions || actionIndex >= step.Actions.Count)
			return 1;

		PhysicsParams p = ctx.Physics;
		TilePos node = Body.NodeOf(s, p);
		float startX = Body.CanonicalLeft(step.From.X, p);
		if (!actionsStarted) {
			if (settled && node.ChebyshevTo(step.From) > 1) {
				if (TryResync(node))
					return 2;
				Finish(ExecStatus.OffPath, $"step {StepIndex} {step}: expected to start at {step.From}, standing at {node}");
				return -1;
			}
			if (!settled || node != step.From) {
				input.Horizontal = Arrive.Steer(s.X, s.VX, startX, p, 2f);
				Phase = "Actions: positioning";
				return 0;
			}
			actionsStarted = true;
		}

		while (actionIndex < step.Actions.Count) {
			BlockAction a = step.Actions[actionIndex];
			CellKind k = ctx.Grid.Get(a.Cell.X, a.Cell.Y);
			bool done = a.Kind == ActionKind.Break ? !k.IsSolid() : k.IsSolid();
			if (done) {
				actionIndex++;
				continue;
			}
			input.Tool = a.Kind == ActionKind.Break ? ToolRequest.Pickaxe : ToolRequest.Block;
			input.UseItem = true;
			input.Aim(a.Cell.X, a.Cell.Y);
			if (s.Grounded)
				input.Horizontal = Arrive.Steer(s.X, s.VX, startX, p, 3f);
			Phase = $"{(a.Kind == ActionKind.Break ? "Mining" : "Placing")} {a.Cell} ({actionIndex + 1}/{step.Actions.Count})";
			return 0;
		}
		return 1;
	}

	/// <summary>
	/// Pillar: settle centred on the start node with a block in hand, jump, place under the feet as soon as the
	/// hitbox has cleared the cell (vanilla refuses to place into a hitbox), and land on it.
	/// </summary>
	private bool TickPillar(in BodyState s, in PathStep step, out MoveInput input, out bool advanced)
	{
		PhysicsParams p = ctx.Physics;
		input = new MoveInput { Tool = ToolRequest.Block };
		advanced = false;
		TilePos node = Body.NodeOf(s, p);
		float startX = Body.CanonicalLeft(step.From.X, p);
		ITileGrid g = ctx.Grid;
		bool placed = g.Get(step.From.X, step.From.Y).IsSolid() || g.Get(step.From.X + 1, step.From.Y).IsSolid();

		if (settled && node == step.To && placed) {
			advanced = true;
			return true;
		}

		if (jumpPhase == JumpPhase.Align) {
			if (settled && node.ChebyshevTo(step.From) > 1) {
				if (TryResync(node)) {
					advanced = true;
					return true;
				}
				Finish(ExecStatus.OffPath, $"step {StepIndex} pillar: expected to start at {step.From}, standing at {node}");
				return false;
			}
			bool aligned = settled && node == step.From && Math.Abs(s.X - startX) <= JumpLibrary.AlignTolerance
				&& Math.Abs(s.VX) <= 0.1f && !lastJumpPressed && s.ReleaseJump;
			if (!aligned) {
				input.Horizontal = Arrive.Steer(s.X, s.VX, startX, p, 1f);
				Phase = "Pillar align";
				return true;
			}
			jumpPhase = JumpPhase.Air;
			jumpTick = 0;
		}

		if (jumpTick == 1 && s.Grounded && node == step.From && jumpRetries < 3) {
			jumpRetries++;
			jumpPhase = JumpPhase.Align;
			Phase = "Pillar retry";
			return true;
		}
		if (jumpTick > 1 && settled) {
			if (node == step.To && placed) {
				advanced = true;
				return true;
			}
			if (node == step.From && jumpRetries < 3) {
				jumpRetries++; // came down without the block (placement refused: NPC in the way, out of blocks...)
				jumpPhase = JumpPhase.Align;
				Phase = "Pillar retry";
				return true;
			}
			Finish(ExecStatus.OffPath, $"step {StepIndex} pillar {step.From}->{step.To} landed at {node} (placed {placed})");
			return false;
		}

		TilePos cell = BuildMove.PillarCell(g, step.From);
		input.Jump = jumpTick < BuildMove.PillarHoldTicks(p) && !placed;
		input.Horizontal = Arrive.Steer(s.X, s.VX, startX, p, 1f);
		// Vanilla compares the cell against Rectangle((int)X, (int)Y, w, h): place once the feet are above it.
		if (!placed && (float)Math.Floor(s.Y) + p.Height <= cell.Y * Body.TileSize - BuildMove.PillarClearance) {
			input.UseItem = true;
			input.Aim(cell.X, cell.Y);
		}
		Phase = placed ? "Pillar landing" : $"Pillar t{jumpTick}";
		jumpTick++;
		return true;
	}
}