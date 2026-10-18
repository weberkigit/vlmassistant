using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Sanity checks on one read of the cube (faces in standard orientation, keyed by face index in "URFDLB"), whoever made
/// it - the VLM or the classical reader. A real cube obeys hard rules even when only some faces are known:
///   - a face's middle sticker is its own color;
///   - no color shows up more than 9 times;
///   - every edge piece has two different, non-opposite colors;
///   - every corner piece has three different, non-opposite colors in one fixed clockwise order
///     (this also catches a mirrored reading).
/// A read that breaks them is certainly wrong somewhere; the faces involved can be dropped before they reach the memory.
/// </summary>
public static class ReadChecks
{
    private const string Order = "URFDLB";

    // Facelet indices of each corner position (clockwise around the corner) and the cubie that belongs there,
    // as in the Kociemba solver: URF UFL ULB UBR DFR DLF DBL DRB.
    private static readonly int[][] CornerSlots =
    {
        new[] { 8, 9, 20 }, new[] { 6, 18, 38 }, new[] { 0, 36, 47 }, new[] { 2, 45, 11 },
        new[] { 29, 26, 15 }, new[] { 27, 44, 24 }, new[] { 33, 53, 42 }, new[] { 35, 17, 51 },
    };
    private static readonly string[] CornerCubies = { "URF", "UFL", "ULB", "UBR", "DFR", "DLF", "DBL", "DRB" };

    // UR UF UL UB DR DF DL DB FR FL BL BR
    private static readonly int[][] EdgeSlots =
    {
        new[] { 5, 10 }, new[] { 7, 19 }, new[] { 3, 37 }, new[] { 1, 46 },
        new[] { 32, 16 }, new[] { 28, 25 }, new[] { 30, 43 }, new[] { 34, 52 },
        new[] { 23, 12 }, new[] { 21, 41 }, new[] { 50, 39 }, new[] { 48, 14 },
    };
    private static readonly string[] EdgeCubies = { "UR", "UF", "UL", "UB", "DR", "DF", "DL", "DB", "FR", "FL", "BL", "BR" };

    public class Report
    {
        public readonly List<string> Problems = new List<string>();
        public readonly HashSet<int> SuspectFaces = new HashSet<int>();
        public bool Ok => Problems.Count == 0;
        public override string ToString() => Ok ? "ok" : string.Join("; ", Problems);
    }

