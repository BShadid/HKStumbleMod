# Stumble

Every so often while running, our hero will trip, fall
flat on their face, and have to pick themselves back up.

![Ow.](stumble.gif)

## Installation

Install with **Lumafly**: search for Stumble and click Install. Lumafly also
sets up the Modding API, which Stumble needs (Hollow Knight 1.5).

To install by hand, download `Stumble.zip` from the
[latest release](../../releases/latest) and extract it into
`hollow_knight_Data/Managed/Mods/Stumble/`.

## What happens

- They can trip whenever they're walking or running along the ground.
- They slide forward with their momentum, fall, and stay down until you press
  left or right once again.
- Getting hit while they're down knocks them back as usual.

## Options

In the game, go to **Options > Mods > Stumble**.

| Menu Option | What it does |
|---|---|
| Knight Clumsiness | How often they trip: Low, Medium (default), High or Very High. |
| Time on the ground | The shortest time they stay down before you can get them up. |
| Fall animation | Show them falling over, rather than dropping straight to the floor. |
| Fall speed | How quickly they fall. |
| Skip pauses in fall | Smooths the fall by skipping the moment they'd pause on one knee. |
| Forward slide | How far they slide after tripping. |
| At ledges | Whether a slide stops at the edge or carries them over it. |
| Landing sound | A soft landing sound when they tumble off a short drop. |
| Trip thud | A thud when they hit the floor. |
| Impact dust | A puff of dust where they land. |
| Detailed logging | Extra detail in the game's log, for reporting problems. |

## Good to know

- They can't trip while dashing, jumping, healing or using Shape of Unn. If
  you're determined, you can hop your way around Hallownest.
- The map and inventory can't be opened while they're down. The pause menu still
  works.
- Tripping into a cutscene keeps them down until it's over.
- Falling into water, or acid with Isma's Tear, ends the trip just before the
  splash, so swimming works normally.
- The bouncy mushrooms in Fungal Wastes don't bounce a tripped Knight. They just
  faceplant. This is intentional.
- A trip isn't damage, so it won't break the Delicate Flower.
- How often they trip doesn't depend on your frame rate.

## Reporting problems

1. Turn on **Detailed logging** in the Stumble options.
2. Make the problem happen again.
3. [Open an issue](../../issues) describing what happened, and attach
   `ModLog.txt` and a list of your other installed mods.

`ModLog.txt` is in the game's save folder:

- Windows: `%AppData%\..\LocalLow\Team Cherry\Hollow Knight`
- macOS: `~/Library/Application Support/unity.Team Cherry.Hollow Knight`
- Linux: `~/.config/unity3d/Team Cherry/Hollow Knight`

## Credits

Made by TCM, with help from Claude (Anthropic).

Building the mod from source is covered in [DEVELOPING.md](DEVELOPING.md).
