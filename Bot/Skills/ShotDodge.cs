using System;
using System.Collections.Generic;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Shots about to hit, dodged by look-ahead: the boss fights' planner (<see cref="Dodge"/>) on whatever the bot is doing. The move
/// chosen this tick is simulated <see cref="Dodge.Horizon"/> ticks through the real tiles against every hostile shot closing in (a
/// Demon's sickle, a Fire Imp's fireball, spit); about to be hit, the plan hit latest or not at all takes the keys, the tool still
/// aimed and used. Bodies are not threats here: a sword fight meets them (the boss fights dodge bodies, with their own kiting). A
/// plan that ends below the deck's floor, or deeper than a safe fall, scores as a hit, so no dodge leaves the bridge. Seen in game
/// (world_wof E10, 2026-10-17): "Hurt 36 (projectile type 44) ... vel (0,0) ... [waiting for it in the open]", nine sickles taken
/// standing still, two deaths on the bridge; E11 and E12: sickles taken while building and crafting at the bridge's end, where no
/// fight ran, and the knockback threw the bot off into the lava.
/// </summary>
public sealed class ShotDodge
{
	/// <summary>Shots within this many px are threats.</summary>
	public const float ShotThreatPx = 30 * 16f;
	/// <summary>Hysteresis: the dodge starts when the chosen move comes within DodgeStartGap px of a threat, stops once it clears DodgeStopGap.</summary>
	public const float DodgeStartGap = 16f, DodgeStopGap = 32f;
	/// <summary>Half-width (px) of the ground the dodge may use when no deck bounds it.</summary>
	public const float FreeRangePx = 50 * 16f;
	/// <summary>Dodges planned (one per stretch of dodging).</summary>
	public int Dodges { get; private set; }
	private bool dodging;
	private long lastDodgeLog = long.MinValue / 2;
	private readonly List<Threat> threats = new();

	/// <summary>
	/// Reads the shots and the move in <see cref="BotContext.Output"/>; overrides its keys when a dodge is needed. True when it did.
	/// <paramref name="deck"/>: the columns and feet row the body must stay on (an errand's bridge), else a safe fall bounds it.
	/// </summary>
	public bool Apply(BotContext ctx, (int MinX, int MaxX, int StandY)? deck, string who)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		threats.Clear();
		foreach (ShotInfo s in ctx.Shots)
			if (s.Damage > 0 && s.DistanceTo(cx, cy) <= ShotThreatPx && WallOfFleshPattern.Incoming(s, cx, cy, Dodge.Horizon))
				threats.Add(new Threat(s.X, s.Y, s.Width, s.Height, s.VX, s.VY));
		if (threats.Count == 0) {
			dodging = false;
			return false;
		}
		float minX, maxX, maxBottom;
		if (deck is (int x0, int x1, int standY)) {
			minX = x0 * 16f;
			maxX = (x1 + 1) * 16f - p.Width;
			maxBottom = (standY + 2) * 16f;
		}
		else {
			minX = ctx.Body.X - FreeRangePx;
			maxX = ctx.Body.X + FreeRangePx;
			maxBottom = ctx.Body.Y + p.Height + p.SafeFallTiles * 16f;
		}
		int immune = (int)Math.Max(0L, BotContext.HurtImmunityTicks - (ctx.Tick - ctx.LastEnemyHitTick) - 1);
		int chosen = ctx.Output.Horizontal;
		DodgePlan kept = Dodge.Evaluate(ctx.Body, p, float.PositiveInfinity, minX, maxX, threats, chosen, ctx.Output.Jump ? 1 : 0, Dodge.Horizon, immune, ctx.Grid);
		if (!kept.Hits && kept.Clearance >= (dodging ? DodgeStopGap : DodgeStartGap)) {
			dodging = false;
			return false;
		}
		DodgePlan best = Dodge.Best(ctx.Body, p, float.PositiveInfinity, minX, maxX, threats, chosen, Dodge.Horizon, immune, ctx.Grid, maxBottom);
		if (best.Hits && kept.Hits && best.HitTick <= kept.HitTick)
			return false; // nothing better than the move chosen
		if (!dodging) {
			Dodges++;
			if (ctx.Tick - lastDodgeLog >= 30) {
				lastDodgeLog = ctx.Tick;
				ctx.Log.Info($"{who}: dodge {(best.Dir > 0 ? "right" : best.Dir < 0 ? "left" : "stand")}{(best.JumpHold > 0 ? $" + jump {best.JumpHold}" : "")} "
					+ $"({threats.Count} threat(s); {(kept.Hits ? $"the move chosen is hit in {kept.HitTick} ticks" : $"it passes {kept.Clearance:0} px off")}; "
					+ $"the dodge {(best.Hits ? $"is hit in {best.HitTick}" : $"clears by {Math.Min(best.Clearance, 999f):0} px")})");
			}
		}
		dodging = true;
		ctx.Output.Horizontal = best.Dir;
		ctx.Output.Jump = best.JumpHold > 0;
		return true;
	}
}
