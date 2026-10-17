using System;
using TerraBot.Bot;
using TerraBot.Bot.Brain;
using TerraBot.Bot.Skills;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace TerraBot.Tests;

/// <summary>
/// The corruption chasm of measured run M10d (world_eow, log M10d_partial_0150.log lines 1380-1583), rebuilt from its plans:
/// a bridge at row 270 over x 1702-1708 (Build "chasm bridge at (1702,270) (7x1)", line 1124), a 2-block pillar on its west
/// end at x 1703 (rows 268-269, "Pillar (1703,269)->(1703,268) {place(1703,269)}"), west of it the chasm left open under the
/// overhang ("Walk (1701,269)->(1700,269) {place(1701,270)}"), a one-block ledge at (1699,276) ("Jump (1704,269)->(1699,275)")
/// and the shaft at x 1700-1701 down to row 361, where the bot died ("Hurt 685 (fall damage) at tile (1701,361)").
/// </summary>
public class ChasmFallTests
{
	private readonly ITestOutputHelper output;

	public ChasmFallTests(ITestOutputHelper output) => this.output = output;

	private sealed class Log : IBotLog
	{
		private readonly ITestOutputHelper o;
		public Log(ITestOutputHelper o) => this.o = o;
		public void Info(string m) => o.WriteLine("INFO  " + m);
		public void Warn(string m) => o.WriteLine("WARN  " + m);
		public void Error(string m, Exception ex = null) => o.WriteLine("ERROR " + m);
		public void Debug(string m) { }
	}

	/// <summary>World column and row of the grid's (0, 0).</summary>
	public const int X0 = 1690, Y0 = 250;

	/// <summary>The bot's feet on the pillar top, where the Devourer hit it ("at tile (1703,267)").</summary>
	public static readonly TilePos PillarTop = new(1703 - X0, 267 - Y0);

	/// <summary>
	/// The M10d chasm in grid coordinates (world minus <see cref="X0"/>, <see cref="Y0"/>), with 1502 dirt blocks to place. The
	/// chasm's ebonstone is unbreakable ('@'): the run's Iron Pickaxe (power 40) is below ebonstone's 65
	/// (BotContext.EvilStonePickPower), which is why its chase plans there were all "Partial". The bridge and the pillar are
	/// the bot's dirt.
	/// </summary>
	public static (ArrayTileGrid Grid, TestBlockActions Blocks) M10dChasm()
	{
		const int w = 30, h = 116;
		var rows = new char[h][];
		for (int y = 0; y < h; y++) {
			int wy = y + Y0;
			rows[y] = new char[w];
			for (int x = 0; x < w; x++) {
				int wx = x + X0;
				bool ebonstone =
					wy >= 362 // the chasm's floor
					|| wx <= 1698 && wy >= 262 // the west side, where the worm burrows
					|| wx == 1699 && wy >= 276 // the ledge 9 rows down, and the shaft's west wall under it
					|| wx >= 1707 && wy >= 293 // the chasm's east wall ("Drop (1706,275)->(1707,292)")
					|| wx >= 1709 && wy >= 270; // the ground east of the bridge
				bool dirt = wy == 270 && wx >= 1702 && wx <= 1708 // the bridge
					|| wx == 1703 && wy is 268 or 269; // the pillar on its west end
				rows[y][x] = ebonstone ? '@' : dirt ? '#' : '.';
			}
		}
		rows[PillarTop.Y][PillarTop.X] = 'S';
		var s = new string[h];
		for (int y = 0; y < h; y++)
			s[y] = new string(rows[y]);
		(ArrayTileGrid g, TestBlockActions b) = TestBlockActions.Parse(s);
		b.BlockCount = 1502;
		return (g, b);
	}

	/// <summary>A Devourer's head (NPCID 7: 22x22, worm AI 6, noTileCollide) centred in the tile.</summary>
	private static EntityInfo Devourer(int wx, int wy) =>
		new(3, 7, "Devourer", (wx - X0) * 16f + 8f - 11f, (wy - Y0) * 16f + 8f - 11f, 22, 22, 0, 0, 68, 100, 26, AiStyle: WormPattern.WormAiStyle, NoTileCollide: true);

	private BotContext Archer(ArrayTileGrid g, SimWorld sim, TestBlockActions blocks = null) => new() {
		Grid = g, Log = new Log(output), Body = sim.State, Blocks = blocks,
		Combat = new CombatInfo { WeaponReach = 30f, WeaponName = "Iron Broadsword", Facing = 1, HasRanged = true, RangedName = "Demon Bow", AmmoCount = 554,
			Projectile = new ProjectileModel(9f, 15, 0.1f, 16f), Life = 111, LifeMax = 200 },
	};

