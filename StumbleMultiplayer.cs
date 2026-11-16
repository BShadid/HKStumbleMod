using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Stumble
{
    /// <summary>The stages of a trip, as told to other players. Sent as a byte.</summary>
    internal enum TripPhase : byte
    {
        Up = 0,
        Falling = 1,
        Lying = 2,
        Rising = 3
    }

    /// <summary>
    /// What the rest of the mod sees of multiplayer. Uses no HKMP types, so
    /// this file loads fine without HKMP; the HKMP side is StumbleHkmp.cs.
    /// </summary>
    internal interface ICoopLink
    {
        /// <summary>Connected to an HKMP server.</summary>
        bool Connected { get; }

        /// <summary>Tells the others which stage of a trip this Knight is in.</summary>
        void SetPhase(TripPhase phase);

        /// <summary>Every frame: status updates, showing other players' trips, and helping up a fallen player within reach.</summary>
        void Tick(HeroController hc, bool canHelp);

        /// <summary>This Knight just struggled to get up: show it on the others' screens.</summary>
        void Struggled();
    }

    // Multiplayer Help-Ups. With HKMP installed, the option on and connected
    // to a server, a fallen Knight stays down for MultiplayerLieTime, unless
    // another player walks over and holds Up next to them for HelpUpTime. Off by default: HKMP only lets players join servers whose
    // networked add-ons match theirs, so turning it on means everyone on the
    // server needs it on too. Takes effect when the game starts.
    public partial class StumbleMod
    {
        private static ICoopLink _coop;
        private static bool _coopSetUp;

        /// <summary>At start-up: registers the HKMP add-on if the option is on and HKMP is installed.</summary>
        private void SetUpMultiplayer()
        {
            if (_coopSetUp) return;
            _coopSetUp = true;
            if (!_settings.MultiplayerHelpUps) return;

            if (!System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "HKMP"))
            {
                LogWarn("Multiplayer Help-Ups is on, but HKMP isn't installed, so it does nothing.");
                return;
            }

            try
            {
                _coop = ConnectToHkmp();
                Log("Multiplayer Help-Ups is on. Everyone on an HKMP server needs it on to connect.");
            }
            catch (System.Exception e)
            {
                LogWarn("Couldn't set up Multiplayer Help-Ups with HKMP: " + e.Message);
            }
        }

        // Kept in its own method so HKMP's DLL is only looked for when this runs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private ICoopLink ConnectToHkmp() => StumbleHkmp.Register(this);

        /// <summary>
        /// The shortest time on the ground before the Knight can get up alone:
        /// MultiplayerLieTime while connected to a multiplayer server (whether
        /// or not anyone is nearby), otherwise MinLieTime.
        /// </summary>
        private float MinLieTimeNow()
        {
            if (_coop != null && _coop.Connected)
                return Mathf.Max(_settings.MinLieTime, _settings.MultiplayerLieTime);
            return _settings.MinLieTime;
        }

        /// <summary>Standing, in control and not tripped: able to help someone up.</summary>
        private bool CanHelpNow(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            return _trip == null
                && hc.acceptingInput
                && !hc.controlReqlinquished
                && s.onGround
                && !s.dashing
                && !s.recoiling
                && !s.transitioning
                && !s.dead
                && !s.hazardDeath
                && !s.hazardRespawning
                && !s.isPaused;
        }

        private void TellOthers(TripPhase phase)
        {
            if (_coop != null) _coop.SetPhase(phase);
        }

        // ---- Called by the HKMP add-on ----

        internal float HelpUpTime => _settings.HelpUpTime;
        internal bool DetailedLoggingOn => _settings.DetailedLogging;
        internal void LogMultiplayer(string message) => Log(message);

        // Another player's trip, shown on their Knight here with the same
        // animations (and fall settings) as this game's own trips.
        internal tk2dSpriteAnimationClip RemoteFallClip(tk2dSpriteAnimator anim) =>
            _settings.FallAnimation ? GetFallClip(RemoteRiseClip(anim)) : null;

        internal tk2dSpriteAnimationClip RemoteLyingClip(tk2dSpriteAnimator anim) =>
            string.IsNullOrEmpty(_settings.LyingClip) ? null : anim.GetClipByName(_settings.LyingClip);

        internal tk2dSpriteAnimationClip RemoteRiseClip(tk2dSpriteAnimator anim) =>
            string.IsNullOrEmpty(_settings.RiseClip) ? null : anim.GetClipByName(_settings.RiseClip);

        /// <summary>
        /// Another player helped this Knight up: they stand as soon as they're
        /// on the ground. False if they aren't down.
        /// </summary>
        internal bool HelpedUpBy(string helper)
        {
            if (_trip == null) return false;
            _trip.HelpedUp = true;
            return true;
        }

        /// <summary>
        /// Another player started holding Up next to this Knight: while they
        /// hold, the Knight is lifted towards kneeling over that many seconds.
        /// Only while lying down. False if not.
        /// </summary>
        internal bool LiftStartedBy(ushort helperId, float seconds)
        {
            ActiveTrip t = _trip;
            if (t == null || !t.Lying || t.RiseClip == null) return false;
            tk2dSpriteAnimationClip lift = LiftClip(t.RiseClip, seconds);
            if (lift == null) return false;
            t.StruggleClip = null;
            t.LiftClip = lift;
            t.LiftHelper = helperId;
            t.Anim.Play(lift);
            return true;
        }

        /// <summary>That player let go before the Knight was up: they sink back down.</summary>
        internal void LiftStoppedBy(ushort helperId)
        {
            ActiveTrip t = _trip;
            if (t == null || t.LiftClip == null || t.LiftHelper != helperId) return;
            t.LiftClip = null;
            t.Anim.Play(t.LyingClip);
        }

        // ---- Struggling ----
        //
        // While waiting out MultiplayerLieTime, each left/right press makes the
        // Knight struggle to get up: they push partway up and flop back down,
        // on every screen. Other players see the same struggle once when they
        // walk within reach of a fallen Knight. It's how players learn a fallen
        // Knight can be (and wants to be) helped, without any prompt.

        /// <summary>True while the longer multiplayer wait applies, so presses struggle instead of standing.</summary>
        private bool CanStruggle => _coop != null && _coop.Connected;

        /// <summary>Plays a struggle on this Knight and shows it to the others. Ignored mid-struggle.</summary>
        private void StartStruggle(ActiveTrip t)
        {
            if (t.StruggleClip != null || t.LiftClip != null || t.RiseClip == null) return;
            tk2dSpriteAnimationClip struggle = StruggleClip(t.RiseClip);
            if (struggle == null) return;
            t.StruggleClip = struggle;
            t.Anim.Play(struggle);
            if (_coop != null) _coop.Struggled();
        }

        /// <summary>
        /// A failed attempt to get up: the stand-up from lying to a bit over
        /// halfway to the kneel, then back down, a little quicker than standing.
        /// </summary>
        internal tk2dSpriteAnimationClip StruggleClip(tk2dSpriteAnimationClip rise)
        {
            if (rise == null || rise.frames == null || rise.frames.Length < 3) return null;
            string key = $"struggle|{rise.GetHashCode()}";
            if (_liftClips.TryGetValue(key, out tk2dSpriteAnimationClip cached)) return cached;

            KneelRange(rise, out int kneelStart, out _);
            int peak = Mathf.Clamp(Mathf.RoundToInt(kneelStart * 0.6f), 1, rise.frames.Length - 1);
            var frames = new List<tk2dSpriteAnimationFrame>();
            for (int i = 0; i <= peak; i++) frames.Add(rise.frames[i]);
            for (int i = peak - 1; i >= 0; i--) frames.Add(rise.frames[i]);

            var clip = new tk2dSpriteAnimationClip
            {
                name = "Struggle",
                frames = frames.ToArray(),
                fps = rise.fps * 1.25f,
                loopStart = 0,
                wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
            };
            _liftClips[key] = clip;
            return clip;
        }

        // ---- Being lifted: the stand-up, split at the kneel ----
        //
        // The stand-up animation goes from lying, to kneeling (a held pose),
        // to standing. While a helper holds Up, the Knight plays the part up to
        // the kneel, stretched over the hold time, so they're halfway there
        // halfway through and kneeling when help arrives; the rest of the
        // stand-up follows from the kneel. Letting go drops them back to
        // lying. Built once per stand-up animation and hold time.

        private readonly Dictionary<string, tk2dSpriteAnimationClip> _liftClips =
            new Dictionary<string, tk2dSpriteAnimationClip>();

        /// <summary>The stand-up from lying to kneeling, lasting the given seconds.</summary>
        internal tk2dSpriteAnimationClip LiftClip(tk2dSpriteAnimationClip rise, float seconds)
        {
            if (rise == null || rise.frames == null || rise.frames.Length < 2) return null;
            seconds = Mathf.Max(seconds, 0.05f);
            string key = $"lift|{rise.GetHashCode()}|{seconds:0.000}";
            if (_liftClips.TryGetValue(key, out tk2dSpriteAnimationClip cached)) return cached;

            KneelRange(rise, out int kneelStart, out _);
            tk2dSpriteAnimationFrame[] frames = rise.frames.Take(kneelStart + 1).ToArray();
            var clip = new tk2dSpriteAnimationClip
            {
                name = "Help Lift",
                frames = frames,
                fps = frames.Length / seconds,
                loopStart = 0,
                wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
            };
            _liftClips[key] = clip;
            return clip;
        }

        /// <summary>The rest of the stand-up, from kneeling to standing, at normal speed.</summary>
        internal tk2dSpriteAnimationClip RiseFromKneelClip(tk2dSpriteAnimationClip rise)
        {
            if (rise == null || rise.frames == null || rise.frames.Length < 2) return null;
            string key = $"rise|{rise.GetHashCode()}";
            if (_liftClips.TryGetValue(key, out tk2dSpriteAnimationClip cached)) return cached;

            KneelRange(rise, out _, out int kneelEnd);
            var clip = new tk2dSpriteAnimationClip
            {
                name = "Help Rise",
                frames = rise.frames.Skip(kneelEnd).ToArray(),
                fps = rise.fps,
                loopStart = 0,
                wrapMode = tk2dSpriteAnimationClip.WrapMode.Once
            };
            _liftClips[key] = clip;
            return clip;
        }

        /// <summary>
        /// The kneel in the stand-up: the first run of repeated frames (a held
        /// pose). Without one, the middle frame.
        /// </summary>
        private static void KneelRange(tk2dSpriteAnimationClip rise, out int start, out int end)
        {
            tk2dSpriteAnimationFrame[] f = rise.frames;
            for (int i = 0; i < f.Length - 1; i++)
            {
                if (!SameSprite(f[i], f[i + 1])) continue;
                int j = i + 1;
                while (j < f.Length - 1 && SameSprite(f[j], f[j + 1])) j++;
                start = i;
                end = j;
                return;
            }
            start = end = f.Length / 2;
        }

        // ---- The Up button ----

        private static FieldInfo _upField;
        private static PropertyInfo _isPressed;
        private static bool _upLookedUp;

        /// <summary>Whether Up is held. Looked up by name, like the map button.</summary>
        internal bool UpHeld()
        {
            object actions = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
            if (actions == null) return false;

            if (!_upLookedUp)
            {
                _upLookedUp = true;
                _upField = actions.GetType().GetField("up");
                object button = _upField != null ? _upField.GetValue(actions) : null;
                _isPressed = button != null ? button.GetType().GetProperty("IsPressed") : null;
                if (_isPressed == null) LogWarn("Couldn't find the Up button, so you can't help other players up.");
            }
            if (_upField == null || _isPressed == null) return false;

            object up = _upField.GetValue(actions);
            return up != null && (bool)_isPressed.GetValue(up, null);
        }
    }
}
