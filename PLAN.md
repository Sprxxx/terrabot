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

Current (2026-10-10): Milestones 1–7 are done. `/bot auto` takes a fresh character to a house, iron gear,
200 max life and a bow, then defeats the Eye of Cthulhu. 141 unit tests pass; see the self-test history for the
in-game suite.

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

## Milestone 6: a house, a broadsword and iron armour
Plan `settle` (the default for `/bot auto`) is the iron plan plus a house that passes the game's housing
check, an iron-tier broadsword, and the iron-tier armour set (helmet, chainmail, greaves), worn.

### Verified mechanics (decompiled tML 2026.08)
* Recipes: Iron/Lead Helmet 15 bars, Chainmail 25, Greaves 20, Broadsword 8, all @ Anvil (copper/tin sets
  12/20/16). Wooden Door 6 Wood, Chair 4, Table 8, Wood Wall ×4 = 1 Wood, all @ Work Bench.
* Housing (`WorldGen.StartRoomCheck` / `CheckRoom` / `RoomNeeds` / `ScoreRoom`, which is what the housing
  query runs):
  - an 8-neighbour flood fill from a room cell that stops at solid tiles, with closed doors counting as
    solid. The tiles it reaches, the frame included, must number between 60 and 750;
  - every open cell needs a player-placed wall (`Main.wallHouse`) or a solid tile within 2 cells, both
    horizontally and vertically;
  - a chair, a table, a door and a light source (`TileID.Sets.RoomNeeds`);
  - `ScoreRoom` needs a home spot: a solid floor tile with solid tiles on both sides and 3 free rows above.
    `hiScore` −1 means occupied, ≤ −250 means too evil.
* Walls (`Player.PlaceThing_Walls`): the wall item at a target within tool range, with a tile or wall on a
  4-neighbour. The player's hitbox doesn't matter.
* Equipping: right-click calls `ItemSlot.SwapEquip(inventory, Context.InventoryItem, slot)`, which swaps the
  piece with `player.armor[0..2]`.
* Furniture footprints come from `TileObjectData` (size and origin): table 3×2 with origin (1,1), chair 1×2
  with origin (0,1), door 1×3 with origin (0,0).

### Building (`Bot/Building/`)
* `Blueprint.SmallHouse`: 12×6 including the frame (72 room tiles). Interior 10×4 with wood walls, a doorway
  in the left column under a frame block, a table, a chair, a torch on the back wall, and the door, placed
  last.
* `SiteFinder`: within 40 columns of the work bench, the floor row is the ground under the plan's middle. It
  picks the site with the fewest edits (blocks to place ×1, tiles to mine ×2) and rejects liquid, trees,
  platforms, stations, unbreakable tiles, or more than 40 edits.
* `BuildSkill`, in phase order:
  1. mine the room cells clear, top-down;
  2. place frame blocks bottom-up, each next to an existing tile;
  3. place walls;
  4. place furniture, never standing where it goes;
  5. place the door from outside the house;
  6. run `IWorldInfo.CheckHousing`, the game's query.

  The current target is sticky: re-picking "the nearest" every tick made the bot step off the cell it stood
  on and oscillate. Three failures on one cell fail the skill.
* While building, every plan cell is reserved (`BotContext.Reserved` → `ReservedBlocks`), so path moves
  never dig through the frame or pillar up inside the room.
* Planner `HouseBuilt`: the site is remembered across re-plans. It gathers what is still missing (building
  blocks for the bare frame cells, walls for the bare room cells, each unplaced furniture item), then builds.
  After two failed builds on one site it tries another.

### Armour
`Wearing(item)`: worn → done; carried → `EquipSkill` (`ICrafter.TryEquip`); otherwise craft it. Hotbar
stocking picks up the broadsword by damage per tick.

### Bugs the simulator found (all fixed, each with a test)
* Livelock: a skill restarted "collect drops" for a drop outside the collect radius, and the collect
  finished at once. Drop checks now use `CollectDropsSkill.AnyInRange`.
* Livelock: an unreachable drop was retried by each fresh collect. Callers now share one skip set.
* Build oscillation (see sticky target).
* Test fakes: SimWorld placed a block for any item use (walls, furniture), and fake drops didn't fall.

