using System;
using System.Collections.Generic;
using TerraBot.Core.Grid;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The bot's own mine, kept on the context across jobs, deaths and trips (Baritone mines at the ore's level along
/// tunnels; a player keeps one mine and goes back to it): a staircase down from an entrance, one tile across per row so
/// it is walked both ways, and a tunnel every <see cref="TunnelSpacing"/> rows, <see cref="TunnelLength"/> tiles each
/// way, lit as it goes (LightKeeper). Exploring with no cave in view digs on in it instead of 20-25 tile legs from
/// wherever the bot stood. Seen in game (measured run R1): 126 such legs against 25 into caves, exploring 13% of the run,
/// each leg planned afresh and none of them kept.
/// </summary>
public sealed class BranchMine
{
	/// <summary>
	/// Stair legs go 2 down and 2 across: never more than 2 rows straight down in one place, so the way back up is jumps
	/// and step-ups, not a shaft to pillar out of.
	/// </summary>
	public const int TunnelSpacing = 12, TunnelLength = 40, StairLeg = 2, TunnelLeg = 20;
	public bool StairDone { get; private set; }
	/// <summary>Farther than this from the bot (tiles), a mine is left for a new one where the bot is.</summary>
	public const int AbandonTiles = 150;

	public TilePos Entrance { get; }
	/// <summary>Which way the staircase goes down (+1 right, -1 left).</summary>
	public int Dir { get; }
	/// <summary>The lowest stair node dug so far.</summary>
	public TilePos StairEnd { get; private set; }
	public List<Tunnel> Tunnels { get; } = new();

	public sealed class Tunnel
	{
		public int Row;
		public int OriginX, Left, Right;
		public bool LeftDone, RightDone;
		public bool Done => LeftDone && RightDone;
	}

	public BranchMine(TilePos entrance, int dir)
	{
		Entrance = entrance;
		Dir = dir == 0 ? 1 : Math.Sign(dir);
		StairEnd = entrance;
	}

	/// <summary>The next spot to dig to: the open end of an unfinished tunnel, else the staircase on down; null when the mine is done (deepest allowed reached).</summary>
	public TilePos? NextTarget(int deepestRow, out string what)
	{
		foreach (Tunnel t in Tunnels) {
			if (!t.RightDone) {
				what = $"the mine's tunnel at row {t.Row}, east";
				return new TilePos(Math.Min(t.Right + TunnelLeg, t.OriginX + TunnelLength), t.Row);
			}
			if (!t.LeftDone) {
				what = $"the mine's tunnel at row {t.Row}, west";
				return new TilePos(Math.Max(t.Left - TunnelLeg, t.OriginX - TunnelLength), t.Row);
			}
		}
		int down = Math.Min(StairLeg, deepestRow - StairEnd.Y);
		if (down <= 0 || StairDone) {
			what = "the mine is as deep as the gear allows";
			return null;
		}
		what = "the mine's staircase down";
		return new TilePos(StairEnd.X + Dir * down, StairEnd.Y + down);
	}

	/// <summary>The bot got to <paramref name="at"/> on a leg for <paramref name="target"/>: the mine's extent grows.</summary>
	public void Reached(TilePos at, TilePos target)
	{
		foreach (Tunnel t in Tunnels) {
			if (at.Y != t.Row || target.Y != t.Row)
				continue;
			if (target.X > t.OriginX) {
				t.Right = Math.Max(t.Right, at.X);
				if (t.Right >= t.OriginX + TunnelLength - 2 || at.X < target.X - 3)
					t.RightDone = true; // as long as planned, or blocked short of it
			}
			else {
				t.Left = Math.Min(t.Left, at.X);
				if (t.Left <= t.OriginX - TunnelLength + 2 || at.X > target.X + 3)
					t.LeftDone = true;
			}
			return;
		}
		if (at.Y > StairEnd.Y) {
			StairEnd = at;
			// A new tunnel every TunnelSpacing rows below the entrance.
			if ((at.Y - Entrance.Y) / TunnelSpacing > (Tunnels.Count > 0 ? (Tunnels[^1].Row - Entrance.Y) / TunnelSpacing : 0))
				Tunnels.Add(new Tunnel { Row = at.Y, OriginX = at.X, Left = at.X, Right = at.X });
		}
	}

	/// <summary>A leg to <paramref name="target"/> could not be made: that way is finished (rock too hard, lava, off limits).</summary>
	public void Failed(TilePos target)
	{
		foreach (Tunnel t in Tunnels)
			if (target.Y == t.Row) {
				if (target.X > t.OriginX)
					t.RightDone = true;
				else
					t.LeftDone = true;
				return;
			}
		StairDone = true; // no deeper this way
	}

	public override string ToString() => $"mine from {Entrance} going {(Dir > 0 ? "east" : "west")}, stair at {StairEnd}, {Tunnels.Count} tunnel(s)";
}
