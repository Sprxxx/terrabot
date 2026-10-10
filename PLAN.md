# TerraBot plan

Long-term goal: a tModLoader mod whose bot plays Terraria from a fresh world toward Moon Lord,
Baritone/Altoclef style. This document covers the architecture and **Milestone 1**.

## Verified environment (2026-10-10)

| Thing | Value | Source |
|---|---|---|
| tModLoader | 1.4.4.9, build `2026.08.3.0` stable, define `TML_2026_08` | `client.log` header, `tMLMod.targets` |
| Target framework | `net8.0`, LangVersion 12 (ExampleMod overrides to `latest`) | `tMLMod.targets` |
| SDK | .NET SDK 8.0.425 (installed this session via winget) | `dotnet --list-sdks` |
| csproj | `<Import Project="..\tModLoader.targets" />`, which imports `<steam>\tModLoader\tMLMod.targets` | ExampleMod.csproj, `ModCompile.UpdateReferencesFolder` |
| Logs | `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log` | tML `Logging.cs` |

### Build pipeline facts that shape the layout
* `dotnet build` compiles via the csproj, then the `BuildMod` target runs `tModLoader.dll -server -build ... -eac <dll>`
  which packages `Mods/TerraBot.tmod`.
* **The in-game "Build" button ignores the csproj** and compiles every `*.cs` under the mod folder except paths
  matched by `buildIgnore`. So the test project is excluded twice: `buildIgnore = ... tests\*` in `build.txt` and
  `<Compile Remove="tests\**" />` in the csproj.
* `ModCommand.Action` exceptions are swallowed by tML (shown only as "Usage"), so the command wraps its body and
  logs errors itself.
* `GameInterfaceLayer.Draw` begins/ends the SpriteBatch itself (`Game` scale = zoom matrix, world pos minus
  `Main.screenPosition`). `Utils.DrawLine/DrawRect` take **world** coordinates.

## Verified player physics (decompiled `Terraria.Player` / `Terraria.Collision` from the installed tModLoader.dll)

Per tick for the local player: `SetControls` → … → `HorizontalMovement()` → `JumpMovement()` → gravity
(`velocity.Y += gravity`, clamp to `maxFallSpeed`) → `StepDown`/`StepUp` → `DryCollision` (position += velocity
with tile collision).

* Defaults reset each `Update`: `maxRunSpeed 3`, `accRunSpeed = maxRunSpeed`, `runAcceleration 0.08`,
  `runSlowdown 0.2`, `gravity 0.4`, `maxFallSpeed 10`, `Player.jumpHeight 15` (static), `Player.jumpSpeed 5.01`
  (static, + `jumpSpeedBoost`). Final values are readable in `ModPlayer.PostUpdateRunSpeeds`.
* Horizontal: holding a direction under max speed: `if moving the other way faster than runSlowdown, v -= runSlowdown`;
  then `v += runAcceleration` (same in air). No input: friction `runSlowdown` on ground, `runSlowdown/2` in air.
* Jump: starts only when `velocity.Y == 0 && releaseJump`: `vy = -jumpSpeed; jump = jumpHeight`. While held and
  `jump > 0`: `vy = -jumpSpeed; jump--` (bonk → `vy==0` → jump cancelled). Releasing sets `jump = 0`,
  `releaseJump = true`. ⇒ the bot must release jump ≥ 1 tick between jumps.
* Full jump from rest rises ≈ 98 px ≈ 6.1 tiles (simulated, not hand-computed: see `PlayerPhysics`).
* `StepUp`: while walking on the ground (not holding down) the player is lifted ≤ 16.1 px onto a **solid**
  (not solid-top) tile if the 3 cells above it and the head cell above the leading body column are free.
