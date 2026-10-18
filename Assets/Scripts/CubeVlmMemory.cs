using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Tunable weights for the per-face trust score, shared between LLMHandler (decisions) and CubeDebugOverlay
/// (sliders), so what you tune in the view is exactly what the assistant uses.
/// </summary>
public class VlmTrustParams
{
    /// <summary>Share of the score that comes from recency (0..1). The rest, 1 - this, comes from stability.</summary>
    public float recencyWeight = 0.5f;
    /// <summary>Recency score decays as exp(-secondsSinceLastSeen / forgetSeconds).</summary>
    public float forgetSeconds = 60f;
    /// <summary>Stability score rises as 1 - exp(-secondsSinceLastChange / settleSeconds).</summary>
    public float settleSeconds = 30f;
    /// <summary>A face scoring below this counts as "completely uncertain" - the assistant asks to see it.</summary>
    public float askBelow = 0.25f;
}

/// <summary>How often the VLM looks at the cube on its own, in addition to looking when asked to solve.</summary>
public class VlmLookSettings
{
    public bool regularly = true;
    public float intervalSeconds = 5f;
    /// <summary>
    /// Which reader turns a picture of the cube into faces: false = the vision-language model, true = the classical
    /// image-processing pipeline (ClassicalCubeReader). Everything downstream - memory, trust, solver - is the same.
    /// </summary>
    public bool classical = false;
    /// <summary>
    /// How the VLM writes its answer: false = one JSON object, true = one plain "face center=.. up=.. stickers=.." line
    /// per face (shorter, with less to get wrong for a small model).
    /// </summary>
    public bool plainFormat = false;
    /// <summary>
    /// Whether the VLM writes colors as full words (green, red, ...) instead of one letter (G, R, ...). Full words are
    /// ordinary language for a language model; single letters are an arbitrary code it has to keep straight.
    /// </summary>
    public bool colorWords = false;
    /// <summary>
    /// When a solve makes several VLM reads: false = the code takes the majority per sticker, true = the language model is
    /// handed all the reads and writes the combined answer (checked afterwards against what the reads actually said).
    /// </summary>
    public bool llmMerge = false;

    /// <summary>
    /// Watch for a move: when faces seen again differ from what is remembered in the way a layer turn changes them,
    /// everything that was not just seen is forgotten (it can no longer be trusted).
    /// </summary>
    public bool estimateMoves = true;
    /// <summary>
    /// After every look, drop remembered faces that contradict each other as a real cube (an impossible edge or corner,
    /// the same piece twice) - the one seen longest ago goes first.
    /// </summary>
    public bool enforceConsistency = true;
    /// <summary>
    /// The language model writes every spoken reply and what it says is spoken as it is: it is tried even when the app thinks
    /// the server is down, and no fixed sentence from the code and no check of its reply replaces it (if it gives no answer
    /// at all, only the generic "could you repeat that" is spoken). Off: with the classical reader and no server connection
    /// the fixed sentence is spoken straight away, and a reply that does not fit the situation is replaced by it.
    /// </summary>
    public bool llmSpeaks = true;
}

/// <summary>
/// What the VLM has seen of the cube so far, one entry per face. The VLM reports only the faces it can actually
/// see; each face is identified by its fixed center sticker and oriented using the center of a neighbouring face,
/// then stored in the standard facelet orientation (the one cube_solver_server.py expects). Every face carries
/// when it was last watched and when it last changed, from which a trust score is derived.
///
/// Face letters follow the solver convention: U R F D L B. Colors map to faces through the fixed center colors
/// of a standard cube: white U, red R, green F, yellow D, orange L, blue B.
/// </summary>
public class CubeVlmMemory
{
    public const string FaceOrder = "URFDLB";

    public readonly VlmTrustParams Params = new VlmTrustParams();
    public readonly VlmLookSettings Look = new VlmLookSettings();

    public class FaceState
    {
        public string Stickers;      // 9 face letters, standard orientation, row-major
        public float FirstSeen;
        public float LastSeen;
        public float LastChanged;    // the first sighting counts as a change: there is no earlier knowledge
        public int Observations;
        public float Agreement;      // average per-sticker agreement of the latest accepted observation
        public string Pending;       // a differing reading waiting for a second sighting (regular looks only)
    }

