# Night log

Autonomous work while the user sleeps. The newest entries are at the bottom. Each entry gives what was observed,
what was decided and the result.

## 2026-10-13

### Testing infrastructure
- **Fast mode** (commits 8dc2616, fd4ce53): `selftest.ps1 -Fast -DrawEvery 16` runs the game at about 27–35× normal
  speed; settle_auto takes 14 s instead of about 6 min. Vanilla's `Main.superFast` alone did nothing (vsync is turned
  back on before every draw), so a `SetResolution` hook keeps vsync off.
- **Background running** (d52ca7e): a `Main.DoUpdate` hook tells the game it has focus, so runs no longer pause when
  the window is not in front. Tested by focusing Notepad mid-run: heartbeats kept coming at about 28×.

### world_eye run 12
- **Observed:** the bot reached the furnace and iron ore, then swung at a Red Slime 2.9 tiles away for over a whole
  game day without hitting it. The "no closer" give-up never fired because the slime was in reach, and ignored
  enemies within 3 tiles were still fought.
- **Decision:** after 300 ticks of in-reach swinging with no damage, the bot leaves the enemy and ignores it for 30 s,
  even at close range (`BotContext.IgnoreEvenClose`). The give-up logs a tile map so the cause can be found.
  Unit test `GivesUpOnEnemyItCannotHurtEvenInReach` added; 194 tests pass.
- **Regression found:** fight_two_slimes_both_sides failed, leaving the second slime alive. The bot had chased the
  first slime, and the 24-tile defend leash was measured from the bot, so the second slime was now out of range.
  This came from an earlier leash change, not tonight's fix.
- **Decision:** measure the defend leash from where the fight started. Unit test
  `DefendLeash_IsMeasuredFromWhereTheFightStarted` added (195 pass). In game, fight_green_slime,
  fight_two_slimes_both_sides, early_game_with_slimes and night_shelter all pass (4/4, 21 s at fast speed).

### world_eye run 13 (fast mode: 1 game hour in about 3 real minutes)
- **Progress:** work bench, furnace, 15 iron, anvil, then 24 of 30 iron for the pickaxe. Four deaths, all
  recovered: two cave bats and a red slime while it was digging in to recover, and one 42-tile fall. Timed out after
  1 game hour.
- **Fall:** it was digging down beside a deep one-tile shaft, standing on one column with its hitbox overlapping the
  support by a few px, and slid in. **Decision:** footing over a drop deeper than the safe fall (`Body.IsPrecarious`)
  costs +200 ticks in planning, so such spots are used only when there is no alternative.
- **Stall:** an ignored Yellow Slime sat at 2.9–3.0 tiles. Ignored enemies within 3 tiles count as danger, so a fight
  started, the slime drifted to 3.01 tiles and the fight ended, every 2 s for the rest of the hour. **Decision:**
  hysteresis. An ignored enemy starts a fight only within 2 tiles, and a fight keeps it out to 3.
- **Leash fix follow-up:** the fight anchor was read in Start(), before the body was refreshed (the self-test position
  was stale), so it is now set on the first tick.
- **Timeout:** world_eye raised to 4 game hours (about 8 real minutes in fast mode).
- **Tests:** FootingTests added (198 pass). In game, 19 movement and fight scenarios pass.

### world_eye run 14
- **Failure:** it stopped after about 4 game minutes. Mining iron "failed after 3 explorations", but every exploration
  had found ore. The counter counted all searches, not only fruitless ones. Three failed Mine jobs in a row then
  stopped auto mode for good.
- **Decision 1:** a successful mine resets the exploration counter, so only fruitless explorations in a row count.
- **Decision 2:** auto mode no longer quits after 3 failures. It rests 10 s, forgets what it had marked unreachable or
  failing (the world changes as it digs), and tries again. It stops for good only after 5 such rests with no success
  in between.

