using System;
using TerraBot.Core.Grid;
using TerraBot.Core.Pathfinding;
using TerraBot.Core.Physics;

namespace TerraBot.Bot.Skills;

/// <summary>
/// The hellbridge (docs M12_SPEC.md section 5d, S4, G5): a walking builder that lays one row of blocks (<see cref="Row"/>) from
/// <see cref="FromX"/> to <see cref="ToX"/> over the Underworld's open band, the arena the Wall of Flesh is fought along. Not a site
/// plan: the builder's sites rule out liquid and its chasm bridges stop at 40 tiles, and this one is 1000-1750 long over lava.
/// Each step, standing at the bridge's end:
/// <list type="number">
/// <item>mine what is solid in the next column's <see cref="ClearRows"/> rows over the bridge (the body's three and one more for a
/// jump's head): the ash and the ruined houses' bricks, none of them pick-gated at this depth (spec 5b), top down; never a cell that
/// lets lava in (BlockRules: lava beside or above it), and lava streaming through the body's rows is waited out
/// (<see cref="LavaWaitTicks"/>) as it drains through the gap the next block fills;</item>
/// <item>if the next column's bridge cell is not solid, place a block there: it touches the bridge's last block, which holds it (the
/// game refuses blocks only into lava, TerrariaBlockActions.CanPlaceAt, not over far lava);</item>
/// <item>walk one tile onto it (<see cref="Arrive"/> steering: from rest, never past the end).</item>
/// </list>
/// The state is read from the tiles each tick (the end is the last solid cell from the start), so a fight's pause, a knock off it and
/// the walk back, or a death and a return all go on where the bridge ends.
/// </summary>
public sealed class HellbridgeSkill : ISkill
{
	/// <summary>Rows over the bridge kept open: the body's 3 and a 4th for the head on a jump (spec 5b: "body clearance 4 rows").</summary>
	public const int ClearRows = 4;
	/// <summary>Ticks an action (a mine, a place) gets before it counts as failed; three failures on a cell fail the skill.</summary>
	public const int ActionTicks = 90;
	/// <summary>
	/// Ticks a tree cell gets: an axe does axe-power / 5 × 1.2 a swing against a tree tile's 100 (Player.cs, the tileAxe branch of
	/// ItemCheck_UseMiningTools_ActuallyUseMiningTool: a Copper Axe 8, thirteen swings of <see cref="AxeSwingTicks"/>), far more than
	/// <see cref="ActionTicks"/>. Seen in game (world_wof E11 and E16, 2026-10-17): "could not mine (3967,1037) (3 tries)", tile 634,
	/// the axe on the hotbar and swinging, nine swings in the 270 ticks of three tries, the tree at 72 of 100.
	/// </summary>
	public static int TreeTicks(BotContext ctx)
	{
		int aSwing = Math.Max(1, (int)(ctx.AxePower / 5 * 1.2f));
		return (100 + aSwing - 1) / aSwing * AxeSwingTicks + 60;
	}
	/// <summary>Ticks between an axe's swings (vanilla useTime of the early axes: 30).</summary>
	public const int AxeSwingTicks = 30;
	public const int MaxFailuresPerCell = 3;
	/// <summary>Ticks lava in the body's rows ahead is waited for to drain (a pocket runs down through the open bridge cell in a few seconds).</summary>
	public const int LavaWaitTicks = 300;
	/// <summary>Ticks to wait for the hotbar to be stocked with blocks from the bag (the adapter does it every 30).</summary>
	public const int MaxWaitForBlocks = 90;
	/// <summary>The progress is logged at these shares of the length (the spec's milestones: hellbridge 25 / 50 / 75 %).</summary>
	public static readonly int[] ProgressPercents = { 25, 50, 75 };

