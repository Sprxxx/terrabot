# TerraBot

A tModLoader mod with a bot that plays Terraria by pressing the same controls you do: left, right, jump, use
item, hotbar slot, and pointing at a tile. It never teleports, spawns items or edits tiles directly; blocks
change only because its pickaxe or block swing does it, with vanilla's range, tool power and speed.

What it can do so far: walk to a target tile across uneven ground. Moves:
- walking and 1-tile step-ups;
- jumps up to 6 tiles high, about 5 tiles across from a standstill, or 9 tiles across with a 2–4 tile
  running start;
- drops of up to 23 tiles;
- since Milestone 2: tunnelling through blocks, digging down, bridging over gaps and pillaring up, using the
  pickaxe and blocks in your hotbar.

## Build
From this folder:
```bash
dotnet build
```
This compiles the mod and writes `Documents\My Games\Terraria\tModLoader\Mods\TerraBot.tmod`. Unit tests:
```bash
dotnet test tests/TerraBot.Tests
```
In game: **Workshop → Manage Mods**, enable **TerraBot**, then **Reload Mods**. Later rebuilds only need
Reload. You can also use **Workshop → Develop Mods → TerraBot → Build + Reload**.

## Controls
| Input | Effect |
|---|---|
| `P` (keybind "Toggle Bot") | Bot on/off. Off returns control to you the same tick. |
| `O` (keybind "Set Bot Target") | Sets the target to the tile under the mouse. |
| `/bot goto <x> <y>` | Target a tile and switch the bot on. `~` means relative to you: `/bot goto ~20 ~0` is 20 tiles right. |
| `/bot stop` | Clear the target. The bot stays on but idles. |
| `/bot on`, `/bot off` | Same as the keybind. |
| `/bot status` | One-line status in chat and in the log. |
| `/bot dump` | Writes a JSON snapshot (player, path, nearby tiles as ASCII, NPCs, items, inventory) to client.log. |
| `/bot trace [on\|off\|<seconds>]` | Logs every tick while walking (default 30 s). |
| `/bot dig [on\|off]` | Allow or forbid mining (default on). No argument toggles. |
| `/bot build [on\|off]` | Allow or forbid placing blocks (default on). No argument toggles. |

If `P` or `O` clashes with another mod, rebind them under **Settings → Controls → Keybindings → Mod Controls**.
While the bot is on, it owns your movement keys. The mouse button stays yours except while the bot is mining or
placing; then it also switches your hotbar slot. It only uses items from the hotbar (slots 1–10): the best
pickaxe there, and plain blocks such as dirt, stone or wood. It never spends ores, gems, sand or seeds.

Coordinates are tile coordinates: x to the right, y downward. The HUD shows your current tile.

## What you should see
- **HUD** at the top centre of the screen:
  - Bot off: one grey line, `TerraBot OFF [P] toggle [O] target /bot`.
  - Bot on: a green line plus the current skill, the current step and its phase, the last plan's stats, and
    your tile, node, velocity and the inputs the bot is pressing.
- **World overlay**:
  - Magenta box: the 2×3 body cell box the planner thinks you occupy.
  - Yellow square: the target tile.
  - Cyan box: the goal area you'll stand in.
  - The path as lines between nodes, coloured by move: white walk, green step-up, blue drop, orange jump.
    Done steps fade, and the current step is drawn thicker with a box at its end.
  - During a jump, faint orange cells show the area the jump sweeps through. Red ticks mark tile tops it must
    not land on early.
  - Red outlined cells will be mined and green outlined cells will get a block. The current step's are bold.
    Pillar steps are purple.
- **HUD** (bot on) also shows `dig on/off build on/off | pick <power> (slot n, <ticks>/hit) | <count> <block> (slot n)`.

