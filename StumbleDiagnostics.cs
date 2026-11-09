using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using Modding;
using UnityEngine;

namespace Stumble
{
    // Log output for troubleshooting. Warnings and important events are always
    // written; the longer descriptions only with DetailedLogging on.
    public partial class StumbleMod
    {
        private bool _loggedClipList;
        private bool _loggedRiseFrames;
        private readonly HashSet<int> _loggedGround = new HashSet<int>();

        // Last known state of each map, inventory or Knight state machine (FSM),
        // to log changes while the Knight is down.
        private readonly Dictionary<int, string> _menuFsmStates = new Dictionary<int, string>();
        private static System.Type _fsmType;
        private static bool _fsmTypeLookedUp;

        /// <summary>Writes to ModLog.txt only with DetailedLogging on.</summary>
        private void LogDetail(string message)
        {
            if (_settings.DetailedLogging) Log(message);
        }

        private void ResetMenuFsmTracking() => _menuFsmStates.Clear();

        private bool _loggedMapWalkMiss;

        /// <summary>
        /// With DetailedLogging on: the first time per session the Knight moves
        /// along the ground with the map button held but can't trip, logs what
        /// the game shows about them, so map-walking can be recognised properly.
        /// </summary>
        private void LogMapWalkMissOnce(HeroController hc)
        {
            if (_loggedMapWalkMiss || !hc.cState.onGround || !MapButtonHeld()) return;

            Rigidbody2D body = hc.GetComponent<Rigidbody2D>();
            if (body == null || Mathf.Abs(body.velocity.x) <= 0.1f) return;

            tk2dSpriteAnimator anim = hc.GetComponent<tk2dSpriteAnimator>();
            string clip = anim != null && anim.CurrentClip != null ? anim.CurrentClip.name : "(none)";

            _loggedMapWalkMiss = true;
            Log($"Moving with the map button held, but not trippable: animation \"{clip}\", "
                + $"state {hc.hero_state}, acceptingInput {hc.acceptingInput}, "
                + $"controlReqlinquished {hc.controlReqlinquished}, speed {body.velocity.x:0.0}");
        }

        /// <summary>Whether the quick map button is held. Looked up by name.</summary>
        private static bool MapButtonHeld()
        {
            object actions = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
            if (actions == null) return false;
            FieldInfo field = actions.GetType().GetField("quickMap");
            object button = field != null ? field.GetValue(actions) : null;
            PropertyInfo pressed = button != null ? button.GetType().GetProperty("IsPressed") : null;
            return pressed != null && (bool)pressed.GetValue(button, null);
        }

        /// <summary>
        /// With DetailedLogging on, while the Knight is down: logs each state
        /// change of the game's state machines (FSMs) that belong to the Knight
        /// or have "map" or "inventory" in their name, to find what opens a
        /// menu during a trip. Looked up by name, so nothing here depends on
        /// the PlayMaker library being referenced.
        /// </summary>
        private void LogMenuFsmChanges(ActiveTrip t)
        {
            if (!_fsmTypeLookedUp)
            {
                _fsmTypeLookedUp = true;
                _fsmType = System.Type.GetType("PlayMakerFSM, PlayMaker");
            }
            if (_fsmType == null) return;

            PropertyInfo nameProp = _fsmType.GetProperty("FsmName");
            PropertyInfo stateProp = _fsmType.GetProperty("ActiveStateName");
            if (nameProp == null || stateProp == null) return;

            foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(_fsmType))
            {
                Component fsm = found as Component;
                if (fsm == null) continue;

                string fsmName = nameProp.GetValue(fsm, null) as string ?? "";
                string objName = fsm.gameObject.name;
                string both = (fsmName + " " + objName).ToLowerInvariant();
                bool relevant = fsm.transform.IsChildOf(t.Hero.transform)
                    || both.Contains("map") || both.Contains("inventory");
                if (!relevant) continue;

                string state = stateProp.GetValue(fsm, null) as string ?? "";
                int id = fsm.GetInstanceID();
                if (_menuFsmStates.TryGetValue(id, out string previous) && previous != state)
                    Log($"During a trip, FSM \"{fsmName}\" on \"{objName}\" went {previous} -> {state}");
                _menuFsmStates[id] = state;
            }
        }

