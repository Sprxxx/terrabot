using System;
using System.Linq;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Throw exactly one of an item with the Throw key (vanilla controlThrow, Player.cs:5927-5929; <see cref="MoveInput.Throw"/>),
/// facing <see cref="Facing"/>: the Guide Voodoo Doll off the hellbridge's end into the lava below, which summons the Wall of
/// Flesh (docs M12_SPEC.md section 2c, S6). A stack of several is split first by the game adapter: a stack of N dolls in lava
/// kills N-1 more town NPCs (Item.cs:50426-50460).
/// <list type="number">
/// <item>Refuse with a reason if <see cref="Refuse"/> says so (no Guide alive: the doll would be wasted).</item>
/// <item>Face the throw's side: one tick on that direction key between swings (only the movement keys turn a player that is not
/// using an item), then stand until the body is still: the item flies off at 4 px/tick plus the player's own speed
/// (Player.cs:6005-6006).</item>
/// <item>Press Throw for one tick (released the tick before: releaseThrow) and see the count drop by one.</item>
/// <item>Watch for <see cref="Done"/> (the Wall here) for <see cref="WatchTicks"/>; if it does not come and the item lies in sight,
/// it did not reach the lava: fail with where it lies (the planner picks it up again).</item>
/// </list>
/// </summary>
public sealed class ThrowItemSkill : ISkill
{
	/// <summary>Ticks to see the thrown item's effect (spec S6: 120): a doll from a bridge 25 rows over lava falls in about 110.</summary>
	public const int WatchTicks = 120;
	/// <summary>Ticks a pressed throw may take to show in the count (the drop happens in the same update; two for the perception).</summary>
	public const int ThrowShowsTicks = 10;
	public const int MaxPresses = 3;
	/// <summary>Ticks to stand still for after turning, at most (from a run at 6 px/tick the player slows at 0.2 px/tick a tick: 30).</summary>
	public const int MaxSettleTicks = 90;
	/// <summary>The body is still enough below this speed (px/tick): the thrown item's speed is 4 px/tick plus the body's.</summary>
	public const float StillSpeed = 0.1f;

	private enum Phase { Face, Settle, Press, Pressed, Watch }

	private Phase phase;
	private int phaseTicks, presses, countBefore;

	public int ItemType { get; }
	/// <summary>The side to throw to: -1 west, +1 east.</summary>
	public int Facing { get; }
	public string What { get; }
	/// <summary>Why not to throw now (checked before the throw), or null to go ahead.</summary>
	public Func<BotContext, string> Refuse { get; init; }
	/// <summary>What the throw is for has happened (the Wall is up); null: the throw itself is the success.</summary>
	public Func<BotContext, bool> Done { get; init; }
	/// <summary>Said once <see cref="Done"/> is seen (where the Wall came from).</summary>
	public Func<BotContext, string> DoneText { get; init; }
	public bool Thrown { get; private set; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Throw";
	public string Key => $"Throw {ItemType} {Facing}";

	public ThrowItemSkill(int itemType, int facing, string what)
	{
		ItemType = itemType;
		Facing = facing >= 0 ? 1 : -1;
		What = what;
	}

	public string Describe() => $"Throw one {What} {(Facing > 0 ? "east" : "west")} [{phase}]";

	public void Start(BotContext ctx)
	{
		phase = Phase.Face;
		phaseTicks = 0;
		ctx.Log.Info($"Throw start: one {What} (carrying {ctx.Inventory.Count(ItemType)}) {(Facing > 0 ? "east" : "west")} from {ctx.Node}");
	}

	public void Stop(BotContext ctx) => ctx.Output = MoveInput.None;

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null)
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		phaseTicks++;
		switch (phase) {
			case Phase.Face:
				if (ctx.Inventory.Count(ItemType) <= 0)
					return Fail(ctx, $"no {What} carried");
				if (Refuse?.Invoke(ctx) is string why)
					return Fail(ctx, why);
				if (ctx.Combat.Facing != Facing) {
					if (ctx.Combat.ItemAnimation == 0)
						ctx.Output.Horizontal = Facing; // one tick on the key turns the player
					if (phaseTicks <= MaxSettleTicks)
						return SkillStatus.Running;
					return Fail(ctx, $"could not turn {(Facing > 0 ? "east" : "west")}");
				}
				Next(Phase.Settle);
				return SkillStatus.Running;
			case Phase.Settle:
				if (!ctx.Body.Grounded || Math.Abs(ctx.Body.VX) > StillSpeed) {
					if (phaseTicks > MaxSettleTicks)
						Next(Phase.Press); // still moving after all that: throw anyway, its speed adds to the item's
					return SkillStatus.Running;
				}
				Next(Phase.Press);
				return SkillStatus.Running;
			case Phase.Press:
				if (Refuse?.Invoke(ctx) is string late)
					return Fail(ctx, late);
				if (ctx.Combat.ItemAnimation != 0)
					return SkillStatus.Running; // between swings only (the game's dropItemCheck)
				countBefore = ctx.Inventory.Count(ItemType);
				if (countBefore <= 0)
					return Fail(ctx, $"no {What} carried");
				ctx.Output.Throw = true;
				ctx.Output.ThrowItem = ItemType;
				presses++;
				Next(Phase.Pressed);
				return SkillStatus.Running;
			case Phase.Pressed: {
				int now = ctx.Inventory.Count(ItemType);
				if (now < countBefore) {
					Thrown = true;
					ctx.Log.Info($"Throw: one {What} thrown {(Facing > 0 ? "east" : "west")} from {ctx.Node}" + (countBefore - now != 1 ? $" (the count fell by {countBefore - now})" : "")
						+ $", {now} left");
					if (Done == null)
						return SkillStatus.Succeeded;
					Next(Phase.Watch);
					return SkillStatus.Running;
				}
				if (phaseTicks < ThrowShowsTicks)
					return SkillStatus.Running;
				if (presses >= MaxPresses)
					return Fail(ctx, $"the Throw key did nothing ({presses} presses)");
				Next(Phase.Press);
				return SkillStatus.Running;
			}
			case Phase.Watch:
				if (Done(ctx)) {
					ctx.Log.Info(DoneText?.Invoke(ctx) ?? $"Throw: the {What} did what it was thrown for, {phaseTicks} ticks after the throw");
					return SkillStatus.Succeeded;
				}
				if (phaseTicks < WatchTicks)
					return SkillStatus.Running;
				DropInfo? lying = ctx.Drops.Where(d => d.Type == ItemType).Cast<DropInfo?>().FirstOrDefault();
				return Fail(ctx, lying is DropInfo d ? $"the {What} did not reach lava: it lies at {d.Tile}" : $"nothing came of the {What} in {WatchTicks} ticks");
		}
		return SkillStatus.Running;
	}

	private void Next(Phase p)
	{
		phase = p;
		phaseTicks = 0;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Throw failed: {reason}");
		return SkillStatus.Failed;
	}
}
