using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 12 (docs M12_SPEC.md sections 4-6): fighting the Wall of Flesh (<see cref="WallOfFleshPattern"/>) with
/// <see cref="BossFightSkill"/> on a flat bridge. The field as in <see cref="SkeletronFightTests"/>: air down to row 24 (the feet
/// row), the bridge's blocks from row 25; the bot at column 150, the Wall coming from the east, travelling west.
/// </summary>
public class WallOfFleshFightTests
{
	private readonly ITestOutputHelper output;
	public WallOfFleshFightTests(ITestOutputHelper output) => this.output = output;

	private static readonly PhysicsParams P = PhysicsParams.Default;
	private const int MouthId = 1, UpperEyeId = 2, LowerEyeId = 3;
	private static readonly (int, int, int) Bridge = (5, 295, 24);
	private const float BodyX = 150 * 16f;
	private static float PlayerTop => 25 * 16f - P.Height;
	private static float PlayerCX => BodyX + P.Width / 2f;
	private static float PlayerCY => PlayerTop + P.Height / 2f;

	/// <summary>The Wall's parts with the mouth's centre <paramref name="aheadTiles"/> tiles east of our centre, travelling west.</summary>
	private static EntityInfo[] Wall(float aheadTiles, int life = 8000, float speed = -1.5f)
	{
		float x = PlayerCX + aheadTiles * 16f - 50f, y = PlayerCY - 50f;
		return new[] {
			new EntityInfo(MouthId, WallOfFleshPattern.Mouth, "Wall of Flesh", x, y, 100, 100, speed, 0f, life, 8000, 50, WallOfFleshPattern.MouthAiStyle),
			new EntityInfo(UpperEyeId, WallOfFleshPattern.Eye, "Wall of Flesh", x, y - 270f, 100, 100, speed, 0f, life, 8000, 50, WallOfFleshPattern.EyeAiStyle, 1f),
			new EntityInfo(LowerEyeId, WallOfFleshPattern.Eye, "Wall of Flesh", x, y + 270f, 100, 100, speed, 0f, life, 8000, 50, WallOfFleshPattern.EyeAiStyle, -1f),
		};
	}

	private (FakeGame g, BotContext ctx, BossFightSkill fight) Setup(params EntityInfo[] hostiles)
	{
		FakeGame g = EarlyGameTests.Field(300);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		ctx.Combat.Projectile = new ProjectileModel(10f, 15, 0.1f, 16f);
		ctx.Body = new BodyState { X = BodyX, Y = PlayerTop, ReleaseJump = true };
		ctx.Hostiles = hostiles;
		var fight = new BossFightSkill(FakeGame.GuideVoodooDoll, FakeGame.WofNpc, FakeGame.Ids.WallOfFleshMinions) { Arena = Bridge };
		fight.Start(ctx);
		return (g, ctx, fight);
	}

	/// <summary>world_wof E31 (2026-10-18): stood on a knoll six rows over the deck, its rising side read as the bridge's end; the Wall ran it over.</summary>
	[Fact]
	public void StoodAboveTheDeck_TheFightWalksBackDownOntoIt()
	{
		var (g, ctx, fight) = Setup(Wall(50f));
		for (int x = 148; x <= 153; x++)
			for (int y = 20; y <= 24; y++)
				g.Grid.Set(x, y, Core.Grid.CellKind.Solid); // a knoll on the deck
		g.Sim.State = new BodyState { X = Core.Pathfinding.Body.CanonicalLeft(150, P), Y = Core.Pathfinding.Body.StandingTop(19, P), ReleaseJump = true };
		g.Refresh(ctx);
		ctx.Hostiles = Wall(50f);
		fight.Tick(ctx);
		Assert.Contains(((TestLog)ctx.Log).Lines, l => l.Contains("off the arena"));
	}

