using System.Collections.Generic;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// Right before a boss is summoned: use what the bag holds for the fight, as a player does at the arena. First each fight potion
/// and the best food whose buff is not on (<see cref="IInventory.FightBuffItems"/>: Summoning, Ironskin, Endurance, Regeneration,
/// Archery, Swiftness...), then a minion from each carried staff while minion slots and mana allow (<see cref="IInventory.SummonStaves"/>),
/// the most damage first. Minions fight on their own whatever the player holds (vanilla: their damage is the staff's and the
/// owner's summon bonuses, Projectile.cs:15925-15930; they take the nearest target, NPC.cs:107202-107209), so a bow build loses
/// nothing by them; they are gone after a death (Player.UpdateDead), so each fight's try summons them again.
/// <para>
/// Seen in game (world_wof E26, 2026-10-17): the Wall fought with none of it, Burger, Steak and Spelunker Potions in the bag; the
/// user: "We should also make sure to do whatever possible to make bosses easier. Just because we use a bow or a melee weapon
/// doesnt mean we cant use a summon for example." Never fails: what cannot be used is left, and the fight goes on.
/// </para>
/// </summary>
public sealed class ReadyForBossSkill : ISkill
{
	/// <summary>Staff uses at most (a minion that did not come does not loop the bot): two slots with a Summoning Potion and a table, and one spare.</summary>
	public const int MaxSummons = 4;
	/// <summary>The minion is put this far above the body's centre (px): in the air beside the player, not in a block.</summary>
	public const float MinionMouseUp = -40f;

	private readonly HashSet<int> tried = new();
	private UseItemSkill current;
	private bool currentIsStaff;
	private int summons;

	public string Name => "Ready";
	public string Key => "Ready for a boss";
	public string FailureReason => "";
	/// <summary>Items used (potions, food) and minions summoned, for the log and the tests.</summary>
	public int Used { get; private set; }
	public int Summoned { get; private set; }

	public string Describe() => current == null ? "ready" : currentIsStaff ? $"summoning a minion (item {current.ItemType})" : $"using item {current.ItemType}";

	public void Start(BotContext ctx) { }

	public void Stop(BotContext ctx) => current?.Stop(ctx);

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		// Standing still to drink is for a quiet moment: with a hostile near or a shot coming, the fight goes on and dodges (review,
		// 2026-10-17: nothing dodges or heals while this runs). What is mid-use finishes first.
		if (current == null && ThreatNear(ctx)) {
			if (Used + Summoned + tried.Count > 0 || summons > 0)
				ctx.Log.Info("Ready: cut short, a hostile near");
			return SkillStatus.Succeeded;
		}
		if (current != null) {
			SkillStatus s = current.Tick(ctx);
			if (s == SkillStatus.Running)
				return SkillStatus.Running;
			if (s == SkillStatus.Succeeded) {
				if (currentIsStaff)
					Summoned++;
				else
					Used++;
				ctx.Log.Info($"Ready: {(currentIsStaff ? "summoned a minion with" : "used")} {ctx.Knowledge?.ItemName(current.ItemType) ?? current.ItemType.ToString()}");
			}
			current = null;
		}
		foreach (int item in ctx.Inventory.FightBuffItems()) {
			if (!tried.Add(item))
				continue;
			return Begin(ctx, new UseItemSkill(item), staff: false);
		}
		if (summons < MaxSummons)
			foreach (var (item, mana, slots) in ctx.Inventory.SummonStaves()) {
				if (ctx.Combat.MinionSlotsFree + 1e-3f < slots || ctx.Combat.Mana < mana)
					continue;
				summons++;
				return Begin(ctx, new UseItemSkill(item) { MouseOffset = (ctx.Combat.Facing * 24f, MinionMouseUp) }, staff: true);
			}
		if (Used + Summoned > 0 && !logged) {
			logged = true;
			ctx.Log.Info($"Ready: {Used} item(s) used, {Summoned} minion(s) summoned");
		}
		return SkillStatus.Succeeded;
	}

	private bool logged;

	/// <summary>A hostile (not a spared one) within this many tiles, or a shot coming, cuts the readying short.</summary>
	public const int QuietTiles = 25;

	private static bool ThreatNear(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (EntityInfo h in ctx.Hostiles)
			if (h.Damage > 0 && !ctx.Spared(h) && h.DistanceTo(cx, cy) <= QuietTiles * 16f)
				return true;
		foreach (ShotInfo s in ctx.Shots)
			if (s.Damage > 0 && s.DistanceTo(cx, cy) <= QuietTiles * 16f)
				return true;
		return false;
	}

	private SkillStatus Begin(BotContext ctx, UseItemSkill skill, bool staff)
	{
		// Journey: researched, not carried: one duplicated first (the menu's click).
		if (ctx.Inventory.Count(skill.ItemType) <= 0 && ctx.Infinite(skill.ItemType) && ctx.Journey.Duplicate(skill.ItemType) > 0)
			ctx.Log.Info($"Ready: duplicated {ctx.Knowledge?.ItemName(skill.ItemType) ?? skill.ItemType.ToString()} (researched)");
		if (ctx.Inventory.Count(skill.ItemType) <= 0)
			return Tick(ctx); // nothing to use: the next one
		current = skill;
		currentIsStaff = staff;
		current.Start(ctx);
		return current.Tick(ctx) == SkillStatus.Running ? SkillStatus.Running : Tick(ctx);
	}
}
