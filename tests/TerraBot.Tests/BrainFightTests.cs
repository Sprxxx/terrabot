using System;
using System.Collections.Generic;
using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 10 in a Crimson world: fighting the Brain of Cthulhu (<see cref="BrainPattern"/>) with <see cref="BossFightSkill"/>'s
/// Brain tactics. The field: air down to row 24 (the feet row), ground from row 25 to 29; the bot stands on it at column 97, the
/// arena over columns 50-145 as in <see cref="WormFightTests"/>.
/// </summary>
public class BrainFightTests
{
	private readonly ITestOutputHelper output;
	public BrainFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly PhysicsParams P = PhysicsParams.Default;
	private const int Spine = FakeGame.BloodySpine, BrainType = BrainPattern.Brain, CreeperType = BrainPattern.Creeper;
	private static readonly (int, int, int) Arena = (50, 145, 24);

	/// <summary>The Brain (160 x 110, NPC.cs:7590-7608) with its centre at tile (<paramref name="tx"/>, <paramref name="ty"/>); hiding unless <paramref name="exposed"/>.</summary>
	internal static EntityInfo Brain(float tx, float ty, bool exposed = false, float vx = 0f, float vy = 0f, int life = 1250) =>
		new(1, BrainType, "Brain of Cthulhu", tx * 16f - 80f, ty * 16f - 55f, 160, 110, vx, vy, life, 1250, 30, BrainPattern.BrainAiStyle,
			Ai0: exposed ? -1f : 0f, DontTakeDamage: !exposed);

	/// <summary>A Creeper (30 x 30, NPC.cs:7609-7622) with its centre at tile (<paramref name="tx"/>, <paramref name="ty"/>); dashing (ai[0] 1) if <paramref name="dashing"/>.</summary>
	internal static EntityInfo Creeper(int id, float tx, float ty, float vx = 0f, float vy = 0f, bool dashing = false) =>
		new(id, CreeperType, "Creeper", tx * 16f - 15f, ty * 16f - 15f, 30, 30, vx, vy, 100, 100, 20, BrainPattern.CreeperAiStyle, Ai0: dashing ? 1f : 0f);

	private (FakeGame g, BotContext ctx, BossFightSkill fight) Setup(float bodyX, float vx, (int, int, int)? arena, params EntityInfo[] hostiles)
	{
		FakeGame g = EarlyGameTests.Field(200).UseCrimson();
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new ProjectileModel(9f, 15, 0.1f, 16f);
		ctx.Body = new BodyState { X = bodyX, Y = 25 * 16f - P.Height, VX = vx, ReleaseJump = true };
		ctx.Hostiles = hostiles;
		g.Items[Spine] = 1;
		var fight = new BossFightSkill(Spine, BrainType, CreeperType) { Arena = arena };
		fight.Start(ctx);
		return (g, ctx, fight);
	}

	[Fact]
	public void TheBrain_IsFoughtByItsOwnRules()
	{
		var fight = new BossFightSkill(Spine, BrainType, CreeperType);
		Assert.True(fight.IsBrain);
		Assert.False(fight.IsWorm);
		Assert.False(fight.IsSkeletron);
		Assert.False(new BossFightSkill(FakeGame.WormFood, WormPattern.EaterHead).IsBrain);
	}

	[Fact]
	public void WhileItHides_TheCreepersAreShot_NotTheBrain()
	{
		// The Brain nearer and in a clear line, but nothing hurts it while a Creeper lives (dontTakeDamage): the Creeper is shot.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(104, 14), Creeper(7, 88, 15));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, {fight.State}");
		Assert.Equal(7, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
		Assert.Contains("creeper", fight.State);
	}

	[Fact]
	public void ACreeperDashingAtUs_IsShotBeforeANearerOne()
	{
		// A dash ends at a hit (NPC.cs:33463-33489): the one coming at us at 8 px/tick from 12 tiles off goes first.
		float cx = 97 * 16f + 6f + P.Width / 2f, cy = 25 * 16f - P.Height / 2f;
		float dx = cx - 109 * 16f, dy = cy - 16 * 16f, d = MathF.Sqrt(dx * dx + dy * dy);
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(115, 10), Creeper(5, 91, 18), Creeper(6, 109, 16, dx / d * 8f, dy / d * 8f, dashing: true));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, {fight.State}");
		Assert.Equal(6, fight.TargetId);
		Assert.Contains("dashing at us", fight.State);
	}