    /// <summary>One face as the VLM reported it, before interpretation (colors as letters).</summary>
    public class RawFace
    {
        public string center;
        public string up;
        public string stickers;
    }

    /// <summary>What a single observation (several independent reads) contributed.</summary>
    public class Observation
    {
        public int ReadsUsable;
        public readonly List<int> Accepted = new List<int>();
        public readonly List<int> Unclear = new List<int>();   // reported, but too few reads or too much disagreement
        public readonly List<int> Pending = new List<int>();   // differs from memory, waiting for a confirming look

        public bool MoveDetected;                              // the faces seen again changed like a layer turn
        public string MoveNote = "";
        public readonly List<int> Forgotten = new List<int>(); // remembered faces dropped because of a move
        public readonly List<int> Dropped = new List<int>();   // remembered faces dropped because they contradict others
        public string ConflictNote = "";
        public float ChangeEvidence;                           // share of the faces seen again whose stickers changed
    }

    /// <summary>How a face's stickers changed between two readings.</summary>
    public enum ChangeKind
    {
        None,    // identical
        Noise,   // one or two stickers - most likely a misread
        Many,    // several stickers, no pattern
        Strip,   // three or more stickers, all in one row or column - a neighbouring layer turned
        Turned,  // the face's own stickers turned a quarter or half turn - the layer of this face turned
    }

    private struct Change
    {
        public int Face;
        public ChangeKind Kind;
        public bool Applied;       // false: only seen once so far (waiting for confirmation)
        public float Agreement;
    }

    // ---- what the move estimator has concluded so far ----
    public int MovesDetected { get; private set; }
    public float LastMoveTime { get; private set; } = float.NegativeInfinity;
    public string LastMoveNote { get; private set; } = "";

    private readonly FaceState[] faces = new FaceState[6];

    public FaceState Face(int index) => faces[index];
    public int SeenCount => faces.Count(f => f != null);

    // ---------------------------------------------------------------- color / face helpers

    /// <summary>
    /// W Y R O G B (or the color names) to the face letter whose center has that color. Small models often answer
    /// in face letters instead (U D F L for white yellow green orange). Those don't clash with the color letters -
    /// B and R mean the same in both vocabularies - so they are accepted too.
    /// </summary>
    public static char FaceFromColor(string color)
    {
        if (string.IsNullOrEmpty(color)) return '\0';

        // A full color word ("green", "dark blue", "grey") first; the first letter alone would read "grey" as green.
        foreach (string token in Regex.Split(color, @"[^A-Za-z]+"))
        {
            if (token.Length < 2) break;
            string word = token.ToLowerInvariant();
            if (ColorAdjectives.Contains(word)) continue;
            if (ColorWords.TryGetValue(word, out char face)) return face;
            break;
        }

        foreach (char ch in color)
        {
            if (!char.IsLetter(ch)) continue;
            switch (char.ToUpperInvariant(ch))
            {
                case 'W': case 'U': return 'U';
                case 'Y': case 'D': return 'D';
                case 'G': case 'F': return 'F';
                case 'O': case 'L': return 'L';
                case 'B': return 'B';
                case 'R': return 'R';
                default: return '\0';
            }
        }
        return '\0';
    }

    // Color words a model may use for the six colors, including how the shaded stickers of the 3D render look
    // (white in shadow is grey, yellow olive, orange brown) - mapped to the face whose center has that color.
    private static readonly Dictionary<string, char> ColorWords = new Dictionary<string, char>
    {
        ["white"] = 'U', ["grey"] = 'U', ["gray"] = 'U', ["silver"] = 'U', ["cream"] = 'U', ["ivory"] = 'U',
        ["yellow"] = 'D', ["olive"] = 'D', ["gold"] = 'D', ["golden"] = 'D', ["lemon"] = 'D',
        ["green"] = 'F', ["lime"] = 'F',
        ["orange"] = 'L', ["brown"] = 'L', ["amber"] = 'L',
        ["blue"] = 'B', ["navy"] = 'B', ["cyan"] = 'B',
        ["red"] = 'R', ["crimson"] = 'R', ["maroon"] = 'R', ["pink"] = 'R',
    };

