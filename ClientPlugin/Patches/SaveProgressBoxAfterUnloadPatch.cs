using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Analytics;
using Sandbox.Game.World;
using Shared.Plugin;

namespace ClientPlugin.Patches;

// Closes the game instead of crashing when the "Do you want to save your progress?" box is
// answered after the session is gone. See Docs/save-progress-box-after-unload.md.
//
// MySandboxGame.Window_OnManualWindowCloseRequest queues that box when MySession.Static is set
// and Sync.IsServer is true. Sync.IsServer is true when MyMultiplayer.Static is null, which a
// multiplayer client also sees near the end of MySession.Unload, after the multiplayer object
// is disposed and before MySession.Static is cleared. Closing the window then queues the box,
// the unload finishes and the box stays on the main menu.
//
// Answering it runs OnSaveWorldMessageBoxCallback, which reports the mined, emote, block and
// network stats, ends the analytics session and saves the world on Yes. With no session the
// first of these, MySpaceAnalytics.GatherMinedStatsData, throws a NullReferenceException on
// MySession.Static.AmountMined. Its guard in ReportMinedStats passes because exiting to the
// main menu leaves m_isSessionStarted set. The emote and network stats and the save on Yes
// read MySession.Static without a null check too, so guarding only the mined stats would
// move the crash (SE-1877).
//
// With no session there is nothing to report or save, so the prefix does only what is left
// of the callback: it ends the analytics session as a window close and exits the game, which
// is what the player asked for by closing the window.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MySandboxGame), "OnSaveWorldMessageBoxCallback")]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class SaveProgressBoxAfterUnloadPatch
{
    private static bool Prefix()
    {
        if (MySession.Static != null || Common.Config?.Enabled != true)
            return true;

        Common.Logger.Info("Save progress box answered with no session, exiting the game");
        MySpaceAnalytics.Instance.ReportSessionEnd("Window closed");
        MySandboxGame.ExitThreadSafe();
        return false;
    }
}
