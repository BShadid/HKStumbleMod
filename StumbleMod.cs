using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Modding;
using UnityEngine;

namespace Stumble
{
    /// <summary>
    /// Every so often while the Knight is running on the ground, they trip:
    /// they fall over, slide, and lie flat until the player presses left or
    /// right, then get back up. How often is set by Knight Clumsiness.
    ///
    /// The mod is split across files by topic:
    ///   StumbleMod.cs         start-up, game hooks, starting and ending a trip
    ///   StumbleSequence.cs    one trip from start to finish, and its safety checks
    ///   StumbleEffects.cs     the fall animation, sounds and dust
    ///   StumbleDiagnostics.cs log output for troubleshooting
    ///   StumbleSlip.cs        wall slips and the effect where the grip goes
    ///   StumbleMenu.cs        the in-game options menu
    ///   StumbleSettings.cs    the settings and their defaults
    /// </summary>
    public partial class StumbleMod : Mod, ITogglableMod, IGlobalSettings<StumbleSettings>, IMenuMod
    {
        private StumbleSettings _settings = new StumbleSettings();

        // The trip in progress, or null. Everything about one trip lives in
        // this object, so each trip starts fresh and ending one is one step.
        private ActiveTrip _trip;

        public StumbleMod() : base("Stumble") { }

        public override string GetVersion() => "0.2.0.0";

        // ---- Lifecycle -----------------------------------------------------

        /// <summary>
        /// Objects for the Modding API to load from other areas at start-up,
        /// behind the loading screen: the tinkable spikes whose spark plays on
        /// a wall slip (see StumbleSlip.cs).
        /// </summary>
        public override List<(string, string)> GetPreloadNames()
        {
            var preloads = new List<(string, string)>();
            string scene = TinkSourceScene();
            if (scene != null) preloads.Add((scene, TinkSourcePath));
            return preloads;
        }

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            TakeTinkSparkFromPreload(preloadedObjects);
            Initialize();
        }

        public override void Initialize()
        {
            // Unsubscribe first, guarding against a double subscription if the
            // mod is toggled back on.
            ModHooks.HeroUpdateHook -= OnHeroUpdate;
            ModHooks.HeroUpdateHook += OnHeroUpdate;
            ModHooks.AfterTakeDamageHook -= OnAfterTakeDamage;
            ModHooks.AfterTakeDamageHook += OnAfterTakeDamage;
            ModHooks.BeforeSceneLoadHook -= OnBeforeSceneLoad;
            ModHooks.BeforeSceneLoadHook += OnBeforeSceneLoad;
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;
            InstallGrabHooks();
            On.HeroController.RelinquishControl -= OnRelinquishControl;
            On.HeroController.RelinquishControl += OnRelinquishControl;
            On.HeroController.RegainControl -= OnRegainControl;
            On.HeroController.RegainControl += OnRegainControl;
            On.HeroController.StartAnimationControl -= OnStartAnimationControl;
            On.HeroController.StartAnimationControl += OnStartAnimationControl;
            On.HeroController.CanQuickMap -= OnCanQuickMap;
            On.HeroController.CanQuickMap += OnCanQuickMap;
            On.HeroController.CanOpenInventory -= OnCanOpenInventory;
            On.HeroController.CanOpenInventory += OnCanOpenInventory;
        }

        public void Unload()
        {
            ModHooks.HeroUpdateHook -= OnHeroUpdate;
            ModHooks.AfterTakeDamageHook -= OnAfterTakeDamage;
            ModHooks.BeforeSceneLoadHook -= OnBeforeSceneLoad;
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            On.HeroController.RelinquishControl -= OnRelinquishControl;
            On.HeroController.RegainControl -= OnRegainControl;
            On.HeroController.StartAnimationControl -= OnStartAnimationControl;
            On.HeroController.CanQuickMap -= OnCanQuickMap;
            On.HeroController.CanOpenInventory -= OnCanOpenInventory;

            // Switched off mid-trip: hand the Knight back. Any no-grab window
            // after a wall slip ends too.
            EndTrip(_trip, TripEnd.StandUp);
            EndGrabLockout();
            RemoveGrabHooks();
            SetMenuButtonsBlocked(false);
        }

        // ---- Settings ------------------------------------------------------

