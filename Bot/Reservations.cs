using System;
using System.Collections.Generic;
using TerraBot.Bot.Brain;

namespace TerraBot.Bot;

/// <summary>
/// What an errand has reserved in the bag (docs ERRAND_DESIGN.md section 7): its packing list and its reserve. The habits that
/// trash, research or craft from the bag (BotBrain.KeepRoom, ResearchHabit, LightKeeper) use only what is spare
/// (<see cref="Spare"/>), so a bridge's blocks are not made into a wall and the fight's arrows are not researched away.
/// </summary>
public sealed class Reservations
{
	private readonly List<(object Owner, IReadOnlyList<int> Accepts, int Count)> lines = new();

	/// <summary>Reserve a need's items: a <see cref="HaveItem"/> (any of its accepted items, its count); other needs reserve nothing.</summary>
	public void Reserve(object owner, Need need)
	{
		switch (need) {
			case HaveItem h:
				lines.Add((owner, h.Accepts, h.Count));
				break;
			case PrepFor pf:
				Reserve(owner, pf.Inner);
				break;
			case FromBag fb:
				Reserve(owner, fb.Inner);
				break;
		}
	}

	public void Reserve(object owner, int item, int count)
	{
		if (item > 0 && count > 0)
			lines.Add((owner, new[] { item }, count));
	}

	public void Release(object owner) => lines.RemoveAll(l => ReferenceEquals(l.Owner, owner));

	public void Clear() => lines.Clear();

	public int Count => lines.Count;

	/// <summary>How many of the item are reserved (a line accepting several items reserves its count of each: conservative).</summary>
	public int Reserved(int item)
	{
		int n = 0;
		foreach (var l in lines)
			for (int i = 0; i < l.Accepts.Count; i++)
				if (l.Accepts[i] == item) {
					n += l.Count;
					break;
				}
		return n;
	}

	public bool IsReserved(int item) => Reserved(item) > 0;

	/// <summary>What is carried over the reservation (never negative).</summary>
	public int Spare(IInventory inventory, int item) => Math.Max(0, inventory.Count(item) - Reserved(item));

	public IEnumerable<int> Items()
	{
		var seen = new HashSet<int>();
		foreach (var l in lines)
			foreach (int a in l.Accepts)
				if (seen.Add(a))
					yield return a;
	}
}
