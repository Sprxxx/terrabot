using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Put on a carried armour piece, like right-clicking it in the inventory (the game swaps it with whatever is
/// worn in that slot). One try every <see cref="TryInterval"/> ticks, at most <see cref="MaxTries"/>.
/// </summary>
public sealed class EquipSkill : ISkill
{
	public const int TryInterval = 10;
	public const int MaxTries = 5;

	private int cooldown;
	private int tries;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Equip";

	public EquipSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Equip item {ItemType} (try {tries}/{MaxTries})";
	public void Start(BotContext ctx) => ctx.Log.Info($"Equip start: {ctx.Knowledge?.ItemName(ItemType) ?? $"item {ItemType}"}");
	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.Inventory.IsEquipped(ItemType)) {
			ctx.Log.Info($"Equipped item {ItemType}");
			return SkillStatus.Succeeded;
		}
		if (ctx.Inventory.Count(ItemType) <= 0)
			return Fail(ctx, "not carried");
		if (cooldown-- > 0)
			return SkillStatus.Running;
		cooldown = TryInterval;
		if (++tries > MaxTries)
			return Fail(ctx, $"still not worn after {MaxTries} tries");
		ctx.Crafter.TryEquip(ItemType);
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Equip item {ItemType} failed: {reason}");
		return SkillStatus.Failed;
	}
}
