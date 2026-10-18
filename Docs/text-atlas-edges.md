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
label is seen from about 3 m or farther, averaging the distances still
thinned the strokes: at about 7 m parts of N, A and I went missing and the
label drew about 13% less light than without the fix. So the levels from 3
on never go below the share of each texel the glyphs cover. From level 5 on
the strokes are thinner than a texel and even that share stays under 0.5, so
those levels use it pushed away from 0.5 level by level instead (times 3 per
halving). A stroke thinner than a texel then stays one texel wide, the way
the game's own mips keep it. Levels 0 to 2, the ones seen from close up, are
unchanged.

The levels were tuned offline: a script decoded the game's mask and the
generated copy, drew what `clip(sample - 0.5)` keeps from each level with
bilinear and trilinear sampling at random texel offsets, and compared that
with the full size glyphs. Around level 4.5, where the label sits at about
7 m, the previous copy drew 58% of the glyph area and missed 46% of it, the
game's mask 82% and 35%, and this copy 109% and 12%.

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
screenshotted from the same spots with `--quality low` and `--quality high`,
once without and once with the fix, with the HUD left out of the
screenshots.

2026-10-15 at 1280x720 (SE1-0166):

- About 0.5 m from the label: without the fix the strokes have regular
  saw-tooth edges, large at Low and finer at High. With the fix the edges are
  straight at both presets.
- About 0.8 m away the same, with smaller steps.

2026-10-18 at 1920x1080, after the change to the small mip levels, from
about 0.5, 0.85, 3, 5, 7 and 11 m. Close up the edges are straight as
before; levels 0 to 2 of the generated file are byte for byte the same as
before the change. From 3 m on the text is complete with the fix and a bit
bolder than without it, where the game's own mips drop parts of strokes. The
light the label adds above its background, and its white text pixels, in
the same box at each spot (without the fix, then with it):

| Distance | Low, light | Low, text px | High, light | High, text px |
|---|---|---|---|---|
| 0.5 m | +0.6% | +0.4% | -0.5% | -0.6% |
| 0.85 m | +0.1% | -0.4% | -0.6% | -2.4% |
| 3 m | +9.8% | +14.1% | +12.1% | +10.7% |
| 5 m | +13.8% | +16.7% | +15.8% | +17.2% |
| 7 m | +15.6% | +17.3% | +24.0% | +35.2% |
| 11 m | +23.4% | +37.1% | +53.4% | +148.7% |

Before the change the same 7 m spot gave -12.5% light at Low and -14.9% at
High.

The game log has `Using the smoothed text atlas mask` once per start, and the
generated file decodes to the expected 1024x1024 R8 texture with 11 mip
levels, matching the offline model of the generator. Not tested on Windows.
