using System;
using System.Collections.Generic;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;

namespace TerraBot.Bot.Errands;

/// <summary>How an errand ended, or that it has not (docs ERRAND_DESIGN.md section 2).</summary>
public enum OutcomeKind
{
	Running,
	/// <summary>Its goal holds.</summary>
	Done,
	/// <summary>It cannot go on by its method; <see cref="Outcome.Reason"/> and <see cref="Outcome.Evidence"/> say why. A record, never a retry.</summary>
	Blocked,
	/// <summary>Given up by a decision (its budget, or the plan no longer wants it).</summary>
	Abandoned,
}

/// <summary>An errand's outcome: the kind, the reason, and the evidence (a cell, a tile type, a count) a later decision reads.</summary>
public readonly record struct Outcome(OutcomeKind Kind, string Reason, string Evidence)
{
	public static readonly Outcome Running = new(OutcomeKind.Running, "", "");
	public static Outcome Done(string reason) => new(OutcomeKind.Done, reason, "");
	public static Outcome Blocked(string reason, string evidence) => new(OutcomeKind.Blocked, reason, evidence);
	public static Outcome Abandoned(string reason) => new(OutcomeKind.Abandoned, reason, "");
	public bool Over => Kind != OutcomeKind.Running;
	public override string ToString() => Kind == OutcomeKind.Running ? "running" : $"{Kind.ToString().ToLowerInvariant()}: {Reason}" + (Evidence.Length > 0 ? $" ({Evidence})" : "");
}

/// <summary>What the planner does for an errand: resolve its needs (the packing list, the remaining demand) into the next skill.</summary>
public interface IErrandSupport
{
	/// <summary>
	/// The first unmet need's skill, or null when all are met. <paramref name="blocked"/> names a need that cannot be met at all
	/// (no recipe, nothing places the station); then the errand cannot go on by this packing list.
	/// </summary>
	ISkill ResolveNeeds(IEnumerable<Need> needs, BotContext ctx, out string blocked);
	/// <summary>Why the last resolved skill was chosen (the planner's status line).</summary>
	string Status { get; }

	/// <summary>Armour better than what is worn that the bag alone makes (crafting at the stations), one need per slot.</summary>
	IEnumerable<Need> ArmourFromTheBag(BotContext ctx) => System.Array.Empty<Need>();
}

/// <summary>
/// A persistent job that owns the bot (docs ERRAND_DESIGN.md section 3): one at a time, with an identity (the place and method
/// chosen), a packing list resolved before it leaves base and then frozen (reserved in the bag, <see cref="BotContext.Reservations"/>),
/// a remaining demand resupplied only when the rest of the work is not feasible with what is carried, an interrupt policy
/// (<see cref="Accepts"/>: optional work offered with its detour cost; emergencies preempt it and hand back), explicit outcomes
/// (<see cref="Check"/>) and persistent progress (<see cref="Save"/>). A death does not end it: the bot goes back to its frontier.
/// The brain (<see cref="BotBrain"/>) asks it for its leaf skills and never asks the planner while it runs.
/// </summary>
public abstract class Errand
{
	/// <summary>The kind of job: "access:underworld", "boss:EyeOfCthulhu"...</summary>
	public abstract string Id { get; }
	/// <summary>The place and method chosen (a shaft's column); part of the saved state and the logs.</summary>
	public string Identity { get; protected set; } = "";
	/// <summary>The errand's own phase: "prepare" (the packing list), then its work's phases.</summary>
	public string Phase { get; protected set; } = PreparePhase;
	public const string PreparePhase = "prepare";
	public long StartedTick { get; private set; }
	/// <summary>Deaths while the errand ran.</summary>
	public int Deaths { get; private set; }
	/// <summary>Loaded from a save (<see cref="Load"/>), not begun afresh.</summary>
	public bool Restored { get; private set; }
	/// <summary>Why the last skill was chosen (the errand's status line, as the planner's).</summary>
	public string Status { get; protected set; } = "";
	/// <summary>The plan it belongs to (ProgressionPlanner.Plans): taken up again after a load only under the same plan.</summary>
	public string Plan { get; set; } = "";

	/// <summary>What must hold before leaving base (gear, a station): resolved in the prepare phase, before the packing list.</summary>
	public IReadOnlyList<Need> Prerequisites { get; protected set; } = Array.Empty<Need>();
	/// <summary>What to carry before leaving base: resolved in the prepare phase, then reserved and frozen.</summary>
	public IReadOnlyList<Need> Packing { get; protected set; } = Array.Empty<Need>();
	/// <summary>What must stay in the bag for survival and the way back (potions, a recall potion): reserved, never spent on the work.</summary>
	public IReadOnlyList<Need> Reserve { get; protected set; } = Array.Empty<Need>();
	/// <summary>What the unfinished work is still expected to consume: resupplied only when short of it (the departure stock is not).</summary>
	public virtual IEnumerable<Need> RemainingDemand(BotContext ctx) => Array.Empty<Need>();

	/// <summary>
	/// No progress (<see cref="Progress"/> unchanged) for this long and the errand is blocked. 2 game hours, the brain's own
	/// no-progress window for a skill (BotBrain.NoProgressTicks); a policy choice.
	/// </summary>
	public virtual long NoProgressTicks => 2 * 3600;
	private float lastProgress = float.NaN;
	private long progressAt;

