# Research: how players progress and fight bosses

Notes from the Terraria wiki (terraria.wiki.gg, the official wiki; fandom blocks automated reads) and progression
guides, read on 2026-10-11, with what each finding means for TerraBot. Numbers marked *(code)* were checked against
the decompiled game (tModLoader 1.4.4.9); the rest come from the guides and still need checking in game before the
bot relies on them.

## Sources

- [Guide:Class setups](https://terraria.wiki.gg/wiki/Guide:Class_setups): gear per class and stage (the wiki.gg
  version of the fandom Class progression page)
- [Guide:Eye of Cthulhu strategies](https://terraria.wiki.gg/wiki/Guide:Eye_of_Cthulhu_strategies)
- [Guide:Arena](https://terraria.wiki.gg/wiki/Guide:Arena): arena layouts per boss
- [Guide:Walkthrough](https://terraria.wiki.gg/wiki/Guide:Walkthrough): marked "most likely outdated since 1.4"
- [Bosses](https://terraria.wiki.gg/wiki/Bosses): summons and progression gates
- [Campfire](https://terraria.wiki.gg/wiki/Campfire), [Heart Lantern](https://terraria.wiki.gg/wiki/Heart_Lantern)
- Third-party boss-order guides, used only to cross-check the order:
  [supercraft](https://supercraft.host/wiki/terraria/boss-progression/),
  [xgamingserver](https://xgamingserver.com/blog/terraria-progression-guide-boss-order/),
  [TheGamer](https://www.thegamer.com/terraria-boss-progression-guide/)

## 1. Every guide builds an arena first

An arena is a stretch of ground or platforms prepared before the summon. For the Eye of Cthulhu:

- **Size:** about one screen wide, with several rows of **wood platforms** at least **6 tiles apart** (jump height),
  and **torches** for light. The other standard layout is a long flat runway: the run-and-gun the bot does now.
- **Regeneration placed in the arena:**
  - **Campfire:** 10 wood + 5 torches. Gives Cozy Fire: +10% life regen and +0.5 HP/s within 170×125 tiles.
  - **Heart Lantern:** 1 Life Crystal + 4 Chain, crafted by hand. Gives 1 HP/s within 170×125 tiles. Both must be
    *placed*.
  - Also: honey (needs buckets), Heart Statues, a **Nurse** in a house under the arena, a Bast Statue (defense),
    Sunflowers (Happy!: faster movement, fewer spawns).
- **Placement rules:** away from town NPC houses, so the boss doesn't kill them; near the ground, so harpies don't
  spawn; summon right after dusk for the most night.

**What it means for the bot.** It already builds a house (Milestone 6) and places stations, so an arena is the same
machinery with a different blueprint:
1. Choose a site: a flat strip about 60–100 tiles wide near spawn, not on top of the house.
2. Level the floor: break bumps, fill pits.
3. Optionally add one or two platform rows 6+ tiles up.
4. Place torches, a campfire and a heart lantern in the middle.

This also fixes something the in-game tests showed. The bot gets hit when it is forced to turn round at a wall or at
the end of its run, so a planned arena with known ends replaces "whatever ground is there".

## 2. Preparation the guides recommend for the Eye of Cthulhu

- **Life:** at least 200. That is also the natural-spawn condition: 200+ life, 10+ defense, 3+ housed NPCs. The
  bot already gets to 200.
- **Armour:** silver or tungsten, or gold or platinum. Fossil for ranged. Iron works; the bot wears iron now.
- **Ranged:** a gold or platinum bow, or the Demon/Tendon Bow. Minishark, Boomstick and Musket are better.
  - Ammo: **Flaming Arrows** (wood arrows + a torch) or Frostburn Arrows; Jester's Arrows pierce servants.
  - Phase 1 has 12 defense, which takes 6 off every hit *(code)*. That is why slow, hard-hitting shots beat fast
    weak ones there; the bot's wooden arrows lose about half their damage.
- **Potions:** a stack of healing potions, plus Ironskin, Regeneration, Swiftness and Archery.
- **Mobility:** **Hermes Boots** are "key", and near-required in Expert. Cloud in a Bottle helps against rams;
  Magiluminescence adds 20% speed on top of the boots.

**What it means for the bot.** The ranking by value:
1. **Arena with campfire and heart lantern:** cheap; it already has wood, torches and life crystals.
2. **Healing potions:** Lesser Healing Potions from gel, mushrooms and bottles. Today it has only what the test
   hands it.
3. **Better arrows and bow:** flaming arrows cost one torch per batch.
4. **Hermes Boots:** found in chests, so this needs exploring and opening chests, which the bot can't do yet.
5. **Buff potions:** each needs a herb (Daybloom, Blinkroot), which means herb gathering.

## 3. Fight tactics from the guides, checked against the code

| Guide says | Game code *(code)* | Bot now |
|---|---|---|
| Phase 1: hovers, spits servants, then charges 3 times | Hovers 600 ticks (Expert 210), 3 dashes 150 apart (Expert 100), speed 6 (7) | `EyePattern` reads ai[] for exact timing |
| Phase 2 below 50%: 0 defense, faster, 3 charges then pause | Hovers 120 px above for 200 ticks, dashes at 6.8, 130 apart (Expert 90) | Modelled: hover seek, dash, brake |
| Spin into phase 2: "attack freely" | ai[0] 1–2: no dashes, slows ×0.98 | Treated as safe to turn |
| Run in a straight line; don't get caught turning | A dash aims at where the player is when it starts | Turns only when the next dash is ≥ 60 ticks away |
| Don't jump into it (implied by "rows ≥ 6 apart") | It hovers 120 px above in phase 2; a full jump rises ~100 px | The dodge planner predicts the hover and avoids it |
| Expert phase 2: quick steep dashes at low life | ai[1] 3–5 | Not modelled; treated as "dash now" |

The bot already did the things the guides call key: keep running, never stand under it, and use the spin to hit.
Hermes Boots and arena regeneration are the missing pieces.

## 4. Progression order toward Moon Lord

**Pre-Hardmode** (the guides agree; King Slime and Deerclops are optional):
1. King Slime (optional): a Slime Crown, or a Slime Rain.
2. **Eye of Cthulhu:** done.
3. **Eater of Worlds or Brain of Cthulhu**, depending on the world's evil. Summoned with Worm Food or a Bloody
   Spine, or by breaking 3 Shadow Orbs or Crimson Hearts.
   - Gives Demonite or Crimtane plus Shadow Scales or Tissue Samples: better pickaxe and armour.
   - Arenas: Eater of Worlds, a tall thin platform arena in Classic; Brain, a 5-layer platform arena in the Crimson.
4. Queen Bee (optional, but unlocks the Witch Doctor): a jungle hive, platforms every 5 tiles, honey at the bottom.
5. **Skeletron:** the Old Man at the Dungeon at night. Opens the Dungeon.
6. **Wall of Flesh:** Guide Voodoo Doll into Underworld lava. Needs a **hellbridge** of at least 1000 tiles, and
   unlocks Hardmode.
   - Gear before it: Molten armour (45 Hellstone Bars), and the Hellwing Bow or Star Cannon for ranged.

**Hardmode:**
1. Smash altars for Hardmode ores (needs the Pwnhammer).
2. Queen Slime (optional).
3. The Twins, the Destroyer and Skeletron Prime. Arenas: a long asphalt skybridge (Twins), suspended platform rows
   (Prime).
4. **Plantera**, after all three mechanical bosses. Gives the Temple Key.
5. **Golem**, in the Jungle Temple. A platform row 25–30 tiles up.
6. **Lunatic Cultist**, after Golem and Skeletron. Starts the Lunar Events.
7. The four Celestial Pillars.
8. **Moon Lord.** Asphalt skybridges and a Nurse.

**Ranged gear path from the class guide:**
- Pre-boss: Boomstick, Minishark or Musket; Fossil armour.
- Pre-Skeletron: Molten Fury, Bee's Knees.
- Pre-WoF: Hellwing Bow, Phoenix Blaster.
- Early Hardmode: Daedalus Stormbow, Uzi, Megashark.
- Then Chlorophyte Shotbow, Shroomite, and so on.

Ranged suits the bot best: the aiming and ballistics already work.

## 5. Proposed next steps

1. **Milestone 8, arena:**
   - An `ArenaBlueprint`: a flat floor about 80 wide, optional platform rows, torches every 10 tiles, a campfire
     and a heart lantern in the middle.
   - Reuse BuildSkill and SiteFinder to build it.
   - A `PrepFor(boss)` need for it in the boss plan.
   - BossFightSkill kites inside the arena's known bounds.
2. **Healing potions:** Lesser Healing Potion (mushrooms + gel + bottles at a placed bottle), plus mushroom
   gathering.
3. **Flaming arrows** and a gold or platinum bow when the ore is there.
4. Then the next boss: Eater of Worlds or Brain of Cthulhu, whichever evil the world has. This needs the bot to
   travel to the evil biome, a new capability.
