using TerraBot.Core.Grid;

namespace TerraBot.Core.Pathfinding;

/// <summary>
/// A pre-simulated jump from a node at rest to the node (Dx, Dy) relative to it. Produced by
/// <see cref="JumpLibrary"/> by running <see cref="Physics.PlayerPhysics"/> with exactly the controller the
/// executor replays: hold jump for <see cref="HoldTicks"/>, press nothing horizontally for
/// <see cref="DelayTicks"/>, then arrive-steer onto the destination column.
/// </summary>
public sealed class JumpTemplate
{
	public int Dx { get; init; }
	public int Dy { get; init; }
	public int HoldTicks { get; init; }
	public int DelayTicks { get; init; }
	/// <summary>Running jump: tiles sprinted along the start row before take-off (0 = standing jump). The run-up
	/// nodes must be standable; the bot still starts at rest on the start node.</summary>
	public int RunupTiles { get; init; }

	/// <summary>Ticks from pressing jump to touching down (worst case over the simulated start offsets).</summary>
	public int Ticks { get; init; }

	/// <summary>Planner cost in ticks: <see cref="Ticks"/> plus the stop-and-align overhead before take-off.</summary>
	public float Cost { get; init; }

	/// <summary>Cells (relative to the start node) the hitbox overlaps at any time. Must all be passable.</summary>
	public TilePos[] BodyCells { get; init; }

	/// <summary>Cells whose top edge the feet cross while descending before the landing. Must not be standable,
	/// otherwise the player would land early (platforms) or clip a corner.</summary>
	public TilePos[] CrossCells { get; init; }

	public string Variant => RunupTiles > 0 ? $"run{RunupTiles} h{HoldTicks}" : $"h{HoldTicks} d{DelayTicks}";

	private readonly struct MaskRow
	{
		public readonly int Dy, X0, Width;
		public readonly ulong Bits;
		public readonly GridMask.Kind Kind;
		public MaskRow(int dy, int x0, int width, ulong bits, GridMask.Kind kind) { Dy = dy; X0 = x0; Width = width; Bits = bits; Kind = kind; }
	}

	private MaskRow[] maskRows;

	/// <summary>
	/// As <see cref="Fits(ITileGrid, TilePos)"/>, a row of cells at a time against <paramref name="m"/>: the body cells as
	/// not-headroom (the start feet as not-passable), the cross cells as stand-on, the run-up floor as stand-on.
	/// </summary>
	public bool Fits(GridMask m, TilePos from)
	{
		maskRows ??= Compile();
		foreach (MaskRow r in maskRows) {
			ulong bits = m.Bits(r.Kind, from.X + r.X0, from.Y + r.Dy, r.Width);
			if ((bits & r.Bits) != 0)
				return false;
		}
		int dir = System.Math.Sign(Dx);
		for (int i = 1; i <= RunupTiles; i++)
			if (m.Bits(GridMask.Kind.StandOn, from.X + dir * i, from.Y + 1, Body.WidthTiles) == 0)
				return false;
		return true;
	}

	private MaskRow[] Compile()
	{
		var rows = new System.Collections.Generic.List<MaskRow>();
		void Add(System.Collections.Generic.IEnumerable<TilePos> cells, GridMask.Kind kind)
		{
			foreach (var g in System.Linq.Enumerable.GroupBy(cells, c => c.Y)) {
				int x0 = System.Linq.Enumerable.Min(g, c => c.X), x1 = System.Linq.Enumerable.Max(g, c => c.X);
				ulong bits = 0;
				foreach (TilePos c in g)
					bits |= 1UL << (c.X - x0);
				rows.Add(new MaskRow(g.Key, x0, x1 - x0 + 1, bits, kind));
			}
		}
		bool StartFeet(TilePos c) => c.Y == 0 && (c.X == 0 || c.X == 1);
		// The rows near the start first: a jump from under a low ceiling fails on its first rows.
		Add(System.Linq.Enumerable.Where(BodyCells, c => !StartFeet(c)), GridMask.Kind.NotHeadroom);
		Add(System.Linq.Enumerable.Where(BodyCells, StartFeet), GridMask.Kind.NotPassable);
		Add(CrossCells, GridMask.Kind.StandOn);
		rows.Sort((a, b) => System.Math.Abs(a.Dy).CompareTo(System.Math.Abs(b.Dy)));
		return rows.ToArray();
	}

	public bool Fits(ITileGrid g, TilePos from)
	{
		// A slope or half block is open only where the feet stand at take-off: anywhere else on the way it is in the
		// way (seen in game: three jumps through slopes landed a tile short, in the same places every run).
		foreach (TilePos c in BodyCells) {
			CellKind k = g.Get(from.X + c.X, from.Y + c.Y);
			bool startFeet = c.Y == 0 && (c.X == 0 || c.X == 1);
			if (startFeet ? !k.IsPassable() : !k.IsHeadroom())
				return false;
		}
		foreach (TilePos c in CrossCells)
			if (g.Get(from.X + c.X, from.Y + c.Y).CanStandOn())
				return false;
		// Running jumps need floor under every run-up node (the body cells above are already in BodyCells).
		int dir = System.Math.Sign(Dx);
		for (int i = 1; i <= RunupTiles; i++)
			if (!Body.HasSupport(g, from.X + dir * i, from.Y))
				return false;
		return true;
	}
}