	[Fact]
	public void ThePattern_HasTheDecompilesNumbers()
	{
		Assert.True(new BossFightSkill(FakeGame.GuideVoodooDoll, FakeGame.WofNpc).IsWallOfFlesh);
		Assert.False(new BossFightSkill(0, SkeletronPattern.Head).IsWallOfFlesh);
		// NPC.cs:26337-26383: 1.5 / 1.75 / 2.15 / 2.65 / 3.25 px/tick in Classic.
		Assert.Equal(new[] { 1.5f, 1.75f, 2.15f, 2.65f, 3.25f }, new[] { 1f, 0.7f, 0.4f, 0.2f, 0.05f }.Select(f => WallOfFleshPattern.Speed(f, false)).Select(v => System.MathF.Round(v, 2)));
		// The band's near edge: the Hungry's reach (300 / 500 / 700 px, x1.3) + 5 tiles: 30 / 46 / 62 (spec section 6).
		Assert.Equal(new[] { 30, 46, 62 }, new[] { 1f, 0.6f, 0.3f }.Select(f => WallOfFleshPattern.BandMinTiles(f, false)));
		// NPC.SpawnWOF: a doll at or left of the middle brings it from the west, right of it from the east.
		Assert.Equal(-1, WallOfFleshPattern.SpawnSide(2100, 4200));
		Assert.Equal(1, WallOfFleshPattern.SpawnSide(2101, 4200));
		Assert.True(WallOfFleshPattern.Behind(Wall(-3f)[0], -1, PlayerCX));
		Assert.False(WallOfFleshPattern.Behind(Wall(1f)[0], -1, PlayerCX));
	}

