# TerraBot: notes for Claude sessions

A tModLoader mod whose bot plays Terraria by pressing controls (Baritone/Altoclef style). Long-term goal:
fresh world → Moon Lord with no human input. `PLAN.md` has the design rationale and milestone checklist;
`README.md` has the in-game test script the user follows.

## Setup facts (verified 2026-10-10)
- Repo / mod folder: `C:\Users\karlf\Documents\My Games\Terraria\tModLoader\ModSources\TerraBot` (git, branch `main`).
  `ModSources\tModLoader.targets` is a hand-written shim importing
  `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tMLMod.targets` (tML regenerates it when you open
  Develop Mods; content is identical).
- tModLoader 1.4.4.9, build 2026.08 stable, `net8.0`, define `TML_2026_08`. .NET SDK 8.0.425 (winget).
- The user runs the game and reports back. **You cannot run the game.** Make every test run produce log output
  you can read afterwards.

## Build and test
```bash
dotnet build                                  # in the mod folder: compiles + packages Mods/TerraBot.tmod
dotnet test tests/TerraBot.Tests              # pure unit tests (pathfinder, physics, closed-loop executor sim)
```
- `dotnet build` must stay at 0 warnings. The tModCodeAssist analyzers run during build.
- After building, the user enables/reloads the mod in-game (Workshop > Manage Mods > Reload, or Develop Mods >
  Build + Reload). If packaging fails with "file in use", the game has the .tmod open: ask the user to unload.
- Tests must never end up in the mod: `build.txt` has `buildIgnore = ... tests\*` (the **in-game** Build button
  ignores the csproj and compiles every .cs not ignored) and `TerraBot.csproj` has `<Compile Remove="tests\**" />`.
  To verify, search the built .tmod bytes for a test class name: it must be absent.

## Architecture (layers, bottom-up)
| Folder | Terraria types? | Contents |
|---|---|---|
| `Core/Grid` | no | `TilePos`, `CellKind` flags, `ITileGrid`, `ArrayTileGrid` (ASCII parser), `GridWindow` cache |
| `Core/Physics` | no | `PhysicsParams`, `BodyState`, `MoveInput`, `PlayerPhysics` (vanilla tick replica), `Arrive` steering |
| `Core/Pathfinding` | no | `Body` (2×3 node model), `IMoveType` + `Moves/` (Walk, StepUp, Drop, Jump, DigMove, BuildMove), `BlockActions` (`IBlockActions`, `BlockAction`, `BlockRules`), `JumpLibrary`/`JumpTemplate`, `Goals`, `PathSearch`/`AStarPathfinder` |
| `Core/Execution` | no | `PathExecutor`: path + live `BodyState` → `MoveInput`; reports Finished/Stuck/OffPath/Blocked |
| `Bot/` | no | `BotContext`, `IBotLog`, `Skills/` (`ISkill`, `WalkToSkill`), `Brain/BotBrain` (stub: one target) |
| `Game/` | **yes** | `TerraBotPlayer` (ModPlayer), `TerrariaTileGrid`, `TerrariaBlockActions` (hotbar pickaxe/blocks, vanilla pickaxe-damage mirror), `PhysicsReader`, `BotKeybinds`, `BotCommand` (/bot), `DebugOverlaySystem`, `Perception/` snapshot |

**Rule:** `Core/` and `Bot/` must not reference Terraria, tModLoader or XNA. The test project compiles those
folders directly without tModLoader references, so a violation breaks `dotnet test`.

Control flow per tick (local player): `ProcessTriggers` (keybinds) → `SetControls`: build `BotContext`
(body from `Player.position/velocity/jump/releaseJump`, physics captured last tick in `PostUpdateRunSpeeds`) →
`BotBrain.Tick` → `WalkToSkill.Tick` (sliced A* `PathSearch.Step(4 ms)`, then `PathExecutor.Tick`) → write
`Player.controlLeft/Right/Up/Down/Jump`; if the `MoveInput` requests a tool, also `selectedItem` (only when
`itemAnimation == 0`), `controlUseItem` (released between swings for non-autoReuse items) and `controlTorch = false`.
The aim cell is written to `Player.tileTargetX/Y` in `PreItemCheck`, because vanilla recomputes the tile target
from the mouse *after* `SetControls`. When the bot is disabled, nothing is written.

### Pathfinding model
- Node `(x, y)`: body = columns `x..x+1`, rows `y-2..y`, `y` = feet row. Canonical hitbox left = `x*16+6`.
  `Body.NodeOf` maps pixels back to nodes; the rounding is deliberate (half blocks map to the full-block node).
- Edge costs are in **ticks**. Each move type implements `Expand` (generate edges) and `IsValid` (re-check while
  executing). Add a move = add an `IMoveType` + register in `MoveContext.DefaultMoves` + executor handling.
  Planned next moves: BreakBlock/PlaceBlock (cost = mine/place time), DropThroughPlatform (hold Down), running jumps.
- Jumps are **simulated, not hand-tuned**: `JumpLibrary.Build` runs `PlayerPhysics` with
  `JumpLibrary.ControllerInput` (the exact controller `PathExecutor` replays) from start offsets −3/0/+3 px and
  records swept `BodyCells` and descending `CrossCells`. If you change the executor's jump control, change
  `ControllerInput`, never one side alone.
- Move types are shared singletons and must be stateless; per-search scratch lives on `MoveContext`.
- World edits ride on steps as `PathStep.Actions` (Break/Place). The movement kind stays Walk/StepUp/Drop, so a
  tunnel is a Walk with breaks. Pillar is its own kind, with the place happening mid-jump.
  `PathStep.Source` names the move type that re-validates the step.
