using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Scores the active face reader (VLM or classical) against the truth. The app knows the real state of its own cube,
/// so every trial scrambles it, turns the whole cube to a random view, lets the reader look at the render, and compares
/// what it returned with the faces that are actually in view. Reports, with and without the answer checks (ReadChecks):
/// how many of the visible faces were read, how many were right or wrong, whether a mistake was a wrong color or a wrong
/// orientation, and how long a look takes. Pictures of every failed trial are saved so they can be studied or replayed.
/// Opened from Debug Tools -> Benchmark. Leave the cube alone while it runs; the cube is put back afterwards.
/// </summary>
public class CubeBenchmark : MonoBehaviour
{
    public static bool Running { get; private set; }

    [Header("Run")]
    [Tooltip("Random seed: the same seed gives the same scrambles and views, so two readers can be compared fairly.")]
    public int seed = 1234;
    public int minScrambleMoves = 0;
    public int maxScrambleMoves = 25;
    [Tooltip("A face counts as clearly visible if the angle between its outward direction and the camera is below " +
        "acos(this). Faces seen more edge-on than that are neither required nor penalised.")]
    [Range(0.05f, 0.9f)] public float minVisibleCos = 0.35f;
    public bool saveFailedPictures = true;

    [Header("Full cube mode")]
    [Tooltip("Turn the cube to show the faces that are still unknown, as the assistant asks the user to. Off = a random view " +
        "every look.")]
    public bool guidedViews = true;
    [Tooltip("Give up on a cube after this many looks.")]
    public int maxLooksPerCube = 12;
    [Tooltip("How many random views are tried to find the one that shows most of the missing faces.")]
    public int candidateViews = 24;

    private bool showPanel;
    private bool stopRequested;
    private string status = "Idle. Pick a number of trials.";
    private string summary = "";
    private Vector2 scroll;
    private GUIStyle textStyle, titleStyle;

    private LLMHandler llm;
    private RubiksCubeGame game;

    public void TogglePanel()
    {
        showPanel = !showPanel;
        if (!showPanel) DebugUi.Benchmark = Rect.zero;
    }

    /// <param name="compareFormats">VLM only: read every picture in all four answer formats and compare them.</param>
    public void StartRun(int trials, bool compareFormats = false)
    {
        if (Running) return;
        if (llm == null) llm = FindFirstObjectByType<LLMHandler>();
        if (game == null) game = FindFirstObjectByType<RubiksCubeGame>();
        if (llm == null || game == null || Camera.main == null)
        {
            status = "Needs the LLMHandler, the RubiksCubeGame and a main camera in the scene.";
            return;
        }
        stopRequested = false;
        StartCoroutine(Run(Mathf.Max(1, trials), compareFormats && llm.ActiveReader == "VLM"));
    }

    // ---------------------------------------------------------------- scoring

    private class Tally
    {
        public int Reported, Correct, Wrong, Extra, ExtraCorrect, ColorErrors, OrientationErrors;
        public int StickersChecked, StickersCorrect;
        public int Missed;
    }

    private class Trial
    {
        public int Index, ScrambleMoves, VisibleCount, ReportedRaw, CorrectRaw, WrongRaw, ReportedChecked, CorrectChecked, WrongChecked;
        public bool Usable, Flagged;
        public float Seconds;
        public string Variant = "", Visible = "", Truth = "", Read = "", Problems = "";
    }

    // Everything counted for one way of asking (or for the one reader of a normal run).
    private class Stats
    {
        public string Name;
        public readonly Tally Raw = new Tally();
        public readonly Tally Checked = new Tally();
        public readonly List<Trial> Trials = new List<Trial>();
        public readonly List<float> Seconds = new List<float>();
        public int Unusable, TimedOut, Flagged, WrongRemovedByChecks, CorrectLostToChecks;
        public readonly Dictionary<int, (int visible, int reportedCorrect, int reportedWrong)> ByViewCount =
            new Dictionary<int, (int, int, int)>();
    }

    private static string Letters(IEnumerable<int> faces) =>
        string.Concat(faces.OrderBy(f => f).Select(f => CubeGroundTruth.StdOrder[f]));