    /// <summary>Checks a read; the report lists every broken rule and the faces that are involved in it.</summary>
    public static Report Check(IDictionary<int, string> read)
    {
        var report = new Report();
        if (read == null) return report;

        // Lay the read faces out as a 54-letter string, '?' where nothing is known.
        var facelets = Enumerable.Repeat('?', 54).ToArray();
        foreach (var kv in read)
        {
            int face = kv.Key;
            string stickers = kv.Value;
            if (face < 0 || face > 5 || stickers == null || stickers.Length != 9)
            {
                report.Problems.Add($"face {face} is not 9 stickers");
                if (face >= 0 && face <= 5) report.SuspectFaces.Add(face);
                continue;
            }
            if (stickers.Any(ch => Order.IndexOf(ch) < 0))
            {
                report.Problems.Add($"{Order[face]} has an unknown sticker ({stickers})");
                report.SuspectFaces.Add(face);
                continue;
            }
            if (stickers[4] != Order[face])
            {
                report.Problems.Add($"{Order[face]} has the center {stickers[4]}");
                report.SuspectFaces.Add(face);
            }
            for (int k = 0; k < 9; k++) facelets[face * 9 + k] = stickers[k];
        }

        // No color more than 9 times.
        foreach (char color in Order)
        {
            var carriers = Enumerable.Range(0, 54).Where(i => facelets[i] == color).ToList();
            if (carriers.Count <= 9) continue;
            report.Problems.Add($"{carriers.Count} stickers read {color} (a cube has 9)");
            foreach (int i in carriers) if (i % 9 != 4) report.SuspectFaces.Add(i / 9);
        }

        // Edges: both facelets known -> the pair has to be one of the cube's 12 edge pieces.
        for (int e = 0; e < EdgeSlots.Length; e++)
        {
            char a = facelets[EdgeSlots[e][0]], b = facelets[EdgeSlots[e][1]];
            if (a == '?' || b == '?') continue;
            bool legal = EdgeCubies.Any(cubie => (cubie[0] == a && cubie[1] == b) || (cubie[0] == b && cubie[1] == a));
            if (legal) continue;
            report.Problems.Add($"edge {EdgeCubies[e]} reads {a}{b}");
            report.SuspectFaces.Add(EdgeSlots[e][0] / 9);
            report.SuspectFaces.Add(EdgeSlots[e][1] / 9);
        }

        // Every piece exists once: the same edge piece (or corner piece) in two places means one of them is out of date.
        var edgeAt = new Dictionary<string, int>();
        for (int e = 0; e < EdgeSlots.Length; e++)
        {
            char a = facelets[EdgeSlots[e][0]], b = facelets[EdgeSlots[e][1]];
            if (a == '?' || b == '?') continue;
            string key = a < b ? $"{a}{b}" : $"{b}{a}";
            if (!edgeAt.TryGetValue(key, out int first)) { edgeAt[key] = e; continue; }
            report.Problems.Add($"edge piece {key} shows up at {EdgeCubies[first]} and at {EdgeCubies[e]}");
            foreach (int slot in EdgeSlots[first].Concat(EdgeSlots[e])) report.SuspectFaces.Add(slot / 9);
        }
        var cornerAt = new Dictionary<string, int>();
        for (int c = 0; c < CornerSlots.Length; c++)
        {
            char a = facelets[CornerSlots[c][0]], b = facelets[CornerSlots[c][1]], d = facelets[CornerSlots[c][2]];
            if (a == '?' || b == '?' || d == '?') continue;
            // the same corner piece, whichever of its three stickers is read first
            string[] turns = { $"{a}{b}{d}", $"{b}{d}{a}", $"{d}{a}{b}" };
            string key = turns.OrderBy(t => t, System.StringComparer.Ordinal).First();
            if (!cornerAt.TryGetValue(key, out int first)) { cornerAt[key] = c; continue; }
            report.Problems.Add($"corner piece {key} shows up at {CornerCubies[first]} and at {CornerCubies[c]}");
            foreach (int slot in CornerSlots[first].Concat(CornerSlots[c])) report.SuspectFaces.Add(slot / 9);
        }

        // Corners: each known facelet (two or three of them) has to fit one corner piece, in clockwise order.
        for (int c = 0; c < CornerSlots.Length; c++)
        {
            int[] slots = CornerSlots[c];
            var known = new List<int>();
            for (int k = 0; k < 3; k++) if (facelets[slots[k]] != '?') known.Add(k);
            if (known.Count < 2) continue;

            bool legal = false;
            foreach (string cubie in CornerCubies)
            {
                for (int rotation = 0; rotation < 3 && !legal; rotation++)
                {
                    bool fits = true;
                    foreach (int k in known)
                        if (cubie[(k + rotation) % 3] != facelets[slots[k]]) { fits = false; break; }
                    legal = fits;
                }
                if (legal) break;
            }
            if (legal) continue;

            string seen = string.Concat(Enumerable.Range(0, 3).Select(k => facelets[slots[k]]));
            report.Problems.Add($"corner {CornerCubies[c]} reads {seen}");
            foreach (int k in known) report.SuspectFaces.Add(slots[k] / 9);
        }

        return report;
    }

    /// <summary>
    /// The read without the faces that break a rule (a conflict between two faces can't say which one is wrong, so both
    /// go). Null if nothing is left.
    /// </summary>
    public static Dictionary<int, string> Filter(IDictionary<int, string> read, out Report report)
    {
        report = Check(read);
        if (read == null) return null;
        if (report.Ok) return new Dictionary<int, string>(read);

        var kept = new Dictionary<int, string>();
        foreach (var kv in read) if (!report.SuspectFaces.Contains(kv.Key)) kept[kv.Key] = kv.Value;
        return kept.Count == 0 ? null : kept;
    }
}
