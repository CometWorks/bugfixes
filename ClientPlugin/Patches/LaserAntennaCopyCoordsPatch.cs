using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Shared.Plugin;
using VRage;
using VRageMath;

namespace ClientPlugin.Patches;

// Fixes the laser antenna's "Copy my coords" and "Copy target coords" buttons writing a GPS
// string that its own "Paste coords" button cannot read back.
//
// Both buttons write "GPS:<name>:<x>:<y>:<z>:" with the antenna's whole display name (or
// the last known target name), only replacing ':' with ' '. Every GPS parser in
// MyGpsCollection, ParseOneGPS used by "Paste coords" and by the programmable block's
// IMyLaserAntenna.SetTargetCoords among them, matches the name with [^:]{0,32}. A name longer
// than 32 characters therefore makes the pasted string fail to parse, and the antenna stays
// idle without a word. 32 characters is also what the GPS screen's name box allows
// (MyGuiScreenTerminal, panelGpsName), so the parsers define the format and the copy buttons
// are the ones that break it.
//
// Present on Windows: the buttons and the parser are platform independent game code.
//
// The postfix replaces the actions of the two buttons with copies of the vanilla ones that
// cut the name to 32 characters. Only the name is shortened, the coordinates are written as
// before, so a pasted string resolves to the same antenna.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyLaserAntenna), "CreateTerminalControls")]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class LaserAntennaCopyCoordsPatch
{
    // The name group of MyGpsCollection's GPS patterns, [^:]{0,32}
    private const int MaxGpsNameLength = 32;

    private static readonly AccessTools.FieldRef<
        MyLaserAntenna,
        StringBuilder
    > LastKnownTargetName = AccessTools.FieldRefAccess<MyLaserAntenna, StringBuilder>(
        "m_lastKnownTargetName"
    );

    private static void Postfix()
    {
        foreach (var control in MyTerminalControlFactory.GetControls(typeof(MyLaserAntenna)))
        {
            if (!(control is MyTerminalControlButton<MyLaserAntenna> button))
                continue;

            switch (button.Id)
            {
                case "CopyCoords":
                    button.Action = antenna => CopyGps(antenna.DisplayNameText, antenna.HeadPos);
                    break;

                case "CopyTargetCoords":
                    button.Action = antenna =>
                    {
                        if (antenna.TargetId.HasValue)
                            CopyGps(LastKnownTargetName(antenna).ToString(), antenna.TargetCoords);
                    };
                    break;
            }
        }
    }

    private static void CopyGps(string name, Vector3D position)
    {
        name = name.Replace(':', ' ');
        if (Common.Config?.Enabled == true && name.Length > MaxGpsNameLength)
            name = name.Substring(0, MaxGpsNameLength);

        var gps = new StringBuilder("GPS:", 256);
        gps.Append(name);
        gps.Append(':').Append(Math.Round(position.X, 2).ToString(CultureInfo.InvariantCulture));
        gps.Append(':').Append(Math.Round(position.Y, 2).ToString(CultureInfo.InvariantCulture));
        gps.Append(':').Append(Math.Round(position.Z, 2).ToString(CultureInfo.InvariantCulture));
        gps.Append(':');
        MyVRage.Platform.System.Clipboard = gps.ToString();
    }
}
