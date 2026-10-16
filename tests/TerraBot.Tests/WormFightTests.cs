using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 10: fighting the Eater of Worlds (<see cref="WormPattern"/>) with <see cref="BossFightSkill"/>'s worm tactics.
/// The field: air down to row 24 (the feet row), ground from row 25 to 29; the bot stands on it at column 97, the
/// arena over columns 50-145 as in <see cref="ArenaFightTests"/>.
/// </summary>
public class WormFightTests
{
	private readonly ITestOutputHelper output;
	public WormFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly Core.Physics.PhysicsParams P = Core.Physics.PhysicsParams.Default;
	/// <summary>ItemID.WormFood.</summary>
	private const int WormFood = 70;
	private const int Head = WormPattern.EaterHead, Bod = WormPattern.EaterBody, Tail = WormPattern.EaterTail;
	/// <summary>Every segment is 38 × 38 (NPC.cs:4007-4065).</summary>
	private const int Size = 38;

	/// <summary>A segment with its top-left at tile (<paramref name="tx"/>, <paramref name="ty"/>).</summary>
	private static EntityInfo Seg(int id, int type, float tx, float ty, float vx = 0f, float vy = 0f, float ai0 = 0f, float ai1 = 0f) =>
		new(id, type, "Eater of Worlds", tx * 16f, ty * 16f, Size, Size, vx, vy, 150, 150, type == Head ? 22 : type == Bod ? 13 : 11,
			WormPattern.WormAiStyle, ai0, ai1);

	private (FakeGame g, BotContext ctx, BossFightSkill fight) Setup(float bodyX, float vx, (int, int, int)? arena, params EntityInfo[] segs)
	{
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new Core.Physics.BodyState { X = bodyX, Y = 25 * 16f - P.Height, VX = vx, ReleaseJump = true };
		ctx.Hostiles = segs;
		var fight = new BossFightSkill(WormFood, Head) { Arena = arena };
		fight.Start(ctx);
		return (g, ctx, fight);
	}

	private static readonly (int, int, int) Arena = (50, 145, 24);

	[Fact]
	public void TheEaterOfWorlds_IsFoughtAsAWorm_TheEyeIsNot()
	{
		Assert.True(new BossFightSkill(WormFood, Head).IsWorm);
		Assert.False(new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc, FakeGame.ServantNpc).IsWorm);
	}

	[Fact]
	public void Shoots_TheNearestSegmentInAClearLine()
	{
		// Nearest a body in the ground (no line to it), then a body in the air 6 tiles left and up, then the head farther.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena,
			Seg(3, Head, 112, 10), Seg(1, Bod, 99, 27), Seg(2, Bod, 91, 18));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, aim {ctx.Output}");
		Assert.Equal(2, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
		Assert.Equal(3, fight.SegmentsInView);
	}

	[Fact]
	public void RunsAway_FromAHeadComingAtItThroughTheGround()
	{
		// Running right; the head comes from the right, under the ground and rising, its bodies behind it.
		var (_, ctx, fight) = Setup(97 * 16f, 2f, Arena,
			Seg(1, Head, 117, 27, -4f, -2f), Seg(2, Bod, 119, 27), Seg(3, Bod, 121, 27), Seg(4, Tail, 123, 27));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}, {fight.Dodges} dodges");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void RunsFrom_WhereAHeadInTheAirWillFall_NotWhereItIs()
	{
		// The head is out of the ground a little to the left, rising and moving right: under gravity it comes down some 18
		// tiles to the right, so the way from it is left (in a straight line it would never come down, still on the left).
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 94, 15, 4f, -3f), Seg(2, Bod, 92, 17), Seg(3, Bod, 90, 19));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}, {fight.Dodges} dodges");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void AFallingHeadOverhead_IsDodged()
	{
		// Standing still under a head at the top of its arc, three tiles up: in a straight line it would hang there, but it
		// falls onto us in some 30 ticks. The falling-head model sees the hit coming and dodges.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 97, 17));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> {ctx.Output}, {fight.Dodges} dodges");
		Assert.Equal(1, fight.Dodges);
	}

	[Fact]
	public void NoEyeDashModel_ForAWorm()
	{
		// A head beside us in the air, its AI fields what the Eye's would be on the tick a dash starts (ai[0] = 0 first form,
		// ai[1] = 1 dash now). The Eye's model would see a dash at us and dodge; a worm's head just falls, beside us.
		var lines = new System.Collections.Generic.List<string>();
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 104, 20, 0f, 0f, 0f, 1f));
		ctx.Log = new ListLog(lines);
		for (int t = 0; t < 5; t++) {
			ctx.Tick = t;
			fight.Tick(ctx);
		}
		output.WriteLine($"{fight.Describe()}, {fight.Dodges} dodges; " + string.Join(" | ", lines));
		Assert.Equal(0, fight.Dodges);
		Assert.DoesNotContain(lines, l => l.Contains("dash"));
	}

	[Fact]
	public void AtTheLeashEnd_WithoutAnArena_RunsPastTheHeadInsteadOfOut()
	{
		// No arena: the fight keeps within 40 tiles of where it began. At that end, a head coming from inside: the way from
		// it leads out, so the bot runs back past it.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, null);
		ctx.Hostiles = new[] { Seg(9, Bod, 60, 27) };
		fight.Tick(ctx); // the fight's centre: column 97
		ctx.Tick++;
		ctx.Body = ctx.Body with { X = (97 + BossFightSkill.HardHalfWidth) * 16f, VX = 0f };
		ctx.Hostiles = new[] { Seg(1, Head, 130, 27, 3f, -2f), Seg(2, Bod, 128, 27) };
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void AfterASplit_TheFightGoesOnWhileBodiesRemain()
	{
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 80, 27), Seg(2, Bod, 82, 27), Seg(3, Bod, 84, 27), Seg(4, Tail, 86, 27));
		fight.Tick(ctx);
		// The head is dead: what is left is a body (a new head in the game, but any segment will do) and the tail.
		ctx.Hostiles = new[] { Seg(3, Bod, 84, 27), Seg(4, Tail, 86, 27) };
		for (int t = 1; t <= WormPattern.GoneTicks + 30; t++) {
			ctx.Tick = t;
			Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		}
		output.WriteLine(fight.Describe());
		Assert.Equal("", fight.FailureReason);
		Assert.Equal(2, fight.SegmentsInView);
	}

	[Fact]
	public void WithNoSegmentLeftAndTheBossDefeated_Succeeds()
	{
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 80, 27), Seg(2, Tail, 82, 27));
		fight.Tick(ctx);
		// The last segment alive sets the flag as it dies; while one is in view the fight is not over (another worm).
		g.BossesDefeated.Add(Head);
		ctx.Tick = 1;
		ctx.Hostiles = new[] { Seg(2, Tail, 82, 27) };
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		ctx.Tick = 2;
		ctx.Hostiles = System.Array.Empty<EntityInfo>();
		Assert.Equal(SkillStatus.Succeeded, fight.Tick(ctx));
		Assert.Equal("won", fight.State);
	}

	[Fact]
	public void NoSegmentInView_NotDefeated_FailsAfterGoneTicks()
	{
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 80, 27), Seg(2, Tail, 82, 27));
		fight.Tick(ctx);
		ctx.Hostiles = System.Array.Empty<EntityInfo>();
		SkillStatus s = SkillStatus.Running;
		int t = 0;
		while (s == SkillStatus.Running && t < 2 * WormPattern.GoneTicks) {
			ctx.Tick = ++t;
			s = fight.Tick(ctx);
		}
		output.WriteLine($"{fight.Describe()} after {t} ticks: {fight.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Equal(WormPattern.GoneTicks + 1, t);
		Assert.Contains("the worm left", fight.FailureReason);
	}

	[Fact]
	public void Bodies_GetTheirMotionMeasured()
	{
		// Bodies report no velocity (the AI moves them by position): the second tick's dodge and lead use the motion seen.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Bod, 91, 18));
		fight.Tick(ctx);
		ctx.Tick = 1;
		ctx.Hostiles = new[] { Seg(1, Bod, 91.5f, 18) };
		fight.Tick(ctx);
		Assert.Equal(1, fight.TargetId);
		Assert.Equal(1, fight.SegmentsInView);
	}

	private sealed class ListLog : IBotLog
	{
		private readonly System.Collections.Generic.List<string> lines;
		public ListLog(System.Collections.Generic.List<string> lines) => this.lines = lines;
		public void Info(string m) => lines.Add(m);
		public void Warn(string m) => lines.Add(m);
		public void Error(string m, System.Exception ex = null) => lines.Add(m);
		public void Debug(string m) { }
	}
}