### world_eye run 15 (stopped after 2 game hours to test fixes)
- **Progress:** the furthest real-world run so far. Furnace, anvil, 30 iron, iron pickaxe, then a house site and
  furniture crafting. A chest gave a **Depth Meter**, which it put on, so looting and accessories work in game.
  3 deaths: a skeleton and poison spit about 200 tiles down, and a zombie at a water edge.
- **Stairway re-plans:** digging a staircase up through rock re-planned 26 times at about 1 s each. A step could
  stand on a block that an earlier step of the same plan digs away. **Decision:** A* looks 8 steps back along the
  path and does not count cells those steps break as support (`PathSearch.SupportOnlyFromBroken`).
- **Too deep:** **Decision:** a gear-based depth limit (`BotContext.DeepestRow`). With defense under 8, exploring and
  ore stay above the cavern layer (+15 rows). Under 16, at most 150 rows into the caverns. No limit beyond that.
- **Water edge:** at a pool the game swapped dry and wet physics every few ticks, and each swap meant a 1.4 s
  re-plan, so the bot stood still. **Decision:** keep the dry physics values while wet (water is costed, not modelled).
- **settle_auto regression:** standing in a hole just below the furnace, the planner thought it was in range and the
  game did not, so crafting failed over and over. **Decision:** `NearStation` now uses vanilla's AdjTiles box, and
  after a craft fails for lack of a station the bot walks right up to it first.
- **Flaky check:** the iron_age_auto pick-power check ran before the 30-tick hotbar restock. It now restocks first.
- **Tests:** 201 unit tests pass. In game, 21 movement, fight and crafting-chain scenarios pass.

### world_eye runs 16 and 17
- **Run 16:** got stuck exploring, with the same leg failing at once for 9 game minutes. A leg that fails at once did
  not count as a failure, so it retried for ever. **Decision:** after 10 instant failures in a row, exploring fails
  and logs a tile map, so the brain's rest-and-retry clears what boxes it in.
- **Run 17:** the anvil was placed by game minute 6, the fastest yet. Then:
  - **Bitten while ignoring the biter:** the "can't hit it" rule gave up on a red slime in water that kept biting
    the bot, and the bot chased another slime 20 tiles away. **Decision:** the context records the last NPC that
    hurt the bot (`NoteAttacker`). That clears any ignore on it, and the fight targets it first.
  - **Crowds:** most deaths happen while digging in at 30% life among 2–3 enemies. **Decision:** with 2+ hostiles
    within 8 tiles, retreat at 50%.
  - **Boxed in:** a diagonal dig down had not cleared the head-height cell in front. The body steps sideways at its
    old height before it drops, so the bot pressed into that block, then found no way on. **Decision:** the
    TunnelDown move also breaks that cell. The debug map now marks unbreakable cells 'X' and unsafe ones 'U'.
- **Tests:** 203 unit tests. In game, 23 regression scenarios pass.

### world_eye run 18
- **Progress:** iron pickaxe and house started. **Life crystals work end to end** (seen, mined, used: max life
  120). Two chests looted, one with **Healing Potion ×5**, Silver Bars, a Hunter Potion and an Inner Tube (worn).
- **But 9 deaths.** Causes:
  - Fighting cavern enemies (skeletons, mother slime, antlion chargers) with a Copper Shortsword at 100 HP.
  - A lava pool walked into while chasing.
  - Crafting a new work bench and furnace after every death: the station scan was still from where it died, so the
    stations at spawn counted as "gone".
- **Decisions:**
  - **Lava** is impassable for the planner (it was only "liquid", 3× cost).
  - **Depth limit:** with no armour, stay 10 rows above the caverns. Fights do not chase enemies more than 10 rows
    below the limit, unless they are attacking.
  - **Broadsword before pickaxe:** after the anvil, an iron-tier broadsword comes first in the boss and settle plans.
  - **Weapon choice** scores damage after half of a typical early enemy's defense (8). The Wooden Sword now beats the
    Copper Shortsword, which did 1–2 damage a hit to yellow slimes.
  - **Station memory:** stations count as gone only when the scan was made near them (scan origin recorded).
