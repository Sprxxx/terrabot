using TerraBot.Bot.Skills;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Journey Mode's research, as the user plays it ("I wait until I have the number I need for researching"): auto mode, Journey
/// on; nothing at all on Classic.
/// <list type="bullet">
/// <item>an item is researched only once the bag holds all research still takes of it, and then in one go (stack after stack
/// into the research slot in the same action): never partly, never a would-be trash for progress (the game's own trash slot
/// researches nothing either: ItemSlot.cs:1061-1071 only moves the stack to Player.trashItem, and CreativeUI.SacrificeItem,
/// CreativeUI.cs:326, is the only caller of ItemsSacrificedUnlocksTracker.RegisterItemSacrifice);</item>
/// <item>nothing is held back for it: what the plan uses (life crystals for max life) is used as before; research comes when a
/// stack reaches the count by itself;</item>
/// <item>gear in use is never given up: a tool, weapon, armour piece, potion or ammo (IInventory.IsGear) only when a spare is
/// left in the bag after research, and never the item in hand mid-swing nor worn gear (the game adapter offers bag stacks only
/// and refuses the selected item while it is in use);</item>
/// <item>what research used up of another thing the bag keeps (a station, the plan's items and their makings: BotBrain.KeptItems)
/// is duplicated back at once; from then on the planner duplicates whatever is researched instead of gathering it.</item>
/// </list>
/// One research every <see cref="Every"/> ticks at most, never in a boss fight, a throw or with an enemy about (a player in the
/// menu is not fighting).
/// </summary>
public static class ResearchHabit
{
	/// <summary>A click in the menu every this many ticks: the crafting menu's pace (CraftSkill.ClickInterval).</summary>
	public const int Every = CraftSkill.ClickInterval;
	/// <summary>
	/// An item the research slot refused (the item in hand mid-swing, a favourited stack) is not offered again for ten habit
	/// passes (BotBrain.HabitEvery): long past the swing (a swing is at most about a second, Item.useAnimation).
	/// </summary>
	public const int RefusedTicks = 10 * BotBrain.HabitEvery;
	/// <summary>Stacks given to the research slot in one go at most: more than the bag's 50 main and 4 ammo slots hold.</summary>
	public const int MaxStacksAtOnce = 54;

	/// <summary>The habit's one action this tick, if any: true when something was researched.</summary>
	public static bool Tick(BotContext ctx, ISkill current)
	{
		if (ctx.Journey == null || !ctx.Journey.Enabled || ctx.Inventory == null || ctx.Tick % Every != 0)
			return false;
		if (current is BossFightSkill or ThrowItemSkill or ResearchSkill or DuplicateSkill || ProgressionPlanner.InDanger(ctx))
			return false;
		foreach (var (type, carried) in ctx.Journey.Researchable()) {
			if (!Ready(ctx, type, carried) || ctx.ResearchRefused.TryGetValue(type, out long at) && ctx.Tick - at < RefusedTicks && ctx.Tick >= at)
				continue;
			if (ResearchOnce(ctx, type, $"{carried} carried, research takes {ctx.Journey.ResearchLeft(type)}") > 0)
				return true;
		}
		return false;
	}

	/// <summary>
	/// Is the item ready for research: the bag holds all research still takes of it, and for gear one more (the copy in use stays);
	/// one more of anything when the duplication menu gives nothing (<see cref="IJourney.DuplicationAllowed"/>). Always false on Classic.
	/// </summary>
	public static bool Ready(BotContext ctx, int itemType, int carried)
	{
		int left = ctx.Journey.ResearchLeft(itemType);
		if (left <= 0)
			return false;
		// The Guide Voodoo Doll is an accessory by its stats (Item.cs:6839-6845), so "gear", but never worn (BotBrain.WearAccessories)
		// nor in use: research takes the one carried (1, Sacrifices.tsv "D") and it is duplicated back the same tick.
		bool inUse = ctx.Inventory.IsGear(itemType) && itemType != ctx.Ids.GuideVoodooDoll;
		// No duplication (a research mod's checklist mode): nothing research takes comes back, so one is always left in the bag.
		return inUse || !ctx.Journey.DuplicationAllowed ? carried > left : carried >= left;
	}