	[Fact]
	public void OnceTheCreepersAreDead_TheBrainIsShot()
	{
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(110, 12, exposed: true, vx: -2f, vy: 1f, life: 900));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, {fight.State}");
		Assert.Equal(1, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
		Assert.Contains("the brain", fight.State);
	}

	[Fact]
	public void RunsAway_FromTheBrainsSwarm_WhenItIsNearAcross()
	{
		// Hiding, its centre 6 tiles to the right and 9 up: inside the swarm's reach across (SwarmKeepPx), so away, left.
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(104, 15), Creeper(2, 101, 13), Creeper(3, 106, 18));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal);
		// The other side: right.
		ctx.Tick = 1;
		ctx.Hostiles = new[] { Brain(90, 15), Creeper(2, 92, 13) };
		fight.Tick(ctx);
		Assert.Equal(1, ctx.Output.Horizontal);
	}

	[Fact]
	public void ChasedOnceExposed_RunsFromTheBrainFarther()
	{
		// Exposed it chases at 8 px/tick: 15 tiles off across is inside ChaseKeepPx (20 tiles); hiding there it is not (SwarmKeepPx).
		Assert.True(15 * 16f < BrainPattern.ChaseKeepPx && 15 * 16f > BrainPattern.SwarmKeepPx);
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(113, 10, exposed: true));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(-1, ctx.Output.Horizontal);
	}

	[Fact]
	public void AtTheArenasEnd_RunsPastTheBrain_InsteadOfOut()
	{
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(130, 10));
		fight.Tick(ctx); // the fight's centre: the arena's middle
		ctx.Tick++;
		ctx.Body = ctx.Body with { X = 52 * 16f, VX = 0f };
		ctx.Hostiles = new[] { Brain(58, 10) };
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()} -> horizontal {ctx.Output.Horizontal}");
		Assert.Equal(1, ctx.Output.Horizontal);
	}

	[Fact]
	public void OutOfTheCrimsonZone_TheSpineIsNotUsed_TheFightFailsAtOnce()
	{
		// The Bloody Spine does nothing out of the Crimson zone (Player.cs:43549-43552), and the Brain would sink away (NPC.cs:33385).
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena);
		ctx.Combat.InEvilZone = false;
		Assert.Equal(SkillStatus.Failed, fight.Tick(ctx));
		Assert.Contains("not in the Crimson zone", fight.FailureReason);
	}

	[Fact]
	public void InTheZone_TheSpineIsUsed()
	{
		var (_, ctx, fight) = Setup(97 * 16f, 0f, Arena);
		ctx.Combat.InEvilZone = true;
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		Assert.Equal("summoning", fight.State);
		Assert.Equal(ToolRequest.Item, ctx.Output.Tool);
		Assert.Equal(Spine, ctx.Output.ToolItem);
	}

	[Fact]
	public void WithTheBrainDefeated_AndNoneOfItInView_Succeeds()
	{
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(110, 12, exposed: true, life: 30));
		fight.Tick(ctx);
		g.BossesDefeated.Add(BrainType);
		ctx.Tick = 1;
		ctx.Hostiles = Array.Empty<EntityInfo>();
		Assert.Equal(SkillStatus.Succeeded, fight.Tick(ctx));
		Assert.Equal("won", fight.State);
	}

	[Fact]
	public void OutOfView_TheFightWaitsWhileItsBarShowsLife_AndFailsOnceItIsEmpty()
	{
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena, Brain(110, 12), Creeper(2, 108, 12));
		fight.Tick(ctx);
		ctx.Hostiles = Array.Empty<EntityInfo>();
		g.BossBars[BrainType] = 1350;
		for (int t = 1; t <= 3 * BossFightSkill.GoneTicks; t++) {
			ctx.Tick = t;
			Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		}
		g.BossBars[BrainType] = 0;
		ctx.Tick++;
		Assert.Equal(SkillStatus.Failed, fight.Tick(ctx));
		Assert.Contains("the brain left", fight.FailureReason);
	}

	/// <summary>
	/// The Brain of Cthulhu's fight as the game plays it, for the closed loop: the Brain's drift and its jumps while it hides
	/// (NPC.cs:33274-33383), its chase and jumps once exposed (NPC.cs:33157-33272), the Creepers' orbit and dashes (NPC.cs:33411-33489),
	/// each random roll by a seeded die at the AI's odds. The bot's arrows land after their flight (a straight line at 10 px/tick)
	/// with <see cref="HitChance"/>, doing the Tendon Bow's wooden-arrow damage less half the defence (Classic: 24 - 5 a Creeper, 24 - 7
	/// the Brain), and a hit ends a Creeper's dash. The field is open air over row 25's ground (EarlyGameTests.Field).
	/// </summary>
	private sealed class BrainSim
	{
		public float Bx, By, Bvx, Bvy;
		public int BrainLife = 1250;
		public readonly float[] Cx, Cy, Vx, Vy;
		public readonly int[] Life;
		public readonly bool[] Dash;
		private readonly Random rng;
		public int Dashes, Jumps, Shots, Landed, SinceJump;
		/// <summary>The share of arrows that land (the rest miss: a jump of the Brain, a Creeper's turn).</summary>
		public float HitChance = 0.6f;
		private int jumpIn, fade = -1;
		private float jumpX, jumpY;
		private readonly List<(int Target, int At)> flying = new();
		public bool Exposed => Life.All(l => l <= 0);
		public bool Dead => BrainLife <= 0;

		public BrainSim(int seed, float bx, float by)
		{
			rng = new Random(seed);
			(Bx, By) = (bx, by);
			int n = BrainPattern.CreeperCount;
			(Cx, Cy, Vx, Vy, Dash, Life) = (new float[n], new float[n], new float[n], new float[n], new bool[n], new int[n]);
			for (int k = 0; k < n; k++) {
				// NPC.cs:33103-33113: round the centre within its width and height, velocity -3..3 on each axis.
				Cx[k] = bx + rng.Next(-160, 160);
				Cy[k] = by + rng.Next(-110, 110);
				Vx[k] = rng.Next(-30, 31) * 0.1f;
				Vy[k] = rng.Next(-30, 31) * 0.1f;
				Life[k] = 100;
			}
			jumpIn = 120 + rng.Next(300);
		}

		public IReadOnlyList<EntityInfo> Perceived()
		{
			var all = new List<EntityInfo>();
			if (!Dead)
				all.Add(new EntityInfo(1, BrainType, "Brain of Cthulhu", Bx - 80f, By - 55f, 160, 110, Bvx, Bvy, Math.Max(1, BrainLife), 1250, 30,
					BrainPattern.BrainAiStyle, Ai0: Exposed ? (fade >= 0 ? -2f : -1f) : 0f, DontTakeDamage: !Exposed));
			for (int k = 0; k < Cx.Length; k++)
				if (Life[k] > 0)
					all.Add(new EntityInfo(10 + k, CreeperType, "Creeper", Cx[k] - 15f, Cy[k] - 15f, 30, 30, Vx[k], Vy[k], Life[k], 100, 20,
						BrainPattern.CreeperAiStyle, Ai0: Dash[k] ? 1f : 0f));
			return all;
		}

		/// <summary>An arrow loosed at <paramref name="target"/> (the fight's TargetId) from the player's centre at tick <paramref name="t"/>.</summary>
		public void Shoot(int target, float pcx, float pcy, int t)
		{
			float tx, ty;
			if (target == 1) {
				(tx, ty) = (Bx, By);
			}
			else {
				int k = target - 10;
				if (k < 0 || k >= Cx.Length || Life[k] <= 0)
					return;
				(tx, ty) = (Cx[k], Cy[k]);
			}
			Shots++;
			float d = MathF.Sqrt((tx - pcx) * (tx - pcx) + (ty - pcy) * (ty - pcy));
			flying.Add((target, t + (int)(d / 10f) + 1));
		}

		private void Land(int t)
		{
			for (int i = flying.Count - 1; i >= 0; i--) {
				if (flying[i].At > t)
					continue;
				int target = flying[i].Target;
				flying.RemoveAt(i);
				if (rng.NextDouble() >= HitChance)
					continue;
				if (target == 1) {
					if (Exposed && BrainLife > 0) {
						BrainLife -= 24 - 7;
						Landed++;
					}
				}
				else if (Life[target - 10] > 0) {
					Life[target - 10] -= 24 - 5;
					Dash[target - 10] = false;
					Landed++;
				}
			}
		}

		/// <summary>A jump's spot (NPC.cs:33188-33238 and 33320-33345): <paramref name="lo"/>-<paramref name="hi"/> tiles off the player on each axis, out of the ground.</summary>
		private void PickJump(float pcx, float pcy, float pvx, int lo, int hi)
		{
			for (int tries = 0; tries <= 100; tries++) {
				int ox = rng.Next(lo, hi + 1) * (rng.Next(2) == 0 ? -1 : 1), oy = rng.Next(lo, hi + 1) * (rng.Next(2) == 0 ? -1 : 1);
				float fx = ox * 16f, fy = oy * 16f;
				if (pvx * fx > 0f) { // the way the player runs: farther along it
					float len = MathF.Sqrt(fx * fx + fy * fy);
					fx += fx / len * 16f * MathF.Abs(pvx);
					fy += fy / len * 16f * MathF.Abs(pvx);
				}
				int x = (int)(pcx / 16f) + (int)(fx / 16f), y = (int)(pcy / 16f) + (int)(fy / 16f);
				if (y < 25 || tries == 100) {
					(jumpX, jumpY) = (x * 16f, y * 16f);
					return;
				}
			}
		}

		public void Step(float pcx, float pcy, float pvx, int t)
		{
			Land(t);
			SinceJump++;
			if (Dead)
				return;
			float dx = pcx - Bx, dy = pcy - By, d = MathF.Sqrt(dx * dx + dy * dy);
			if (Exposed) {
				if (d > 0f) {
					Bvx = (Bvx * 50f + dx / d * 8f) / 51f;
					Bvy = (Bvy * 50f + dy / d * 8f) / 51f;
				}
				if (fade < 0 && --jumpIn <= 0) {
					PickJump(pcx, pcy, pvx, 10, 12);
					fade = 11;
				}
				if (fade >= 0) {
					Bvx *= 0.9f;
					Bvy *= 0.9f;
					if (--fade < 0) {
						(Bx, By) = (jumpX, jumpY);
						jumpIn = 60 + rng.Next(120);
						Jumps++;
						SinceJump = 0;
					}
				}
			}
			else {
				(Bvx, Bvy) = d < 1f ? (dx, dy) : (dx / d, dy / d);
				if (fade < 0 && --jumpIn <= 0) {
					PickJump(pcx, pcy, pvx, 12, 40);
					fade = 51;
				}
				if (fade >= 0 && --fade < 0) {
					(Bx, By) = (jumpX, jumpY);
					jumpIn = 51 + 120 + rng.Next(300); // the fade in, then the count again
					Jumps++;
					SinceJump = 0;
				}
			}
			Bx += Bvx;
			By += Bvy;
			for (int k = 0; k < Cx.Length; k++) {
				if (Life[k] <= 0)
					continue;
				float ox = Bx - Cx[k], oy = By - Cy[k], od = MathF.Sqrt(ox * ox + oy * oy);
				if (Dash[k]) {
					if (od > BrainPattern.DashLeashPx)
						Dash[k] = false;
				}
				else if (od > BrainPattern.OrbitPx) {
					float s = 8f / od;
					Vx[k] = (Vx[k] * 15f + ox * s) / 16f;
					Vy[k] = (Vy[k] * 15f + oy * s) / 16f;
				}
				else {
					if (MathF.Abs(Vx[k]) + MathF.Abs(Vy[k]) < 8f) {
						Vx[k] *= 1.05f;
						Vy[k] *= 1.05f;
					}
					if (rng.Next(200) == 0) {
						float px = pcx - Cx[k], py = pcy - Cy[k], pd = MathF.Sqrt(px * px + py * py);
						(Vx[k], Vy[k]) = (px / pd * 8f, py / pd * 8f);
						Dash[k] = true;
						Dashes++;
					}
				}
				Cx[k] += Vx[k];
				Cy[k] += Vy[k];
			}
		}

		/// <summary>What touches the body: "brain", "dash", "orbit", or null.</summary>
		public string Touches(BodyState s)
		{
			if (!Dead && Dodge.Gap(s.X, s.Y, P.Width, P.Height, Bx - 80f, By - 55f, 160, 110) < 0f)
				return "brain";
			for (int k = 0; k < Cx.Length; k++)
				if (Life[k] > 0 && Dodge.Gap(s.X, s.Y, P.Width, P.Height, Cx[k] - 15f, Cy[k] - 15f, 30, 30) < 0f)
					return Dash[k] ? "dash" : "orbit";
			return null;
		}
	}

	/// <summary>
	/// The fight against the sim for up to <paramref name="ticks"/> ticks (40 ticks of immunity after each hit, Player.Hurt), the bot's
	/// arrows shot every 30 ticks (the Tendon Bow's useTime) while it holds use (and with <paramref name="standing"/> only shooting, never
	/// moving); or, not <paramref name="fighting"/>, the bot standing, shooting nothing.
	/// </summary>
	private (int Hits, string By, int Ticks, BrainSim Sim) Play(int seed, bool fighting, int ticks, bool standing = false)
	{
		var (g, ctx, fight) = Setup(97 * 16f, 0f, Arena);
		g.Sim.State = ctx.Body = new BodyState { X = 97 * 16f + 6f, Y = 25 * 16f - P.Height, ReleaseJump = true };
		var sim = new BrainSim(seed, 115 * 16f, 10 * 16f);
		var by = new Dictionary<string, int>();
		int hits = 0, t = 0;
		long lastHit = -1000, lastShot = -1000;
		while (++t <= ticks && !sim.Dead) {
			ctx.Tick = t;
			ctx.Body = g.Sim.State;
			ctx.Hostiles = sim.Perceived();
			MoveInput o = MoveInput.None;
			float pcx = g.Sim.State.CenterX(P), pcy = g.Sim.State.CenterY(P);
			if (fighting) {
				fight.Tick(ctx);
				o = ctx.Output;
				if (o.UseItem && o.Tool == ToolRequest.Ranged && fight.TargetId is int target && t - lastShot >= 30) {
					sim.Shoot(target, pcx, pcy, t);
					lastShot = t;
				}
				o.UseItem = false;
				if (standing) { // shooting, but standing where it is
					o.Horizontal = 0;
					o.Jump = false;
				}
			}
			g.Sim.Step(o);
			sim.Step(g.Sim.State.CenterX(P), g.Sim.State.CenterY(P), g.Sim.State.VX, t);
			if (sim.Touches(g.Sim.State) is string what && t - lastHit >= BotContext.HurtImmunityTicks) {
				hits++;
				by[what] = by.GetValueOrDefault(what) + 1;
				lastHit = t;
				ctx.NoteHit(20);
			}
		}
		string byText = string.Join(", ", by.Select(kv => $"{kv.Value} {kv.Key}"));
		output.WriteLine($"seed {seed}, {(fighting ? standing ? "standing and shooting" : "fighting" : "standing")}: {hits} hits ({byText}) in {t - 1} ticks, {sim.Dashes} dashes, {sim.Jumps} jumps, "
			+ $"{sim.Landed}/{sim.Shots} arrows landed, creepers left {sim.Life.Count(l => l > 0)}, brain {sim.BrainLife}, at {g.Sim.Node}, {fight.Dodges} dodges");
		return (hits, byText, t - 1, sim);
	}

	[Fact]
	public void AgainstTheCreepersDashes_TheFightTakesFarFewerHits_ThanStandingStill()
	{
		// A minute of the first phase, the Brain 18 tiles right and 15 up to begin with: its 20 Creepers dash at 8 px/tick, about one
		// every 10 ticks. Standing still, about a hit every immunity (40 ticks).
		int fought = 0, stood = 0;
		for (int seed = 1; seed <= 3; seed++) {
			var f = Play(seed, fighting: true, 3600);
			fought += f.Hits;
			stood += Play(seed, fighting: false, 3600).Hits;
		}
		output.WriteLine($"fighting {fought} hits, standing {stood}");
		Assert.True(fought * 3 <= stood, $"fighting {fought} hits, standing {stood}");
	}

	[Fact]
	public void TheWholeFight_KillsTheBrain_WithAFifthOfTheHitsOfStandingAndShooting()
	{
		// Arrows landing 60 % of the time: the Creepers first (a dash ended by a hit), then the Brain chasing, some 3 minutes. The
		// same fight shot from where the bot stands: about a hit every immunity. Measured here: 22-32 hits fighting (most in the
		// second phase, the Brain at 8 px/tick against a run of 3), 176-187 standing. Each is a Creeper's 20 or the Brain's 30
		// less half the defence (iron armour, 9): some 600 life, which 200 life and the healing potions of the kit must cover.
		int fought = 0, stood = 0;
		for (int seed = 1; seed <= 3; seed++) {
			var (hits, _, ticks, sim) = Play(seed, fighting: true, 60 * 60 * 8);
			Assert.True(sim.Dead, $"seed {seed}: the brain at {sim.BrainLife} after {ticks} ticks");
			fought += hits;
			stood += Play(seed, fighting: true, 60 * 60 * 8, standing: true).Hits;
		}
		output.WriteLine($"fighting {fought} hits, standing and shooting {stood}");
		Assert.True(fought * 5 <= stood, $"fighting {fought} hits, standing and shooting {stood}");
		Assert.True(fought <= 3 * 35, $"{fought} hits in 3 fights");
	}
}

