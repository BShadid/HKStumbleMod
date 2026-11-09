using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Modding;
using UnityEngine;

namespace Stumble
{
    // One trip from start to finish: fall, lie, get up. Plus the safety checks
    // run every frame while the Knight is down.
    public partial class StumbleMod
    {
        // Safety caps on how long we wait for the fall and stand-up animations.
        // The full 21-frame fall at 1x speed takes 1.75s, so this sits above it.
        private const float MaxFallTime = 3f;
        private const float MaxRiseTime = 1.5f;

        // Longest the fall waits for the game to put the map away.
        private const float MaxMapAwayWait = 1f;

        // How far ahead of the Knight's middle to look for ground while sliding.
        private const float EdgeLookahead = 0.15f;

        // Cap on falling speed during a tumble, close to the game's own limit.
        private const float MaxFallSpeed = 20f;

        // How long the Knight may be off the ground without falling before
        // the mod assumes they're stuck and hands control back.
        private const float MaxStuckInAirTime = 0.5f;

        // How far below the Knight to look for water while they're falling, so
        // the trip ends just before they touch it.
        private const float WaterLookahead = 0.6f;

        // Frames the Knight must overlap something damaging, with the game not
        // reacting, before the mod applies the damage itself.
        private const int MissedDamageFrames = 2;

        private int _terrainMask;

        /// <summary>
        /// Everything about one trip in progress. A new one is made for each
        /// trip, so nothing carries over from the last.
        /// </summary>
        private sealed class ActiveTrip
        {
            public readonly HeroController Hero;
            public readonly Rigidbody2D Body;
            public readonly Collider2D Collider;
            public readonly tk2dSpriteAnimator Anim;

            public tk2dSpriteAnimationClip FallClip;
            public tk2dSpriteAnimationClip LyingClip;
            public tk2dSpriteAnimationClip RiseClip;

            // Control hand-offs (see the control hooks in StumbleMod.cs).
            public bool TakingControl;    // the trip's own RelinquishControl call is in progress
            public bool ExternalHold;     // a cutscene or other sequence has taken control
            public bool RestartLieClock;  // control was handed back: restart MinLieTime

            // Movement.
            public float SlideStartVx;    // running speed at the moment of the trip
            public bool SlideStopped;     // the slide stopped at a ledge
            public float TripTime;        // seconds since the trip started
            public bool Grounded;         // this frame's ground check
            public float StuckInAirTime;
            public int DamageOverlapFrames;

            // Impact and landings (see StumbleEffects.cs).
            public bool Impacted;         // the body has hit the floor
            public float TimeDown;        // seconds since the impact; MinLieTime uses this
            public bool ThudPlayed;       // this trip's thud has played (the mod's or the game's)
            public float AirTime;         // seconds in the air in the current drop
            public bool GameSawFlight;    // the game noticed the Knight leave the ground
            public bool GameHardLanding;  // the game will play its heavy landing
            public float LandingWait;     // time spent waiting for the game to register a landing

            // Areas the Knight was already in when the trip started.
            public readonly HashSet<Collider2D> TriggersAtStart = new HashSet<Collider2D>();

            public ActiveTrip(HeroController hero, Rigidbody2D body, Collider2D collider, tk2dSpriteAnimator anim)
            {
                Hero = hero;
                Body = body;
                Collider = collider;
                Anim = anim;
            }

            /// <summary>Resets the tracking for one drop through the air.</summary>
            public void EndFlight()
            {
                AirTime = 0f;
                GameSawFlight = false;
                GameHardLanding = false;
                LandingWait = 0f;
            }
        }

        // ---- The sequence --------------------------------------------------

