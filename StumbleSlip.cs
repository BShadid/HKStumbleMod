using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Modding;
using UnityEngine;

namespace Stumble
{
    // Wall slips. With the Mantis Claw, the Knight can lose their grip each
    // time they catch a wall, and the longer they cling to one: they drop off the wall and can't grab a wall or
    // wall-jump again until they land or SlipNoGrabTime passes, whichever
    // comes first. For the first SlipNoDoubleJumpTime of that (the startle),
    // double jumps don't work either, so a jump pressed out of habit at the
    // moment of the slip doesn't turn into a Monarch Wings rescue. After that
    // they keep full control, so a deliberate double jump or a dash can still
    // save them. How often is set by Wall Clumsiness.
    public partial class StumbleMod
    {
        // The no-grab window after a slip: whose it is and when it ends.
        private HeroController _noGrabHero;
        private float _noGrabUntil;

        private bool GrabBlocked => _noGrabHero != null && Time.time < _noGrabUntil;

        // The startle at the start of the window: when it ends (0 once over).
        private float _startleUntil;

        private bool StartleActive => _noGrabHero != null && Time.time < _startleUntil;

        private static MethodInfo _cancelWallsliding;
        private static bool _cancelWallslidingLookedUp;
        private bool _warnedNoWallRelease;

        // ---- Rolling for and starting a slip -------------------------------

        // Wall contact, for the roll each time the Knight catches a wall.
        // Contact that drops out for less than WallContactRearm and comes back
        // is the same contact, so a flicker doesn't count as a new catch.
        private bool _wasTouchingWall;
        private float _wallContactEndedAt = -1f;
        private const float WallContactRearm = 0.1f;

        /// <summary>
        /// True on the frame the Knight, in the air with the Mantis Claw and
        /// the player in control, starts touching a wall they could climb.
        /// Counts wall-jump climbs, where they touch each wall too briefly to
        /// start wall-sliding. Not while dashing (the contact counts when the
        /// dash ends) or during the no-grab window.
        /// </summary>
        private bool WallContactBegan(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            bool touching = s.touchingWall
                && !s.touchingNonSlider
                && !s.onGround
                && !s.dashing
                && !s.superDashing
                && hc.acceptingInput
                && !hc.controlReqlinquished
                && !s.recoiling
                && !s.transitioning
                && !s.dead
                && !s.hazardDeath
                && !s.hazardRespawning
                && !s.isPaused
                && PlayerData.instance != null
                && PlayerData.instance.GetBool("hasWalljump");

            bool began = touching && !_wasTouchingWall
                && Time.time - _wallContactEndedAt >= WallContactRearm;
            if (!touching && _wasTouchingWall) _wallContactEndedAt = Time.time;
            _wasTouchingWall = touching;
            return began && !GrabBlocked;
        }

        /// <summary>Clinging to a wall, with the player in control.</summary>
        private static bool IsClingingToWall(HeroController hc)
        {
            HeroControllerStates s = hc.cState;
            return s.wallSliding
                && hc.acceptingInput
                && !hc.controlReqlinquished
                && !s.recoiling
                && !s.transitioning
                && !s.dead
                && !s.hazardDeath
                && !s.hazardRespawning
                && !s.isPaused;
        }

        /// <summary>
        /// This frame's slip roll. Like the trip roll, the chance is scaled to
        /// how long the frame took, so slips average one per
        /// AverageSlipSeconds() of clinging at any frame rate.
        /// </summary>
        private bool SlipRollSucceeds()
        {
            float average = _settings.AverageSlipSeconds();
            if (average <= 0f) return false; // Wall Clumsiness is Off
            float chance = 1f - Mathf.Exp(-Time.deltaTime / average);
            return Random.value < chance;
        }

        /// <summary>
        /// Lets go of the wall and starts the no-grab window. The Knight keeps
        /// control and falls under normal gravity.
        /// </summary>
        private void StartSlip(HeroController hc)
        {
            int wallSide = WallSide(hc);
            if (!LetGoOfWall(hc)) return;
            ResetAirMoves(hc);
            ShowSlipEffect(hc, wallSide);

            _noGrabHero = hc;
            _noGrabUntil = Time.time + _settings.SlipNoGrabTime;
            float startle = Mathf.Min(_settings.SlipNoDoubleJumpTime, _settings.SlipNoGrabTime);
            _startleUntil = startle > 0f ? Time.time + startle : 0f;

            if (_settings.DetailedLogging) UpdateSoundCatalogue();
            PlayNamedSound(hc, _settings.SlipSound);
        }

