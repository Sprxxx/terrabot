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
	/// Is the item ready for research: the bag holds all research still takes of it (coins: what a slot holds of it, the rest
	/// next time), gear in use too (it comes back duplicated); one more of anything when the duplication menu gives nothing
	/// (<see cref="IJourney.DuplicationAllowed"/>). Always false on Classic.
	/// </summary>
	public static bool Ready(BotContext ctx, int itemType, int carried)
	{
		int left = ctx.Journey.ResearchLeft(itemType);
		if (left <= 0)
			return false;
		// The Guide Voodoo Doll is an accessory by its stats (Item.cs:6839-6845), so "gear", but never worn (BotBrain.WearAccessories)
		// nor in use: research takes the one carried (1, Sacrifices.tsv "D") and it is duplicated back the same tick.
		// Coins (copper, silver, gold): a stack of 100 turns into one of the next coin (Player.DoCoins, Player.cs:38531-38550), so
		// the bag never holds what research takes of one: they go in as soon as the bag holds 99 (or the rest), in two goes.
		int slot = ctx.Inventory.MaxStack(itemType);
		if (System.Array.IndexOf(ctx.Ids.Coins, itemType) >= 0 && slot <= left)
			return carried >= System.Math.Min(left, slot - 1);
		// No duplication (a research mod's checklist mode): nothing research takes comes back, so one is always left in the bag.
		if (!ctx.Journey.DuplicationAllowed)
			return carried > left;
		// Gear in use too (ammo, a pickaxe, an accessory before it is worn): what research takes comes back from the menu at once
		// (ResearchOnce). The user (2026-10-17): accessories "should research, and THEN equip"; "same thing with ammo and coins".
		return carried >= left;
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
		// Back into the bag unless it is junk (NoUse): seen in game (Journey run J2), the only Magic Mirror and Hook went into
		// research and never came back, and a second mirror was trashed later ("nothing needs it").
		if (done && ctx.Inventory.Count(itemType) <= 0 && !NoUse(ctx, itemType))
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
		foreach (int type in System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(stacks, s => s.Type))) {
			if (!Trashable(ctx, type))
				continue;
			int n = ctx.Inventory.Count(type);
			// Stack after stack (the quick trash takes the smallest), at most the bag's slots.
			for (int k = 0; k < MaxStacksAtOnce && ctx.Inventory.Count(type) > 0 && ctx.Crafter.TryTrash(type); k++)
				trashed++;
			if (ctx.Inventory.Count(type) < n)
				ctx.Log.Info($"Journey: trashed {n - ctx.Inventory.Count(type)} {ctx.Knowledge.ItemName(type)} (researched, nothing needs it)");
		}
		return trashed + TrashSpares(ctx);
	}

	/// <summary>
	/// The user (2026-10-18): "we should really only need one stack of building blocks in the hotbar". Of what is researched (duplicated
	/// again in one click when needed; a researched block is not used up when placed, TerraBotConfig.InfiniteResearchedConsumables),
	/// the bag keeps one stack of one building block (the biggest), one stack of each other item, and one of each thing that does not
	/// stack; the rest is trashed, to leave the slots for what is not researched yet. Coins, and what is not researched, are never
	/// touched. Returns how many stacks it trashed.
	/// </summary>
	public static int TrashSpares(BotContext ctx)
	{
		IJourney j = ctx.Journey;
		if (j == null || !j.Enabled || !j.DuplicationAllowed || ctx.Inventory == null || ctx.Crafter == null || ctx.Knowledge == null)
			return 0;
		var stacks = ctx.Inventory.Stacks();
		bool Researched(int t) => t > 0 && j.IsResearched(t) && System.Array.IndexOf(ctx.Ids.Coins, t) < 0;
		bool Block(int t) => System.Array.IndexOf(ctx.Ids.BuildingBlocks, t) >= 0;
		int keepBlock = -1;
		long keepCount = -1;
		foreach (var g in System.Linq.Enumerable.GroupBy(stacks, s => s.Type))
			if (Researched(g.Key) && Block(g.Key) && System.Linq.Enumerable.Sum(g, s => (long)s.Stack) > keepCount) {
				keepBlock = g.Key;
				keepCount = System.Linq.Enumerable.Sum(g, s => (long)s.Stack);
			}
		int trashed = 0;
		foreach (var g in System.Linq.Enumerable.GroupBy(stacks, s => s.Type)) {
			int type = g.Key;
			if (!Researched(type))
				continue;
			bool otherBlock = Block(type) && type != keepBlock;
			int keep = otherBlock ? 0 : 1, have = System.Linq.Enumerable.Count(g), before = ctx.Inventory.Count(type);
			for (int k = have; k > keep && ctx.Crafter.TryTrash(type); k--)
				trashed++;
			if (ctx.Inventory.Count(type) < before)
				ctx.Log.Info($"Journey: trashed {before - ctx.Inventory.Count(type)} {ctx.Knowledge.ItemName(type)} (researched: "
					+ (otherBlock ? $"one building block is kept, {ctx.Knowledge.ItemName(keepBlock)}" : "one stack is kept") + ")");
		}
		return trashed;
	}

	private static BotContext keptFor;
	private static long keptAt = long.MinValue;
	private static System.Collections.Generic.HashSet<int> keptNow;

	/// <summary>
	/// An item researched to its cap that the bot has no use for (<see cref="NoUse"/>): for the bag's stacks and for a pickup
	/// (the game adapter deletes those as they are picked up).
	/// </summary>
	public static bool Trashable(BotContext ctx, int itemType)
	{
		IJourney j = ctx?.Journey;
		return itemType > 0 && j != null && j.Enabled && j.DuplicationAllowed && ctx.Inventory != null && ctx.Knowledge != null && j.IsResearched(itemType)
			&& NoUse(ctx, itemType);
	}

	/// <summary>
	/// Junk to the bot: not kept by the bag (<see cref="BotBrain.KeptItems"/>, worked out once a tick), not coins, not stone or
	/// dirt, not a thing a slot holds one of (a tool, a mirror, a hook, a weapon, armour, an accessory: the bot may want any of
	/// them, and they are few), another placeable only while stone or dirt is carried.
	/// </summary>
	public static bool NoUse(BotContext ctx, int itemType)
	{
		if (itemType <= 0 || ctx.Inventory == null || ctx.Knowledge == null || System.Array.IndexOf(ctx.Ids.BuildingBlocks, itemType) >= 0
			|| System.Array.IndexOf(ctx.Ids.Coins, itemType) >= 0 || ctx.Inventory.MaxStack(itemType) <= 1)
			return false;
		if (!ReferenceEquals(keptFor, ctx) || keptAt != ctx.Tick || keptNow == null) {
			keptNow = BotBrain.KeptItems(ctx, ctx.Inventory.Stacks());
			keptFor = ctx;
			keptAt = ctx.Tick;
		}
		if (keptNow.Contains(itemType))
			return false;
		return ctx.Knowledge.TilePlacedBy(itemType) < 0 || System.Array.Exists(ctx.Ids.BuildingBlocks, b => ctx.Inventory.Count(b) > 0);
	}
}
