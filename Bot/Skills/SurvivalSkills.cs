using System.Linq;
using TerraBot.Core.Grid;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Get <see cref="Amount"/> of an item that NPCs drop (gel from slimes): fight NPCs with the given AI style
/// when they are around and pick up the drops; otherwise wander along the surface and wait for spawns.
/// </summary>
public sealed class HuntSkill : ISkill
{
	public const int SearchTiles = 40;
	public const int WanderTiles = 30;
	public const int WaitTicks = 60 * 5;
	public const int MaxWanders = 12;

	private ISkill sub;
	private int wanders;
	private int wanderDir = 1;
	private int waitTicks;
	private int lastHave;

	public int ItemType { get; }
	public int AiStyle { get; }
	public int Amount { get; }
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hunt";

	public HuntSkill(int itemType, int aiStyle, int amount)
	{
		ItemType = itemType;
		AiStyle = aiStyle;
		Amount = amount;
	}

	public string Describe() => $"Hunt (AI {AiStyle}) for item {ItemType} {lastHave}/{Amount} [{State}]" + (sub != null ? " > " + sub.Describe() : "");
	public void Start(BotContext ctx) => ctx.Log.Info($"Hunt start: item {ItemType} x{Amount} from NPCs with AI style {AiStyle}, have {ctx.Inventory.Count(ItemType)}");

	public void Stop(BotContext ctx)
	{
		sub?.Stop(ctx);
		sub = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		int have = ctx.Inventory.Count(ItemType);
		lastHave = have;
		if (have >= Amount && sub is not CollectDropsSkill) {
			ctx.Log.Info($"Hunt done: item {ItemType} {have}/{Amount}");
			State = "done";
			return SkillStatus.Succeeded;
		}
		if (!ctx.Combat.HasWeapon)
			return Fail(ctx, "no melee weapon");
		if (sub != null) {
			SkillStatus s = sub.Tick(ctx);
			if (s == SkillStatus.Running && !(sub is WalkToSkill && PreyInSight(ctx)))
				return SkillStatus.Running;
			bool fought = sub is FightSkill;
			sub.Stop(ctx);
			sub = null;
			if (fought) {
				Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, 20), "collecting");
				return SkillStatus.Running;
			}
		}
		if (ctx.Drops.Any(d => d.Type == ItemType)) {
			Begin(ctx, new CollectDropsSkill(d => d.Type == ItemType, 20), "collecting");
			return SkillStatus.Running;
		}
		if (PreyInSight(ctx)) {
			Begin(ctx, new FightSkill(), "fighting");
			return SkillStatus.Running;
		}
		if (waitTicks-- > 0) {
			State = "waiting for spawns";
			return SkillStatus.Running;
		}
		if (++wanders > MaxWanders)
			return Fail(ctx, $"no NPC with AI style {AiStyle} found after {MaxWanders} wanders");
		TilePos node = ctx.Node;
		var goal = new TilePos(node.X + wanderDir * WanderTiles, node.Y - 10);
		wanderDir = -wanderDir;
		waitTicks = WaitTicks;
		ctx.Log.Info($"Hunt: nothing to hunt in sight, wandering towards {goal}");
		Begin(ctx, new WalkToSkill(goal) { Quiet = true, Tolerance = 4 }, "wandering");
		return SkillStatus.Running;
	}

	private bool PreyInSight(BotContext ctx)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		return ctx.Hostiles.Any(h => h.AiStyle == AiStyle && h.DistanceTo(cx, cy) <= SearchTiles * 16f);
	}

	private void Begin(BotContext ctx, ISkill skill, string state)
	{
		sub = skill;
		State = state;
		skill.Start(ctx);
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Hunt failed: {reason}");
		return SkillStatus.Failed;
	}
}

/// <summary>
/// Sit out the night in a sealed hole: dig a shaft <see cref="Depth"/> rows straight down, place blocks in the
/// two cells above the head (the old floor row, where the shaft walls give the placement a neighbour), wait
/// for morning, then mine the seal open again. Zombies and demon eyes cannot get through blocks. Without blocks
/// the bot still waits at the bottom of the shaft.
/// </summary>
public sealed class ShelterSkill : ISkill
{
	public const int Depth = 5;
	public const int MaxActionTicks = 60 * 6;

	private enum Phase { Digging, Sealing, Waiting, Unsealing }

	private Phase phase;
	private WalkToSkill dig;
	private TilePos[] seal;
	private int actionTicks;

	/// <summary>Sealed in and waiting: the brain does not interrupt this for hostiles it cannot reach anyway.</summary>
	public bool Sealed => phase == Phase.Waiting && seal != null && sealedOk;
	private bool sealedOk;

	public string State => phase.ToString();
	public string FailureReason { get; private set; } = "";
	public string Name => "Shelter";

	public string Describe() => $"Shelter [{phase}]" + (dig != null ? " > " + dig.Describe() : "");

	public void Start(BotContext ctx)
	{
		TilePos n = ctx.Node;
		ctx.Log.Info($"Shelter start at {n}: digging in for the night");
		dig = new WalkToSkill(new TilePos(n.X, n.Y + Depth)) { Quiet = true };
		dig.Start(ctx);
	}

	public void Stop(BotContext ctx)
	{
		dig?.Stop(ctx);
		dig = null;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null)
			return SkillStatus.Running;
		switch (phase) {
			case Phase.Digging: {
				SkillStatus s = dig.Tick(ctx);
				if (s == SkillStatus.Running)
					return SkillStatus.Running;
				string why = dig.FailureReason;
				dig.Stop(ctx);
				dig = null;
				TilePos n = ctx.Node;
				seal = new[] { new TilePos(n.X, n.Y - 3), new TilePos(n.X + 1, n.Y - 3) };
				if (s == SkillStatus.Failed)
					ctx.Log.Info($"Shelter: could not dig down ({why}); waiting here at {n}");
				phase = Phase.Sealing;
				actionTicks = 0;
				return SkillStatus.Running;
			}
			case Phase.Sealing: {
				TilePos? open = seal.Select(c => (TilePos?)c).FirstOrDefault(c => !ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid());
				if (open == null) {
					sealedOk = true;
					ctx.Log.Info($"Shelter: sealed in at {ctx.Node}, waiting for morning");
					phase = Phase.Waiting;
					return SkillStatus.Running;
				}
				if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0 || ++actionTicks > MaxActionTicks) {
					ctx.Log.Info($"Shelter: cannot seal {open.Value} (blocks {ctx.Blocks?.BlockCount ?? 0}); waiting unsealed");
					phase = Phase.Waiting;
					return SkillStatus.Running;
				}
				ctx.Output.Tool = ToolRequest.Block;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(open.Value.X, open.Value.Y);
				return SkillStatus.Running;
			}
			case Phase.Waiting:
				if (ctx.IsNight)
					return SkillStatus.Running;
				ctx.Log.Info("Shelter: morning, opening up");
				phase = Phase.Unsealing;
				actionTicks = 0;
				return SkillStatus.Running;
			default: {
				TilePos? closed = sealedOk ? seal.Select(c => (TilePos?)c).FirstOrDefault(c => ctx.Grid.Get(c.Value.X, c.Value.Y).IsSolid()) : null;
				if (closed == null || ctx.PickPower <= 0 || ++actionTicks > MaxActionTicks) {
					ctx.Log.Info("Shelter done");
					return SkillStatus.Succeeded;
				}
				ctx.Output.Tool = ToolRequest.Pickaxe;
				ctx.Output.UseItem = true;
				ctx.Output.Aim(closed.Value.X, closed.Value.Y);
				return SkillStatus.Running;
			}
		}
	}
}