        /// <summary>
        /// One trip, one step per frame. Whenever the trip has been ended
        /// (by the routine itself or from outside, through EndTrip), the
        /// routine sees _trip has changed and stops without touching anything.
        /// </summary>
        private IEnumerator TripRoutine(ActiveTrip t)
        {
            HeroController hc = t.Hero;

            // Remember the running speed before taking control away, so the
            // Knight keeps sliding forward through the fall.
            t.SlideStartVx = t.Body.velocity.x;

            // Note which areas the Knight is already in, so the ones they slide
            // or fall into during the trip can be re-entered when they stand up.
            foreach (Collider2D col in OverlappingTriggers(t)) t.TriggersAtStart.Add(col);

            t.TakingControl = true;
            try { hc.RelinquishControl(); }
            finally { t.TakingControl = false; }
            hc.StopAnimationControl();
            ApplyTripMotion(t);

            // Fall over.
            if (t.FallClip != null)
            {
                t.Anim.Play(t.FallClip);
                float duration = Mathf.Min(ClipLength(t.FallClip), MaxFallTime);

                // The last frames of the fall already show the Knight on the
                // floor, so the thud goes off a few frames before the end.
                float thudAt = t.FallClip.fps > 0f
                    ? Mathf.Max(0f, duration - _settings.ThudFramesEarly / t.FallClip.fps)
                    : duration;

                float elapsed = 0f;
                float mapAwayTime = 0f;
                while (elapsed < duration)
                {
                    if (!TickDown(t)) yield break;

                    // Tripped while walking with the map out: the game plays
                    // its own put-the-map-away animation over the fall. Let it
                    // finish (cutting it short can leave the map half-closed),
                    // then fall from the start. The fall's clock waits too.
                    if (t.Anim.CurrentClip != t.FallClip)
                    {
                        mapAwayTime += Time.deltaTime;
                        if (t.Anim.Playing && IsMapClip(t.Anim.CurrentClip) && mapAwayTime < MaxMapAwayWait)
                        {
                            AdvanceClocks(t);
                            yield return null;
                            continue;
                        }
                        t.Anim.Play(t.FallClip);
                        elapsed = 0f;
                    }

                    if (elapsed >= thudAt && !t.Impacted && t.Grounded) Impact(t);
                    AdvanceClocks(t);
                    yield return null;
                    elapsed += Time.deltaTime;
                }
                if (_trip != t) yield break;
            }

            // Lie there until the player tries to move again. Any slide left
            // over from the fall finishes here, and if the Knight went over a
            // ledge they keep falling in this pose until they land.
            t.Anim.Play(t.LyingClip);

            // The body hits the floor, if it hasn't already. If the Knight is
            // in mid-air here (went over a ledge), the impact is their landing
            // instead, handled in TrackLanding, and one impact never thuds twice.
            if (!t.Impacted && IsGrounded(t)) Impact(t);

            while (true)
            {
                if (!TickDown(t)) yield break;
                AdvanceClocks(t);

                // A cutscene just handed control back: the Knight is still
                // down, back in the lying pose, and MinLieTime starts now.
                if (t.RestartLieClock)
                {
                    t.RestartLieClock = false;
                    t.TimeDown = 0f;
                    t.Anim.Play(t.LyingClip);
                }

                // Something else played over the lying pose. Let the map
                // finish being put away, then lie back down.
                if (t.Anim.CurrentClip != t.LyingClip
                    && !(t.Anim.Playing && IsMapClip(t.Anim.CurrentClip)))
                    t.Anim.Play(t.LyingClip);

                // Only stand up from solid ground, and never mid-cutscene.
                // WasPressed (not IsPressed) so holding the direction you were
                // running in doesn't instantly stand you back up.
                if (!t.ExternalHold && t.TimeDown >= _settings.MinLieTime && t.Grounded
                    && MovePressedThisFrame()) break;

                yield return null;
            }

            // Get back up.
            if (t.RiseClip != null)
            {
                t.Anim.Play(t.RiseClip);
                float duration = Mathf.Min(ClipLength(t.RiseClip), MaxRiseTime);
                for (float elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
                {
                    if (_trip != t || HandleInterruption(t)) yield break;
                    if (ApplyMissedDamage(t)) yield break;
                    t.Body.velocity = new Vector2(0f, t.Body.velocity.y);
                    yield return null;
                }
                if (_trip != t) yield break;
            }

            // A cutscene that started while they were getting up keeps the Knight
            // until it's done; control comes back when it ends.
            while (t.ExternalHold)
            {
                if (_trip != t || HandleInterruption(t)) yield break;
                yield return null;
            }
            if (_trip != t) yield break;

            EndTrip(t, TripEnd.StandUp);
            ReenterNewAreas(t);
        }

        /// <summary>
        /// One frame while the Knight is falling over or lying down: every
        /// check that can end the trip early, then the slide and the landing
        /// tracking. Returns false if the trip has ended. New per-frame
        /// checks go here, so the fall and lying stages both get them.
        /// </summary>
        private bool TickDown(ActiveTrip t)
        {
            if (_trip != t || HandleInterruption(t)) return false;

            t.Grounded = IsGrounded(t);
            if (StuckInAir(t) || EnteringWater(t) || ApplyMissedDamage(t)) return false;

            ApplyTripMotion(t);
            TrackLanding(t);
            if (_settings.DetailedLogging) LogMenuFsmChanges(t);
            return true;
        }

        private static void AdvanceClocks(ActiveTrip t)
        {
            t.TripTime += Time.deltaTime;
            if (t.Impacted) t.TimeDown += Time.deltaTime;
        }

        // ---- Reasons a trip ends early -------------------------------------

        /// <summary>
        /// The game taking over (death, hazard respawn, room transition) or a
        /// hit the damage hook didn't catch. Returns true if the trip ended.
        /// </summary>
        private bool HandleInterruption(ActiveTrip t)
        {
            HeroControllerStates s = t.Hero.cState;
            if (s.dead || s.hazardDeath || s.hazardRespawning || s.transitioning)
            {
                EndTrip(t, TripEnd.GameTookOver);
                return true;
            }
            if (s.recoiling)
            {
                // Let the normal damage/recoil logic run.
                EndTrip(t, TripEnd.StandUp);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Last-resort safety: if the Knight is off the ground but not falling
        /// for a while, something is holding them in mid-air. Hand control
        /// back so the game's own physics takes over. Returns true if the trip
        /// ended.
        /// </summary>
        private bool StuckInAir(ActiveTrip t)
        {
            if (t.Grounded || Mathf.Abs(t.Body.velocity.y) > 0.05f)
            {
                t.StuckInAirTime = 0f;
                return false;
            }

            t.StuckInAirTime += Time.deltaTime;
            if (t.StuckInAirTime < MaxStuckInAirTime) return false;

            LogWarn("Knight was held in mid-air during a trip; handing control back.");
            EndTrip(t, TripEnd.StandUp);
            return true;
        }

        /// <summary>
        /// Ends the trip just before the Knight falls into water (or acid,
        /// with Isma's Tear), so the game's own swimming takes over with full
        /// control and normal buoyancy. Tripping while standing in shallow
        /// water, or landing on a platform above it, is unaffected. Returns
        /// true if the trip ended.
        /// </summary>
        private bool EnteringWater(ActiveTrip t)
        {
            if (t.Grounded) return false;

            // Look a little below the Knight, further the faster they fall.
            Bounds b = t.Collider.bounds;
            float reach = WaterLookahead + Mathf.Max(0f, -t.Body.velocity.y) * Time.deltaTime * 2f;
            Vector2 center = new Vector2(b.center.x, b.center.y - reach / 2f);
            Vector2 size = new Vector2(b.size.x, b.size.y + reach);

            foreach (Collider2D col in Physics2D.OverlapBoxAll(center, size, 0f))
            {
                if (col == null || !col.isTrigger || !IsWater(col)) continue;

                // A platform between the Knight and the surface catches them
                // first, so they never reach the water.
                if (GroundAboveWater(b, reach, col.bounds.max.y)) continue;

                Log($"Trip ended just before entering water ({col.gameObject.name}).");
                EndTrip(t, TripEnd.StandUp);
                return true;
            }
            return false;
        }

        /// <summary>
        /// True if there's solid ground under the Knight's feet that sits at
        /// or above the water's surface, within the look-ahead distance.
        /// </summary>
        private bool GroundAboveWater(Bounds body, float reach, float waterTop)
        {
            float y = body.min.y + 0.05f;
            float inset = Mathf.Min(0.05f, body.extents.x);
            foreach (float x in new[] { body.min.x + inset, body.center.x, body.max.x - inset })
            {
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, y), Vector2.down, reach + 0.05f, TerrainMask);
                if (hit.collider != null && hit.point.y >= waterTop - 0.05f) return true;
            }
            return false;
        }

        private static bool IsWater(Collider2D col)
        {
            if (col.GetComponent("SurfaceWaterRegion") != null) return true;

            string name = col.gameObject.name.ToLowerInvariant();
            if (name.Contains("water")) return true;

            // Acid is a hazard until Isma's Tear makes it swimmable.
            return name.Contains("acid") && PlayerData.instance != null
                && PlayerData.instance.GetBool("hasAcidArmour");
        }

        /// <summary>
        /// Safety net for damage the game misses while the Knight is down: if
        /// they overlap something that should hurt them for a couple of frames
        /// and the game hasn't reacted, apply that damage the way the game
        /// would. Anything the game already handles ends the trip first, so
        /// damage is never applied twice. Returns true if the trip ended.
        /// </summary>
        private bool ApplyMissedDamage(ActiveTrip t)
        {
            Bounds b = t.Collider.bounds;
            DamageHero source = null;
            Collider2D sourceCol = null;
            foreach (Collider2D col in Physics2D.OverlapBoxAll(b.center, b.size, 0f))
            {
                if (col == null || !col.enabled) continue;
                DamageHero dh = col.GetComponent<DamageHero>();
                if (dh == null || !dh.enabled || dh.damageDealt <= 0) continue;
                source = dh;
                sourceCol = col;
                break;
            }

            if (source == null)
            {
                t.DamageOverlapFrames = 0;
                return false;
            }

            if (++t.DamageOverlapFrames < MissedDamageFrames) return false;
            t.DamageOverlapFrames = 0;

            HeroControllerStates s = t.Hero.cState;
            if (s.invulnerable || s.recoiling || s.dead || s.hazardDeath || s.hazardRespawning) return false;

            CollisionSide side = sourceCol.bounds.center.y > b.center.y ? CollisionSide.top
                : sourceCol.bounds.center.y < b.min.y ? CollisionSide.bottom
                : sourceCol.bounds.center.x < b.center.x ? CollisionSide.left
                : CollisionSide.right;

            Log($"Applied damage the game missed during a trip ({source.gameObject.name}).");
            t.Hero.TakeDamage(source.gameObject, side, source.damageDealt, source.hazardType);

            // A normal hit ends the trip through the damage hook (OnAfterTakeDamage).
            return _trip != t;
        }

        // ---- Movement ------------------------------------------------------

        /// <summary>
        /// Sets the Knight's velocity for one frame of a trip: the forward
        /// slide horizontally. Vertical speed is only capped, never changed
        /// otherwise, and gravity is left to the game, so water, acid and
        /// other areas that change how the Knight falls keep working.
        /// </summary>
        private void ApplyTripMotion(ActiveTrip t)
        {
            float vy = Mathf.Max(t.Body.velocity.y, -MaxFallSpeed);
            t.Body.velocity = new Vector2(SlideVelocity(t), vy);
        }

        /// <summary>
        /// Forward speed during a trip: the running speed at the moment of the
        /// trip, easing down to zero over SlideTime seconds. With
        /// TumbleOffEdges off, the slide stops for good at a ledge.
        /// </summary>
        private float SlideVelocity(ActiveTrip t)
        {
            if (t.SlideStopped || _settings.SlideTime <= 0f || Mathf.Approximately(t.SlideStartVx, 0f))
                return 0f;

            float v = t.SlideStartVx * Mathf.Clamp01(1f - t.TripTime / _settings.SlideTime);
            if (Mathf.Approximately(v, 0f)) return 0f;

            if (!_settings.TumbleOffEdges && !GroundAhead(t, Mathf.Sign(v)))
            {
                t.SlideStopped = true;
                return 0f;
            }
            return v;
        }

        // ---- Ground and area checks ----------------------------------------

        /// <summary>
        /// The mod's own "standing on something" check: rays down from the
        /// left, middle and right of the Knight's feet. Used instead of the
        /// game's flag, which may not update while control is taken away.
        /// </summary>
        private bool IsGrounded(ActiveTrip t)
        {
            Bounds b = t.Collider.bounds;
            float y = b.min.y + 0.05f;
            float inset = Mathf.Min(0.05f, b.extents.x);
            foreach (float x in new[] { b.min.x + inset, b.center.x, b.max.x - inset })
            {
                if (Physics2D.Raycast(new Vector2(x, y), Vector2.down, 0.15f, TerrainMask).collider != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True if there's ground just ahead of the Knight's middle. Lets the
        /// Knight slide until they're partly over a ledge (still supported), then
        /// stops them there.
        /// </summary>
        private bool GroundAhead(ActiveTrip t, float direction)
        {
            Bounds b = t.Collider.bounds;
            Vector2 origin = new Vector2(b.center.x + direction * EdgeLookahead, b.min.y + 0.1f);
            return Physics2D.Raycast(origin, Vector2.down, 0.6f, TerrainMask).collider != null;
        }

        private int TerrainMask
        {
            get
            {
                if (_terrainMask == 0)
                {
                    _terrainMask = LayerMask.GetMask("Terrain");
                    if (_terrainMask == 0) _terrainMask = 1 << 8; // Hollow Knight's terrain layer
                }
                return _terrainMask;
            }
        }

        /// <summary>
        /// Trigger areas the Knight currently overlaps that can actually
        /// detect them (same rules the physics engine uses), not counting the
        /// Knight's own triggers.
        /// </summary>
        private static List<Collider2D> OverlappingTriggers(ActiveTrip t)
        {
            var result = new List<Collider2D>();
            Bounds b = t.Collider.bounds;
            int heroLayer = t.Hero.gameObject.layer;
            foreach (Collider2D col in Physics2D.OverlapBoxAll(b.center, b.size, 0f))
            {
                if (col == null || !col.enabled || !col.isTrigger) continue;
                if (col.transform.IsChildOf(t.Hero.transform)) continue;
                if (Physics2D.GetIgnoreLayerCollision(heroLayer, col.gameObject.layer)) continue;
                result.Add(col);
            }
            return result;
        }

        /// <summary>
        /// Areas the game may have ignored the Knight entering while they were
        /// tripped (hidden alcoves that light up, and the like) get told they
        /// left and came back in, now that they have normal control. Anything
        /// that handled the first entry fine ends up where it was: the "left"
        /// undoes it and the "entered" redoes it. Room exits and things that
        /// deal damage are left alone.
        /// </summary>
        private void ReenterNewAreas(ActiveTrip t)
        {
            if (t.Hero == null) return;

            var names = new List<string>();
            foreach (Collider2D col in OverlappingTriggers(t))
            {
                if (t.TriggersAtStart.Contains(col)) continue;
                if (col.GetComponent("TransitionPoint") != null) continue;
                if (col.GetComponent<DamageHero>() != null) continue;

                // Unity delivers trigger events to the collider's object and,
                // if different, the object holding its rigidbody.
                var targets = new List<GameObject> { col.gameObject };
                if (col.attachedRigidbody != null && col.attachedRigidbody.gameObject != col.gameObject)
                    targets.Add(col.attachedRigidbody.gameObject);

                foreach (GameObject go in targets)
                {
                    try
                    {
                        go.SendMessage("OnTriggerExit2D", t.Collider, SendMessageOptions.DontRequireReceiver);
                        go.SendMessage("OnTriggerEnter2D", t.Collider, SendMessageOptions.DontRequireReceiver);
                    }
                    catch (System.Exception e)
                    {
                        LogWarn($"Re-entering {go.name} after a trip failed: {e.Message}");
                    }
                }
                names.Add(col.gameObject.name);
            }

            if (names.Count > 0)
                LogDetail("Re-entered areas after standing up: " + string.Join(", ", names.ToArray()));
        }

        // ---- Small helpers -------------------------------------------------

        private static float ClipLength(tk2dSpriteAnimationClip clip) =>
            clip.fps > 0f ? clip.frames.Length / clip.fps : 0f;

        /// <summary>One of the Knight's map animations (opening, walking with or putting away the map).</summary>
        private static bool IsMapClip(tk2dSpriteAnimationClip clip) =>
            clip != null && clip.name != null && clip.name.ToLowerInvariant().Contains("map");

        private static bool MovePressedThisFrame()
        {
            HeroActions actions = InputHandler.Instance.inputActions;
            return actions.left.WasPressed || actions.right.WasPressed;
        }
    }
}
