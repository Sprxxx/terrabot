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
/// <item>Watch for <see cref="Done"/> (the Wall here) while the thrown item is in the air, its track logged; once it has lain still
/// for <see cref="RestTicks"/> without that, it did not reach the lava: fail with where it lies (the planner picks it up again);
/// at most <see cref="MaxWatchTicks"/>.</item>
/// </list>
/// </summary>
public sealed class ThrowItemSkill : ISkill
{
	/// <summary>
	/// The longest watch: an item falls at 0.1 px/tick² up to 7 px/tick (Item.cs:50115-50116), so 25-36 rows from the hellbridge to
	/// the lava (spec 5b) take 110-150 ticks after the throw's 2 px/tick rise; 600 is room for a deeper drop or a slow start. Seen in
	/// game (doll_lava_east, 2026-10-17): the watch of 120 ended with the doll still in sight at 9 rows under the ledge.
	/// </summary>
	public const int MaxWatchTicks = 600;
	/// <summary>An item seen in one place this long lies there (a falling one moves every tick): 30 ticks.</summary>
	public const int RestTicks = 30;
	/// <summary>The thrown item's place is logged this often while it is watched.</summary>
	public const int TrackTicks = 30;
	/// <summary>Ticks a pressed throw may take to show in the count (the drop happens in the same update; two for the perception).</summary>
	public const int ThrowShowsTicks = 10;
	public const int MaxPresses = 3;
	/// <summary>Ticks to stand still for after turning, at most (from a run at 6 px/tick the player slows at 0.2 px/tick a tick: 30).</summary>
	public const int MaxSettleTicks = 90;
	/// <summary>The body is still enough below this speed (px/tick): the thrown item's speed is 4 px/tick plus the body's.</summary>
	public const float StillSpeed = 0.1f;

	private enum Phase { Face, Settle, Press, Pressed, Watch }

	private Phase phase;
	private int phaseTicks, presses, countBefore, stillTicks;
	private (float X, float Y)? lastSeen;
	private bool fell;
	/// <summary>Rising faster than this (px/tick) after it fell: pulled, not bounced (items do not bounce off liquid or tiles).</summary>
	public const float PulledUpSpeed = 3f;

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
				DropInfo? seen = ctx.Drops.Where(d => d.Type == ItemType).Cast<DropInfo?>().FirstOrDefault();
				if (seen is DropInfo item) {
					bool moved = lastSeen is not (float lx, float ly) || Math.Abs(item.X - lx) > 0.5f || Math.Abs(item.Y - ly) > 0.5f;
					stillTicks = moved ? 0 : stillTicks + 1;
					if (phaseTicks % TrackTicks == 0 && lastSeen is (float px, float py))
						ctx.Log.Info($"Throw: the {What} at {item.Tile} ({item.X:0},{item.Y:0} px), moving ({item.X - px:0.0},{item.Y - py:0.0}) px/tick, {phaseTicks} ticks after the throw");
					// Falling, then rising fast toward us: a grab range reaches it (a magnet, Journey's placement range: Player.GrabItems
					// pulls at 7 px/tick once the 100-tick no-grab delay is over, Player.cs:34073-34090), and the bot picks it up again.
					if (lastSeen is (float qx, float qy)) {
						if (item.Y - qy > 1f)
							fell = true;
						else if (fell && item.Y - qy < -PulledUpSpeed)
							return Fail(ctx, $"the {What} was pulled back toward us from {item.Tile} ({phaseTicks} ticks after the throw): an item grab range reaches it");
					}
					lastSeen = (item.X, item.Y);
					if (stillTicks >= RestTicks)
						return Fail(ctx, $"the {What} did not reach lava: it lies at {item.Tile} ({phaseTicks} ticks after the throw)");
				}
				if (phaseTicks < MaxWatchTicks)
					return SkillStatus.Running;
				return Fail(ctx, seen is DropInfo d ? $"the {What} did not reach lava in {MaxWatchTicks} ticks: it is at {d.Tile}" : $"nothing came of the {What} in {MaxWatchTicks} ticks");
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
