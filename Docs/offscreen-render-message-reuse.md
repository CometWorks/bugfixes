# Render thread crash in the LCD offscreen renderer

**Category:** Client only
**Patch:** `ClientPlugin/Patches/OffscreenRenderMessageReusePatch.cs`
**Reported in:** SE-1377 (CometWorks tickets)

## The bug

The client crashes with "Exception in render!" and this inner exception, on a
render worker thread:

```
System.InvalidOperationException: InvalidOperation_EnumFailedVersion
   at VRage.Render11.Render.MyOffscreenRenderer.MyData.Dispose()
   at VRage.Render11.Render.MyOffscreenRenderer.RenderWork(MyData data, RenderTargetProxy renderTarget)
```

It was seen once, on a busy server with many LCDs in view (Octahedron, about
190 players). Platform independent game code, settled against the IL of
`VRage.Render11` in 1.210.014.

LCDs and other text surfaces ask for their texture to be redrawn with
`MyRenderProxy.RenderOffscreenTexture`, which takes a
`MyRenderMessageRenderOffscreenTexture` from `MyRenderProxy.MessagePool` on the
update thread and writes the texture name into it. On the render thread,
`MyOffscreenRenderer.Render` sends the queued requests to worker threads:

```csharp
SubmitWork(myData);
m_toSubmitDict.Remove(myData.Message.OffscreenTexture);
m_toSubmitSortedList.Remove(myData);
```

The worker's `RenderWork` ends with `MyData.Dispose`, which releases the last
reference to the message, so it goes back to the pool. If the worker gets
there before the render thread reads `myData.Message.OffscreenTexture`, the
update thread can already have taken the message for another LCD and written
that LCD's texture name into it. `Remove` then uses the wrong key: the
disposed `MyData` stays in `m_toSubmitDict`, and the other texture's request
loses its entry. `SubmitWork` disposing the data itself when the texture is
missing opens the same window on the render thread.

The next request for the first texture (`Add`) or its removal
(`OnTextureDisposed`) disposes the stale `MyData` a second time. That releases
someone else's message and puts its sprite list (`MySpriteMessageData`) back
into its object pool a second time. The pool then hands the same list to two
textures. The render thread fills or clears it for one while a worker walks it
for the other, and the worker's enumerator throws.

## The fix

A prefix on `MyOffscreenRenderer.SubmitWork` adds a reference to the message,
and a postfix on `MyOffscreenRenderer.Render` releases those references after
the vanilla code has read the texture names. The message can no longer go back
to the pool before `Render` is done with it, so the right entry is removed and
nothing is disposed twice. Both run on the render thread.

## Testing

Linux, Pulsar Interim, isolated headless client with rendering on, offline
copy of the Remote suite's space world (SE-1377, 2026-11-12). A test-only
probe plugin (`testing/windows-test-kits/se-1377-offscreen-render` in the
CometWorks workspace) creates 60 generated textures and, every frame, draws a
sprite into 6 of them and asks for them to be rendered, so requests wait in
the queue past the update frame that sent them, as many LCDs do. After each
`SubmitWork` it waits for the worker and checks whether the message is back in
the pool before `Render` reads its name. In "reuse" runs it then renames the
message, which is what the update thread does when it takes it from the pool.

| Run | Submits | Message back in the pool early | Stale queue entries | Sprite lists pooled twice | Result |
|---|---|---|---|---|---|
| Without this fix | 1,555 | 1,405 | 0 | 0 | finished |
| Without this fix, reuse | 375 | 383 | up to 16 | up to 132 | render crash, `InvalidOperation_EnumFailedVersion` |
| With this fix, reuse | 4,555 | 0 | 0 | 0 | finished |
| With this fix | 4,560 | 0 | 0 | 0 | finished |

The crash without the fix came about 3 s into the run, from the same
doubly pooled sprite list, enumerated in `SubmitWork` that time instead of in
`MyData.Dispose`.
