using System;
using System.Collections.Generic;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// <see cref="FallArrest"/>, the reflex that stops a fall that would hurt (measured run M10d: thrown down a corruption chasm's
/// shaft, 94 rows, 685 damage, dead, with 1502 dirt blocks in the bag).
/// </summary>
public class FallArrestTests
{
	private readonly ITestOutputHelper output;

	public FallArrestTests(ITestOutputHelper output) => this.output = output;

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m);
		public void Debug(string m) { }
	}

	/// <summary>Ground at row 10 with a shaft at columns <paramref name="x0"/>..<paramref name="x1"/>, <paramref name="depth"/> rows deep (or water).</summary>
	private static (ArrayTileGrid, TestBlockActions) Shaft(int x0, int x1, int depth, int botX, int width = 30, int waterFrom = int.MaxValue)
	{
		var rows = new List<string>();
		for (int y = 0; y <= 10 + depth + 1; y++) {
			var r = new char[width];
			for (int x = 0; x < width; x++)
				r[x] = y < 10 ? '.' : y < 10 + depth && x >= x0 && x <= x1 ? (y >= waterFrom ? '~' : '.') : '#';
			if (y == 9)
				r[botX] = 'S';
			rows.Add(new string(r));
		}
		return TestBlockActions.Parse(rows.ToArray());
	}

	/// <summary>Runs the body with the given keys each tick, the reflex after them; the worst fall in rows, as vanilla counts it.</summary>
	private (int worst, FallArrest arrest, SimWorld sim, bool everActive) Run(ArrayTileGrid g, TestBlockActions b, Action<SimWorld> start, Func<int, MoveInput> keys, int ticks = 240)
	{
		var sim = new SimWorld(g, PhysicsParams.Default, g.Markers['S'], b);
		var ctx = new BotContext { Grid = g, Log = new Log(output), Blocks = b, Body = sim.State };
		int fallStart = (int)(sim.State.Y / 16f), worst = 0;
		start(sim);
		var arrest = new FallArrest();
		bool everActive = false;
		for (int t = 0; t < ticks; t++) {
			ctx.Tick = t;
			ctx.Body = sim.State;
			ctx.Output = keys(t);
			arrest.Tick(ctx);
			everActive |= arrest.Active;
			bool wasDown = sim.State.VY == 0f;
			sim.Step(ctx.Output);
			int row = (int)(sim.State.Y / 16f);
			if (sim.State.VY == 0f) {
				if (!wasDown)
					worst = Math.Max(worst, row - fallStart);
				fallStart = row;
			}
			else if (sim.State.Jump > 0)
				fallStart = row;
		}
		output.WriteLine($"at {sim.Node}, worst fall {worst} rows, {sim.Placed} placed, {b.BlockCount} blocks left");
		return (worst, arrest, sim, everActive);
	}

	/// <summary>Steps off the lip into the shaft at columns 12-13: over its middle, coming down.</summary>
	private static void OverTheShaft(SimWorld s)
	{
		s.State.X = 12 * 16f + 6f;
		s.State.Y = 10 * 16f - 42f;
		s.State.VX = 0.5f;
		s.State.VY = 0.4f;
	}

	[Fact]
	public void SteppedOffIntoADeepShaft_ABlockIsPlacedAgainstItsWall_AndLandedOn_WithinASafeFall()
	{
		// A 2-wide shaft 90 rows deep, walls both sides (as the M10d shaft at x 1700-1701 between the ledge's column and the
		// bridge's): no ledge to steer to.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 13, 90, botX: 9);
		(int worst, _, SimWorld sim, bool active) = Run(g, b, OverTheShaft, _ => MoveInput.None);
		Assert.True(active);
		Assert.True(sim.Placed >= 1, "no block placed");
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles, $"fell {worst} rows");
		Assert.True(sim.Node.Y < 10 + PhysicsParams.Default.SafeFallTiles, $"down at {sim.Node}");
	}

	[Fact]
	public void ThrownOverAWideChasm_SteersToTheWall_ThenPlacesABlockAgainstIt()
	{
		// A 9-wide chasm, thrown in near its middle, nothing under the body to fix a block to: towards the nearer wall first.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(10, 18, 90, botX: 8);
		(int worst, _, SimWorld sim, _) = Run(g, b, s => {
			s.State.X = 13 * 16f + 6f;
			s.State.Y = 10 * 16f - 42f - 2f;
			s.State.VX = 0f;
			s.State.VY = 1f;
		}, _ => MoveInput.None);
		Assert.True(sim.Placed >= 1, "no block placed");
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles, $"fell {worst} rows");
	}

	[Fact]
	public void NoBlocks_TheFallIsNotArrested_ButSaysWhy()
	{
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 13, 90, botX: 9);
		b.BlockCount = 0;
		(int worst, FallArrest arrest, SimWorld sim, bool active) = Run(g, b, OverTheShaft, _ => MoveInput.None, ticks: 30);
		Assert.True(active);
		Assert.Equal(0, sim.Placed);
		Assert.Contains("no block", arrest.State);
	}

	[Fact]
	public void AJumpOverAPit_ThatLandsOnTheFarLip_IsLeftAlone()
	{
		// Committed plans are not second-guessed: a running jump over a 4-wide pit 60 rows deep lands on its far side.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 15, 60, botX: 6);
		(int worst, _, SimWorld sim, bool active) = Run(g, b, _ => { }, t => new MoveInput { Right = t < 60, Jump = t >= 14 && t < 30 });
		Assert.False(active, "the reflex took over a jump that lands");
		Assert.Equal(0, sim.Placed);
		Assert.True(sim.Node.X >= 15 && sim.Node.Y == 9, $"at {sim.Node}"); // on the far lip (its column 16)
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles);
	}

	[Fact]
	public void AFallIntoDeepWater_IsLeftAlone()
	{
		// Vanilla resets the fall in liquid (Player.Update: wet sets fallStart): nothing to stop, 30 rows down or not.
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 13, 60, botX: 9, waterFrom: 40);
		(_, _, SimWorld sim, bool active) = Run(g, b, OverTheShaft, _ => MoveInput.None, ticks: 60);
		Assert.False(active);
		Assert.Equal(0, sim.Placed);
		Assert.True(sim.Node.Y > 30, $"at {sim.Node}");
	}

	[Fact]
	public void PlannedDrops_AreNeverArrested()
	{
		// A drop as deep as the planner makes them (MoveContext.MaxDrop: a safe fall less 2 rows) lands within a safe fall.
		int maxDrop = PhysicsParams.Default.SafeFallTiles - 2;
		(ArrayTileGrid g, TestBlockActions b) = Shaft(12, 13, maxDrop, botX: 9);
		(int worst, _, SimWorld sim, bool active) = Run(g, b, OverTheShaft, _ => MoveInput.None);
		Assert.False(active);
		Assert.Equal(0, sim.Placed);
		Assert.Equal(10 + maxDrop - 1, sim.Node.Y);
		Assert.True(worst <= PhysicsParams.Default.SafeFallTiles);
	}
}