        /// <summary>
        /// Every frame: ends the no-grab window once time is up or the Knight
        /// lands, dies, respawns or changes room. During the startle (and on
        /// the first frame after it), forgets any double jump the game is
        /// holding on to from a press, so it can't fire the moment the startle
        /// ends. If the game's grab checks couldn't be hooked, lets go of any
        /// wall grabbed during the window instead.
        /// </summary>
        private void UpdateGrabLockout(HeroController hc)
        {
            if (_noGrabHero == null) return;
            if (_noGrabHero != hc || !GrabBlocked)
            {
                EndGrabLockout();
                return;
            }

            HeroControllerStates s = hc.cState;
            if (s.onGround || s.dead || s.hazardDeath || s.hazardRespawning || s.transitioning)
            {
                EndGrabLockout();
                return;
            }

            if (_startleUntil > 0f)
            {
                ForgetQueuedDoubleJump(hc);
                if (Time.time >= _startleUntil) _startleUntil = 0f;
            }

            if (_grabHooksMissing && s.wallSliding) LetGoOfWall(hc);
        }

        private void EndGrabLockout()
        {
            _noGrabHero = null;
            _noGrabUntil = 0f;
            _startleUntil = 0f;
        }

        // The game remembers a jump pressed when it can't be used, and does it
        // a few frames later if it becomes possible while the button is still
        // held. These are the private fields behind that for double jumps,
        // looked up by name when the mod starts.
        private static FieldInfo _doubleJumpQueuing;
        private static FieldInfo _doubleJumpQueueSteps;

        private static void ForgetQueuedDoubleJump(HeroController hc)
        {
            if (_doubleJumpQueuing != null) _doubleJumpQueuing.SetValue(hc, false);
            if (_doubleJumpQueueSteps != null) _doubleJumpQueueSteps.SetValue(hc, 0);
        }

        // ---- Blocking grabs (and double jumps) during the window -----------
        //
        // The game asks HeroController.CanWallSlide before the Knight grabs a
        // wall, and CanWallJump before a wall-jump (which works off any wall
        // being touched, not only one being clung to). During the window both
        // answer no. CanDoubleJump answers no during the startle. The hooks
        // are attached by name when the mod starts, so a game version without
        // one doesn't break the build: it's logged, and for the grab checks
        // the fallback in UpdateGrabLockout takes over.

        private delegate bool orig_HeroCheck(HeroController self);

        private static readonly string[] GrabCheckNames = { "CanWallSlide", "CanWallJump" };
        private readonly List<MonoMod.RuntimeDetour.Hook> _grabHooks = new List<MonoMod.RuntimeDetour.Hook>();
        private bool _grabHooksMissing;

        private void InstallGrabHooks()
        {
            RemoveGrabHooks();
            _grabHooksMissing = false;

            const string grabFallback = "after a wall slip, a re-grabbed wall is let go of instead";
            foreach (string name in GrabCheckNames)
                if (!TryHookHeroCheck(name, BlockDuringNoGrab, grabFallback)) _grabHooksMissing = true;

            const string startleFallback = "double jumps aren't blocked right after a wall slip";
            TryHookHeroCheck("CanDoubleJump", BlockDuringStartle, startleFallback);

            const BindingFlags fieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _doubleJumpQueuing = typeof(HeroController).GetField("doubleJumpQueuing", fieldFlags);
            _doubleJumpQueueSteps = typeof(HeroController).GetField("doubleJumpQueueSteps", fieldFlags);
            if (_doubleJumpQueuing != null && _doubleJumpQueuing.FieldType != typeof(bool)) _doubleJumpQueuing = null;
            if (_doubleJumpQueueSteps != null && _doubleJumpQueueSteps.FieldType != typeof(int)) _doubleJumpQueueSteps = null;
            if (_doubleJumpQueuing == null)
                LogWarn("Couldn't find HeroController.doubleJumpQueuing; a jump pressed during the startle after a wall slip may still fire as a double jump once it ends.");
        }

        /// <summary>
        /// Hooks one of HeroController's yes/no checks by name. Logs a warning
        /// ending in <paramref name="fallback"/> and returns false if it can't.
        /// </summary>
        private bool TryHookHeroCheck(string name, System.Func<orig_HeroCheck, HeroController, bool> detour, string fallback)
        {
            MethodInfo method = typeof(HeroController).GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);

