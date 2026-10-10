# Laser antenna copy buttons writing GPS strings the paste button rejects

**Category:** Client only
**Patch:** `ClientPlugin/Patches/LaserAntennaCopyCoordsPatch.cs`
**Pull request:** [#9](https://github.com/CometWorks/bugfixes/pull/9)

## The bug

The laser antenna's "Copy my coords" button writes
`GPS:<name>:<x>:<y>:<z>:` to the clipboard with the antenna's whole name,
only replacing `:` with a space. "Copy target coords" does the same with the
last known target name. Every GPS pattern in `MyGpsCollection` reads the
name with `[^:]{0,32}`, and that includes `ParseOneGPS`, which "Paste coords"
and the programmable block's `IMyLaserAntenna.SetTargetCoords` use. So once
the name is longer than 32 characters, the pasted string doesn't parse, and
the antenna stays idle without any message.

The GPS screen limits names to 32 characters as well (`MyGuiScreenTerminal`,
the `panelGpsName` text box), so the parsers define the format and the copy
buttons are what break it. The buttons and the parser are platform
independent game code. Settled against decompiled 1.210.014.

Reported on the Discord support channel as DIS-0004.

## The fix

A postfix on `MyLaserAntenna.CreateTerminalControls` replaces the actions of
the two buttons with copies of the vanilla ones that cut the name to 32
characters. The coordinates are written as before, so the pasted string
still finds the same antenna, which searches within 10 m of them. With the
plugin disabled in its config, the buttons write the whole name, as vanilla
does.

## Testing

In game under Pulsar, a headless Linux client with the DIS-0004 rig
(`notes/dis-0004-laser-antenna` in the workspace). A test probe presses
"Copy my coords" on an antenna named `LA Bravo with a 33 character name`,
records what it wrote, and the other antenna's programmable block pastes
that string:

- Without the plugin, the button wrote
  `GPS:LA Bravo with a 33 character name:500:5.18:20000:`, `ParseOneGPS`
  rejected it and both antennas stayed idle. The Windows build under Proton
  wrote and rejected the same string.
- With the plugin, the button wrote
  `GPS:LA Bravo with a 33 character nam:500:5.18:20000:`. It parsed, and the
  antennas linked about 8 s later.
- Names of 32 characters or fewer are written unchanged.