- **Tests:** 203 unit tests. In game, 25 regression scenarios pass, including eye_of_cthulhu.

### world_eye run 19
- **Stall:** the bot came to rest on a slope tile with air under both foot columns. The planner counted only blocks
  under the feet as support, so its own position was "InvalidStart" and every plan failed. All 5 rests happened on the
  same tile and auto mode stopped. **Decision:** a slope or half block in the feet row counts as support, since the
  player stands partly inside it. Also, each rest now starts with 45 ticks of hopping sideways, a different way each
  time, to get off whatever spot keeps failing.
- **Tests:** '/' in test grids now means a shaped tile. 204 unit tests. In game, 21 movement and chain scenarios pass.

### world_eye run 20
- **Failure:** walking back to the furnace failed over and over. Jumps ending with the feet inside a slope cell
  failed 3 times each, and the rest wiped the "never plan this step" notes, so it retried them.
- **Decisions:**
  - Jumps no longer target a landing with feet inside a slope (`JumpMove.LandsInASlope`); walking and step-ups still
    handle slopes.
  - The run 19 slope fix is narrowed to the start only (`Body.IsStartable`). As a general "standable" rule it created
    these bad jump targets.
  - Rests no longer clear the never-plan-again steps, which are facts about the world.
- **Tests:** 205 unit tests. In game, 19 + 11 regression scenarios pass.

### world_eye run 21, and a boss_auto regression
- **Run 21:** no deaths for the first game hour, a big improvement on earlier runs. It retreated at 37% life,
  quick-healed with a looted potion, and recovered in a dug-in shelter. Then it stalled, "chasing" a red slime 3.8
  tiles away for 7000+ ticks without moving. The no-closer give-up skipped anything within 6 tiles.
  **Decision:** that give-up now applies whenever the target is not in sword reach, and being in reach counts as
  progress.
- **Tried and reverted:** weighted A* for digging legs. It cut search nodes by only 2% (the heuristic is tiny going
  down), so it is not worth keeping.
