# Developing Stumble

How to build the mod, where things are in the code, every setting in the
settings file, and what the mod writes to the log.

## Building

You need Hollow Knight 1.5 with the Modding API installed (through Lumafly,
**before** building: the project compiles against the Modding API's version of
`Assembly-CSharp.dll`) and the .NET SDK (any recent version).

1. Open `Stumble.csproj` and set `GameDir` to your Hollow Knight install folder.
2. Run `dotnet build -c Release` in a folder containing `Stumble.csproj` and the
   six source files listed below, and no other `.cs` files (the build compiles
   every `.cs` file in the folder and its subfolders).

The build copies `Stumble.dll` into `hollow_knight_Data/Managed/Mods/Stumble/`
automatically. To install by hand, copy `bin/Release/net472/Stumble.dll` there.

## Source files

| File | What's in it |
|---|---|
| `StumbleMod.cs` | Start-up, the game hooks, starting and ending a trip (every way a trip ends goes through `EndTrip`), and blocking the map and inventory while down. |
| `StumbleSequence.cs` | One trip from start to finish (fall, lie, get up), the per-trip state, and the checks run every frame while the Knight is down (`TickDown`). |
| `StumbleEffects.cs` | The fall animation, the thud and landing sounds, and the landing dust. |
| `StumbleDiagnostics.cs` | Log output for troubleshooting. |
| `StumbleMenu.cs` | The in-game options menu. |
| `StumbleSettings.cs` | The settings, their defaults, the safe ranges for hand-edited values, and the timing behind each clumsiness level. |

Common changes:

- **A new option:** add a field to `StumbleSettings`, then one `MenuToggle` or
  `MenuChoice` line in `StumbleMenu.cs`.
- **A new reason to end a trip early:** add a check to `TickDown` in
  `StumbleSequence.cs` that calls `EndTrip`, and it applies to both the fall and
  lying stages.
- **Anything about one trip:** add a field to `ActiveTrip` in
  `StumbleSequence.cs`. A new one is made for each trip, so nothing needs
  resetting.
- **Retuning clumsiness:** the average seconds of running between trips for
  each level is one line near the top of `StumbleSettings.cs`.

## Settings file

Everything in the menu is saved to `Stumble.GlobalSettings.json` in the game's
save folder, along with a few settings the menu doesn't show. The file is
created the first time the game saves settings, usually when you quit or
change a setting in the menu. Edit it with the game closed; delete it to go
back to the defaults.

| Setting | Default | What it does |
|---|---|---|
| KnightClumsiness | Medium | How often the Knight trips while running: Low, Medium, High or Very High. |
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

### Fine-tuning the fall

With DetailedLogging on, the first trip each session writes every frame of the
stand-up animation to `ModLog.txt`, in order, with its sprite name and a note
on frames that repeat the one before. Frame numbers in `FallSkipFrames` match
that list.

## How it behaves, in detail

- A trip can start while the Knight is running on the ground with full control
  (not dashing, focusing, recoiling, transitioning or paused), or while they're
  walking with the quick map out. Focusing includes Shape of Unn's slug form.
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
- Switching the mod off mid-trip stands the Knight back up.

## Log messages

Always written:

- a trip ended early because the Knight was about to enter water (names the object);
- a trip ended because a scene is loading (names the scene);
- damage the game missed while the Knight was down was applied (names the source);
- control was handed back because the Knight was held in mid-air;
- a configured animation can't be found (lists every animation the Knight has,
  so the right name can go in `LyingClip` / `RiseClip`; no rebuild needed);
- menu buttons that couldn't be switched off during trips.

Only with DetailedLogging on:

- the stand-up animation's frame list (see Fine-tuning the fall);
- what the Knight lands on, once per object per session (names it and lists
  what it's made of, plus every other object around their feet);
- areas re-entered after standing up (names them);
- state changes of the game's map, inventory and Knight state machines while they're down;
- the first time per session they move with the map button held but can't trip.