	public int Row { get; }
	public int FromX { get; }
	public int ToX { get; }
	public int Dir => ToX >= FromX ? 1 : -1;
	public string State { get; private set; } = "new";
	public string FailureReason { get; private set; } = "";
	public string Name => "Hellbridge";
	public string Key => $"Hellbridge {Row} {FromX} {ToX}";
	/// <summary>The bridge's last solid column from the start, as last read.</summary>
	public int End { get; private set; }
	/// <summary>The cell the bridge stopped at because nothing it has mines it (hellstone, hellstone brick), once it has.</summary>
	public TilePos? Blocker { get; private set; }
	/// <summary>A chest in the deck's cell the bridge stopped at: the game keeps it until it is looted empty (the errand loots it, then the bridge goes on).</summary>
	public TilePos? Chest { get; private set; }
	/// <summary>Blocks placed and cells mined by this instance.</summary>
	public int Placed { get; private set; }
	public int Mined { get; private set; }

	private WalkToSkill walk;
	private TilePos? actionCell;
	private bool actionPlace;
	private int actionTicks, lavaTicks, waitForBlocks, loggedPercent, lastEnd = int.MinValue;
	private readonly System.Collections.Generic.Dictionary<TilePos, int> failures = new();

	public HellbridgeSkill(int row, int fromX, int toX)
	{
		Row = row;
		FromX = fromX;
		ToX = toX;
	}

	public double Progress => Math.Clamp((End - FromX) / (double)(ToX - FromX), 0.0, 1.0);

	public string Describe() => $"Hellbridge row {Row} {FromX}->{ToX} at {End} ({Progress:P0}) [{State}]" + (walk != null ? " > " + walk.Describe() : "");

	public void Start(BotContext ctx)
	{
		End = BridgeEnd(ctx.Grid, Row, FromX, ToX, ctx);
		ctx.Log.Info($"Hellbridge start: row {Row} from x {FromX} to {ToX} ({Math.Abs(ToX - FromX)} tiles), built to {End}, "
			+ $"{(ctx.Inventory != null ? ctx.Inventory.CountAny(ctx.Ids.BuildingBlocks) + ctx.Inventory.Count(ctx.Ids.AshBlock) : 0)} blocks carried");
	}

	public void Stop(BotContext ctx)
	{
		walk?.Stop(ctx);
		walk = null;
		actionCell = null;
	}

	public void Pause(BotContext ctx) => Stop(ctx);
	public void Resume(BotContext ctx) { }

	/// <summary>
	/// The last column of the bridge going from <paramref name="fromX"/> toward <paramref name="toX"/> (the start itself if the next
	/// is not): its cell in the row solid and the body's rows over it open (<see cref="Built"/>).
	/// </summary>
	public static int BridgeEnd(ITileGrid g, int row, int fromX, int toX, BotContext ctx = null)
	{
		int dir = toX >= fromX ? 1 : -1, x = fromX;
		while (x != toX && (Built(g, row, x + dir) || Passable(ctx, g, row, x + dir)))
			x += dir;
		return x;
	}

	/// <summary>
	/// A column the bridge passes over rather than builds: a locked chest in the deck's cell (a ruined house's Shadow Chest wants a
	/// Shadow Key, and the game keeps a chest with items in it), standing on a floor one row down, the rows over it open. A chest is
	/// not solid: the body drops the one row through it and steps up onto the deck beyond. Seen in game (world_wof E10, 2026-10-17):
	/// "could not mine (3198,1042) (3 tries)", tile 21 at 1050 tiles out, and the errand blocked on it. An unlocked one is looted,
	/// then mined (<see cref="Chest"/>).
	/// </summary>
	public static bool Passable(BotContext ctx, ITileGrid g, int row, int x)
	{
		if (ctx?.World == null || !IsContainer(ctx, new TilePos(x, row)) || !g.Get(x, row + 1).CanStandOn())
			return false;
		for (int y = row - ClearRows; y < row; y++)
			if (InTheWay(g.Get(x, y)) && !(y == row - ClearRows && BlockRules.LetsLavaIn(g, new TilePos(x, y))))
				return false;
		foreach (ChestInfo c in ctx.World.SeenChests(new TilePos(x, row), 2))
			if (x - c.TopLeft.X is 0 or 1 && row - c.TopLeft.Y is 0 or 1)
				return c.Locked;
		return false;
	}