- **The planner does not track its own edits.** A move treats the cells of its current body as clear. That is
  what lets chained digging plan at all. `DigMove` also emits action-less "dug-earlier" walks for moves that
  only the stale grid would reject. Costs can be over-estimated; the executor skips actions whose cell is
  already done.
- Cost knobs live on `MoveContext`:
  - `BreakCostMultiplier` (1.5) and `BreakOverheadTicks` (8).
  - `PlacePenaltyTicks`: scaled by the hotbar stack, see `PlacePenaltyFor`.
  - `PillarRiskTicks` (30).
  - Tune them with regression tests on concrete shapes; `BlockMoveTests.WideDryPit` is the bridge-vs-pillar case.
- Safety rule semantics: *never undermine* a falling block. Mining sand top-down as part of the path is fine,
  and intended.

## Verifying game behaviour (don't trust memory)
tModLoader's API and vanilla physics change between versions. Before relying on a hook, field or constant, check:
1. The XML docs: `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tModLoader.xml` (grep `M:Terraria.ModLoader.ModPlayer.`).
2. ExampleMod on GitHub, branch `1.4.4`: `https://raw.githubusercontent.com/tModLoader/tModLoader/1.4.4/ExampleMod/...`.
3. Decompile the installed DLL (vanilla code such as `Player.JumpMovement` only exists there):
   ```bash
   dotnet tool install ilspycmd --version 8.2.0.7535 --tool-path <scratch>/tools
   DOTNET_ROLL_FORWARD=Major <scratch>/tools/ilspycmd -t Terraria.Player -r "<tml>/Libraries" "<tml>/tModLoader.dll" > Player.cs
   ```
   Key methods: `Player.Update` (per-tick order and defaults reset), `HorizontalMovement`, `JumpMovement`,
   `UpdateJumpHeight`, `Collision.StepUp/StepDown/TileCollision`, plus the fall-damage block (search `extraFall`).
   `PLAN.md` § "Verified player physics" records what was found. Update it when you re-verify.

Gotchas already hit:
- `ModCommand.Action` exceptions are swallowed by tML. `BotCommand` catches and logs them itself. Args arrive lower-cased.
- Interface layers Begin/End the SpriteBatch themselves. `Utils.DrawLine/DrawRect` take **world** coordinates.
- A ceiling bonk sets `velocity.Y = 0` for one tick mid-air, so the executor's "landed" checks require two grounded ticks.
- A new jump only starts if jump was released the previous tick (`releaseJump`).
- Analyzer CommonCollisionName: never name a type `Player`, `Tile`, `Item`, `NPC`, `Main`, `Mod`, ...
- Analyzer ChangeMagicNumberToID: use `TileID.X` constants, not tile numbers (names verified against the decompiled TileID).
- Solid blocks can't be placed into any player/NPC hitbox (`Collision.EmptyTile`, integer rectangle), so the
  pillar places only once `floor(Y) + height <= cellTop - 2`.
- Placement needs a solid 4-neighbour or a wall. Bridge and pillar cells are chosen so that the block we stand
  on is that neighbour.
- `Player.GetPickaxeDamage` is private and has side effects: `TerrariaBlockActions.PickDamage` mirrors it.
  Re-check it when tML updates.

## Reading the logs
- File: `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log` (previous runs in
  `Old/`). Mod lines look like `[12:34:56.789] [Main Thread/INFO] [TerraBot]: ...`. Grep for `[TerraBot]`.
- What gets logged:
  - `Bot ENABLED/DISABLED (source)`, `Target set to (x,y) (source); tile there: <CellKind>`.
  - `WalkTo start: target -> StandAt(goal)`.
  - `Plan #n start -> goal: <Status>: N steps, cost, expanded, ms :: step | step | ...` (first 40 steps).
  - `Re-plan #n: Stuck|OffPath|Blocked: <reason>`: the reason names the step, expected node and actual node.
  - `WalkTo reached ...`, `WalkTo ... failed: ...`, `Skill finished: ...`, and `Physics params changed: ...`.
  - `resync: settled at <node> during step ...; skipping to step n`: the bot overshot onto a later path node
    and carried on without re-planning.
  - `Hurt <dmg> (<cause>: NPC name / projectile / fall damage), knockback ...`, logged whether or not the bot is on.
    Knockback is the usual cause of `OffPath`.
  - `SNAPSHOT {json}` from `/bot dump`: player, physics, bot state/path, an ASCII tile window (legend in
    `Game/Perception/WorldSnapshot.cs`: `#` solid, `=` platform, `.` air, `~` water, `!` lava, `P` body,
    `T` target, `g` goal feet), NPCs, items, inventory.
  - `TRACE t<tick> node pos vel jump step [phase] in <input>` per tick while `/bot trace` is on (DEBUG level).
  - `STATUS ...` from `/bot status`: includes `dig/build on/off` and the hotbar tools
    (`pick <power> (slot n, <t>/hit) | <count> <block> (slot n)`).
  - Plans that change the world end with `, breaks N, places M`. Steps print their actions as
    `{break(x,y) place(x,y)}`. The executor phases `Mining (x,y) (i/n)`, `Placing ...` and `Pillar t<n>` show up in TRACE lines.
  - `Digging allowed/disabled`, `Building allowed/disabled` when toggled.
- To debug a failed run, find the `Plan` line, then the `Re-plan` reasons. If needed, ask the user to repeat it
  with `/bot trace` on and `/bot dump` at the start.

## Conventions
- Tabs, file-scoped namespaces, `TerraBot.<Folder>` namespaces, XML doc comments on public types. Comments
  explain *why* and cite the vanilla method being mirrored.
- Nullable and implicit usings are off in both projects; write explicit `using`s.
- Commits: small and descriptive, ending with the `Co-Authored-By` trailer. Run `dotnet build` (0 warnings) and
  `dotnet test` before committing.
