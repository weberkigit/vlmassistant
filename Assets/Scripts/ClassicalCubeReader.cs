using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// A classical (no neural network) reader for a Rubik's Cube photo, the counterpart of the VLM face reader.
/// It produces the same thing - for each clearly visible face: the center color, the center color of a visible
/// neighbouring face across the top edge of the reading, and the nine sticker colors in that reading order - so its
/// output goes through exactly the same orientation, voting and trust code.
///
/// Pipeline (all on raw RGB pixels, no libraries):
///   1. Segment the cube: the black plastic body is the largest dark region; its convex hull is the cube silhouette.
///      Everything inside the hull that is not black is a sticker, so the backdrop - even a white wall - is ignored.
///   2. Find stickers: connected components of that mask, each reduced to a quadrilateral (corner points) and a color.
///   3. Group stickers into faces: stickers of one face form a regular grid, so neighbours sit one pitch away along
///      the sticker's own edge directions and share their edge directions. Linked blobs form components of nine.
///   4. Build each face's 3x3 grid in the order it appears in the image, classify every sticker's color from its hue,
///      saturation and brightness, and use the middle sticker as the face's identity.
///   5. Orientation: find a visible face on the other side of one of the grid's edges and rotate the reading so that
///      edge is on top; that neighbour's center color is the "up" value.
///
/// Rows are top-down, three bytes (R, G, B) per pixel.
/// </summary>
public static class ClassicalCubeReader
{
    /// <summary>When true, Result.Debug gets a line per sticker blob (for tuning outside Unity).</summary>
    public static bool Verbose;

    /// <summary>Diagnostics only: run just one segmentation (0 body, 1 color-only, 2 plain background); -1 = all.</summary>
    public static int OnlyMode = -1;

    public class Result
    {
        public readonly List<string> Debug = new List<string>();
        public readonly List<CubeVlmMemory.RawFace> Faces = new List<CubeVlmMemory.RawFace>();
        public int StickersFound;
        public string Report = "";
        /// <summary>Every sticker candidate that was found (image coordinates, top-down), for drawing what the reader saw.</summary>
        public readonly List<Mark> Marks = new List<Mark>();
        /// <summary>The detected outline of the cube as x,y pairs (image coordinates, top-down); empty if none.</summary>
        public readonly List<float[]> Silhouette = new List<float[]>();
    }

    public class Mark
    {
        public float X, Y;
        public char Letter;
        public bool InFace;   // part of a 3x3 face that was read
    }

    // ---------------------------------------------------------------- tunables

    private const float DarkValue = 0.20f;          // below this brightness a pixel is black plastic
    private const float MinStickerValue = 0.28f;    // darker than this inside the cube is treated as body
    private const float WhiteMaxSaturation = 0.30f;
    private const float ColoredMinSaturation = 0.45f; // color-only mode: a pixel this saturated is a colored sticker
    private const float WhiteMaxSaturationStrict = 0.20f; // ... and this pale and bright is a white one
    private const float WhiteMinValue = 0.50f;
    private const float WhiteMinValuePlain = 0.72f; // on a plain background the pale gap lines are the only thing to avoid
    private const int MinStickerArea = 60;          // pixels
    private const float LinkTolerance = 0.30f;      // in units of the sticker's edge length
    private const float DirectionToleranceDegrees = 24f;

    // ---------------------------------------------------------------- small math helpers

