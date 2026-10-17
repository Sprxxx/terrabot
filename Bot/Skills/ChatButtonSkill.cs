using System.Linq;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Talk to a town NPC in chat range and press its first chat button (<see cref="ICrafter.PressFirstChatButton"/>: a UI click
/// the bot has no control for; PLAN.md, "Actions that stand in for UI clicks"). For the Old Man at night that is "Curse",
/// which summons Skeletron. Fails at once, with the reason, when the NPC is not in view or not in range, or the game's guards
/// refuse (no button by day, a head already up): the planner walks up and tries again.
/// </summary>
public sealed class ChatButtonSkill : ISkill
{
	/// <summary>
	/// The chat box round the player's centre: Player.tileRangeX × 16 = 80 px each way across and Player.tileRangeY × 16 = 64 px
	/// up and down (Player.cs:2859-2862), which the NPC's hitbox must intersect (Main.HoverOverNPCs, Main.cs:56054-56063).
	/// </summary>
	public const float ChatRangeXPx = 5 * 16f, ChatRangeYPx = 4 * 16f;

	public int NpcType { get; }
	public string What { get; }
	public string FailureReason { get; private set; } = "";
	public string Name => "Chat";
	public string Key => $"Chat {NpcType}";
	public bool Pressed { get; private set; }

	public ChatButtonSkill(int npcType, string what)
	{
		NpcType = npcType;
		What = what;
	}

	public string Describe() => $"Chat with the {What}{(Pressed ? " (pressed)" : "")}";
	public void Start(BotContext ctx) => ctx.Log.Info($"Chat start: the {What}'s first button");
	public void Stop(BotContext ctx) { }

	/// <summary>Is the NPC's hitbox within the chat box round the player's centre (as Rectangle.Intersects: overlapping, not touching)?</summary>
	public static bool InChatRange(BotContext ctx, EntityInfo npc)
	{
		PhysicsParams p = ctx.Physics;
		float cx = ctx.Body.CenterX(p), cy = ctx.Body.CenterY(p);
		float x0 = cx - ChatRangeXPx, x1 = cx + ChatRangeXPx, y0 = cy - ChatRangeYPx, y1 = cy + ChatRangeYPx;
		return npc.X < x1 && x0 < npc.X + npc.Width && npc.Y < y1 && y0 < npc.Y + npc.Height;
	}

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null)
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		EntityInfo? npc = ctx.TownNpcs.Where(n => n.Type == NpcType).Cast<EntityInfo?>().FirstOrDefault();
		if (npc is not EntityInfo n)
			return Fail(ctx, $"no {What} in view");
		if (!InChatRange(ctx, n))
			return Fail(ctx, $"the {What} at {n.FeetTile} is out of chat range");
		string why = ctx.Crafter.PressFirstChatButton(n.Id);
		if (why != null)
			return Fail(ctx, why);
		Pressed = true;
		ctx.Log.Info($"Chat: pressed the {What}'s first button at {n.FeetTile}, us at {ctx.Node}");
		return SkillStatus.Succeeded;
	}

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		ctx.Log.Warn($"Chat failed: {reason}");
		return SkillStatus.Failed;
	}
}