            if (method == null || method.ReturnType != typeof(bool))
            {
                LogWarn($"Couldn't find HeroController.{name}; {fallback}.");
                return false;
            }

            try
            {
                _grabHooks.Add(new MonoMod.RuntimeDetour.Hook(method, detour));
                return true;
            }
            catch (System.Exception e)
            {
                LogWarn($"Couldn't hook HeroController.{name} ({e.Message}); {fallback}.");
                return false;
            }
        }

        private void RemoveGrabHooks()
        {
            foreach (MonoMod.RuntimeDetour.Hook hook in _grabHooks) hook.Dispose();
            _grabHooks.Clear();
        }

        private bool BlockDuringNoGrab(orig_HeroCheck orig, HeroController self) =>
            !GrabBlocked && orig(self);

        private bool BlockDuringStartle(orig_HeroCheck orig, HeroController self) =>
            !StartleActive && orig(self);

        // ---- The effect where the grip went --------------------------------
        //
        // SlipEffect picks it: "Tink" (the spark the nail makes off something
        // it can't cut, which goes with the tink sound), the name of one of
        // the Knight's own effects (such as "Wall Puff", the wall-jump puff),
        // or blank for none.
        //
        // The tink spark belongs to tinkable objects (saws, spikes, armoured
        // enemies), not the Knight, so the mod has the Modding API load one at
        // start-up: a set of spikes on a City of Tears roof (level file 98).
        // It keeps its own copy of their spark, which works everywhere. If
        // that preload comes back empty, it looks for a spark whenever an area
        // loads, and on a slip, with the wall puff standing in until then.
        //
        // A Knight effect is restarted the way the game does (switched off and
        // on again). The wall puff lets go of the Knight as it plays, so it
        // stays put; it's attached in front of the Knight, who usually faces
        // away from the wall mid-slip, so it's mirrored to the wall side at
        // the same distance and height and held there for its first few
        // frames. With DetailedLogging on, each slip logs where it is.

        private const string TinkEffectName = "Tink";
        private const string WallPuffName = "Wall Puff";
        private const float SlipEffectLifetime = 2f;

        private void ShowSlipEffect(HeroController hc, int wallSide)
        {
            string wanted = (_settings.SlipEffect ?? "").Trim();
            if (wanted.Length == 0) return;
            if (_settings.DetailedLogging) LogKnightEffectsOnce(hc);

            if (string.Equals(wanted, TinkEffectName, System.StringComparison.OrdinalIgnoreCase))
            {
                if (FindTinkSpark())
                {
                    ShowTinkSpark(hc, wallSide);
                    return;
                }
                if (!_loggedTinkStandIn)
                {
                    _loggedTinkStandIn = true;
                    LogDetail("No tink spark has been loaded yet, so the wall puff stands in until one is.");
                }
                wanted = WallPuffName;
            }

            GameObject puff = FindKnightEffect(hc, wanted);
            if (puff == null)
            {
                if (_warnedMissingEffects.Add(wanted.ToLowerInvariant()))
                    LogWarn($"Couldn't find the Knight effect \"{wanted}\" for SlipEffect, so wall slips show no effect. "
                        + "With Detailed Logging on, the Knight's effects are listed on the first slip.");
                return;
            }

            RestartKnightEffect(hc, puff, wallSide);
        }

        private readonly HashSet<string> _warnedMissingEffects = new HashSet<string>();
        private bool _loggedTinkStandIn;
        private bool _loggedKnightEffects;

