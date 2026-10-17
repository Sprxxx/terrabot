using System.Collections.Generic;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 11: fighting Skeletron (<see cref="SkeletronPattern"/>) with <see cref="BossFightSkill"/>. The field as in
/// <see cref="WormFightTests"/>: air down to row 24 (the feet row), ground from row 25; the bot at column 97, the arena over
/// columns 50-145. The head hovers with its top 250 px over the player's (NPC.cs:22298-22321).
/// </summary>
public class SkeletronFightTests
{
	private readonly ITestOutputHelper output;
	public SkeletronFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly PhysicsParams P = PhysicsParams.Default;
	private const int HeadId = 1;
	private static readonly (int, int, int) Arena = (50, 145, 24);
	private const float BodyX = 97 * 16f;
	/// <summary>The player's top standing on row 25 (42 px tall) and its centre's column.</summary>
	private static float PlayerTop => 25 * 16f - P.Height;
	private static float PlayerCX => BodyX + P.Width / 2f;

	/// <summary>The head (80 × 102), its centre <paramref name="dxTiles"/> tiles across from ours, its top 250 px over ours (hovering) or <paramref name="dy"/> px off that.</summary>
	private static EntityInfo Head(float dxTiles = 0f, int phase = 0, float dy = 0f, float vx = 0f, float vy = 0f) =>
		new(HeadId, SkeletronPattern.Head, "Skeletron", PlayerCX + dxTiles * 16f - 40f, PlayerTop - SkeletronPattern.HoverAbovePx + dy, 80, 102, vx, vy,
			4400, 4400, 32, SkeletronPattern.HeadAiStyle, 1f, phase, 100f);

	/// <summary>A hand (52 × 52) with its top-left at (x, y) px, on side <paramref name="side"/> (ai[0]), at step <paramref name="step"/> (ai[2]).</summary>
	private static EntityInfo Hand(int id, float x, float y, float side, int step, float vx = 0f, float vy = 0f) =>
		new(id, SkeletronPattern.Hand, "Skeletron Hand", x, y, 52, 52, vx, vy, 600, 600, 20, SkeletronPattern.HandAiStyle, side, HeadId, step, 0f);

	private (FakeGame g, BotContext ctx, BossFightSkill fight) Setup(params EntityInfo[] hostiles)
	{
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new BodyState { X = BodyX, Y = PlayerTop, ReleaseJump = true };
		ctx.Hostiles = hostiles;
		ctx.IsNight = true;
		ctx.NightTicksLeft = 20000;
		var fight = new BossFightSkill(0, SkeletronPattern.Head, SkeletronPattern.Hand) { Arena = Arena };
		fight.Start(ctx);
		return (g, ctx, fight);
	}