	[Fact]
	public void FarAhead_TheBotStands_AndShootsTheUpperEye_TheBridgeHidesTheLowerOne()
	{
		var (_, ctx, fight) = Setup(Wall(50f));
		fight.Tick(ctx);
		output.WriteLine($"{fight.Describe()}: target {fight.TargetId}, state {fight.State}, out {ctx.Output}");
		Assert.Equal(0, ctx.Output.Horizontal);
		Assert.Equal(UpperEyeId, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
		Assert.Equal(ToolRequest.Ranged, ctx.Output.Tool);
	}

	[Fact]
	public void BelowHalfLife_TheBand62TilesOff_TheUpperEyeIsStillShot()
	{
		// Spec section 6: below 50 % the Hungry reach 57 tiles, the band from 62: an arrow's flight there is over the 120 ticks
		// other fights aim within.
		var (_, ctx, fight) = Setup(Wall(66f, life: 3000));
		fight.Tick(ctx);
		output.WriteLine($"{fight.State}: target {fight.TargetId}, out {ctx.Output}");
		Assert.Equal(0, ctx.Output.Horizontal);
		Assert.Equal(UpperEyeId, fight.TargetId);
		Assert.True(ctx.Output.UseItem);
	}

	[Fact]
	public void AtTheBandsNearEdge_TheBotRunsOnAwayFromTheWall_AndKeepsRunningThroughTheBand()
	{
		var (_, ctx, fight) = Setup(Wall(31f));
		fight.Tick(ctx);
		output.WriteLine($"{fight.State}: out {ctx.Output}");
		Assert.Equal(-1, ctx.Output.Horizontal); // the Wall travels west: run west
		ctx.Tick = 1;
		ctx.Hostiles = Wall(36f);
		fight.Tick(ctx);
		Assert.Equal(-1, ctx.Output.Horizontal); // still in the near part of the band: on
		ctx.Tick = 2;
		ctx.Hostiles = Wall(44f);
		fight.Tick(ctx);
		Assert.Equal(0, ctx.Output.Horizontal); // the far part: stand and let it come

		// Below 50 % the Hungry reach 57 tiles: the same 44 tiles is inside the band's near edge, run.
		var (_, ctx2, fight2) = Setup(Wall(44f, life: 3000));
		fight2.Tick(ctx2);
		Assert.Equal(-1, ctx2.Output.Horizontal);
	}

	[Fact]
	public void TheWallTravellingEast_TheBotRunsEast()
	{
		var wall = Wall(-31f, speed: 1.5f); // west of us, coming east
		var (_, ctx, fight) = Setup(wall);
		fight.Tick(ctx);
		Assert.Equal(1, ctx.Output.Horizontal);
	}

	[Fact]
	public void AHungryNear_IsShotBeforeTheEyes()
	{
		var hungry = new EntityInfo(9, WallOfFleshPattern.Hungry, "The Hungry", PlayerCX + 6 * 16f, PlayerCY - 40f, 30, 30, -2f, 0f, 240, 240, 30, WallOfFleshPattern.HungryAiStyle);
		var (_, ctx, fight) = Setup(Wall(50f).Append(hungry).ToArray());
		fight.Tick(ctx);
		output.WriteLine($"{fight.State}: target {fight.TargetId}");
		Assert.Equal(9, fight.TargetId);
		Assert.Contains("Hungry", fight.State);
	}

	/// <summary>
	/// Self-test fight_wof_150 on Classic (twice): from the Wall's 465 life on, "shooting the Leech by us", its life stuck at 263 and
	/// 242, and the bot killed by the last bursts. Nearly down, the Wall is shot before the servants by us.
	/// </summary>
	[Fact]
	public void TheWallNearlyDown_IsShotBeforeAHungryNear()
	{
		var hungry = new EntityInfo(9, WallOfFleshPattern.Hungry, "The Hungry", PlayerCX + 6 * 16f, PlayerCY - 40f, 30, 30, -2f, 0f, 240, 240, 30, WallOfFleshPattern.HungryAiStyle);
		var (_, ctx, fight) = Setup(Wall(50f, life: 300).Append(hungry).ToArray());
		fight.Tick(ctx);
		output.WriteLine($"{fight.State}: target {fight.TargetId}");
		Assert.NotEqual(9, fight.TargetId);
		Assert.DoesNotContain("Hungry", fight.State);
	}

	[Fact]
	public void ALaserComingAtUs_IsDodged_NeverTowardTheWall()
	{
		// A laser from the upper eye 50 tiles east, aimed at our centre, 27 px/tick (9 with extraUpdates 2).
		EntityInfo[] wall = Wall(40f);
		EntityInfo eye = wall[1];
		float sx = eye.CenterX - 20f * 16f, sy = eye.CenterY + 100f;
		float dx = PlayerCX - sx, dy = PlayerCY - sy, d = System.MathF.Sqrt(dx * dx + dy * dy);
		var laser = new ShotInfo(77, WallOfFleshPattern.Laser, sx, sy, 4, 4, dx / d * 27f, dy / d * 27f, 22);
		var (g, ctx, fight) = Setup(wall);
		ctx.Shots = new[] { laser };
		fight.Tick(ctx);
		output.WriteLine($"{fight.State}: out {ctx.Output}, dodges {fight.Dodges}, lasers {fight.LasersSeen}");
		Assert.Equal(1, fight.LasersSeen);
		Assert.Equal(1, fight.Dodges);
		Assert.NotEqual(1, ctx.Output.Horizontal); // never east, toward the Wall: west or a jump in place
		Assert.True(ctx.Output.Horizontal != 0 || ctx.Output.Jump);
		// The same laser under the bridge (the lower eye's, from below): the blocks stop it, no dodge.
		var (_, ctx2, fight2) = Setup(wall);
		ctx2.Shots = new[] { laser with { Y = PlayerCY + 200f, VY = -System.Math.Abs(laser.VY) } };
		fight2.Tick(ctx2);
		Assert.Equal(0, fight2.LasersSeen);
		Assert.Equal(0, fight2.Dodges);
	}

	[Fact]
	public void ALaserIsTheStretchItSweepsInATick_NotADotThatSkipsOverTheBody()
	{
		// 27 px a tick across a 20 px body: at no tick's end inside it, but the game's three moves a tick hit it (extraUpdates 2).
		var body = new BodyState { X = 100f, Y = 100f };
		float floor = 100f + P.Height;
		var laser = new ShotInfo(1, WallOfFleshPattern.Laser, 14f, 120f, 4, 4, 27f, 0f, 22);
		var dot = new Threat(laser.X, laser.Y, 4, 4, 27f, 0f);
		Assert.False(Dodge.Evaluate(body, P, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { dot }, 0, 0).Hits);
		Assert.True(Dodge.Evaluate(body, P, floor, float.NegativeInfinity, float.PositiveInfinity, new[] { WallOfFleshPattern.ShotThreat(laser) }, 0, 0).Hits);
	}

	[Fact]
	public void TheWallFight_HealsBelow70Percent_AndWalksOnOverAHeartAhead()
	{
		var (g, ctx, fight) = Setup(Wall(50f));
		ctx.Combat.Life = 130;
		ctx.Combat.LifeMax = 200;
		ctx.Combat.CanQuickHeal = true;
		fight.Tick(ctx);
		Assert.True(ctx.Output.QuickHeal);
		// Standing in the band's far part: a Heart 5 tiles ahead (west, the way it runs) is walked to; one behind is not.
		ctx.Tick = 1;
		ctx.Combat.CanQuickHeal = false;
		ctx.Drops = new[] { new DropInfo(7, 58, "Heart", 1, PlayerCX - 5 * 16f, PlayerCY, 12, 12) };
		fight.Tick(ctx);
		Assert.Equal(-1, ctx.Output.Horizontal);
		ctx.Tick = 2;
		ctx.Drops = new[] { new DropInfo(7, 58, "Heart", 1, PlayerCX + 5 * 16f, PlayerCY, 12, 12) };
		fight.Tick(ctx);
		Assert.Equal(0, ctx.Output.Horizontal);
	}

	/// <summary>world_wof E26 (2026-10-17): healed at 200 of 240 (a Lesser potion's 40, then a minute's sickness), 22 Healing Potions unused.</summary>
	[Fact]
	public void TheWallFight_HealsWhenTheBestPotionsLifeIsMissing_NotAtEightyFivePercent()
	{
		var (g, ctx, fight) = Setup(Wall(50f));
		ctx.Combat.LifeMax = 240;
		ctx.Combat.BestHealLife = 100;
		ctx.Combat.CanQuickHeal = true;
		ctx.Combat.Life = 200; // 40 missing: a potion of 100 would waste most of it
		fight.Tick(ctx);
		Assert.False(ctx.Output.QuickHeal);
		ctx.Tick = 2;
		ctx.Combat.Life = 140; // 100 missing
		fight.Tick(ctx);
		Assert.True(ctx.Output.QuickHeal);
	}

	[Fact]
	public void NearTheUnderworldsTopRow_NoJump_TheTongueWouldTakeUs()
	{
		var hungry = new EntityInfo(9, WallOfFleshPattern.Hungry, "The Hungry", PlayerCX + 40f, PlayerCY + 10f, 30, 30, -4f, 0f, 240, 240, 30, WallOfFleshPattern.HungryAiStyle);
		var (_, ctx, fight) = Setup(Wall(50f).Append(hungry).ToArray());
		ctx.UnderworldRow = 22; // the body's top (row 22.4) just under it: any jump lifts it above
		for (int t = 0; t < 5; t++) {
			ctx.Tick = t;
			fight.Tick(ctx);
			Assert.False(ctx.Output.Jump);
		}
	}

	[Fact]
	public void HardmodeOnBeforeTheFight_IsNoWin_TheWallGoneIs()
	{
		// Seen in game (m12_synthetic_3): fight_wof_unholy "won" in 3 ticks, Hardmode left on by fight_wof.
		var (g, ctx, fight) = Setup(Wall(50f));
		g.BossesDefeated.Add(FakeGame.WofNpc);
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		ctx.Hostiles = System.Array.Empty<EntityInfo>();
		SkillStatus s = SkillStatus.Running;
		int t = 0;
		for (; t < 200 && s == SkillStatus.Running; t++) {
			ctx.Tick = t + 1;
			s = fight.Tick(ctx);
		}
		output.WriteLine($"{s} after {t} ticks");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.True(t > BossFightSkill.GoneTicks);
	}

	[Fact]
	public void TheWallDown_IsHardmode_AndTheFightIsWon()
	{
		var (g, ctx, fight) = Setup(Wall(50f));
		Assert.Equal(SkillStatus.Running, fight.Tick(ctx));
		g.BossesDefeated.Add(FakeGame.WofNpc); // Main.hardMode (TerrariaGameInfo.BossDefeated)
		ctx.Hostiles = System.Array.Empty<EntityInfo>();
		Assert.Equal(SkillStatus.Succeeded, fight.Tick(ctx));
		Assert.NotNull(fight.WallLastSeen);
	}
}
