# Changelog

## 2026-11-04 (Stage 2 and 3)
- Added: player trade and stalls, bind-on-equip, party kill-quest and EXP sharing, class change, north
  quest givers, Parnassus, forge equipment compounding, mentor system with B-Gold rewards, guild
  building and quest rules, training ground (generated spawns) and the Lost Book chain.
- The code is included. Most of it is switched off by default: the reference server turns them on
  with environment variables (for example `GODSWAR_MENTOR=1`, `GODSWAR_FORGE_EQUIP_COMPOUND=1`,
  `GODSWAR_PARNASSUS=1`, `GODSWAR_NORTH_QUESTS=1`) and hot rates in `server_rates`
  (`guild.building_rules`, `guild.quest_rules`, `instance.training_ground_entry`, `event.lost_book`,
  the trade and stall keys). The setup script does not set them; read the source for each switch.

## 2026-11-04 (R3)
- Server updated to the R3 release: quest release 2026-11 with north quest givers, monster skill
  presentation and melee reach fixes, skill refusal notices, unique GM NPC keys per map, Turret skill.
  Pan's Labyrinth, professions (`/craft`), sigil fixed damage and holiday drop windows are included
  but switched off by default.
- Protocol checks that need development-repository files not in this package now report SKIP.

## First release
- First public release: server, editor, sign-up site, launcher, guided `setup.sh` (local or VPS).
- `GODSWAR_EDITION=classic|reborn` switches the gear limits (10/12 or 20/25).
- Baseline game database with all content and no players.
