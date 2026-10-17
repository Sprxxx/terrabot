using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;

namespace TerraBot.Tests;

/// <summary>
/// Journey Mode for <see cref="FakeGame"/>: research caps (vanilla Sacrifices.tsv, CreativeItemSacrificesCatalog's letter codes:
/// D 1, H 10, B 25, J 30, K 99, L 100), research counts, and a duplication store that gives a full stack a click, as the
/// game's shift-click on the duplication menu does. Research takes one bag stack a call, at most what research still needs
/// (CreativeUI.SacrificeItem). Off unless <see cref="On"/>.
/// </summary>
public sealed class FakeJourney : IJourney
{
	private readonly FakeGame game;
	public FakeJourney(FakeGame game) => this.game = game;

	public bool On;
	/// <summary>Research caps by item; unlisted items cannot be researched.</summary>
	public readonly Dictionary<int, int> Caps = new() {
		[FakeGame.Wood] = 100, [FakeGame.StoneBlock] = 100, [FakeGame.DirtBlock] = 100, [FakeGame.IronOre] = 100, [FakeGame.Torch] = 100,
		[FakeGame.Gel] = 99, [FakeGame.WoodenArrow] = 99, [FakeGame.IronBar] = 25, [FakeGame.Lens] = 25, [FakeGame.HealingPotion] = 30,
		[FakeGame.LifeCrystal] = 10, [FakeGame.SuspiciousEye] = 3,
		[FakeGame.WorkBench] = 1, [FakeGame.Furnace] = 1, [FakeGame.IronAnvil] = 1, [FakeGame.WoodenSword] = 1, [FakeGame.IronPickaxe] = 1,
		[FakeGame.GuideVoodooDoll] = 1, [FakeGame.Chair] = 1, [FakeGame.Table] = 1, [FakeGame.Door] = 1,
	};
	public readonly Dictionary<int, int> Done = new();
	/// <summary>The item in hand mid-swing: the research slot refuses it.</summary>
	public int InUse;
	public readonly List<(int Type, int Used)> Researched = new();
	public readonly List<(int Type, int Got)> Duplicated = new();

	/// <summary>Mark items researched to their caps (a character that has played before).</summary>
	public void MarkResearched(params int[] types)
	{
		foreach (int t in types)
			Done[t] = Caps[t];
	}

	public bool Enabled => On;
	public int ResearchNeeded(int itemType) => Caps.TryGetValue(itemType, out int c) ? c : 0;
	public int ResearchDone(int itemType) => Done.GetValueOrDefault(itemType);
	public bool IsResearched(int itemType) => On && ResearchNeeded(itemType) > 0 && ResearchDone(itemType) >= ResearchNeeded(itemType);

	public IReadOnlyList<(int Type, int Carried)> Researchable() => !On ? Array.Empty<(int, int)>()
		: game.Items.Where(kv => kv.Value > 0 && ResearchNeeded(kv.Key) > 0 && !IsResearched(kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();

	public int Research(int itemType)
	{
		if (!On || IsResearched(itemType) || ResearchNeeded(itemType) <= 0 || itemType == InUse || game.Count(itemType) <= 0)
			return 0;
		int left = ResearchNeeded(itemType) - ResearchDone(itemType);
		var stacks = game.Stacks().Where(s => s.Type == itemType).Select(s => s.Stack).ToList();
		int stack = stacks.Where(s => s >= left).DefaultIfEmpty(-1).Min();
		if (stack < 0)
			stack = stacks.Max();
		int used = Math.Min(left, stack);
		game.Items[itemType] -= used;
		Done[itemType] = ResearchDone(itemType) + used;
		Researched.Add((itemType, used));
		return used;
	}

	/// <summary>Upgraded Research's settings, as its adapter (UpgradedResearchCompat) answers them; off: the vanilla answers.</summary>
	public bool ChecklistMode;
	public bool AutoTrash;
	public readonly HashSet<int> StationsByResearch = new();
	public bool DuplicationAllowed => !ChecklistMode;
	public bool PickupVanishes(int itemType) => On && AutoTrash && !ChecklistMode && IsResearched(itemType) && game.Count(itemType) <= 0;
	public bool StationAtHand(int tileType) => On && StationsByResearch.Contains(tileType);

	public int Duplicate(int itemType)
	{
		if (!DuplicationAllowed || !IsResearched(itemType) || !game.HasRoomFor(itemType))
			return 0;
		int got = game.StackOf(itemType);
		game.Items[itemType] = game.Count(itemType) + got;
		Duplicated.Add((itemType, got));
		return got;
	}
}