* `StepDown`: walking off a ledge with ground 7–17 px below snaps down (smooth 1-tile step down).
* Fall damage: on landing, `tilesFallen = (int)(pos.Y/16) - fallStart`; damage if `> 25 + extraFall`.
* Hitbox: `width 20`, `height 42` px → 2×3 tiles when standing. Collision treats a tile as blocking if
  `tileSolid`, or `tileSolidTop && frameY == 0`; solid-top tiles (platforms) only collide from above.

## Architecture

```
TerraBot/                      (git repo root = mod folder)
  TerraBot.cs                  Mod class
  Core/        PURE C#, no Terraria/XNA types (compiled into the test project too, enforcing purity)
    Grid/        CellKind, ITileGrid, TilePos, ArrayTileGrid (+ ASCII parser for tests/dumps)
    Physics/     PhysicsParams, BodyState, MoveInput, PlayerPhysics (tick replica), Arrive controller
    Pathfinding/ Body (2x3 clearance), IMoveType + Walk/StepUp/Drop/Jump move types, JumpTemplate library,
                 IGoal, AStarPathfinder, PathResult/PathStep
    Execution/   PathExecutor: turns a path + live BodyState into MoveInput, detects stuck/off-path/blocked
  Bot/         PURE C# bot runtime (also compiled into tests)
    Skills/      ISkill, SkillStatus, WalkToSkill (plan → execute → replan)
    Brain/       BotBrain (goal holder / skill sequencer, stub)
    BotContext, IBotLog
  Game/        Terraria-facing adapters (only place that touches tModLoader APIs)
    TerraBotPlayer   ModPlayer: ProcessTriggers (keybinds), SetControls (apply MoveInput), PostUpdateRunSpeeds
    TerrariaTileGrid ITileGrid over Main.tile
    Perception/      WorldSnapshot DTOs + builder (JSON for /bot dump)
    BotKeybinds, BotCommand (/bot), DebugOverlaySystem
  tests/TerraBot.Tests/  xUnit; links ../../Core/** and ../../Bot/**; never built into the mod
```

Layers (bottom-up): **Perception** (snapshot + live grid adapter) → **Pathfinding** (A*, Terraria-free) →
**Skills** (tick-driven, `Running/Succeeded/Failed`) → **Brain** (stub) → **Debug** (overlay, logs, dump).

### Control model
The bot only writes `Player.controlLeft/Right/Up/Down/Jump` inside `ModPlayer.SetControls()` (later also
`controlUseItem` and `selectedItem`), and only while enabled. Disabling happens in `ProcessTriggers` of the same
tick, so the player's own input applies that very tick. M1 leaves the mouse to the player. No teleporting, tile
edits or item spawning.

### Pathfinding model
* Node `(x, y)` = body occupies columns `x..x+1`, rows `y-2..y` (`y` = feet row). Standable = body cells
  passable and `(x,y+1)` or `(x+1,y+1)` is Solid/Platform. Canonical body left = `x*16 + 6` (centred).
* Moves implement `IMoveType { Expand(...); IsValid(...) }`, each edge costed in **ticks** (Baritone style).
  Adding `BreakBlock`/`PlaceBlock` later = new `IMoveType`s whose cost includes mining/placing time, plus a
  "world edits" overlay on the grid; nothing in A* changes.
  * `Walk` ±1: cost 16/maxRunSpeed.
  * `StepUp` ±1/−1 onto a solid tile: mirrors `Collision.StepUp` conditions.
  * `Drop` ±1/+k: walk off a ledge and fall straight (k ≤ safe fall, 25 + extraFall − margin).
  * `Jump` (dx, dy): **templates produced by simulating the real physics** (`PlayerPhysics`) with the same
    controller the executor uses (hold jump N ticks, optional horizontal delay, arrive-at-target steering).
    Each template stores the cells the body sweeps (union over start offsets ±3 px) and the cells where the
    feet cross a tile top while descending (must not be platforms). Planner checks those cells on the grid.
    Templates are rebuilt when the live physics params change (boots, buffs).