/// <summary>The Brain of Cthulhu's model (<see cref="BrainPattern"/>) against its AI's numbers.</summary>
public class BrainPatternTests
{
	private readonly ITestOutputHelper output;
	public BrainPatternTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void TheOrbitingCreepers_StayWithinTheSwarmsReach()
	{
		// Spawned round the centre within the Brain's size, -3..3 px/tick each axis (NPC.cs:33103-33113), then the orbit's AI.
		var rng = new Random(7);
		float worst = 0f, beyondOrbit = 0f;
		var still = new float[2000];
		for (int n = 0; n < 200; n++) {
			var c = new EntityInfo(1, BrainPattern.Creeper, "Creeper", rng.Next(-160, 160) - 15f, rng.Next(-110, 110) - 15f, 30, 30,
				rng.Next(-30, 31) * 0.1f, rng.Next(-30, 31) * 0.1f, 100, 100, 20, BrainPattern.CreeperAiStyle);
			var (px, py) = BrainPattern.OrbitPath(c, still, still);
			for (int t = 120; t < px.Length; t++) {
				float d = MathF.Sqrt((px[t] + 15f) * (px[t] + 15f) + (py[t] + 15f) * (py[t] + 15f));
				worst = MathF.Max(worst, d);
				if (d > BrainPattern.OrbitPx)
					beyondOrbit = MathF.Max(beyondOrbit, d);
			}
		}
		output.WriteLine($"farthest from the Brain's centre after 2 s: {worst:0} px");
		Assert.True(worst <= BrainPattern.SwarmPx, $"{worst:0} px");
		Assert.True(beyondOrbit > BrainPattern.OrbitPx + 20f, "they overshoot the 90 px");
	}