    private struct Vec
    {
        public float X, Y;
        public Vec(float x, float y) { X = x; Y = y; }
        public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.X - b.X, a.Y - b.Y);
        public static Vec operator *(Vec a, float s) => new Vec(a.X * s, a.Y * s);
        public float Length => (float)Math.Sqrt(X * X + Y * Y);
    }

    private class Blob
    {
        public int Area;
        public Vec Center;
        public Vec U, V;           // the sticker's two edge vectors (image space)
        public float Hue, Sat, Val;
        public char Letter = '?';
        public float Solidity;
        public int Component = -1;
    }

    private static float Cross(Vec a, Vec b) => a.X * b.Y - a.Y * b.X;

    // Angle of a direction in degrees, folded into [0, 180) - edges have no sign.
    private static float Axis(Vec v)
    {
        float a = (float)(Math.Atan2(v.Y, v.X) * 180.0 / Math.PI);
        a %= 180f;
        if (a < 0f) a += 180f;
        return a;
    }

    private static float AxisDiff(float a, float b)
    {
        float d = Math.Abs(a - b) % 180f;
        return d > 90f ? 180f - d : d;
    }

    // ---------------------------------------------------------------- color

    public static void ToHsv(byte r8, byte g8, byte b8, out float h, out float s, out float v)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        v = max;
        s = max <= 0f ? 0f : d / max;
        if (d <= 1e-6f) { h = 0f; return; }
        if (max == r) h = 60f * (((g - b) / d) % 6f);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - g) / d + 4f);
        if (h < 0f) h += 360f;
    }

    /// <summary>W Y R O G B from hue, saturation and brightness; '?' if the color fits none of them.</summary>
    public static char Classify(float hue, float sat, float val)
    {
        if (sat < WhiteMaxSaturation && val > 0.30f) return 'W';
        if (sat < 0.30f) return '?';
        if (hue < 12f || hue >= 340f) return 'R';
        if (hue < 42f) return 'O';
        if (hue < 75f) return 'Y';
        if (hue < 170f) return 'G';
        if (hue < 265f) return 'B';
        return '?';
    }

    // ---------------------------------------------------------------- image helpers

    private static int[] Label(bool[] mask, int w, int h, out int count)
    {
        var label = new int[mask.Length];
        var stack = new int[mask.Length];
        count = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0) continue;
            count++;
            int sp = 0;
            stack[sp++] = start;
            label[start] = count;
            while (sp > 0)
            {
                int p = stack[--sp];
                int x = p % w, y = p / w;
                if (x > 0 && mask[p - 1] && label[p - 1] == 0) { label[p - 1] = count; stack[sp++] = p - 1; }
                if (x < w - 1 && mask[p + 1] && label[p + 1] == 0) { label[p + 1] = count; stack[sp++] = p + 1; }
                if (y > 0 && mask[p - w] && label[p - w] == 0) { label[p - w] = count; stack[sp++] = p - w; }
                if (y < h - 1 && mask[p + w] && label[p + w] == 0) { label[p + w] = count; stack[sp++] = p + w; }
            }
        }
        return label;
    }

    private static bool[] Erode(bool[] mask, int w, int h, int iterations)
    {
        bool[] current = mask;
        for (int it = 0; it < iterations; it++)
        {
            var next = new bool[mask.Length];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    next[i] = current[i] && current[i - 1] && current[i + 1] && current[i - w] && current[i + w];
                }
            current = next;
        }
        return current;
    }

    // Grows every label outwards by the given number of pixels, but only over pixels that are in the mask.
    private static void Regrow(int[] label, bool[] mask, int w, int h, int iterations)
    {
        for (int it = 0; it < iterations; it++)
        {
            var previous = (int[])label.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!mask[i] || previous[i] != 0) continue;
                    int l = 0;
                    if (x > 0 && previous[i - 1] != 0) l = previous[i - 1];
                    else if (x < w - 1 && previous[i + 1] != 0) l = previous[i + 1];
                    else if (y > 0 && previous[i - w] != 0) l = previous[i - w];
                    else if (y < h - 1 && previous[i + w] != 0) l = previous[i + w];
                    label[i] = l;
                }
        }
    }

    // Like Label, but two neighbouring pixels only connect if they have the same (non-zero) class.
    private static int[] LabelClasses(int[] cls, int w, int h, out int count)
    {
        var label = new int[cls.Length];
        var stack = new int[cls.Length];
        count = 0;
        for (int start = 0; start < cls.Length; start++)
        {
            if (cls[start] == 0 || label[start] != 0) continue;
            count++;
            int c = cls[start];
            int sp = 0;
            stack[sp++] = start;
            label[start] = count;
            while (sp > 0)
            {
                int p = stack[--sp];
                int x = p % w, y = p / w;
                if (x > 0 && cls[p - 1] == c && label[p - 1] == 0) { label[p - 1] = count; stack[sp++] = p - 1; }
                if (x < w - 1 && cls[p + 1] == c && label[p + 1] == 0) { label[p + 1] = count; stack[sp++] = p + 1; }
                if (y > 0 && cls[p - w] == c && label[p - w] == 0) { label[p - w] = count; stack[sp++] = p - w; }
                if (y < h - 1 && cls[p + w] == c && label[p + w] == 0) { label[p + w] = count; stack[sp++] = p + w; }
            }
        }
        return label;
    }

    // Erosion that also eats pixels next to a different class, so class boundaries become gaps.
    private static int[] ErodeClasses(int[] cls, int w, int h, int iterations)
    {
        int[] current = cls;
        for (int it = 0; it < iterations; it++)
        {
            var next = new int[cls.Length];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    int c = current[i];
                    if (c != 0 && current[i - 1] == c && current[i + 1] == c && current[i - w] == c && current[i + w] == c)
                        next[i] = c;
                }
            current = next;
        }
        return current;
    }

    // Grows labels back over the original class map, only into pixels of the same class as the label.
    private static void RegrowClasses(int[] label, int[] cls, int w, int h, int iterations)
    {
        for (int it = 0; it < iterations; it++)
        {
            var previous = (int[])label.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (cls[i] == 0 || previous[i] != 0) continue;
                    int c = cls[i], l = 0;
                    if (x > 0 && previous[i - 1] != 0 && cls[i - 1] == c) l = previous[i - 1];
                    else if (x < w - 1 && previous[i + 1] != 0 && cls[i + 1] == c) l = previous[i + 1];
                    else if (y > 0 && previous[i - w] != 0 && cls[i - w] == c) l = previous[i - w];
                    else if (y < h - 1 && previous[i + w] != 0 && cls[i + w] == c) l = previous[i + w];
                    label[i] = l;
                }
        }
    }

    // Two or three neighbouring stickers of the same color can fuse into one long blob. A blob whose edge is much longer
    // than the same edge of nearby blobs (same direction, so same face) is cut into that many equal parts.
    private static List<Blob> SplitMergedBlobs(List<Blob> blobs)
    {
        var result = new List<Blob>();
        foreach (Blob blob in blobs)
        {
            Blob current = blob;
            var parts = new List<Blob> { current };
            for (int axisIndex = 0; axisIndex < 2; axisIndex++)
            {
                var next = new List<Blob>();
                foreach (Blob part in parts)
                {
                    Vec axis = axisIndex == 0 ? part.U : part.V;
                    float reference = ReferenceLength(part, axis, blobs);
                    int pieces = reference > 0f ? (int)Math.Round(axis.Length / reference) : 1;
                    if (pieces < 2 || axis.Length < reference * 1.55f) { next.Add(part); continue; }
                    pieces = Math.Min(pieces, 3);
                    for (int k = 0; k < pieces; k++)
                    {
                        float t = (k + 0.5f) / pieces - 0.5f;
                        var child = new Blob
                        {
                            Area = part.Area / pieces,
                            Center = part.Center + axis * t,
                            U = axisIndex == 0 ? part.U * (1f / pieces) : part.U,
                            V = axisIndex == 1 ? part.V * (1f / pieces) : part.V,
                            Hue = part.Hue, Sat = part.Sat, Val = part.Val,
                            Letter = part.Letter, Solidity = part.Solidity,
                        };
                        next.Add(child);
                    }
                }
                parts = next;
            }
            result.AddRange(parts);
        }
        return result;
    }

    // Median length, over nearby blobs, of the edge that runs the same way as the given axis; 0 if there are too few.
    private static float ReferenceLength(Blob blob, Vec axis, List<Blob> all)
    {
        float direction = Axis(axis);
        float reach = Math.Max(blob.U.Length, blob.V.Length) * 3f;
        var lengths = new List<float>();
        foreach (Blob other in all)
        {
            if (other == blob || (other.Center - blob.Center).Length > reach) continue;
            if (AxisDiff(Axis(other.U), direction) < 25f) lengths.Add(other.U.Length);
            else if (AxisDiff(Axis(other.V), direction) < 25f) lengths.Add(other.V.Length);
        }
        if (lengths.Count < 2) return 0f;
        lengths.Sort();
        return lengths[lengths.Count / 2];
    }

    private static List<Vec> ConvexHull(List<Vec> points)
    {
        var pts = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        var hull = new List<Vec>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (Vec p in pts)
            {
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 2]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            pts.Reverse();
        }
        return hull;
    }

    private static float PolygonArea(IList<Vec> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Count; i++) a += Cross(poly[i], poly[(i + 1) % poly.Count]);
        return Math.Abs(a) * 0.5f;
    }

    // The four hull points that enclose the most area: for a sticker this recovers its corners.
    private static List<Vec> BestQuad(List<Vec> hull)
    {
        List<Vec> pts = hull;
        if (pts.Count > 22)
        {
            pts = new List<Vec>();
            for (int i = 0; i < 22; i++) pts.Add(hull[i * hull.Count / 22]);
        }
        if (pts.Count <= 4) return pts;

        float best = -1f;
        int bi = 0, bj = 1, bk = 2, bl = 3;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = j + 1; k < n; k++)
                    for (int l = k + 1; l < n; l++)
                    {
                        float a = PolygonArea(new[] { pts[i], pts[j], pts[k], pts[l] });
                        if (a > best) { best = a; bi = i; bj = j; bk = k; bl = l; }
                    }
        return new List<Vec> { pts[bi], pts[bj], pts[bk], pts[bl] };
    }

    // ---------------------------------------------------------------- the pipeline

    // For a render on a plain (near-black) background: the cube is the biggest piece that is not background, and its
    // convex hull is the silhouette. Returns null if the picture's border is not plain dark.
    private static bool[] TryPlainBackgroundSilhouette(float[] val, int width, int height, Result result, StringBuilder report)
    {
        int n = width * height;
        int border = 0, darkBorder = 0;
        for (int x = 0; x < width; x++)
        {
            border += 2;
            if (val[x] < 0.08f) darkBorder++;
            if (val[(height - 1) * width + x] < 0.08f) darkBorder++;
        }
        for (int y = 1; y < height - 1; y++)
        {
            border += 2;
            if (val[y * width] < 0.08f) darkBorder++;
            if (val[y * width + width - 1] < 0.08f) darkBorder++;
        }
        if (darkBorder < border * 0.9f) { report.Append("background not plain; "); return null; }

        var lit = new bool[n];
        for (int i = 0; i < n; i++) lit[i] = val[i] >= 0.10f;
        int[] label = Label(lit, width, height, out int count);
        if (count == 0) { report.Append("nothing but background; "); return null; }
        var areas = new int[count + 1];
        foreach (int l in label) if (l > 0) areas[l]++;
        int cube = 1;
        for (int l = 2; l <= count; l++) if (areas[l] > areas[cube]) cube = l;
        if (areas[cube] < n / 200) { report.Append("cube too small; "); return null; }

        var hullPoints = new List<Vec>();
        for (int y = 0; y < height; y++)
        {
            int minX = -1, maxX = -1;
            for (int x = 0; x < width; x++)
            {
                if (label[y * width + x] != cube) continue;
                if (minX < 0) minX = x;
                maxX = x;
            }
            if (minX >= 0) { hullPoints.Add(new Vec(minX, y)); hullPoints.Add(new Vec(maxX, y)); }
        }
        return FillHull(ConvexHull(hullPoints), width, height, result);
    }

    // Convex hull of the biggest very dark region, as a per-pixel mask; null if there is no such region.
    private static bool[] TryBodySilhouette(bool[] dark, int width, int height, Result result, StringBuilder report)
    {
        int n = width * height;
        int[] darkLabel = Label(dark, width, height, out int darkCount);
        if (darkCount == 0) { report.Append("no dark cube body (color-only mode); "); return null; }
        var darkAreas = new int[darkCount + 1];
        foreach (int l in darkLabel) if (l > 0) darkAreas[l]++;
        int body = 1;
        for (int l = 2; l <= darkCount; l++) if (darkAreas[l] > darkAreas[body]) body = l;
        if (darkAreas[body] < n / 200) { report.Append("dark body too small (color-only mode); "); return null; }

        var hullPoints = new List<Vec>();
        for (int y = 0; y < height; y++)
        {
            int minX = -1, maxX = -1;
            for (int x = 0; x < width; x++)
            {
                if (darkLabel[y * width + x] != body) continue;
                if (minX < 0) minX = x;
                maxX = x;
            }
            if (minX >= 0) { hullPoints.Add(new Vec(minX, y)); hullPoints.Add(new Vec(maxX, y)); }
        }
        List<Vec> silhouette = ConvexHull(hullPoints);
        if (silhouette.Count < 3) { report.Append("no silhouette (color-only mode); "); return null; }
        return FillHull(silhouette, width, height, result);
    }

    // Per-pixel mask of the inside of a convex polygon; also records the polygon in result.Silhouette. Null if degenerate.
    private static bool[] FillHull(List<Vec> silhouette, int width, int height, Result result)
    {
        if (silhouette.Count < 3) return null;
        foreach (Vec p in silhouette) result.Silhouette.Add(new[] { p.X, p.Y });
        int n = width * height;
        var inside = new bool[n];
        for (int y = 0; y < height; y++)
        {
            float xl = float.MaxValue, xr = float.MinValue;
            for (int e = 0; e < silhouette.Count; e++)
            {
                Vec p = silhouette[e], q = silhouette[(e + 1) % silhouette.Count];
                if (p.Y == q.Y)
                {
                    if (Math.Abs(y - p.Y) < 0.5f) { xl = Math.Min(xl, Math.Min(p.X, q.X)); xr = Math.Max(xr, Math.Max(p.X, q.X)); }
                    continue;
                }
                if (y < Math.Min(p.Y, q.Y) || y > Math.Max(p.Y, q.Y)) continue;
                float x = p.X + (y - p.Y) * (q.X - p.X) / (q.Y - p.Y);
                xl = Math.Min(xl, x);
                xr = Math.Max(xr, x);
            }
            if (xl > xr) continue;
            for (int x = Math.Max(0, (int)Math.Ceiling(xl)); x <= Math.Min(width - 1, (int)Math.Floor(xr)); x++)
                inside[y * width + x] = true;
        }
        return inside;
    }


    /// <summary>
    /// Reads whatever faces of the cube are visible. Tries two ways of finding the stickers - inside the silhouette of
    /// the dark plastic body, and by sticker color alone (for renders whose body isn't black or whose background is
    /// busy) - and keeps the one that reads more faces.
    /// </summary>
    public static Result Read(byte[] rgb, int width, int height)
    {
        if (OnlyMode >= 0) return ReadWith(rgb, width, height, (Segmentation)OnlyMode);

        // Most trustworthy first: on a plain background the silhouette is exact. A later way only wins by reading
        // strictly more faces, so ties stay with the cleaner segmentation.
        Segmentation[] order = { Segmentation.PlainBackground, Segmentation.Body, Segmentation.ColorOnly };
        Result best = null;
        string bestName = null;
        var notes = new List<string>();
        foreach (Segmentation mode in order)
        {
            Result other = ReadWith(rgb, width, height, mode);
            string name = mode == Segmentation.ColorOnly ? "color-only"
                : mode == Segmentation.Body ? "body silhouette" : "plain background";
            if (best == null) { best = other; bestName = name; continue; }
            if (Verbose) { best.Debug.Add("--- " + name); best.Debug.AddRange(other.Debug); }
            if (other.Faces.Count > best.Faces.Count)
            {
                notes.Add($"{bestName}: {best.Report}");
                best = other;
                bestName = name;
            }
            else
            {
                notes.Add($"{name}: {other.Report}");
            }
        }
        best.Report = $"[{bestName}] {best.Report}" + (best.Faces.Count == 0 ? " | " + string.Join(" | ", notes) : "");
        return best;
    }

    private enum Segmentation
    {
        Body,             // stickers inside the hull of the black plastic body
        ColorOnly,        // stickers by color alone, anywhere in the picture
        PlainBackground,  // stickers inside the hull of everything that is not a (near-)black background
    }

    private static Result ReadWith(byte[] rgb, int width, int height, Segmentation mode)
    {
        var result = new Result();
        var report = new StringBuilder();
        int n = width * height;

        // --- 1. segment the cube: black body -> convex hull -> sticker mask
        var hue = new float[n];
        var sat = new float[n];
        var val = new float[n];
        var dark = new bool[n];
        for (int i = 0; i < n; i++)
        {
            ToHsv(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], out hue[i], out sat[i], out val[i]);
            dark[i] = val[i] < DarkValue;
        }

        // The black plastic body gives a clean silhouette - when the render has one. If it does not (a lit grey body,
        // a busy background) the stickers are found by color alone and the 3x3 lattice test weeds out what isn't one.
        bool[] inside = mode == Segmentation.Body ? TryBodySilhouette(dark, width, height, result, report)
            : mode == Segmentation.PlainBackground ? TryPlainBackgroundSilhouette(val, width, height, result, report)
            : null;
        if (mode == Segmentation.PlainBackground && inside == null) { result.Report = report.ToString(); return result; }

        float whiteMin = mode == Segmentation.PlainBackground ? WhiteMinValuePlain : WhiteMinValue;
        var stickerMask = new bool[n];
        if (inside != null && mode == Segmentation.Body)
        {
            for (int i = 0; i < n; i++)
                stickerMask[i] = inside[i] && !dark[i] && val[i] >= MinStickerValue && !(sat[i] < 0.25f && val[i] < 0.5f); // not dull grey body
        }
        else
        {
            for (int i = 0; i < n; i++)
                stickerMask[i] = (inside == null || inside[i])
                    && ((sat[i] >= ColoredMinSaturation && val[i] >= 0.18f)
                        || (sat[i] < WhiteMaxSaturationStrict && val[i] >= whiteMin));
        }

        // --- 2. sticker blobs
        // Opening-by-labels: stickers of the same color are separated only by a thin dark line that anti-aliasing and
        // JPEG leave patchy, so shrink the mask until the lines cut, label the shrunken pieces, then grow the labels
        // back over the original mask.
        // Each pixel also gets its color class, and blobs never cross a class boundary: neighbouring stickers of
        // different colors are only a thin, blurred line apart, which a plain mask would happily bridge.
        var colorClass = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (!stickerMask[i]) continue;
            int k = "WYROGB".IndexOf(Classify(hue[i], sat[i], val[i]));
            colorClass[i] = k + 1; // 0 = none
        }
        int shrink = Math.Max(1, Math.Min(width, height) / 250);
        int[] blobLabel = LabelClasses(ErodeClasses(colorClass, width, height, shrink), width, height, out int blobCount);
        RegrowClasses(blobLabel, colorClass, width, height, shrink);
        var pixelsByBlob = new List<int>[blobCount + 1];
        for (int i = 0; i < n; i++)
        {
            int l = blobLabel[i];
            if (l == 0) continue;
            (pixelsByBlob[l] ?? (pixelsByBlob[l] = new List<int>())).Add(i);
        }

        var blobs = new List<Blob>();
        for (int l = 1; l <= blobCount; l++)
        {
            List<int> px = pixelsByBlob[l];
            if (px == null || px.Count < MinStickerArea) continue;

            // outline: leftmost / rightmost pixel of every row of the blob
            var rows = new Dictionary<int, (int min, int max)>();
            float sx = 0f, sy = 0f;
            foreach (int p in px)
            {
                int x = p % width, y = p / width;
                sx += x; sy += y;
                rows[y] = rows.TryGetValue(y, out var r) ? (Math.Min(r.min, x), Math.Max(r.max, x)) : (x, x);
            }
            var outline = new List<Vec>();
            foreach (var kv in rows) { outline.Add(new Vec(kv.Value.min, kv.Key)); outline.Add(new Vec(kv.Value.max, kv.Key)); }
            List<Vec> hull = ConvexHull(outline);
            if (hull.Count < 4) continue;
            float hullArea = PolygonArea(hull);
            float solidity = hullArea > 0f ? px.Count / hullArea : 0f;
            if (Verbose) result.Debug.Add($"component {l}: area {px.Count} hull {hullArea:0} solidity {solidity:0.00} center ({sx / px.Count:0},{sy / px.Count:0})");
            if (solidity < 0.70f) continue; // merged or ragged region, not a single sticker

            List<Vec> quad = BestQuad(hull);
            if (quad.Count < 4) continue;
            Vec c0 = quad[0], c1 = quad[1], c2 = quad[2], c3 = quad[3];
            Vec u = ((c1 - c0) + (c2 - c3)) * 0.5f;
            Vec v = ((c3 - c0) + (c2 - c1)) * 0.5f;
            if (u.Length < 4f || v.Length < 4f) continue;

            // color: average hue (as a vector), saturation and brightness over the blob
            double hx = 0, hy = 0, ss = 0, vv = 0;
            foreach (int p in px)
            {
                double rad = hue[p] * Math.PI / 180.0;
                hx += Math.Cos(rad) * sat[p]; hy += Math.Sin(rad) * sat[p];
                ss += sat[p]; vv += val[p];
            }
            float meanHue = (float)(Math.Atan2(hy, hx) * 180.0 / Math.PI);
            if (meanHue < 0f) meanHue += 360f;
            var blob = new Blob
            {
                Area = px.Count,
                Center = new Vec(sx / px.Count, sy / px.Count),
                U = u, V = v,
                Hue = meanHue, Sat = (float)(ss / px.Count), Val = (float)(vv / px.Count),
                Solidity = solidity,
            };
            blob.Letter = Classify(blob.Hue, blob.Sat, blob.Val);
            blobs.Add(blob);
        }

        if (Verbose)
            for (int i = 0; i < blobs.Count; i++)
                result.Debug.Add($"blob {i}: c=({blobs[i].Center.X:0},{blobs[i].Center.Y:0}) area={blobs[i].Area} " +
                    $"u=({blobs[i].U.X:0.0},{blobs[i].U.Y:0.0}) |u|={blobs[i].U.Length:0.0} axis={Axis(blobs[i].U):0} " +
                    $"v=({blobs[i].V.X:0.0},{blobs[i].V.Y:0.0}) |v|={blobs[i].V.Length:0.0} axis={Axis(blobs[i].V):0} letter={blobs[i].Letter}");

        blobs = SplitMergedBlobs(blobs);

        // Stickers of one cube have similar sizes; drop blobs that are far off the typical area.
        if (blobs.Count > 0)
        {
            float median = blobs.Select(b => (float)b.Area).OrderBy(a => a).ElementAt(blobs.Count / 2);
            blobs = blobs.Where(b => b.Area > median * 0.25f && b.Area < median * 4f).ToList();
        }
        result.StickersFound = blobs.Count;
        foreach (Blob b in blobs) result.Marks.Add(new Mark { X = b.Center.X, Y = b.Center.Y, Letter = b.Letter });
        report.Append($"{blobs.Count} sticker blobs");
        if (blobs.Count < 9) { result.Report = report + "; fewer than 9, no face"; return result; }

        // --- 3. group stickers into faces
        // Pitch: how many sticker edge lengths separate the centres of neighbouring stickers (median over the data).
        var ratios = new List<float>();
        foreach (Blob a in blobs)
        {
            foreach (Blob b in blobs)
            {
                if (a == b) continue;
                if (!TryCoordinates(a, b, out float alpha, out float beta)) continue;
                if (Math.Abs(beta) < 0.25f && Math.Abs(alpha) > 0.9f && Math.Abs(alpha) < 1.9f) ratios.Add(Math.Abs(alpha));
                if (Math.Abs(alpha) < 0.25f && Math.Abs(beta) > 0.9f && Math.Abs(beta) < 1.9f) ratios.Add(Math.Abs(beta));
            }
        }
        if (ratios.Count == 0) { result.Report = report + "; no neighbouring stickers"; return result; }
        float pitch = ratios.OrderBy(r => r).ElementAt(ratios.Count / 2);
        report.Append($", pitch {pitch:0.00}");

        var parent = Enumerable.Range(0, blobs.Count).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (int i = 0; i < blobs.Count; i++)
            for (int j = i + 1; j < blobs.Count; j++)
                if (AreNeighbours(blobs[i], blobs[j], pitch) && AreNeighbours(blobs[j], blobs[i], pitch))
                    parent[Find(i)] = Find(j);

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < blobs.Count; i++)
        {
            int root = Find(i);
            if (!groups.ContainsKey(root)) groups[root] = new List<int>();
            groups[root].Add(i);
        }
        report.Append($", groups [{string.Join(",", groups.Values.Select(g => g.Count))}]");

        // --- 4. build each face
        // The color at a point of the picture (majority of the pixels around it) - used to read a sticker whose blob
        // was lost to a reflection or a bad cut, at the place the rest of its face says it must be.
        char SampleLetter(Vec c)
        {
            int cx = (int)Math.Round(c.X), cy = (int)Math.Round(c.Y);
            int reach = Math.Max(2, Math.Min(width, height) / 150);
            var counts = new Dictionary<char, int>();
            int total = 0;
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    int i = y * width + x;
                    char l = Classify(hue[i], sat[i], val[i]);
                    counts.TryGetValue(l, out int k);
                    counts[l] = k + 1;
                    total++;
                }
            if (total == 0) return '?';
            var best = counts.OrderByDescending(kv => kv.Value).First();
            return best.Value >= total * 0.6 ? best.Key : '?';
        }

        var faces = new List<FaceGrid>();
        foreach (List<int> group in groups.Values)
        {
            if (group.Count < MinBlockStickers) continue;
            faces.AddRange(FindFaces(group.Select(i => blobs[i]).ToList(), pitch, SampleLetter));
        }
        report.Append($", {faces.Count} faces");
        if (faces.Count == 0) { result.Report = report.ToString(); return result; }
        foreach (Mark m in result.Marks)
            foreach (FaceGrid f in faces)
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        if (Math.Abs(f.P[r, c].X - m.X) < 1f && Math.Abs(f.P[r, c].Y - m.Y) < 1f) m.InFace = true;

        // --- 5. orientation from a visible neighbour
        foreach (FaceGrid face in faces)
        {
            if (face.Letters[1, 1] == '?') continue;

            // Probe points one sticker step beyond the middle of each edge, in reading order: top, right, bottom, left.
            Vec[] probe =
            {
                face.P[0, 1] + (face.P[0, 1] - face.P[1, 1]),
                face.P[1, 2] + (face.P[1, 2] - face.P[1, 1]),
                face.P[2, 1] + (face.P[2, 1] - face.P[1, 1]),
                face.P[1, 0] + (face.P[1, 0] - face.P[1, 1]),
            };
            float[] step =
            {
                (face.P[0, 1] - face.P[1, 1]).Length, (face.P[1, 2] - face.P[1, 1]).Length,
                (face.P[2, 1] - face.P[1, 1]).Length, (face.P[1, 0] - face.P[1, 1]).Length,
            };

            int edge = -1;
            char neighbourCenter = '\0';
            for (int e = 0; e < 4 && edge < 0; e++)
            {
                FaceGrid best = null;
                float bestDistance = step[e] * 0.9f;
                foreach (FaceGrid other in faces)
                {
                    if (other == face) continue;
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++)
                        {
                            float d = (other.P[r, c] - probe[e]).Length;
                            if (d < bestDistance) { bestDistance = d; best = other; }
                        }
                }
                if (best != null && best.Letters[1, 1] != '?') { edge = e; neighbourCenter = best.Letters[1, 1]; }
            }

            // Turn the reading so the edge that has a neighbour is the top edge.
            char[,] letters = face.Letters;
            if (edge == 1) letters = RotateCcw(letters);                      // right edge -> top
            else if (edge == 2) letters = RotateCcw(RotateCcw(letters));       // bottom edge -> top
            else if (edge == 3) letters = RotateCw(letters);                   // left edge -> top

            var sb = new StringBuilder(9);
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) sb.Append(letters[r, c]);
            result.Faces.Add(new CubeVlmMemory.RawFace
            {
                center = face.Letters[1, 1].ToString(),
                up = edge >= 0 ? neighbourCenter.ToString() : null,
                stickers = sb.ToString(),
            });
        }

        result.Report = report.ToString();
        return result;
    }

    // ---------------------------------------------------------------- face construction

    private class FaceGrid
    {
        public Vec[,] P = new Vec[3, 3];       // sticker centres, in reading order (row 0 = top of the image)
        public char[,] Letters = new char[3, 3];
    }

    // Coordinates of b's centre in a's own (u, v) frame: centre_b - centre_a = alpha * u + beta * v.
    private static bool TryCoordinates(Blob a, Blob b, out float alpha, out float beta)
    {
        alpha = beta = 0f;
        float det = Cross(a.U, a.V);
        if (Math.Abs(det) < 1e-3f) return false;
        Vec d = b.Center - a.Center;
        alpha = Cross(d, a.V) / det;
        beta = Cross(a.U, d) / det;
        return true;
    }

    // b is one pitch away from a along one of a's edge directions, and has the same edge directions as a.
    private static bool AreNeighbours(Blob a, Blob b, float pitch)
    {
        if (!TryCoordinates(a, b, out float alpha, out float beta)) return false;
        bool along = (Math.Abs(Math.Abs(alpha) - pitch) < LinkTolerance * pitch && Math.Abs(beta) < LinkTolerance)
                  || (Math.Abs(Math.Abs(beta) - pitch) < LinkTolerance * pitch && Math.Abs(alpha) < LinkTolerance);
        if (!along) return false;

        float au = Axis(a.U), av = Axis(a.V), bu = Axis(b.U), bv = Axis(b.V);
        float straight = AxisDiff(au, bu) + AxisDiff(av, bv);
        float crossed = AxisDiff(au, bv) + AxisDiff(av, bu);
        return Math.Min(straight, crossed) < 2f * DirectionToleranceDegrees;
    }

    // Looks for 3x3 blocks of stickers inside a group. A group is normally one face, but where two faces meet at a
    // shallow angle their stickers can be linked into one group of 18; the blocks that are really faces are the ones
    // whose rows and columns are straight and evenly spaced - across a crease the centres jump.
    private static List<FaceGrid> FindFaces(List<Blob> group, float pitch, Func<Vec, char> sample)
    {
        var candidates = new List<(float score, Dictionary<(int, int), Blob> cells)>();
        foreach (Blob seed in group)
        {
            var cells = TryBlock(seed, group, pitch);
            if (cells == null) continue;
            float score = Straightness(cells);
            if (score < MaxCurvature) candidates.Add((score, cells));
        }

        // Fullest blocks first, then the straightest; no sticker used twice.
        var used = new HashSet<Blob>();
        var faces = new List<FaceGrid>();
        foreach (var candidate in candidates.OrderByDescending(c => c.cells.Count).ThenBy(c => c.score))
        {
            if (candidate.cells.Values.Any(used.Contains)) continue;
            var complete = CompleteBlock(candidate.cells, sample);
            FaceGrid face = MakeFace(complete);
            if (face == null) continue;
            foreach (Blob b in candidate.cells.Values) used.Add(b);
            faces.Add(face);
        }
        return faces;
    }

    private const float MaxCurvature = 0.12f;
    private const int MinBlockStickers = 7; // of 9; the rest are read from the picture at their predicted places

    // Fills the cells of a block that has no sticker blob with a stand-in at the position the lattice predicts,
    // reading its color straight from the picture.
    private static Dictionary<(int, int), Blob> CompleteBlock(Dictionary<(int, int), Blob> cells, Func<Vec, char> sample)
    {
        if (cells.Count == 9) return cells;

        Vec stepI = new Vec(0, 0), stepJ = new Vec(0, 0);
        int nI = 0, nJ = 0;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                if (i < 1 && cells.TryGetValue((i, j), out Blob a) && cells.TryGetValue((i + 1, j), out Blob b)) { stepI = stepI + (b.Center - a.Center); nI++; }
                if (j < 1 && cells.TryGetValue((i, j), out Blob c) && cells.TryGetValue((i, j + 1), out Blob d)) { stepJ = stepJ + (d.Center - c.Center); nJ++; }
            }
        stepI = stepI * (1f / Math.Max(1, nI));
        stepJ = stepJ * (1f / Math.Max(1, nJ));

        var full = new Dictionary<(int, int), Blob>(cells);
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                if (full.ContainsKey((i, j))) continue;
                (int, int) nearest = (0, 0);
                int bestDistance = int.MaxValue;
                foreach (var key in cells.Keys)
                {
                    int dist = Math.Abs(key.Item1 - i) + Math.Abs(key.Item2 - j);
                    if (dist < bestDistance) { bestDistance = dist; nearest = key; }
                }
                Vec centre = cells[nearest].Center + stepI * (i - nearest.Item1) + stepJ * (j - nearest.Item2);
                full[(i, j)] = new Blob { Center = centre, Letter = sample(centre) };
            }
        return full;
    }

    // The 3x3 block centred on seed, in the seed's own frame, or null if some cell is empty or doubly occupied.
    private static Dictionary<(int, int), Blob> TryBlock(Blob seed, List<Blob> pool, float pitch)
    {
        var cells = new Dictionary<(int, int), Blob>();
        foreach (Blob b in pool)
        {
            if (!TryCoordinates(seed, b, out float alpha, out float beta)) continue;
            float ai = alpha / pitch, bj = beta / pitch;
            int i = (int)Math.Round(ai), j = (int)Math.Round(bj);
            if (Math.Abs(i) > 1 || Math.Abs(j) > 1) continue;
            if (Math.Abs(ai - i) > 0.35f || Math.Abs(bj - j) > 0.35f) continue;
            if (cells.ContainsKey((i, j))) return null;
            cells[(i, j)] = b;
        }
        return cells.Count >= MinBlockStickers ? cells : null;
    }

    // Worst deviation, over the three rows and three columns, of the middle sticker from the midpoint of the two
    // end stickers (relative to the line's length); evenly spaced collinear centres score 0.
    private static float Straightness(Dictionary<(int, int), Blob> cells)
    {
        float worst = 0f;
        int lines = 0;
        for (int k = -1; k <= 1; k++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                // Lines with a missing sticker can't be measured; the others still say whether the block is a face.
                if (!cells.TryGetValue(axis == 0 ? (-1, k) : (k, -1), out Blob first)) continue;
                if (!cells.TryGetValue(axis == 0 ? (0, k) : (k, 0), out Blob middle)) continue;
                if (!cells.TryGetValue(axis == 0 ? (1, k) : (k, 1), out Blob last)) continue;
                float length = (last.Center - first.Center).Length;
                if (length < 1f) return float.MaxValue;
                worst = Math.Max(worst, (middle.Center - (first.Center + last.Center) * 0.5f).Length / length);
                lines++;
            }
        }
        return lines >= 3 ? worst : float.MaxValue;
    }

    private static FaceGrid MakeFace(Dictionary<(int, int), Blob> cells)
    {
        // Image-space step for +i and +j (average of adjacent cells).
        Vec stepI = new Vec(0, 0), stepJ = new Vec(0, 0);
        for (int i = -1; i <= 0; i++) for (int j = -1; j <= 1; j++) stepI = stepI + (cells[(i + 1, j)].Center - cells[(i, j)].Center) * (1f / 6f);
        for (int i = -1; i <= 1; i++) for (int j = -1; j <= 0; j++) stepJ = stepJ + (cells[(i, j + 1)].Center - cells[(i, j)].Center) * (1f / 6f);

        // Reading frame: columns run along one lattice axis, rows along the other. Which is which does not matter - the
        // neighbouring face turns the reading upright later - but the frame must not be mirrored: seen from outside, a
        // face's (right, down) axes always have a positive cross product in image coordinates (y points down).
        bool columnsAlongI = Cross(stepI, stepJ) > 0f;

        var face = new FaceGrid();
        for (int i = -1; i <= 1; i++)
        {
            for (int j = -1; j <= 1; j++)
            {
                int row = columnsAlongI ? j + 1 : i + 1;
                int col = columnsAlongI ? i + 1 : j + 1;
                Blob b = cells[(i, j)];
                face.P[row, col] = b.Center;
                face.Letters[row, col] = b.Letter;
            }
        }
        return face;
    }

    private static char[,] RotateCw(char[,] g)
    {
        var n = new char[3, 3];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) n[c, 2 - r] = g[r, c];
        return n;
    }

    private static char[,] RotateCcw(char[,] g)
    {
        var n = new char[3, 3];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) n[r, c] = g[c, 2 - r];
        return n;
    }
}