* A*: tick costs, admissible heuristic from max horizontal speed / max fall speed / jump rise speed,
  node budget; returns `Found`, `Partial` (budget hit → best node so far, execute then replan) or `NoPath`.
  The search is resumable (`PathSearch.Step(budgetMs)`): the skill spends ≤ 4 ms per tick on it while the bot
  stands still, so an unreachable target (40k expansions ≈ 2 s of CPU) never freezes the game.

### Execution and re-planning
`PathExecutor` runs one step at a time: walking runs are chained without stopping; jumps first align on the
start node (±3 px, |vx| small), then replay the template's controller; drops arrive slowly over the edge.
Re-plan triggers:
* Stuck: a direction is held for 45 ticks without the body moving, or a step exceeds its tick budget.
* Off-path: the bot settles on a node more than 1 tile from the current step's start and end.
* Blocked: the next 8 steps are re-validated every 15 ticks, and one fails.
* The physics params change materially (`PhysicsParams.MateriallyDiffers`): jump height/speed, gravity, the
  hitbox, the safe fall height, or run stats by more than ±25%.
  * Smaller speed drift keeps the path. `PathExecutor.TryAdopt` re-fits the remaining jumps to templates
    simulated with the new physics, and only re-plans if a jump no longer fits.
  * This came from the self-test: the test character's +21% speed toggle used to force a re-plan each time.
    At 0.8× speed a 6-tile jump would land one tile short, which is why jumps are re-fitted rather than
    replayed unchanged.

`WalkToSkill` fails after 3 consecutive `NoPath` results or 25 re-plans.

## Status
Milestone 1 is implemented: `dotnet build` passes with 0 warnings, and the unit tests pass, including
closed-loop executor runs in `tests/TerraBot.Tests/SimWorld.cs`.

First in-game test (2026-10-10, two worlds, ~30 walks) confirmed the following:
* Toggle and hand-back, walking, step-ups and step-downs, drops (up to 15 tiles, no damage), and gap and
  ledge jumps all worked.
* Blocked re-planning against a player-built wall worked.
* "No path" was correct for the pillar and the walled-off areas.
* The overlay and HUD rendered correctly.

Fixed after that run:
* Overshooting a 1-tile drop no longer triggers a re-plan; the executor resyncs to the later path step.
* Targets inside solid ground now resolve to the surface.
* Hits are now logged.

Those fixes have not been re-tested in game yet.

Open question: why vanilla StepDown sometimes doesn't snap at running speed (needs a `/bot trace` capture).

Milestone 2 (mining and placing) is implemented: 52 unit tests pass, including closed-loop tunnel, dig-down,
pillar and bridge runs in the simulator, and `dotnet build` has 0 warnings. In-game testing is pending (README,
"Test script: Milestone 2"). (Since verified by the self-test.)

Current (2026-10-10): Milestones 1–5 are done, plus platform drop-through and melee combat. `/bot auto`
takes a fresh character to an iron-tier pickaxe and digs in at night. 113 unit tests pass and the in-game
self-test passes 25/25.

## Milestone 1 checklist
1. Scaffold (csproj, build.txt, description, localization), builds with `dotnet build`, loads.
2. Keybinds: toggle bot (default `P`), set target to tile under cursor (default `O`); on-screen state.
3. `/bot goto <x> <y>` (also `~dx ~dy` relative), `/bot stop`, `/bot dump` (JSON snapshot → client.log),
   plus `/bot trace` (per-tick execution trace to the log) and `/bot status`.
4. WalkTo across uneven terrain by walking, step-ups, jumps and drops.
5. Re-plan when stuck or blocked.
6. Overlay: path, current step, jump-template cells, target, skill/state text.
7. Unit tests: flat ground, step up, gap, unreachable, plus physics sanity and a closed-loop executor
   simulation on fake grids.

## Milestone 2: mining and placing as costed moves

