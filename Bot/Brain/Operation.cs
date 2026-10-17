namespace TerraBot.Bot.Brain;

/// <summary>
/// A continuing job that owns the bot (docs DESCENT_DIAGNOSIS.md, the second opinion's "operation"): its name, its identity (the
/// method and place chosen: a shaft's column), when and at which death count it began (a death ends it), and its failures in a
/// row and the last blocker (its escape: too many, and it hands the bot back to the plan with the reason).
/// </summary>
public sealed class Operation
{
	public string Name { get; }
	public string Identity { get; }
	public int StartedDeaths { get; }
	public long StartedTick { get; }
	public int Failures { get; set; }
	public string Blocker { get; set; }

	public Operation(string name, string identity, int startedDeaths, long startedTick)
	{
		Name = name;
		Identity = identity;
		StartedDeaths = startedDeaths;
		StartedTick = startedTick;
	}
}