    // Scores one read against the truth, adding to the tally. Returns the faces that were read wrongly.
    private static List<int> Score(Dictionary<int, string> read, List<int> visible, string truth, Tally tally,
        out int correct, out int wrong)
    {
        correct = wrong = 0;
        var wrongFaces = new List<int>();
        int seenVisible = 0;
        if (read != null)
        {
            foreach (var kv in read)
            {
                string expected = CubeGroundTruth.Face(truth, kv.Key);
                bool isCorrect = kv.Value == expected;
                if (visible.Contains(kv.Key))
                {
                    seenVisible++;
                    tally.Reported++;
                    for (int k = 0; k < 9; k++)
                    {
                        tally.StickersChecked++;
                        if (kv.Value[k] == expected[k]) tally.StickersCorrect++;
                    }
                    if (isCorrect) { tally.Correct++; correct++; }
                    else
                    {
                        tally.Wrong++; wrong++;
                        wrongFaces.Add(kv.Key);
                        // Same letters in another arrangement = turned or mirrored; different letters = misread colors.
                        bool sameLetters = kv.Value.OrderBy(c => c).SequenceEqual(expected.OrderBy(c => c));
                        if (sameLetters) tally.OrientationErrors++; else tally.ColorErrors++;
                    }
                }
                else
                {
                    tally.Extra++;
                    if (isCorrect) tally.ExtraCorrect++;
                }
            }
        }
        tally.Missed += visible.Count - seenVisible;
        return wrongFaces;
    }

    // ---------------------------------------------------------------- the run

    private static Quaternion RandomRotation(System.Random rng)
    {
        double Gauss() => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
        var q = new Quaternion((float)Gauss(), (float)Gauss(), (float)Gauss(), (float)Gauss());
        float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return length < 1e-4f ? Quaternion.identity : new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
    }