	/// <summary>
	/// A column of the bridge done: its cell in the row one to stand on (a block, or a tile already there that is stood on: a
	/// platform, a slope or half block of a ruined house's bricks, which the grid reads as platforms), the <see cref="ClearRows"/>
	/// over it open, the top one (the jump's spare) left in only where opening it would let lava in.
	/// </summary>
	public static bool Built(ITileGrid g, int row, int x)
	{
		if (!g.Get(x, row).CanStandOn())
			return false;
		for (int y = row - ClearRows; y < row; y++)
			if (InTheWay(g.Get(x, y)) && !(y == row - ClearRows && BlockRules.LetsLavaIn(g, new TilePos(x, y))))
				return false;
		return true;
	}

	/// <summary>A cell over the bridge the body cannot pass: a block, a slope or half block, a platform (mined: a jump would stop on it).</summary>
	private static bool InTheWay(CellKind k) => k.IsSolid() || k.IsShaped() || k.IsPlatform() || (k & CellKind.Hazard) != 0;

	/// <summary>The node standing at the end with the body's front column on it (the body is 2 wide: going west, the end is its left column).</summary>
	/// <summary>
	/// Columns kept between the body and the bridge's end while working: a hit's knockback (4.5 px a tick, vanilla Player.Hurt) at the
	/// very end threw the bot off into the lava below (world_wof E11, 2026-10-17: two of three deaths, "Hurt 40 (projectile type 44)
	/// ... at (3882,1041)" then "Fall: ... lava at (3887,1072), 31 rows down"). The reach (tileRangeX 5) covers the next column from here.
	/// </summary>
	public const int EdgeMargin = 3;
	/// <summary>Tiles within which a shooter or a worm (not a spared one) has the lip raised at the end (<see cref="ThreatNear"/>).</summary>
	public const int LipRadiusTiles = 30;
	/// <summary>Blocks the lip stands on the next column's deck block.</summary>
	public const int LipHeight = 2;
	/// <summary>
	/// Ticks the bridge holds behind a lip for a hostile that neither comes nor goes, then carries on; and ticks before another lip for
	/// one (a shot flying at the body raises it regardless). Hell is never empty: holding while anything was within 30 tiles stalled
	/// the bridge two game hours and the watchdog blocked the errand (world_wof E15, 2026-10-17, "[holding behind the lip]").
	/// </summary>
	public const int LipHoldTicks = 900, LipCooldownTicks = 1800;
	/// <summary>Vanilla NPC AI styles that throw or shoot from afar (NPCAIStyleID.HoveringFighter: Demons; Caster: Fire Imps) or come through blocks (Worm: Bone Serpents).</summary>
	public const int HoveringFighterAi = 22, CasterAi = 8, WormAi = 6;
	/// <summary>The column the lip stands on (or is going up on) while a threat is near; null otherwise.</summary>
	private int? lipAt;
	private long lipSince, lipCooldownUntil = long.MinValue / 2;

	/// <summary>Where to stand to work on the column past <paramref name="end"/>: on the deck, <see cref="EdgeMargin"/> columns short of it, or in the dip of a column passed over (<see cref="Passable"/>).</summary>
	private TilePos StandAt(int end, BotContext ctx)
	{
		// The retry from "a column back" (standBack) is a column closer instead: the reach (tileRangeX 5) ends a column past the next.
		int x = (Dir > 0 ? end - 1 : end) - Dir * (standBack ? EdgeMargin - 1 : EdgeMargin);
		x = Dir > 0 ? Math.Max(x, FromX) : Math.Min(x, FromX); // never behind the start: the deck begins there
		if (!Passable(ctx, ctx.Grid, Row, x))
			return new TilePos(x, Row - 1);
		// In a passed column's dip the body (20 px, over two columns) must be clear of the deck blocks either side: the run's left column.
		while (Passable(ctx, ctx.Grid, Row, x - 1))
			x--;
		return new TilePos(x, Row);
	}

	/// <summary>
	/// Placing the next block failed from the end (<see cref="MaxFailuresPerCell"/> tries): the next tries from a column back (the
	/// body's hitbox, the reach), then the skill fails (and the planner does not start the same step again at once). A tile in the
	/// cell that is neither stood on nor solid is mined before any try. Seen in game (measured run M12a, world_wof): "could not place a block at (2156,1042) (3 tries)" by the ruined
	/// tower's roof, the planner starting the same step again 33 times in a few seconds.
	/// </summary>
	private bool standBack;
	/// <summary>Ticks the game has refused the block for something in the cell (<see cref="IWorldInfo.PlaceBlocker"/>): an NPC, lava.</summary>
	private int occupiedTicks;

