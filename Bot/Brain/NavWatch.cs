using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Brain;

/// <summary>
/// Wasted movement, measured so the navigation overhaul is judged by numbers (the user, 2026-10-18: "running back and forth 20 million
/// times up the same stone stairs towards the big trees only to go back down again ... I want you to monitor and fix until it's
/// perfect economical movement"). Logged as "Nav waste: ..." with the goal and the job, and counted by the self-test:
/// <list type="bullet">
/// <item>up and back: the body rises <see cref="ExcursionRows"/> rows or more and comes back down to within <see cref="BackRows"/>
/// rows of where it started, within <see cref="ExcursionTicks"/> and <see cref="ExcursionColumns"/> columns of it (the same for
/// down and back up);</item>
/// <item>a revisit: the same area (<see cref="AreaTiles"/> square) entered <see cref="RevisitCount"/> times within
/// <see cref="RevisitTicks"/>;</item>
/// <item>a plan loop: the same decision (numbers left out) chosen <see cref="DecisionCount"/> times within <see cref="DecisionTicks"/>.</item>
/// </list>
/// </summary>
public sealed class NavWatch
{
	public const int SampleTicks = 30;
	public const int ExcursionRows = 10, BackRows = 3, ExcursionColumns = 25;
	public const int ExcursionTicks = 3 * 3600;
	public const int AreaTiles = 16, RevisitCount = 4, RevisitTicks = 3600;
	public const int DecisionCount = 3, DecisionTicks = 3 * 3600;

	public int UpAndBack { get; private set; }
	public int DownAndBack { get; private set; }
	public int Revisits { get; private set; }
	public int PlanLoops { get; private set; }
	/// <summary>Tiles moved (sample to sample, Chebyshev) and the most recent stretch's start, for the walked/net report.</summary>
	public long Walked { get; private set; }

	private readonly List<(long Tick, TilePos Node)> samples = new();
	private readonly Dictionary<(int, int), List<long>> areaVisits = new();
	private readonly Dictionary<string, List<long>> decisions = new();
	private (int, int)? lastArea;
	private long lastExcursionAt = long.MinValue / 2;
	private long lastSample = long.MinValue / 2;

	/// <summary>Feeds the body's node every <see cref="SampleTicks"/>; returns the waste seen, or null.</summary>
	public string Sample(long tick, TilePos node, string goal, string job)
	{
		if (tick - lastSample < SampleTicks)
			return null;
		lastSample = tick;
		if (samples.Count > 0)
			Walked += samples[^1].Node.ChebyshevTo(node);
		samples.Add((tick, node));
		samples.RemoveAll(s => tick - s.Tick > ExcursionTicks);
		string found = Excursion(tick, node, goal, job);
		var area = (FloorDiv(node.X, AreaTiles), FloorDiv(node.Y, AreaTiles));
		if (lastArea != area) {
			lastArea = area;
			if (!areaVisits.TryGetValue(area, out List<long> visits))
				areaVisits[area] = visits = new List<long>();
			visits.RemoveAll(t => tick - t > RevisitTicks);
			visits.Add(tick);
			if (visits.Count >= RevisitCount) {
				Revisits++;
				visits.Clear();
				found ??= $"Nav waste: back in the area round ({area.Item1 * AreaTiles},{area.Item2 * AreaTiles}) for the {RevisitCount}th time "
					+ $"within {RevisitTicks / 60} s; goal \"{goal}\", job {job}";
			}
		}
		return found;
	}

	private string Excursion(long tick, TilePos node, string goal, string job)
	{
		if (tick - lastExcursionAt < ExcursionTicks / 3)
			return null;
		// From an earlier sample near here (within BackRows and ExcursionColumns), the highest and lowest points since then.
		for (int i = 0; i < samples.Count - 1; i++) {
			TilePos start = samples[i].Node;
			if (Math.Abs(start.Y - node.Y) > BackRows || Math.Abs(start.X - node.X) > ExcursionColumns)
				continue;
			int top = node.Y, bottom = node.Y;
			for (int k = i; k < samples.Count; k++) {
				top = Math.Min(top, samples[k].Node.Y);
				bottom = Math.Max(bottom, samples[k].Node.Y);
			}
			int up = start.Y - top, down = bottom - start.Y;
			if (up < ExcursionRows && down < ExcursionRows)
				continue;
			lastExcursionAt = tick;
			samples.RemoveRange(0, i + 1);
			bool rose = up >= down;
			if (rose)
				UpAndBack++;
			else
				DownAndBack++;
			return $"Nav waste: {(rose ? $"up {up} rows and back down" : $"down {down} rows and back up")} from {start} to {node} "
				+ $"in {(tick - samples.FirstOrDefault().Tick) / 60} s; goal \"{goal}\", job {job}";
		}
		return null;
	}

	/// <summary>Feeds a decision of the planner (its status line); returns a plan loop, or null.</summary>
	public string Decision(long tick, string status, string goal)
	{
		if (string.IsNullOrEmpty(status))
			return null;
		string key = Regex.Replace(status, @"-?\d+", "#");
		if (!decisions.TryGetValue(key, out List<long> times))
			decisions[key] = times = new List<long>();
		times.RemoveAll(t => tick - t > DecisionTicks);
		times.Add(tick);
		if (times.Count < DecisionCount)
			return null;
		PlanLoops++;
		times.Clear();
		return $"Nav waste: the same decision {DecisionCount} times within {DecisionTicks / 3600} game hours: \"{status}\"; goal \"{goal}\"";
	}

	private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
