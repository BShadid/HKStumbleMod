# Developing Stumble

How to build the mod, where things are in the code, every setting in the
settings file, and what the mod writes to the log.

## Building

You need Hollow Knight 1.5 with the Modding API installed (through Lumafly,
**before** building: the project compiles against the Modding API's version of
`Assembly-CSharp.dll`) and the .NET SDK (any recent version).

1. Open `Stumble.csproj` and set `GameDir` to your Hollow Knight install folder.
2. Run `dotnet build -c Release` in a folder containing `Stumble.csproj` and the
   seven source files listed below, and no other `.cs` files (the build compiles
   every `.cs` file in the folder and its subfolders).

The build copies `Stumble.dll` into `hollow_knight_Data/Managed/Mods/Stumble/`
automatically. To install by hand, copy `bin/Release/net472/Stumble.dll` there.

## Releasing

Every release from v0.2, the Preloaded Tinkables Update, onward needs:

1. **The Path of Pain, cleared on the hardest settings:** Knight Clumsiness and
   Wall Clumsiness both on Very High, with every other setting at its default,
   on the build being released. No exceptions.
2. The version updated in both `StumbleMod.cs` (`GetVersion`) and
   `Stumble.csproj`, as four numbers (`0.2.0.0`).
3. The high-risk checks in the test plan passed on that build.
4. A GitHub release tagged `vX.Y` with `Stumble.dll` zipped as `Stumble.zip`.
5. A pull request to [modlinks](https://github.com/hk-modding/modlinks)
   updating Stumble's entry: the version, the release link, and the zip's
   SHA256 (without the `sha256:` prefix).

## Source files

| File | What's in it |
|---|---|
| `StumbleMod.cs` | Start-up, the game hooks, starting and ending a trip (every way a trip ends goes through `EndTrip`), and blocking the map and inventory while down. |
| `StumbleSequence.cs` | One trip from start to finish (fall, lie, get up), the per-trip state, and the checks run every frame while the Knight is down (`TickDown`). |
| `StumbleEffects.cs` | The fall animation, the thud and landing sounds, and the landing dust. |
| `StumbleDiagnostics.cs` | Log output for troubleshooting. |
| `StumbleSlip.cs` | Wall slips: losing grip as a wall is caught or while clinging, the no-grab window and startle that follow, and the spark where the grip goes. |
| `StumbleMenu.cs` | The in-game options menu: the mod's on/off switch, Knight Clumsiness, Wall Clumsiness and Detailed Logging. |
| `StumbleSettings.cs` | The settings, their defaults, the safe ranges for hand-edited values, and the timing behind each clumsiness level. |

Common changes:

- **A new setting:** add a field to `StumbleSettings`. It appears in the
  settings file with its default. To also show it in the menu, add an entry in
  `StumbleMenu.cs` (`MenuToggle` covers on/off options).
- **A new reason to end a trip early:** add a check to `TickDown` in
  `StumbleSequence.cs` that calls `EndTrip`, and it applies to both the fall and
  lying stages.
- **Anything about one trip:** add a field to `ActiveTrip` in
  `StumbleSequence.cs`. A new one is made for each trip, so nothing needs
  resetting.
- **Retuning clumsiness:** the average seconds of running between trips, the
  chance of stumbling after a ground dash, the average seconds of wall
  clinging between slips, and the chance of slipping on each wall caught are
  each one line per level near the top of `StumbleSettings.cs`.

## Settings file

Every setting is saved to `Stumble.GlobalSettings.json` in the game's save
folder. The in-game menu offers the on/off switch, Knight Clumsiness, Wall
Clumsiness and Detailed Logging; everything else in the table below is changed
here. The file is created the first time the game saves settings, usually when
you quit or change a setting in the menu. Edit it with the game closed; delete
it to go back to the defaults.

| Setting | Default | What it does |
|---|---|---|
| KnightClumsiness | Medium | How often the Knight trips while running, and their chance of stumbling after a dash along the ground: Low, Medium, High or Very High. |
| WallClumsiness | Medium | How often the Knight loses their grip on walls, both as they catch one and while clinging: Off, Low, Medium, High or Very High. |
| SlipNoGrabTime | 0.5 | Seconds after a wall slip during which the Knight can't grab a wall or wall-jump. Landing ends it sooner. |
| SlipNoDoubleJumpTime | 0.25 | Seconds at the start of that window (the startle) during which double jumps don't work either, so a jump pressed out of habit as they slip doesn't save them. Never longer than SlipNoGrabTime; 0 turns it off. |
| SlipSound | sword_hit_reject | Sound played on a wall slip, by name from `Stumble.Sounds.txt` (see Auditioning sounds). The default is the nail's "tink" off something it can't cut. A clip that isn't one of the Knight's own sounds is kept once found, so it keeps playing in other areas. Blank plays nothing. |
| SlipEffect | Tink | Effect where the Knight loses their grip: `Tink` (the spark the nail makes off something it can't cut, preloaded at start-up), the name of one of the Knight's own effects (e.g. `Wall Puff`; see Auditioning effects), or blank for none. |
| MinLieTime | 0.35 | Seconds on the ground before a left/right press stands them up, counted from the impact: when the trip thud plays (or would, if it's off), or the landing after a tumble. |
| FallAnimation | true | Play the stand-up animation in reverse as a fall. |
| FallSpeed | 1.5 | How much faster than standing up the fall plays. |
| FallSkipHolds | true | Drop held poses (repeated frames, like the kneeling pause) from the fall. |
| FallSkipFrames | 3-15 | Frames of the stand-up animation to leave out of the fall, e.g. `6-11, 14`. |
| SlideTime | 0.75 | Seconds the Knight keeps sliding forward after tripping, slowing to a stop. 0 turns it off. |
| TumbleOffEdges | true | true: a slide carries the Knight off ledges and they fall in the lying pose. false: the slide stops at the edge. |
| LandingSounds | true | Soft landing sound when a tumble drops a short way. Long falls always get the game's own heavy thud. |
| TripThud | true | Soft thud when the Knight hits the floor on every trip. |
| ImpactDust | true | Show the game's landing dust where the Knight hits the floor. |
| ThudFramesEarly | 3 | Play the trip thud this many animation frames before the fall ends, to match when the body visibly lands. 0 plays it as the lying pose starts. |
| LyingClip | Prostrate | Knight animation used while down. |
| RiseClip | Prostrate Rise | Animation for getting up, and reversed for the fall. Blank skips both. |
| DetailedLogging | false | Write extra detail to `ModLog.txt` (see Log messages). |

### Auditioning sounds

With DetailedLogging on, the mod writes `Stumble.Sounds.txt` next to
`ModLog.txt`. It lists the Knight's own sounds, which always play, and every
sound clip the game has had loaded this session, with the area where each was
first seen. It's updated whenever you enter an area with new sounds, and on
each wall slip. Put any name from it into `SlipSound` (game closed), set Wall
Clumsiness to Very High, and slip off a wall to hear it. Sounds that aren't the
Knight's own only play while the game has them loaded. The Knight's looping
sounds (footsteps, wall sliding) are stopped after a second.

### Auditioning effects

With DetailedLogging on, the first wall slip each session lists the Knight's
own effects in `ModLog.txt`. Put any of those names, or `Tink`, into
`SlipEffect` (game closed) and slip off a wall to see it. Effects that belong
to the Knight are attached to them, so some may follow the Knight down.

### Fine-tuning the fall

With DetailedLogging on, the first trip each session writes every frame of the
stand-up animation to `ModLog.txt`, in order, with its sprite name and a note
on frames that repeat the one before. Frame numbers in `FallSkipFrames` match
that list.

## How it behaves, in detail

- A trip can start while the Knight is running on the ground with full control
  (not dashing, focusing, recoiling, transitioning or paused), or while they're
  walking with the quick map out. Focusing includes Shape of Unn's slug form.
- A dash (Mothwing or Shade Cloak) that starts and finishes on the ground gets
  one roll for a trip when it ends, at the clumsiness level's per-dash chance.
  A trip's slide is capped a little above running speed, so dash speed doesn't
  carry into it.
- Each frame's chance is scaled to how long that frame took, so trips average
  one per clumsiness interval of running at any frame rate. A stutter gets a
  proportionally bigger chance.
- Tripped while map-walking: the game's put-the-map-away animation plays first,
  then the fall from the start (capped at a one-second wait).
- Standing up needs a fresh left/right press and solid ground.
- A hit while down hands control back the moment the hit lands (the Modding
  API's damage hook), so the normal knockback plays out.
- Falling into water, or acid with Isma's Tear, ends the trip just before the
  splash. Landing on a platform above the water, or tripping in shallow water,
  is unaffected.
- Cutscenes that take and return control during a trip are held off: the Knight
  stays down through them, and MinLieTime restarts when the cutscene ends.
- On standing up, any trigger area they slid or fell into during the trip is
  told they left and came back in, so hidden areas light up and so on.
- Gravity is left entirely to the game. If the Knight is ever held off the
  ground without falling for half a second, the trip ends and control returns.
- A room transition, death, hazard respawn, warp or scene load mid-trip ends
  the trip: the Knight is put back the way they'd be if they'd never tripped and
  the game runs its sequence.
- While down, the quick map, full map and inventory buttons are switched off,
  and switched back on when the trip ends.
- Each time the Knight catches a wall (starts touching a climbable wall in the
  air, with the Mantis Claw and full control, not mid-dash), a slip is rolled
  once at the wall clumsiness level's per-catch chance. This covers wall-jump
  climbs, where each wall is touched too briefly to start wall-sliding.
  Contact that drops out for under a tenth of a second counts as the same
  catch. While clinging to a wall (wall-sliding), a slip is also rolled each
  frame the same way as a trip, scaled by frame time. Every slip gives back
  the double jump and air dash, as clinging would have (through the game's
  `ResetAirMoves`, or the `doubleJumped` and `airDashed` fields if that's
  missing), so a slip the instant a wall is caught counts as a grip.
- The slip effect (SlipEffect): `Tink` plays the spark from a tinkable
  object's `TinkEffect.blockEffect` (looked up by name). The Modding API
  preloads a set of tinkable spikes at start-up
  (`ruind_bridge_roof_06/ruind_bridge_roof_04_spikes` in level file 98, a
  City of Tears roof; the area's name is looked up from that number), and
  the mod keeps a silent copy of their spark that works everywhere. If the
  preload comes back empty, it looks for a spark whenever an area loads, and
  on a slip, and keeps the first one found; until then the wall puff stands
  in. The spark plays against the wall (`touchingWallL` /
  `touchingWallR`) at the Knight's middle height. A Knight effect is
  restarted the way the game does (switched off and on again); the wall puff
  (`wallPuffPrefab`) lets go of the Knight as it plays, so it stays put, and
  since it sits in front of the Knight, who usually faces away from the wall
  mid-slip, it's mirrored to the wall side at the same distance and height
  and held there for its first three frames. A slip lets go of the wall through
  the game's own `CancelWallsliding` (looked up by name). The Knight keeps
  full control, but for SlipNoGrabTime, or until they land, the game's
  `CanWallSlide` and `CanWallJump` checks answer no, so they can't grab a wall
  or wall-jump (a wall-jump otherwise works off any wall being touched). For
  the first SlipNoDoubleJumpTime of the window (the startle), `CanDoubleJump`
  answers no as well, and every frame the mod clears the double jump the game
  remembers from an early press (`doubleJumpQueuing`), so a press during the
  startle can't fire the moment it ends; the player has to press again. Those
  checks are hooked by name when the mod starts; if a grab check can't be
  found, a warning is logged and the mod instead lets go of any wall grabbed
  during the window. A room transition, death, hazard respawn or scene load ends the
  window. If `CancelWallsliding` can't be found, slips are skipped and a
  warning is logged.
- Switching the mod off mid-trip stands the Knight back up.

## Log messages

Always written:

- a trip ended early because the Knight was about to enter water (names the object);
- a trip ended because a scene is loading (names the scene);
- damage the game missed while the Knight was down was applied (names the source);
- control was handed back because the Knight was held in mid-air;
- a configured animation can't be found (lists every animation the Knight has,
  so the right name can go in `LyingClip` / `RiseClip`; no rebuild needed);
- menu buttons that couldn't be switched off during trips;
- the game's way of letting go of a wall couldn't be found, so wall slips are off;
- the game's way of giving back the double jump and air dash couldn't be found
  (once, on the first slip);
- the game's tink spark couldn't be found (once; `Tink` then shows the wall puff);
- the spikes couldn't be preloaded, or had no spark (at start-up; the spark is
  then picked up in game, with the wall puff standing in until then);
- a `SlipEffect` name that isn't one of the Knight's effects (once per name);
- `HeroController.CanWallSlide` or `CanWallJump` couldn't be found or hooked
  (at start-up; wall slips fall back to letting go of re-grabbed walls);
- `HeroController.CanDoubleJump` couldn't be found or hooked, or
  `doubleJumpQueuing` couldn't be found (at start-up; the startle after a slip
  is then partly or fully missing);
- a `SlipSound` name that isn't one of the Knight's sounds or a loaded sound (once per name);
- `Stumble.Sounds.txt` couldn't be written.

Only with DetailedLogging on:

- the stand-up animation's frame list (see Fine-tuning the fall);
- what the Knight lands on, once per object per session (names it and lists
  what it's made of, plus every other object around their feet);
- areas re-entered after standing up (names them);
- state changes of the game's map, inventory and Knight state machines while they're down;
- the first time per session they move with the map button held but can't trip;
- each time `Stumble.Sounds.txt` is updated, with the number of sounds listed;
- the Knight's effects, on the first wall slip (for SlipEffect);
- the tink spark when the mod gets it (what it is, and whether it was preloaded or found in game);
- that the wall puff is standing in because no tink spark is loaded yet (once);
- on each wall slip, where the tink spark played, or, for a Knight effect,
  the effect as the slip starts and just after: whether it's showing, where
  it sits relative to the Knight, which side the wall is on, its renderers,
  components and children.