	[Fact]
	public void HidingTheBrainDrifts_AtOnePxATick()
	{
		var brain = BrainFightTests.Brain(50, 10);
		var (x, y) = BrainPattern.BrainPath(brain, brain.CenterX - 300f, brain.CenterY, 50);
		Assert.Equal(brain.CenterX - 50f, x[^1], 2);
		Assert.Equal(brain.CenterY, y[^1], 2);
		Threat th = BrainPattern.Predict(brain, new[] { brain }, false, 0f, 0f);
		Assert.Equal(BrainPattern.DriftSpeed, th.HomingSpeed);
		Assert.Equal(0f, th.HomingInertia);
	}

	[Fact]
	public void ExposedTheBrainChases_EasedTo8_AsTheDodgeModelHasIt()
	{
		// From rest: v = 8 (1 - (50/51)^t) towards the player, about 5.1 px/tick after 50 ticks (NPC.cs:33157-33169).
		var brain = BrainFightTests.Brain(50, 10, exposed: true);
		var (x, _) = BrainPattern.BrainPath(brain, brain.CenterX - 2000f, brain.CenterY, 51);
		float v = x[49] - x[50];
		Assert.InRange(v, 5.0f, 5.2f);
		// The dodge planner's threat for it is the same motion: a standing body 300 px to its left is hit when the path says.
		Threat th = BrainPattern.Predict(brain, new[] { brain }, false, 0f, 0f);
		Assert.Equal((BrainPattern.ChaseSpeed, BrainPattern.ChaseInertia), (th.HomingSpeed, th.HomingInertia));
		var p = PhysicsParams.Default;
		float floor = brain.CenterY + p.Height / 2f;
		var body = new BodyState { X = brain.X - 300f - p.Width, Y = floor - p.Height };
		DodgePlan stand = Dodge.Evaluate(body, p, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { th }, 0, 0, 120);
		var (bx, by) = BrainPattern.BrainPath(brain, body.CenterX(p), body.CenterY(p), 120);
		int hitAt = Enumerable.Range(0, 120).First(t => Dodge.Gap(body.X, body.Y, p.Width, p.Height, bx[t] - 80f, by[t] - 55f, 160, 110) < 0f) + 1;
		output.WriteLine($"the planner: hit in {stand.HitTick}; the path: {hitAt}");
		Assert.InRange(stand.HitTick, hitAt - 1, hitAt + 1);
	}