### Verified game mechanics (decompiled Player / WorldGen / Collision, tML 2026.08)
* **Aiming.**
  * `Player.Update` recomputes the static `Player.tileTargetX/Y` from the mouse *after* `SetControls`, then
    runs the smart-cursor lookup, then calls `ItemCheck()`. `ItemCheck()` calls `ModPlayer.PreItemCheck()`
    before `ItemCheck_Inner`.
  * ⇒ The bot aims by writing `tileTargetX/Y` in `PreItemCheck`. That is the mouse-aim equivalent: range,
    pick power and use speed still apply.
* **Swinging.** A swing starts when `controlUseItem` is held, `itemAnimation == 0`, and either `releaseUseItem`
  or `autoReuse` is true. Smart-select only acts while `controlTorch` is held, so the bot forces it off.
  `delayUseItem` (the mouse is over UI) cancels use.
* **Mining.**
  * Each hit happens when `toolTime(=itemTime) == 0 && itemAnimation > 0 && controlUseItem`. It adds
    `GetPickaxeDamage` to the tile's hit counter, and the tile breaks at ≥ 100.
  * The next hit comes after `CombinedHooks.TotalUseTime(useTime * pickSpeed)` ticks.
  * Damage is `pickPower`, divided for hard tiles (dungeon /2, hallow ores /2..../4, Lihzahrd /4,
    Chlorophyte /5), and doubled for dirt/sand/mud/clay/ash/silt/slush/snow/hardened sand.
  * Minimum pick powers apply (Ebonstone/Crimstone/Pearlstone 65, Meteorite 50, Demonite/Crimtane below the
    surface 55, Obsidian 55, Hellstone 65, dungeon bricks 100, hardmode ores 100/110/150, Chlorophyte 200,
    Lihzahrd 210). Modded tiles use `ModTile.MineResist/MinPick`.
  * `WorldGen.CanKillTile` must be true; it protects tiles under chests, trees and similar.
* **Reach.** `IsTargetTileInItemRange`: `tileRangeX = 5`, `tileRangeY = 4` tiles beyond the hitbox, plus
  `item.tileBoost`. Cells adjacent to the body are always in range.
* **Placing a solid block** (`PlaceThing_Tiles`, `WorldGen.PlaceTile`):
  * The target cell must be empty, or hold a `tileCut` plant or a `BreakableWhenPlacing` tile.
  * It needs a solid/beam/rope neighbour (4-neighbourhood) or a background wall.
  * `Collision.EmptyTile` refuses if the cell's rectangle intersects any player or NPC hitbox. So pillaring
    means jumping until `(int)Y + height <= cellTop`, then placing under the feet.

### Design
* `IBlockActions` (Core, Terraria-free) answers three questions, and the game adapter implements it from the
  hotbar:
  * How many ticks would mining this cell take? (∞ if forbidden or impossible.)
  * Can a block go here?
  * How many blocks are available?
* `PathStep.Actions`: an ordered list of `Break(cell)` / `Place(cell)` actions the executor performs before
  (or, for pillars, during) the movement. Movement kinds stay Walk/StepUp/Drop, plus a new Pillar kind.
* New move types:
  * `DigMove`: Tunnel (walk through a blocked column), TunnelUp (step up through blocks), TunnelDown
    (diagonal step down), DigDown (mine the two cells underfoot).
  * `BuildMove`: Bridge (place a block under the leading column, then walk), Pillar (jump and place
    underfoot). Pillar picks its column at run time, from whichever cell below is solid.
* Costs:
  * Breaking: mining ticks × 1.5 + 8 ticks overhead per block.
  * Placing: place ticks + a penalty that depends on the hotbar stack (3 ticks at ≥ 100 blocks, 8 at ≥ 30,
    25 below).
  * Pillar steps add 30 ticks of risk.
  * So the bot prefers existing routes, and only digs or builds when that's clearly cheaper.
  * The scaled penalty and the pillar risk came from the first Milestone 2 run. With 9,991 wood, the bot
    dropped into a 27-wide pit and pillared out instead of bridging, because a flat 20-tick penalty made each
    bridge tile cost 35 ticks.
