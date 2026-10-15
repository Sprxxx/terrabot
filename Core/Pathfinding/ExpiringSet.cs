using System;
using System.Collections;
using System.Collections.Generic;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// A set whose members lapse: each is added with an expiry (ticks on <see cref="Clock"/>), and is no longer in the set
/// after it (AltoClef's forgiving blacklist; Baritone keeps no bans at all). The bot's "unreachable", "never this step
/// again" and "never break/place here" sets were permanent until a rest cleared them: a life crystal fell inside one
/// left hours earlier, and the bot ran past it with no reason given (user report, 2026-10-15).
/// </summary>
public sealed class ExpiringSet<T> : IReadOnlySet<T>
{
	private readonly Dictionary<T, long> until = new();
	private readonly HashSet<T> scratch = new();

	/// <summary>Current time in ticks.</summary>
	public Func<long> Clock { get; set; } = () => 0;
	/// <summary>How long a member stays by default.</summary>
	public long DefaultTicks { get; set; }

	public ExpiringSet(long defaultTicks) => DefaultTicks = defaultTicks;

	public bool Add(T item) => Add(item, DefaultTicks);

	/// <summary>Adds (or extends) the member for <paramref name="ticks"/>; true if it was not in the set.</summary>
	public bool Add(T item, long ticks)
	{
		long now = Clock(), end = now + ticks;
		bool isNew = !until.TryGetValue(item, out long old) || old <= now;
		if (isNew || end > old)
			until[item] = end;
		return isNew;
	}

	public bool Contains(T item) => until.TryGetValue(item, out long end) && end > Clock();

	public bool Remove(T item) => until.Remove(item);

	public void Clear() => until.Clear();

	public int RemoveWhere(Predicate<T> match)
	{
		var gone = new List<T>();
		foreach (T k in until.Keys)
			if (match(k))
				gone.Add(k);
		foreach (T k in gone)
			until.Remove(k);
		return gone.Count;
	}

	private HashSet<T> Live()
	{
		long now = Clock();
		scratch.Clear();
		foreach (var (k, end) in until)
			if (end > now)
				scratch.Add(k);
		return scratch;
	}

	public int Count => Live().Count;
	public IEnumerator<T> GetEnumerator() => new List<T>(Live()).GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	public bool IsProperSubsetOf(IEnumerable<T> other) => Live().IsProperSubsetOf(other);
	public bool IsProperSupersetOf(IEnumerable<T> other) => Live().IsProperSupersetOf(other);
	public bool IsSubsetOf(IEnumerable<T> other) => Live().IsSubsetOf(other);
	public bool IsSupersetOf(IEnumerable<T> other) => Live().IsSupersetOf(other);
	public bool Overlaps(IEnumerable<T> other) => Live().Overlaps(other);
	public bool SetEquals(IEnumerable<T> other) => Live().SetEquals(other);
}
