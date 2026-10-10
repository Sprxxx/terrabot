# TerraBot

A tModLoader mod with a bot that plays Terraria by pressing the same controls you do: left, right, jump, and
later use-item and hotbar. It never teleports, edits tiles or spawns items. Milestone 1: the bot walks to a
target tile across uneven ground, using walking, 1-tile step-ups, jumps up to 6 tiles high or about 5 tiles
across, and drops of up to 23 tiles.

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

If `P` or `O` clashes with another mod, rebind them under **Settings → Controls → Keybindings → Mod Controls**.
While the bot is on, it owns your movement keys. The mouse stays yours, so you can still place and break blocks.

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

## Test script
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

## Known limits (Milestone 1)
- Jumps are standing jumps only: the bot stops, aligns and jumps. Running jumps (longer gaps) come later.
- No digging, block placing, platform drop-through, ropes, doors or swimming physics. Water is only avoided by
  cost, and lava is never entered.
- Slopes and half blocks are treated as full blocks.
- The bot pauses while you are dead, mounted, grappling, on a rope, or in reversed gravity.
- Single-player first. On a multiplayer client, unreceived world sections look like empty air.

See `PLAN.md` for the design and `CLAUDE.md` for developer notes.