* Safety rules (Core):
  * Never break a cell with a falling block (sand family) directly above it, unless that cell is also being
    broken in the same move.
  * Never break a cell next to lava.
  * Never break frame-important tiles, containers or tree/altar tiles (adapter).
  * Only hotbar items are used (M2).
  * `/bot dig off`, `/bot build off` disable the new moves.

## Milestone 3: running jumps
* A running jump starts at rest on the start node, like every jump, so templates stay deterministic. It
  sprints `RunupTiles` (2 or 4) along the start row and takes off when the hitbox passes the take-off point.
  Then it holds jump and arrive-steers as usual.
* `JumpController` is the single implementation of that policy, used by both `JumpLibrary` (simulation) and
  `PathExecutor` (replay).
* `JumpTemplate.Fits` also requires floor under every run-up node.
* Running entries exist only where the flight part (take-off node to landing) is not a standing-jump entry,
  i.e. only for distances a standing jump can't do. That keeps 178 extra entries instead of thousands.
* Reach with vanilla stats: standing 5 tiles, running flight 9 tiles at the same height.
* Library build is about 130 ms (was 80) per distinct physics set; it's cached.
* Later: flying starts, i.e. taking off from a walk chain without stopping at the run-up start.

## Dropping through platforms
`DropThroughMove`: when the body stands on platforms only, hold Down to fall through, then land like a normal
drop (same landing and fall-damage rules). Vanilla passes `fallThrough = controlDown` to collision, which ignores
solid-top tiles while falling slower than 1 px/tick. The executor holds Down only until the feet pass the
platform row, so it doesn't also fall through a platform it means to land on.

## Combat (`FightSkill`)
* Perception: hostiles are NPCs that pass vanilla `NPC.CanBeChasedBy` (the check homing projectiles use).
  The snapshot gives position, size, life and velocity.
* Target: keep the current target while alive, else the nearest within 25 tiles. An enemy that once came
  within 25 stays a target out to 50 (the "leash"), so a second slime that wandered off still gets chased.
* Approach with a quiet `WalkToSkill` to the target's feet (re-targeted every 30 ticks); close and level,
  steer straight.
* Swing: melee direction comes from `player.direction`, which vanilla sets from the movement keys and only
  while `itemAnimation == 0`, so the skill presses towards the enemy between swings. Aim uses the virtual
  mouse (`Main.mouseX/Y` set in `PreItemCheck`, restored in `PostItemCheck`).
* Quick-heal below 40% life, pulsed so vanilla sees a fresh key press.
* Done after 90 ticks with no hostile in range.

## Milestone 4: early-game loop (auto mode)
`/bot auto early` runs the early plan of `ProgressionPlanner` (below) through `BotBrain`. Whenever no skill is running, the planner looks at
the inventory and what it has seen, and returns the next skill:
1. no bench placed or carried: `GatherWood` to 10, then `Craft` a work bench;
2. bench carried: `PlaceItem` it beside the bot;
3. no wooden sword: gather to 7, walk back within 3 tiles of the bench, `Craft` the sword;
4. stockpile 30 wood;
5. done.

Skills:
* `ChopTreeSkill`: stand beside the trunk base, aim at it with the axe until the trunk is gone. Axe damage per
  hit is `axe × 1.2`, and a tree falls at 100 (copper axe: 13 hits).
* `CollectDropsSkill`: walk over item drops of a type until none is left in range.
* `GatherWoodSkill`: chop the nearest tree, collect, repeat; with no tree in sight, walk 40 tiles to look, alternating
  left and right, and give up after 6 tries.
* `CraftSkill`: one recipe click every 10 ticks until the count is reached; fails with the crafter's
  reason (`Missing`, `NeedsStation`, `NoRecipe`).
