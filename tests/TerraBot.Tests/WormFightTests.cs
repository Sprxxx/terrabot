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
	public void AnEaterOfSoulsAboutToBite_IsShotFirst_TheWormAfter()
	{
		// Measured run M10k: held in a pocket, hit by segments and the zone's eaters of souls in turn, none of which the boss
		// fight shot; dead in the fight. One 5 tiles off is shot before a segment 7 tiles off; one far off is left to the worm.
		var eater = new EntityInfo(7, 6, "Eater of Souls", 92 * 16f, 21 * 16f, 30, 30, 0f, 0f, 40, 40, 22, FakeGame.FlyingAi);
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(2, Bod, 90, 18), eater);
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}");
		Assert.Equal(7, fight.TargetId);
		Assert.Contains("Eater of Souls", fight.State);
		ctx.Tick = 1;
		ctx.Hostiles = new[] { Seg(2, Bod, 90, 18), eater with { X = 60 * 16f } };
		fight.Tick(ctx);
		Assert.Equal(2, fight.TargetId);
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
	public void NoSegmentInView_ButItsBarShowsLife_TheFightWaitsForTheWorm_BackOnTheArena()
	{
		// Measured run M10j: "no segment of the worm in view" at (1914,361), in a chasm's water, the worm at 8597 of 10050 on
		// its bar, and the fight was over: the Worm Food used up for nothing. Burrowed out of view, the worm is still up.
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(1, Head, 80, 27), Seg(2, Tail, 82, 27));
		fight.Tick(ctx);
		ctx.Hostiles = System.Array.Empty<EntityInfo>();
		g.BossBars[Head] = 8597;
		for (int t = 1; t <= 3 * WormPattern.GoneTicks; t++) {
			ctx.Tick = t;
			Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		}
		output.WriteLine($"waiting: {fight.Describe()}");
		Assert.Equal("", fight.FailureReason);

		// Knocked off the arena's east end (columns 50-145) meanwhile: back onto it, the worm still out of view.
		ctx.Body = ctx.Body with { X = 152 * 16f, VX = 0f };
		ctx.Tick++;
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		output.WriteLine($"off the arena: {fight.Describe()}");
		Assert.Contains("walking back to the arena", fight.State);

		// Its bar empty: despawned, the fight is over.
		g.BossBars[Head] = 0;
		ctx.Tick++;
		Assert.Equal(SkillStatus.Failed, fight.Tick(ctx));
		Assert.Contains("the worm left", fight.FailureReason);
	}

	[Fact]
	public void AFightTakenUp_WithTheWormUpOutOfView_DoesNotSummonAgain()
	{
		// Measured run M10j: the fight paused for breath ("surfacing (pausing Boss)") with the worm out of view. Taken up again,
		// the worm is on its bar, the Worm Food is used up: no summon (the game allows none while a head lives), a wait for it.
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena);
		g.BossBars[Head] = 8597;
		Assert.Equal(0, g.Count(WormFood));
		SkillStatus s = fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: {fight.FailureReason}");
		Assert.Equal(SkillStatus.Running, s);
		Assert.NotEqual("summoning", fight.State);
		Assert.Equal("", fight.FailureReason);
	}

	[Fact]
	public void WithoutAnArena_DownAChasmDeeperThanASafeFall_ClimbsBackToWhereTheFightBegan()
	{
		// Measured run M10j: no arena, the fight began at (1874,291) on the corruption's open ground; the bot ran down a chasm
		// to (1921,358), under water, where the worm was lost. A walk never drops deeper than a safe fall on its own.
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 25; y++)
			rows.Add(y == 24 ? "...S" + new string('.', 196) : new string('.', 200));
		for (int y = 25; y < 70; y++) {
			char[] r = new string('#', 200).ToCharArray();
			if (y < 65)
				for (int x = 118; x <= 125; x++)
					r[x] = '.'; // a chasm 40 rows deep
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new Core.Physics.BodyState { X = 97 * 16f, Y = 25 * 16f - P.Height, ReleaseJump = true };
		ctx.Hostiles = new[] { Seg(1, Head, 80, 27), Seg(2, Tail, 82, 27) };
		var fight = new BossFightSkill(WormFood, Head);
		fight.Start(ctx);
		fight.Tick(ctx); // the fight's centre: (97,24)

		// Within a safe fall of the start: fought on from there.
		ctx.Tick = 1;
		ctx.Body = new Core.Physics.BodyState { X = 120 * 16f, Y = (24 + P.SafeFallTiles + 1) * 16f - P.Height, ReleaseJump = true };
		fight.Tick(ctx);
		output.WriteLine($"at {ctx.Node}: {fight.Describe()}");
		Assert.DoesNotContain("walking back", fight.State);

		// At the chasm's floor, 40 rows down: back up.
		ctx.Tick = 2;
		ctx.Body = new Core.Physics.BodyState { X = 120 * 16f, Y = 65 * 16f - P.Height, ReleaseJump = true };
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		output.WriteLine($"at {ctx.Node}: {fight.Describe()}");
		Assert.Contains("walking back to where the fight began", fight.State);
	}

	/// <summary>The body standing on the field's feet row 24 at node column <paramref name="x"/>, moving at <paramref name="vx"/>.</summary>
	private static Core.Physics.BodyState StandingAt(int x, float vx) =>
		new() { X = Core.Pathfinding.Body.CanonicalLeft(x, P), Y = 25 * 16f - P.Height, VX = vx, ReleaseJump = true };

	[Fact]
	public void OutOfTheZone_OnTheArena_RunsBackIn_AndThenKitesShortOfWhereItWasLost()
	{
		// Measured runs M10m and M10n (world_eow): on the arena at (1872,239) the game's Corruption count (read from M10m's
		// checkpoint worlds) fell under the zone's 300 east of x 1932; kiting east to (1935,245), and from (1933,245), the bot
		// left the zone, and "the worm left: no segment in view for 320 ticks, not defeated, its bar empty", twice. Here the
		// game's flag says the zone holds up to column 110 of the arena (50-145).
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(9, Bod, 60, 27));
		var lines = new System.Collections.Generic.List<string>();
		ctx.Log = new ListLog(lines);
		void At(int x, float vx)
		{
			ctx.Tick++;
			ctx.Body = StandingAt(x, vx);
			ctx.Combat.InEvilZone = x <= 110;
			fight.Tick(ctx);
			output.WriteLine($"at {ctx.Node} ({(ctx.Combat.InEvilZone == true ? "in" : "out of")} the zone): {fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		}
		At(97, 0f);
		// Run out of it east (no head near): back west, though the kiting's turn (20 from the centre) is still 5 columns on.
		At(112, 2.5f);
		Assert.Equal(-1, ctx.Output.Horizontal);
		Assert.Contains("out of the zone", fight.State);
		Assert.Contains(lines, l => l.Contains("out of the Corruption zone at (112,24)"));
		// Back in it and running east again: turned short of where it was lost, not out of it a second time.
		At(108, 2.5f);
		Assert.Equal(-1, ctx.Output.Horizontal);
		Assert.Contains(lines, l => l.Contains("back in the Corruption zone"));
	}

	[Fact]
	public void TheFloorsColumnsWhereTheZoneWasSeenNotToHold_BoundTheKiting_BeforeItIsLostInTheFight()
	{
		// Measured run M10n: the build walked the whole floor of the arena at (1872,239), the zone holding west of x 1933 only
		// (the game's count), and the fight then began at (1933,245) and kited east out of it. What the bot saw standing on
		// the floor (BotContext.Zone) bounds the kiting from the start: here the zone held up to column 104.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Seg(9, Bod, 60, 27));
		for (int x = 50; x <= 145; x++)
			ctx.Zone.Note(new Core.Grid.TilePos(x, 24), x <= 104, -1000 + x);
		ctx.Combat.InEvilZone = true;
		fight.Tick(ctx);
		ctx.Tick = 1;
		ctx.Body = StandingAt(101, 2.5f);
		fight.Tick(ctx);
		output.WriteLine($"at {ctx.Node}: {fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal); // the bound is 2 columns short of 105, the first seen out
		Assert.True(ctx.InEvilZone(ctx.Node));
	}

	[Fact]
	public void OutOfTheZone_TheWormFoodIsNotUsed_TheFightFailsAtOnce()
	{
		// Worm Food does nothing out of the zone (Player.cs:43462), and the worm would not stay there (NPC.cs:55436).
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena);
		g.Items[WormFood] = 1;
		ctx.Combat.InEvilZone = false;
		SkillStatus s = fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: {fight.FailureReason}");
		Assert.Equal(SkillStatus.Failed, s);
		Assert.Contains("not in the Corruption zone", fight.FailureReason);
		Assert.Equal(1, g.Count(WormFood));
	}

	[Fact]
	public void WithoutAnArena_OutOfTheZone_WalksBackToWhereItLastHeld()
	{
		// No arena: the leash is 40 tiles each way, wider than the zone may be. Out of the zone 23 columns from where it held,
		// within the leash: a walk back there (the ground need not be flat), not the kiting on.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, null, Seg(9, Bod, 60, 27));
		ctx.Combat.InEvilZone = true;
		fight.Tick(ctx);
		ctx.Tick = 1;
		ctx.Body = StandingAt(120, 0f);
		ctx.Combat.InEvilZone = false;
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		output.WriteLine($"at {ctx.Node}: {fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Contains("walking back", fight.State);
		Assert.Equal(-1, ctx.Output.Horizontal);
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

	/// <summary>
	/// The Eater of Worlds as the game moves it, for closed-loop tests (NPC.AI_006_Worms): the head on its velocity, falling at
	/// 0.11 px/tick² up to 10 in open air (NPC.cs:55366-55378; in the ground it goes straight on here, where the game steers it
	/// at the player); each segment behind it pulled along the line to the one ahead until 38 px from it, its velocity zeroed
	/// (NPC.cs:55280-55327). Ids 1, 2, ... from the head; each segment's ai[1] is the one ahead (NPC.cs:54749).
	/// </summary>
	private sealed class WormSim
	{
		public readonly float[] Cx, Cy;
		public float HeadVX, HeadVY;
		public const float Spacing = Size;

		/// <summary><paramref name="n"/> segments in a line back from the head's centre, against (<paramref name="dx"/>, <paramref name="dy"/>), moving that way at <paramref name="speed"/>.</summary>
		public WormSim(int n, float headCx, float headCy, float dx, float dy, float speed)
		{
			float d = System.MathF.Sqrt(dx * dx + dy * dy);
			dx /= d;
			dy /= d;
			Cx = new float[n];
			Cy = new float[n];
			for (int k = 0; k < n; k++) {
				Cx[k] = headCx - k * Spacing * dx;
				Cy[k] = headCy - k * Spacing * dy;
			}
			(HeadVX, HeadVY) = (dx * speed, dy * speed);
		}

		public EntityInfo[] Perceived() => Enumerable.Range(0, Cx.Length).Select(k =>
			Seg(k + 1, k == 0 ? Head : k == Cx.Length - 1 ? Tail : Bod, (Cx[k] - Size / 2f) / 16f, (Cy[k] - Size / 2f) / 16f,
				k == 0 ? HeadVX : 0f, k == 0 ? HeadVY : 0f, ai1: k)).ToArray();

		public void Step(Core.Grid.ITileGrid grid, Core.Physics.BodyState player)
		{
			if (WormPattern.Airborne(Perceived()[0], grid, player.X, player.Y))
				HeadVY = System.MathF.Min(HeadVY + WormPattern.AirGravity, WormPattern.HeadTopSpeed);
			Cx[0] += HeadVX;
			Cy[0] += HeadVY;
			for (int k = 1; k < Cx.Length; k++) {
				float dx = Cx[k - 1] - Cx[k], dy = Cy[k - 1] - Cy[k], d = System.MathF.Sqrt(dx * dx + dy * dy);
				if (d <= 0f)
					continue;
				float f = (d - Spacing) / d;
				Cx[k] += dx * f;
				Cy[k] += dy * f;
			}
		}

		public bool Touches(Core.Physics.BodyState s) => Enumerable.Range(0, Cx.Length).Any(k =>
			Core.Physics.Dodge.Gap(s.X, s.Y, P.Width, P.Height, Cx[k] - Size / 2f, Cy[k] - Size / 2f, Size, Size) < 0f);
	}

	/// <summary>
	/// The fight against <paramref name="worm"/> for <paramref name="ticks"/> ticks, the body moved by the fake's tile collision:
	/// the hits it takes, a touch hurting only once the 40 ticks of immunity after the last one are over (Player.Hurt, as
	/// <see cref="BotContext.HurtImmunityTicks"/>). The bot has just been hit at tick 0.
	/// </summary>
	private int HitsTaken(FakeGame g, BotContext ctx, BossFightSkill fight, WormSim worm, int ticks)
	{
		int hits = 0;
		long lastHit = 0;
		ctx.Tick = 0;
		ctx.NoteHit(13);
		for (int t = 1; t <= ticks; t++) {
			ctx.Tick = t;
			ctx.Body = g.Sim.State;
			ctx.Hostiles = worm.Perceived();
			fight.Tick(ctx);
			g.Sim.Step(ctx.Output);
			worm.Step(ctx.Grid, g.Sim.State);
			if (worm.Touches(g.Sim.State) && t - lastHit >= BotContext.HurtImmunityTicks) {
				hits++;
				lastHit = t;
				ctx.NoteHit(13);
				output.WriteLine($"t{t}: hit at {g.Sim.Node} {g.Sim.State}, state {fight.State}");
			}
		}
		output.WriteLine($"{hits} hits in {ticks} ticks; at {g.Sim.Node} {g.Sim.State}; {fight.Dodges} dodges");
		return hits;
	}

	[Fact]
	public void BodiesStreamingUpThroughItsSpot_JustHit_ItMovesClearBeforeTheImmunityEnds()
	{
		// Measured runs M10o and M10p: hit after hit as the bodies behind a head that came up through the bot streamed on
		// through its spot, "running past" the head into them (M10o fight 2: 8 hits in a row from x 1885 to 1879 on the gully's
		// flat floor, each 40 ticks after the last, the bot running right at 2 px/tick into segments on its right). Every dodge
		// plan touched the stream at once, so all tied at "hit in 1 tick" and the kiting's way won. Here a head rose out of
		// the ground at the arena's west end, up and to the right, through the bot; its bodies come up after it along the same
		// line, through the bot's lower right. Out of their way by the time the immunity wears off: no more hits.
		var (g, ctx, fight) = Setup(53 * 16f, 0f, Arena);
		g.Sim.State = ctx.Body = StandingAt(53, 0f);
		float cx = ctx.Body.CenterX(P);
		float dx = 0.35f, dy = -0.94f; // up and to the right, steeply
		var worm = new WormSim(30, cx + 20f + 120f * dx, 400f + 120f * dy, dx, dy, 9f);
		Assert.True(worm.Touches(ctx.Body)); // the hit at tick 0
		int hits = HitsTaken(g, ctx, fight, worm, 160);
		Assert.Equal(0, hits);
	}

	[Fact]
	public void InANarrowPit_BodiesPassingThroughIt_ItJumpsOutOfThePit()
	{
		// Measured runs M10o and M10p: 23 and 15 hits at (1721,268), 11 at (1727,268), the body boxed in 2- and 3-column pits
		// a few rows below the ground beside them (x 27520-27553 px), the worm in the ground going through the pit and the bot
		// in it. The dodge planner took the pit's sides for walls as high as the sky, so no plan got out of it. Here a pit 2
		// columns wide and 3 rows deep, the worm going through it level with the bot, head first (the hit at tick 0).
		var rows = new System.Collections.Generic.List<string>();
		for (int y = 0; y < 25; y++)
			rows.Add(new string('.', 200));
		for (int y = 25; y < 60; y++) {
			char[] r = new string('#', 200).ToCharArray();
			if (y <= 27)
				r[96] = r[97] = '.';
			if (y == 27)
				r[96] = 'S';
			rows.Add(new string(r));
		}
		var g = new FakeGame(rows.ToArray());
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new Core.Physics.ProjectileModel(9f, 15, 0.1f, 16f);
		g.Sim.State = ctx.Body = new Core.Physics.BodyState { X = 96 * 16f + 6f, Y = 28 * 16f - P.Height, ReleaseJump = true };
		var fight = new BossFightSkill(WormFood, Head);
		fight.Start(ctx);
		float cx = ctx.Body.CenterX(P), cy = ctx.Body.CenterY(P);
		var worm = new WormSim(30, cx + 30f, cy, 1f, 0f, 9f);
		Assert.True(worm.Touches(ctx.Body));
		int hits = HitsTaken(g, ctx, fight, worm, 160);
		Assert.Equal(0, hits);
		Assert.True(g.Sim.Node.Y <= 24, $"still in the pit at {g.Sim.Node}");
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

	[Fact]
	public void ABodyAtABend_IsPredictedToFollowTheChainRoundIt_NotToGoOnStraight()
	{
		// A head in the ground (no tiles here, so off the grid: Airborne is false far from the player) turned up; the body behind
		// it still moves right, as it last did. It follows the head up (NPC.cs:55280-55327), where a straight line takes it on
		// right; 20 ticks on it is some 140 px above where it is, not 180 px to its right.
		FakeGame g = EarlyGameTests.Field(200);
		var head = new EntityInfo(1, WormPattern.EaterHead, "", 100 * 16f, 10 * 16f, 38, 38, 0f, -9f, 150, 150, 22, WormPattern.WormAiStyle);
		var body = new EntityInfo(2, WormPattern.EaterBody, "", head.X - 38f, head.Y, 38, 38, 9f, 0f, 150, 150, 13, WormPattern.WormAiStyle, 0f, 1f);
		Core.Physics.Threat[] chain = WormPattern.PredictChain(new[] { head, body }, g.Grid, 0f, 0f, expert: false);
		float bx = chain[1].PathX[19], by = chain[1].PathY[19];
		Assert.Equal(head.X, chain[0].PathX[19], 3);
		Assert.Equal(head.Y - 20 * 9f, chain[0].PathY[19], 3);
		Assert.InRange(bx - body.X, 0f, 45f);
		Assert.InRange(body.Y - by, 120f, 180f);
		// It keeps its distance behind the head.
		float dx = chain[0].PathX[19] - bx, dy = chain[0].PathY[19] - by;
		Assert.Equal(38f, System.MathF.Sqrt(dx * dx + dy * dy), 1);
	}
}
