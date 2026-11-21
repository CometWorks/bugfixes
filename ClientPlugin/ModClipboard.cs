using System;
using Sandbox.ModAPI;
using Shared.Logging;
using VRage;

namespace ClientPlugin;

// Gives mods the system clipboard (SE-1809).
//
// The mod API whitelist has no clipboard: MyVRage.Platform.System.Clipboard and
// MyClipboardHelper are not on it, and a MyModScreen carries no game controls, so a mod's text
// field can't copy or paste outside the mod. Mod messages are whitelisted, so a mod sends one on
// MessageId: a string payload sets the clipboard, an Action<string> payload is called back with
// its text. OctahedronMod and Trade Box use it for their text fields and fall back to a
// clipboard of their own when nothing answers.
//
// On Linux LinuxCompat answers these messages, as it reads the clipboard through SDL on the
// render thread, so this class stays off there.
public static class ModClipboard
{
    // "CLIPBOAR" in ASCII
    public const long MessageId = 0x434C4950424F4152;

    private static IPluginLogger log;

    public static void Register(IPluginLogger logger)
    {
        if (Environment.OSVersion.Platform == PlatformID.Unix)
            return;
        log = logger;
        MyAPIUtilities.Static.RegisterMessageHandler(MessageId, Handle);
    }

    private static void Handle(object payload)
    {
        try
        {
            switch (payload)
            {
                case string text when text.Length > 0:
                    MyVRage.Platform.System.Clipboard = text;
                    break;
                case Action<string> reply:
                    reply(MyVRage.Platform.System.Clipboard ?? string.Empty);
                    break;
            }
        }
        catch (Exception e)
        {
            log.Error(e, "Mod clipboard message failed");
        }
    }
}