* `PlaceItemSkill`: a cell that `TileObject.CanPlace` accepts and whose footprint doesn't overlap the hitbox
  (rewritten in M5 to search wider and walk).

Brain rules:
* A hostile within 12 tiles (when carrying a weapon) pre-empts any skill but a fight.
* Three failures in a row stop auto mode.
* `KnownStations` remembers stations while out of sight and forgets one only when within 20 tiles and not
  seen. A bench the bot just placed is trusted for 120 ticks, because the scan runs every 20.

Hotbar housekeeping: tools are only used from the hotbar, but crafted items land in the backpack. Every
30 ticks, the best pickaxe/axe/weapon/block stack in slots 11–50 is swapped onto the hotbar if it beats the
hotbar's.

## Milestone 5: recipe-driven progression to an iron-tier pickaxe
`/bot auto` (plan `iron`, the default) gets a fresh character (copper tools) to an iron or lead pickaxe:
work bench, wooden sword, furnace, anvil, then the pickaxe. `/bot auto early` is the M4 chain. Both are plans
of `ProgressionPlanner`, which replaced `EarlyGamePlanner`.

### Verified mechanics (decompiled tML 2026.08)
* Recipes (`Recipe.SetupRecipes`): Torch ×3 = 1 Gel + 1 Wood (any wood). Furnace = 20 Stone + 4 Wood + 3
  Torch @ Work Bench. Iron Bar = 3 Iron Ore @ Furnace (Lead Bar the same with lead). Iron/Lead Anvil = 5 bars
  @ Work Bench. Iron/Lead Pickaxe = 10 bars + 3 Wood @ Anvil. From scratch that is 45 ore.
* tML turns vanilla's `anyWood` flag into the "Wood" recipe group (`acceptedGroups`). It also adds reverse
  recipes such as 4 Iron Fence → 1 Iron Bar, while fences are made from bars, so the recipe graph has cycles
  between crafted items.
* Tool range (`Player.IsTargetTileInItemRange`): tileRangeX 5, tileRangeY 4, plus the item's `tileBoost`.
  For the canonical standing hitbox of node (x, y) that is dx ∈ [−4−b, 5+b], dy ∈ [−5−b, 3+b]. Pickaxes need
  no line of sight.
* Station range (`Player.AdjTiles`): ±4 columns around the hitbox centre, rows from 3 above to 2 below the
  floor row under the feet.
* A world has one ore per tier: `WorldGen.SavedOreTiers.Copper/Iron/Silver/Gold` hold the tile type (e.g.
  Iron 6 or Lead 167).
* Night = `!Main.dayTime`.

### Planner (`Bot/Brain/ProgressionPlanner.cs`)
* Stages are needs: `StationPlaced(tile)`, `HaveItem(items, count)`, `PickaxeAtLeast(power, item)`. For the
  first unmet stage it resolves depth-first and returns the first leaf skill. It is re-evaluated after every
  skill, so it always works from the current inventory.
* Raw material, from `IGameKnowledge.SourceOf`: wood → `GatherWoodSkill`, gel → `HuntSkill` (slimes, AI
  style 1), stone or ore → `MineSkill` on the tile the item places (`createTile`), limited to natural tiles
  and the world's ore of each tier.
* Crafted item: the cheapest recipe by a rough gather-time estimate. Unobtainable branches cost infinity,
  e.g. lead in an iron world, or a recipe that only loops back to the item. A small cost per crafting step
  keeps a roundabout chain from tying with a direct one. Then it ensures every ingredient (recursively),
  ensures stations are placed, walks next to them (`GoalNearTile`, beside and not on top), and crafts.
* Station: if carried, place it, after walking to the work bench so stations stay together. Otherwise
  obtain the cheapest item that places it.
* Claims: amounts already counted for one branch are subtracted for its siblings, so torches and the
  furnace don't both count the same 4 wood.
