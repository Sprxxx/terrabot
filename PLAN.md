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
"Test script: Milestone 2").

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

## Out of scope for M1 (noted for later)
Running-jump templates (currently standing jumps only), dropping through platforms, ropes/ladders, liquids
physics (water is only penalised, lava avoided), slopes/half-blocks modelled as full blocks, doors,
break/place moves, async/time-sliced A*, combat, crafting, real brain.

Automated in-game self-test (`tools/selftest.ps1`, `Game/SelfTest/`) added 2026-10-10. It runs 14 scenarios in
the real game with copies of the user's player and world. First full run: 14/14 passed, deterministic across
runs. Milestone 2 is now verified in game, including the earlier failures: bridging over drop-and-pillar, running
out of blocks, sand safety.
