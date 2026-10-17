using TerraBot.Bot;
using TerraBot.Bot.Skills;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>Which arrows go first (<see cref="AmmoOrder"/>), and the boss fight asking for the best (MoveInput.BestAmmo).</summary>
public class AmmoOrderTests
{
	private readonly ITestOutputHelper output;
	public AmmoOrderTests(ITestOutputHelper output) => this.output = output;

	[Fact]
	public void ForABoss_UnholyArrows_BeatFlamingAndWooden_AndCheapArrowsGoFirstOtherwise()
	{
		// Measured run M10o: 60 unholy arrows (damage 12, pierce 5) sat in the last ammo slot behind 507 wooden ones (damage 5),
		// never fired in two lost Eater of Worlds fights.
		float wooden = AmmoOrder.BossValue(5, 1), flaming = AmmoOrder.BossValue(7, 1), unholy = AmmoOrder.BossValue(12, 5);
		Assert.True(unholy > flaming && flaming > wooden);
		Assert.True(AmmoOrder.BossValue(10, -1) > AmmoOrder.BossValue(10, 1)); // no pierce limit
		Assert.True(AmmoOrder.EverydayValue(5) > AmmoOrder.EverydayValue(40)); // wooden (value 5) before unholy (40)
	}

	[Fact]
	public void TheBossFight_AsksForTheBestArrows_EveryTick()
	{
		FakeGame g = EarlyGameTests.Field(200);
		BotContext ctx = g.CreateContext(output);
		ctx.Combat.HasRanged = true;
		var fight = new BossFightSkill(FakeGame.SuspiciousEye, FakeGame.EyeNpc, FakeGame.ServantNpc);
		fight.Start(ctx);
		for (int i = 0; i < 5; i++) {
			fight.Tick(ctx);
			Assert.True(ctx.Output.BestAmmo, $"tick {i}: {fight.State}");
		}
	}
}