### Bugs the in-game runs found (all fixed)
* The bot mined the floor out from under its own work bench, furnace and anvil. Furniture with no support
  breaks and drops, so it placed them again and finally found no free floor. `TerrariaBlockActions.BreakTicks`
  now refuses any tile with a frame-important tile (furniture, stations, doors) on top, for mining and for
  digging moves. Station placement also searches ±20 columns.
* "Out of blocks" mid-build while 20 stone sat in the backpack: the hotbar dirt stack ran out, and stocking
  only swaps a stack in when it is clearly bigger, so the stone came up only after the dirt hit zero. The
  build now waits for stocking (up to 90 ticks) while the backpack still has blocks, and the planner asks for
  10 spare blocks (walking may pillar with them).
* A search that started on a goal node (a zero-step "Found") counted as "no path". It now counts as arrived.
* Targets it can never reach: ore under the arena's unbreakable floor, and trees outside its walls. Each try
  ran A* to the 40,000-node cap (about 2.5 s), and a fresh skill tried the same ones again. Now a shared
  `BotContext.UnreachableTiles` (cleared when auto starts) remembers them, plus same-type tiles within 4 of an
  unreachable ore. Mining and chopping walks get one attempt with a 12,000-node limit.
* No flat floor anywhere near (bumpy or dug-up ground): station placement now walks ±25, then ±50 tiles from
  where it started and looks again.
* Hotbar stocking was skipped whenever its 30-tick mark fell mid-swing. While chopping that happened again and
  again, so a crafted sword could stay in the backpack. It now retries on the next free tick.
* Self-test arena: unbreakable side walls. The bot used to wander out into the real world around the spawn,
  which made runs depend on that terrain (a failed `night_shelter`, and `settle_auto` ending up 138 rows down).
