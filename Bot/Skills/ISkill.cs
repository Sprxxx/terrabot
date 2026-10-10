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

	/// <summary>Why the skill failed (empty while running or after success).</summary>
	string FailureReason => "";
}