	[Fact]
	public void ADashingCreeper_ComesAtUs_OneGoingAwayOrOrbitingDoesNot()
	{
		var at = BrainFightTests.Creeper(1, 20, 10, -8f, 0f, dashing: true);
		Assert.Equal(20, BrainPattern.ComingAt(at, at.CenterX - 160f, at.CenterY));
		Assert.Equal(-1, BrainPattern.ComingAt(at with { VX = 8f }, at.CenterX - 160f, at.CenterY));
		Assert.Equal(-1, BrainPattern.ComingAt(at with { Ai0 = 0f }, at.CenterX - 160f, at.CenterY));
		// Its line passing 5 tiles above us: not at us.
		Assert.Equal(-1, BrainPattern.ComingAt(at, at.CenterX - 160f, at.CenterY + 80f));
	}

	[Fact]
	public void TheShotOrder_IsTheDashersAtUsFirst_ThenTheNearest()
	{
		float pcx = 0f, pcy = 0f;
		var near = BrainFightTests.Creeper(1, 3, 0);
		var far = BrainFightTests.Creeper(2, 20, 0);
		var dasher = BrainFightTests.Creeper(3, 25, 0, -8f, 0f, dashing: true);
		Assert.Equal(new[] { 3, 1, 2 }, BrainPattern.ShotOrder(new[] { far, near, dasher }, pcx, pcy).Select(c => c.Id));
	}