        public void OnLoadGlobal(StumbleSettings s)
        {
            _settings = s ?? new StumbleSettings();
            _settings.Validate();
        }

        public StumbleSettings OnSaveGlobal() => _settings;

        // ---- Starting a trip or a slip --------------------------------------

        private void OnHeroUpdate()
        {
            HeroController hc = HeroController.instance;
            if (hc == null) return;

            // Tracked every frame, so a dash's start and end are never missed.
            bool groundDashJustEnded = TrackDash(hc);

            // A trip whose Knight was destroyed (quit to menu, etc.) died with them.
            if (_trip != null && _trip.Hero == null)
            {
                _trip = null;
                SetMenuButtonsBlocked(false);
            }
            if (_trip != null) return;

            // The no-grab window after a wall slip: ends on landing or when time is up.
            UpdateGrabLockout(hc);

            // Catching a wall: one roll for a slip, however briefly they touch it.
            if (WallContactBegan(hc) && Random.value < _settings.GrabSlipChance())
            {
                StartSlip(hc);
                return;
            }

            // Clinging to a wall: roll for a slip instead of a trip.
            if (IsClingingToWall(hc))
            {
                if (SlipRollSucceeds()) StartSlip(hc);
                return;
            }

            // A dash along the ground just finished: one roll for a stumble.
            if (groundDashJustEnded && CanTripAfterDash(hc))
            {
                if (Random.value < _settings.DashTripChance()) StartTrip(hc);
                return;
            }

            if (!IsRunningOnGround(hc) && !IsMapWalking(hc))
            {
                if (_settings.DetailedLogging) LogMapWalkMissOnce(hc);
                return;
            }
            if (!TripRollSucceeds()) return;

            StartTrip(hc);
        }

        // ---- Stumbling out of a dash ---------------------------------------

        private bool _wasDashing;
        private bool _dashStartedOnGround;

        /// <summary>
        /// Watches the dash (Mothwing or Shade Cloak) start and end. Returns
        /// true on the frame a dash that started on the ground finishes with
        /// the Knight still on the ground.
        /// </summary>
        private bool TrackDash(HeroController hc)
        {
            bool dashing = hc.cState.dashing;
            bool groundDashEnded = false;

            if (dashing && !_wasDashing) _dashStartedOnGround = hc.cState.onGround;
            else if (!dashing && _wasDashing) groundDashEnded = _dashStartedOnGround && hc.cState.onGround;

            _wasDashing = dashing;
            return groundDashEnded;
        }

        /// <summary>
        /// Whether a trip can start right after a dash: on the ground, with the
        /// player in control, and nothing else going on.
        /// </summary>
        private static bool CanTripAfterDash(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            return hc.acceptingInput
                && !hc.controlReqlinquished
                && s.onGround
                && !s.focusing
                && !s.recoiling
                && !s.transitioning
                && !s.dead
                && !s.hazardDeath
                && !s.hazardRespawning
                && !s.isPaused;
        }

        /// <summary>
        /// This frame's roll. The chance is scaled to how long the frame took,
        /// so trips average one per AverageTripSeconds() of running (set by the
        /// clumsiness level) at any frame rate: more frames per second means a
        /// smaller chance on each. A long frame (a stutter) gets a
        /// proportionally bigger chance.
        /// </summary>
        private bool TripRollSucceeds()
        {
            float chance = 1f - Mathf.Exp(-Time.deltaTime / _settings.AverageTripSeconds());
            return Random.value < chance;
        }

        private static bool IsRunningOnGround(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            return hc.acceptingInput
                && !hc.controlReqlinquished
                && hc.hero_state == ActorStates.running
                && s.onGround
                && !s.dashing
                && !s.focusing // includes Shape of Unn's slug form
                && !s.recoiling
                && !s.transitioning
                && !s.dead
                && !s.hazardDeath
                && !s.hazardRespawning
                && !s.isPaused;
        }