* At night, surface gathering (wood, hunting) becomes a `ShelterSkill`; mining and crafting go on.
* Unresolvable (no recipe or source, too deep): `Stuck`, and the brain stops auto mode with the reason.

### New skills
* `MineSkill(item, tile, amount)`: collect matching drops; pick the nearest tile that is safe to break (the
  same `BlockRules` as digging moves) with depth weighted 1.5×; walk until it is in tool range
  (`GoalReachTile`, one tile of slack, never standing on the tile itself); swing until it is gone. With
  nothing in sight it digs down 15 rows, zig-zagging, up to 8 times. Each target gets one path attempt.
  Four unreachable targets in a row fail the skill, saying so if digging is off.
* `HuntSkill(item, aiStyle, amount)`: fight NPCs of that AI style when in sight (`FightSkill`), collect the
  drop, otherwise wander ±30 tiles and wait for spawns.
* `ShelterSkill`: dig a shaft 5 rows down, place blocks in the two cells above the head (the old floor row,
  where the shaft walls give placement a neighbour), wait for morning, mine the seal open. While sealed,
  hostiles do not pre-empt it.
* `PlaceItemSkill` (rewritten): the nearest accepted cell within ±10 columns and ±3 rows. It walks into tool
  range if needed and skips cells that turn out unreachable or refused.

### Brain changes
* After a fight in auto mode it picks up all drops within 15 tiles (gel, coins...).
* Perception rescans trees and stations on the first tick after the bot is switched on or stopped, so it
  never plans from a scan of a world that changed meanwhile.

### Actions that stand in for UI clicks (approved by the user 2026-10-10)
The bot otherwise acts only through controls. These four have no control to press, so they call what the
UI click would call:
* **Crafting**: `Main.CraftItem(recipe)`, the crafting menu's click handler. It only works for recipes that
  `Recipe.FindRecipes` lists as available (ingredients carried, station in range), consumes the ingredients
  and puts the result on the cursor. Then `Player.GetItem` moves it into the inventory like clicking an empty
  slot.
* **Station range**: `Player.AdjTiles()` normally runs only while the inventory screen is open. The crafter
  calls it right before `FindRecipes`, which is the same range check.
* **Moving items** between backpack and hotbar is a slot swap, like dragging.
* **Pointing**: the tile target (`tileTargetX/Y`) and the mouse position (`Main.mouseX/Y`) are overridden for
  the duration of `ItemCheck`. Range, tool power and speed are vanilla's.

## Not yet (noted for later)
Ropes/ladders, swimming (water only penalised, lava avoided), slopes/half-blocks (modelled as full blocks),
doors, breaking above the head while pillaring, flying jump starts, ranged/magic combat, dodging,
housing/NPCs, chests, torches placed for light, armour, biome woods (only forest Wood is a gather
source), conditional recipes (near water, biome...), bosses.

## Self-test history
Automated in-game self-test (`tools/selftest.ps1`, `Game/SelfTest/`) added 2026-10-10. It runs scenarios in
the real game with copies of the user's player and world.
* First run: 14/14 passed, deterministic across runs. Milestone 2 is verified in game, including the earlier
  failures: bridging over drop-and-pillar, running out of blocks, sand safety.
* After running jumps, drop-through, combat and M4: 22/22 passed, plus `early_game_full_hotbar` (23
  scenarios). There are 100 unit tests.
* M5: `iron_age_auto` (copper tools to an iron pickaxe in about 5,200 ticks, with no re-plans) and
  `night_shelter`. 25/25 passed, plus 113 unit tests. Bugs the in-game runs found: the scenario had digging
  off, which led to finding MineSkill had no fail-fast; station placement had too little room beside the bot;
  the cost cache let the iron bar ↔ iron fence cycle through; and perception was stale right after enabling.
* Seen once in `night_shelter`: the arena has 4 trees, so the bot left it for real trees, and a pillar step
  failed to place three times in real terrain (x 2003) before it recovered. Worth a look.