	[Fact]
	public void TheBrainHiding_CannotBeHurt_Exposed_ItCan()
	{
		Assert.False(BrainPattern.Hittable(BrainFightTests.Brain(0, 0)));
		Assert.True(BrainPattern.Hittable(BrainFightTests.Brain(0, 0, exposed: true)));
		Assert.True(BrainPattern.Hittable(BrainFightTests.Creeper(1, 0, 0)));
	}

	[Fact]
	public void AnOrbitingCreeper_IsPredictedRoundTheBrain_ADashingOneInALine()
	{
		var brain = BrainFightTests.Brain(50, 10);
		var orbiting = BrainFightTests.Creeper(2, 55, 10, 0f, 6f);
		Threat th = BrainPattern.Predict(orbiting, new[] { brain, orbiting }, false, 0f, 160f);
		Assert.NotNull(th.PathX);
		Assert.Equal(Dodge.Horizon, th.PathX.Length);
		// Its centre stays within the swarm's reach of where the Brain's centre will be (drifting 1 px/tick at the player).
		var (bx, by) = BrainPattern.BrainPath(brain, 0f, 160f);
		for (int t = 0; t < th.PathX.Length; t++) {
			float dx = th.PathX[t] + 15f - bx[t], dy = th.PathY[t] + 15f - by[t];
			Assert.True(MathF.Sqrt(dx * dx + dy * dy) <= BrainPattern.SwarmPx, $"tick {t + 1}");
		}
		Assert.Null(BrainPattern.Predict(orbiting with { Ai0 = 1f }, new[] { brain }, false, 0f, 0f).PathX);
		Assert.Null(BrainPattern.Predict(orbiting, Array.Empty<EntityInfo>(), false, 0f, 0f).PathX);
	}
}