/// <summary>The falling-head model and the crossing point (<see cref="WormPattern"/>).</summary>
public class WormPatternTests
{
	[Fact]
	public void AHeadInTheAir_FallsUnderGravity_OneInTheGroundGoesStraight()
	{
		var head = new EntityInfo(1, WormPattern.EaterHead, "Eater of Worlds", 0f, 0f, 38, 38, 2f, -5f, 150, 150, 22, WormPattern.WormAiStyle);
		Core.Physics.Threat air = WormPattern.Predict(head, airborneHead: true, expert: false);
		Assert.Equal(WormPattern.AirGravity, air.Gravity);
		Assert.Equal(WormPattern.HeadTopSpeed, air.MaxFallSpeed);
		Assert.Equal(0f, WormPattern.Predict(head, airborneHead: false, expert: false).Gravity);
	}

	[Fact]
	public void Airborne_OnlyOutOfTilesAndWithinFreeFlightRange()
	{
		FakeGame g = EarlyGameTests.Field(200);
		var inAir = new EntityInfo(1, WormPattern.EaterHead, "", 50 * 16f, 10 * 16f, 38, 38, 0f, 0f, 150, 150, 22, WormPattern.WormAiStyle);
		var inGround = inAir with { Y = 26 * 16f };
		Assert.True(WormPattern.Airborne(inAir, g.Grid, 50 * 16f, 20 * 16f));
		Assert.False(WormPattern.Airborne(inGround, g.Grid, 50 * 16f, 20 * 16f));
		Assert.False(WormPattern.Airborne(inAir, g.Grid, 50 * 16f + WormPattern.FreeFlightPx + 16f, 20 * 16f));
	}

	[Fact]
	public void CrossingX_OfARisingHead_IsWhereItComesDown()
	{
		// Rising at 5 px/tick from 100 px above the line, moving right at 2: up 5² / (2 × 0.11) ≈ 114 px and down 214,
		// about 45 + 62 ticks, so it comes down some 214 px to the right.
		var head = new EntityInfo(1, WormPattern.EaterHead, "", 0f, 0f, 38, 38, 2f, -5f, 150, 150, 22, WormPattern.WormAiStyle);
		float y = head.CenterY + 100f;
		float x = WormPattern.CrossingX(head, airborne: true, y, expert: false);
		Assert.InRange(x - head.CenterX, 190f, 240f);
		Assert.Equal(head.CenterX, WormPattern.CrossingX(head, airborne: false, y, expert: false)); // rising away in a line
	}
}