    private static readonly HashSet<string> ColorAdjectives = new HashSet<string>
    {
        "dark", "light", "bright", "pale", "deep", "dull", "shaded", "dim",
    };

    /// <summary>The color letter (W Y R O G B) of the face whose center has that color.</summary>
    public static char ColorLetterOfFace(char face)
    {
        switch (face)
        {
            case 'U': return 'W';
            case 'D': return 'Y';
            case 'F': return 'G';
            case 'L': return 'O';
            case 'B': return 'B';
            default: return 'R';
        }
    }

    /// <summary>The face letter for a color word ("grey" -> U, "brown" -> L), or false if it is not one.</summary>
    public static bool TryColorWord(string word, out char face)
    {
        face = '\0';
        return word != null && ColorWords.TryGetValue(word.ToLowerInvariant(), out face);
    }

    public static string ColorName(char face)
    {
        switch (face)
        {
            case 'U': return "white";
            case 'D': return "yellow";
            case 'F': return "green";
            case 'B': return "blue";
            case 'R': return "red";
            case 'L': return "orange";
            default: return "unknown";
        }
    }

    /// <summary>"white-centered face" - how the assistant refers to a face when asking the user to show it.</summary>
    public static string FaceTitle(int faceIndex) => ColorName(FaceOrder[faceIndex]) + "-centered face";

    // Neighbours of each face, clockwise starting at the top edge when the face is viewed straight on in the
    // standard facelet orientation (U with B on top, D with F on top, every other face with U on top).
    private static readonly Dictionary<char, string> Around = new Dictionary<char, string>
    {
        ['U'] = "BRFL", ['D'] = "FRBL", ['F'] = "URDL", ['R'] = "UBDF", ['B'] = "ULDR", ['L'] = "UFDB",
    };

    // ---------------------------------------------------------------- what one image can show