## Automated in-game self-test
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/selftest.ps1
```
This opens tModLoader in a separate, throw-away save folder, using copies of your `Pepi` character and the
`Enchanting Outskirts` world; your real saves are only read. It plays 14 scenarios in an arena above the
spawn and prints PASS/FAIL for each:
- walking, ledge jump, gap jump, a drop without fall damage, an unreachable target;
- tunnel, dig down, pillar, bridging a wide pit;
- sand in the path, a sand roof, a lava neighbour, running out of blocks, a wall appearing mid-walk.

Then it quits. It takes about two minutes. Keep the game window in front, because Terraria pauses while it's
in the background. Claude can run this on its own; the manual scripts below are for things the self-test
can't see, such as how the overlay looks.

## Test script: Milestone 2 (mining and placing)
Give the character a pickaxe and a stack of dirt or stone blocks (99+) in the hotbar. Say what pickaxe it was.

1. **Tools detected.** `/bot status`. Expected: the reply ends with `pick <power> (slot n, ...) | <count> <block> (slot n)`.
2. **Tunnel.** Build a 3-tall corridor with a roof (or use a cave) and wall it off with a 3-high block wall. Target
   the far side. Expected: the bot stops at the wall and switches to the pickaxe. The HUD shows `Mining (x,y) (1/3)`
   and the red outlined cells disappear one by one, then it walks through.
3. **Dig down.** On flat ground, point at a block about 8–10 tiles straight down and press `O`. Expected: it digs a
   2-wide shaft, one row at a time, and ends standing on that block. With digging allowed, a buried target means
   "stand on that block".
4. **Pillar.** `/bot dig off`, then target the top of a cliff or a block tower 8+ tiles high. Expected: it jumps and
   places a block under its feet each time (purple steps), then walks or jumps onto the top. `/bot dig on` afterwards.
5. **Bridge.** `/bot dig off`. Dig a dry pit 10+ tiles wide and 5+ deep (or find a chasm) and target the other side.
   Expected with 100+ blocks in the hotbar: it places blocks under its leading foot and walks across (green
   outlines), maybe finishing with a jump. With only a few blocks it may instead drop in and pillar out, because
   that spends fewer blocks.
6. **Prefers walking.** Target something over a small hill. Expected: it walks over; digging costs more than a
   short detour.
7. **Off switches.** `/bot dig off` and `/bot build off`, then repeat test 2. Expected: `no path` (or a long way
   round). The log shows `breaks 0, places 0` on plans.
8. **Safety (sand).** The rule is "never undermine sand", not "never mine sand". If sand is in the way, the bot mines
   it first, top-down, so nothing can fall.
   - To test the rule itself, the sand must sit **above** the tunnel: build a 3-tall corridor whose roof, right
     above the wall, is sand, and wall it off with a 3-high wall of dirt or stone.
   - Expected: the bot never mines the wall block directly under the sand. It digs a way around (under the wall,
     or over it through the dirt) or reports `no path`. The game logs `Hurt ... (projectile type 31)` if sand
     ever lands on you, so the log tells us.
   - It also won't mine a block next to lava.
9. **Out of blocks.** Put only 3 blocks in the hotbar and repeat test 4. Expected: it pillars until it runs out,
   then re-plans (`Blocked ... no longer valid`) and reports no path if there's no other way.

## Test script: Milestone 1 (movement)
Use a fresh small world. Journey mode is fine, but leave god mode off so fall damage is real. If you can,
disable the big content mods (Calamity, Fargo's...) for the first run; the bot reads your live movement
stats, but fewer mods means less noise. After each test, note what you did and what happened. Rough times
help me find it in the log.

1. **Load and HUD.** Enter the world. Expected: the grey HUD line. In client.log: `TerraBot 0.1.0 loaded.` and
   `Entered world ...`.
2. **Toggle.** Press `P`: chat says `TerraBot ON` and the HUD turns green. Your movement keys stop working.
   Press `P` again: control comes back immediately.
3. **Flat walk.** Point at the ground about 20 tiles away and press `O` with the bot on. A path appears and the
   bot walks there, stopping with its body over the yellow tile. The HUD then shows `idle - last: WalkTo Succeeded`.
4. **Steps and ledges.** Pick a target on top of a small hill or a 2–5 tile ledge. Expected: green step-ups for
   1-tile steps. For higher ledges the bot stops, lines up for a moment ("Jump align"), then jumps (orange).
5. **Gap.** Dig or find a gap 2–4 tiles wide that is too deep to walk through (fill the bottom with lava, or make it
   deeper than 23 tiles). Target the far side. Expected: one orange jump across.
6. **Drop.** Stand on a cliff 5–20 tiles high and target the ground below. Expected: the bot walks slowly to the
   edge, drops (blue) and takes no fall damage.
7. **Unreachable.** Target the top of a pillar at least 7 tiles high, or the inside of a sealed box. Expected: about
   3 seconds of `no path ... retry n/3`, then `last: WalkTo Failed: no path ...`. The game should not freeze.
8. **Re-plan, blocked.** Send the bot about 30 tiles (`/bot goto ~30 ~0`). While it walks, place a wall 3+ tiles
   tall in front of it. Expected: it notices within about a quarter second (`Re-plan #1: Blocked ...` in the log)
   and routes over or around the wall, or reports no path if it can't.
9. **Re-plan, knocked off the path.** Send the bot on a long walk near enemies (at night, or spawn a slime in
   Journey mode) and let one hit it. Knockback pushes it off the planned nodes. Expected: `OffPath` (or `Stuck`)
   in the log, then a new plan from where it landed.
10. **Hand-back.** Press `P` mid-jump or mid-walk. You should have full control instantly. Press `P` again and it
    plans afresh from wherever you are.
11. **Logs for me.** Before or after an odd case, run `/bot dump`. If something looks wrong in motion, run
    `/bot trace` and repeat it.

Then just tell me "done". I can read `client.log` from the tModLoader install folder myself.

## Known limits
- Jumps are standing jumps only: the bot stops, aligns and jumps. Running jumps (longer gaps) come later.
- No platform drop-through, ropes, doors or swimming physics. Water is only avoided by cost, and lava is never
  entered.
- Mining and placing use hotbar items only. Pillars can't break blocks above the head yet; it needs open sky
  or a separate tunnel step.
- The planner doesn't track its own edits. It assumes cells it's standing in are clear, which covers chained
  digging, but it may over-estimate some costs.
- If your mouse cursor is over a UI element (inventory, a menu), the game blocks item use. Move the mouse away
  while the bot mines or builds.
- Slopes and half blocks are treated as full blocks.
- The bot pauses while you are dead, mounted, grappling, on a rope, or in reversed gravity.
- Single-player first. On a multiplayer client, unreceived world sections look like empty air.

See `PLAN.md` for the design and `CLAUDE.md` for developer notes.