        /// <summary>
        /// Walking with the quick map out. The map takes over how the Knight
        /// moves, so they don't count as running, but they're on the ground,
        /// moving, with their nose in a map. Recognised by the map-walking
        /// animation playing (any animation with "map" in its name) while they
        /// move along the ground. A trip switches the map button off, so they
        /// drop the map.
        /// </summary>
        private static bool IsMapWalking(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            if (!s.onGround || s.dashing || s.focusing || s.recoiling || s.transitioning
                || s.dead || s.hazardDeath || s.hazardRespawning || s.isPaused) return false;

            tk2dSpriteAnimator anim = hc.GetComponent<tk2dSpriteAnimator>();
            if (anim == null || !IsMapClip(anim.CurrentClip)) return false;

            Rigidbody2D body = hc.GetComponent<Rigidbody2D>();
            return body != null && Mathf.Abs(body.velocity.x) > 0.1f;
        }

        private void StartTrip(HeroController hc)
        {
            tk2dSpriteAnimator anim = hc.GetComponent<tk2dSpriteAnimator>();
            Rigidbody2D body = hc.GetComponent<Rigidbody2D>();
            Collider2D collider = hc.GetComponent<Collider2D>();
            if (anim == null || body == null || collider == null) return;

            tk2dSpriteAnimationClip lying = anim.GetClipByName(_settings.LyingClip);
            if (lying == null)
            {
                LogClipNamesOnce(anim, _settings.LyingClip);
                return;
            }

            tk2dSpriteAnimationClip rise = null;
            if (!string.IsNullOrEmpty(_settings.RiseClip))
            {
                rise = anim.GetClipByName(_settings.RiseClip);
                if (rise == null) LogClipNamesOnce(anim, _settings.RiseClip);
            }

            var trip = new ActiveTrip(hc, body, collider, anim)
            {
                LyingClip = lying,
                RiseClip = rise,
                FallClip = _settings.FallAnimation ? GetFallClip(rise) : null
            };
            _trip = trip;
            SetMenuButtonsBlocked(true);
            ResetMenuFsmTracking();
            hc.StartCoroutine(TripRoutine(trip));
        }

        // ---- Ending a trip -------------------------------------------------

        private enum TripEnd
        {
            /// <summary>Hand control back: standing up, a hit, water, the mod switched off.</summary>
            StandUp,

            /// <summary>The game is taking over: room transition, death, hazard respawn, warp.</summary>
            GameTookOver
        }

        /// <summary>
        /// Ends a trip. The only place a trip ends, whatever the reason. The
        /// trip's routine notices on its next step and stops by itself.
        /// Does nothing if this trip has already ended.
        /// </summary>
        private void EndTrip(ActiveTrip trip, TripEnd how)
        {
            if (trip == null || _trip != trip) return;

            // Clear the trip first, so the control hooks let the calls below through.
            _trip = null;
            SetMenuButtonsBlocked(false);

            HeroController hc = trip.Hero;
            if (hc == null) return;
            hc.StartAnimationControl();

            // A cutscene or the death sequence took control during the trip:
            // it hands control back itself when it's done.
            if (trip.ExternalHold) return;

            if (how == TripEnd.StandUp)
            {
                hc.RegainControl();
            }
            else
            {
                // Put the Knight back the way they'd be if they had never tripped,
                // then step aside. The game won't return the control the trip
                // took (it doesn't know about the trip), and a Knight left that
                // way can't attack, falls with no speed limit and can't trip.
                // RegainControl would start them moving mid-sequence, so only
                // the trip's hold is dropped.
                hc.controlReqlinquished = false;
            }
        }

        /// <summary>
        /// Runs the moment a hit lands (after the game's invincibility checks,
        /// before damage and knockback are applied). If the Knight is tripped,
        /// control goes back right now, so the game's knockback plays out
        /// exactly as it would otherwise. Hazards
        /// (spikes, acid, pits) are left to the hazard respawn, which takes
        /// control itself.
        /// </summary>
        private int OnAfterTakeDamage(int hazardType, int damageAmount)
        {
            if (hazardType <= 1) EndTrip(_trip, TripEnd.StandUp);
            return damageAmount;
        }

        /// <summary>
        /// Any scene load while tripped (a warp, for example from the
        /// Benchwarp mod, if it skips the usual room-transition route) ends
        /// the trip the same way a room transition does.
        /// </summary>
        private string OnBeforeSceneLoad(string sceneName)
        {
            if (_trip != null)
            {
                EndTrip(_trip, TripEnd.GameTookOver);
                Log($"Trip ended by a scene load ({sceneName}).");
            }
            EndGrabLockout();
            return sceneName;
        }