        /// <summary>
        /// A configured animation name doesn't exist: list every animation the
        /// Knight has, once per session, so the right one can go in the settings.
        /// Always logged, since the mod can't trip without it.
        /// </summary>
        private void LogClipNamesOnce(tk2dSpriteAnimator anim, string missing)
        {
            if (_loggedClipList || anim.Library == null) return;
            _loggedClipList = true;
            string names = string.Join(", ", anim.Library.clips.Select(c => c.name).ToArray());
            LogWarn($"Animation clip \"{missing}\" not found. The Knight's clips are: {names}");
        }

        /// <summary>
        /// Writes the stand-up clip's frames to ModLog.txt once per session,
        /// marking repeats, so frames can be picked for FallSkipFrames.
        /// </summary>
        private void LogRiseFramesOnce(tk2dSpriteAnimationClip rise)
        {
            if (_loggedRiseFrames) return;
            _loggedRiseFrames = true;

            Log($"\"{rise.name}\" has {rise.frames.Length} frames at {rise.fps} fps (played forwards, standing up):");
            for (int i = 0; i < rise.frames.Length; i++)
            {
                tk2dSpriteAnimationFrame f = rise.frames[i];
                bool repeat = i > 0 && SameSprite(f, rise.frames[i - 1]);
                Log($"  Frame {i + 1}: {SpriteName(f)}{(repeat ? "  (same as previous)" : "")}");
            }
        }

        private static string SpriteName(tk2dSpriteAnimationFrame f)
        {
            var defs = f.spriteCollection != null ? f.spriteCollection.spriteDefinitions : null;
            if (defs != null && f.spriteId >= 0 && f.spriteId < defs.Length && defs[f.spriteId] != null)
                return defs[f.spriteId].name;
            return "sprite #" + f.spriteId;
        }

        /// <summary>
        /// Logs what's at the impact point, once per object per session: the
        /// ground the Knight hit, then every other object with a collider
        /// around their feet (decorations, grass, effect zones). Platform effects
        /// usually live on one of those rather than on the ground itself.
        /// </summary>
        private void LogGroundOnce(ActiveTrip t)
        {
            Bounds b = t.Collider.bounds;
            RaycastHit2D hit = Physics2D.Raycast(new Vector2(b.center.x, b.min.y + 0.05f), Vector2.down, 0.3f, TerrainMask);
            GameObject ground = hit.collider != null ? hit.collider.gameObject : null;
            if (ground != null && _loggedGround.Add(ground.GetInstanceID()))
                Log($"Trip impact on {Describe(ground)}");

            // Everything else around the feet: a box the Knight's width plus a
            // margin, from their middle to a little below the floor.
            Vector2 center = new Vector2(b.center.x, (b.center.y + b.min.y - 0.5f) / 2f);
            Vector2 size = new Vector2(b.size.x + 0.5f, b.extents.y + 0.5f);
            foreach (Collider2D col in Physics2D.OverlapBoxAll(center, size, 0f))
            {
                if (col == null || col.transform.IsChildOf(t.Hero.transform)) continue;
                GameObject go = col.gameObject;
                if (go == ground || !_loggedGround.Add(go.GetInstanceID())) continue;

                string kind = col.isTrigger ? "trigger" : "solid";
                Log($"  Near the impact ({kind}, layer {LayerMask.LayerToName(go.layer)}): {Describe(go)}");
            }
        }

        /// <summary>An object's name, parent, components and first few children.</summary>
        private static string Describe(GameObject go)
        {
            string parent = go.transform.parent != null ? $" (in \"{go.transform.parent.name}\")" : "";
            var children = new List<string>();
            foreach (Transform child in go.transform)
            {
                if (children.Count == 10) { children.Add("…"); break; }
                children.Add(child.name);
            }
            return $"\"{go.name}\"{parent}. Components: {DescribeComponents(go)}."
                + (children.Count > 0 ? $" Children: {string.Join(", ", children.ToArray())}." : "");
        }

        /// <summary>Component type names, with the name of any game state machine (FSM).</summary>
        private static string DescribeComponents(GameObject go)
        {
            var parts = new List<string>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string name = c.GetType().Name;
                PropertyInfo fsmName = c.GetType().GetProperty("FsmName");
                if (fsmName != null) name += $" \"{fsmName.GetValue(c, null)}\"";
                parts.Add(name);
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
