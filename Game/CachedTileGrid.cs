using TerraBot.Core.Grid;
using Terraria;
using Terraria.ModLoader;

namespace TerraBot.Game;

/// <summary>
/// The world as the planner sees it, read once into an array (Baritone keeps a cached copy of the world for the same
/// reason): a cell read is an array index, not a walk through Main.tile and the tile tables. Chunks of 16x16 are
/// filled on first use and again once older than <see cref="MaxAgeTicks"/> (liquids flow, NPCs and explosions change
/// tiles); any tile change the game frames (mining, placing, sand landing: <see cref="Invalidator"/>) marks its
/// chunks stale at once, so the bot's own digging shows on the next read.
/// </summary>
public sealed class CachedTileGrid : ITileGrid
{
	public static readonly CachedTileGrid Instance = new();

	public const int ChunkShift = 4;
	public const int MaxAgeTicks = 60;

	private CellKind[] cells = System.Array.Empty<CellKind>();
	private uint[] stamp = System.Array.Empty<uint>();
	private int width, height, chunksX;

	/// <summary>Game ticks (Main.GameUpdateCount), set every tick by the bot.</summary>
	public uint Now { get; set; }

	public CellKind Get(int x, int y)
	{
		if (!WorldGen.InWorld(x, y, 1))
			return CellKind.Solid;
		if (width != Main.maxTilesX || height != Main.maxTilesY)
			Resize();
		int ci = (y >> ChunkShift) * chunksX + (x >> ChunkShift);
		uint s = stamp[ci];
		if (s == 0 || Now - s > MaxAgeTicks)
			Fill(ci, x >> ChunkShift, y >> ChunkShift);
		return cells[y * width + x];
	}

	/// <summary>Mark the chunks holding cells within one tile of (x, y) stale.</summary>
	public void Invalidate(int x, int y)
	{
		if (stamp.Length == 0)
			return;
		for (int dy = -1; dy <= 1; dy += 2)
			for (int dx = -1; dx <= 1; dx += 2) {
				int cx = (x + dx) >> ChunkShift, cy = (y + dy) >> ChunkShift;
				if (cx >= 0 && cy >= 0 && cx < chunksX && cy * chunksX + cx < stamp.Length)
					stamp[cy * chunksX + cx] = 0;
			}
	}

	/// <summary>Everything stale (a new world, or the self-test redrew the arena).</summary>
	public void Clear() => System.Array.Clear(stamp);

	private void Resize()
	{
		width = Main.maxTilesX;
		height = Main.maxTilesY;
		chunksX = (width >> ChunkShift) + 1;
		int chunksY = (height >> ChunkShift) + 1;
		cells = new CellKind[width * height];
		stamp = new uint[chunksX * chunksY];
	}

	private void Fill(int ci, int cx, int cy)
	{
		int x0 = cx << ChunkShift, y0 = cy << ChunkShift;
		int x1 = System.Math.Min(x0 + (1 << ChunkShift), width), y1 = System.Math.Min(y0 + (1 << ChunkShift), height);
		TerrariaTileGrid live = TerrariaTileGrid.Instance;
		for (int y = y0; y < y1; y++)
			for (int x = x0; x < x1; x++)
				cells[y * width + x] = live.Get(x, y);
		stamp[ci] = Now == 0 ? 1u : Now;
	}

	/// <summary>Tile changes, as the game frames them, make the cache read those chunks again.</summary>
	public sealed class Invalidator : GlobalTile
	{
		public override bool TileFrame(int i, int j, int type, ref bool resetFrame, ref bool noBreak)
		{
			Instance.Invalidate(i, j);
			return true;
		}

		public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem) => Instance.Invalidate(i, j);

		public override void PlaceInWorld(int i, int j, int type, Item item) => Instance.Invalidate(i, j);
	}
}