    /// <summary>
    /// A single image of a cube shows at most three faces, and never two opposite ones (U/D, R/L, F/B). A read that
    /// reports more has certainly guessed some faces it can't see, so it can't be trusted for the rest either.
    /// </summary>
    public static bool IsPlausibleView(ICollection<int> faceIndices, out string reason)
    {
        reason = null;
        if (faceIndices.Count > 3)
        {
            reason = $"{faceIndices.Count} faces reported, but at most 3 can be visible at once";
            return false;
        }
        // Face indices follow "URFDLB": 0 U / 3 D, 1 R / 4 L, 2 F / 5 B are the opposite pairs.
        foreach (int f in faceIndices)
        {
            if (faceIndices.Contains((f + 3) % 6))
            {
                reason = $"opposite faces {FaceOrder[f]} and {FaceOrder[(f + 3) % 6]} can't be visible together";
                return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- orienting one reported face

    /// <summary>
    /// Turns one reported face into (face index, 9 letters in standard orientation). The face is identified by its
    /// center color; the VLM's reading orientation is recovered from "up", the center color of the neighbouring face
    /// across the top edge of its reading. Returns false (with a reason) if anything is inconsistent.
    /// </summary>
    public static bool TryOrient(RawFace raw, out int faceIndex, out string standard9, out string error)
    {
        faceIndex = -1;
        standard9 = null;
        error = null;

        if (raw == null) { error = "empty face entry"; return false; }

        char center = FaceFromColor(raw.center);
        if (center == '\0') { error = $"unknown center color '{raw.center}'"; return false; }

        // The nine stickers come as one letter each ("WWR WWG"), as a run of letters or as color words
        // ("white white red ..."); adjectives such as "dark" are ignored.
        var stickers = new List<char>();
        foreach (string token in Regex.Split(raw.stickers ?? "", @"[^A-Za-z]+"))
        {
            if (token.Length == 0) continue;
            string lower = token.ToLowerInvariant();
            if (ColorAdjectives.Contains(lower)) continue;
            if (token.Length > 1 && ColorWords.TryGetValue(lower, out char word)) { stickers.Add(word); continue; }
            foreach (char ch in token)
            {
                char face = FaceFromColor(ch.ToString());
                if (face == '\0') { error = $"unknown sticker color '{token}'"; return false; }
                stickers.Add(face);
            }
        }
        if (stickers.Count != 9) { error = $"expected 9 stickers, got {stickers.Count}"; return false; }
        if (stickers[4] != center) { error = $"middle sticker {stickers[4]} does not match the declared center {center}"; return false; }

        char up = FaceFromColor(raw.up);
        if (up == '\0') { error = "orientation unknown (no neighbouring face reported)"; return false; }
        int k = Around[center].IndexOf(up);
        if (k < 0) { error = $"'{up}' does not touch the {center} face"; return false; }

        // In the reading, the neighbour at the standard top sits j quarter-turns clockwise from the top;
        // rotating the reading j quarter-turns counter-clockwise brings it back to the standard top.
        int j = (4 - k) % 4;
        char[,] grid = new char[3, 3];
        for (int i = 0; i < 9; i++) grid[i / 3, i % 3] = stickers[i];
        for (int turn = 0; turn < j; turn++) grid = RotateCcw(grid);

        var sb = new StringBuilder(9);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) sb.Append(grid[r, c]);

        faceIndex = FaceOrder.IndexOf(center);
        standard9 = sb.ToString();
        return true;
    }

    private static char[,] RotateCcw(char[,] g)
    {
        var n = new char[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) n[r, c] = g[c, 2 - r];
        return n;
    }

    // ---------------------------------------------------------------- observing

    /// <summary>54-character string for display: faces not in the dictionary read as '?'.</summary>
    public static string Flatten(IDictionary<int, string> read)
    {
        var chars = new char[54];
        for (int f = 0; f < 6; f++)
        {
            bool has = read != null && read.TryGetValue(f, out string s) && s != null && s.Length == 9;
            for (int k = 0; k < 9; k++) chars[f * 9 + k] = has ? read[f][k] : '?';
        }
        return new string(chars);
    }

    /// <summary>
    /// Merges several independent reads of the same moment (null = unusable read) into the memory. A face is only
    /// accepted if enough of the reads reported it and they agree well enough; accepted faces refresh their
    /// last-seen time and, if their stickers differ from what was stored, their last-changed time.
    ///
    /// With confirmChanges (used for the regular single-read looks) a reading that differs from a face already in
    /// memory is not applied right away: it is parked as pending and only replaces the memory if the next look
    /// reports the same thing. That keeps one noisy single read from overwriting a well-voted face.
    /// </summary>
    public Observation Observe(IList<Dictionary<int, string>> reads, float now, float minAgreement, bool confirmChanges = false)
    {
        var result = new Observation();
        var usable = reads?.Where(r => r != null).ToList() ?? new List<Dictionary<int, string>>();
        result.ReadsUsable = usable.Count;
        if (usable.Count == 0) return result;

        int required = (usable.Count + 1) / 2; // at least half of the usable reads must have seen the face
        var changes = new List<Change>();
        int reseen = 0; // faces that were already in memory and were read again
        string[] before = faces.Select(face => face?.Stickers).ToArray();   // memory as it was when this look began
        var votedFaces = new Dictionary<int, string>();                      // what this look says, per face

        for (int f = 0; f < 6; f++)
        {
            var seenIn = usable.Where(r => r.ContainsKey(f)).ToList();
            if (seenIn.Count == 0) continue;
            if (seenIn.Count < required) { result.Unclear.Add(f); continue; }

            var voted = new char[9];
            float agreementSum = 0f;
            for (int k = 0; k < 9; k++)
            {
                var votes = new Dictionary<char, int>();
                foreach (var r in seenIn)
                {
                    char c = r[f][k];
                    votes.TryGetValue(c, out int n);
                    votes[c] = n + 1;
                }
                var best = votes.OrderByDescending(v => v.Value).First();
                voted[k] = best.Key;
                agreementSum += (float)best.Value / seenIn.Count;
            }

            float agreement = agreementSum / 9f;
            if (agreement < minAgreement) { result.Unclear.Add(f); continue; }

            string stickers = new string(voted);
            votedFaces[f] = stickers;
            var state = faces[f];
            if (state == null)
            {
                faces[f] = new FaceState
                {
                    Stickers = stickers, FirstSeen = now, LastSeen = now, LastChanged = now,
                    Observations = 1, Agreement = agreement,
                };
            }
            else
            {
                reseen++;
                if (state.Stickers != stickers)
                {
                    ChangeKind kind = ClassifyChange(state.Stickers, stickers);
                    if (confirmChanges && state.Pending != stickers)
                    {
                        state.Pending = stickers;     // first sighting of a different reading: wait for confirmation
                        result.Pending.Add(f);
                        changes.Add(new Change { Face = f, Kind = kind, Applied = false, Agreement = agreement });
                        continue;
                    }
                    state.Stickers = stickers;
                    state.LastChanged = now;
                    changes.Add(new Change { Face = f, Kind = kind, Applied = true, Agreement = agreement });
                }
                state.Pending = null;
                state.LastSeen = now;
                state.Observations++;
                state.Agreement = agreement;
            }
            result.Accepted.Add(f);
        }

        result.ChangeEvidence = reseen == 0 ? 0f : Math.Min(1f, (float)changes.Count / reseen);
        if (Look.estimateMoves) EvaluateMove(result, changes, now, before, votedFaces);
        if (Look.enforceConsistency) ResolveConflicts(result, now);
        return result;
    }

    // ---------------------------------------------------------------- the move estimator

    private static string TurnClockwise(string s)
    {
        var t = new char[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) t[r * 3 + c] = s[(2 - c) * 3 + r];
        return new string(t);
    }

    /// <summary>
    /// How one face's stickers changed. A move leaves marks no misread does: the face of the turned layer comes back
    /// turned by a quarter or half turn; the four faces around it get a whole row or column replaced. One or two
    /// changed stickers are far more likely a misread.
    /// </summary>
    public static ChangeKind ClassifyChange(string before, string after)
    {
        if (before == null || after == null || before.Length != 9 || after.Length != 9) return ChangeKind.None;

        var differing = new List<int>();
        for (int i = 0; i < 9; i++) if (i != 4 && before[i] != after[i]) differing.Add(i);
        if (differing.Count == 0) return ChangeKind.None;

        string turned = before;
        for (int quarter = 1; quarter <= 3; quarter++)
        {
            turned = TurnClockwise(turned);
            if (turned == after) return ChangeKind.Turned;
        }

        if (differing.Count <= 2) return ChangeKind.Noise;
        bool oneRow = differing.Select(i => i / 3).Distinct().Count() == 1;
        bool oneColumn = differing.Select(i => i % 3).Distinct().Count() == 1;
        return oneRow || oneColumn ? ChangeKind.Strip : ChangeKind.Many;
    }

    // A move was made if a face seen again changed like a layer turn (and was read reliably), or if two or more faces
    // changed in the same look - one misread doesn't change two faces. Then what was seen before is out of date. If one
    // move (or two in a row) turns the old memory into what this look shows, the memory is turned the same way and keeps
    // what it knew; otherwise what was not seen in this look is forgotten.
    private void EvaluateMove(Observation result, List<Change> changes, float now, string[] before, Dictionary<int, string> voted)
    {
        var applied = changes.Where(c => c.Applied).ToList();
        var strong = applied.Where(c => c.Kind >= ChangeKind.Many && c.Agreement >= 0.8f).ToList();
        if (strong.Count == 0 && applied.Count < 2) return;

        string what = strong.Count > 0
            ? string.Join(", ", strong.Select(c => c.Kind == ChangeKind.Turned ? $"{FaceOrder[c.Face]} turned" : $"{FaceOrder[c.Face]} changed"))
            : "faces " + string.Join(", ", applied.Select(c => FaceOrder[c.Face]));

        // The faces of this look that were still waiting for confirmation take their new reading now.
        var keep = new HashSet<int>(result.Accepted);
        for (int f = 0; f < 6; f++)
        {
            var state = faces[f];
            if (state == null || keep.Contains(f) || state.Pending == null) continue;
            state.Stickers = state.Pending;
            state.Pending = null;
            state.LastSeen = now;
            state.LastChanged = now;
            state.Observations = 1;
            keep.Add(f);
        }

        string moveName = ExplainByMoves(before, voted);
        if (moveName != null)
        {
            string turned = ApplyMovesToMemory(moveName, before, keep, now, result);
            result.MoveNote = $"Move detected ({moveName})" + (result.Forgotten.Count > 0
                ? $" - memory turned along, forgot {string.Join(", ", result.Forgotten.Select(f => FaceOrder[f]))}"
                : " - memory turned along");
            LastMoveNote = moveName;
        }
        else
        {
            for (int f = 0; f < 6; f++)
            {
                if (faces[f] == null || keep.Contains(f)) continue;
                faces[f] = null;
                result.Forgotten.Add(f);
            }
            result.MoveNote = result.Forgotten.Count == 0
                ? $"Move detected ({what})"
                : $"Move detected ({what}) - forgot {string.Join(", ", result.Forgotten.Select(f => FaceOrder[f]))}";
            LastMoveNote = what;
        }

        MovesDetected++;
        LastMoveTime = now;
        result.MoveDetected = true;
    }

    private static string KnownState(string[] stickersByFace)
    {
        var sb = new StringBuilder(54);
        for (int f = 0; f < 6; f++) sb.Append(stickersByFace[f] ?? new string('?', 9));
        return sb.ToString();
    }

    /// <summary>
    /// Which move (or two moves in a row) turns the remembered cube into what this look shows? Every candidate is
    /// tried on the memory and compared sticker by sticker with the faces just read; stickers the memory doesn't know
    /// count for nothing. A candidate is acceptable if it contradicts the look in no sticker and accounts for at least
    /// one sticker that really changed. Returns its name, or null if no
    /// candidate fits or several fit equally well (the look can't tell them apart).
    /// </summary>
    public static string ExplainByMoves(string[] before, IDictionary<int, string> looked)
    {
        string known = KnownState(before);
        string seen = KnownState(Enumerable.Range(0, 6).Select(f => looked.TryGetValue(f, out string s) ? s : null).ToArray());

        var scored = new List<(string name, int mismatches, int explained)>();
        void Try(string name, string state)
        {
            int compared = 0, mismatches = 0, explained = 0;
            for (int p = 0; p < 54; p++)
            {
                if (seen[p] == '?' || state[p] == '?') continue;
                compared++;
                if (state[p] != seen[p]) mismatches++;
                else if (known[p] != '?' && known[p] != seen[p]) explained++;
            }
            if (mismatches == 0 && explained >= 1) scored.Add((name, mismatches, explained));
        }

        for (int m = 0; m < CubeMoves.Count; m++) Try(CubeMoves.Names[m], CubeMoves.Apply(known, m));
        if (scored.Count == 0)
        {
            for (int m1 = 0; m1 < CubeMoves.Count; m1++)
            {
                string once = CubeMoves.Apply(known, m1);
                for (int m2 = 0; m2 < CubeMoves.Count; m2++)
                    if (m1 / 3 != m2 / 3) Try(CubeMoves.Names[m1] + " " + CubeMoves.Names[m2], CubeMoves.Apply(once, m2));
            }
        }
        if (scored.Count == 0) return null;

        int best = scored.Min(c => c.mismatches);
        var top = scored.Where(c => c.mismatches == best).ToList();
        int mostExplained = top.Max(c => c.explained);
        top = top.Where(c => c.explained == mostExplained).ToList();
        return top.Count == 1 ? top[0].name : null;
    }

    // Turns the memory along with the move(s): every known sticker goes where the move takes it. A face stays known only
    // if all its nine stickers are known afterwards. Faces of this look keep what they were just read as.
    private string ApplyMovesToMemory(string moves, string[] before, HashSet<int> keep, float now, Observation result)
    {
        string state = KnownState(before);
        foreach (string name in moves.Split(' ')) state = CubeMoves.Apply(state, Array.IndexOf(CubeMoves.Names, name));

        for (int f = 0; f < 6; f++)
        {
            if (keep.Contains(f)) continue;
            string block = state.Substring(f * 9, 9);
            if (before[f] == null && faces[f] == null) continue;
            if (block.Contains('?'))
            {
                faces[f] = null;
                result.Forgotten.Add(f);
                continue;
            }
            if (faces[f] == null) faces[f] = new FaceState { FirstSeen = now, LastSeen = now, Agreement = 1f, Observations = 1 };
            if (faces[f].Stickers != block) faces[f].LastChanged = now;
            faces[f].Stickers = block;
            faces[f].Pending = null;
        }
        return moves;
    }

    // ---------------------------------------------------------------- keeping the faces consistent

    // The remembered faces have to fit together as parts of one real cube. If they don't, one of them is out of date
    // (the cube was turned since it was seen): the one that was seen longest ago goes.
    private void ResolveConflicts(Observation result, float now)
    {
        for (int guard = 0; guard < 6; guard++)
        {
            var known = new Dictionary<int, string>();
            for (int f = 0; f < 6; f++) if (faces[f] != null) known[f] = faces[f].Stickers;

            ReadChecks.Report report = ReadChecks.Check(known);
            if (report.Ok) return;

            var candidates = report.SuspectFaces.Where(f => faces[f] != null).ToList();
            if (candidates.Count == 0) return;
            int victim = candidates.OrderBy(f => faces[f].LastSeen).ThenBy(f => Trust(f, now)).First();

            faces[victim] = null;
            result.Dropped.Add(victim);
            result.ConflictNote += (result.ConflictNote.Length > 0 ? "; " : "") + $"{FaceOrder[victim]} dropped ({report.Problems.FirstOrDefault()})";
        }
    }

    // ---------------------------------------------------------------- trust

    public float RecencyScore(int f, float now)
    {
        var s = faces[f];
        if (s == null) return 0f;
        return (float)Math.Exp(-Math.Max(0f, now - s.LastSeen) / Math.Max(0.001f, Params.forgetSeconds));
    }

    public float StabilityScore(int f, float now)
    {
        var s = faces[f];
        if (s == null) return 0f;
        return 1f - (float)Math.Exp(-Math.Max(0f, now - s.LastChanged) / Math.Max(0.001f, Params.settleSeconds));
    }

    /// <summary>0 = never seen / completely uncertain, 1 = just watched and long unchanged.</summary>
    public float Trust(int f, float now)
    {
        if (faces[f] == null) return 0f;
        float w = Math.Min(1f, Math.Max(0f, Params.recencyWeight));
        return w * RecencyScore(f, now) + (1f - w) * StabilityScore(f, now);
    }

    public float AverageTrust(float now)
    {
        float sum = 0f;
        for (int f = 0; f < 6; f++) sum += Trust(f, now);
        return sum / 6f;
    }

    /// <summary>Faces never seen, or whose trust fell below the "ask" threshold.</summary>
    public List<int> FacesNeedingLook(float now)
    {
        var list = new List<int>();
        for (int f = 0; f < 6; f++)
            if (faces[f] == null || Trust(f, now) < Params.askBelow) list.Add(f);
        return list;
    }

    /// <summary>The n least trusted faces (never-seen faces first), for asking the user to re-show them.</summary>
    public List<int> LeastTrusted(float now, int n)
    {
        return Enumerable.Range(0, 6).OrderBy(f => Trust(f, now)).Take(n).ToList();
    }

    /// <summary>54-character facelet string (U R F D L B, standard orientation); unknown faces read as '?'.</summary>
    public string ToFaceletString()
    {
        var sb = new StringBuilder(54);
        for (int f = 0; f < 6; f++)
            sb.Append(faces[f] != null ? faces[f].Stickers : new string('?', 9));
        return sb.ToString();
    }

    public void Clear()
    {
        for (int f = 0; f < 6; f++) faces[f] = null;
    }
}