	/// <summary>
	/// Vanilla's fall damage bookkeeping (Player.Update, decompiled Player.cs:24331-24395): fallStart is the hitbox's top row
	/// while velocity.Y is 0 (or a jump is held); on landing, more than 25 rows below it hurts 10 a row.
	/// </summary>
	private sealed class FallMeter
	{
		public int FallStart, Worst;
		public FallMeter(in BodyState s) => FallStart = (int)(s.Y / 16f);
		public void After(in BodyState s, bool landedBefore)
		{
			int row = (int)(s.Y / 16f);
			if (s.VY == 0f) {
				if (!landedBefore)
					Worst = Math.Max(Worst, row - FallStart);
				FallStart = row;
			}
			else if (s.Jump > 0)
				FallStart = row;
		}
	}

	[Fact]
	public void AWorm_IsMet_NotChasedIntoTheChasm_AndItsKnockbackFindsTheBotOffTheLip()
	{
		// Measured run M10d (lines 1380-1485): chasing a Devourer through the chasm's walls, the fight's walks jumped into the
		// chasm ("Jump (1704,269)->(1699,275)", bridging at row 276 and pillaring), and on the pillar top a hit from the east
		// ("Hurt 26 (NPC Devourer), knockback 4.5 dir -1 at tile (1703,267)") threw the bot 3 columns west over the open shaft.
		(ArrayTileGrid g, TestBlockActions blocks) = M10dChasm();
		var sim = new SimWorld(g, PhysicsParams.Default, PillarTop, blocks);
		BotContext ctx = Archer(g, sim, blocks);
		EntityInfo worm = Devourer(1694, 270); // in the west side's rock: out of sight, as in the log ("at (1691,269)")
		var skill = new FightSkill();
		ctx.Hostiles = new[] { worm };
		skill.Start(ctx);
		int lowest = sim.Node.Y;
		for (int t = 0; t < 240; t++) {
			ctx.Body = sim.State;
			ctx.Hostiles = new[] { worm };
			ctx.Tick = t;
			skill.Tick(ctx);
			if (ctx.Output.Horizontal != 0 && ctx.Combat.ItemAnimation == 0)
				ctx.Combat.Facing = ctx.Output.Horizontal; // vanilla HorizontalMovement turns the player with the keys
			if (t == 120) {
				// The worm comes up through the bridge on the east side and bites: vanilla Player.Hurt, 4.5 * dir and -3.5.
				sim.State.VX = -4.5f;
				sim.State.VY = -3.5f;
				ctx.NoteAttacker(worm.Id);
			}
			sim.Step(ctx.Output);
			lowest = Math.Max(lowest, sim.Node.Y);
		}
		output.WriteLine($"{skill.Describe()}; at world {sim.Node.X + X0},{sim.Node.Y + Y0}, feet down to row {lowest + Y0}");
		Assert.True(lowest + Y0 <= 269, $"went down into the chasm, to row {lowest + Y0}");
	}

	[Fact]
	public void ThrownOverTheOpenShaft_MidFight_TheFallIsArrested()
	{
		// Measured run M10d (lines 1485-1579): thrown off the pillar top, the bot was over the open shaft at (1700,267) ("WalkTo
		// start ... bot at (1700,267)"), then (1700,284), (1700,303), (1700,322) while the fight retargeted the worm, and died
		// at (1701,361) of a 94-row fall, 685 damage. The ledge at (1699,276) was 6.4 px west of the body all the way down
		// (death at px 27206.4: the hitbox's left edge 6.4 px east of column 1699).
		(ArrayTileGrid g, TestBlockActions blocks) = M10dChasm();
		var sim = new SimWorld(g, PhysicsParams.Default, PillarTop, blocks);
		var meter = new FallMeter(sim.State); // standing on the pillar top: vanilla's fallStart
		// Over columns 1700-1701, its right edge 8 px short of the bridge's column, coming down.
		sim.State.X = (1700 - X0) * 16f + 4f;
		sim.State.Y = (268 - Y0) * 16f - 42f;
		sim.State.VX = -0.3f;
		sim.State.VY = 2f;
		BotContext ctx = Archer(g, sim, blocks);
		EntityInfo worm = Devourer(1691, 269);
		var brain = new BotBrain();
		ctx.Hostiles = new[] { worm };
		ctx.NoteAttacker(worm.Id);
		brain.StartSkill(new FightSkill(), ctx);
		int lowest = sim.Node.Y;
		for (int t = 0; t < 300; t++) {
			ctx.Body = sim.State;
			ctx.Hostiles = new[] { worm };
			ctx.Tick = t;
			brain.Tick(ctx);
			bool wasDown = sim.State.VY == 0f;
			sim.Step(ctx.Output);
			meter.After(sim.State, wasDown);
			lowest = Math.Max(lowest, sim.Node.Y);
		}
		output.WriteLine($"{brain.Describe()}; at world {sim.Node.X + X0},{sim.Node.Y + Y0}, feet down to row {lowest + Y0}, worst fall {meter.Worst} rows, {sim.Placed} placed");
		Assert.True(meter.Worst <= PhysicsParams.Default.SafeFallTiles, $"fell {meter.Worst} rows, to row {lowest + Y0}");
	}
}