- **boss_auto regression** (broken since tonight's earlier M9 habits):
  - The use-crystals-on-sight habit ate the life crystal meant for the heart lantern, so the bot explored 220 tiles for
    a new one. **Decision:** keep one crystal while the plan still needs a heart lantern.
  - The torch keeper lit the way with the arena's torches and gel. **Decision:** it pauses during builds and boss
    fights, and keeps 12 torches for crafting, while crafting up to 20.
  - **Result:** boss_auto **PASS**: the Eye was defeated, with 0 deaths.
- **Tests:** 206 unit tests. In game, fights, boss, building and crystal scenarios pass.

### world_eye run 22
- **Stall:** after a fight the bot was 57 tiles below and 37 tiles to the right of the furnace. Every search for a way
  back (40,000 nodes, about 3 s each) found nothing, over and over. **Decision:** after 2 failed walks to a known
  station, the planner forgets it and makes a new one where the bot is (stone first), like a player who can't find
  the way back. Test `StationItCannotGetBackTo_IsForgottenAndMadeAgain` added (207 unit tests).
- In game, iron_age_auto, settle_auto and boss_auto pass.

### world_eye run 23 (stopped at game hour 1 to test fixes)
- **Progress:** "no way back to the furnace" fired and it went on. Iron bars, anvil, then **Iron Broadsword
  crafted**. A chest gave 5 healing potions and an inner tube. 3 deaths (a cave bat, a zombie, poison spit).
- **Three stalls, and the decisions:**
  - Two steps from one tile took turns failing ("pressing right without moving"), so the "same step 3 times in a row"
    guard never fired. **Decision:** count failures per step over the whole walk.
  - 5 iron short, it explored the same 50 tiles for an hour. **Decision:** after a mining job finds nothing more,
    travel about 120 tiles along the surface (the other way next time) before mining again.
  - A life crystal 7 rows below the depth limit interrupted mining every second, since crystal mining refuses it.
    **Decision:** crystal spotting obeys the depth limit too.
- In game, 12 regression scenarios pass, including boss_auto and life_crystals.

### world_eye run 24: Iron Broadsword with 0 deaths, then the Iron Pickaxe
- **Progress:** best run so far. A chest gave an **Aglet** (worn), 4 Lesser Healing Potions, 50 arrows and 4 **Recall
  Potions**. It rebuilt the base where it was when it had no way back. **Iron Broadsword at game minute 20 with 0
  deaths**, then the **Iron Pickaxe** at about minute 33 (3 deaths by then: an antlion swarmer and devourers).
- **What went wrong, and the decisions:**
  - Chasing a jellyfish into water. **Decision:** enemies in liquid are not targets or danger unless they bite.
  - Rebuilt furnace and anvil twice, with 5 iron bars each time. One bad step counted as "no way back". **Decision:**
    walks that end on a banned step do not count. With a **Recall Potion** in the bag, drink it to get home instead of
    rebuilding (new GameIds.RecallPotion).
  - **"No building site within 40 tiles of the bench"** stopped auto mode: the new bench was underground, 120 tiles
    from spawn. **Decision:** a bench more than 60 tiles from the respawn point does not anchor the house; the spawn
    does. A stuck plan now rests and retries with its given-up sites forgotten, and stops only after 5 rests.
  - The iron_age_auto check restocks the hotbar several times (one swap per call).
- **Tests:** 208 unit tests. In game, iron_age_auto, boss_auto, settle_auto, build_house and the fight scenarios pass.

### world_eye run 25
- **Progress:** another chest gave a Leaf Wand, a Living Wood Wand, a Regeneration Potion and a Recall Potion. Iron
  for the broadsword was being mined when I stopped the run. 3 deaths, all around row 496, well below the depth limit
  of 441.
- **Why so deep:** level "digging on" tunnels at row 433 broke into caves and the bot dropped through, sinking leg by
  leg. Once below the limit, exploring looked only 10 rows up for frontiers and kept tunnelling at that depth.
  **Decision:** below the limit, exploring searches upward far enough and its fallback leg digs back up.
- **The same cave at (2125,339)** had no way back to spawn in three runs (40,000-node searches). **Decision:** a
  search that expands over 1000 nodes and finds nothing now writes the region to
  `tModLoader-Logs/TerraBot/nopath_*.txt`, so it can be replayed in a unit test.
- In game, iron_age_auto, settle_auto and life_crystals pass.

### world_eye run 26, and the cave with no way back (solved)
- **Progress:** "travel to new ground" fired ("no more Iron Ore found around here; travelling to new ground") and
  found iron there. **Iron Broadsword** again. 2 deaths (an antlion charger, a rolling cactus).
- **The cave at (2124,339)** (no way back in runs 22, 25, 26): replayed offline from the new region dump (test
  `DumpReplayTests.CaveBelowSpawn_HasAWayBackUp`). A plain search over the moves reached the surface, but A* stopped
  after 3362 nodes. **Cause:** my run 15 rule rejected any step held up only by cells its own path mines. That rule
  depends on the path a node was first reached by, and A* keeps one path per node, so it made reachable nodes dead.
  **Decision:** turn it into a +300 tick penalty instead of a ban. A* now finds a 73-step way home.
- **Also found:** the surface row is mostly unbreakable for the planner, because grass and flowers on a block count as
  furniture above it (never undermined). Fine for now.
- **Dump survey:** 9 dumps; 7 now find paths. The 2 that don't are targets under a lake, which is correct.
- **Tests:** 211 unit tests (dump replays included). In game, 19 movement and chain scenarios pass.

### world_eye run 27
- **Progress:** made it back from the deep mine to the original furnace (the penalty change at work), then anvil,
  travel to new ground, iron for the broadsword. 1 death.
- **Zigzag again:** a pure penalty brought back the run 15 staircase re-plans (2.8 s each). **Decision (hybrid):**
  support dug by the last 2 steps of the path bans the step; dug further back only costs +300. The cave replay still
  finds its path.
- **Short no-path loops** around (2199–2217, 425–435) failed in game but succeed in offline replays, so the dumps miss
  constraints. **Decision:** dumps now also mark open cells where no block may be placed (',') and list the
  never-plan-again steps in the region. The replay loader uses both.
- In game, 14 movement and chain scenarios pass.

### world_eye run 28: plan repair
- **Seen:** the walk home from the mine got there, but every few steps a step ahead became "no longer valid" and the
  whole 60-tile path was planned again (about 2.6 s and 35,000 nodes each time).
- **Decision: plan repair.** When a step ahead turns invalid, a small local search (up to 3000 nodes) finds a way to
  where the old plan carries on 3 steps later, and the rest of the plan is kept. Full re-planning happens only if
  that fails. In blocked_wall_appears the new wall is now handled by a 14-node repair, with no re-plan; the scenario
  check accepts either.
- In game, 20 movement, chain and fight scenarios pass.

### world_eye run 29
- **Progress:** no deaths for 20 game minutes, looted two chests, anvil placed, iron for the broadsword. Then
  antlions in the underground desert, around (2276–2317, 378–380), killed it twice. Antlions have killed it in nearly
  every run tonight.
- **Zigzag stairs:** still chosen at +300. The penalty for steps whose support an older step dug is now **+1500**. A
  ban reaching 4 steps back broke the cave replay, so the ban stays at 2 steps.
- **Decision: danger zones.** Where enemies (or their projectiles) killed the bot, exploring, ore, chests and travel
  targets within 30 tiles are avoided for 30 game minutes, like a player who stays away from where they keep dying.
- **Tests:** 211 unit tests. In game, iron_age_auto, settle_auto, life_crystals, boss_auto and 2 movement scenarios
  pass.

### world_eye run 30
- **Progress:** survived fights at 50% life (a Pinky, red slimes) and recovered. Stuck on the walk home again.
- **Cause:** three jumps on the way home, at the same places every run ((2141,368), (2130,365), (2114,359); up 2 and
  left 1, near slopes), land a tile short and time out after about 200 ticks. Each failed once per walk, and the count
  was per walk, so none was ever banned. Every failure cost a 2.8 s re-plan. **Decision:** step failure counts live
  on the bot's context, across walks.
- In game, 4 walk scenarios pass.

### Jumps through slopes (found from run 30)
- **Cause of the short landings:** a jump's swept cells only had to be "passable", and slopes and half blocks count
  as passable (Platform|Shaped), so jumps were planned through them. In the game they are solid from below.
  **Decision:** on a jump's way, a shaped cell is open only where the feet stand at take-off. Test
  `JumpThroughASlope_IsNotPlanned` added (212 unit tests).
- **Run 31 so far:** anvil at game minute 6 and the **Iron Broadsword at minute 14**, the fastest yet. 2 deaths: an
  antlion charger in the desert (now a danger zone) and a crawdad.

### world_eye run 32
- **Progress:** plan repair worked in a real world ("repaired with 5 steps to rejoin, 102 nodes"). The zigzag stair
  re-plans were still there, though (2.9 s each): a step up onto a stair block that a step 3–4 back in the same plan
  digs away. That's outside the 2-step ban, so it only cost +1500 and was still chosen.
- **Decision:** for step-ups, any earlier step of the path (8 back) digging the stair block bans the move. Other
  moves keep the 2-step ban plus penalty. The cave replay still finds its path.
- In game, 14 movement and chain scenarios pass.
