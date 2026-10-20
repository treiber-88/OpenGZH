# Shipyard artwork: what the engine expects

The shipyard is the one OpenGZH building with no original art. The engine side is done: every
faction's dozer or worker can build a shipyard on a shoreline, and the shipyard builds that
faction's ships. Until the new art exists, the shipyards use the original concrete pier
`PMDock01` as a stand-in. This document lists what to make so the real art can replace it.

## Which shipyards

| Faction | Rules file                         | Model name (new) | Built by        |
|---------|------------------------------------|------------------|-----------------|
| USA     | `mods/zh/rules/usa/shipyards.yaml` | `ABShipyard`     | Dozer           |
| China   | `mods/zh/rules/china/shipyards.yaml` | `NBShipyard`   | Dozer           |
| GLA     | `mods/zh/rules/gla/shipyards.yaml` | `UBShipyard`     | Worker          |

The prefixes follow the original naming: `AB` = America building, `NB` = China building,
`UB` = GLA building. Each general (Air Force, Laser, Superweapon, Tank, Infantry, Nuke,
Demolition, Stealth, Toxin) uses their faction's shipyard model, so three models cover all
twelve factions.

## Model files (W3D)

Export each model as a W3D file containing an HLOD with the same name as the file, exactly
like the original buildings (for example `ABWarFact.w3d`). The engine reads models from
`Art/W3D/`, so put new files in `mods/zh/assets/Art/W3D/` (this folder overrides the original
archives).

Required for each faction, using the original damage-state convention:

| File               | Shown when                         |
|--------------------|------------------------------------|
| `ABShipyard.w3d`   | normal                             |
| `ABShipyard_D.w3d` | damaged (below 70% health)         |
| `ABShipyard_E.w3d` | badly damaged (below 35%) / rubble |

Optional, if you want them (the original buildings have them):
night `_N`, `_DN`, `_EN`; snow `_S`, `_DS`, `_ES`; night+snow `_NS`, `_DNS`, `_ENS`.

## Size, placement and orientation

* World scale: one terrain cell is 10 units. For comparison, the USA War Factory is about
  106 × 120 units and 40 units tall.
* Footprint the rules currently use: **110 units along X × 120 units along Y**, about
  35 units tall (`GeometryMajorRadius: 55`, `GeometryMinorRadius: 60`, `GeometryHeight: 35`).
  If your model ends up a different size, tell me the size and I'll update these numbers.
* Model origin (0,0,0) is the building's center, at ground level. Z is up.
* **+X faces the water; −X is the land side.** The shipyard straddles the shore: at least 15% of
  its footprint must be on buildable land and at least 15% on water.
* Ships appear at **X = +90, Y = 0** (just beyond the water side) and sail to X = +170.
  If your design has a launch slipway or dock gate somewhere else, tell me where and I'll
  move these points (`UnitCreatePoint` / `NaturalRallyPoint` in the rules file).
* The water level is often below the ground, so give the water side pilings or a seawall going
  down about 15 units below the origin, so the building never looks like it's floating.

## Player (team) colour

The same two methods as the original game:

* Meshes whose name starts with `HOUSECOLOR` (e.g. `HOUSECOLOR01`) take the player's colour.
* Or textures named `ZHCA…` (alpha marks the team-coloured areas) or `ZHCD…` (the top-row
  palette is recoloured).

## Textures

`.dds` or `.tga` files, up to 512 × 512 like the originals, placed in
`mods/zh/assets/Art/Textures/`.

## Command bar images (cameos)

These are separate from the model, and currently also use stand-ins:

| Image             | Size (px) | Used for                          | Current stand-in    |
|-------------------|-----------|-----------------------------------|---------------------|
| `SAShipyard`      | 56 × 48   | USA dozer build button            | `SACWeaponsfact`    |
| `SAShipyard_L`    | 108 × 89  | USA shipyard portrait when selected | `SACWeaponsfact_L` |
| `SNShipyard`      | 56 × 48   | China dozer build button          | `SNWarFact`         |
| `SNShipyard_L`    | 108 × 89  | China shipyard portrait           | `SNWarFact_L`       |
| `SUShipyard`      | 56 × 48   | GLA worker build button           | `SUArmsDealer`      |
| `SUShipyard_L`    | 120 × 96  | GLA shipyard portrait             | `SUArmsDealer_L`    |

The ships could use their own cameos too (the battleship and carrier already have original
ones): `SAPTBoat`, `SNPatrolJunk`, `SNGunboat`, `SUArmedTrawler`, `SUBombBoat` (56 × 48 each).

Save each image as its own `.tga` file in `mods/zh/assets/Art/Textures/`; OpenGZH does not need
them packed into the original atlas pages.

## When the art is ready

Drop the files in place, then in each `shipyards.yaml` replace `PMDock01` with the new
model names and the stand-in button images with the new ones. Tell me and I'll do it, check the
building in game, and adjust footprint and launch point to match the model.
