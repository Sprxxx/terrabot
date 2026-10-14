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
- **Tried and reverted:** banning walks too (all 8 steps back, then 4) removes the cave's only way out. **Known issue:**
  zigzag staircases still re-plan on walks back. The real fix is a planner that tracks its own digging (A* states
  with the cells mined so far), which is a bigger job left for later. In run 33 the bot still got home, just slowly.

### world_eye run 33: Iron Broadsword with 0 deaths at game minute 32
- **Progress:** no deaths at all in the first 44 game minutes, the safest run so far. It walked home despite the
  zigzag re-plans, travelled to new ground twice, and got the **Iron Broadsword at minute 32**.
- **Known issue:** the underground desert is almost all 'U' (unsafe to break), because the planner never breaks a
  block with sand on top. Explore legs into it fail, and exploring moves on.
- **Travel bounced** between the same two places (+120, then −120 from the bot's position). **Decision:** travel
  goes out from the spawn and farther each time (east 120, west 120, east 240...).

### world_eye run 34: Iron Pickaxe and a house started
- **Progress:** broadsword, then 30 iron ore smelted into the **Iron Pickaxe**, then a house site by the spawn and
  the wood crafting for it, all in the first night. 2 deaths, both at night on the surface.
- **Decision:** at night with no armour the bot goes home and waits for the morning ("stay home"), instead of doing
  surface work. The house stage moves before the anvil (except in the Iron plan), so a shelter exists early.
  Run 34 was stopped to test this.

### Staying home: doors and the goal (from the boss_auto regression)
- **Closed doors were walls to the planner.** In the game a door opens by itself when the player walks into it at
  floor level, if the "auto-open doors" setting is on for everything (it is). **Decision:** with that setting on,
  closed doors count as open space on the bot's map.
- **The walk home aimed at the housing check's tile,** 2 rows above the floor, so it had no end. **Decision:** the
  goal is the floor inside the door.
- **Getting home failed again and again until the brain gave up.** **Decision:** after a failure, no new try for a
  game hour; the bot shelters where it is.
- **Armour now matters:** only a bot with defense below 6 stays home. The boss plan (iron armour, 200 life) sat in
  the house all night instead of getting its last wood.
- New in-game scenario night_home (passes). boss_auto, night_shelter, build_house and settle_auto pass. 214 unit tests.

### world_eye run 35: house on day one, the first night spent at home
- **Progress:** house built at game minute 2 (before the anvil), anvil by the first evening. At nightfall the bot
  walked into the house and waited, as planned. It looted a chest: 5 healing potions, an inner tube, silver bars.
- **Problem 1, leaving the house to fight:** zombies outside showed up as danger, so the bot went out three times;
  the third time a zombie killed it (the only death). **Decision:** once inside, enemies are only fought if one has
  actually hit the bot, and there's no retreat either (the house is the retreat).
- **Problem 2, climbing out of the mine (40 re-plans of 3 s each, 1 stair step each):** the 40,000-node searches ran
  out, and a partial plan ends at the node closest to the goal, whatever its cost. So the "stands on a block its own
  path digs away" penalty never kept those steps out: each partial plan failed after one step. **Decision:** a
  partial plan never ends past such a step. Test `PartialPlanThroughRock_NeverStandsOnWhatItDigs` fails without
  this, passes with it (215 unit tests).
- Run 35 was stopped at game day 2 (11 more iron ore to find) to test these.
- **Regressions (the boss scenario, twice):** (1) with iron armour, it dug in for the whole night over 1 wood.
  **Decision:** at defense 6+ there's no night shelter either (a player in iron armour fights the night). (2) Every
  arrow shot at a demon eye topped the 500 up again, until the wood was gone. **Decision:** stocks like arrows refill
  only below 80% once full. boss_auto passes again (Eye defeated). In game, 9 walk and dig scenarios, night_home,
  night_shelter and iron_age_auto pass. 217 unit tests.

### world_eye run 36: Iron Broadsword and Iron Pickaxe by the second night
- **Progress:** house and anvil on day one, the first night at home with no trips outside, the **Iron Broadsword on
  day 2** and the **Iron Pickaxe on the second night**. Then mining 45 iron for armour.
- **5 deaths,** 4 of them in the caverns (rows 452-471, depth limit 441): skeletons, crawdads, a spitter. The causes:
  - Walks home from mining at the limit went through the open caverns below it, the cheapest route. **Decision:**
    path edges below the depth limit cost 25 ticks more (the move context follows the gear).
  - Just below the limit, "dig back up to the limit" was within the walk's 3-tile tolerance: 14 legs in a row ended
    at once, the iron search gave up, and the bot travelled off through the caverns. **Decision:** dig a full 20 up,
    and a leg that goes nowhere counts as failed (the next leg turns). Test
    `JustBelowTheDepthLimit_DigsBackUp_InsteadOfGoingNowhere` failed on the old code (16 legs in 236 ticks).
- 1 death to a vulture in the desert while travelling east for ore.
- The PLAN now has an M9 section and the milestones after the Eye, in order (M10 Brain of Cthulhu through M18 Moon
  Lord).
- In game, 7 scenarios pass (early_game, iron_age, settle, night_home, life_crystals, tunnel, dig-down). 219 unit tests.

### world_eye run 37: one fight for 7 game days
- **Progress:** the anvil after 30 real seconds, the fastest yet, and 0 deaths. Then it never got past "19 more
  iron ore".
- **Cause:** two red slimes out of reach above took turns as the fight's target every tick. Each switch reset the
  "no closer for 600 ticks, give up" count, so the fight never ended. **Decision:** the count is per enemy. The new
  test `TwoUnreachableEnemies_TakingTurnsAsTarget_AreStillGivenUp` is still running after 3600 ticks on the old code
  and gives up at tick 1294 on the new.
- **Missed for 30 minutes:** the monitor filter dropped the status lines. **Decision:** real-world runs log
  "SELFTEST stall" when the plan status hasn't changed for 3 game hours (later 6).
- **Flaky unit test:** a static switch (`CheckBrokenSupport`) raced with parallel tests. It's now per search.
- In game, 5 fight scenarios pass, including boss_auto. 220 unit tests.

### world_eye run 38: broadsword and pickaxe by game day 3, then the iron ran out nearby
- **Progress:** the house, then the Iron Broadsword and the Iron Pickaxe; 29 of the 45 iron for armour. Looted the
  chest at row 463 (5 healing potions). 3 deaths.
- **No way home from the cave below the spawn (the runs 22-26 cave):** with every block held back for building, the
  only way up stands on blocks the path itself digs away, and the broken-support check rules those steps out, so
  three 40,000-node searches found nothing. The bot made a new bench, furnace and anvil down there and carried on.
  **Decision:** after a no-path, WalkTo plans once more without the check (such steps just cost a re-plan when
  reached). Replay test `CaveBelowSpawn_Run38_NoBlocks_OnlyTheRelaxedSearchGetsOut`.
- **Two deaths on the same tile, nothing attacking:** touch damage ("other" 3: thorns, cactus, spikes; Crimson thorns
  here), read as open space. **Decision:** a Hazard cell kind (TileID.Sets.TouchDamageImmediate), not passable and
  not stood on. Deaths by lava, drowning and tiles now mark danger zones too (not falls).
- **The stall warning** fired during nights at home. **Decision:** waits at home or in a shelter don't count.
- **Next:** iron is scarce near the spawn after 114 ore (anvil, sword, pickaxe, armour). A player would also take
  lead, silver or tungsten armour, or go farther; the plan only knows iron-tier armour. To do if it keeps stalling.
- In game, 12 walk, dig and chain scenarios pass. 222 unit tests.

### Iron runs out: silver tier as a second choice (from runs 38 and 39)
- **Run 39** stalled for 12 game hours on the last 8 iron for the broadsword, with deaths in the caverns. Its log was
  lost: the next game launch overwrites client.log, and I now copy real-world logs before starting another game.
- **Decision:** the broadsword, pickaxe and each armour piece take the iron tier or the silver tier (Silver or
  Tungsten, per world). An ore that mining keeps coming back without costs more in the planner each time, so it
  switches when iron runs out. Test `Armour_SwitchesToSilver_WhenIronKeepsRunningOut`.
- **The chest at row 463** (22 rows into the caverns) drew the bot down in four runs, and most deaths were around
  it. **Decision:** chests more than 15 rows below the depth limit wait for armour (the limit goes down with it).
- **A mining livelock in settle_auto:** the walk said "arrived" (its search started on a node beside the bot) but the
  ore was out of reach from where the bot stood, round and round for 50,000 ticks. Intermittent, but it hadn't been
  seen before. **Decision:** 3 such arrivals give up the tile.
- In game, iron_age_auto, boss_auto and settle_auto pass. 224 unit tests.

### world_eye run 40: the fastest yet, then six deaths in the underground desert
- **Progress:** the anvil about a minute in, the **Iron Broadsword at about 2 real minutes**, the **Iron Pickaxe at
  about 3**. The silver switch worked: iron came up empty, it explored for silver, then went back to iron when it found
  more. It got to 39 of the 45 iron for armour.
- **Deaths:** 6, all in the underground desert east of the spawn (x 2230-2350): rolling cacti (the "other 3" touch
  damage of runs 38-40, tile 484; 80 when one rolls), antlion chargers and swarmers.
- **Decision:** seen chunks below the surface that are mostly sand, hardened sand, sandstone or fossil count as the
  underground desert. It's off limits until defense 16: no ore, exploring or chests there, paths through cost more,
  and explore legs dug toward it turn round. Death spots now also make paths cost more (before, only targets avoided
  them). Hurt by a tile, the log now names the tile.
- In game, 9 scenarios pass (walk, dig, fight, night_home, iron_age, settle, boss_auto). 226 unit tests.

### world_eye run 41 (desert off limits)
- **Progress:** the anvil, then a recall potion home from 168 tiles away, the Iron Broadsword, the Iron Pickaxe.
- **Death 1:** walking home at dusk, the brain stopped to fight zombies (copper sword), lost, and dug in among them.
  **Decision:** on the way home for the night the bot keeps walking (zombies are slower and can't open doors), and
  the low-life dig-in doesn't interrupt it either (going home is the retreat). Test
  `WalkingHomeAtNight_DoesNotStopToFight` (fails on the old brain). The fake game's doors are now open space for
  walking too, as in the game since the door fix.
- **Known issue, open:** after a death, the first walk home from the spawn plans a pillar beside the work bench at
  (2091,281) that never places its block (seen in runs 36, 38, 41). It's banned after 3 failures, so it costs one
  night outside per run. Cause not found yet.
- **Later in run 41:** the Iron Pickaxe, then armour from **silver** (iron came up empty: 55 of 60 silver ore). 5
  deaths: 2 zombies on the surface, a ghost, and 2 in the desert band.
- **The spawn pillar, found:** vanilla only places a block next to a solid tile (or a beam, rope or wall). A work
  bench is only solid on top, so a pillar from on top of it never places. **Decision:** a pillar that fails counts
  as a failed placement at its cell (twice, and the planner routes round it), as other placements already did.
- **A desert death the detection missed:** one 16x16 chunk at 40% missed the desert's edges. **Decision:** the 48x48
  around a tile, at 25% desert tiles among the seen solid ones.
- In game, 7 scenarios pass (pillar, out of blocks, blocked wall, night_home, iron_age, settle, boss_auto). 227 unit tests.

### world_eye run 42: the broadsword at 90 real seconds, then a mining loop
- **Progress:** the anvil about a minute in, then a recall potion home and the **Iron Broadsword about 90 seconds
  in**, the fastest yet. 21 of the 30 iron for the pickaxe.
- **Loop for 18 game hours:** 10 rows below the depth limit, on a ledge over a pool, the dig-up leg had no path. A
  fight every few seconds (skeletons and slimes around) paused mining, and the planner started it afresh each time,
  with its count of fruitless explorations back at 0 and the same failing first leg. **Decision:** (1) the count is
  kept on the context per ore, so it reaches the give-up and the travel; (2) a leg with no path marks its target
  unreachable, and the next leg tries the other way, then level either way. Test
  `FruitlessExplorations_AreCountedAcrossRestarts`. 228 unit tests.
- **settle_auto regression:** the bot climbed out of the arena and mined the stone row it stood on, tile by tile,
  placing a block each time to keep its footing (1,258 planned placements), so the stone count never rose.
  **Decision:** tiles under the feet are mining's last choice. settle_auto passes again (21,919 ticks), as do
  iron_age_auto, early_game_auto, life_crystals and boss_auto.

### Retreat home rather than into a hole (from runs 40-43)
- 10 of the 13 deaths in runs 40-43 came while digging in to recover ("Shelter [Digging]"): slimes, zombies and a
  devourer kept hitting the bot as it dug. Run 43 also lost the bot to a vulture on the desert's surface.
- **Decision:** badly hurt with the house within 60 tiles (and getting home hasn't just failed), the retreat walks
  home and waits inside until 80% life. The brain doesn't stop that walk to fight. Elsewhere it still digs in.
  Test `BadlyHurt_NearTheHouse_RetreatsHome_NotIntoAHole`. 229 unit tests.
- In game, 5 scenarios pass (fights, night_shelter, night_home, settle, boss_auto).

### world_eye run 44: zigzag staircases again, in full plans
- Walking back to the furnace from a mine, the full (Found) plans had steps onto blocks an earlier step digs away:
  allowed at +1500, and still the cheapest. 10+ re-plans of 2 s each, the plan invalid 2-4 steps in.
- **Decision:** the normal search bans every step held up only by what its own path digs (8 steps back), not just
  step-ups and the last 2 steps. When that leaves no path at all (the cave below the spawn), WalkTo plans again
  without the ban (penalty only), as added for run 38. The cave replay: strict no path, relaxed found (73 steps).
- In game, 13 walk, dig and chain scenarios pass. 229 unit tests.

### world_eye run 45: anvil at 75 seconds, broadsword soon after; two deaths
- **Death 1:** cave bats bit the bot during a sideways explore at row 432 and flew off down. The fight's "what bit
  us goes first" rule picked them at any depth (the depth filter didn't apply to it), so it chased them into the
  caverns (row 469) and a skeleton killed it. **Decision:** the depth rule applies to attackers too: below the limit,
  one is only fought within 4 tiles. The chase margin is now 5 rows (was 10). Test
  `AnAttackerBelowTheDepthLimit_IsNotChasedDown`.
- **Death 2:** a vulture, digging in on the surface 76 tiles from the house. **Decision:** the walk-home retreat
  applies up to 100 tiles.
- In game, 7 fight and chain scenarios pass. 230 unit tests.

### world_eye run 46: two pathfinding and home-walk fixes
- **"No path" from the relaxed retry too,** after 40,000 nodes each, so the bot made a new furnace underground. In
  the relaxed search a step onto what the path digs is allowed, but it taints the nodes after it. A partial plan
  never ends at a tainted node, so when every node that got closer was tainted, the result was "no path".
  **Decision:** in that case the partial plan ends at the best tainted node.
- **Night walks home:** zombies killed the bot walking home 160 tiles across the surface without fighting back, and
  twice more just below the spawn. **Decisions:** at nightfall only go home from within 150 tiles, else shelter
  where it is. On the way home, ignore only enemies that aren't hitting it, and only within 100 tiles of the house.
  A hurt retreat home keeps running even when hit (fighting would hand it straight back to the retreat, every tick).
- In game, 8 scenarios pass (night_home, night_shelter, fights, walks, iron_age, settle, boss_auto). 231 unit tests.

### world_eye run 47: anvil at 27 seconds, a Silver Broadsword, the pickaxe at 3 minutes
- **Progress:** the anvil 27 real seconds in. Iron came up empty, so the sword was a **Silver Broadsword** (better
  than iron), then a recall potion home from 147 tiles and the **Iron Pickaxe**.
- **Deaths:** a skeleton at row 449, 8 rows under the limit (mining at the limit puts it in reach of the caverns), and
  an eater of souls west of the spawn (this world has corruption too).
- **Decisions:** without armour the depth limit is 20 rows above the caverns (was 10). The Corruption and Crimson
  (seen stone, grass, sand and ice) are off limits like the desert, until defense 8 (an iron or silver set). Test
  `TheEvilBiome_IsOffLimits_UntilThereIsArmour`. 232 unit tests.
- **Later in run 47: the first armour in a real world,** an Iron Helmet, crafted and worn about 6 real minutes in.
  Also a death to an antlion charger at row 498, and a travel target 1,900 tiles away. The cause was my run-42 fix:
  the count of fruitless explorations survived a give-up, so every later mining job failed at once, each counting as
  another fruitless mine, and the travel went 120 tiles further each time. **Decisions:** the count is cleared when
  mining gives up, and travel for ore goes at most 600 tiles from the spawn.
- In game, 5 chain scenarios pass (iron_age, settle, night_home, boss_auto, life_crystals). 232 unit tests.

### world_eye run 48: 0 deaths, but short of ore
- **Progress:** the anvil about 2 minutes 20 seconds in, a recall potion home, and the **Silver Broadsword at about 3
  minutes**, with 0 deaths over a whole day and night (the safest run so far).
- **Problem:** at 20 rows above the caverns, the iron and silver of that band near the spawn ran out. The bot
  travelled back and forth between the same three spots all night, also across the surface at night.
- **Decisions:** with an iron-tier sword or better (10+ damage) but no armour, the limit is 10 rows above the
  caverns again (20 with a copper sword). Travel for ore counts as surface work, so in no armour it waits for the
  morning at home.
- In game, 5 chain scenarios pass. 232 unit tests.

### world_eye run 49: anvil at 15 seconds, broadsword at about 65
- **Progress:** the anvil 15 real seconds in and the **Iron Broadsword at about 65 seconds**, both the fastest yet.
- **Drowned:** the surfacing skill's walk to air 9 tiles off (placing blocks under water) used all 900 ticks, and the
  pillar-up fallback never ran. **Decision:** after 3 s, or below 35% breath, the walk gives way to pillaring up.
- **Known gap, open:** no way out of a pocket whose only way up is straight up through the ceiling (beside an old
  minecart track: its row can't be placed on, and the row under it can't be broken). The planner has no "mine above
  the head and pillar" move, only diagonal climbs. The fallback (new stations where it is) keeps the run going. Dump
  kept as Dumps/nopath_2114_359_to_2080_281.txt for when that move is added.
- **Later in run 49:** the **Silver Pickaxe**, two chests looted (9 recall potions, 4 lesser healing potions, an
  Aglet and a Step Stool, both worn), and the **Iron Helmet**, worn. Deaths: a yellow slime, a devourer, and
  **Explosives** (542 damage: the bot mined an explosives tile while exploring).
- **Decisions:** explosives tiles are never mined. **Digging straight up:** a pillar now mines the two cells above
  the head first (standing), then jumps and places. A dug feet cell counts as open (as DigMove's in-body cells), and
  pillars are exempt from the broken-support ban (they stand on the block they place). The run-49 pocket replay now
  finds a 153-step way home; simulator test `Sim_DigsUpThroughACeiling` (6 mined, 5 placed). 234 unit tests.
- Run 49 ended at 5 deaths: drowned, a yellow slime, two devourers on the corruption's edge west of the spawn, and
  explosives. **Decision:** in weak gear, an eater of souls, devourer, crimera, face monster or blood crawler in
  sight marks a danger zone there (the biome is near), as a player knows a biome by its monsters. Test
  `AnEvilBiomeMonsterInSight_MarksADangerZone_InWeakGear`. In game, 13 walk, dig and chain scenarios pass with the
  dig-up pillars. 235 unit tests.

### world_eye run 50: the fastest yet (helmet at about 3 minutes)
- **Progress:** the anvil about 35 seconds in, the **Silver Broadsword at about 90 seconds**, the **Iron Pickaxe at
  2 minutes**, and the **Iron Helmet worn at about 3 minutes**, with 0 deaths until then. The evil-biome monster
  sighting worked (devourers marked west of the spawn).
- **Deaths after that:** devourers twice (dug in beside one, then back after the zone lapsed), a spore bat and a
  yellow slime, all while digging in to recover, and the house walk failed once.
- **Decisions:**
  - A worm (devourer, giant worm) burrows: with one near, the hurt retreat goes home at any distance, or fights
    on if there's no house, never into a hole.
  - With something on it, a hurt dig-in first runs about 25 tiles away (up to 6 s; the bot outruns slimes and
    zombies), then digs.
  - Zones from monster sightings last 5 game days (biomes don't move); death zones still 1.25.
- In game, 7 fight and retreat scenarios pass. 235 unit tests.

### world_eye run 51
- **Progress:** the anvil a minute in, the Iron Broadsword, then iron and silver for the pickaxe.
- **Problems:**
  - A step-up onto a block with a slope on top: more than vanilla's one-tile step, pressing right three times.
    **Decision:** a step-up needs the cell above the step fully open. Test `AStepWithASlopeOnTop_IsNotAStepUp`.
  - A trip to new ground went through the caverns (rows 452-471), where a skeleton killed the bot, and across the
    underground desert (an antlion charger). The path penalties (+25 below the limit, +40 off limits) were cheaper
    than digging. **Decision:** +120 below the depth limit, +150 in off-limits areas.
  - A jellyfish while surfacing.
- In game, 11 walk and chain scenarios pass. 236 unit tests.

### world_eye run 52: auto mode switched itself off
- **Progress:** the anvil about 20 seconds in, the **Silver Broadsword at about 3 minutes**.
- **Deaths:** a zombie just below the house on the way home, and a drowning during a mine with no surfacing at all.
- **The end:** shut into a tiny pocket (a 14-node search) in the underground desert, probably by sand it won't mine
  under. After 5 rests auto mode stopped, at 7 real minutes.
- **Decisions:**
  - Before switching off, once: dig anyway (under sand, beside water; never beside lava, +300 a block), and start
    the rests over. Cleared when a skill succeeds. Test `DigAnyway_BreaksUnderSand_ButNeverBesideLava`.
  - A death on the way home is not "cannot get home". It had sheltered outside all night after the respawn, a few
    steps from the house. Test `DyingOnTheWayHome_IsNotCannotGetHome`.
  - Breath falling without the head cells reading as liquid is now logged, to find the cause of that drowning.
- 238 unit tests.
- **Regression findings:** "dig anyway" leaked from boss_auto into the next scenario (the bot's context carries over
  between self-test scenarios) and let a walk test undermine sand. **Decision:** reset on SetAuto and before every
  scenario. boss_auto ran 4 wood short again: torches made from the plan's wood. **Decision:** LightKeeper leaves the
  last 15 wood alone. In game, sand, lava, early_game, iron_age, settle, night_home and boss_auto pass. 238 unit tests.

### world_eye run 53: broadsword at 40 seconds, helmet at 3.5 minutes
- **Progress:** the anvil about 17 seconds in, the **Iron Broadsword at about 40 seconds** (the fastest yet), the
  **Silver Pickaxe at about 2 minutes**, and the **Iron Helmet worn at about 3.5 minutes**, with 0 deaths until then.
- **Deaths:** a poison spit at row 457 (a trip to new ground from underground went down through the caverns), and a
  rolling cactus spike in the underground desert, which exploring entered before seeing it as desert.
- **Decisions:**
  - From more than 15 rows underground, travel for ore first walks up to the surface above, then goes along it.
  - Desert monsters in sight (antlions, tomb crawlers) mark 5-day danger zones until defense 16, as evil-biome
    monsters do until defense 8.
- In game, 5 chain scenarios pass. 238 unit tests.

### world_eye run 54: anvil at 15 s, broadsword at 31 s, pickaxe at 58 s, helmet at 4 minutes
- **Progress:** all the fastest yet: the anvil about 15 seconds in, the **Iron Broadsword at about 31 seconds**, the
  **Iron Pickaxe at about 58 seconds**, the **Iron Helmet worn at about 4 minutes**. Desert sightings worked (antlions
  marked at x 2238-2298).
- **Death:** an antlion charger. The hurt run-off went right, away from one enemy and straight into the desert zones
  just marked. **Decision:** the run-off goes the other way when its side is off limits.
- **Surfacing loop:** "heading for air at (2010,411)" walked to a plain tile, which WalkTo reads as "stand on the
  ground below it", here the bot's own spot under water, so it "arrived" every tick. **Decision:** the walk goes to the
  air node itself. The breath diagnostic now warns only while breath falls (it fired while breath came back).
- **Known issue, open:** the way up from that spot was through the same flooded passage, so walk up, surface, walk up
  again. A player would swim up; the bot can't swim well without flippers.
- In game, 4 fight, retreat and chain scenarios pass. 238 unit tests.

### world_eye run 55: helmet and chainmail, 0 deaths for 8 minutes
- **Progress:**
  - The anvil about 15 seconds in, the **Silver Broadsword at about 33 seconds**, the **Silver Pickaxe at about 2
    minutes**.
  - The **Iron Helmet at about 3:50**, then the **Iron Chainmail at about 8 minutes**: the first chainmail in a real
    world.
  - Three chests looted (recall potions, healing and regeneration potions, an Aglet, a Step Stool, throwing knives).
  - 0 deaths for the first 8 minutes, then 1 (a yellow slime at row 452).
- **The end:** boxed into a 2-node pocket at the desert's west edge, probably by fossil or sandstone the silver
  pickaxe can't mine. Dig-anyway didn't help, and auto mode stopped at about 10 minutes.
- **Decisions:**
  - Desert and evil biome detection counts the 80x80 around a tile at 15% (was 48x48 at 25%), so the edge where
    most desert deaths and this trap were counts too.
  - Region dumps are also written for tiny searches (20 nodes or fewer), to see the next pocket.
  - An open door reads as the closed door: the house blueprint thought its door was missing and a new one was
    crafted and placed every night (runs 51 and 55).
- **Known issue, open:** a bridge step near the house that never places its block (likely the vanilla anchor rule).
- In game, 6 house and chain scenarios pass. 238 unit tests.

### world_eye run 56: the spawn sits between a corruption and a desert
- Two chests (9 recall potions, healing potions, an Aglet, a Step Stool), but no anvil: the iron near the spawn ran
  out, and every trip to new ground landed in the desert (a demon eye on the way, then a rolling cactus) or toward
  the corruption.
- **Decision:** a trip to new ground looks at the seen ground under its destination (13 wide, 8 deep). If it's largely
  desert or evil-biome tiles in too weak gear, it tries the other side, then farther. In game, iron_age, settle and
  boss_auto pass. 238 unit tests.

### world_eye run 57: a slime fight for 42 game hours
- **Progress:** the anvil about 47 seconds in, the **Silver Broadsword at about 2.5 minutes**, 0 deaths, then 20 of 30
  iron for the pickaxe.
- **Stall:** two slimes 2.7 tiles off behind a ledge. They took turns as the target and hopped in and out of reach, so
  neither the per-enemy no-closer rule nor the no-damage rule ever fired. The approach walk "arrived" about 1,500
  times on the bot's own spot.
- **Decision:** a whole-fight backstop: if no enemy loses any life for 1,200 ticks, the fight ends and the enemies
  around are ignored for a while, even close by.
- In game, 6 fight and chain scenarios pass. 238 unit tests.

### world_eye run 58
- **Progress:** the fight backstop worked ("no enemy hurt in 1200 ticks"). The anvil at about 3 minutes, a chest (3
  recall potions), the **Silver Broadsword at about 5 minutes**.
- **Deaths (4):** a zombie far east at night, a yellow slime, an antlion charger at x 2192 (30 tiles past the desert
  zones), and a salamander at row 460.
- **Decisions:** monster-sighting zones cover 60 tiles round (death zones 30). With a good sword but no armour, the
  depth limit is 15 rows above the caverns (was 10). In game, iron_age, settle, boss_auto and life_crystals pass.
  238 unit tests.

## Run 59 (commit 182d37d), ~8 game min, stopped for fixes
- Anvil about 1 min, Iron Broadsword and Iron Pickaxe about 4–6 min, **Iron Helmet worn at about 8 min**. 3 deaths.
- Deaths 1 and 2: in the underground desert east of the spawn (a Giant Antlion Charger at x 2258, then an Antlion Charger plus rolling cacti at x 2400). Explore legs kept going through the desert:
  - **Cause A:** BotContext cached chunk counts for a game minute, including chunks counted from far off (by a path search) before the bot had seen them. The desert read as "nothing seen" while the bot was inside it.
    - **Fix:** a chunk not yet fully seen is recounted after 60 ticks.
  - **Cause B:** when every fallback leg was off limits, ExploreSkill went ahead with the off-limits leg anyway.
    - **Fix:** Explore now fails ("every way from here is off limits").
    - **Fix:** MineSkill no longer explores when the bot itself stands off limits. It fails with an "explorations" reason, so the planner travels to new ground.
- Death 3: a cave bat during a shelter flight (not addressed yet).
- Breath mystery found: vanilla's drown box (Collision.DrownCollision) is 10×12 px starting 2 px above the hitbox top. When the bot stands a few px low (on a slope), the box reaches water in the row below the node's head cells.
  - **Fix:** SurfaceSkill.HeadUnder now mirrors that box on the real hitbox.
- 3 new tests (241 pass). Regressions iron_age_auto, settle_auto and life_crystals: all pass.

## Run 60 (commit 0d3a121), stopped after 3 deaths in about 2 game minutes
- The new give-up worked ("every way from here is off limits", then mining failed and the bot travelled). But the bot still died:
  - Two antlion chargers on the desert's underground edge, by the spawn.
  - A vulture on the surface desert.
- **Causes:**
  - Every mining job (a new one after each fight) explored right first, which is east, into the desert.
  - The trip for new ore went to (2215,279), on the surface right above the underground desert's edge.
- **Fixes:**
  - The first exploration of a mining job goes the safer side (BotContext.SaferSide: fewer known off-limits spots out to 60 tiles ahead and below). With neither side worse, it alternates across jobs.
  - TravelForOre skips spots with known off-limits ground below them (OffLimitsBelow: 30 tiles either side, down to 100 rows).
- 1 new test (242 pass). iron_age_auto and settle_auto pass.

## Run 61 (commit 8f0a515), stopped after about 4 game minutes and 5 deaths
- Deaths:
  - A crowd of 8 slimes at row 420, with only a copper shortsword.
  - Antlion charger, umbrella slime and demon eye, all in the desert or at its edge.
- **Thrash:** the trip for new ore, (2455,208) east or (1975,280) west, was stopped by a fight in the surface desert. The mining job that followed failed at once (off limits). Each failure counted as fruitless, so the next trip went the other way, back and forth over the desert.
- **Fixes:**
  - ProgressionPlanner remembers the trip's destination, shared across ores, and goes on to it after an interruption until the bot is within 8 tiles. It is dropped when the walk succeeds or fails with a reason, or when the destination becomes off limits.
  - "Up to the surface before travelling" now goes back to the spawn instead when the bot stands off limits, or when the surface above is biome ground or has off-limits ground below.
- The test fixture ProgressionTests.Mine gained a `sky` height (travel looks for the surface from row 40 down). 1 new test (243 pass). iron_age_auto and settle_auto pass.

## Run 62 (commit d80a190), about 8 game minutes, **0 deaths**, stopped for a fix
- Anvil about 40 s, Iron Broadsword about 1.5 min, Iron Pickaxe about 2 min. A chest looted. No deaths: the desert changes worked.
- **Stuck instead:** from about game minute 5, in shafts 80–120 rows down, the walks to the surface kept failing on the same steps.
  - Pillars "placed" a block and still landed back on the start.
  - Walks pressed into cells the grid reads as open.
  - This brought on five rests of "3 failures in a row", with 3 recall potions carried.
- **Decisions:**
  - Escape like a player: after walks fail in a row more than 50 tiles from the spawn, the planner drinks a recall potion next (ProgressionPlanner.RequestRecall).
  - Diagnostics for next time:
    - A pillar's off-path landing now logs the left px, the start px, the feet and below cell kinds, and the feet px.
    - A banned step logs the tile types in the body at the target.
  - The cause of the pillar failures is still unknown.
- 1 new test (244 pass). settle_auto and pillar_up_cliff pass.

## Run 63 (commit 669cf21), stopped after about 3 game minutes (2 deaths)
- Anvil about 15 s, Iron Broadsword about 80 s.
- **Death 1:** 7 slimes in the cave at row 428 (the same cave as run 61's crowd). The bot retreated at 48% (the crowd rule), was hit four more times while fleeing, and a bat killed it while it dug in.
- **Death 2:** at first light the StayHome night ended. The bot walked out at once among 6 zombies still at the door and was killed just below the house.
- **Fixes:**
  - StayHomeSkill: in the morning it waits inside while hostiles are within 25 tiles, for up to 3 game hours.
  - Retreat at 70% with 4 or more hostiles within 8 tiles (MobRetreatBelow).
  - BotBrain.NoteCrowd: 5 or more hostiles within 20 tiles, underground, in no-armour gear (defense < 8), marks a danger zone of radius 25 for the death-zone time, so mining and exploring stay away from that cave.
- 2 new tests (246 pass). night_home, early_game_with_slimes, fight_two_slimes_both_sides and settle_auto pass.

## Run 64 (commit aa781de), stopped after about 4 game minutes (2 deaths)
- Iron Broadsword and Iron Pickaxe at about 3 min. Recall potions were drunk to get home to the stations.
- The crowd zones fired ("8 hostiles about at (2104,408)"; "5 at (2038,417)").
- **Death 1:** the walk "back to the spawn" from row 408 followed caves east into the desert, and antlion swarmers killed the bot.
  - **Fix (commit 4500e64):** a trip for new ore starts with a recall potion when the bot is more than 50 tiles from the spawn and carries one.
- **Death 2:** yellow slimes at row 420 (the slime caves west of the spawn).
- **Bug found:** the fight weapon was chosen by raw damage per tick, so the copper shortsword beat the wooden sword, while the hotbar stocking (correctly) takes the typical defense off. The bot fought slime crowds at a point or two a hit.
  - **Fix:** the fight weapon now uses the same defense-adjusted score.
- Not yet used: throwing knives from chests (222 in one), since thrown weapons are not counted as weapons yet.
- fight_green_slime, fight_two_slimes_both_sides, early_game_with_slimes and settle_auto pass.

## Run 65 (commit af7038b), stopped after about 4 game minutes (6 deaths)
- The bot now fights with the wooden sword, and slime fights ended quickly. Anvil about 15 s. Silver ore (32) mined, and a Silver Broadsword at about 5 min.
- **Death loop at night:** respawned at the spawn and sent to the furnace below the house ("walking to the Furnaces to craft Silver Bar"). It fought zombies in the pit there and died 5 times in a row (zombies hit 17–23).
  - The night rule only held back mining, exploring, gathering and travel.
  - **Fix:** any WalkTo near the surface at night (defense < 6, house within 150 tiles) waits for the morning in the house.
- Seen too: a few "InvalidStart" plans from (1963,399), which caused rests. Not followed up yet.
- 1 new test (248 pass). night_home, night_shelter and settle_auto pass.

## Run 66 (commit 3879996), stopped after about 3 game minutes (0 deaths, but stuck)
- Anvil at about 1.5 min. No deaths, and no night deaths at the furnace.
- **Stuck:** from about 2 min, at (2117,461), below the depth limit. Every explore leg or walk up was interrupted by "Danger: pausing ... to fight", and each fight ended at once with 0 kills, several times a second for 6 game hours (a selftest stall).
  - InDanger counted hostiles that FightSkill will not take on below the depth limit (NotTooDeep: only an attacker within 4 tiles).
  - **Fix:** starting a fight uses InDanger(toFight: true), with the same depth filter. The retreat check still counts every hostile.
- 1 new test (249 pass). fight_green_slime, shoot_demon_eye, early_game_with_slimes, night_shelter and settle_auto pass.

## Run 67 (commit 98b712c), stopped after about 10 game minutes. **Best run tonight: 1 death**
- Progress:
  - Anvil 35 s, Silver Broadsword about 3 min, Iron Pickaxe about 4 min, **Iron Helmet worn at about 7 min**.
  - Crowd zones and the recall-before-trip worked; no desert deaths.
- **Death:** a trip west to (1735,269) crossed the corruption. An eater of souls fight at a chasm edge ended in a 66-tile fall (409 damage).
  - **Fix:** TravelForOre skips destinations whose way along the surface (sampled every 15 tiles) crosses biome ground or off-limits spots the gear is too weak for (RouteCrossesBiome).
- **Other fixes from watching it (commit abd5277):**
  - At a trip's destination, exploring went back west across the desert. With no safer side, exploring more than 60 tiles from the spawn now goes onwards, away from it.
  - UseItemSkill finished when the potion left the bag, but the recall teleport comes later in the use. The next walk set off from the old spot and a second potion was drunk. Now it waits for the use animation to end.
- **Pillar mystery, diagnosed further:** four "placed True, landed back at the start" cases. Each time the block sits in a feet cell of the start node while the feet stay at that row's bottom, so the body overlaps the block. Vanilla's PlaceTile refuses blocks over a player (Collision.EmptyTile), and the blocks are dirt or stone (falling tiles are excluded). Unexplained. It costs a re-plan (and banned steps) each time.
- 3 new tests (251 pass). iron_age_auto, settle_auto and life_crystals pass.
