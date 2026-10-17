using System;
using System.Collections.Generic;
using TerraBot.Bot.Brain;

namespace TerraBot.Bot.Progression;

/// <summary>
/// A row of the progression table (docs ERRAND_DESIGN.md section 6): a stage as data. What it is, which stages come before it, what
/// to pack and keep back when its errand leaves base, and which errand runs it. The planner reads the rows; the stage's strategy is
/// not written as planner code. Migration step 1 holds one row (the Underworld's access); the rest follow as their errands do.
/// </summary>
public sealed record StageRow(
	string Id,
	IReadOnlyList<string> After,
	Func<GameIds, IReadOnlyList<Need>> Prerequisites,
	Func<GameIds, IReadOnlyList<Need>> Packing,
	Func<GameIds, IReadOnlyList<Need>> Reserve,
	string Errand);

public static class StageTable
{
	/// <summary>
	/// Access to the Underworld (the Wall of Flesh's S3): a straight shaft from near home. Packed: the fight's arrows
	/// (ProgressionPlanner.WofFightArrows), the blocks the way down takes (<see cref="BridgeBlocks"/>: ledges, the first stretch of the
	/// bridge; the shaft's own cells and the ash give the rest), torches for the shaft. Kept back: the healing potions and a recall
	/// potion. Comes after the evil boss and Skeletron, as a player's guide orders them (the pickaxe and the arrows come from them).
	/// </summary>
	public static readonly StageRow WallOfFlesh = new(
		"boss:WallOfFlesh",
		new[] { "boss:evil", "boss:Skeletron" },
		// Resolved before the packing, at base: a pickaxe for hellstone (the Nightmare or Deathbringer Pickaxe, power 65: the hellstone
		// bricks of the ruined houses on the bridge's row, E6 2026-10-17; the evil boss is fought again for its scales or samples if
		// the bag lacks them, ProgressionPlanner.BossDropOf). The user: "be as prepared as possible for the next step of the game".
		ids => new Need[] {
			new PickaxeAtLeast(HellstonePickPower, Tiers(ids.NightmarePickaxe, ids.DeathbringerPickaxe), "a pickaxe for hellstone"),
		},
		ids => new Need[] {
			new HaveItem(Tiers(ids.WoodenArrow, ids.UnholyArrow), ProgressionPlanner.WofFightArrows, "arrows for the fight"),
			new HaveItem(ids.Torch, ShaftTorches, "torches for the shaft"),
			new BridgeBlocks("bridge blocks"),
		},
		ids => new Need[] {
			new HaveItem(Tiers(ids.HealingPotion, ids.LesserHealingPotion), ProgressionPlanner.WofPotions, "healing potions"),
			new HaveItem(ids.RecallPotion, 1, "a recall potion"),
		},
		"AccessErrand");

	/// <summary>Torches packed for the shaft: one every LightKeeper.PlaceEvery rows or so of some 760 (a measured estimate).</summary>
	public const int ShaftTorches = 40;
	/// <summary>Pick power hellstone and hellstone brick want (Player.cs:55038-55041; a game mechanic).</summary>
	public const int HellstonePickPower = 65;

	public static IReadOnlyList<StageRow> Rows { get; } = new[] { WallOfFlesh };

	private static int[] Tiers(params int[] items)
	{
		var list = new List<int>();
		foreach (int i in items)
			if (i > 0 && !list.Contains(i))
				list.Add(i);
		return list.ToArray();
	}
}