* Test data: with spawns off and finite trees and ore, the settle scenario needs four slimes (gel for the
  house torch on top of the furnace's three), 10 trees and 300 ore.

## Milestone 7: the first boss (Eye of Cthulhu)
Plan `boss` (the default for `/bot auto`) runs settle, then 200 max life, an iron-tier bow, 500 wooden arrows,
and the Eye of Cthulhu defeated. Plan `crystals` is just the life part.

### Verified mechanics (decompiled tML 2026.08; research notes in this section)
* **Arrows** (projectile 1, Projectile.AI_001): straight for 14 updates, then +0.1 px/update² down, capped at 16,
  no drag, no extra updates.
  - Launch speed = bow shootSpeed + ammo shootSpeed: wooden bow 6.1, iron/lead/gold/platinum bow 6.6, wooden
    arrow +3, flaming arrow +3.5.
  - The shot starts at the player centre and points at the mouse.
* **Damage to NPCs**: damage − defense/2 (minimum 1), ±15%.
  - Eye of Cthulhu: 2800 life (normal mode), 12 defense and 15 contact damage in phase 1; below 50% life
    defense 0 and 23 damage.
  - It hovers about 200 px above the player and dashes 3 times. In phase 1 it spits Servants of Cthulhu (8 life);
    normal mode spawns none in phase 2. It leaves at dawn.
  - Expected hits: iron bow + wooden arrow 7/hit in phase 1, 13 in phase 2.
* **Summon**: the Suspicious Looking Eye (6 Lens @ Demon Altar) works only at night with no Eye alive.
  - Natural spawn would need 5 crystals, defense > 10, 4 town NPCs and a 1/3 roll each night.
  - Demon eyes drop a Lens 1/3 of the time and only spawn at night on the surface.
  - Demon altars cannot be broken or placed pre-hardmode; they are found in the world.
* **Life crystals**: heart tile 12, 2×2, frame-important. Any pickaxe breaks it (100 damage), and it drops 1
  Life Crystal (the item places nothing). Using one gives +20 max life, up to 15 crystals.
* **Recipes**:
  - Iron/Lead bow: 7 bars @ Anvil.
  - Wooden Arrow ×25: 1 Wood + 1 Stone @ Work Bench.
  - Flaming Arrow ×10: 10 arrows + 1 torch.
  - Lesser Healing Potion ×2: 1 Mushroom + 2 Gel + 2 Bottle @ placed Bottle. Not used yet.

### Ranged combat
* `Core/Physics/Ballistics`: simulates the arrow update by update and searches the launch angle (bisection,
  per arrival tick) so the shot meets the target's predicted position. Targets on the left are solved in a
  mirrored frame.
* FightSkill shoots fliers (AI styles 2/4/5/14) and anything out of melee reach within 40 tiles; with a bow it
  engages at that range. It only fires when the simulated arc is clear (`ArcClear`). Melee needs a clear line
  too.
* Enemies out of sight are approached by path. If there is no path, that enemy is left alone. Danger
  (pre-empting other work) counts only hostiles in line of sight. The user saw the bot fight a demon eye
  through a wall: the eye was outside the arena, and nothing checked line of sight.

### Boss fight (`BossFightSkill`)
* Summon (UseItemSkill), then wait up to 5 s for the boss to appear: the item is used up before the boss is
  visible.
* Kite: run to ±20 tiles of the start and back (hopping every 50 ticks until the dodge planner replaced it, see
  "Classic difficulty" below).
* Shoot servants within 8 tiles first, then the boss. Quick-heal below 45%.
* Bosses are perceived out to 200 tiles. The brain never pre-empts a boss fight.
* Done when `IWorldInfo.BossDefeated` (NPC.downedBoss1 and friends) is true.

### Planner
* `LifeAtLeast` uses a carried crystal, or mines one (`DroppedByTile`: crystal from the heart).
  `IWorldInfo.CanMineTarget` lets MineSkill break that frame-important target.
* `BossDefeated`: get the summon item, then wait for night (`WaitSkill`), then fight.
* Hunting nocturnal prey (demon eyes for lenses) waits for night by day and does not shelter at night.
* Unplaceable stations (demon altar) are searched within 150 tiles and remembered. If none is found, the plan
  is stuck with "needs a DemonAltar: none within 150 tiles, and nothing can place one".
* `PrepFor(boss, need)`: the life, bow and arrow stages count as done once that boss is defeated. Without it,
  the bot went back to crafting arrows right after winning.

### In-game results
* `shoot_demon_eye`: wooden bow, a demon eye shot from 18 to 40 tiles, no damage taken.
* `life_crystals`: 100 to 200 max life in 2779 ticks.
* `eye_of_cthulhu` (geared): won in 6814 ticks, losing 62 life.
* `boss_auto` (from a settled character, at night): placed its stations, built the house, crafted 500
  arrows, hunted lenses, found the altar, crafted the summon and won. 11,685 ticks.
* The test world is a Journey world: the Eye had 1400 life and hit for 1-4. A classic world will be harder,
  and is untested.
* Final full suite on the finished milestone: 31/31 passed, 141 unit tests. `boss_auto` won in 12,371 ticks and
  `eye_of_cthulhu` in 6,093.

## Classic difficulty (2026-10-11)
On Classic (character Peppe, world "Noxious Vision of Organs"; `selftest.ps1 -Player/-World/-WorldFile`) the
Eye has 2800 life and dashes properly. The first try: 18 hits, a death at 94 boss life. What changed:

* **Quick-heal never worked.** Vanilla reads `controlQuickHeal` in Player.Update *before*
  `PlayerLoader.SetControls`, and ResetControls clears it next tick. The bot now calls `Player.QuickHeal()`
  (what the key does; same checks and potion sickness). The Journey fights never dropped below 45%, so nobody
  noticed.
* **Deaths** are counted in `ModPlayer.Kill`: SetControls does not run while dead, so the bot never saw
  "dead". The boss fight fails with "died".
* **Dodge planner** (`Core/Physics/Dodge.cs`): every tick, 8 plans (left/right/stand × no jump/full/short
  jump) are simulated for 50 ticks with the real player physics against the threats; the plan hit latest (or
  never, widest gap) wins, ties going to the kite direction. It takes over from kiting when kiting comes within
  16 px, and hands back at 32.
* **Eye model** (`EyePattern`, from NPC.AI style 4): ai[0] phase, ai[1] step, ai[2] timer, ai[3] dash count.
  - First form: hover 200 px above (accel 0.04, speed 5) for 600 ticks, then 3 dashes at 6 px/tick, 150 apart,
    braking ×0.98 after 40.
  - Second form: hover 120 px above (0.07, 6) for 200 ticks, dashes at 6.8, 130 apart, ×0.97.
  - Expert numbers are in too.
  - Threats are predicted with this behaviour, including the next dash aimed at where the simulated player
    will be. A straight-line model missed both the hover that follows the player (jumps into it) and dashes
    about to start.
* **Turning is the danger.** A dash aimed at a standing player cannot be escaped: a 100 × 110 eye, 0.08 px/tick²
  acceleration (DodgeTests shows it). The kite turns only when the next dash is ≥ 60 ticks away; past the hard
  end it turns anyway.
* Result: `eye_of_cthulhu` on Classic won in 7955 ticks, 5 hits, no deaths, 157/200 life left.

## Milestone 8: a boss arena
From the guides (Desktop/Projekt/terrabot/docs/RESEARCH.md): build an arena before the fight, keep it away from the house, add
regeneration.

### Verified (decompiled Recipe.cs / Item.cs)
* Campfire: 10 any wood + 5 torches, by hand; tile 215, 3×2. Cozy Fire within 170×125 tiles.
* Heart Lantern: 1 life crystal + 4 chain, by hand. Tile 42 style 9, 1×2, hangs from a solid tile above.
  Heart Lamp: +1 HP/s within 170×125 tiles.
* Chain: 1 iron-tier bar → 15 at an anvil.

### Design
* `Blueprint.Arena`: 96 wide, a floor row of blocks and 7 rows of open air above. Torches every 12 tiles on the
  floor, a campfire in the middle.
  - The width was 64 at first. In game that left only 9 tiles between "turn when safe" and the end, less than
    one gap between dashes, so turns were often forced with a dash coming.
* `SiteFinder`: limits per blueprint (arena: 600 edits, 80 columns), keep-out rectangles (the house plus 4), and
  trees count as edits when the blueprint fells them.
* `BuildSkill` for the arena:
  - **Chop phase first:** a tree in the open cells is felled with `ChopTreeSkill` at its base, which may be a
    neighbouring column; in game, axing a branch or root did nothing.
  - **No housing check.**
  - **Reserves only the floor and item cells,** so walking may dig open cells that get mined anyway.
* The heart lantern hangs in the house: `SmallHouse(ids, heartLantern: true)` adds it under the roof at (2,1).
  The bot places it from outside; placing needs no line of sight.
* The planner:
  - `ArenaBuilt`, plus the plan `arena`.
  - In the boss plan, after the arrows: `PrepFor(Eye, ArenaBuilt)` and `PrepFor(Eye, HouseBuilt(heartLantern))`.
  - Before summoning:
    - walk to the arena middle;
    - rest to 90% life, once per summon attempt (the lantern and campfire refill it);
    - fight with `BossFightSkill { Arena = columns }`.
* The fight in the arena:
  - Turn when safe 10 columns before an end, and anyway 2 before.
  - The ends are walls to the dodge planner.
  - The bot never presses towards an end it stands at; a dodge into the end once ran it out of the arena.

### The cost estimate froze the game
The lantern chain (lantern → chain → anvil → iron bar → furnace → torch → gel) is 12 deep, past the recursive
cost estimate's depth limit of 10. Raising the limit to 24 made it exponential on the real recipe graph, because
only raw costs were cached. The game hung for 4.5 minutes; the fake world's few recipes never showed it.

It is now a fixpoint, `SolveCosts`:
* Collect everything an item can depend on: ingredients, and the items that place missing stations.
* Iterate "cheapest recipe" until nothing changes.
* The result is exact, handles cycles (a way round one is never cheaper), and takes 3,393 recipe evaluations on
  the 40-level test graph.

Passes over 50 ms are logged. The self-test logs a heartbeat every 30 s and `tools/watch_selftest.sh` reports
STALL after 60 s without a log line.

### Surviving a Classic night (found by the `boss_auto` runs)
* **Fliers with only a sword:** FightSkill held its ground and met demon eyes as they came, instead of chasing
  their "feet tile"; chasing had cost 150 life. Drops close by are picked up while the next flier is still far
  off, because with fliers coming one after another the fight never ends and "loot after the fight" never came.
* **Retreat:** below 30% life with no potion and enemies in sight, the brain digs in with
  `ShelterSkill.Recover(0.8)`. It is never interrupted to fight and is capped at 2 minutes.
  - The dig column is one whose cells down to 5 deep can all be mined. Straight down was under the work bench
    once, and a 40,000-node search ran for 2.7 s while demon eyes killed the bot.
  - Its walk has a small limit and one attempt.
* **Protected cells:** `BotContext.Protected` keeps every house cell and the arena floor off limits to path
  moves (never broken or filled). A retreat had dug through the house's doorway.
* **After a death** the brain drops the running skill (stale, at the old spot) and plans again from the spawn.
  The boss fight notices "died" itself.
* **Night left:** a night boss is summoned only with at least 4.5 minutes of night left. Late summons saw the
  Eye fly off at dawn at 1275/2800.
* **Arena edges:**
  - Turn anyway 4 columns before an end; a hit knocks the bot a tile or two.
  - Brake within the stopping distance (v²/2a + 8 px).
  - Walk back to the middle if off the arena.
  - Dodge plans that end within 10 tiles of an end lose points.
  - Knock-offs still happen; the bot is back within 1-3 s.

### In-game results
* `arena_auto` (rough ground: mound, pit, two trees; wood and gel only): it crafted torches and a campfire,
  felled the tree, mined the mound and built the arena in about 2,100 ticks.
* `boss_auto` on Classic, with a 64-wide arena:
  - It placed the stations, built the house and crafted 500 arrows.
  - It built the arena (0 edits on flat ground), crafted chains and the heart lantern and hung it, hunted
    lenses and crafted the summon.
  - It walked to the arena and won: 14,829 ticks of fight, finishing at 200/200 life.
  - Its hits came from the scenario's demon eyes and from the forced turns at the arena's left end, both fixed
    since.
* `boss_auto` on Classic with everything above and the 96-wide arena: won in 25,551 ticks (14,133 of fight).
  - 13 hits (163 damage), all from the Eye; no deaths.
  - 200/200 life at the end; 42 dodges.
  - With the final edge rules (turn 4 tiles before an end, dodges avoid ending within 10 tiles of one): won in
    23,723 ticks (13,862 of fight). 1 hit in the whole run (21 damage), never off the arena, no deaths,
    200/200 at the end.
  - Demon eyes now come only while the bot hunts for lenses. Sent from the first tick, they met a sword-only bot
    building its house, and on Classic took most of the night.

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
* **Equipping armour**: `ItemSlot.SwapEquip`, the right-click handler.
* **Using items** (life crystal, boss summon) is holding the item and pressing use: a control, not a UI call.
* **Housing check**: `WorldGen.StartRoomCheck` + `RoomNeeds` + `ScoreRoom`, read-only, as the housing query runs it.
* **Pointing**: the tile target (`tileTargetX/Y`) and the mouse position (`Main.mouseX/Y`) are overridden for
  the duration of `ItemCheck`. Range, tool power and speed are vanilla's.
* **Talking to an NPC and pressing its first chat button** (Milestone 11, docs M11_SPEC.md section 2c):
  `SetTalkNPC` + `GetChat` (the right-click's chat opening, `Main.HoverOverNPCs`), then the button-1 handler
  (`NPCLoader.PreChatButtonClicked/OnChatButtonClicked`, `NPC.SpawnSkeletron`). The click is read only while the
  chat panel is drawn, which the fast self-test skips, so the handler runs with the game's guards: chat range
  (80 x 64 px round the player's centre), the Old Man's "Curse" button only at night and as
  `NPCLoader.SetChatButtons` leaves it, no Skeletron head up already (`TerrariaGameInfo.PressFirstChatButton`).
* **Throwing an item** (Milestone 12, docs M12_SPEC.md section 2c) is the Throw control (`controlThrow`, held one tick:
  `Player.dropItemCheck` drops the selected stack). A stack of one is first split off: one into an empty slot, as a
  right-click on the stack and a click on an empty slot do, then onto the hotbar like dragging it; a favourite mark is
  cleared as an alt-click does (`TerraBotPlayer.HoldOneToThrow`). A stack of N Guide Voodoo Dolls in lava kills N-1 more
  town NPCs, so only one is ever thrown.
* **Journey research** (approved by the user 2026-10-17; `Game/TerrariaJourney.cs`): a bag stack into the research slot and
  the research button, as the shift-click with the research menu open runs it (`ItemSlot.cs:640-647`:
  `Main.CreativeMenu.SwapItem`, then the button's `CreativeUI.SacrificeItem(out int)`, CreativeUI.cs:293/303-354, with
  `ItemLoader.CanResearch` and the catalog's cap); the rest is swapped back. Never `CreativeUI.ResearchItem` (research from
  nothing). Only once the bag holds the whole count research still takes, in one go (the user's way of playing), never gear
  without a spare left, never the item in hand mid-swing (`Bot/Brain/ResearchHabit.cs`).
* **Journey duplication**: the duplication menu's shift-click on a researched item (`ItemSlot.cs:618-625`, context 29: a
  `maxStack` clone, `OnCreated(JourneyDuplicationItemCreationContext)`, `Player.GetItem`), only if `Player.ItemSpace` takes
  it. The planner's `DuplicateSkill` for any need met by a researched item.

## Milestone 9: real-world play up to the Eye of Cthulhu (in progress)
Scenario `world_eye`: the copied real world (Peppe on "Noxious Vision of Organs", a small Crimson world), the
character's own inventory, normal spawns, starting at the world spawn. Run with `-Fast -DrawEvery 16` (about 30x)
and with the window unfocused (the focus pause is hooked off). Run by run, decisions and results go in
`Desktop/Projekt/terrabot/docs/NIGHT_LOG.md` (outside the repo).

Done so far:
* Survival: depth limits by defense, death spots avoided, retreat and rest, recall potions, and a night spent in
  the house (doors count as open space with the auto-open setting). With defense 6+ the bot works at night.
* Pathfinding in real terrain: broken support, plan repair, region dumps replayed as tests, and partial plans that
  never end past a self-undermined step.
* Stations, house, anvil, broadsword and pickaxe in a real world. Best so far: Iron Broadsword at game minute 14
  (run 31), and the house on day one plus the first night at home (runs 35 and 36).

Left: armour, 200 life (crystals), bow and arrows, the arena, the summon, and the win. Known issues: the
underground desert counts as unsafe (sand on top), and mining near the depth limit drops into caverns.

## Milestones after the Eye (planned in progression order, as a normal player plays)
Each mechanic is to be verified against the decompile and terraria.wiki.gg when its milestone starts.
* **M10, the evil biome boss**, by the world's evil (`GameIds.Crimson`, WorldGen.crimson). Corruption: the Eater of
  Worlds by Worm Food (docs M10_SPEC.md; plan `evilboss`, scenario `world_eow`). Crimson (built 2026-10-17, branch
  `boc-crimson`; same plan `evilboss`, scenarios `world_boc`, `fight_boc`, `fight_boc_ironbow`, not yet measured): the
  Brain of Cthulhu by a Bloody Spine (30 Vicious Powder + 15 Vertebrae at an altar, Recipe.cs:14414). Stages: the Tendon
  Bow from the Eye's crimtane (from the bag), 15 vertebrae hunted from crimeras on the crimson's ground, vicious mushrooms
  (crimson plants of frame 270) at a placed bottle into 30 powder, the spine at a (crimson) altar, unholy arrows from the
  vertebrae left over (after the spine: they share the vertebrae), an arena where the Crimson zone holds, the fight there
  by day or night. The fight (`BrainPattern`): the Creepers shot first (those dashing at us before the rest: a hit ends a
  dash), the Brain once it can be hurt; kite away from its swarm (it drifts at 1 px/tick) and from its chase (8 px/tick,
  eased by 50); the dodge planner models the Creepers' orbit round where the Brain will be. The Brain is perceived while it
  hides (dontTakeDamage), for its contact damage. Crimson Hearts (3 smashed summon it) are not used.
  After the win: Crimtane gear and the Deathbringer Pickaxe (12 bars + 6 Tissue Samples, Recipe.cs:10369), not built.
* **M11, Skeletron** (built 2026-10-17, docs M11_SPEC.md; plan `skeletron`, scenario `world_skeletron`, not yet
  measured). Prep from the boss plan's gear (a bow, 500 arrows, 200 life, potions and a recall potion from the bag);
  the Dungeon found along the surface by the snow/jungle rule, stopping when the Old Man shows; a 160-wide arena
  by him on his entrance's side, raised over the lake there, the sky over it open; the night waited for at its end;
  the summon by the Old Man's Curse button (a UI-equivalent action) before night time 10800; the fight stands under
  the hovering head, runs from its spin, dodges the hands' lunges from their wind-up, escapes dawn by recall
  potion. The Dungeon Guardian's ground (below the surface on a dungeon wall, until Skeletron is beaten) is never a
  path's node. After the win the dungeon opens (Muramasa, Water Bolt, golden keys): M11b.
* **M12, the Wall of Flesh** (built 2026-10-17, docs M12_SPEC.md; plan `wof`, scenario `world_wof`, not yet
  measured). Prep from the boss plan's gear (a bow, 1000 arrows, 200 life, 10 potions from the bag, blocks for the
  bridge); the Guide alive (his head on the map); a hellevator 60 tiles east of the spawn, dug in 20-row legs round
  lava cells with a ledge block after each (later trips drop ledge to ledge), onto ground on row UnderworldLayer + 42
  (the ruined tower's roof); a hellbridge laid block by block over the lava by a walking builder (L = 1.25 x 62,175 / DPS: 3829 tiles from fight_wof's 20.3 a second, both ways from the shaft, its summon end 120 tiles from the edge); Voodoo
  Demons spared (not fought, shot or shot through) until the hunt on the bridge's last 100 tiles; the doll thrown off
  the far end with the Throw key (one, a stack split first) into the lava below; the fight keeps a band ahead of the
  Wall beyond the Hungry's reach, one way, shoots the upper eye, dodges the lasers, never jumps into The Tongue; the
  loot from the brick box. Synthetic scenarios (by name): doll_lava_east/west/noguide/stack2, fight_wof(_unholy),
  voodoo_rule. Molten Fury, Hellstone and Shadow armour (the spec's upgrades S1u, M12b) are not built.
  Optional on the way: Queen Bee (jungle), King Slime.
* **M13, early Hardmode.** The Pwnhammer smashes altars (Cobalt/Palladium, Mythril/Orichalcum,
  Adamantite/Titanium), a hardmode anvil and forge, and survival against the spreading evil biomes and new enemies.
* **M14, the mechanical bosses.** The Destroyer, the Twins and Skeletron Prime (summons crafted from souls), an
  arena with platforms, Hallowed armour and wings.
* **M15, Plantera.** The bulb in the underground jungle after all three mechs. Chlorophyte, the arena in the
  jungle.
* **M16, Golem.** The Temple Key, the Lihzahrd temple's traps, Lihzahrd Power Cells at the altar.
* **M17, the Lunatic Cultist and the Lunar Events.** The cultists at the dungeon, then the four pillars (shields
  down by kills).
* **M18, the Moon Lord.** The summon after the pillars, the best gear from the pillar fragments, a long arena.
  This completes the game.

## Not yet (noted for later)
Ropes/ladders, swimming (water only penalised, lava avoided), slopes/half-blocks (modelled as full blocks),
doors, breaking above the head while pillaring, flying jump starts, magic combat, accessories beyond wearing what is found, healing and buff potions, better arrows, biome woods (only forest Wood is a gather source),
conditional recipes (near water, biome...), bosses after the Eye.

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
* M6: `build_house` (valid housing per the game's query in 674 ticks) and `settle_auto` (copper tools to house,
  iron broadsword and full iron armour in about 20,000 to 24,000 ticks). The last full run passed 26/27. Its failure was
  `early_game_full_hotbar` checking for a "moved" log line; the sword had legitimately been crafted straight
  onto the hotbar, so the check now looks at the hotbar. The scenarios touched by the final hotbar-stocking fix
  (5 of them) were re-run on the final build and pass. 124 unit tests. Arena walls since this milestone.
* M8 (2026-10-11):
  - `arena_auto` and `boss_auto` on Classic pass.
  - The affected scenarios pass on the final build: `build_house`, `settle_auto`, `eye_of_cthulhu`,
    `life_crystals`, `night_shelter`, plus 8 early ones. That is 5/5 targeted plus 8/8 before the full run was
    stopped.
  - 178 unit tests. No more full 40-minute suites for routine regressions; only the scenarios a change touches.
