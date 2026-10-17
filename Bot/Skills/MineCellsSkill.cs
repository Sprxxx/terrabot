using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Mines the cells given, from a stand within reach (a walk there first): the hellbridge's deck opened over the lava where the
/// Guide Voodoo Doll is to fall (world_wof E12, 2026-10-17: the bridge's end against the world's edge had no lava under it, the
/// lava sea stopping short of the edge, and the errand blocked "no lava under where the doll would land"). A tree cell takes the axe.
/// </summary>
public sealed class MineCellsSkill : ISkill
{
	/// <summary>Ticks a cell gets before the skill fails on it.</summary>
	public const int CellTicks = 300;

	public IReadOnlyList<TilePos> Cells { get; }
	public TilePos Stand { get; }
	public string Name => "MineCells";
	public string Key => $"MineCells {Stand} {Cells.Count}";
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Describe() => $"MineCells {Cells.Count} cell(s) from {Stand} [{State}]";
	private WalkToSkill walk;
	private int cellTicks;
	private TilePos? cellAt;

	public MineCellsSkill(IReadOnlyList<TilePos> cells, TilePos stand)
	{
		Cells = cells;
		Stand = stand;
	}

	public void Start(BotContext ctx) { }

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		}
		TilePos? next = Cells.Cast<TilePos?>().FirstOrDefault(c => ctx.Grid.Get(c.Value.X, c.Value.Y).CanStandOn() || ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid());
		if (next is not TilePos cell) {
			State = "done";
			return SkillStatus.Succeeded;
		}
		// At the stand, or a column either side of it (the walk's goal is met a column short at times; the reach covers it).
		bool there = ctx.Node.Y == Stand.Y && Math.Abs(ctx.Node.X - Stand.X) <= 1;
		if (!there || walk != null) {
			if (walk == null) {
				walk = new WalkToSkill(new GoalStandAt(Stand, ctx.Physics)) { Quiet = true };
				walk.Start(ctx);
			}
			SkillStatus ws = walk.Tick(ctx);
			if (ws == SkillStatus.Running)
				return SkillStatus.Running;
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			if (ws == SkillStatus.Failed && !(ctx.Node.Y == Stand.Y && Math.Abs(ctx.Node.X - Stand.X) <= 1))
				return Fail(ctx, $"no way to {Stand} ({why})");
			State = $"at {Stand}";
			return SkillStatus.Running;
		}
		if (!ctx.AllowBreak)
			return Fail(ctx, "digging is off");
		if (cellAt != cell) {
			cellAt = cell;
			cellTicks = 0;
		}
		if (++cellTicks > CellTicks)
			return Fail(ctx, $"could not mine {cell} in {CellTicks} ticks (the cell reads {ctx.Grid.Get(cell.X, cell.Y)}, tile {ctx.World?.TileTypeAt(cell.X, cell.Y) ?? -1})");
		ctx.Output.Tool = ctx.World?.IsTreeTrunk(cell.X, cell.Y) == true ? ToolRequest.Axe : ToolRequest.Pickaxe;
		ctx.Output.Aim(cell.X, cell.Y);
		ctx.Output.UseItem = true;
		State = $"mining {cell}";
		return SkillStatus.Running;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"MineCells failed: {reason}");
		return SkillStatus.Failed;
	}
}
