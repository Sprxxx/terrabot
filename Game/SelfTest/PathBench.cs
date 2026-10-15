using System.Diagnostics;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using Terraria.ModLoader;

namespace TerraBot.Game.SelfTest;

/// <summary>
/// Times path searches on the real world from where the bot stands (self-test mode "pathbench"): per move type, the
/// off-limits check, the live grid against the cached one. The searches the bot stands still for took about 290 ms for
/// 3,000 nodes in game, ten times the same search on a test grid.
/// </summary>
public static class PathBench
{
	public static void Run(BotContext ctx, Mod mod)
	{
		TilePos n = ctx.Node;
		var goals = new (string Name, IGoal Goal)[] {
			("down 120", new GoalNearTile(new TilePos(n.X + 40, n.Y + 120), 4, ctx.Physics)),
			("along 150", new GoalNearTile(new TilePos(n.X - 150, n.Y), 4, ctx.Physics)),
			("down 40", new GoalNearTile(new TilePos(n.X + 20, n.Y + 40), 4, ctx.Physics)),
		};
		foreach (var (gridName, grid) in new (string, ITileGrid)[] { ("live", TerrariaTileGrid.Instance), ("cached", CachedTileGrid.Instance) }) {
			foreach (var (name, goal) in goals) {
				ProfilingMove[] moves = MoveContext.DefaultMoves.Select(m => new ProfilingMove(m)).ToArray();
				long avoidTicks = 0;
				int avoidCalls = 0;
				MoveContext Make(bool profiled) => new(grid, ctx.Physics, profiled ? moves : null) {
					Blocks = ctx.PathBlocks, AllowBreak = ctx.AllowBreak, AllowPlace = ctx.AllowPlace, BadEdges = ctx.BadEdges,
					AvoidBelowRow = ctx.DeepestRow,
					Avoid = t => { long t0 = Stopwatch.GetTimestamp(); bool v = ctx.OffLimitsForPaths(t); avoidTicks += Stopwatch.GetTimestamp() - t0; avoidCalls++; return v; },
				};
				for (int i = 0; i < 8; i++)
					new PathSearch(Make(false), n, goal).Step(); // warm up
				var sw = Stopwatch.StartNew();
				var plain = new PathSearch(Make(false), n, goal);
				plain.Step();
				double plainMs = sw.Elapsed.TotalMilliseconds;
				avoidTicks = 0;
				avoidCalls = 0;
				var prof = new PathSearch(Make(true), n, goal);
				prof.Step();
				string per = string.Join(", ", moves.Select(m => $"{m.Inner.GetType().Name.Replace("Move", "")} {m.Ms:0.0}"));
				mod.Logger.Info($"PATHBENCH {gridName} {name}: {plain.Result} -> {plainMs:0.0} ms, {plainMs * 1000 / System.Math.Max(1, plain.Result.Expanded):0.0} us/node | {per} | avoid {avoidTicks * 1000.0 / Stopwatch.Frequency:0.0} ms in {avoidCalls} calls");
			}
		}
		mod.Logger.Info("PATHBENCH done");
	}
}
