# TerraBot

A tModLoader mod with a bot that plays Terraria by pressing the same controls you do: left, right, jump, use
item, hotbar slot, and pointing at a tile. It never teleports, spawns items or edits tiles directly; blocks
change only because its pickaxe or block swing does it, with vanilla's range, tool power and speed.

What it can do so far:
- **Get around** (`/bot goto`, `O`): walking and 1-tile step-ups; jumps up to 6 tiles high, about 5 tiles
  across from a standstill or 9 tiles with a 2–4 tile running start; drops of up to 23 tiles; dropping through
  platforms; tunnelling, digging down, bridging gaps and pillaring up with the pickaxe and blocks it carries.
- **Fight** (`/bot fight`): melee with the best sword or shortsword it has, chasing enemies over the same
  paths, and quick-healing when low.
- **Play on its own** (`/bot auto`): from a fresh character's copper tools to an iron (or lead) pickaxe. It chops
  trees, kills slimes for gel, makes torches, mines stone and ore, builds a work bench, furnace and anvil,
  smelts bars and crafts the pickaxe, and works out each step from the game's own recipes. It breaks off to
  fight anything that comes close and digs in for the night. `/bot auto early` stops after the work bench,
  a wooden sword and 30 wood.

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
| `/bot auto [iron|early]` | Auto mode, plan `iron` (default) or `early` (see above). Switches the bot on. `/bot stop` ends it; `P` pauses it, and pressing `P` again resumes it. |
| `/bot fight` | Fight nearby enemies until none has been close for 1.5 s. Switches the bot on. |
| `/bot stop` | Clear the target and leave auto mode. The bot stays on but idles. |
| `/bot on`, `/bot off` | Same as the keybind. |
| `/bot status` | One-line status in chat and in the log. |
| `/bot dump` | Writes a JSON snapshot (player, path, nearby tiles as ASCII, NPCs, items, inventory) to client.log. |
| `/bot trace [on\|off\|<seconds>]` | Logs every tick while walking (default 30 s). |
| `/bot dig [on\|off]` | Allow or forbid mining (default on). No argument toggles. |
| `/bot build [on\|off]` | Allow or forbid placing blocks (default on). No argument toggles. |

If `P` or `O` clashes with another mod, rebind them under **Settings → Controls → Keybindings → Mod Controls**.
While the bot is on, it owns your movement keys. The mouse button stays yours except while the bot is using an
item (mining, placing, chopping, swinging a sword); then it also switches your hotbar slot and points its own
"virtual mouse" for the swing. It uses items from the hotbar (slots 1–10): the best pickaxe, axe and melee
weapon there, and plain blocks such as dirt, stone or wood. It never spends ores, gems, sand or seeds. If a
better one of those is in your backpack (a freshly crafted sword lands there when the hotbar is full), it
swaps it onto the hotbar, into an empty slot or one holding none of its tools.

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
`Enchanting Outskirts` world; your real saves are only read. It plays 25 scenarios in an arena above the
spawn and prints PASS/FAIL for each:
- walking, ledge jump, gap jump, running jump over an 8-tile gap (and refusing one with no run-up room),
  dropping through platforms, a drop without fall damage, an unreachable target;
- tunnel, dig down, pillar, bridging a wide pit;
- sand in the path, a sand roof, a lava neighbour, running out of blocks, a wall appearing mid-walk;
- killing a green slime, two slimes on both sides, chopping trees;
- the auto chain from an empty inventory, with slimes arriving midway, and with a hotbar full of junk and the
  axe in the backpack.
- the iron chain from copper tools to an iron-tier pickaxe, and a night spent sealed in a hole while a zombie
  waits outside.

Then it quits. It takes about ten minutes. Keep the game window in front, because Terraria pauses while it's
in the background. Run a subset with `-Scenarios "name1,name2"`. Claude can run this on its own; the manual
scripts below are for things the self-test can't see, such as how the overlay looks or a real world's trees.

## Test script: iron pickaxe (Milestone 5)
Start a fresh character (copper tools) in a forest at daytime, with stone and ore within about 40 tiles below.
A normal world's surface is fine.

1. `/bot auto`. Expected: it chops trees for a work bench and sword as in Milestone 4. It kills slimes for gel
   (`Hunt` in the HUD) unless one already died to it, makes torches, mines 20 stone and places a furnace.
2. It mines 15 ore of whatever iron tier your world has (iron or lead), smelts 5 bars and places an anvil
   next to the bench and furnace. Then it mines 30 more ore and crafts the pickaxe.
3. The HUD ends at `auto: iron-age chain complete ...`, and `/bot status` shows `pick 40` (iron) or `pick 43`
   (lead).
4. At night: if it needs wood or gel, it digs a hole about five blocks deep, seals the top and waits
   (`Shelter [Waiting]`). In the morning it opens up and carries on. Mining and crafting continue at night.
5. Notes for me: where it got stuck, if anywhere, and the time. The log lines starting `Auto:` show every
   decision.

## Test script: auto mode (Milestone 4)
Start a fresh character (copper shortsword, pickaxe and axe) in a forest. Daytime is easiest.

1. `/bot auto`. Expected: it walks to the nearest tree, chops it from the base and walks around picking up the
   wood. The HUD shows `auto: GatherWood ...`.
2. With 10 wood it crafts a work bench (no menu opens; the log says `Craft done`) and places it beside itself,
   then crafts a wooden sword at it (`Craft start: item 24 ... @ Work Bench`).
3. It gathers until it carries 30 wood, then the HUD shows `auto: early-game chain complete ...` and it idles.
4. Danger: let a slime come near (or spawn one) while it chops. Expected: `Danger: pausing GatherWood to fight`,
   the slime dies, and the chain carries on where it left off.
5. Leave the area and come back, or break the bench: it remembers the bench while out of sight, and makes a new
   one if it sees the old one is gone.

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
- Every jump starts from a standstill on its start tile (running jumps sprint from there), so the bot pauses
  briefly before each jump.
- No ropes, doors or swimming physics. Water is only avoided by cost, and lava is never entered.
- Pillars can't break blocks above the head yet; it needs open sky or a separate tunnel step.
- Combat is melee only (swords and shortswords). No ranged or magic weapons, no dodging projectiles, no
  retreating. Auto mode stops after three failures in a row, e.g. when there are no trees to be found.
- Auto mode's goals end at an iron-tier pickaxe. No houses, armour, chests or bosses yet. It gathers only
  forest wood, and recipes with conditions (near water, a biome...) are left out.
- If your mouse cursor is over a UI element (inventory, a menu), the game blocks item use. Move the mouse away
  while the bot uses items.
- Slopes and half blocks are treated as full blocks.
- The bot pauses while you are dead, mounted, grappling, on a rope, or in reversed gravity.
- Single-player first. On a multiplayer client, unreceived world sections look like empty air.

See `PLAN.md` for the design and `CLAUDE.md` for developer notes.
