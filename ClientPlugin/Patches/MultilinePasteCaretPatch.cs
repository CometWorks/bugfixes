using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using Shared.Plugin;

namespace ClientPlugin.Patches;

// Fixes the caret landing after the wrong character when multi-line text is pasted into a
// multi-line text box: LCD text, Custom Data, the programmable block editor and the other
// MyGuiControlMultilineEditableText boxes.
//
// MyGuiControlMultilineSelection.PasteText inserts the clipboard text with "\r\n" replaced
// by "\n", then puts the caret at the insertion point plus ClipboardText.Length, the length
// before that replacement. Every line break in the pasted text therefore pushes the caret
// one more character to the right. The game's own Copy writes "\r\n" line breaks, and so do
// most Windows programs, so pasting three copied lines in the middle of a script leaves the
// caret two characters into the text after them. The setter clamps the caret to the text
// length, which is why pasting at the very end looks fine.
//
// Present on Windows: PasteText is platform independent game code. On Linux, LinuxCompat
// replaces PasteText with its own copy reading the clipboard through SDL, which carries the
// same caret computation, so the Linux side of the bug is fixed there.
//
// The text after the insertion point is not touched by the paste, so the prefix remembers
// its length and the postfix puts the caret right before it. The selection is reset again,
// as vanilla resets it to its own wrong caret position.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class MultilinePasteCaretPatch
{
    private static readonly System.Type SelectionType = AccessTools.Inner(
        typeof(MyGuiControlMultilineText),
        "MyGuiControlMultilineSelection"
    );

    private static readonly AccessTools.FieldRef<MyGuiControlMultilineText, StringBuilder> Text =
        AccessTools.FieldRefAccess<MyGuiControlMultilineText, StringBuilder>("m_text");

    private static readonly PropertyInfo CarriagePositionIndex = AccessTools.Property(
        typeof(MyGuiControlMultilineText),
        "CarriagePositionIndex"
    );

    private static readonly PropertyInfo SelectionLength = AccessTools.Property(
        SelectionType,
        "Length"
    );

    private static readonly PropertyInfo SelectionEnd = AccessTools.Property(SelectionType, "End");

    private static readonly MethodInfo SelectionReset = AccessTools.Method(SelectionType, "Reset");

    private static MethodBase TargetMethod() => AccessTools.Method(SelectionType, "PasteText");

    // __state: length of the text after the insertion point
    private static void Prefix(object __instance, MyGuiControlMultilineText sender, out int __state)
    {
        // EraseText removes the selection and leaves the caret at its start, so the text
        // after the insertion point starts at the selection's end, or at the caret
        var insertEnd =
            (int)SelectionLength.GetValue(__instance) > 0
                ? (int)SelectionEnd.GetValue(__instance)
                : (int)CarriagePositionIndex.GetValue(sender);
        __state = Text(sender).Length - insertEnd;
    }

    private static void Postfix(
        object __instance,
        MyGuiControlMultilineText sender,
        int __state,
        bool __runOriginal
    )
    {
        // LinuxCompat replaces PasteText with an asynchronous SDL clipboard read, nothing to
        // correct here then
        if (!__runOriginal || Common.Config?.Enabled != true)
            return;

        CarriagePositionIndex.SetValue(sender, Text(sender).Length - __state);
        SelectionReset.Invoke(__instance, new object[] { sender });
    }
}
