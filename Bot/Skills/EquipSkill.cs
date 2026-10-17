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
	public string Key => $"Equip {ItemType}";

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

/// <summary>
/// Use one of a consumable item (a life crystal, a boss summon): hold it and press use until the stack shrinks, or until a use
/// of it shows (<see cref="CombatInfo.HeldItemType"/> in hand mid-swing after a press) where the stack does not: Upgraded
/// Research's "researched consumables are infinite" keeps a researched item (its GlobalItem.ConsumeItem returns false) and the
/// effect still comes. Pressed for ever on a kept stack, it would use it again and again (a second recall, max life climbing).
/// Fails after <see cref="MaxTicks"/> (the game refused: max life reached, not night for a summon... the game starts no swing then,
/// Player.ItemCheck's CanUseItem).
/// </summary>
public sealed class UseItemSkill : ISkill
{
	public const int MaxTicks = 60 * 3;

	private int startCount = -1;
	private int ticks, afterTicks;
	private bool pressed, swung;

	public int ItemType { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Use";
	public string Key => $"Use {ItemType}";

	public UseItemSkill(int itemType) => ItemType = itemType;

	public string Describe() => $"Use item {ItemType}";
	public void Start(BotContext ctx)
	{
		startCount = ctx.Inventory.Count(ItemType);
		ctx.Log.Info($"Use start: {ctx.Knowledge?.ItemName(ItemType) ?? $"item {ItemType}"} (have {startCount})");
	}

	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		int have = ctx.Inventory.Count(ItemType);
		swung |= pressed && ctx.Combat.ItemAnimation > 0 && ctx.Combat.HeldItemType == ItemType;
		if (have < startCount || swung) {
			// Used up at the start of the swing, but its effect comes during it (a recall potion teleports partway): seen in
			// game (world_eye run 67), the next walk started from where the bot drank it, and a second potion was drunk.
			if (ctx.Combat.ItemAnimation > 0 && ++afterTicks <= MaxTicks)
				return SkillStatus.Running;
			ctx.Log.Info($"Used item {ItemType} ({have} left" + (have >= startCount ? ", the stack kept: researched, infinite by a mod)" : ")"));
			return SkillStatus.Succeeded;
		}
		if (have <= 0)
			return Fail(ctx, "not carried");
		if (++ticks > MaxTicks)
			return Fail(ctx, $"not used up after {MaxTicks} ticks (the game refused it)");
		ctx.Output.Tool = ToolRequest.Item;
		ctx.Output.ToolItem = ItemType;
		ctx.Output.UseItem = true;
		pressed = true;
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Use item {ItemType} failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>Do nothing (stand still, fights still pre-empt it) until a condition holds, e.g. nightfall.</summary>
public sealed class WaitSkill : ISkill
{
	private readonly System.Func<BotContext, bool> until;
	private int ticks;

	public string What { get; }
	public int MaxTicks { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Wait";
	public string Key => $"Wait {What}";

	public WaitSkill(string what, System.Func<BotContext, bool> until, int maxTicks)
	{
		What = what;
		this.until = until;
		MaxTicks = maxTicks;
	}

	public string Describe() => $"Wait for {What} ({ticks / 60}s)";
	public void Start(BotContext ctx) => ctx.Log.Info($"Wait start: for {What}");
	public void Stop(BotContext ctx) { }

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (until(ctx)) {
			ctx.Log.Info($"Wait done: {What} after {ticks} ticks");
			return SkillStatus.Succeeded;
		}
		if (++ticks > MaxTicks) {
			FailureReason = $"no {What} after {MaxTicks} ticks";
			return SkillStatus.Failed;
		}
		return SkillStatus.Running;
	}
}