	/// <summary>The observable completion: standing on the bridge's row, a boss down, a set worn.</summary>
	public abstract bool GoalMet(BotContext ctx);
	/// <summary>A measure that only grows while the work gets on (rows dug, tiles built, items in): the progress watchdog's.</summary>
	public abstract float Progress(BotContext ctx);
	/// <summary>The leaf skill for the current phase (the same instance while it runs), or null when the errand has nothing to do.</summary>
	public abstract ISkill NextSkill(BotContext ctx, IErrandSupport planner);
	/// <summary>The errand's own outcome check (its goal, its failures); the brain adds the progress watchdog (<see cref="Watch"/>).</summary>
	protected abstract Outcome CheckOwn(BotContext ctx);
	/// <summary>Rules that hold every tick while the errand owns the bot (the depth limit lifted on the way down).</summary>
	public virtual void Rules(BotContext ctx) { }
	/// <summary>The deck a fight keeps to while the errand owns the bot (the hellbridge), or null (FightSkill.Deck).</summary>
	public virtual (int MinX, int MaxX, int StandY)? FightDeck(BotContext ctx) => null;
	/// <summary>
	/// Optional work offered with its detour cost (ticks): accept, and the brain runs it as a sub-skill and comes back; decline, and
	/// it waits for the errand to end. The prepare phase accepts everything, as the plan did.
	/// </summary>
	public virtual bool Accepts(in Opportunity o, BotContext ctx) => Phase == PreparePhase;
	/// <summary>A skill of the errand's ended; <paramref name="why"/> is its failure reason, or the brain's (no progress).</summary>
	public virtual void OnSkillFinished(ISkill skill, SkillStatus status, string why, BotContext ctx) { }
	/// <summary>A death: the body is at the spawn, whatever ran is stale, the errand goes on to its frontier (the watchdog starts afresh).</summary>
	public virtual void OnDeath(BotContext ctx)
	{
		Deaths++;
		lastProgress = float.NaN;
		progressAt = ctx.Tick;
	}
	/// <summary>A reflex (a fight, surfacing) is over and the work resumes.</summary>
	public virtual void OnEmergencyOver(BotContext ctx) { }
	public abstract string Describe();

	public virtual void Begin(BotContext ctx)
	{
		StartedTick = ctx.Tick;
		progressAt = ctx.Tick;
		lastProgress = float.NaN;
	}

	/// <summary>The errand is over: its reservations are released.</summary>
	public virtual void End(BotContext ctx, Outcome outcome) => ctx.Reservations.Release(this);

	/// <summary>
	/// The outcome now: the errand's own check, then the progress watchdog (no progress for <see cref="NoProgressTicks"/>: blocked,
	/// saying where it stood). Commitment never means staying stuck.
	/// </summary>
	public Outcome Check(BotContext ctx)
	{
		Outcome own = CheckOwn(ctx);
		if (own.Over)
			return own;
		return Watch(ctx);
	}

	private Outcome Watch(BotContext ctx)
	{
		float p = Progress(ctx);
		if (float.IsNaN(lastProgress) || p > lastProgress || Phase == PreparePhase) {
			lastProgress = p;
			progressAt = ctx.Tick;
			return Outcome.Running;
		}
		if (ctx.Tick - progressAt > NoProgressTicks)
			return Outcome.Blocked($"no progress in {NoProgressTicks / 3600} game hours", $"progress {p:0} at {ctx.Node}, phase {Phase}");
		return Outcome.Running;
	}

	/// <summary>The prepare phase is over: the packing list and the reserve are reserved in the bag.</summary>
	protected void Packed(BotContext ctx, string nextPhase)
	{
		ctx.Reservations.Release(this);
		foreach (Need n in Packing)
			ctx.Reservations.Reserve(this, n);
		foreach (Need n in Reserve)
			ctx.Reservations.Reserve(this, n);
		Phase = nextPhase;
	}

	/// <summary>The state to save with the player (strings only: Core and Bot know no game types). Keys and values of this errand's own.</summary>
	public virtual Dictionary<string, string> Save() => new() { ["phase"] = Phase, ["identity"] = Identity, ["deaths"] = Deaths.ToString(), ["plan"] = Plan };

	public virtual void Load(IReadOnlyDictionary<string, string> state)
	{
		Plan = state.GetValueOrDefault("plan", "");
		Phase = state.GetValueOrDefault("phase", PreparePhase);
		Identity = state.GetValueOrDefault("identity", "");
		Deaths = int.TryParse(state.GetValueOrDefault("deaths", "0"), out int d) ? d : 0;
		Restored = true;
	}

	/// <summary>Reserved again after a load: the bag's reservations are not saved, the errand's lists are.</summary>
	public void ReserveAgain(BotContext ctx)
	{
		if (Phase == PreparePhase)
			return;
		ctx.Reservations.Release(this);
		foreach (Need n in Packing)
			ctx.Reservations.Reserve(this, n);
		foreach (Need n in Reserve)
			ctx.Reservations.Reserve(this, n);
	}
}

/// <summary>Makes an errand from its saved id and state (TerraBotPlayer.LoadData).</summary>
public static class ErrandFactory
{
	public static Errand Create(string id, IReadOnlyDictionary<string, string> state, GameIds ids)
	{
		Errand e = id switch {
			WallOfFleshErrand.ErrandId => WallOfFleshErrand.FromState(state, ids),
			_ => null,
		};
		e?.Load(state);
		return e;
	}
}
