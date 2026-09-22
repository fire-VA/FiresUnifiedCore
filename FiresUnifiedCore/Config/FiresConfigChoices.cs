using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.Sync;
using UnityEngine;

namespace FiresCore.Config
{
    // A string setting limited to a list of choices, drawn in ConfigurationManager as one button per choice.
    // ConfigurationManager's own dropdown for an AcceptableValueList can open over the rows drawn before it, and
    // IMGUI gives a click to the first enabled control under the cursor, so those rows take the click and nothing
    // in the list can be picked. The list stays an AcceptableValueList, so values are still validated.
    public static class FiresConfigChoices
    {
        public static ConfigDescription InlineDescription(string description, IEnumerable<string> choices) =>
            new ConfigDescription(description, new AcceptableValueList<string>(choices.ToArray()),
                new ConfigurationManagerAttributes { CustomDrawer = DrawInline });

        private static void DrawInline(ConfigEntryBase entry)
        {
            if (!(entry.Description.AcceptableValues is AcceptableValueList<string> choices)) return;
            string current = entry.BoxedValue as string;
            GUILayout.BeginVertical();
            foreach (string choice in choices.AcceptableValues)
            {
                bool isCurrent = choice == current;
                if (GUILayout.Toggle(isCurrent, choice, GUI.skin.button, GUILayout.ExpandWidth(true)) && !isCurrent)
                    entry.BoxedValue = choice;
            }
            GUILayout.EndVertical();
        }
    }
}
