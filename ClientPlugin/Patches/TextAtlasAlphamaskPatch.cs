using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Render11.Resources;

namespace ClientPlugin.Patches;

// Fixes the stair-stepped edges of the text on block labels, like "1x1x1 CARGO CONTAINER",
// when looked at from close up.
//
// The labels are mesh parts with the DECAL_CUTOUT technique. Their glyphs come from
// Textures\Models\Cubes\TextAtlas_alphamask.dds, a 1024x1024 BC4 mask, and the pixel shader
// cuts them out with clip(alphamask - 0.5). Every mip level of that mask holds only 0 and 255.
// A bilinear sample of a mask like that crosses 0.5 along the texel grid, so a slanted edge
// comes out as a staircase with one step per texel. The texture quality setting drops the
// two (Low) or one (Medium) largest mip levels of every model texture, which makes the steps
// four or two times larger. Anti-aliasing settings cannot help, the edge is cut out before
// they run.
//
// Present on Windows: the asset and the shaders are the same on both platforms.
//
// The postfix redirects the game's text atlas mask to a smoothed copy made from it on first
// use. The copy holds the signed distance to the glyph edge, blurred to remove the steps of
// the source, and maps a distance of one texel to 0.5 on each side of the edge in every mip
// level. The edges stay where they were, but bilinear sampling now draws them straight.
// Smaller levels keep the coverage of the top one so the text does not fade out from afar.
// The copy is uncompressed R8, because BC4 quantization brings back a visible wobble.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyResourceUtils), nameof(MyResourceUtils.GetTextureFullPath))]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class TextAtlasAlphamaskPatch
{
    private const string AtlasName = "TextAtlas_alphamask.dds";
    private const string AtlasContentPath = "/Textures/Models/Cubes/" + AtlasName;

    // Blur of the distance field in full resolution texels. The source steps one texel
    // sideways every few rows along a slanted edge; this is wide enough to even that out.
    private const double BlurSigma = 2;

    private static readonly object Lock = new();
    private static bool tried;
    private static string smoothAtlasPath;

    private static void Postfix(ref string __result)
    {
        if (
            Common.Config?.Enabled != true
            || __result == null
            || !__result.EndsWith(AtlasName, StringComparison.OrdinalIgnoreCase)
        )
            return;

        // Only the game's own atlas, a mod may ship its own texture under the same name
        var path = __result.Replace('\\', '/');
        var content = MyFileSystem.ContentPath.Replace('\\', '/').TrimEnd('/');
        if (!path.Equals(content + AtlasContentPath, StringComparison.OrdinalIgnoreCase))
            return;

        lock (Lock)
        {
            if (!tried)
            {
                tried = true;
                smoothAtlasPath = TryCreateSmoothAtlas(__result);
            }
        }

        if (smoothAtlasPath != null)
            __result = smoothAtlasPath;
    }

    private static string TryCreateSmoothAtlas(string sourcePath)
    {
        try
        {
            byte[] dds;
            using (var stream = MyFileSystem.OpenRead(sourcePath))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                dds = memory.ToArray();
            }

            var inside = DecodeBc4Mask(dds, out var size);
            if (inside == null)
            {
                Common.Logger.Warning(
                    "Text atlas mask is not a square BC4 texture, leaving it alone: {0}",
                    sourcePath
                );
                return null;
            }

            var output = CreateDistanceDds(inside, size);

            var outputPath = Path.Combine(Common.DataDir, AtlasName);
            if (!File.Exists(outputPath) || !File.ReadAllBytes(outputPath).SequenceEqual(output))
            {
                Directory.CreateDirectory(Common.DataDir);
                File.WriteAllBytes(outputPath, output);
            }

            Common.Logger.Info("Using the smoothed text atlas mask: {0}", outputPath);
            return outputPath;
        }
        catch (Exception e)
        {
            Common.Logger.Error(e, "Could not create the smoothed text atlas mask");
            return null;
        }
    }

    // Returns the top mip level as inside (>= 0.5) flags, or null if the format is unexpected
    private static bool[] DecodeBc4Mask(byte[] dds, out int size)
    {
        size = 0;
        if (dds.Length < 148 || Encoding.ASCII.GetString(dds, 0, 4) != "DDS ")
            return null;

        var height = BitConverter.ToInt32(dds, 12);
        var width = BitConverter.ToInt32(dds, 16);
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4);
        var isDx10 = fourCc == "DX10";
        var isBc4 = fourCc is "BC4U" or "ATI1" || isDx10 && BitConverter.ToInt32(dds, 128) == 80; // DXGI_FORMAT_BC4_UNORM
        var dataOffset = isDx10 ? 148 : 128;
        if (
            !isBc4
            || width != height
            || width < 4
            || (width & (width - 1)) != 0
            || dds.Length < dataOffset + width * width / 2
        )
            return null;

        size = width;
        var inside = new bool[size * size];
        var blocks = size / 4;
        for (var by = 0; by < blocks; by++)
        for (var bx = 0; bx < blocks; bx++)
        {
            var offset = dataOffset + (by * blocks + bx) * 8;
            int a0 = dds[offset];
            int a1 = dds[offset + 1];
            var codes = 0UL;
            for (var i = 0; i < 6; i++)
                codes |= (ulong)dds[offset + 2 + i] << (8 * i);

            for (var i = 0; i < 16; i++)
            {
                var value = Bc4Value(a0, a1, (int)(codes >> (3 * i)) & 7);
                inside[(by * 4 + i / 4) * size + bx * 4 + i % 4] = value >= 128;
            }
        }

        return inside;
    }

    private static int Bc4Value(int a0, int a1, int code) =>
        code switch
        {
            0 => a0,
            1 => a1,
            _ when a0 > a1 => ((8 - code) * a0 + (code - 1) * a1) / 7,
            6 => 0,
            7 => 255,
            _ => ((6 - code) * a0 + (code - 1) * a1) / 5,
        };

    private static byte[] CreateDistanceDds(bool[] inside, int size)
    {
        // Signed distance to the edge in texels, positive inside
        var toOutside = DistanceTransform(inside, size, false);
        var toInside = DistanceTransform(inside, size, true);
        var distance = new float[size * size];
        for (var i = 0; i < distance.Length; i++)
            distance[i] = inside[i] ? toOutside[i] - 0.5f : 0.5f - toInside[i];

        distance = Blur(distance, size);

        var mipCount = 1;
        for (var s = size; s > 1; s /= 2)
            mipCount++;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        WriteR8Header(writer, size, mipCount);

        var coverage = inside.Count(x => x) / (double)inside.Length;
        var level = distance;
        for (var s = size; ; s /= 2)
        {
            // Strokes thinner than a texel would vanish from the small levels, and the text
            // with them when seen from afar. Shift the edge so each level covers as much as
            // the top one, like the game's own mips do.
            float bias = 0;
            var insideCount = (int)Math.Round(coverage * level.Length);
            if (s < size && insideCount > 0)
            {
                var sorted = (float[])level.Clone();
                Array.Sort(sorted);
                bias = -sorted[level.Length - insideCount];
            }

            // One texel of this level from the edge maps to 0 or 1
            foreach (var d in level)
                writer.Write(
                    (byte)Math.Round(255 * Math.Min(1, Math.Max(0, 0.5 + (d + bias) / 2)))
                );

            if (s == 1)
                break;

            // Halve the resolution, the distances shrink with the texel size
            var half = s / 2;
            var next = new float[half * half];
            for (var y = 0; y < half; y++)
            for (var x = 0; x < half; x++)
            {
                var i = 2 * y * s + 2 * x;
                next[y * half + x] =
                    (level[i] + level[i + 1] + level[i + s] + level[i + s + 1]) / 8;
            }

            level = next;
        }

        writer.Flush();
        return memory.ToArray();
    }

    // Euclidean distance from each texel to the nearest texel whose flag equals target
    // (Felzenszwalb and Huttenlocher, columns then rows)
    private static float[] DistanceTransform(bool[] mask, int size, bool target)
    {
        const float far = 1e20f;
        var grid = new float[size * size];
        for (var i = 0; i < grid.Length; i++)
            grid[i] = mask[i] == target ? 0 : far;

        var line = new float[size];
        var result = new float[size];
        var vertices = new int[size];
        var bounds = new float[size + 1];

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
                line[y] = grid[y * size + x];
            SquaredDistance1D(line, result, vertices, bounds);
            for (var y = 0; y < size; y++)
                grid[y * size + x] = result[y];
        }

        for (var y = 0; y < size; y++)
        {
            Array.Copy(grid, y * size, line, 0, size);
            SquaredDistance1D(line, result, vertices, bounds);
            for (var x = 0; x < size; x++)
                grid[y * size + x] = (float)Math.Sqrt(result[x]);
        }

        return grid;
    }

    private static void SquaredDistance1D(float[] f, float[] d, int[] v, float[] z)
    {
        var n = f.Length;
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                var p = v[k];
                s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                if (s > z[k])
                    break;
                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            var p = v[k];
            d[q] = (q - p) * (q - p) + f[p];
        }
    }

    // Separable Gaussian blur, clamped at the borders
    private static float[] Blur(float[] source, int size)
    {
        var radius = (int)Math.Ceiling(3 * BlurSigma);
        var weights = new float[2 * radius + 1];
        for (var i = -radius; i <= radius; i++)
            weights[i + radius] = (float)Math.Exp(-i * i / (2 * BlurSigma * BlurSigma));
        var total = weights.Sum();
        for (var i = 0; i < weights.Length; i++)
            weights[i] /= total;

        var rows = new float[size * size];
        var result = new float[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float sum = 0;
            for (var i = -radius; i <= radius; i++)
                sum += weights[i + radius] * source[y * size + Clamp(x + i, size)];
            rows[y * size + x] = sum;
        }

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float sum = 0;
            for (var i = -radius; i <= radius; i++)
                sum += weights[i + radius] * rows[Clamp(y + i, size) * size + x];
            result[y * size + x] = sum;
        }

        return result;
    }

    private static int Clamp(int i, int size) =>
        i < 0 ? 0
        : i >= size ? size - 1
        : i;

    // DDS header with the DX10 extension for a square R8_UNORM texture with a full mip chain
    private static void WriteR8Header(BinaryWriter w, int size, int mipCount)
    {
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124); // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000); // caps, height, width, pitch, pixel format, mip count
        w.Write(size); // height
        w.Write(size); // width
        w.Write(size); // pitch of one row
        w.Write(0); // depth
        w.Write(mipCount);
        for (var i = 0; i < 11; i++)
            w.Write(0); // reserved
        w.Write(32); // pixel format size
        w.Write(0x4); // DDPF_FOURCC
        w.Write(Encoding.ASCII.GetBytes("DX10"));
        for (var i = 0; i < 5; i++)
            w.Write(0); // bit count and masks
        w.Write(0x1000 | 0x8 | 0x400000); // texture, complex, mipmap
        for (var i = 0; i < 4; i++)
            w.Write(0); // caps2, caps3, caps4, reserved
        w.Write(61); // DXGI_FORMAT_R8_UNORM
        w.Write(3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0); // misc flags
        w.Write(1); // array size
        w.Write(0); // misc flags 2
    }
}
