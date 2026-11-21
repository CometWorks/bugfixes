# Mods can't copy or paste text

**Category:** Client only
**Code:** `ClientPlugin/ModClipboard.cs`
**Setting:** `ModClipboard` ("Clipboard for mods")

## The bug

The mod API whitelist has no clipboard. `MyVRage.Platform.System.Clipboard` and
`MyClipboardHelper` are not on it, and a `MyModScreen` carries no game controls, so a
mod can't put a `MyGuiControlTextbox` on its screen either. The text fields that mods
draw themselves, like the Workshop link field of OctahedronMod's Shipyard Console, can't
copy or paste: a player has to type a Workshop link by hand. Settled against decompiled
1.211.018 (`MySpaceGameDefaultIlChecker`, `MyModScreen`).

## The fix

Mod messages are whitelisted, so a mod asks a plugin. The plugin listens on mod message id
`0x434C4950424F4152` ("CLIPBOAR" in ASCII):

- a `string` payload sets the system clipboard;
- an `Action<string>` payload is called back with the clipboard's text.

On Windows Bugfixes answers on the game thread, at once. On Linux LinuxCompat answers
instead, a frame or two later, as it reads the clipboard through SDL on the render thread;
Bugfixes stays off there so a paste isn't answered twice. A mod sends a paste request
once at start and treats an answer as the sign that a plugin is there; without one it
keeps a clipboard of its own.

## Testing

OctahedronMod's `tests/test_mod_clipboard.py` (CometWorks/worlds) runs a rendering
client in a private Xvfb, types Ctrl+V, Ctrl+A, Ctrl+C and Ctrl+X on the real keyboard
and owns and reads the X clipboard itself: that covers the Linux path through
LinuxCompat. The Windows path through this class is queued for the Windows boot.