	public SkillStatus Tick(BotContext ctx)
	{
		ctx.Output = MoveInput.None;
		if (ctx.BodyIssue != null) {
			State = "paused: " + ctx.BodyIssue;
			return ctx.BodyIssue == "dead" ? Fail(ctx, "died") : SkillStatus.Running;
		}
		ITileGrid g = ctx.Grid;
		// The action under way done (the cell mined, or the block in): counted, and the next. A mined cell is done once no tile is
		// left in it (a non-solid one, such as a platform, too).
		if (actionCell is TilePos done && (actionPlace ? g.Get(done.X, done.Y).IsSolid()
				: ctx.World != null ? ctx.World.TileTypeAt(done.X, done.Y) < 0 : !g.Get(done.X, done.Y).IsSolid())) {
			if (actionPlace)
				Placed++;
			else
				Mined++;
			actionCell = null;
		}
		if (!g.Get(FromX, Row).CanStandOn())
			return Fail(ctx, $"no ground at the bridge's start ({FromX},{Row})");
		// From where it ended last tick while that column still stands (a scan of up to 1700 columns a tick otherwise).
		int scanFrom = End != FromX && (End - FromX) * Dir > 0 && (Built(g, Row, End) || Passable(ctx, g, Row, End)) ? End : FromX;
		End = BridgeEnd(g, Row, scanFrom, ToX, ctx);
		// The lip's column is the next deck block with two on top (below): the end is held short of it while the lip is wanted, so the
		// two go on before the bridge moves on; the lip gone with the threat, the two come down as cells in the way.
		bool threat = ShotIncoming(ctx) || ThreatNear(ctx) && ctx.Tick >= lipCooldownUntil && (lipAt == null || ctx.Tick - lipSince <= LipHoldTicks);
		if (!threat) {
			if (lipAt != null)
				lipCooldownUntil = ctx.Tick + LipCooldownTicks;
			lipAt = null;
		}
		else if (lipAt is int lip && (End - lip) * Dir >= 0)
			End = lip - Dir;
		LogProgress(ctx);
		if (End != lastEnd) {
			standBack = false; // on: from the end again
			lastEnd = End;
		}
		if (End == ToX) {
			// Done at the end, a cliff over lava: back from it first, out of a hit's knockback (about five tiles, FightSkill.KnockbackTiles),
			// before the next skill works here. Seen in game (world_wof E12, 2026-10-17): the arrows crafted at the end as the bridge was
			// done, a sickle's knockback carried the bot three tiles off it, 25 rows down, dead.
			if (Math.Abs(ctx.Node.X - ToX) < FightSkill.KnockbackTiles + 2 && (Dir > 0 ? ctx.Node.X > FromX : ctx.Node.X < FromX)) {
				ctx.Output.Horizontal = -Dir;
				State = "stepping back from the end";
				return SkillStatus.Running;
			}
			State = "done";
			ctx.Log.Info($"Hellbridge done: row {Row} from x {FromX} to {ToX}, {Placed} blocks placed and {Mined} cells mined here");
			return SkillStatus.Succeeded;
		}
		TilePos stand = StandAt(End, ctx), n = ctx.Node;
		bool onBridge = n.Y == stand.Y && ctx.Body.Grounded && (Dir > 0 ? n.X >= Math.Min(FromX, End) - 1 && n.X <= stand.X : n.X <= Math.Max(FromX, End) && n.X >= stand.X);
		if (!onBridge || walk != null) {
			// Off it (knocked down, a fight took us away, back from the house): a walk to the end.
			if (walk == null) {
				walk = new WalkToSkill(new GoalStandAt(stand, ctx.Physics)) { Quiet = true };
				walk.Start(ctx);
				State = $"walking to the bridge's end at {stand}";
			}
			SkillStatus ws = walk.Tick(ctx);
			if (ws == SkillStatus.Running)
				return SkillStatus.Running;
			string why = walk.FailureReason;
			walk.Stop(ctx);
			walk = null;
			if (ws == SkillStatus.Failed && ctx.Node != stand)
				return Fail(ctx, $"no way back to the bridge's end at {stand} ({why})");
			return SkillStatus.Running;
		}
		if (n.X != stand.X) {
			// Along the bridge to its end, braking there: from rest each step, never past it.
			ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(stand.X, ctx.Physics), ctx.Physics);
			State = $"walking along it to {stand}";
			return SkillStatus.Running;
		}
		int c = End + Dir;
		// 0. A hostile near while the end is a cliff over lava: a lip on the next column (its deck block and LipHeight on top) stops a
		// hit's knockback; the bridge holds behind it (the fight reflex takes the hostile) and takes the two down again, as cells in
		// the way, once it is gone. Standing back cannot do it: a knockback carries about five tiles (FightSkill.KnockbackTiles), more
		// than the reach allows. Seen in game (world_wof E11 and E12, 2026-10-17): four deaths from a sickle's or a bite's knockback
		// at the end, 20-35 rows down into lava.
		if (threat) {
			if (lipAt == null) {
				lipAt = c;
				lipSince = ctx.Tick;
			}
			for (int y = Row; y >= Row - LipHeight; y--) {
				CellKind lk = g.Get(c, y);
				if (lk.IsSolid())
					continue;
				if (lk.HasLava() || ctx.World != null && ctx.World.TileTypeAt(c, y) >= 0 || ctx.Blocks == null || !ctx.Blocks.CanPlaceAt(c, y))
					break; // no lip there: what is in the cell stays
				return Act(ctx, new TilePos(c, y), place: true);
			}
			ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(stand.X, ctx.Physics), ctx.Physics);
			State = $"holding behind the lip at x {c}: a hostile near";
			return SkillStatus.Running;
		}
		// 1. The body's rows over the next column, top down.
		for (int y = Row - ClearRows; y <= Row - 1; y++) {
			var cell = new TilePos(c, y);
			CellKind k = g.Get(c, y);
			if (k.HasLava() && !k.IsSolid()) {
				if (y == Row - ClearRows)
					continue; // the head's spare row: lava there flows down through the body's rows below, waited out there
				if (++lavaTicks > LavaWaitTicks)
					return Fail(ctx, $"lava streams through the bridge's way at {cell} (waited {LavaWaitTicks} ticks)");
				State = $"waiting for the lava at {cell} to drain";
				return SkillStatus.Running;
			}
			if (!InTheWay(k))
				continue;
			if (ctx.Blocks != null && float.IsPositiveInfinity(ctx.Blocks.BreakTicks(cell.X, cell.Y)))
				return Unmineable(ctx, cell);
			if (BlockRules.LetsLavaIn(g, cell)) {
				if (y == Row - ClearRows)
					continue; // the jump's spare row may stay: the body passes under it
				return Fail(ctx, $"the cell {cell} in the bridge's way has lava beside or above it: mining it would flood the bridge");
			}
			return Act(ctx, cell, place: false);
		}
		lavaTicks = 0;
		// 2. The bridge's own cell.
		var deck = new TilePos(c, Row);
		if (!g.Get(c, Row).CanStandOn()) {
			if (g.Get(c, Row).HasLava()) {
				if (++lavaTicks > LavaWaitTicks)
					return Fail(ctx, $"lava in the bridge's cell {deck}: no block goes into lava");
				State = $"waiting for the lava in {deck} to go";
				return SkillStatus.Running;
			}
			// A tile there that is neither stood on nor solid (a pot, a chain, a banner: placing a block over it fails, Player.PlaceThing):
			// mined first; so is a hot one (hellstone brick: it burns whoever stands on it, Collision.HurtTiles; the grid reads it
			// as a hazard, not to stand on), and a block goes in its place. Seen in game (measured run M12b): the deck ran over the
			// ruined houses' hot bricks, the bot burning ("run 1.665") until "reduced to charcoal" at (2442,1041).
			if (ctx.World != null && ctx.World.TileTypeAt(c, Row) >= 0) {
				if (ctx.Blocks != null && float.IsPositiveInfinity(ctx.Blocks.BreakTicks(c, Row)))
					return Unmineable(ctx, deck);
				return Act(ctx, deck, place: false);
			}
			// Past a column passed over (a locked chest's), the deck's cell has no block beside it to fix to (vanilla's rule; the chest is
			// not solid): a support goes in under it first, fixed to the chest's floor, as a player builds out from a floor.
			var support = new TilePos(c, Row + 1);
			if (Passable(ctx, g, Row, End) && !g.Get(c, Row).IsSolid() && !g.Get(support.X, support.Y).CanStandOn() && !g.Get(support.X, support.Y).HasLava()
				&& ctx.Blocks != null && ctx.Blocks.CanPlaceAt(support.X, support.Y))
				return Act(ctx, support, place: true);
			return Act(ctx, deck, place: true);
		}
		// 3. One tile on (a jump out of a passed column's dip onto the deck).
		ctx.Output.Horizontal = Dir;
		ctx.Output.Jump = n.Y >= Row;
		State = "stepping on";
		return SkillStatus.Running;
	}

	/// <summary>
	/// Why a cell in the bridge's way cannot be mined: hellstone (TileID 58) wants pick power 65 (Player.cs:55038-55041) and burns on
	/// touch; the ways past it a player has are a better pickaxe (Molten from hellstone itself, or the Nightmare Pickaxe: 12
	/// demonite bars and 6 shadow scales, Recipe.cs:10296-10302), or fire immunity to walk round it (an Obsidian Skin Potion, its
	/// fireWalk: Waterleaf, Fireblossom, Obsidian and Bottled Water at a placed bottle, Recipe.cs:1806-1812).
	/// </summary>
	private static string Unbreakable(BotContext ctx, TilePos cell) =>
		$"the cell {cell} in the bridge's way cannot be mined at pick power {ctx.PickPower} (tile {ctx.World?.TileTypeAt(cell.X, cell.Y) ?? -1}; hellstone wants 65 and burns on touch)";

	/// <summary>
	/// A cell in the way that the game will not mine: hellstone (pick-gated and hot) stops the bridge, saying what gets past it; a plain
	/// block under furniture (a ruined house's floor under a pot, a Hellforge, a chair: WorldGen.CanKillTile refuses the block while the
	/// furniture stands, TerrariaBlockActions.BreakTicks) has the furniture mined first, as a player does. Seen in game (X1B, X2B,
	/// 2026-10-17): "the cell (2255,1040) in the bridge's way cannot be mined at pick power 45 (tile 1 ...)": stone under a ruined
	/// house's furniture, and both runs stopped there, retrying in place.
	/// </summary>
	private SkillStatus Unmineable(BotContext ctx, TilePos cell)
	{
		CellKind k = ctx.Grid.Get(cell.X, cell.Y);
		// A hot cell (hellstone, hellstone brick) the pickaxe is too weak for stops the bridge, saying what gets past it. With the power
		// for it (65), what keeps the game from mining it is what stands on it, below. Seen in game (E7, 2026-10-17): "cannot be mined at
		// pick power 65 (tile 76)", a ruined house's hellstone brick with furniture on it, and the bombs tried instead killed the bot.
		if ((k & CellKind.Hazard) != 0 && ctx.PickPower < Progression.StageTable.HellstonePickPower) {
			Blocker = cell;
			return Fail(ctx, Unbreakable(ctx, cell));
		}
		// A platform, a slope, a pot, a chain: frame-important, so the paths never mine them (TerrariaBlockActions.BreakTicks), but a
		// pickaxe takes them, as a player clears a ruined house's floor. Seen in game (E5, 2026-10-17): "the cell (2413,1040) in the
		// bridge's way cannot be mined at pick power 45 (tile 19)", a platform two rows over the deck; the bridge stopped there twice.
		if (!k.IsSolid() || k.IsPlatform() || k.IsShaped()) {
			State = $"mining the tile at {cell} (a pickaxe takes it)";
			return Act(ctx, cell, place: false);
		}
		var above = new TilePos(cell.X, cell.Y - 1);
		if (ctx.World != null && ctx.World.TileTypeAt(above.X, above.Y) >= 0 && !ctx.Grid.Get(above.X, above.Y).IsSolid()) {
			State = $"mining the tile on {cell} first ({above})";
			return Act(ctx, above, place: false);
		}
		Blocker = cell;
		return Fail(ctx, Unbreakable(ctx, cell));
	}

	/// <summary>Mine or place at the cell: aim, use, and count the action's failures (three on a cell fail the skill).</summary>
	private SkillStatus Act(BotContext ctx, TilePos cell, bool place)
	{
		if (actionCell != cell || actionPlace != place) {
			actionCell = cell;
			actionPlace = place;
			actionTicks = 0;
			occupiedTicks = 0;
		}
		if (place) {
			if (!ctx.AllowPlace)
				return Fail(ctx, "building is off");
			if (ctx.Blocks == null || ctx.Blocks.BlockCount <= 0) {
				int carried = ctx.Inventory?.CountAny(ctx.Ids.BuildingBlocks) ?? 0;
				if (carried > 0 && ++waitForBlocks <= MaxWaitForBlocks) {
					State = "waiting for blocks on the hotbar";
					return SkillStatus.Running;
				}
				return Fail(ctx, $"out of blocks at x {cell.X} ({Math.Abs(cell.X - FromX)} of {Math.Abs(ToX - FromX)} tiles built)");
			}
			waitForBlocks = 0;
		}
		else if (!ctx.AllowBreak)
			return Fail(ctx, "digging is off");
		// Something the game refuses a block over while it is there: an NPC in the cell (a Bone Serpent's segments through the deck
		// row, a Hellbat: WorldGen.PlaceTile's Collision.EmptyTile), a trickle of lava (Player.PlaceThing_Tiles_CheckLavaBlocking).
		// No failure of the place: waited out as lava is (LavaWaitTicks), the block held ready so it goes in the tick the cell is
		// clear. Seen in game (world_wof E9, 2026-10-17): "placing at (2397,1042) did nothing in 90 ticks: the cell reads Empty, tile
		// -1", six tries from the end and a column back with three Bone Serpents under the bridge, and the errand blocked on it.
		string occupant = place ? ctx.World?.PlaceBlocker(cell.X, cell.Y) : null;
		if (occupant == null)
			occupiedTicks = 0;
		else {
			if (++occupiedTicks > LavaWaitTicks)
				return Fail(ctx, $"{occupant} in the bridge's cell {cell} for {LavaWaitTicks} ticks: no block goes in over it");
			if (occupiedTicks == 1)
				ctx.Log.Info($"Hellbridge: {occupant} in the bridge's cell {cell}: waiting for it to go");
			actionTicks = 0;
		}
		bool tree = !place && ctx.World?.IsTreeTrunk(cell.X, cell.Y) == true;
		int budget = tree ? TreeTicks(ctx) : ActionTicks;
		if (occupant != null)
			State = $"waiting for {occupant} to leave {cell}";
		else if (++actionTicks > budget) {
			int f = failures.TryGetValue(cell, out int was) ? was + 1 : 1;
			failures[cell] = f;
			actionTicks = 0;
			if (f == 1)
				ctx.Log.Info($"Hellbridge: {(place ? "placing at" : "mining")} {cell} did nothing in {budget} ticks: the cell reads {ctx.Grid.Get(cell.X, cell.Y)}, "
					+ $"tile {ctx.World?.TileTypeAt(cell.X, cell.Y) ?? -1}, us at {ctx.Node} ({ctx.Body.X:0},{ctx.Body.Y:0} px), {ctx.Blocks?.BlockCount ?? 0} blocks on the hotbar"
					+ (!place && ctx.World?.IsTreeTrunk(cell.X, cell.Y) == true ? $", a tree: the axe{(ctx.Blocks?.HasAxe == true ? "" : " (none on the hotbar)")}" : ""));
			// A chest (a ruined house's) in the deck's cell: the game keeps it while it holds items (WorldGen.KillTile: Chest.DestroyChest
			// refuses one with items), and the bot has a skill for that, so the errand loots it and the bridge comes back to a cell it
			// can mine. Seen in game (world_wof E10, 2026-10-17): "could not mine (3198,1042) (3 tries)", tile 21, three times in a row
			// at x 3197, 1050 tiles out, and the errand blocked on it.
			if (!place && IsContainer(ctx, cell)) {
				Chest = cell;
				return Fail(ctx, $"a chest at {cell} in the bridge's way (tile {ctx.World?.TileTypeAt(cell.X, cell.Y) ?? -1}): the game keeps it until it is looted empty");
			}
			// Escalate rather than repeat: from a column back, then give up (standBack).
			if (place && f == MaxFailuresPerCell && !standBack) {
				standBack = true;
				ctx.Log.Info($"Hellbridge: placing at {cell} failed {f} times from the end; trying from a column back");
			}
			else if (f >= (place ? 2 : 1) * MaxFailuresPerCell)
				return Fail(ctx, $"could not {(place ? "place a block at" : "mine")} {cell} ({f} tries{(place ? ", from the end and a column back" : "")})");
			else
				ctx.Log.Info($"Hellbridge: {(place ? "placing" : "mining")} {cell} did nothing in {budget} ticks, try {f + 1}");
		}
		// A tree in the way (an ash tree on a knoll: vanilla keeps the block under a trunk, WorldGen.CanKillTile, so the skill goes for
		// the trunk first) is chopped, with the axe: a pickaxe does nothing to a tree. Seen in game (world_wof E11, 2026-10-17): "could
		// not mine (3967,1037) (3 tries)", tile 634 (TreeAsh), twice in a row at 94 % of the bridge, and the errand blocked on it.
		ctx.Output.Tool = place ? ToolRequest.Block : ctx.World?.IsTreeTrunk(cell.X, cell.Y) == true ? ToolRequest.Axe : ToolRequest.Pickaxe;
		ctx.Output.Aim(cell.X, cell.Y);
		ctx.Output.UseItem = true;
		ctx.Output.Horizontal = Arrive.Steer(ctx.Body.X, ctx.Body.VX, Body.CanonicalLeft(StandAt(End, ctx).X, ctx.Physics), ctx.Physics);
		if (occupant == null)
			State = place ? $"placing {cell}" : $"mining {cell}";
		return SkillStatus.Running;
	}

	private void LogProgress(BotContext ctx)
	{
		int pct = (int)(Progress * 100);
		foreach (int p in ProgressPercents)
			if (pct >= p && loggedPercent < p) {
				loggedPercent = p;
				ctx.Log.Info($"Hellbridge: {p} % (x {End} of {FromX}->{ToX})");
			}
	}

	/// <summary>A shooter or a worm that is not spared within <see cref="LipRadiusTiles"/>: what throws the body off the end from afar or through the deck.</summary>
	private static bool ThreatNear(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (EntityInfo h in ctx.Hostiles)
			if (h.Damage > 0 && h.AiStyle is HoveringFighterAi or CasterAi or WormAi && !ctx.Spared(h) && h.DistanceTo(cx, cy) <= LipRadiusTiles * 16f)
				return true;
		return false;
	}

	/// <summary>A shot flying at the body (a spared Voodoo Demon's sickle too).</summary>
	private static bool ShotIncoming(BotContext ctx)
	{
		float cx = ctx.Body.CenterX(ctx.Physics), cy = ctx.Body.CenterY(ctx.Physics);
		foreach (ShotInfo s in ctx.Shots)
			if (s.Damage > 0 && s.DistanceTo(cx, cy) <= ShotDodge.ShotThreatPx && WallOfFleshPattern.Incoming(s, cx, cy, Core.Physics.Dodge.Horizon))
				return true;
		return false;
	}

	private static bool IsContainer(BotContext ctx, TilePos cell) =>
		ctx.World?.TileTypeAt(cell.X, cell.Y) is int t && (t == ctx.Ids.ChestTile || t == ctx.Ids.ChestTile2 || t == ctx.Ids.DresserTile);

	private SkillStatus Fail(BotContext ctx, string reason)
	{
		FailureReason = reason;
		State = "failed: " + reason;
		ctx.Log.Warn($"Hellbridge failed: {reason}");
		return SkillStatus.Failed;
	}
}