        // ---- Control hand-offs during a trip -------------------------------
        //
        // Cutscenes (boss intros, auto-starting dialogue) take control of the
        // Knight when they start and hand it back when they end, with no idea
        // a trip is going on. While tripped, the mod keeps hold of the Knight
        // through both: the Knight can't stand up during the cutscene, and when it
        // ends they're still down, with MinLieTime counted from that moment.
        // EndTrip clears _trip before its own calls, and the trip's own
        // RelinquishControl sets TakingControl, so those pass straight through.

        private void OnRelinquishControl(On.HeroController.orig_RelinquishControl orig, HeroController self)
        {
            if (_trip != null && !_trip.TakingControl) _trip.ExternalHold = true;
            orig(self);
        }

        private void OnRegainControl(On.HeroController.orig_RegainControl orig, HeroController self)
        {
            if (_trip != null)
            {
                // Something else is done with the Knight, but the trip still
                // has them: keep control taken.
                _trip.ExternalHold = false;
                _trip.RestartLieClock = true;
                return;
            }
            orig(self);
        }

        private void OnStartAnimationControl(On.HeroController.orig_StartAnimationControl orig, HeroController self)
        {
            // Keep the trip's animation; the game gets animation back when the
            // trip ends.
            if (_trip != null) return;
            orig(self);
        }

        // ---- Menus while down ----------------------------------------------
        //
        // While the Knight is down, the quick map, full map and inventory
        // shouldn't open. The game's map script reads the map button directly
        // rather than asking HeroController, so the buttons themselves are
        // switched off for the length of the trip and switched back on when
        // it ends. The pause menu isn't affected.
        //
        // The two HeroController checks are answered too, for anything that
        // does ask them.

        // The map and inventory buttons, by their names in the game's input
        // set. Looked up by name, so a name this game version doesn't have is
        // simply skipped.
        private static readonly string[] MenuButtonNames =
            { "quickMap", "openInventory", "openInventoryMap", "openInventoryJournal" };

        // Buttons this mod switched off, so exactly those are switched back on.
        private readonly List<object> _blockedButtons = new List<object>();
        private bool _warnedMenuButtons;

        private void SetMenuButtonsBlocked(bool blocked)
        {
            if (!blocked)
            {
                foreach (object button in _blockedButtons) SetButtonEnabled(button, true);
                _blockedButtons.Clear();
                return;
            }

            if (_blockedButtons.Count > 0) return;
            object actions = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
            if (actions == null) return;

            var missing = new List<string>();
            foreach (string name in MenuButtonNames)
            {
                FieldInfo field = actions.GetType().GetField(name);
                object button = field != null ? field.GetValue(actions) : null;
                if (button == null)
                {
                    missing.Add(name);
                    continue;
                }

                // Leave alone a button something else already switched off.
                if (!IsButtonEnabled(button)) continue;
                if (SetButtonEnabled(button, false)) _blockedButtons.Add(button);
                else missing.Add(name + " (can't be switched off)");
            }

            if (missing.Count > 0 && !_warnedMenuButtons)
            {
                _warnedMenuButtons = true;
                // Expected for names this game version doesn't have; a
                // "can't be switched off" entry means blocking doesn't work.
                LogWarn("Menu buttons not blocked during trips: " + string.Join(", ", missing.ToArray()));
            }
        }

        private static bool IsButtonEnabled(object button)
        {
            PropertyInfo enabled = button.GetType().GetProperty("Enabled");
            return enabled == null || (bool)enabled.GetValue(button, null);
        }

        private static bool SetButtonEnabled(object button, bool value)
        {
            PropertyInfo enabled = button.GetType().GetProperty("Enabled");
            if (enabled == null || !enabled.CanWrite) return false;
            enabled.SetValue(button, value, null);
            return true;
        }

        private bool OnCanQuickMap(On.HeroController.orig_CanQuickMap orig, HeroController self)
        {
            if (_trip != null) return false;
            return orig(self);
        }

        private bool OnCanOpenInventory(On.HeroController.orig_CanOpenInventory orig, HeroController self)
        {
            if (_trip != null) return false;
            return orig(self);
        }
    }
}
