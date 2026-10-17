using System;
using System.Collections.Concurrent;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Core.Execution;

/// <summary>
/// Pillaring up in water: one jump stacks several blocks. Vanilla's water jump (<see cref="PhysicsParams.InWater"/>, moved at half
/// speed by Player.WaterCollision) rises some 2.9 px a tick and peaks about 8 rows up, while a block goes in every useTime (10 ticks
/// for blocks, Item.DefaultToPlaceableTile) wherever it is (Player.PlaceThing_Tiles runs every tick of the swing and places once
/// itemTime is 0): free of the hitbox (Collision.EmptyTile), within reach below the feet ((Y + height) / 16 + tileRangeY - 2,
/// Player.cs:38999) and on a solid neighbour, which the block placed before it is. So the blocks go on top of each other under the
/// rising body: the cell over the top of the stack, not the cell under the body's node. Seen in game (world_eye J5, J3): aiming at
/// the cell under the moving node (Body.NodeOf rounds a row up 7 px early), the place had a window of 6 px, 2 ticks at the water
/// jump's speed, over the floor, and the block was asked for only in it, so one tick went to the slot switch; above it, the cell
/// aimed at was open water a row up with nothing to fix to. No row was gained in jump after jump (vy -5.81 / +5.01, 745 to 737
/// and back) and the bot drowned 5-11 rows under air.
/// </summary>
public static class WaterPillar
{
	/// <summary>Rows each simulated pillar climbs when choosing the hold: about a flooded cave's depth (J5: 12 rows to air).</summary>
	public const int SimRows = 8;
	/// <summary>Ticks after which a simulated pillar counts as stuck.</summary>
	public const int SimMaxTicks = 1200;
	/// <summary>Rows under the feet the stack's top may be looked for: vanilla's reach below the hitbox (tileRangeY 4, less 2).</summary>
	public const int ReachRows = PathExecutor.PlaceRangeRows - 2;

	private static readonly ConcurrentDictionary<(PhysicsParams, int), int> holds = new();

	/// <summary>
	/// How long to hold each water jump (ticks from the press) so the stack keeps up with the body: held too long, the body rises
	/// out of reach of the stack's top and the blocks wait for the fall; too short, each jump gains little. Simulated, not tuned:
	/// the hold with the fewest ticks for <see cref="SimRows"/> rows, averaged over where in the swing the first place falls (the
	/// item timer is not in step with the jump in game).
	/// </summary>
	public static int HoldTicks(PhysicsParams dry, float placeTicks)
	{
		int pt = Math.Max(1, (int)Math.Round(placeTicks));
		return holds.GetOrAdd((dry, pt), k => {
			PhysicsParams wet = k.Item1.InWater();
			int best = wet.JumpHeight + 1;
			long bestTicks = long.MaxValue;
			for (int hold = 1; hold <= wet.JumpHeight + 1; hold++) {
				long sum = 0;
				for (int phase = 0; phase < k.Item2; phase++)
					sum += TicksToRise(wet, hold, k.Item2, phase, SimRows);
				if (sum < bestTicks) {
					bestTicks = sum;
					best = hold;
				}
			}
			return best;
		});
	}

	/// <summary>
	/// Ticks for a water pillar to climb <paramref name="rows"/> rows from standing on a floor, jumping as soon as it lands, holding
	/// each jump <paramref name="hold"/> ticks, a block every <paramref name="placeTicks"/> (the first after
	/// <paramref name="phase"/>) where vanilla takes it; <see cref="SimMaxTicks"/> if it does not get there.
	/// </summary>
	public static int TicksToRise(PhysicsParams wet, int hold, int placeTicks, int phase, int rows)
	{
		const int T = Body.TileSize;
		// The floor's top at y = T: feet row 0, stack cells rows 0, -1, -2, ...
		var s = new BodyState { Y = T - wet.Height, ReleaseJump = true };
		int placed = 0, timer = phase, airTicks = 0;
		for (int t = 1; t <= SimMaxTicks; t++) {
			bool grounded = s.VY == 0f;
			airTicks = grounded ? 0 : airTicks + 1;
			var c = new MoveInput { Jump = grounded ? s.ReleaseJump : airTicks < hold && s.VY < 0f };
			PlayerPhysics.ApplyControls(ref s, c, wet);
			float support = T - placed * T;
			float ny = s.Y + s.VY * PhysicsParams.WaterMoveFactor;
			if (s.VY > 0f && ny + wet.Height >= support) {
				ny = support - wet.Height;
				s.VY = 0f;
			}
			s.Y = ny;
			if (timer > 0)
				timer--;
			int row = -placed;
			if (timer == 0 && Placeable(s, wet, row)) {
				placed++;
				timer = placeTicks;
				if (placed >= rows)
					return t;
			}
		}
		return SimMaxTicks;
	}

	/// <summary>Vanilla's checks on a block for row <paramref name="row"/> under the body: out of the hitbox and in reach.</summary>
	private static bool Placeable(in BodyState s, PhysicsParams p, int row) =>
		MathF.Floor(s.Y) + p.Height <= row * Body.TileSize && row <= (s.Y + p.Height) / Body.TileSize + PathExecutor.PlaceRangeRows - 2f;

	/// <summary>
	/// Where the next block of a pillar under the body goes: over the top of whatever carries it in a column the hitbox is over
	/// (looked for down to <see cref="ReachRows"/> rows under the feet), the higher top of the two, the one nearer the hitbox's centre
	/// on a tie. Null if neither column has anything within reach, or the cell takes no block.
	/// </summary>
	public static TilePos? StackCell(ITileGrid g, in BodyState s, PhysicsParams p, IBlockActions blocks)
	{
		TilePos n = Body.NodeOf(s, p);
		TilePos? best = null;
		float bestOff = float.MaxValue;
		float centre = s.X + p.Width / 2f;
		for (int x = n.X; x <= n.X + Body.WidthTiles - 1; x++) {
			if (s.X >= (x + 1) * Body.TileSize || s.X + p.Width <= x * Body.TileSize)
				continue; // the hitbox is not over this column: a block there would not carry it
			for (int y = n.Y + 1; y <= n.Y + 1 + ReachRows; y++) {
				if (!g.Get(x, y).CanStandOn())
					continue;
				var cell = new TilePos(x, y - 1);
				float off = Math.Abs(centre - (x + 0.5f) * Body.TileSize);
				if (blocks != null && !blocks.CanPlaceAt(cell.X, cell.Y))
					break;
				if (best == null || cell.Y < best.Value.Y || cell.Y == best.Value.Y && off < bestOff) {
					best = cell;
					bestOff = off;
				}
				break;
			}
		}
		return best;
	}
}