        /// <summary>One of the Knight's own effects by name; the wall puff through its HeroController field.</summary>
        private static GameObject FindKnightEffect(HeroController hc, string name)
        {
            LookUpWallPuff();
            if (string.Equals(name, WallPuffName, System.StringComparison.OrdinalIgnoreCase) && _wallPuffField != null)
            {
                GameObject puff = _wallPuffField.GetValue(hc) as GameObject;
                if (puff != null) return puff;
            }

            foreach (Transform t in hc.GetComponentsInChildren<Transform>(true))
                if (t != hc.transform && string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase))
                    return t.gameObject;
            return null;
        }

        /// <summary>With DetailedLogging on, lists the Knight's visible effects once per session, for SlipEffect.</summary>
        private void LogKnightEffectsOnce(HeroController hc)
        {
            if (_loggedKnightEffects) return;
            _loggedKnightEffects = true;
            string[] names = hc.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.transform != hc.transform)
                .Select(r => r.gameObject.name)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();
            Log($"The Knight's effects, any of which works for SlipEffect (besides Tink): {string.Join(", ", names)}");
        }

        // ---- The tink spark ------------------------------------------------

        // The spikes preloaded for their spark: the area's build number (its
        // level file is level98) and the object's path in that area. The
        // area's name is looked up from the number at start-up.
        private const int TinkSourceLevel = 98;
        private const string TinkSourcePath = "ruind_bridge_roof_06/ruind_bridge_roof_04_spikes";
        private string _tinkSourceScene;

        private string TinkSourceScene()
        {
            string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(TinkSourceLevel);
            _tinkSourceScene = string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            if (_tinkSourceScene == null)
                LogWarn($"Couldn't find area number {TinkSourceLevel} to preload the tink spark from; it'll be picked up in game instead.");
            return _tinkSourceScene;
        }

        /// <summary>Takes the spark from the preloaded spikes, if they came through.</summary>
        private void TakeTinkSparkFromPreload(Dictionary<string, Dictionary<string, GameObject>> preloaded)
        {
            if (_tinkTemplate != null || preloaded == null || _tinkSourceScene == null) return;

            GameObject source = null;
            if (preloaded.TryGetValue(_tinkSourceScene, out Dictionary<string, GameObject> objects))
                objects.TryGetValue(TinkSourcePath, out source);
            if (source == null)
            {
                LogWarn($"The spikes \"{TinkSourcePath}\" weren't preloaded from {_tinkSourceScene}, "
                    + "so the tink spark is picked up in game instead, with the wall puff standing in until then.");
                return;
            }

            if (!LookUpTinkType()) return;
            Component tink = source.GetComponentInChildren(_tinkType, true);
            GameObject spark = tink != null ? _tinkBlockEffect.GetValue(tink) as GameObject : null;
            if (spark == null)
            {
                LogWarn($"The preloaded \"{TinkSourcePath}\" has no tink spark, "
                    + "so it's picked up in game instead, with the wall puff standing in until then.");
                return;
            }

            KeepTinkSpark(spark, $"preloaded from {_tinkSourceScene}");
        }

        private GameObject _tinkTemplate;
        private static System.Type _tinkType;
        private static FieldInfo _tinkBlockEffect;
        private static bool _tinkTypeLookedUp;
        private bool _warnedNoTinkType;

        private bool WantsTinkSpark() =>
            string.Equals((_settings.SlipEffect ?? "").Trim(), TinkEffectName, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Finds a loaded tink spark (a TinkEffect's blockEffect, looked up by
        /// name) and keeps a silent, inactive copy of it that survives area
        /// changes. True once there is one.
        /// </summary>
        private bool FindTinkSpark()
        {
            if (_tinkTemplate != null) return true;
            if (!LookUpTinkType()) return false;

            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(_tinkType))
            {
                GameObject spark = _tinkBlockEffect.GetValue(found) as GameObject;
                if (spark == null) continue;
                Component owner = found as Component;
                KeepTinkSpark(spark, owner != null ? $"on \"{owner.gameObject.name}\"" : "loaded in game");
                return true;
            }
            return false;
        }

        /// <summary>Finds the game's TinkEffect type and its blockEffect field by name. False (warned once) if missing.</summary>
        private bool LookUpTinkType()
        {
            if (!_tinkTypeLookedUp)
            {
                _tinkTypeLookedUp = true;
                _tinkType = typeof(HeroController).Assembly.GetType("TinkEffect");
                _tinkBlockEffect = _tinkType != null
                    ? _tinkType.GetField("blockEffect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    : null;
                if (_tinkBlockEffect != null && _tinkBlockEffect.FieldType != typeof(GameObject)) _tinkBlockEffect = null;
            }

            if (_tinkBlockEffect == null)
            {
                if (!_warnedNoTinkType)
                {
                    _warnedNoTinkType = true;
                    LogWarn("Couldn't find the game's tink spark (TinkEffect.blockEffect), so SlipEffect \"Tink\" shows the wall puff instead.");
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Keeps a silent, switched-off copy of a tink spark that survives
        /// area changes, to copy from on each slip.
        /// </summary>
        private void KeepTinkSpark(GameObject spark, string from)
        {
            // Copy it switched off, so the copy doesn't play on the spot.
            bool wasActive = spark.activeSelf;
            spark.SetActive(false);
            _tinkTemplate = UnityEngine.Object.Instantiate(spark);
            spark.SetActive(wasActive);

            _tinkTemplate.name = spark.name + " (Stumble)";
            foreach (AudioSource source in _tinkTemplate.GetComponentsInChildren<AudioSource>(true))
                source.enabled = false; // the slip already has its sound
            UnityEngine.Object.DontDestroyOnLoad(_tinkTemplate);

            LogDetail($"Got the tink spark \"{spark.name}\" {from}: {Describe(_tinkTemplate)}");
        }

        /// <summary>Plays a copy of the tink spark against the wall, at the Knight's middle height.</summary>
        private void ShowTinkSpark(HeroController hc, int wallSide)
        {
            Collider2D body = hc.GetComponent<Collider2D>();
            Bounds b = body != null ? body.bounds : new Bounds(hc.transform.position, Vector3.one);
            float x = wallSide < 0 ? b.min.x : wallSide > 0 ? b.max.x : b.center.x;
            Vector3 position = new Vector3(x, b.center.y, hc.transform.position.z);
            Quaternion facing = Quaternion.Euler(0f, 0f, wallSide < 0 ? 180f : 0f);

            GameObject spark = UnityEngine.Object.Instantiate(_tinkTemplate, position, facing);
            spark.SetActive(true);
            UnityEngine.Object.Destroy(spark, SlipEffectLifetime);

            if (_settings.DetailedLogging)
                Log($"Slip tink spark at the {(wallSide < 0 ? "left" : wallSide > 0 ? "right" : "unknown")} wall, "
                    + $"({position.x:0.00}, {position.y:0.00}), scale {spark.transform.lossyScale}.");
        }

        // ---- Restarting one of the Knight's effects -------------------------

        private static FieldInfo _wallPuffField;
        private static FieldInfo _touchingWallL;
        private static FieldInfo _touchingWallR;
        private static bool _wallPuffLookedUp;

        private static void LookUpWallPuff()
        {
            if (_wallPuffLookedUp) return;
            _wallPuffLookedUp = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _wallPuffField = typeof(HeroController).GetField("wallPuffPrefab", flags);
            if (_wallPuffField != null && _wallPuffField.FieldType != typeof(GameObject)) _wallPuffField = null;
            _touchingWallL = typeof(HeroController).GetField("touchingWallL", flags);
            _touchingWallR = typeof(HeroController).GetField("touchingWallR", flags);
            if (_touchingWallL != null && _touchingWallL.FieldType != typeof(bool)) _touchingWallL = null;
            if (_touchingWallR != null && _touchingWallR.FieldType != typeof(bool)) _touchingWallR = null;
        }

        /// <summary>-1 if the wall is on the Knight's left, 1 on their right, 0 if unknown.</summary>
        private static int WallSide(HeroController hc)
        {
            LookUpWallPuff();
            if (_touchingWallL != null && (bool)_touchingWallL.GetValue(hc)) return -1;
            if (_touchingWallR != null && (bool)_touchingWallR.GetValue(hc)) return 1;
            return 0;
        }

        private void RestartKnightEffect(HeroController hc, GameObject puff, int wallSide)
        {
            if (_settings.DetailedLogging) LogPuff("Before the slip", hc, puff, wallSide);

            // Where the game would put it, mirrored to the wall side if needed.
            Collider2D body = hc.GetComponent<Collider2D>();
            Vector3 center = body != null ? body.bounds.center : hc.transform.position;
            Vector3 target = puff.transform.position;
            float offsetX = target.x - center.x;
            bool mirror = wallSide != 0 && offsetX != 0f && Mathf.Sign(offsetX) != wallSide;
            if (mirror) target.x = center.x - offsetX;
            float scaleSign = Mathf.Sign(puff.transform.lossyScale.x) * (mirror ? -1f : 1f);

            puff.SetActive(false);
            puff.SetActive(true);
            PlacePuff(puff, target, scaleSign);

            hc.StartCoroutine(HoldPuff(hc, puff, target, scaleSign, wallSide));
        }

        private const int PuffHoldFrames = 3;

        /// <summary>Keeps the puff at its spot for its first few frames, then logs where it ended up.</summary>
        private IEnumerator HoldPuff(HeroController hc, GameObject puff, Vector3 target, float scaleSign, int wallSide)
        {
            for (int i = 0; i < PuffHoldFrames; i++)
            {
                yield return null;
                if (puff == null || !puff.activeInHierarchy) yield break;
                PlacePuff(puff, target, scaleSign);
            }

            if (!_settings.DetailedLogging) yield break;
            yield return new WaitForSeconds(0.1f);
            if (hc != null && puff != null) LogPuff("just after the slip", hc, puff, wallSide);
        }

        private static void PlacePuff(GameObject puff, Vector3 target, float scaleSign)
        {
            puff.transform.position = target;
            if (Mathf.Sign(puff.transform.lossyScale.x) != scaleSign)
            {
                Vector3 local = puff.transform.localScale;
                local.x = -local.x;
                puff.transform.localScale = local;
            }
        }

        private void LogPuff(string when, HeroController hc, GameObject puff, int wallSide)
        {
            Collider2D body = hc.GetComponent<Collider2D>();
            Vector3 center = body != null ? body.bounds.center : hc.transform.position;
            Vector3 offset = puff.transform.position - center;
            string side = wallSide < 0 ? "left" : wallSide > 0 ? "right" : "unknown";
            string renderers = string.Join(", ", puff.GetComponentsInChildren<Renderer>(true)
                .Select(r => $"{r.gameObject.name} {(r.enabled ? "on" : "off")}{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}")
                .ToArray());
            Log($"Slip puff, {when}: active {puff.activeSelf} (in hierarchy {puff.activeInHierarchy}), "
                + $"part of the Knight {puff.transform.IsChildOf(hc.transform)}, "
                + $"offset from the Knight ({offset.x:0.00}, {offset.y:0.00}), wall on the {side}, "
                + $"scale {puff.transform.lossyScale}. Renderers: {(renderers.Length > 0 ? renderers : "none")}. "
                + Describe(puff));
        }

        // ---- Refreshing double jump and dash -------------------------------
        //
        // Clinging to a wall gives back the double jump and air dash. A slip
        // the instant the Knight catches a wall comes before the game does
        // that, so the slip does it instead, as if they'd gripped for a
        // moment. (The startle still holds off the double jump briefly.)
        // Uses the game's own ResetAirMoves, looked up by name, or failing
        // that the two private fields it clears.

        private static MethodInfo _resetAirMoves;
        private static FieldInfo _doubleJumped;
        private static FieldInfo _airDashed;
        private static bool _airMovesLookedUp;
        private bool _warnedNoAirMoves;

        private void ResetAirMoves(HeroController hc)
        {
            if (!_airMovesLookedUp)
            {
                _airMovesLookedUp = true;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _resetAirMoves = typeof(HeroController).GetMethod("ResetAirMoves", flags, null, System.Type.EmptyTypes, null);
                _doubleJumped = typeof(HeroController).GetField("doubleJumped", flags);
                _airDashed = typeof(HeroController).GetField("airDashed", flags);
                if (_doubleJumped != null && _doubleJumped.FieldType != typeof(bool)) _doubleJumped = null;
                if (_airDashed != null && _airDashed.FieldType != typeof(bool)) _airDashed = null;
            }

            if (_resetAirMoves != null)
            {
                _resetAirMoves.Invoke(hc, null);
                return;
            }

            if (_doubleJumped != null) _doubleJumped.SetValue(hc, false);
            if (_airDashed != null) _airDashed.SetValue(hc, false);

            if ((_doubleJumped == null || _airDashed == null) && !_warnedNoAirMoves)
            {
                _warnedNoAirMoves = true;
                LogWarn("Couldn't find how the game gives back the double jump and air dash, so a slip the instant the Knight catches a wall may not give them back.");
            }
        }

        // ---- Letting go of the wall ----------------------------------------

        /// <summary>
        /// Lets go of the wall the way the game does, through its own
        /// CancelWallsliding, which also stops the wall-slide dust and sound.
        /// Looked up by name; if this game version doesn't have it, slips are
        /// skipped and a warning is logged once.
        /// </summary>
        private bool LetGoOfWall(HeroController hc)
        {
            if (!_cancelWallslidingLookedUp)
            {
                _cancelWallslidingLookedUp = true;
                _cancelWallsliding = typeof(HeroController).GetMethod("CancelWallsliding",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, System.Type.EmptyTypes, null);
            }

            if (_cancelWallsliding == null)
            {
                if (!_warnedNoWallRelease)
                {
                    _warnedNoWallRelease = true;
                    LogWarn("Couldn't find the game's way of letting go of a wall, so wall slips are off.");
                }
                return false;
            }

            _cancelWallsliding.Invoke(hc, null);
            return true;
        }
    }
}
