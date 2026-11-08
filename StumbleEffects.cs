using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Modding;
using UnityEngine;

namespace Stumble
{
    // What the player sees and hears: the fall animation, the thud and landing
    // sounds, and the landing dust.
    public partial class StumbleMod
    {
        // Shortest time in the air that counts as a landing worth a sound,
        // so tiny bumps in the floor stay silent.
        private const float MinAirTimeForSound = 0.15f;

        // How long to wait for the game to register a landing before the mod
        // decides the game won't play a landing sound itself.
        private const float LandingWaitTime = 0.1f;

        // The reversed fall clip is built once and reused until the source
        // clip or any fall setting changes.
        private tk2dSpriteAnimationClip _fallClip;
        private tk2dSpriteAnimationClip _fallSource;
        private string _fallBuiltKey;

        // ---- The fall animation --------------------------------------------

        /// <summary>
        /// Builds the fall animation: the stand-up clip's frames in reverse
        /// order, played faster, optionally without held poses or chosen
        /// frames. The frames point at the game's existing sprites, so no new
        /// art is involved.
        /// </summary>
        private tk2dSpriteAnimationClip GetFallClip(tk2dSpriteAnimationClip rise)
        {
            if (rise == null || rise.frames == null || rise.frames.Length == 0) return null;

            if (_settings.DetailedLogging) LogRiseFramesOnce(rise);

            string key = $"{_settings.FallSpeed}|{_settings.FallSkipHolds}|{_settings.FallSkipFrames}";
            if (_fallClip != null && _fallSource == rise && _fallBuiltKey == key)
                return _fallClip;

            HashSet<int> skip = ParseFrameRanges(_settings.FallSkipFrames);

            // Walk the stand-up frames from last to first (frame numbers are
            // 1-based to match the log), leaving out skipped frames and,
            // if enabled, repeats of the frame just kept.
            var frames = new List<tk2dSpriteAnimationFrame>();
            tk2dSpriteAnimationFrame previous = null;
            for (int i = rise.frames.Length - 1; i >= 0; i--)
            {
                if (skip.Contains(i + 1)) continue;

                tk2dSpriteAnimationFrame f = rise.frames[i];
                if (_settings.FallSkipHolds && previous != null && SameSprite(f, previous)) continue;

                frames.Add(new tk2dSpriteAnimationFrame
                {
                    spriteCollection = f.spriteCollection,
                    spriteId = f.spriteId,
                    // Leave animation events off: the stand-up clip's events
                    // belong to standing up, not falling.
                    triggerEvent = false
                });
                previous = f;
            }

            if (frames.Count == 0)
            {
                LogWarn("FallSkipFrames removed every frame of the fall, so the fall is skipped.");
                _fallClip = null;
            }
            else
            {
                _fallClip = new tk2dSpriteAnimationClip
                {
                    name = "Trip Fall",
                    frames = frames.ToArray(),
                    fps = rise.fps * _settings.FallSpeed,
                    loopStart = 0,
                    wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
                };
            }

            _fallSource = rise;
            _fallBuiltKey = key;
            return _fallClip;
        }

        private static bool SameSprite(tk2dSpriteAnimationFrame a, tk2dSpriteAnimationFrame b) =>
            a.spriteCollection == b.spriteCollection && a.spriteId == b.spriteId;

        /// <summary>Parses "6-11, 14" into {6,7,8,9,10,11,14}. Bad entries are ignored.</summary>
        private static HashSet<int> ParseFrameRanges(string text)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrEmpty(text)) return result;

            foreach (string part in text.Split(','))
            {
                string[] bounds = part.Trim().Split('-');
                if (bounds.Length == 1 && int.TryParse(bounds[0].Trim(), out int single))
                {
                    result.Add(single);
                }
                else if (bounds.Length == 2
                    && int.TryParse(bounds[0].Trim(), out int from)
                    && int.TryParse(bounds[1].Trim(), out int to))
                {
                    for (int n = Mathf.Min(from, to); n <= Mathf.Max(from, to); n++) result.Add(n);
                }
            }
            return result;
        }

        // ---- Impact and landings -------------------------------------------

        /// <summary>
        /// The Knight's body hits the floor on flat ground: starts the clock
        /// for MinLieTime, plays the trip thud and spawns the landing dust if
        /// they're switched on.
        /// </summary>
        private void Impact(ActiveTrip t)
        {
            t.Impacted = true;
            if (_settings.TripThud && !t.ThudPlayed) PlaySoftThud(t);
            SpawnImpactDust(t);
        }

        /// <summary>
        /// Times how long the Knight is in the air during a tumble and makes
        /// sure each landing gets exactly one sound.
        ///
        /// Long falls: the game flags a heavy landing on its own and plays its
        /// heavy thud, so the mod stays quiet. Short drops: if the game noticed
        /// the Knight leave the ground it plays its own soft landing; if not,
        /// the mod plays the soft one.
        /// </summary>
        private void TrackLanding(ActiveTrip t)
        {
            HeroControllerStates s = t.Hero.cState;

            if (!t.Grounded)
            {
                t.AirTime += Time.deltaTime;
                if (!s.onGround) t.GameSawFlight = true;
                if (s.willHardLand) t.GameHardLanding = true;
                t.LandingWait = 0f;
                return;
            }

            if (t.AirTime <= 0f) return;

            if (t.GameHardLanding)
            {
                // The game plays its heavy thud for this landing; that counts
                // as this trip's thud and its impact.
                t.ThudPlayed = true;
                t.Impacted = true;
                t.EndFlight();
                return;
            }

            if (t.GameSawFlight)
            {
                // The mod's ground check can fire a frame or two before the
                // feet actually touch, so give the game a moment to register
                // the landing before deciding it won't.
                if (!s.onGround && t.LandingWait < LandingWaitTime)
                {
                    t.LandingWait += Time.deltaTime;
                    return;
                }

                if (s.onGround)
                {
                    // The game landed the Knight and played its own sound;
                    // that counts as this trip's thud and its impact.
                    t.ThudPlayed = true;
                    t.Impacted = true;
                    t.EndFlight();
                    return;
                }
            }

            // Tiny bumps in the floor aren't a landing: no sound, and they
            // don't count as the trip's impact. A landing the game didn't
            // notice gets the mod's sound and dust instead of the game's.
            if (t.AirTime >= MinAirTimeForSound)
            {
                if (_settings.LandingSounds || (_settings.TripThud && !t.ThudPlayed))
                    PlaySoftThud(t);
                SpawnImpactDust(t);
                t.Impacted = true;
            }
            t.EndFlight();
        }

        private static void PlaySoftThud(ActiveTrip t)
        {
            HeroAudioController audio = t.Hero.GetComponent<HeroAudioController>();
            if (audio == null) return;

            audio.PlaySound(HeroSounds.SOFT_LANDING);
            t.ThudPlayed = true;
        }

        /// <summary>
        /// Spawns the game's own soft-landing effect, the same object and
        /// position the game uses for a normal landing, so it picks its own
        /// look for the area (dust, grass and so on).
        /// </summary>
        private void SpawnImpactDust(ActiveTrip t)
        {
            if (_settings.DetailedLogging) LogGroundOnce(t);
            if (!_settings.ImpactDust) return;

            GameObject prefab = t.Hero.softLandingEffectPrefab;
            if (prefab == null) return;

            try
            {
                prefab.Spawn(t.Hero.transform.position);
            }
            catch (System.Exception e)
            {
                LogWarn("Couldn't spawn the landing dust: " + e.Message);
            }
        }
    }
}
