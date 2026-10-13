# Caret in the wrong place after pasting several lines

**Category:** Client only
**Patch:** `ClientPlugin/Patches/MultilinePasteCaretPatch.cs`
**Pull request:** TBD

## The bug

Pasting text with line breaks into a multi-line text box leaves the caret
after the wrong character. This hits every `MyGuiControlMultilineEditableText`:
LCD text, Custom Data, the programmable block editor, faction descriptions
and the others. Whatever is typed next lands inside the text that followed
the paste.

`MyGuiControlMultilineSelection.PasteText` inserts the clipboard text with
`\r\n` replaced by `\n`, then sets the caret to the insertion point plus
`ClipboardText.Length`, the length before the replacement. Every line break
pushes the caret one more character to the right. The game's own Copy writes
`\r\n` line breaks, and so do most Windows programs. The caret setter clamps
to the end of the text, so pasting at the very end looks fine, which is why
the bug mostly shows when pasting into the middle of a script.

Platform independent game code, settled against decompiled 1.210.014. On
Linux, LinuxCompat replaces `PasteText` with its own SDL clipboard version,
which had copied the same caret arithmetic. That copy is fixed in LinuxCompat
1.0.31.

Reported on the Discord support channel as DIS-0015.

## The fix

A prefix on `PasteText` remembers the length of the text after the insertion
point (after the selection, if there is one), which the paste leaves alone. A
postfix puts the caret right before that text and resets the selection there.
When another prefix replaced the method, as LinuxCompat's does, the postfix
does nothing. With the plugin disabled in its config, the caret lands where
vanilla puts it.

## Testing

In game under Pulsar, a headless Linux client with the DIS-0015 rig
(`notes/dis-0015-multiline-paste` in the workspace). The rig opens Custom Data
of a programmable block, types text, copies with Ctrl+A and Ctrl+C, moves the
caret, pastes with Ctrl+V, types a `#` marker and reads the custom data back
after OK.

TBD
