using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

public class PhysicsTests
{
	private readonly ITestOutputHelper output;

	public PhysicsTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void FullJumpRiseMatchesVanillaArithmetic()
	{
		// 16 ticks at -(5.01 - 0.4) = -4.61 px, then -4.21, -3.81 ... -0.21: 73.76 + 24.31 = 98.07 px.
		float rise = PlayerPhysics.ApexRise(TestUtil.P, TestUtil.P.JumpHeight + 1);
		output.WriteLine($"full jump rise {rise} px = {rise / 16f} tiles");
		Assert.InRange(rise, 97.9f, 98.2f);
	}

	[Fact]
	public void HoldingRightOnGroundReachesMaxRunSpeed()
	{
		var s = new BodyState { ReleaseJump = true };
		for (int t = 0; t < 60; t++) {
			PlayerPhysics.Horizontal(ref s, new MoveInput { Right = true }, TestUtil.P);
			s.VY = 0f; // standing on ground
		}
		Assert.InRange(s.VX, 2.8f, 3.1f);
	}

	[Fact]
	public void JumpDoesNotRetriggerWhileHeld()
	{
		var s = new BodyState { ReleaseJump = true };
		var hold = new MoveInput { Jump = true };
		PlayerPhysics.JumpLogic(ref s, hold, TestUtil.P);
		Assert.Equal(-TestUtil.P.JumpSpeed, s.VY);
		s = new BodyState { ReleaseJump = false }; // grounded but jump never released
		PlayerPhysics.JumpLogic(ref s, hold, TestUtil.P);
		Assert.Equal(0f, s.VY);
	}

	[Fact]
	public void JumpLibraryReachMatchesPhysics()
	{
		JumpLibrary lib = JumpLibrary.Build(TestUtil.P);
		int maxUp = -lib.Entries.Min(e => e.Dy);
		int maxFlat = lib.Entries.Where(e => e.Dy == 0).Max(e => e.Dx);
		output.WriteLine($"entries {lib.Entries.Count}, max rise {maxUp} tiles, max same-level dx {maxFlat}");
		Assert.Equal(6, maxUp); // 98 px rise: lands on a ledge 6 tiles up, never 7
		Assert.InRange(maxFlat, 4, 6);
		Assert.All(lib.Entries, e => Assert.All(e.Variants, v => Assert.True(v.Ticks > 0)));
	}

	[Fact]
	public void NodeMappingRoundTrips()
	{
		var n = new TilePos(123, 456);
		TilePos back = Body.NodeOf(Body.CanonicalLeft(n.X, TestUtil.P), Body.StandingTop(n.Y, TestUtil.P), TestUtil.P);
		Assert.Equal(n, back);
		// Standing on a half block (bottom 8 px lower) still maps to the full-block node above it.
		Assert.Equal(new TilePos(5, 9), Body.NodeOf(Body.CanonicalLeft(5, TestUtil.P), Body.StandingTop(9, TestUtil.P) + 8f, TestUtil.P));
	}

	[Fact]
	public void TilePosKeyRoundTripsNegatives()
	{
		foreach (var p in new[] { new TilePos(0, 0), new TilePos(-5, 7), new TilePos(3, -9), new TilePos(-100000, -1) })
			Assert.Equal(p, TilePos.FromKey(p.Key));
	}
}
