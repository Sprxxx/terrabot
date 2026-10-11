using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Out of breath under water: swim up (hold jump) towards the nearest column with air above the head, until
/// breath is back near full and the head is out. Seen in game: the bot chased a slime into a flooded cave and
/// drowned (vanilla takes 2 life per breath tick at 0 breath, without a hurt event: nothing showed in the log).
/// </summary>
public sealed class SurfaceSkill : ISkill
{
	public const int MaxTicks = 60 * 15;
	public const int SearchColumns = 16, SearchUp = 25;

	private int ticks;
	private int? column;

	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Surface";

	public string Describe() => $"Surface [{State}]";

	public void Start(BotContext ctx) => ctx.Log.Info($"Surface start: breath {ctx.Combat.Breath}/{ctx.Combat.BreathMax} at {ctx.Node}");
	public void Stop(BotContext ctx) { }

	/// <summary>Is the head cell (top of the 2×3 body) in liquid?</summary>
	public static bool HeadUnder(BotContext ctx)
	{
		TilePos n = ctx.Node;
		return ctx.Grid.Get(n.X, n.Y - 2).IsLiquid() || ctx.Grid.Get(n.X + 1, n.Y - 2).IsLiquid();
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (!HeadUnder(ctx) && ctx.Combat.Breath >= ctx.Combat.BreathMax * 0.9f) {
			ctx.Log.Info($"Surface: breathing again at {ctx.Node}");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (++ticks > MaxTicks) {
			FailureReason = $"no air reached in {MaxTicks} ticks";
			ctx.Log.Warn("Surface failed: " + FailureReason);
			return SkillStatus.Failed;
		}
		if (!HeadUnder(ctx)) {
			State = "catching breath";
			return SkillStatus.Running; // head out: just wait here for breath to come back
		}
		if (column == null || ticks % 30 == 0)
			column = AirColumn(ctx);
		TilePos n = ctx.Node;
		ctx.Output.Jump = true; // holding jump swims up
		if (column is int cx && cx != n.X)
			ctx.Output.Horizontal = Math.Sign(cx - n.X);
		State = column != null ? $"swimming up towards column {column}" : "swimming up";
		return SkillStatus.Running;
	}

	/// <summary>The nearest column (by sideways distance plus height to swim) with an air cell above the water and nothing solid in the way.</summary>
	private static int? AirColumn(BotContext ctx)
	{
		TilePos n = ctx.Node;
		int? best = null;
		int bestCost = int.MaxValue;
		for (int dx = 0; dx <= SearchColumns; dx++)
			foreach (int x in dx == 0 ? new[] { n.X } : new[] { n.X - dx, n.X + dx })
				for (int y = n.Y - 2; y >= n.Y - 2 - SearchUp; y--) {
					CellKind k = ctx.Grid.Get(x, y);
					if (k.IsSolid())
						break; // a ceiling: no air this way
					if (!k.IsLiquid()) {
						int cost = dx * 2 + (n.Y - y);
						if (cost < bestCost) {
							bestCost = cost;
							best = x;
						}
						break;
					}
				}
		return best;
	}
}
