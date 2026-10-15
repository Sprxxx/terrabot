using System.Collections.Generic;

namespace TerraBot.Core.Grid;

/// <summary>
/// A grid with edits laid over another: the world as it will be once a plan's remaining steps have mined and placed
/// their blocks, for planning the next segment from that plan's end while the bot still walks it.
/// </summary>
public sealed class OverlayGrid : ITileGrid
{
	private readonly Dictionary<long, CellKind> edits = new();
	public ITileGrid Base { get; private set; }

	public void Reset(ITileGrid baseGrid)
	{
		Base = baseGrid;
		edits.Clear();
	}

	public int Count => edits.Count;

	public void Set(int x, int y, CellKind k) => edits[((long)x << 32) | (uint)y] = k;

	public CellKind Get(int x, int y) =>
		edits.Count > 0 && edits.TryGetValue(((long)x << 32) | (uint)y, out CellKind k) ? k : Base.Get(x, y);
}
