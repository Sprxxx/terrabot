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
