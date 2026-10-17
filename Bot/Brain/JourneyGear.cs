using System.Collections.Generic;
using System.Linq;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Journey Mode: the best gear researched is the bot's to have, a click in the duplication menu away (vanilla
/// ItemsSacrificedUnlocksTracker, the menu's shift-click: <see cref="IJourney.Duplicate"/>). Each pass, the one best upgrade over what
/// is worn and carried is duplicated into the bag, in this order: an armour piece with more defence for its slot, an accessory worth
/// more than the worst worn one (or any, with a slot free), a bow, a melee weapon, a pickaxe, an axe with more power; the habits that
/// wear and stock the hotbar then take it (<see cref="BotBrain"/>.WearBestArmour, WearAccessories, the game's StockHotbar).
/// The user (2026-10-17): "make sure Journey mode is considered and that we research and use everything". Not with the duplication
/// menu off (a research mod's checklist mode), and never a floating accessory or the Guide Voodoo Doll.
/// </summary>
public static class JourneyGear
{
	/// <summary>Duplicates the best upgrade researched, if any; true when one came into the bag.</summary>
	public static bool Tick(BotContext ctx)
	{
		IJourney j = ctx.Journey;
		IInventory inv = ctx.Inventory;
		if (j == null || !j.Enabled || !j.DuplicationAllowed || inv == null || ctx.Knowledge == null)
			return false;
		IReadOnlyList<int> researched = j.ResearchedItems();
		if (researched.Count == 0)
			return false;
		bool Usable(int t) => t > 0 && inv.Count(t) <= 0 && !inv.IsEquipped(t) && t != ctx.Ids.GuideVoodooDoll
			&& System.Array.IndexOf(ctx.Ids.FloatingAccessories, t) < 0;
		var candidates = researched.Where(Usable).ToList();
		if (candidates.Count == 0)
			return false;
		var carried = inv.Stacks().Select(s => s.Type).Distinct().ToList();

		foreach (ArmorSlot slot in new[] { ArmorSlot.Head, ArmorSlot.Body, ArmorSlot.Legs }) {
			int worn = inv.Worn(slot);
			int have = worn >= 0 ? inv.ArmorDefense(worn) : 0;
			foreach (int t in carried)
				if (inv.ArmorSlotOf(t) == slot)
					have = System.Math.Max(have, inv.ArmorDefense(t));
			int best = Best(candidates, t => inv.ArmorSlotOf(t) == slot ? inv.ArmorDefense(t) : 0, have);
			if (best > 0)
				return Duplicate(ctx, best, $"defence {inv.ArmorDefense(best)} over {have}");
		}

		var wornAcc = inv.WornAccessories().Select(w => inv.AccessoryScore(w, worn: true)).OrderBy(s => s).ToList();
		float carriedAcc = inv.UnwornAccessories().Select(a => inv.AccessoryScore(a, worn: false)).DefaultIfEmpty(0f).Max();
		float bar = wornAcc.Count < inv.AccessorySlots ? 0f : wornAcc[0] + BotBrain.SwapMargin;
		bar = System.Math.Max(bar, carriedAcc); // a carried one at least as good goes on first
		int acc = Best(candidates, t => inv.AccessoryScore(t, worn: false), bar);
		if (acc > 0)
			return Duplicate(ctx, acc, $"worth {inv.AccessoryScore(acc, worn: false):0.#} as an accessory");

		foreach (var (score, what) in new (System.Func<int, float>, string)[] {
			(inv.RangedScore, "bow"), (inv.MeleeScore, "melee weapon"),
			(t => inv.ToolPower(t, ToolKind.Pickaxe), "pickaxe"), (t => inv.ToolPower(t, ToolKind.Axe), "axe"),
		}) {
			float have = carried.Select(score).DefaultIfEmpty(0f).Max();
			int best = Best(candidates, score, have);
			if (best > 0)
				return Duplicate(ctx, best, $"a better {what} ({score(best):0.#} over {have:0.#})");
		}
		return false;
	}

	private static int Best(IReadOnlyList<int> candidates, System.Func<int, float> score, float above)
	{
		int best = -1;
		float bestScore = above;
		foreach (int t in candidates) {
			float s = score(t);
			if (s > bestScore) {
				best = t;
				bestScore = s;
			}
		}
		return best;
	}

	private static bool Duplicate(BotContext ctx, int item, string why)
	{
		int got = ctx.Journey.Duplicate(item);
		if (got > 0)
			ctx.Log.Info($"Journey: duplicated {ctx.Knowledge.ItemName(item)} to use it ({why})");
		return got > 0;
	}
}
