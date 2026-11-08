using System.Collections.Generic;
using System.Linq;
using Modding;
using UnityEngine;

namespace Stumble
{
    // The in-game menu: Options > Mods > Stumble. To add an option, add a field
    // to StumbleSettings and one MenuToggle or MenuChoice line below.
    public partial class StumbleMod
    {
        private static readonly float[] LieTimeChoices = { 0f, 0.35f, 0.75f, 1.5f };
        private static readonly float[] FallSpeedChoices = { 1f, 1.5f, 2f, 3f, 4f };
        private static readonly float[] SlideChoices = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly string[] SlideLabels = { "Off", "Short", "Medium", "Long", "Extra long" };

        public bool ToggleButtonInsideMenu => true;

        public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry)
        {
            var entries = new List<IMenuMod.MenuEntry>();
            if (toggleButtonEntry.HasValue) entries.Add(toggleButtonEntry.Value);

            entries.Add(new IMenuMod.MenuEntry
            {
                Name = "Knight Clumsiness",
                Description = "How often the Knight trips while running.",
                Values = StumbleSettings.ClumsinessLevels,
                Saver = i => _settings.KnightClumsiness = StumbleSettings.ClumsinessLevels[i],
                Loader = () => _settings.ClumsinessIndex()
            });

            entries.Add(MenuChoice("Time on the ground", "Minimum time down before a left/right press stands you up.",
                LieTimeChoices, LieTimeChoices.Select(t => t == 0f ? "None" : t + "s").ToArray(),
                () => _settings.MinLieTime, v => _settings.MinLieTime = v));

            entries.Add(MenuToggle("Fall animation", "Show the Knight falling over before lying down.",
                () => _settings.FallAnimation, v => _settings.FallAnimation = v));

            entries.Add(MenuChoice("Fall speed", "How fast the fall plays compared with standing up.",
                FallSpeedChoices, FallSpeedChoices.Select(x => x + "x").ToArray(),
                () => _settings.FallSpeed, v => _settings.FallSpeed = v));

            entries.Add(MenuToggle("Skip pauses in fall", "Drop held poses (like the kneeling pause) from the fall.",
                () => _settings.FallSkipHolds, v => _settings.FallSkipHolds = v));

            entries.Add(MenuChoice("Forward slide", "How long the Knight keeps sliding forward after tripping.",
                SlideChoices, SlideLabels,
                () => _settings.SlideTime, v => _settings.SlideTime = v));

            entries.Add(MenuToggle("At ledges", "Whether a slide stops at an edge or carries the Knight over it.",
                () => _settings.TumbleOffEdges, v => _settings.TumbleOffEdges = v,
                "Stop at edge", "Tumble off"));

            entries.Add(MenuToggle("Landing sound", "Soft landing sound on short tumbles. Long falls use the game's own thud.",
                () => _settings.LandingSounds, v => _settings.LandingSounds = v));

            entries.Add(MenuToggle("Trip thud", "Play a soft thud when the Knight hits the floor on every trip.",
                () => _settings.TripThud, v => _settings.TripThud = v));

            entries.Add(MenuToggle("Impact dust", "Show the game's landing dust when the Knight hits the floor.",
                () => _settings.ImpactDust, v => _settings.ImpactDust = v));

            entries.Add(MenuToggle("Detailed logging", "Write extra detail to ModLog.txt, for reporting problems.",
                () => _settings.DetailedLogging, v => _settings.DetailedLogging = v));

            return entries;
        }

        /// <summary>An on/off option.</summary>
        private static IMenuMod.MenuEntry MenuToggle(string name, string description,
            System.Func<bool> get, System.Action<bool> set, string offLabel = "Off", string onLabel = "On")
        {
            return new IMenuMod.MenuEntry
            {
                Name = name,
                Description = description,
                Values = new[] { offLabel, onLabel },
                Saver = i => set(i == 1),
                Loader = () => get() ? 1 : 0
            };
        }

        /// <summary>
        /// A pick-one option over a list of numbers. A value set by hand in the
        /// settings file shows as the nearest choice.
        /// </summary>
        private static IMenuMod.MenuEntry MenuChoice(string name, string description,
            float[] values, string[] labels, System.Func<float> get, System.Action<float> set)
        {
            return new IMenuMod.MenuEntry
            {
                Name = name,
                Description = description,
                Values = labels,
                Saver = i => set(values[i]),
                Loader = () => ClosestIndex(values, get())
            };
        }

        private static int ClosestIndex(float[] choices, float value)
        {
            int best = 0;
            for (int i = 1; i < choices.Length; i++)
                if (Mathf.Abs(choices[i] - value) < Mathf.Abs(choices[best] - value)) best = i;
            return best;
        }
    }
}
