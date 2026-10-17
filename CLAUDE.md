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
| `Core/Physics` | no | `PhysicsParams`, `BodyState`, `MoveInput`, `PlayerPhysics` (vanilla tick replica), `Arrive` steering, `Ballistics` (arrow flight, lead and drop), `Dodge` (look-ahead dodge planner: simulated plans vs. modelled threats) |
| `Core/Pathfinding` | no | `Body` (2×3 node model), `IMoveType` + `Moves/` (Walk, StepUp, Drop, DropThrough, Jump, DigMove, BuildMove), `BlockActions` (`IBlockActions`, `BlockAction`, `BlockRules`), `JumpLibrary`/`JumpTemplate`/`JumpController` (standing and running jumps), `Goals`, `PathSearch`/`AStarPathfinder` |
| `Core/Execution` | no | `PathExecutor`: path + live `BodyState` → `MoveInput`; reports Finished/Stuck/OffPath/Blocked; re-fits jumps on small physics changes (`TryAdopt`) |
| `Bot/` | no | `BotContext`, `IBotLog`, `Perception.cs` (`EntityInfo`, `DropInfo`, `CombatInfo`), `World.cs` (`IWorldInfo` incl. tile queries, `IInventory`, `ICrafter`, `GameIds`, `Surroundings`), `Knowledge.cs` (`IGameKnowledge`: recipes, raw sources, station items), `Skills/` (`WalkToSkill`, `FightSkill` (melee and bow), `BossFightSkill` (kite, dodge, arena bounds) and `EyePattern` (the Eye's attack cycle from ai[]), `UseItemSkill`, `WaitSkill`, gathering: `ChopTreeSkill`/`CollectDropsSkill`/`GatherWoodSkill`, crafting: `CraftSkill`/`PlaceItemSkill`, `MineSkill`, `EquipSkill`, survival: `HuntSkill`/`ShelterSkill`, `BlastOutSkill` (bomb through a wall the pickaxe cannot break)), `Building/` (`Blueprint`: small house (optionally with a heart lantern), NPC rooms built onto it with shared walls, and boss arena; `SiteFinder`, `BuildSkill`), `Brain/` (`BotBrain`: manual target/skill or auto mode; `ProgressionPlanner`: recipe-driven next-need resolver with plans `early`, `iron`, `settle`, `crystals`, `arena`, `boss` and `housing` (NPC housing: `ProgressionPlanner.NpcHousing.cs`, a stage of the boss plans after the armour); item costs by fixpoint, `SolveCosts`) |
| `Game/` | **yes** | `TerraBotPlayer` (ModPlayer: perception, controls, aim, hotbar stocking), `TerrariaTileGrid`, `TerrariaBlockActions` (hotbar pickaxe/axe/weapon/blocks, vanilla pickaxe-damage mirror, `StockHotbar`), `TerrariaGameInfo` (trees, stations, tile queries, placement checks, inventory counts, crafting, and `IGameKnowledge` built once from the loaded recipes/items, mods included), `PhysicsReader`, `BotKeybinds`, `BotCommand` (/bot), `DebugOverlaySystem`, `Perception/` snapshot, `SelfTest/` (in-game scenarios) |

**Rule:** `Core/` and `Bot/` must not reference Terraria, tModLoader or XNA. The test project compiles those
folders directly without tModLoader references, so a violation breaks `dotnet test`.

Control flow per tick (local player): `ProcessTriggers` (keybinds) → `SetControls`: build `BotContext`
(body from `Player.position/velocity/jump/releaseJump`, physics captured last tick in `PostUpdateRunSpeeds`) →
`BotBrain.Tick` → `WalkToSkill.Tick` (sliced A* `PathSearch.Step(4 ms)`, then `PathExecutor.Tick`) → write
`Player.controlLeft/Right/Up/Down/Jump`; if the `MoveInput` requests a tool, also `selectedItem` (only when
`itemAnimation == 0`), `controlUseItem` (released between swings for non-autoReuse items) and `controlTorch = false`.
The aim cell is written to `Player.tileTargetX/Y` in `PreItemCheck`, because vanilla recomputes the tile target
from the mouse *after* `SetControls`. Weapons aim with a virtual mouse: `Main.mouseX/Y` is pointed at the
target in `PreItemCheck` and restored in `PostItemCheck`. When the bot is disabled, nothing is written.

Things with no control to press go through the function the UI click would call; they're listed in PLAN.md
under "Actions that stand in for UI clicks". Keep that list current, since it's the exception to the rule
that the bot acts only through controls. The items are `Main.CraftItem`, `Player.AdjTiles`, inventory slot
swaps, the tile-target and mouse overrides, an NPC's first chat button (the Old Man's Curse), and Journey Mode's
research slot and duplication shift-click (`Game/TerrariaJourney.cs`; off on Classic and in the synthetic self-tests
unless `Scenario.Journey`; `selftest.ps1 -NoJourney` turns it off for real-world runs). Journey log lines:
`Journey: researched N <item> (...)`, `Journey: duplicated N <item> (...)`, `need N more <item> (researched: duplicating to M)`.

### Pathfinding model
- Node `(x, y)`: body = columns `x..x+1`, rows `y-2..y`, `y` = feet row. Canonical hitbox left = `x*16+6`.
  `Body.NodeOf` maps pixels back to nodes; the rounding is deliberate (half blocks map to the full-block node).
- Edge costs are in **ticks**. Each move type implements `Expand` (generate edges) and `IsValid` (re-check while
  executing). Add a move = add an `IMoveType` + register in `MoveContext.DefaultMoves` + executor handling.
- Jumps are **simulated, not hand-tuned**:
  - `JumpLibrary.Build` runs `PlayerPhysics` with `JumpController`, the same object `PathExecutor` uses to
    replay jumps. It simulates from start offsets −3/0/+3 px and records the swept `BodyCells` and the
    descending `CrossCells`.
  - Jump behaviour lives only in `JumpController`. Never re-implement it on one side.
  - Entries are keyed by (dx, dy, runup); `RunupTiles > 0` marks a running jump, see PLAN.md Milestone 3.
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
  - `Physics changed slightly (...); keeping the plan, re-fitted N jump(s)`: a small speed change was absorbed.
    `Physics changed (...): ... re-planning` means the change was material, or a remaining jump stopped fitting.
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
  - Auto mode: `Auto mode ON/OFF`, `Auto: <why> -> <Skill>` for each planner decision, `Danger: pausing <Skill>
    to fight`, `Auto: 3 failures in a row, stopping auto mode (...)`, and `Auto: early-game chain complete ...`.
  - Skills: `Fight start/target/gone (kill #n)/quick-heal/over`; `ChopTree start` / `... is down after N ticks`;
    `GatherWood start/done` and `GatherWood: no trees in sight, exploring towards (x,y)`; `Craft start: item N,
    needs <recipe>`, `Craft done`, `Craft item N failed: Missing|NeedsStation|NoRecipe ...`; `Placed item N at (x,y)`.
  - `Hotbar: moved <kind> <item> from inventory slot a to hotbar slot b (swapped out <item>)`.
  - Boss (M7): `Fight: shooting <npc> with <bow> (<n> arrows), arrival in <t> ticks`, `Fight: <npc> is out of sight and there
    is no way to it; leaving it`, `Auto: found a DemonAltar at (x,y)`, `Wait start: for night`, `Boss fight start/summoned`,
    `Boss fight: <boss> at <life>/<max>, us <life>/<max>, <n> arrows` (about every 10%), `Boss fight: lost sight of the boss`,
    `Boss fight: boss <n> defeated after <t> ticks`, `Boss fight failed: <reason>`.
  - Classic fights: `Boss fight: dodge <dir> (+ jump n) (kiting ... is hit in t ticks ...)`, `Boss fight: forced to turn at
    (x,y) (wall|far end), next dash in t ticks`, `Quick-heal: life a -> b, potion sickness t ticks`, `Died (...)`.
  - Arena (M8): `Auto: arena site (x,y) (N edits)` (none within 80: `Auto: no arena site ... : <reason> N (n at (x,y)), ...;
    looking within 160`, then `... the boss will be fought on open ground`), `Build start: boss arena ...`, `ChopTree start` inside it,
    `Auto: walking to the arena to summon ...`, `Auto: resting to 90% life before summoning ...`, `fighting ... in the arena`.
  - Survival: `Danger: life a/b and no potion; retreating from <Skill>`, `Shelter start at (x,y): digging in to recover to
    80% life at column x`, `Shelter: done waiting (...)`, `Died during <Skill>; starting afresh from (x,y)`,
    `Boss fight: off the arena at (x,y), walking back` / `back in the arena`, `Auto: too little night left for the ...`.
  - Trapped: `WalkTo: searching once more from (x,y) without ending at a dead end (up to N nodes)` (after an early "no node
    closer than the start"); `Auto: 3 walks in a row found no path from (x,y) ...; looking for a wall to blast through`,
    `BlastOut start`, `BlastOut: N open cells walkable ... trying K nearest the goal`, `BlastOut: <Bomb> at (x,y) (tile t ...):
    thrown from ... out of the blast's box ... in T of the F-tick fuse`, `BlastOut: threw a ...`, `BlastOut: clear of the blast
    at ...`, `BlastOut: (x,y) is open ...`, `BlastOut failed: <reason>` (each wall tried and why not).
  - Wall of Flesh (M12): `Hellevator start/done`, `Hellevator: at (x,y), N rows to go`, `Hellevator: no deeper from ... looking for a way
    onto row R`, `Auto: underworld reached at ...: the hellbridge goes on row R from x a to b`, `Hellbridge: 25 % ...`, `Hellbridge done`,
    `Throw start/failed`, `Throw: split one ...`, `Throw: one Guide Voodoo Doll thrown east`, `Boss fight: wall of flesh summoned at (x,y),
    from the east`, `Boss fight: the Wall from the east N tiles off (band from M ...): running west|standing`, `Boss fight: Wall of Flesh
    defeated: N ticks ... life taken (DPS a second), from x a to b, L lasers came at us, D dodges, H heals`, `Auto: unplanned wof`.
  - Self-test: `SELFTEST heartbeat <scenario> t<ticks> ...` every 30 s; `Planner: slow pass, N ms` over 50 ms.
    Watch runs with `tools/watch_selftest.sh` (prints STALL after 60 s without a log line).
  - Building (M6): `Auto: house site (x,y) (N edits)`, `Build start: small house at (x,y)`, `Build: <Kind> (x,y) failed (...),
    attempt n/3`, `Build done: ... is valid housing` or `Build ... failed: built, but the housing check says: <reason>`;
    `Equip start` / `Equipped item N`.
  - NPC housing: `NPC housing: N rooms (V valid), O occupied (Guide, ...), E empty; H homeless, C can come (Merchant, ...)` when
    the survey changes, `...: building room n at (x,y) (NPC room (left wall shared), N edits)`, `NPC housing: room n at (x,y) is valid
    housing`, `NPC housing: giving up on the room site ...`, `NPC housing: <why>; left for a day (24 game hours), the plan goes on`.
    Self-test `npc_housing` ends with `SELFTEST npc_housing: town NPC <name>: home (x,y), in the bot's room at (x,y)` lines.
  - Planner (M5): `Auto: <why> -> <Skill>` with reasons like `need 15 more Iron Ore (mining to 15)`, `walking to the
    Furnaces to craft Iron Bar`, `placing a Iron Anvil`, `night: sheltering instead of GatherWood (...)`;
    `Auto: stuck on <stage>: <reason>; stopping auto mode` when a need cannot be resolved.
  - `Mine start/done`, `Mine: no <item> within 40 tiles, digging down towards (x,y)`, `Mine: skipping (x,y): ...`,
    `Mine <item> failed: 4 <item> tiles in a row unreachable (digging is off)`; `Hunt start/done`, `Hunt: ...
    wandering towards (x,y)`; `Shelter start`, `Shelter: sealed in at (x,y), waiting for morning`, `Shelter: morning,
    opening up`, `Shelter done`; `Place: (x,y) accepted by the check but nothing was placed; trying elsewhere`.
- To debug a failed run, find the `Plan` line, then the `Re-plan` reasons. If needed, ask the user to repeat it
  with `/bot trace` on and `/bot dump` at the start.

## Conventions
- Tabs, file-scoped namespaces, `TerraBot.<Folder>` namespaces, XML doc comments on public types. Comments
  explain *why* and cite the vanilla method being mirrored.
- Nullable and implicit usings are off in both projects; write explicit `using`s.
- Commits: small and descriptive, ending with the `Co-Authored-By` trailer. Run `dotnet build` (0 warnings) and
  `dotnet test` before committing.
