using System;

/// <summary>
/// Generates a soft-focus, photo-like backdrop - a muted wall with out-of-focus lights above a wooden desk - as
/// raw RGB bytes. Deliberately low in saturation and detail: it should read as a real place without competing
/// with the cube's sticker colors (a bright orange table next to orange stickers would confuse a vision model).
/// Pure C# with no Unity dependency, so the same code can be previewed outside the editor. Rows are top-down.
/// </summary>
public static class ProceduralBackdrop
{
    private const float Horizon = 0.62f;   // where the wall meets the desk, as a fraction of the height

    public static byte[] GenerateRgb(int width, int height, int seed = 7)
    {
        var px = new float[width * height * 3];

        // Out-of-focus point lights on the wall, like a lamp or fairy lights far behind the subject.
        var rng = new Random(seed);
        int bokehCount = 16;
        var bokeh = new float[bokehCount, 7]; // x, y, radius, r, g, b, strength
        for (int i = 0; i < bokehCount; i++)
        {
            bool warm = rng.NextDouble() < 0.6;
            bokeh[i, 0] = 0.05f + 0.9f * (float)rng.NextDouble();
            bokeh[i, 1] = 0.03f + 0.5f * (float)rng.NextDouble();
            bokeh[i, 2] = 0.018f + 0.035f * (float)rng.NextDouble();
            bokeh[i, 3] = warm ? 1.00f : 0.85f;
            bokeh[i, 4] = warm ? 0.90f : 0.93f;
            bokeh[i, 5] = warm ? 0.72f : 1.00f;
            bokeh[i, 6] = 0.07f + 0.13f * (float)rng.NextDouble();
        }

        float aspect = width / (float)height;

        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                float r, g, b;

                float onWall = 1f - Smoothstep(Horizon - 0.01f, Horizon + 0.01f, v);

                // ---- wall: cool light grey, darker toward the top, with faint plaster texture and window glow
                float wy = Math.Min(1f, v / Horizon);
                float shade = Lerp(0.60f, 0.82f, (float)Math.Pow(wy, 0.8));
                float plaster = (Fbm(u * 5f * aspect, v * 5f, seed, 4) - 0.5f) * 0.06f;
                float wr = shade * 0.97f + plaster;
                float wg = shade * 0.99f + plaster;
                float wb = shade * 1.03f + plaster;

                float du = (u - 0.16f) * aspect, dv = v - 0.26f;
                float glow = (float)Math.Exp(-(du * du + dv * dv) / (2f * 0.28f * 0.28f)) * 0.14f;
                wr += glow * 0.95f; wg += glow * 0.98f; wb += glow;

                for (int i = 0; i < bokehCount; i++)
                {
                    float bx = (u - bokeh[i, 0]) * aspect, by = v - bokeh[i, 1];
                    float dist = (float)Math.Sqrt(bx * bx + by * by);
                    float rad = bokeh[i, 2] * aspect * 0.55f;
                    // A disk with a slightly brighter rim, the look of a real defocused highlight.
                    float disk = 1f - Smoothstep(rad * 0.88f, rad, dist);
                    float rim = Smoothstep(rad * 0.55f, rad * 0.9f, dist) * disk * 0.35f;
                    float k = (disk * 0.65f + rim) * bokeh[i, 6];
                    wr += k * bokeh[i, 3]; wg += k * bokeh[i, 4]; wb += k * bokeh[i, 5];
                }

                // ---- desk: muted oak boards running toward the viewer, converging to a vanishing point
                float tv = Clamp01((v - Horizon) / (1f - Horizon));
                float depth = 1f / (tv * 0.85f + 0.10f);
                float lateral = (u - 0.5f) * aspect * depth * 0.55f;
                float boardPos = lateral * 2.4f + 100f;
                int board = (int)Math.Floor(boardPos);
                float f = boardPos - board;
                float seam = Smoothstep(0f, 0.025f, f) * Smoothstep(0f, 0.025f, 1f - f);
                float tone = 0.86f + 0.28f * Hash(board, 3, seed);
                float grain = Fbm(lateral * 38f + board * 11.3f, depth * 2.4f, seed + 1, 3);
                float rings = 0.5f + 0.5f * (float)Math.Sin(grain * 9f + lateral * 22f);
                float wood = tone * (0.92f + 0.22f * (grain - 0.5f) + 0.06f * (rings - 0.5f));
                wood *= 1f - 0.5f * (1f - seam);
                // Window light reflecting off the varnish, and a gentle fall-off toward the viewer.
                float hx = (u - 0.25f) * aspect, hy = tv - 0.22f;
                float gloss = (float)Math.Exp(-(hx * hx * 1.2f + hy * hy * 7f)) * 0.17f;
                float light = 1f - 0.28f * tv;
                float dr = (0.50f * wood + gloss) * light;
                float dg = (0.42f * wood + gloss * 0.97f) * light;
                float db = (0.35f * wood + gloss * 0.92f) * light;

                r = Lerp(dr, wr, onWall);
                g = Lerp(dg, wg, onWall);
                b = Lerp(db, wb, onWall);

                int i3 = (y * width + x) * 3;
                px[i3] = r; px[i3 + 1] = g; px[i3 + 2] = b;
            }
        }

        // Everything behind a subject is out of focus in a real photo: soften the whole thing.
        BoxBlur(px, width, height, Math.Max(1, width / 380), 3);

        var bytes = new byte[width * height * 3];
        var grainRng = new Random(seed + 99);
        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                // Vignette plus a little film grain, the two cues that make a flat gradient look photographed.
                float cx = (u - 0.5f) * 1.1f, cy = (v - 0.5f) * 1.1f;
                float vignette = 1f - 0.42f * (cx * cx + cy * cy) * 1.6f;
                float noise = ((float)grainRng.NextDouble() - 0.5f) * 0.025f;
                int i3 = (y * width + x) * 3;
                for (int c = 0; c < 3; c++)
                    bytes[i3 + c] = (byte)Math.Round(Clamp01(px[i3 + c] * vignette + noise) * 255f);
            }
        }
        return bytes;
    }

    // ------------------------------------------------------------------ helpers

    private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + seed * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = x - x0, fy = y - y0;
        float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
        float a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed);
        float c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, sx), Lerp(c, d, sx), sy);
    }

    private static float Fbm(float x, float y, int seed, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * ValueNoise(x, y, seed + o * 17);
            norm += amp;
            x *= 2f; y *= 2f; amp *= 0.5f;
        }
        return sum / norm;
    }

    // Repeated box blurs approximate a gaussian; each pass is O(pixels) via a running sum.
    private static void BoxBlur(float[] data, int w, int h, int radius, int passes)
    {
        var line = new float[Math.Max(w, h)];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++) line[x] = data[(y * w + x) * 3 + c];
                    BlurLine(line, w, radius);
                    for (int x = 0; x < w; x++) data[(y * w + x) * 3 + c] = line[x];
                }
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++) line[y] = data[(y * w + x) * 3 + c];
                    BlurLine(line, h, radius);
                    for (int y = 0; y < h; y++) data[(y * w + x) * 3 + c] = line[y];
                }
            }
        }
    }

    private static void BlurLine(float[] line, int n, int radius)
    {
        var src = new float[n];
        Array.Copy(line, src, n);
        float window = 2 * radius + 1;
        float sum = 0f;
        for (int i = -radius; i <= radius; i++) sum += src[Math.Min(n - 1, Math.Max(0, i))];
        for (int i = 0; i < n; i++)
        {
            line[i] = sum / window;
            sum += src[Math.Min(n - 1, i + radius + 1)] - src[Math.Max(0, i - radius)];
        }
    }
}