	/// <summary>
	/// The item researched in one go: stack after stack into the research slot (<see cref="IJourney.Research"/>) until it is
	/// researched, logged; then, the bag without a thing it keeps, one stack duplicated back. Returns how many research used (0:
	/// refused). Only for an item <see cref="Ready"/> (the habit), or one the planner chose (<see cref="ResearchSkill"/>).
	/// </summary>
	public static int ResearchOnce(BotContext ctx, int itemType, string why)
	{
		IJourney j = ctx.Journey;
		string name = ctx.Knowledge?.ItemName(itemType) ?? $"item {itemType}";
		int used = 0;
		for (int k = 0; k < MaxStacksAtOnce && !j.IsResearched(itemType); k++) {
			int n = j.Research(itemType);
			if (n <= 0)
				break;
			used += n;
		}
		if (used <= 0) {
			ctx.ResearchRefused[itemType] = ctx.Tick;
			ctx.Log.Debug($"Journey: the research slot refused {name} ({why})");
			return 0;
		}
		bool done = j.IsResearched(itemType);
		ctx.Log.Info($"Journey: researched {used} {name} ({why}): {j.ResearchDone(itemType)}/{j.ResearchNeeded(itemType)}"
			+ (done ? ", researched: duplicable from now on" : $", {j.ResearchLeft(itemType)} to go (the rest would not go in)") + $"; {ctx.Inventory.Count(itemType)} left in the bag");
		if (done && ctx.Inventory.Count(itemType) <= 0 && BotBrain.KeptItems(ctx, ctx.Inventory.Stacks()).Contains(itemType))
			DuplicateOnce(ctx, itemType, "back into the bag, which keeps it");
		return used;
	}

	/// <summary>One click on the item in the duplication menu (<see cref="IJourney.Duplicate"/>), logged. Returns how many came.</summary>
	public static int DuplicateOnce(BotContext ctx, int itemType, string why)
	{
		int got = ctx.Journey.Duplicate(itemType);
		if (got > 0)
			ctx.Log.Info($"Journey: duplicated {got} {ctx.Knowledge?.ItemName(itemType) ?? $"item {itemType}"} ({why}); {ctx.Inventory.Count(itemType)} carried");
		return got;
	}

	/// <summary>
	/// The user's TerraBot setting "Journey: trash researched items the bot has no use for" (<see cref="BotContext.TrashResearched"/>):
	/// every stack (a pass a second, BotBrain.HabitEvery ticks) of an item researched to its cap that the bag does not keep
	/// (<see cref="BotBrain.KeptItems"/>: gear, the plan's items and their makings), trashed as a ctrl-click does; what is needed
	/// later is duplicated. So what is picked up already researched goes within the second, as the user has it with Upgraded
	/// Research's auto-trash, but not what the bot uses (wood, torches, arrows stay in the bag). Never coins, nor the stone and dirt the paths build with; another placeable (sand, mud, furniture)
	/// only while stone or dirt is carried, so the bag never goes without blocks. Not with the duplication menu off (nothing
	/// would come back), not in a boss fight, a throw or a research. Returns how many stacks it trashed.
	/// </summary>
	public static int TrashResearched(BotContext ctx, ISkill current)
	{
		IJourney j = ctx.Journey;
		if (!ctx.TrashResearched || j == null || !j.Enabled || !j.DuplicationAllowed || ctx.Inventory == null || ctx.Crafter == null || ctx.Knowledge == null)
			return 0;
		if (current is BossFightSkill or ThrowItemSkill or ResearchSkill or DuplicateSkill)
			return 0;
		int trashed = 0;
		var stacks = ctx.Inventory.Stacks();
		System.Collections.Generic.HashSet<int> kept = BotBrain.KeptItems(ctx, stacks);
		bool blocks = System.Array.Exists(ctx.Ids.BuildingBlocks, b => ctx.Inventory.Count(b) > 0);
		foreach (int type in System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(stacks, s => s.Type))) {
			if (type <= 0 || kept.Contains(type) || System.Array.IndexOf(ctx.Ids.BuildingBlocks, type) >= 0 || System.Array.IndexOf(ctx.Ids.Coins, type) >= 0
				|| !j.IsResearched(type) || ctx.Knowledge.TilePlacedBy(type) >= 0 && !blocks)
				continue;
			int n = ctx.Inventory.Count(type);
			// Stack after stack (the quick trash takes the smallest), at most the bag's slots.
			for (int k = 0; k < MaxStacksAtOnce && ctx.Inventory.Count(type) > 0 && ctx.Crafter.TryTrash(type); k++)
				trashed++;
			if (ctx.Inventory.Count(type) < n)
				ctx.Log.Info($"Journey: trashed {n - ctx.Inventory.Count(type)} {ctx.Knowledge.ItemName(type)} (researched, nothing needs it)");
		}
		return trashed;
	}
}
