using System;

namespace TerraBot.Bot.Skills;

/// <summary>
/// A walk (a trip along the surface) that ends as soon as what it looks for comes into view (<see cref="Found"/>, checked
/// every <see cref="CheckEvery"/> ticks), as a player stops when the Dungeon shows on the screen; else when the walk ends.
/// For looking for something far off in a known direction: the Dungeon, 1,000 tiles and more from the spawn (docs M11_SPEC.md
/// section 1c), whose Old Man and bricks are seen from about 50-60 tiles.
/// </summary>
public sealed class TravelUntilSkill : ISkill
{
	/// <summary>The look is a tile search (the Dungeon's bricks within 60 tiles): twice a second.</summary>
	public const int CheckEvery = 30;

	private readonly Func<BotContext, bool> found;
	private int ticks;

	public string What { get; }
	public WalkToSkill Walk { get; }
	/// <summary>Did it end because what it looked for came into view?</summary>
	public bool Seen { get; private set; }
	public string FailureReason => Walk.FailureReason;
	public string Name => "Travel";
	public string Key => $"Travel {What} {Walk.Key}";

	public TravelUntilSkill(string what, WalkToSkill walk, Func<BotContext, bool> found)
	{
		What = what;
		Walk = walk;
		this.found = found;
	}

	public string Describe() => $"Travel looking for {What} > {Walk.Describe()}";

	public void Start(BotContext ctx)
	{
		ctx.Log.Info($"Travel start: looking for {What} on the way to {Walk.Target}");
		ticks = 0;
		Walk.Start(ctx);
	}

	public void Stop(BotContext ctx) => Walk.Stop(ctx);
	public void Pause(BotContext ctx) => Walk.Pause(ctx);
	public void Resume(BotContext ctx) => Walk.Resume(ctx);

	public SkillStatus Tick(BotContext ctx)
	{
		if (ticks++ % CheckEvery == 0 && found(ctx)) {
			Seen = true;
			ctx.Output = Core.Physics.MoveInput.None;
			ctx.Log.Info($"Travel: {What} in view at {ctx.Node}");
			return SkillStatus.Succeeded;
		}
		return Walk.Tick(ctx);
	}
}
