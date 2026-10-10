using System;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Melee combat against nearby hostile NPCs.
/// <list type="bullet">
/// <item>Target: keep the current target while it lives and stays near, else the nearest hostile within
/// <see cref="EngageRadius"/>.</item>
/// <item>Far: chase with a quiet <see cref="WalkToSkill"/> towards the target's feet (re-targeted as it moves);
/// close and level: steer straight at it.</item>
/// <item>In reach: stop, face it (melee swings go the way the player faces, which only the movement keys change,
/// and only between swings), aim the virtual mouse at its centre, swing.</item>
/// <item>Below <see cref="HealBelow"/> of max life: press quick-heal (pulsed so vanilla sees a fresh press).</item>
/// </list>
/// Succeeds once no hostile has been within range for <see cref="CalmTicks"/>; fails without a melee weapon.
/// </summary>
public sealed class FightSkill : ISkill
{
	public const float EngageRadius = 25 * 16f;
	/// <summary>Enemies that once came within <see cref="EngageRadius"/> stay targets out to this distance.</summary>
	public const float LeashRadius = 50 * 16f;
	public const int CalmTicks = 90;
	public const float HealBelow = 0.4f;
	private const int RetargetPathTicks = 30;

	private WalkToSkill approach;
	private TilePos approachTarget;
	private int approachAge;
	private int calm;
	private int targetId = -1;
	private string targetName = "";
	private float targetDistance;
	private bool healPressedLastTick;
	private readonly System.Collections.Generic.HashSet<int> engaged = new();

	public int Kills { get; private set; }
	public string State { get; private set; } = "new";
	public string Name => "Fight";
	public string FailureReason { get; private set; } = "";

	public string Describe() =>
		targetId >= 0 ? $"Fight {targetName} ({targetDistance / 16f:0.0} tiles) [{State}] kills {Kills}" : $"Fight [{State}] kills {Kills}";

	public void Start(BotContext ctx) => ctx.Log.Info($"Fight start: {ctx.Hostiles.Count} hostile(s) near, weapon {ctx.Combat.WeaponName}");

	public void Stop(BotContext ctx)
	{
		approach?.Stop(ctx);
		approach = null;
		ctx.Output = MoveInput.None;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return SkillStatus.Running;
		}
		if (!ctx.Combat.HasWeapon) {
			FailureReason = "no melee weapon in the hotbar";
			ctx.Log.Warn("Fight failed: " + FailureReason);
			return SkillStatus.Failed;
		}

		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		EntityInfo? target = PickTarget(ctx, cx, cy);
		if (target == null) {
			approach = null;
			if (++calm >= CalmTicks) {
				ctx.Log.Info($"Fight over: {Kills} kill(s)");
				State = "done";
				return SkillStatus.Succeeded;
			}
			State = "watching";
			Heal(ctx);
			return SkillStatus.Running;
		}
		calm = 0;
		EntityInfo t = target.Value;
		targetDistance = t.DistanceTo(cx, cy);

		float dx = t.CenterX - cx, dy = t.CenterY - cy;
		float reachX = p.Width * 0.5f + ctx.Combat.WeaponReach + t.Width * 0.5f - 4f;
		float reachY = p.Height * 0.5f + t.Height * 0.5f + 8f;
		bool inReach = Math.Abs(dx) <= reachX && Math.Abs(dy) <= reachY;

		if (inReach) {
			approach = null;
			int want = dx >= 0 ? 1 : -1;
			ctx.Output.Tool = ToolRequest.Weapon;
			ctx.Output.AimMouse(t.CenterX, t.CenterY);
			if (ctx.Combat.Facing != want && ctx.Combat.ItemAnimation == 0) {
				ctx.Output.Horizontal = want; // turn first; the swing starts next tick facing the right way
				State = "turning";
			}
			else {
				ctx.Output.UseItem = true;
				State = "swinging";
			}
		}
		else {
			Approach(ctx, t, dx, dy);
			// Swing early at targets about to come into reach (slimes hop in), unless the path is mining/building.
			if (ctx.Output.Tool == ToolRequest.None && Math.Abs(dx) <= reachX + 48f && Math.Abs(dy) <= reachY + 16f
				&& ctx.Combat.Facing == Math.Sign(dx)) {
				ctx.Output.Tool = ToolRequest.Weapon;
				ctx.Output.UseItem = true;
				ctx.Output.AimMouse(t.CenterX, t.CenterY);
			}
		}
		Heal(ctx);
		return SkillStatus.Running;
	}

	private EntityInfo? PickTarget(BotContext ctx, float cx, float cy)
	{
		EntityInfo? current = null, nearest = null;
		float best = float.MaxValue;
		foreach (EntityInfo e in ctx.Hostiles) {
			float d = e.DistanceTo(cx, cy);
			if (d <= EngageRadius)
				engaged.Add(e.Id); // once engaged, keep fighting it while it stays within the leash
			bool candidate = d <= EngageRadius || (engaged.Contains(e.Id) && d <= LeashRadius);
			if (e.Id == targetId && candidate)
				current = e;
			if (candidate && d < best) {
				best = d;
				nearest = e;
			}
		}
		engaged.RemoveWhere(id => !ctx.Hostiles.Any(h => h.Id == id));
		if (targetId >= 0 && current == null) {
			// The target vanished: killed (or despawned / ran off).
			if (!ctx.Hostiles.Any(e => e.Id == targetId)) {
				Kills++;
				ctx.Log.Info($"Fight: {targetName} gone (kill #{Kills})");
			}
			targetId = -1;
		}
		EntityInfo? pick = current ?? nearest;
		if (pick is EntityInfo e2 && e2.Id != targetId) {
			targetId = e2.Id;
			targetName = e2.Name;
			ctx.Log.Info($"Fight: target {e2.Name} #{e2.Id} at {e2.FeetTile}, {e2.DistanceTo(cx, cy) / 16f:0.0} tiles, life {e2.Life}/{e2.LifeMax}");
		}
		return pick;
	}

	private void Approach(BotContext ctx, EntityInfo t, float dx, float dy)
	{
		PhysicsParams p = ctx.Physics;
		// Close and roughly level: just run at it (the path would be stale before it is finished).
		if (Math.Abs(dx) <= 4 * 16f && Math.Abs(dy) <= 1.5f * 16f) {
			approach = null;
			ctx.Output.Horizontal = Math.Sign(dx);
			State = "closing in";
			return;
		}
		TilePos feet = t.FeetTile;
		approachAge++;
		if (approach == null || (approachAge > RetargetPathTicks && feet.ChebyshevTo(approachTarget) > 2)) {
			approach = new WalkToSkill(feet) { Quiet = true, Tolerance = 2 };
			approach.Start(ctx);
			approachTarget = feet;
			approachAge = 0;
		}
		SkillStatus s = approach.Tick(ctx);
		if (s != SkillStatus.Running)
			approach = null;
		State = "chasing";
	}

	private void Heal(BotContext ctx)
	{
		bool want = ctx.Combat.LifeFraction < HealBelow && ctx.Combat.CanQuickHeal && !healPressedLastTick;
		ctx.Output.QuickHeal = want;
		healPressedLastTick = want;
		if (want)
			ctx.Log.Info($"Fight: quick-heal at {ctx.Combat.Life}/{ctx.Combat.LifeMax}");
	}
}
