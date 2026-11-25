using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Shared.Plugin;
using VRage.Render11.Render;
using VRageRender.Messages;

namespace ClientPlugin.Patches;

// Fixes the render thread crashing with InvalidOperation_EnumFailedVersion in
// MyOffscreenRenderer.MyData.Dispose (SE-1377), seen with many LCDs on a busy server.
//
// MyOffscreenRenderer.Render submits queued LCD textures like this:
//
//     SubmitWork(myData);
//     m_toSubmitDict.Remove(myData.Message.OffscreenTexture);
//
// SubmitWork starts RenderWork on a worker, which ends with MyData.Dispose. That drops the
// last reference to the render message, so it goes back to MyRenderProxy.MessagePool. The
// update thread takes messages from that pool every frame for every LCD and writes another
// texture name into OffscreenTexture. If that happens before the render thread reads the
// name on the next line, Remove uses the wrong key and the disposed MyData stays in
// m_toSubmitDict. The next Add or OnTextureDisposed for that texture disposes it a second
// time. Its MySpriteMessageData then goes back to the object pool twice and is handed out to
// two textures at once, and the render thread clears the sprite list while a worker enumerates
// it in MyData.Dispose. SubmitWork disposing the data itself when the texture is missing opens
// the same window on the render thread.
//
// Present on Windows: platform independent game code, settled against the IL of
// VRage.Render11 1.210.014. More threads and more LCDs make it likelier, not the platform.
//
// The fix holds one more reference to each message SubmitWork gets and releases it on the
// render thread after Render returns, when the vanilla code has read the texture name. The
// worker can no longer return the message to the pool early.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyOffscreenRenderer))]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class OffscreenRenderMessageReusePatch
{
    // MyData is a private nested class
    private static readonly AccessTools.FieldRef<
        object,
        MyRenderMessageRenderOffscreenTexture
    > message = AccessTools.FieldRefAccess<MyRenderMessageRenderOffscreenTexture>(
        AccessTools.Inner(typeof(MyOffscreenRenderer), "MyData"),
        "Message"
    );

    // Render thread only
    private static readonly List<MyRenderMessageBase> held = new List<MyRenderMessageBase>();

    [HarmonyPatch("SubmitWork")]
    [HarmonyPrefix]
    private static void SubmitWorkPrefix(object data)
    {
        if (Common.Config?.Enabled != true)
            return;

        var submitted = message(data);
        submitted.AddRef();
        held.Add(submitted);
    }

    // Also releases what an exception in an earlier Render left behind
    [HarmonyPatch(nameof(MyOffscreenRenderer.Render))]
    [HarmonyPostfix]
    private static void RenderPostfix()
    {
        foreach (var message in held)
            message.Dispose();
        held.Clear();
    }
}
