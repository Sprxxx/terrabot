using System.Linq;
using TerraBot.Bot;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// Milestone 12 (docs M12_SPEC.md section 2c): the Throw key, throwing exactly one Guide Voodoo Doll off a ledge into the lava
/// below, and what the game does with it (FakeGame.ThrowKey: Item.CheckLavaDeath and NPC.SpawnWOF).
/// </summary>
public class ThrowTests
{
	private readonly ITestOutputHelper output;
	public ThrowTests(ITestOutputHelper output) => this.output = output;

	/// <summary>The ledge's last column, its top row (the bot's feet on row 19), and the lava's top row under the open side.</summary>
	public const int LedgeEnd = 30, LedgeRow = 20, LavaTop = 40;

	/// <summary>A stone ledge x 0-30 on row 20 (the bot at its east end), open air east of it down to a lava pool on rows 40-44.</summary>
	public static FakeGame Ledge(int width = 120, bool lava = true)
	{
		var rows = new string[46];
		for (int y = 0; y < rows.Length; y++) {
			char[] r = new string('.', width).ToCharArray();
			for (int x = 0; x < width; x++) {
				if (y == LedgeRow && x <= LedgeEnd || y == rows.Length - 1)
					r[x] = '#';
				else if (y >= LavaTop && y < rows.Length - 1)
					r[x] = lava ? '!' : '#';
			}
			rows[y] = new string(r);
		}
		char[] feet = rows[LedgeRow - 1].ToCharArray();
		feet[LedgeEnd - 2] = 'S';
		rows[LedgeRow - 1] = new string(feet);
		return new FakeGame(rows);
	}

	private (FakeGame, BotContext) Setup(int dolls = 1, bool guide = true, bool lava = true)
	{
		FakeGame g = Ledge(lava: lava);
		BotContext ctx = g.CreateContext(output);
		g.Items[FakeGame.GuideVoodooDoll] = dolls;
		if (guide)
			g.AliveTownNpcs.Add(FakeGame.GuideNpc);
		g.Refresh(ctx);
		return (g, ctx);
	}

	private static ThrowItemSkill DollThrow(int facing) => new(FakeGame.GuideVoodooDoll, facing, "Guide Voodoo Doll") {
		Refuse = c => c.World.TownNpcAlive(c.Ids.Guide) ? null : "no Guide alive: the doll would be wasted (doll wasted, no Guide)",
		Done = c => c.Hostiles.Any(h => h.Type == c.Ids.WallOfFlesh),
	};

	[Fact]
	public void OneDollThrownOffTheLedge_FallsIntoTheLava_TheGuideDies_AndTheWallComesFromTheNearerEdge()
	{
		var (g, ctx) = Setup();
		g.WorldWidthTiles = 40; // the ledge's end (x 30) is in the world's east half: the Wall comes from the east
		SkillStatus s = EarlyGameTests.RunSkill(g, ctx, DollThrow(1), 400);
		output.WriteLine($"{s}, throws {g.Throws}, burnt {g.BurntItems}, wall {g.WofSpawn}");
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(1, g.Throws);
		Assert.Equal(0, g.Count(FakeGame.GuideVoodooDoll));
		Assert.False(g.TownNpcAlive(FakeGame.GuideNpc));
		var (x, _, dir) = Assert.IsType<(float, float, int)>(g.WofSpawn);
		Assert.True(x > g.Sim.State.X + 1199f, "east of the bot, 1200 px off");
		Assert.Equal(-1, dir); // travelling west, toward the bot

		// The same throw in a world whose middle is east of the ledge: from the west.
		var (g2, ctx2) = Setup();
		g2.WorldWidthTiles = 400;
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g2, ctx2, DollThrow(1), 400));
		var (x2, _, dir2) = Assert.IsType<(float, float, int)>(g2.WofSpawn);
		Assert.True(x2 < g2.Sim.State.X - 1199f, "west of the bot");
		Assert.Equal(1, dir2);
	}

	[Fact]
	public void AStackOfTwo_ThrowsOne_AndOneIsLeft()
	{
		// Spec 2b: a stack of N in lava kills N - 1 more town NPCs (Item.cs:50426-50460): one is thrown, never the stack.
		var (g, ctx) = Setup(dolls: 2);
		Assert.Equal(SkillStatus.Succeeded, EarlyGameTests.RunSkill(g, ctx, DollThrow(1), 400));
		Assert.Equal(1, g.Throws);
		Assert.Equal(1, g.Count(FakeGame.GuideVoodooDoll));
		Assert.Equal(1, g.WofSpawns);
	}

	[Fact]
	public void WithNoGuideAlive_TheDollIsNotThrown()
	{
		var (g, ctx) = Setup(guide: false);
		var t = DollThrow(1);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, t, 100));
		Assert.Contains("doll wasted, no Guide", t.FailureReason);
		Assert.Equal(0, g.Throws);
		Assert.Equal(1, g.Count(FakeGame.GuideVoodooDoll));
	}

	[Fact]
	public void FacingTheWrongWay_TheBotTurnsFirst_AndADollThatLandsOnGround_IsSaidToLieThere()
	{
		// Facing west on the ledge: thrown west it lands on the ledge (items rest on blocks, Item.cs:50828), not in lava.
		var (g, ctx) = Setup();
		var t = DollThrow(-1);
		Assert.Equal(SkillStatus.Failed, EarlyGameTests.RunSkill(g, ctx, t, 400));
		output.WriteLine(t.FailureReason);
		Assert.Contains("did not reach lava", t.FailureReason);
		Assert.Equal(1, g.Throws);
		Assert.Contains(g.Drops, d => d.Type == FakeGame.GuideVoodooDoll);
		Assert.Equal(0, g.WofSpawns);
		Assert.True(g.TownNpcAlive(FakeGame.GuideNpc));
	}

	[Fact]
	public void TheThrowKey_IsHeldOneTickOnly()
	{
		var (g, ctx) = Setup(dolls: 3);
		var t = new ThrowItemSkill(FakeGame.GuideVoodooDoll, 1, "Guide Voodoo Doll");
		t.Start(ctx);
		int pressed = 0;
		SkillStatus s = SkillStatus.Running;
		for (int i = 0; i < 100 && s == SkillStatus.Running; i++) {
			g.Refresh(ctx);
			s = t.Tick(ctx);
			if (ctx.Output.Throw)
				pressed++;
			g.Step(ctx);
		}
		Assert.Equal(SkillStatus.Succeeded, s);
		Assert.Equal(1, pressed);
		Assert.Equal(2, g.Count(FakeGame.GuideVoodooDoll));
	}
}
