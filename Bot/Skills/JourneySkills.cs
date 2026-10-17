using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Journey Mode: take a researched item from the duplication menu until the bag holds <see cref="Count"/>, one click every
/// <see cref="CraftSkill.ClickInterval"/> ticks (each click a full stack, as the game gives it: <see cref="IJourney.Duplicate"/>).
/// The planner's cheap way to an item it has researched, instead of gathering or crafting it. No room for the stack: room is
/// made as for a craft (<see cref="Brain.BotBrain.KeepRoom(BotContext, int)"/>).
/// </summary>
public sealed class DuplicateSkill : ISkill
{
	/// <summary>Clicks that gave nothing (no room) before giving up, as a craft that stays blocked (CraftSkill.MaxBusyTicks).</summary>
	public const int MaxRefusals = CraftSkill.MaxBusyTicks / CraftSkill.ClickInterval;

	private int cooldown, refusals;

	public int ItemType { get; }
	public int Count { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Duplicate";
	public string Key => $"Duplicate {ItemType} {Count}";

	public DuplicateSkill(int itemType, int count)
	{
		ItemType = itemType;
		Count = count;
	}

	public string Describe() => $"Duplicate item {ItemType} to {Count}";
	public void Start(BotContext ctx) { }
	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.Inventory.Count(ItemType) >= Count)
			return SkillStatus.Succeeded;
		if (!ctx.Infinite(ItemType))
			return Fail(ctx, "not researched");
		if (cooldown > 0) {
			cooldown--;
			return SkillStatus.Running;
		}
		cooldown = CraftSkill.ClickInterval;
		if (Brain.ResearchHabit.DuplicateOnce(ctx, ItemType, $"to {Count}") > 0)
			return SkillStatus.Running;
		if (refusals == 0)
			Brain.BotBrain.KeepRoom(ctx, ItemType);
		if (++refusals > MaxRefusals)
			return Fail(ctx, "the duplication menu gave nothing (no room in the bag?)");
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Duplicate {ctx.Knowledge?.ItemName(ItemType) ?? $"item {ItemType}"} failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>
/// Journey Mode: research the item from the bag until it is researched, one stack into the research slot every
/// <see cref="CraftSkill.ClickInterval"/> ticks (<see cref="Brain.ResearchHabit.ResearchOnce"/>). The planner's when it holds
/// back carried items for research (life crystals: researched, they are duplicated to max life).
/// </summary>
public sealed class ResearchSkill : ISkill
{
	private int cooldown;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Research";
	public string Key => $"Research {ItemType}";

	public ResearchSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Research item {ItemType}";
	public void Start(BotContext ctx) { }
	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.Infinite(ItemType))
			return SkillStatus.Succeeded;
		if (cooldown > 0) {
			cooldown--;
			return SkillStatus.Running;
		}
		cooldown = CraftSkill.ClickInterval;
		if (Brain.ResearchHabit.ResearchOnce(ctx, ItemType, "for the plan") > 0)
			return SkillStatus.Running;
		FailureReason = ctx.Inventory.Count(ItemType) <= 0 ? "none carried" : "the research slot refused it";
		ctx.Log.Warn($"Research {ctx.Knowledge?.ItemName(ItemType) ?? $"item {ItemType}"} failed: {FailureReason}");
		return SkillStatus.Failed;
	}
}
