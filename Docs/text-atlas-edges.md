# Jagged edges on block label text seen close up

**Category:** Client only
**Patch:** `ClientPlugin/Patches/TextAtlasAlphamaskPatch.cs`
**Pull request:** [#24](https://github.com/CometWorks/bugfixes/pull/24)

## The bug

The text on block labels, like "1x1x1 CARGO CONTAINER" on the small cargo
container, has stair-stepped edges on its slanted and vertical strokes when
the player looks at it from close up. At the Low graphics preset each step is
several pixels wide. At High the steps are smaller but still there.
Anti-aliasing settings make no difference.

The labels are mesh parts with the `DECAL_CUTOUT` technique. Their glyphs come
from `Textures\Models\Cubes\TextAtlas_alphamask.dds`, a 1024x1024 BC4 mask
used by 344 model files, and the standard pixel shader cuts them out with
`clip(alphamask - 0.5)`. Every mip level of that mask holds only the values 0
and 255. When the GPU samples a mask like that bilinearly, the result crosses
0.5 halfway between two texels, so the cut-out edge follows the texel grid
and a slanted edge becomes a staircase with one step per texel. The texture
quality setting drops the two largest mip levels of every model texture at
Low and one at Medium (`MyTextureQuality.MipLevelsToSkip`), which makes the
texels, and so the steps, four or two times larger. Anti-aliasing runs after
the cut-out and cannot restore the shape.

Platform independent: the asset and the shaders are the same on Windows and
Linux. Settled against 1.210.014.

## The fix

A postfix on `MyResourceUtils.GetTextureFullPath` redirects the game's own
text atlas mask, and only that file, to a smoothed copy. Every model material
resolves its texture paths through this method.

The copy is made from the game's mask on first use and written to
`Bugfixes/TextAtlas_alphamask.dds` in the game's user data folder; it takes
about 0.1 s. It holds the signed distance to the glyph edge (a Euclidean
distance transform of the mask), blurred by two texels to even out the steps
of the source. In each mip level a distance of one texel of that level maps
to 0 or 1, and the edge itself to 0.5. The edges stay where they were, but
bilinear sampling now draws them as straight lines at every texture quality.

Each smaller mip level also gets its edge shifted so that it covers as much
of the atlas as the full size level does, like the game's own mips. Without
that the strokes, which are about 25 texels wide at full size, average away
below a texel and the label text disappears when seen from 10 m or more.

That shift is one value for the whole level, so it can't save a single thin
stroke. From mip level 3 on, where a texel spans 8 full size texels and the
label is seen from about 3 m or farther, averaging the distances thinned the
strokes: at about 7 m parts of N, A and I went missing and the label drew
about 13% less light than without the fix. So the small levels now follow
the game's own mask more and more closely:

- Levels 0 to 2, seen from close up, are the distance field alone. They give
  the straight edges.
- Level 3 never drops below the share of each texel the glyphs cover.
- Level 4 never drops below that share, and never below the game's own level
  4 either.
- From level 5 on (32 full size texels per texel) the strokes are thinner
  than a texel. The game's own levels keep such a stroke one texel wide,
  which draws it more completely than anything made from the distance field,
  so the copy uses them unchanged.

From level 4 on every texel is at least the game's own value. Bilinear and
trilinear sampling never make a larger texel value draw less, so wherever the
label samples level 4 or smaller, from about 7 m on, it draws at least what
the game's mask draws, at every quality preset and viewing angle.

The levels were tuned offline: a script decoded the game's mask and the
generated copy, drew what `clip(sample - 0.5)` keeps from each level with
bilinear and trilinear sampling at random texel offsets, and compared that
with the full size glyphs. Around level 4.5, the previous copy drew 58% of
the glyph area and missed 46% of it, and the game's mask drew 82% and missed
35%. Two other schemes were tried and dropped after the in-game sweep below:
a held coverage pushed away from 0.5 at levels 5 and smaller lost the dots of
the small "1x1x1" line at 15 m on Low, and using the game's levels from 5 on
without the level 4 floor left 15 m on Low about 4% dimmer.

The copy is uncompressed `R8_UNORM` instead of BC4. BC4 quantizes each 4x4
block to eight levels, which shifts the edge by up to about a quarter of a
texel and brings back a visible wobble. The uncompressed copy takes 1.4 MB of
video memory instead of 0.7 MB.

Seven model files, all of them LOD1 or LOD2 models, use the same mask with
the blended `DECAL` technique instead of the cut-out. On those the glyph
edges get a soft fringe of about one texel of the sampled mip level, which at
their viewing distance is about one pixel.

If the game's mask is not a square BC4 texture, as it might be after a game
update, the patch logs a warning and leaves it alone. A mod that ships its
own `TextAtlas_alphamask.dds` keeps it.

## Testing

Linux, Pulsar Interim, isolated client with rendering on (headless, iGPU),
Remote and DirectTransport only besides this plugin. A small cargo container
was built in an offline copy of the Remote suite's space world and
screenshotted from the same spots with the game's quality presets
(`--quality`), once without and once with the fix.

2026-10-15 at 1280x720 (SE1-0166):

- About 0.5 m from the label: without the fix the strokes have regular
  saw-tooth edges, large at Low and finer at High. With the fix the edges are
  straight at both presets.
- About 0.8 m away the same, with smaller steps.

2026-10-18 at 1920x1080 with the HUD left out of the screenshots (SE1-0166), a
sweep over every quality preset the
game offers: Low (texture quality Low, two mips skipped, no anti-aliasing, no
anisotropic filtering), Medium (texture quality Medium, one mip skipped,
FXAA), High and Extreme (texture quality High, FXAA, 16x anisotropic
filtering). Each preset was shot from 0.5, 0.8, 1.5, 3, 5, 7, 10 and 15 m
straight on, and from 1.5, 3 and 7 m at 45° to the side, which brings in the
anisotropic filtering. The shots with the fix were aligned to the ones
without it by the best whole pixel shift.

Each cell gives the light the label adds above its background and its white
text pixels, with the fix against without it, in the same box:

| Spot | Low | Medium | High | Extreme |
|---|---|---|---|---|
| 0.5 m | +0.4% / +0.3% | -0.1% / -1.1% | +0.8% / +0.6% | +1.5% / -0.0% |
| 0.8 m | +0.0% / -0.4% | +0.1% / -1.8% | +0.4% / -1.2% | +0.2% / -1.8% |
| 1.5 m | +0.8% / +0.7% | +1.2% / +0.6% | +0.3% / -1.2% | +2.6% / +2.8% |
| 3 m | +7.3% / +10.0% | +5.6% / +5.4% | +11.7% / +8.5% | +12.0% / +9.7% |
| 5 m | +14.2% / +17.1% | +30.3% / +37.9% | +26.5% / +27.9% | +23.5% / +24.0% |
| 7 m | +36.5% / +47.4% | +73.5% / +96.5% | +64.0% / +83.2% | +61.6% / +75.0% |
| 10 m | +19.3% / +30.8% | +27.1% / +70.4% | +61.0% / +198.0% | +59.7% / +184.3% |
| 15 m | -2.6% / -3.4% | -2.8% / -16.7% | -5.4% / -30.8% | +9.7% / -9.1% |
| 1.5 m, 45° | +1.9% / +3.6% | +2.0% / +2.5% | -0.2% / -0.8% | -0.4% / -0.6% |
| 3 m, 45° | +17.0% / +22.1% | +19.7% / +15.4% | +15.2% / +12.0% | +14.7% / +12.4% |
| 7 m, 45° | +27.0% / +34.4% | +46.1% / +69.1% | +114.0% / +155.6% | +99.9% / +145.7% |

A second run without the fix, on High, gives the noise of one shot: within 2%
up to 3 m and at 7 m, up to 9% light at 5 and 10 m, and up to 34% of the
text pixels at 10 and 15 m, where the label is only 10 to 50 white pixels.
So the far cells were shot again from five camera positions a few
centimeters apart, without and with the fix, and averaged:

| Distance | Low | Medium | High | Extreme |
|---|---|---|---|---|
| 10 m | +22.8% / +34.8% | +40.7% / +111.5% | +43.5% / +127.5% | +46.4% / +128.1% |
| 12 m | +11.1% / +18.0% | +15.0% / +36.9% | +0.5% / +3.1% | +3.6% / -1.5% |
| 15 m | -1.9% / -4.8% | -1.3% / -7.2% | +1.7% / +6.1% | -1.3% / -1.9% |
| 20 m | +1.7% / +1.2% | -0.1% / -2.8% | +1.6% / +8.6% | -0.2% / -8.1% |

With the fix, no cell draws less light or fewer text pixels beyond that
noise, and none loses a stroke: from 1.5 to 10 m every white text pixel
without the fix has a white pixel with the fix within one pixel, and closer
up the few that don't (up to 6%) are as many as between two runs without the
fix. From 3 to 12 m the text
is brighter with the fix, and at 7 m it is visibly bolder, most of all on Low.
From 15 m on it looks the same as without the fix. Close up the edges are
straight at every preset, and levels 0 to 2 of the generated file are byte
for byte the same as in the first version of this fix.

The game log has `Using the smoothed text atlas mask` once per start, and the
generated file decodes to the expected 1024x1024 R8 texture with 11 mip
levels, matching the offline model of the generator. Not tested on Windows.
