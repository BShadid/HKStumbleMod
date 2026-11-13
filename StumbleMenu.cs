using System.Collections.Generic;
using Modding;

namespace Stumble
{
    // The in-game menu: Options > Mods > Stumble. It shows the mod's on/off
    // switch, Knight Clumsiness, Wall Clumsiness and Detailed Logging. Every other setting
    // keeps its default and can be changed in the settings file (see
    // DEVELOPING.md). To add a menu option, add a field to StumbleSettings
    // and one entry below.
    public partial class StumbleMod
    {
        public bool ToggleButtonInsideMenu => true;

        public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry)
        {
            var entries = new List<IMenuMod.MenuEntry>();

            // The Modding API's own switch for the whole mod.
            if (toggleButtonEntry.HasValue) entries.Add(toggleButtonEntry.Value);

            entries.Add(new IMenuMod.MenuEntry
            {
                Name = "Knight Clumsiness",
                Description = "How often the Knight trips while running.",
                Values = StumbleSettings.ClumsinessLevels,
                Saver = i => _settings.KnightClumsiness = StumbleSettings.ClumsinessLevels[i],
                Loader = () => _settings.ClumsinessIndex()
            });

            entries.Add(new IMenuMod.MenuEntry
            {
                Name = "Wall Clumsiness",
                Description = "How often the Knight loses their grip while clinging to walls.",
                Values = StumbleSettings.WallClumsinessLevels,
                Saver = i => _settings.WallClumsiness = StumbleSettings.WallClumsinessLevels[i],
                Loader = () => _settings.WallClumsinessIndex()
            });

            entries.Add(MenuToggle("Detailed Logging", "Write extra detail to ModLog.txt, for reporting problems.",
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
    }
}