    private IEnumerator Run(int trials, bool compareFormats)
    {
        Running = true;
        RubiksCube cube = game.GetCube();
        Transform root = game.CubeRootTransform;
        FaceColor[] savedState = cube.Snapshot();
        Quaternion savedRotation = root.rotation;
        string reader = llm.ActiveReader;
        string format = llm.ActiveFormat;

        // One list of variants: the four answer formats when comparing, otherwise just the current settings.
        List<LLMHandler.FormatVariant> variants = compareFormats ? LLMHandler.AllFormats.ToList() : null;
        var stats = new List<Stats>();
        if (compareFormats) foreach (var v in variants) stats.Add(new Stats { Name = v.Name });
        else stats.Add(new Stats { Name = reader == "VLM" ? $"VLM ({format})" : reader });

        var rng = new System.Random(seed);

        string folder = null;
        if (saveFailedPictures)
        {
            folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DebugCaptures",
                $"benchmark_{DateTime.Now:yyyyMMdd_HHmmss}_{reader}{(compareFormats ? "_formats" : "")}"));
            Directory.CreateDirectory(folder);
        }

        int trialsRun = 0;
        try
        {
            while (game.IsBusy) yield return null;

            for (int i = 0; i < trials && !stopRequested; i++)
            {
                status = compareFormats
                    ? $"Comparing the 4 VLM formats: trial {i + 1}/{trials}. Leave the cube alone."
                    : $"Running {reader} reader: trial {i + 1}/{trials}. Leave the cube alone.";

                int moves = rng.Next(minScrambleMoves, Math.Max(minScrambleMoves, maxScrambleMoves) + 1);
                game.ScrambleInstantly(moves, rng);
                root.rotation = RandomRotation(rng);
                yield return null;
                yield return null;

                string truth = CubeGroundTruth.ToFaceletString(cube);
                List<int> visible = game.VisibleStdFaces(Camera.main, minVisibleCos);
                if (visible == null)
                {
                    status = "Could not work out which faces are visible (is the cube mesh readable?).";
                    break;
                }

                byte[] picture = null;
                List<LLMHandler.LookSample> samples = null;
                yield return llm.BenchmarkLookVariants(variants, (jpg, list) => { picture = jpg; samples = list; });
                trialsRun++;

                bool anyFailure = false;
                for (int v = 0; v < stats.Count; v++)
                {
                    LLMHandler.LookSample sample = samples[v];
                    Stats st = stats[v];

                    var trial = new Trial
                    {
                        Index = i + 1, ScrambleMoves = moves, VisibleCount = visible.Count, Visible = Letters(visible),
                        Seconds = sample.Seconds, Usable = sample.Raw != null, Flagged = !sample.Report.Ok,
                        Truth = truth, Variant = st.Name,
                    };
                    st.Seconds.Add(sample.Seconds);
                    if (sample.TimedOut) st.TimedOut++;
                    if (sample.Raw == null) st.Unusable++;
                    if (!sample.Report.Ok) { st.Flagged++; trial.Problems = sample.Report.ToString(); }
                    trial.Read = sample.Raw == null ? "-" : string.Join(" ", sample.Raw.OrderBy(kv => kv.Key)
                        .Select(kv => $"{CubeGroundTruth.StdOrder[kv.Key]}={kv.Value}"));

                    List<int> wrongRaw = Score(sample.Raw, visible, truth, st.Raw, out trial.CorrectRaw, out trial.WrongRaw);
                    Score(sample.Checked, visible, truth, st.Checked, out trial.CorrectChecked, out trial.WrongChecked);
                    trial.ReportedRaw = trial.CorrectRaw + trial.WrongRaw;
                    trial.ReportedChecked = trial.CorrectChecked + trial.WrongChecked;
                    st.WrongRemovedByChecks += trial.WrongRaw - trial.WrongChecked;
                    st.CorrectLostToChecks += trial.CorrectRaw - trial.CorrectChecked;

                    (int visible, int reportedCorrect, int reportedWrong) bucket =
                        st.ByViewCount.TryGetValue(visible.Count, out var b) ? b : (0, 0, 0);
                    st.ByViewCount[visible.Count] = (bucket.visible + visible.Count, bucket.reportedCorrect + trial.CorrectChecked,
                        bucket.reportedWrong + trial.WrongChecked);

                    st.Trials.Add(trial);
                    if (wrongRaw.Count > 0 || !trial.Usable) anyFailure = true;
                }

                if (folder != null && anyFailure && picture != null)
                {
                    try { File.WriteAllBytes(Path.Combine(folder, $"trial{i + 1:000}.jpg"), picture); }
                    catch (Exception ex) { Debug.LogWarning("Could not save a failed picture: " + ex.Message); }
                }
            }
        }
        finally
        {
            cube.Restore(savedState);
            game.RestoreInstantly(savedState);
            root.rotation = savedRotation;
            Running = false;
        }

        summary = BuildSummary(reader, stats, trialsRun, compareFormats, folder);
        status = stopRequested ? "Stopped." : "Done.";
        Debug.Log("Benchmark\n" + summary);

        if (folder != null)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "summary.txt"), summary);
                File.WriteAllText(Path.Combine(folder, "trials.csv"), BuildCsv(stats));
            }
            catch (Exception ex) { Debug.LogWarning("Could not write the benchmark files: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- reporting

    private static string Percent(int part, int whole) => whole == 0 ? "n/a" : $"{100.0 * part / whole:0.0}%";

    private string BuildSummary(string reader, List<Stats> all, int trialsRun, bool compare, string folder)
    {
        int visibleTotal = all[0].Trials.Sum(t => t.VisibleCount);
        var sb = new StringBuilder();
        sb.AppendLine($"Reader: {reader}   trials: {trialsRun}   seed {seed}" + (compare ? "   (4 answer formats, same pictures)" : ""));
        sb.AppendLine($"Faces clearly in view: {visibleTotal}   (min cos {minVisibleCos:0.00})");
        sb.AppendLine();

        if (compare)
        {
            // One line per format: the numbers that decide which is best.
            sb.AppendLine("format            read   right   wrong  colors/orient  stickers  unusable  time");
            foreach (Stats st in all)
            {
                Tally t = st.Checked;
                double time = st.Seconds.Count > 0 ? st.Seconds.Average() : 0;
                sb.AppendLine($"{st.Name,-16}  {Percent(t.Reported, visibleTotal),6}  {Percent(t.Correct, t.Reported),6}  {t.Wrong,5}  "
                    + $"{t.ColorErrors,5}/{t.OrientationErrors,-5}   {Percent(t.StickersCorrect, t.StickersChecked),6}   {st.Unusable,5}   {time:0.0} s");
            }
            sb.AppendLine("(read = faces returned of those clearly in view; right = share of those that were exactly right;");
            sb.AppendLine(" all after the answer checks)");
            sb.AppendLine();
        }

        foreach (Stats st in all)
        {
            if (compare) sb.AppendLine($"=== {st.Name}");
            AppendDetails(sb, st, visibleTotal);
            sb.AppendLine();
        }

        var worst = all.SelectMany(st => st.Trials).Where(t => t.WrongRaw > 0 || !t.Usable).Take(8).ToList();
        if (worst.Count > 0)
        {
            sb.AppendLine("First failures (trial: in view -> read):");
            foreach (Trial t in worst) sb.AppendLine($"  #{t.Index}{(compare ? " " + t.Variant : "")}: [{t.Visible}] -> {t.Read}");
        }
        if (folder != null) sb.AppendLine().AppendLine("Files: " + folder);
        return sb.ToString();
    }

    private void AppendDetails(StringBuilder sb, Stats st, int visibleTotal)
    {
        void Block(string title, Tally t)
        {
            sb.AppendLine(title);
            sb.AppendLine($"  read {t.Reported}/{visibleTotal} ({Percent(t.Reported, visibleTotal)}),  missed {t.Missed}");
            sb.AppendLine($"  right {t.Correct} ({Percent(t.Correct, t.Reported)} of read),  wrong {t.Wrong}"
                + $"  [colors {t.ColorErrors}, orientation {t.OrientationErrors}]");
            sb.AppendLine($"  stickers {Percent(t.StickersCorrect, t.StickersChecked)}   extra faces (edge-on) {t.Extra}, {t.ExtraCorrect} right");
        }
        if (st.Raw.Extra > 5 && st.Raw.Extra > 2 * st.Raw.Reported)
            sb.AppendLine("Warning: most faces the reader returned were not in view by my geometry - the visibility test may be off.");
        Block("Raw reads:", st.Raw);
        Block("After the answer checks:", st.Checked);

        sb.AppendLine($"Checks flagged {st.Flagged}/{st.Trials.Count} reads; removed {st.WrongRemovedByChecks} wrong faces, lost {st.CorrectLostToChecks} right ones.");
        sb.AppendLine($"Unusable reads {st.Unusable} (timed out {st.TimedOut}).");

        if (st.Seconds.Count > 0)
        {
            var sorted = st.Seconds.OrderBy(s => s).ToList();
            sb.AppendLine($"Time per look: mean {st.Seconds.Average():0.00} s, median {sorted[sorted.Count / 2]:0.00} s, "
                + $"p95 {sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))]:0.00} s, max {sorted.Last():0.00} s");
        }

        sb.AppendLine("By number of faces in view (after checks):");
        foreach (var kv in st.ByViewCount.OrderBy(k => k.Key))
        {
            int reported = kv.Value.reportedCorrect + kv.Value.reportedWrong;
            sb.AppendLine($"  {kv.Key} in view: read {Percent(reported, kv.Value.visible)}, right {Percent(kv.Value.reportedCorrect, reported)} of read");
        }
    }

    private static string BuildCsv(List<Stats> all)
    {
        var sb = new StringBuilder("trial,variant,scramble_moves,visible,visible_count,usable,flagged_by_checks,reported_raw,right_raw,wrong_raw," +
            "reported_checked,right_checked,wrong_checked,seconds,read,problems\n");
        foreach (Trial t in all.SelectMany(st => st.Trials).OrderBy(t => t.Index))
            sb.AppendLine($"{t.Index},\"{t.Variant}\",{t.ScrambleMoves},{t.Visible},{t.VisibleCount},{t.Usable},{t.Flagged},{t.ReportedRaw},{t.CorrectRaw}," +
                $"{t.WrongRaw},{t.ReportedChecked},{t.CorrectChecked},{t.WrongChecked},{t.Seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},\"{t.Read}\",\"{t.Problems}\"");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- full cube mode
    //
    // The end-to-end test: for every cube, look at it from one view after another until the memory knows all six faces (or
    // the looks run out), then check what the assistant would act on - the assembled 54-sticker state: is it the real cube,
    // is it a legal cube, does the solver accept it, and does its solution solve the real cube?

    private class CubeTrial
    {
        public int Index, Looks, WrongFaces, KnownStickers, RightStickers;
        public bool Completed, Exact, Legal, SolverTried, SolverAccepted, SolutionWorks;
        public float Seconds;
        public string Assembled = "", Truth = "", Solution = "", ViewsSeen = "";
    }

    private const string SolvedState = CubeGroundTruth.SolvedState;

    // Does the move sequence ("R U2 F' ...") take the real cube to the solved one?
    private static bool SolutionSolves(string truth, string solution)
    {
        string state = truth;
        foreach (string token in (solution ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int move = Array.IndexOf(CubeMoves.Names, token);
            if (move < 0) return false;
            state = CubeMoves.Apply(state, move);
        }
        return state == SolvedState;
    }

    public void StartFullCube(int cubes)
    {
        if (Running) return;
        if (llm == null) llm = FindFirstObjectByType<LLMHandler>();
        if (game == null) game = FindFirstObjectByType<RubiksCubeGame>();
        if (llm == null || game == null || Camera.main == null)
        {
            status = "Needs the LLMHandler, the RubiksCubeGame and a main camera in the scene.";
            return;
        }
        stopRequested = false;
        StartCoroutine(RunFullCube(Mathf.Max(1, cubes)));
    }

    // The view, out of a number of random ones, that shows most of the faces the memory still misses (and at least two
    // faces, so each can be oriented by a neighbour).
    private Quaternion ChooseView(HashSet<int> missing, System.Random rng, Transform root)
    {
        Quaternion best = root.rotation;
        int bestScore = -1;
        for (int k = 0; k < Mathf.Max(1, candidateViews); k++)
        {
            Quaternion candidate = RandomRotation(rng);
            root.rotation = candidate;
            List<int> visible = game.VisibleStdFaces(Camera.main, minVisibleCos);
            if (visible == null) return candidate;
            int score = visible.Count(f => missing.Contains(f)) * 10 + (visible.Count >= 2 ? 5 : 0) + visible.Count;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    private IEnumerator RunFullCube(int cubes)
    {
        Running = true;
        RubiksCube cube = game.GetCube();
        Transform root = game.CubeRootTransform;
        FaceColor[] savedState = cube.Snapshot();
        Quaternion savedRotation = root.rotation;
        string reader = llm.ActiveReader;
        string format = llm.ActiveFormat;
        bool solverReachable = llm.cubeSolverClient != null;

        var rng = new System.Random(seed);
        var trials = new List<CubeTrial>();
        var lookSeconds = new List<float>();

        string folder = null;
        if (saveFailedPictures)
        {
            folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DebugCaptures",
                $"benchmark_{DateTime.Now:yyyyMMdd_HHmmss}_{reader}_fullcube"));
            Directory.CreateDirectory(folder);
        }

        try
        {
            while (game.IsBusy) yield return null;

            for (int c = 0; c < cubes && !stopRequested; c++)
            {
                game.ScrambleInstantly(rng.Next(Math.Max(10, minScrambleMoves), Math.Max(11, maxScrambleMoves) + 1), rng);
                string truth = CubeGroundTruth.ToFaceletString(cube);

                // A fresh memory with the same behaviour as the app's own; nothing is carried over from the last cube.
                var memory = new CubeVlmMemory();
                memory.Look.estimateMoves = llm.LookSettings.estimateMoves;
                memory.Look.enforceConsistency = llm.LookSettings.enforceConsistency;

                var trial = new CubeTrial { Index = c + 1, Truth = truth };
                var pictures = new List<byte[]>();
                float now = 0f;

                for (int look = 0; look < Math.Max(1, maxLooksPerCube) && !stopRequested; look++)
                {
                    status = $"Full cube {c + 1}/{cubes}, look {look + 1} ({memory.SeenCount}/6 faces known). Leave the cube alone.";
                    now += 5f; // the time that passes between looks in a real session

                    var missing = new HashSet<int>(memory.FacesNeedingLook(now));
                    root.rotation = guidedViews ? ChooseView(missing, rng, root) : RandomRotation(rng);
                    yield return null;
                    yield return null;

                    List<int> visible = game.VisibleStdFaces(Camera.main, minVisibleCos);
                    if (visible == null)
                    {
                        status = "Could not work out which faces are visible (is the cube mesh readable?).";
                        stopRequested = true;
                        break;
                    }
                    trial.ViewsSeen += (trial.ViewsSeen.Length > 0 ? "|" : "") + Letters(visible);

                    LLMHandler.SolveLookResult result = null;
                    yield return llm.BenchmarkSolveLook(memory, now, r => result = r);
                    trial.Looks++;
                    trial.Seconds += result.Seconds;
                    lookSeconds.Add(result.Seconds);
                    pictures.Add(result.Picture);

                    if (memory.SeenCount == 6 && memory.FacesNeedingLook(now).Count == 0) break;
                }

                // What the assistant would act on.
                trial.Completed = memory.SeenCount == 6;
                trial.Assembled = memory.ToFaceletString();
                var known = new Dictionary<int, string>();
                for (int f = 0; f < 6; f++)
                {
                    CubeVlmMemory.FaceState face = memory.Face(f);
                    if (face == null) continue;
                    known[f] = face.Stickers;
                    string expected = CubeGroundTruth.Face(truth, f);
                    trial.KnownStickers += 9;
                    for (int k = 0; k < 9; k++) if (face.Stickers[k] == expected[k]) trial.RightStickers++;
                    if (face.Stickers != expected) trial.WrongFaces++;
                }
                trial.Exact = trial.Assembled == truth;
                trial.Legal = trial.Completed && FaceletValidator.IsValid(trial.Assembled, out _) && ReadChecks.Check(known).Ok;

                if (trial.Legal && solverReachable)
                {
                    bool accepted = false;
                    string text = null;
                    yield return llm.cubeSolverClient.Solve(trial.Assembled, (solved, answer) => { accepted = solved; text = answer; });
                    if (!accepted && text != null && text.StartsWith("Solver request failed")) solverReachable = false; // not running
                    else
                    {
                        trial.SolverTried = true;
                        trial.SolverAccepted = accepted;
                        trial.Solution = accepted ? text : "";
                        trial.SolutionWorks = accepted && SolutionSolves(truth, text);
                    }
                }

                trials.Add(trial);

                if (folder != null && !trial.Exact)
                {
                    try
                    {
                        for (int k = 0; k < pictures.Count; k++)
                            if (pictures[k] != null) File.WriteAllBytes(Path.Combine(folder, $"cube{trial.Index:000}_look{k + 1:00}.jpg"), pictures[k]);
                    }
                    catch (Exception ex) { Debug.LogWarning("Could not save pictures: " + ex.Message); }
                }
            }
        }
        finally
        {
            cube.Restore(savedState);
            game.RestoreInstantly(savedState);
            root.rotation = savedRotation;
            Running = false;
        }

        summary = BuildFullCubeSummary(reader, format, trials, lookSeconds, llm.cubeSolverClient != null && solverReachable, folder);
        status = stopRequested ? "Stopped." : "Done.";
        Debug.Log("Benchmark (full cube)\n" + summary);

        if (folder != null)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "summary.txt"), summary);
                File.WriteAllText(Path.Combine(folder, "cubes.csv"), BuildFullCubeCsv(trials));
            }
            catch (Exception ex) { Debug.LogWarning("Could not write the benchmark files: " + ex.Message); }
        }
    }

    private string BuildFullCubeSummary(string reader, string format, List<CubeTrial> trials, List<float> lookSeconds,
        bool solverUsed, string folder)
    {
        int n = trials.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"Full cube mode - {reader} reader" + (reader == "VLM" ? $" ({format})" : "") + $"   cubes: {n}   seed {seed}");
        sb.AppendLine($"Views: {(guidedViews ? "turned to show the missing faces" : "random")}, at most {maxLooksPerCube} looks per cube");
        sb.AppendLine();
        if (n == 0) return sb.ToString();

        var done = trials.Where(t => t.Completed).ToList();
        sb.AppendLine($"All 6 faces known: {done.Count}/{n} ({Percent(done.Count, n)})");
        if (done.Count > 0)
        {
            var looks = done.Select(t => t.Looks).OrderBy(l => l).ToList();
            sb.AppendLine($"  looks needed: mean {looks.Average():0.0}, median {looks[looks.Count / 2]}, max {looks.Last()}");
        }
        var notDone = trials.Where(t => !t.Completed).ToList();
        if (notDone.Count > 0)
            sb.AppendLine($"  never completed: {notDone.Count} (avg {notDone.Average(t => t.Looks):0.0} looks, {notDone.Average(t => t.Assembled.Count(ch => ch != '?') / 9f):0.0} faces known)");

        sb.AppendLine();
        sb.AppendLine($"Assembled state exactly the real cube: {trials.Count(t => t.Exact)}/{n} ({Percent(trials.Count(t => t.Exact), n)})");
        int known = trials.Sum(t => t.KnownStickers), right = trials.Sum(t => t.RightStickers);
        sb.AppendLine($"  stickers right: {Percent(right, known)} of the {known} the memory knew; wrong faces left in memory: {trials.Sum(t => t.WrongFaces)}");
        sb.AppendLine($"Legal cube (passes all the checks): {trials.Count(t => t.Legal)}/{done.Count} of the complete ones");

        sb.AppendLine();
        if (!solverUsed && !trials.Any(t => t.SolverTried))
        {
            sb.AppendLine("Solver: not reachable (start cube_solver_server.py), so no solutions were tried.");
        }
        else
        {
            var tried = trials.Where(t => t.SolverTried).ToList();
            int accepted = tried.Count(t => t.SolverAccepted);
            int works = tried.Count(t => t.SolutionWorks);
            sb.AppendLine($"Solver accepted: {accepted}/{tried.Count}");
            sb.AppendLine($"  solution really solves the real cube: {works}/{tried.Count}");
            int dangerous = tried.Count(t => t.SolverAccepted && !t.SolutionWorks);
            sb.AppendLine($"  accepted but WRONG (the assistant would give a wrong solution): {dangerous}");
        }

        if (lookSeconds.Count > 0)
        {
            var sorted = lookSeconds.OrderBy(x => x).ToList();
            sb.AppendLine();
            sb.AppendLine($"Time per look: mean {lookSeconds.Average():0.00} s, p95 {sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))]:0.00} s, max {sorted.Last():0.00} s");
        }

        var failures = trials.Where(t => !t.Exact).Take(5).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("First cubes that did not come out right (memory / truth):");
            foreach (CubeTrial t in failures)
                sb.AppendLine($"  #{t.Index} ({t.Looks} looks): {t.Assembled}\n        {t.Truth}");
        }
        if (folder != null) sb.AppendLine().AppendLine("Files: " + folder);
        return sb.ToString();
    }

    private static string BuildFullCubeCsv(List<CubeTrial> trials)
    {
        var sb = new StringBuilder("cube,looks,completed,exact,wrong_faces,known_stickers,right_stickers,legal,solver_tried,solver_accepted," +
            "solution_works,seconds,views_seen,assembled,truth,solution\n");
        foreach (CubeTrial t in trials)
            sb.AppendLine($"{t.Index},{t.Looks},{t.Completed},{t.Exact},{t.WrongFaces},{t.KnownStickers},{t.RightStickers},{t.Legal}," +
                $"{t.SolverTried},{t.SolverAccepted},{t.SolutionWorks},{t.Seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"{t.ViewsSeen}\",{t.Assembled},{t.Truth},\"{t.Solution}\"");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the panel

    private void OnDisable()
    {
        DebugUi.Benchmark = Rect.zero;
    }

    private void OnGUI()
    {
        if (!showPanel) return;
        Matrix4x4 unscaled = DebugUi.Begin();
        try { DrawPanel(); }
        finally { DebugUi.End(unscaled); }
    }

    private void DrawPanel()
    {

        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = false };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        }

        float w = 380f, h = Mathf.Min(430f, DebugUi.Height - 20f);
        var panel = new Rect(DebugUi.Width - 212f - w, DebugUi.Height - h - 10f, w, h);
        DebugUi.Benchmark = panel;

        DebugUi.Background(panel);

        if (llm == null) llm = FindFirstObjectByType<LLMHandler>();
        string reader = llm != null ? llm.ActiveReader : "?";

        float x = panel.x + 10f, y = panel.y + 8f;
        GUI.Label(new Rect(x, y, w - 20f, 20f), $"Benchmark - {reader} reader" + (llm != null && reader == "VLM" ? $" ({llm.ActiveFormat})" : ""), titleStyle);
        y += 26f;

        GUI.enabled = !Running;
        float bw = (w - 20f - 12f) / 4f;
        if (DebugUi.Button(new Rect(x, y, bw, 24f), "Run 10")) StartRun(10);
        if (DebugUi.Button(new Rect(x + (bw + 4f), y, bw, 24f), "Run 30")) StartRun(30);
        if (DebugUi.Button(new Rect(x + 2 * (bw + 4f), y, bw, 24f), "Run 100")) StartRun(100);
        GUI.enabled = true;
        if (DebugUi.Button(new Rect(x + 3 * (bw + 4f), y, bw, 24f), Running ? "Stop" : "Close"))
        {
            if (Running) stopRequested = true; else TogglePanel();
        }
        y += 30f;

        // The four ways of asking the VLM (JSON/plain x letters/words), each read on the same pictures.
        if (reader == "VLM")
        {
            GUI.enabled = !Running;
            GUI.Label(new Rect(x, y + 2f, 150f, 20f), "Compare 4 formats:", textStyle);
            float cw = (w - 20f - 150f - 8f) / 3f;
            if (DebugUi.Button(new Rect(x + 150f, y, cw, 24f), "10")) StartRun(10, true);
            if (DebugUi.Button(new Rect(x + 150f + (cw + 4f), y, cw, 24f), "30")) StartRun(30, true);
            if (DebugUi.Button(new Rect(x + 150f + 2 * (cw + 4f), y, cw, 24f), "100")) StartRun(100, true);
            GUI.enabled = true;
            y += 30f;
        }

        // The end-to-end test: look at one cube from view after view until all six faces are known, then solve it.
        GUI.enabled = !Running;
        GUI.Label(new Rect(x, y + 2f, 150f, 20f), "Full cube (all 6 faces):", textStyle);
        float fw = (w - 20f - 150f - 8f) / 3f;
        if (DebugUi.Button(new Rect(x + 150f, y, fw, 24f), "5")) StartFullCube(5);
        if (DebugUi.Button(new Rect(x + 150f + (fw + 4f), y, fw, 24f), "10")) StartFullCube(10);
        if (DebugUi.Button(new Rect(x + 150f + 2 * (fw + 4f), y, fw, 24f), "30")) StartFullCube(30);
        GUI.enabled = true;
        y += 30f;

        GUI.Label(new Rect(x, y, w - 20f, 34f), status, textStyle);
        y += 36f;

        string body = summary.Length > 0 ? summary
            : "Scrambles the cube, turns it to random views and scores what the reader returns against the true state. "
            + "Switch the reader first (Reader: VLM / Classical).";
        float viewH = panel.yMax - y - 10f;
        float contentH = textStyle.CalcHeight(new GUIContent(body), w - 44f) + 8f;
        scroll = GUI.BeginScrollView(new Rect(x, y, w - 20f, viewH), scroll, new Rect(0, 0, w - 44f, contentH));
        GUI.Label(new Rect(0, 0, w - 44f, contentH), body, textStyle);
        GUI.EndScrollView();
    }
}
