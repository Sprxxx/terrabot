using TerraBot.Core.Grid;
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
	/// <summary>Shots within this many px are threats; a worm's segments (vanilla NPCAIStyleID.Worm) within <see cref="WormThreatPx"/>.</summary>
	public const float ShotThreatPx = 30 * 16f, WormThreatPx = 15 * 16f;
	public const int WormAi = 6;
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
	/// <summary>
	/// The columns of floor (a block or a platform under the feet row) that run on unbroken from the body's two columns at
	/// <paramref name="nodeX"/>, inside the deck and its slack: where a dodge may go.
	/// </summary>
	public static (int Left, int Right) FloorSpan(ITileGrid grid, int nodeX, (int MinX, int MaxX, int StandY) deck)
	{
		int floor = deck.StandY + 1, l = nodeX, r = nodeX + 1;
		bool Floor(int x) => grid.Get(x, floor) is var k && (k.IsBlock() || k.IsPlatform());
		while (l - 1 >= deck.MinX - FightSkill.DeckSlack && Floor(l - 1))
			l--;
		while (r + 1 <= deck.MaxX + FightSkill.DeckSlack && Floor(r + 1))
			r++;
		return (l, r);
	}

	public bool Apply(BotContext ctx, (int MinX, int MaxX, int StandY)? deck, string who)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		threats.Clear();
		foreach (ShotInfo s in ctx.Shots)
			if (s.Damage > 0 && s.DistanceTo(cx, cy) <= ShotThreatPx && WallOfFleshPattern.Incoming(s, cx, cy, Dodge.Horizon))
				threats.Add(new Threat(s.X, s.Y, s.Width, s.Height, s.VX, s.VY));
		// A worm's segments too (a Bone Serpent through the deck: bites of 25-35 while the fight swings a glove at it, the body
		// standing still; world_wof E16, 2026-10-17, dead at 76/240 with three serpents on the bridge), as the Eater's fight dodges
		// them; a slime or a bat is met with the sword, not dodged.
		foreach (EntityInfo h in ctx.Hostiles)
			if (h.Damage > 0 && h.AiStyle == WormAi && h.DistanceTo(cx, cy) <= WormThreatPx)
				threats.Add(new Threat(h.X, h.Y, h.Width, h.Height, h.VX, h.VY));
		if (threats.Count == 0) {
			dodging = false;
			return false;
		}
		float minX, maxX, maxBottom;
		if (deck is (int x0, int x1, int standY)) {
			// Only over the floor that is there, from the body's columns out, inside the deck: the deck's span can hold a gap (the
			// columns opened for the Guide Voodoo Doll to fall through at the summon column), and a dodge plan does not see the fall
			// it ends in within its horizon. Seen in game (world_wof E28, 2026-10-18): a dodge right at the summon end stepped onto the
			// opened columns, a 35-row fall into lava.
			var (l, r) = FloorSpan(ctx.Grid, ctx.Node.X, (x0, x1, standY));
			minX = Math.Max(x0 * 16f, l * 16f);
			maxX = Math.Min((x1 + 1) * 16f - p.Width, (r + 1) * 16f - p.Width);
			if (maxX < minX)
				maxX = minX = ctx.Body.X;
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
