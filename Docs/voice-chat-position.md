# Voice chat heard from where the speaker first spoke

**Category:** Client only
**Patch:** `ClientPlugin/Patches/VoiceChatPositionPatch.cs`

## The bug

Another player's voice keeps coming from where they were when you first heard them. They walk
away, sit down in a seat or fly off, and their voice stays put. The offset is kept relative to
your camera, so it also follows you around as you move. Seated speakers show it most, because
they usually talked on the way to the seat. NPCs that speak in voice chat behave the same way.

`MyVoiceChatSessionComponent.PlayVoice` plays a speaker's voice through a `MyEntity3DSoundEmitter`
on their character. Its source voice is a buffered one, made by `MyXAudio2.GetSound(emitter,
dimension)`, which places it in 3D once at the character's position relative to the camera.
`MyXAudio2.Update3DCuesPositions` should then move it every frame, but
`Update3DVoicePosition` first looks up the voice's cue and returns when there is none. A voice
chat source has no cue, so its position, distance attenuation and panning stay what they were
when it was made.

`MyXAudio2.IsVoiceStopped` never counts an idle buffered voice as stopped either, so the source is
never released between sentences. It lives until the emitter is disposed: the speaker leaves,
gets a new character or voice chat is turned off.

## The fix

A transpiler on `Update3DVoicePosition` replaces its cue lookup: a voice without a cue gets the
settings `GetSound` placed it with, no default maximum distance (the emitter's own applies) and
a linear volume curve. The method then moves the voice through its own buffered branch, with the
emitter's current position and velocity. Sounds with a cue are untouched. The patch never names
the SharpDX audio types, which LinuxCompat replaces with its own on Linux.

## Testing

`tests/voice-chat-position/rig.py on|off` runs an offline headless client with audio output
(`--audio-file`) and a probe plugin. The probe plays voice chat audio from the local character
through the same emitter the game uses for another player's voice, 20 m from the camera, then
moves the character to 60 m and plays again, then seats it in a cockpit and plays again. After
each step it compares the distance the audio engine uses for the voice with the character's
real distance from the camera.

On Linux (game 1.211.018, offline headless client, 2026-11-21), distances from the listening
camera:

| Step | Real distance | Heard without the fix | Heard with the fix |
|------|---------------|-----------------------|--------------------|
| start | 19.8 m | 19.8 m | 19.8 m |
| moved | 60.8 m | 19.8 m | 60.8 m |
| seated | 2.5 m | 19.8 m | 2.5 m |
