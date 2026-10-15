namespace TerraBot.Bot.Skills;

public enum SkillStatus
{
	Running,
	Succeeded,
	Failed,
}

/// <summary>
/// A small tick-driven behaviour (walk, fight, chop, mine, craft...). Skills write controls into
/// <see cref="BotContext.Output"/> and never touch the world directly. Composite skills run children by
/// ticking them and forwarding their status.
/// </summary>
public interface ISkill
{
	string Name { get; }

	/// <summary>One-line state for the overlay and logs.</summary>
	string Describe();

	void Start(BotContext ctx);

	SkillStatus Tick(BotContext ctx);

	/// <summary>Called when the skill finishes or is interrupted. Must leave no controls pressed.</summary>
	void Stop(BotContext ctx);

	/// <summary>
	/// A reflex (a fight, a breath, loot) takes over for a while: leave no controls pressed but keep what is needed to go
	/// on, plans included (Baritone keeps its path through an interruption). Default: <see cref="Stop"/>.
	/// </summary>
	void Pause(BotContext ctx) => Stop(ctx);

	/// <summary>Going on after <see cref="Pause"/>. Default: <see cref="Start"/> again.</summary>
	void Resume(BotContext ctx) => Start(ctx);

	/// <summary>Why the skill failed (empty while running or after success).</summary>
	string FailureReason => "";

	/// <summary>
	/// What job this is, from its defining parameters only (e.g. "Mine 11 6 24"), or null for "never the same job". Two
	/// skills with the same key are the same job: one paused by a fight resumes (the same instance, with what it has
	/// learnt) when the planner's next choice has its key, instead of a fresh one starting over (AltoClef's Task.isEqual).
	/// </summary>
	string Key => null;
}