	[Fact]
	public void Skeletron_IsFoughtByItsOwnRules()
	{
		Assert.True(new BossFightSkill(0, SkeletronPattern.Head, SkeletronPattern.Hand).IsSkeletron);
		Assert.False(new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc, FakeGame.ServantNpc).IsSkeletron);
		Assert.Equal(19, SkeletronPattern.HoverTopRows); // (250 + 42) / 16 = 18.25 rows over the floor
	}

	[Fact]
	public void Hovering_TheHeadIsShot_NotAHandRestingBesideUs()
	{
		// Spec S7: target the head (the kill is the head's); a hand 4 tiles off is not a "servant about to bite" here.
		var (_, ctx, fight) = Setup(Head(), Hand(2, BodyX + 4 * 16f, PlayerTop - 10f, -1f, 0));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, state {fight.State}, out {ctx.Output}");
		Assert.Equal(HeadId, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
	}

	[Fact]
	public void Hovering_TheBotWalksUnderTheHead_ThenStandsAndShoots()
	{
		var (_, ctx, fight) = Setup(Head(dxTiles: 10f));
		fight.Tick(ctx);
		Assert.Equal(1, ctx.Output.Horizontal);
		ctx.Tick = 1;
		ctx.Hostiles = new[] { Head(dxTiles: 1f) };
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: out {ctx.Output}, {fight.Dodges} dodges");
		Assert.Equal(0, ctx.Output.Horizontal);
		Assert.Equal(0, fight.Dodges);
	}

	[Fact]
	public void Spinning_TheBotRunsFromTheHead_AndPastItWhenTheArenaEndLeavesNoRoom()
	{
		// Spin: straight at us at 1.5 px/tick (NPC.cs:22399), against our 3: run from it.
		var (_, ctx, fight) = Setup(Head(dxTiles: -8f, phase: 1, dy: 200f));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: out {ctx.Output}");
		Assert.Equal(1, ctx.Output.Horizontal);

		// At the arena's east end with the head west of us: past it, back along the floor.
		var lines = new List<string>();
		var (_, ctx2, fight2) = Setup(Head(dxTiles: -8f, phase: 1, dy: 120f));
		ctx2.Log = new ListLog(lines);
		ctx2.Body = new BodyState { X = 143 * 16f, Y = PlayerTop, ReleaseJump = true };
		ctx2.Hostiles = new[] { new EntityInfo(HeadId, SkeletronPattern.Head, "Skeletron", 135 * 16f - 40f, PlayerTop - 130f, 80, 102, 0f, 0f, 4400, 4400, 32,
			SkeletronPattern.HeadAiStyle, 1f, 1f, 50f) };
		fight2.Tick(ctx2);
		output.WriteLine(string.Join(" | ", lines));
		Assert.Contains(lines, l => l.Contains("running past it"));
	}

	[Fact]
	public void ARisingHand_IsSeenComing_ItsSlamAimedAtUs_AndDodged()
	{
		// Spec S7: a hand rising (ai[2] = 1) slams at where we are once its top is 200 px over the head's (NPC.cs:22703-22738),
		// at 18 px/tick. Taken as it moves (straight up), it would never come down; the wind-up model sees the slam coming.
		EntityInfo head = Head();
		float handX = PlayerCX + 180f, handY = head.Y - SkeletronPattern.SlamFromAbovePx + 6f;
		EntityInfo hand = Hand(2, handX, handY, -1f, 1, 0f, -8f);
		Threat modelled = SkeletronPattern.PredictHand(hand, head, expert: false);
		output.WriteLine($"slam at tick {modelled.DashAt}, {modelled.DashSpeed} px/tick");
		Assert.InRange(modelled.DashAt, 1, 3);
		Assert.Equal(SkeletronPattern.SlamSpeed, modelled.DashSpeed);
		var body = new BodyState { X = BodyX, Y = PlayerTop, ReleaseJump = true };
		float floor = 25 * 16f;
		Assert.True(Dodge.Evaluate(body, P, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { modelled }, 0, 0).Hits);
		var straight = new Threat(hand.X, hand.Y, hand.Width, hand.Height, hand.VX, hand.VY);
		Assert.False(Dodge.Evaluate(body, P, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { straight }, 0, 0).Hits);
		Assert.False(Dodge.Best(body, P, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { modelled }, 0).Hits);

		// In the fight, standing under the hovering head: the dodge takes over before the slam.
		var (_, ctx, fight) = Setup(head, hand);
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: out {ctx.Output}, {fight.Dodges} dodges");
		Assert.Equal(1, fight.Dodges);
		Assert.True(ctx.Output.Horizontal != 0 || ctx.Output.Jump);
	}

	[Fact]
	public void ASwingingHand_SweepsAtUsOnce500PxOut_AndTheModelKnowsWhen()
	{
		// ai[2] = 4: out sideways (velocity.X ±0.1 a tick up to 8) until its centre is 500 px from the head's, then 17 px/tick at us.
		EntityInfo head = Head();
		float handX = head.CenterX + SkeletronPattern.SweepFromPx - 26f - 20f; // 20 px short of the turn, going out at 8
		EntityInfo hand = Hand(3, handX, PlayerTop, -1f, 4, 8f, 0f);
		Threat modelled = SkeletronPattern.PredictHand(hand, head, expert: false);
		output.WriteLine($"sweep at tick {modelled.DashAt}");
		Assert.InRange(modelled.DashAt, 3, 5);
		Assert.Equal(SkeletronPattern.SweepSpeed, modelled.DashSpeed);
	}

	[Fact]
	public void ALungingHand_IsDodged()
	{
		// A slam under way (ai[2] = 2): a straight line at where we stood when it set off, 18 px/tick.
		float hx = BodyX + 120f, hy = PlayerTop - 160f;
		float dx = BodyX - hx, dy = PlayerTop - hy, d = System.MathF.Sqrt(dx * dx + dy * dy);
		var (_, ctx, fight) = Setup(Head(), Hand(2, hx, hy, -1f, 2, dx / d * 18f, dy / d * 18f));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: out {ctx.Output}, {fight.Dodges} dodges");
		Assert.Equal(1, fight.Dodges);
	}

	[Fact]
	public void NearDawn_TheFightDrinksARecallPotion_AndEndsEscaped()
	{
		// Spec 3e/S7: at night time 30600 (1800 ticks before dawn) with the head alive, a recall potion: at dawn it does 1000.
		var (g, ctx, fight) = Setup(Head());
		g.Items[FakeGame.RecallPotion] = 1;
		ctx.NightTicksLeft = BossFightSkill.DawnEscapeTicks - 10;
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		output.WriteLine($"{fight.State}: out {ctx.Output}");
		Assert.StartsWith("escaping", fight.State);
		Assert.Equal(ToolRequest.Item, ctx.Output.Tool);
		Assert.Equal(FakeGame.RecallPotion, ctx.Output.ToolItem);
		g.Items[FakeGame.RecallPotion] = 0; // drunk
		Assert.Equal(SkillStatus.Failed, fight.Tick(ctx));
		Assert.StartsWith("escaped", fight.FailureReason);
		Assert.Contains("dawn", fight.Escaped);
	}

	[Fact]
	public void ByDay_OrWithAGuardianInSight_TheFightEscapes_WithNoPotionItKitesOn()
	{
		var (g, ctx, fight) = Setup(Head());
		g.Items[FakeGame.RecallPotion] = 1;
		ctx.IsNight = false;
		fight.Tick(ctx);
		Assert.Contains("it is day", fight.State);

		var guardian = new EntityInfo(9, SkeletronPattern.Guardian, "Dungeon Guardian", BodyX + 400f, PlayerTop - 200f, 80, 102, -8f, 0f, 9999, 9999, 1000, SkeletronPattern.HeadAiStyle);
		var (g2, ctx2, fight2) = Setup(Head(), guardian);
		g2.Items[FakeGame.RecallPotion] = 1;
		fight2.Tick(ctx2);
		Assert.Contains("Dungeon Guardian", fight2.State);

		var (_, ctx3, fight3) = Setup(Head());
		ctx3.NightTicksLeft = 100;
		Assert.Equal(SkillStatus.Running, fight3.Tick(ctx3));
		Assert.DoesNotContain("escaping", fight3.State);
	}

	private sealed class ListLog : IBotLog
	{
		private readonly List<string> lines;
		public ListLog(List<string> lines) => this.lines = lines;
		public void Info(string m) => lines.Add(m);
		public void Warn(string m) => lines.Add(m);
		public void Error(string m, System.Exception ex = null) => lines.Add(m);
		public void Debug(string m) { }
	}
}
