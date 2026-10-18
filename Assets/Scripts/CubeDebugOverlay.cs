using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class CubeDebugOverlay : MonoBehaviour
{
    public RubiksCubeRenderer cubeRenderer;
    private RubiksCube cube;

    private bool show2D = false;
    private bool menuOpen = false;
    private ConversationHandler conversation; // for the Test Sound button

    // ---- VLM look (what the vision model saw most recently, filled in by LLMHandler's solve flow) ----
    private const string VlmFaceOrder = "URFDLB";
    private List<string> vlmSamples = new List<string>(); // one entry per read: 54 letters ('?' = not reported), null = unusable
    private string vlmStatus = "";
    private bool vlmHasData;
    private int vlmMode;                                  // 0 = VLM memory, 1..n = individual read of the latest look
    private bool[] vlmDisputed = new bool[54];
    private int vlmDisputedCount;

    private static readonly Color32[] faceColors = new Color32[]
    {
        Color.black,
        new Color32(220,  50,  50, 255),
        new Color32(255, 140,   0, 255),
        new Color32(240, 240, 240, 255),
        new Color32(255, 255,   0, 255),
        new Color32( 50, 200,  50, 255),
        new Color32( 50, 100, 220, 255),
        Color.magenta,
    };

    void OnGUI()
    {
        if (cube == null)
        {
            if (cubeRenderer != null) cube = cubeRenderer.GetCube();
            return;
        }

        Matrix4x4 unscaled = DebugUi.Begin();
        try
        {
            DrawToggleButtons();

            if (show2D) Draw2D();
            else DebugUi.View2D = Rect.zero;
            DrawSolution();
        }
        finally
        {
            DebugUi.End(unscaled);
        }
    }

    // ---- the solver's answer, big, on the cube view ------------------------------------------------------------

    private string solutionMoves = "";     // as to be made on the cube on screen
    private string solverMoves = "";       // what the solver server returned
    private string solutionNote = "";      // why there is no solution yet, or a warning
    private bool solutionVisible;
    private bool solutionStale;
    private float solutionTime;
    private GUIStyle _movesStyle;

    /// <summary>
    /// Shows the solver's answer on the screen. screenMoves are the moves for this cube on screen (see
    /// CubeMoves.ToScreenNotation), solverMoves what the solver returned; with neither, the note says why there is none.
    /// </summary>
    public void SetSolution(string screenMoves, string solverReturned, string note)
    {
        solutionMoves = screenMoves ?? "";
        solverMoves = solverReturned ?? "";
        solutionNote = note ?? "";
        solutionStale = false;
        solutionVisible = true;
        solutionTime = Time.time;
    }

    /// <summary>The cube was turned since the solution was computed: it no longer applies.</summary>
    public void MarkSolutionStale()
    {
        if (solutionVisible && solutionMoves.Length > 0) solutionStale = true;
    }

    void DrawSolution()
    {
        if (!solutionVisible) { DebugUi.Solution = Rect.zero; return; }
        EnsureDebugStyles();
        if (_movesStyle == null)
        {
            _movesStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
            _movesStyle.normal.textColor = Color.white;
        }

        // Over the bottom of the cube view, left of the Debug Tools column.
        float scale = DebugUi.Scale;
        Rect view = Camera.main != null
            ? new Rect(Camera.main.pixelRect.x / scale, 0f, Camera.main.pixelRect.width / scale, DebugUi.Height)
            : new Rect(0, 0, DebugUi.Width, DebugUi.Height);
        float left = view.x + 12f;
        float width = Mathf.Clamp(view.width - 226f, 300f, 640f);
        bool hasMoves = solutionMoves.Length > 0;
        int moveCount = hasMoves ? solutionMoves.Split(' ').Length : 0;

        const string legend = "A letter alone: turn that face clockwise as you look at it. ' = counterclockwise, 2 = half turn.  "
            + "U white · R red · F green · D yellow · L orange · B blue (the face with that center color).";
        float movesH = hasMoves ? _movesStyle.CalcHeight(new GUIContent(solutionMoves), width - 24f) : 0f;
        float noteH = solutionNote.Length > 0 ? _vlmTextStyle.CalcHeight(new GUIContent(solutionNote), width - 24f) : 0f;
        float legendH = hasMoves ? _vlmTextStyle.CalcHeight(new GUIContent(legend), width - 24f) : 0f;
        float rawH = hasMoves && solverMoves.Length > 0 ? 16f : 0f;
        float height = 34f + movesH + noteH + legendH + rawH + 14f;

        var panel = new Rect(left, DebugUi.Height - height - 12f, width, height);
        DebugUi.Solution = panel;
        DebugUi.Background(panel);

        float x = panel.x + 12f, y = panel.y + 8f, w = panel.width - 24f;
        string title = hasMoves
            ? $"Solution - {moveCount} move{(moveCount == 1 ? "" : "s")}" + (solutionStale ? "   (the cube changed since - ask again)" : "")
            : "No solution yet";
        GUI.Label(new Rect(x, y, w - 30f, 20f), title, _vlmTextStyle);
        if (GUI.Button(new Rect(panel.xMax - 34f, panel.y + 6f, 24f, 22f), "×", _tabStyle)) { solutionVisible = false; return; }
        y += 24f;

        if (hasMoves)
        {
            Color before = GUI.color;
            GUI.color = solutionStale ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
            GUI.Label(new Rect(x, y, w, movesH), solutionMoves, _movesStyle);
            GUI.color = before;
            y += movesH;
            if (rawH > 0f) { GUI.Label(new Rect(x, y, w, rawH), "Solver output: " + solverMoves, _vlmTextStyle); y += rawH; }
            GUI.Label(new Rect(x, y, w, legendH), legend, _vlmTextStyle);
            y += legendH;
        }
        if (noteH > 0f) GUI.Label(new Rect(x, y, w, noteH), solutionNote, _vlmTextStyle);
    }

    private GUIStyle _tabStyle;
    private GUIStyle _vlmTextStyle;

    private void EnsureDebugStyles()
    {
        if (_tabStyle != null) return;

        _tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
        _vlmTextStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        _vlmTextStyle.normal.textColor = new Color(0.88f, 0.9f, 0.94f);
    }

    // ---- Debug Tools menu --------------------------------------------------------------------------------------

    private Vector2 menuScroll;

    private enum ItemKind { Section, Toggle, Command, Choice }

    private struct MenuItem
    {
        public ItemKind kind;
        public string label;
        public bool on;
        public System.Action action;
        public string[] options;
        public int selected;
        public System.Action<int> choose;
    }

    private static MenuItem Section(string label) => new MenuItem { kind = ItemKind.Section, label = label };

    private static MenuItem Switch(string label, bool on, System.Action action)
        => new MenuItem { kind = ItemKind.Toggle, label = label, on = on, action = action };

    private static MenuItem Command(string label, System.Action action)
        => new MenuItem { kind = ItemKind.Command, label = label, action = action };

    private static MenuItem Choice(string[] options, int selected, System.Action<int> choose)
        => new MenuItem { kind = ItemKind.Choice, options = options, selected = selected, choose = choose };

    private List<MenuItem> BuildMenu()
    {
        var items = new List<MenuItem>();

        items.Add(Section("View"));
        items.Add(Switch("2D overlay", show2D, () => show2D = !show2D));
        CubeBackdrop backdrop = CubeBackdrop.Instance;
        items.Add(Switch("Backdrop", backdrop != null && backdrop.Visible, () => { if (backdrop != null) backdrop.Visible = !backdrop.Visible; }));

        if (memory != null)
        {
            items.Add(Section("Face reader"));
            items.Add(Choice(new[] { "VLM", "Classical" }, memory.Look.classical ? 1 : 0, index =>
            {
                memory.Look.classical = index == 1;
                vlmMode = index == 1 ? 1 : 0; // show what the reader returned, not the memory
            }));
            if (!memory.Look.classical)
            {
                items.Add(Switch("Plain output format", memory.Look.plainFormat, () => memory.Look.plainFormat = !memory.Look.plainFormat));
                items.Add(Switch("Color words", memory.Look.colorWords, () => memory.Look.colorWords = !memory.Look.colorWords));
                items.Add(Switch("LLM merges reads", memory.Look.llmMerge, () => memory.Look.llmMerge = !memory.Look.llmMerge));
            }
            items.Add(Switch("Move estimator", memory.Look.estimateMoves, () => memory.Look.estimateMoves = !memory.Look.estimateMoves));
            items.Add(Switch("Consistency repair", memory.Look.enforceConsistency, () => memory.Look.enforceConsistency = !memory.Look.enforceConsistency));
        }

        if (memory != null)
        {
            items.Add(Section("Speech"));
            items.Add(Switch("LLM writes all replies", memory.Look.llmSpeaks, () => memory.Look.llmSpeaks = !memory.Look.llmSpeaks));
        }

        items.Add(Section("Interaction"));
        items.Add(Switch("Highlight dragged layer", RubiksCubeGame.HighlightDraggedFace, () => RubiksCubeGame.HighlightDraggedFace = !RubiksCubeGame.HighlightDraggedFace));
        items.Add(Switch("Click marker", RubiksCubeGame.ShowPickMarker, () => RubiksCubeGame.ShowPickMarker = !RubiksCubeGame.ShowPickMarker));
        items.Add(Switch("Turn particles", RubiksCubeGame.ParticlesEnabled, () => RubiksCubeGame.ParticlesEnabled = !RubiksCubeGame.ParticlesEnabled));
        items.Add(Switch("Turn sound", RubiksCubeGame.TurnSoundEnabled, () => RubiksCubeGame.TurnSoundEnabled = !RubiksCubeGame.TurnSoundEnabled));

        SessionRecorder recorder = SessionRecorder.Instance;
        if (recorder != null)
        {
            items.Add(Section("Record"));
            items.Add(Switch(recorder.IsRecording ? "Stop recording" : "Record video + sound", recorder.IsRecording, recorder.Toggle));
            if (!recorder.IsRecording)
                items.Add(Choice(new[] { "720p10", "1080p10", "720p5" }, recorder.PresetIndex, index => recorder.PresetIndex = index));
        }

        items.Add(Section("Tools"));
        items.Add(Switch("Console", InGameConsole.Visible, () => InGameConsole.Visible = !InGameConsole.Visible));
        items.Add(Command("Solve now", () =>
        {
            LLMHandler handler = FindFirstObjectByType<LLMHandler>();
            if (handler != null) handler.SolveCube("How can I solve the cube?");
        }));
        items.Add(Command("Test sound", () =>
        {
            if (conversation == null) conversation = FindFirstObjectByType<ConversationHandler>();
            if (conversation != null) conversation.PlayTestSound();
        }));
        items.Add(Command("Benchmark", () =>
        {
            CubeBenchmark benchmark = FindFirstObjectByType<CubeBenchmark>();
            if (benchmark == null) benchmark = new GameObject("CubeBenchmark").AddComponent<CubeBenchmark>();
            benchmark.TogglePanel();
        }));

        return items;
    }

    void DrawToggleButtons()
    {
        EnsureDebugStyles();

        const float width = 176f, headerH = 26f, pad = 6f, rowGap = 4f, sectionH = 20f;
        // Right edge of the screen, just below ConversationHandler's four buttons.
        float x = DebugUi.Width - width - pad;
        float y = 112f;

        if (DebugUi.Header(new Rect(x, y, width, headerH), "Debug Tools", menuOpen)) menuOpen = !menuOpen;
        if (!menuOpen) { DebugUi.Menu = new Rect(x, y, width, headerH); return; }

        List<MenuItem> items = BuildMenu();

        // Shrink the rows to the screen height; scroll (mouse wheel) if that is still not enough.
        float panelY = y + headerH + pad;
        float available = DebugUi.Height - panelY - 8f;
        float rowH = 24f;
        float Content(float h) => pad + items.Sum(i => i.kind == ItemKind.Section ? sectionH : h + rowGap);
        float contentH = Content(rowH);
        if (contentH > available) { rowH = Mathf.Clamp(rowH * available / contentH, 18f, 24f); contentH = Content(rowH); }
        float viewH = Mathf.Min(contentH, available);
        bool scrolls = contentH > available;
        float panelW = width + pad * 2f;

        var panel = new Rect(x - pad, panelY, panelW, viewH);
        DebugUi.Background(panel);
        DebugUi.Menu = new Rect(panel.x, y, panel.width, panel.yMax - y);

        float itemW = scrolls ? width - 14f : width;
        float left = scrolls ? pad : x;
        float top = scrolls ? pad : panelY + pad;
        if (scrolls) menuScroll = GUI.BeginScrollView(panel, menuScroll, new Rect(0f, 0f, panelW - 16f, contentH));

        foreach (MenuItem item in items)
        {
            switch (item.kind)
            {
                case ItemKind.Section:
                    DebugUi.Section(new Rect(left + 2f, top, itemW, sectionH), item.label);
                    top += sectionH;
                    continue;
                case ItemKind.Toggle:
                    if (DebugUi.Toggle(new Rect(left, top, itemW, rowH), item.label, item.on)) item.action();
                    break;
                case ItemKind.Command:
                    if (DebugUi.Button(new Rect(left, top, itemW, rowH), item.label)) item.action();
                    break;
                case ItemKind.Choice:
                    int picked = DebugUi.Segmented(new Rect(left, top, itemW, rowH), item.options, item.selected);
                    if (picked != item.selected) item.choose(picked);
                    break;
            }
            top += rowH + rowGap;
        }

        if (scrolls) GUI.EndScrollView();
    }

    // -------------------------------------------------------------------------
    // 2D cross layout
    //
    // Every sticker square is split in two: the LEFT half is the actual cube (game state), the RIGHT half is what
    // the VLM remembers for that same sticker (faces it has not seen stay empty). Next to each face letter a small
    // square shows that face's trust score - lighter = more trusted. Tabs switch the right halves between the
    // memory and the individual reads of the latest look. The panel to the right holds the trust weights.
    // -------------------------------------------------------------------------
    private int hoverFacelet = -1;
    private CubeVlmMemory memory;
    private float drawNow;

    // The most recent image handed to the VLM (any look: regular, solve or question), shown in a small box.
    private Texture2D vlmImage;
    private float vlmImageTime;
    private string vlmImageLabel = "Sent to VLM";

    // "VLM" or "Classical", whichever reader is switched on.
    private string ReaderLabel => memory != null && memory.Look.classical ? "Classical" : "VLM";

    /// <summary>Shows the JPEG that was just captured for the reader - exactly what it gets to see.</summary>
    public void SetVlmImage(byte[] jpg, string label = null)
    {
        if (jpg == null || jpg.Length == 0) return;
        vlmImageLabel = label ?? "Sent to VLM";
        if (vlmImage == null) vlmImage = new Texture2D(2, 2);
        vlmImage.LoadImage(jpg);
        vlmImageTime = Time.time;
    }

    /// <summary>Forgets everything the VLM has seen (memory, latest reads, status) - e.g. after a bad look.</summary>
    public void ClearVlm()
    {
        memory?.Clear();
        vlmSamples = new List<string>();
        vlmDisputed = new bool[54];
        vlmDisputedCount = 0;
        vlmStatus = "";
        vlmHasData = false;
        vlmMode = 0;
    }

    void OnDestroy()
    {
        if (vlmImage != null) Destroy(vlmImage);
    }

    /// <summary>Gives the overlay the VLM memory whose trust scores it shows and whose weights its sliders edit.</summary>
    public void AttachMemory(CubeVlmMemory m)
    {
        memory = m;
    }

    void Draw2D()
    {
        EnsureDebugStyles();
        drawNow = Time.time;

        float s   = 28f;
        float pad = 10f;
        float ox  = 10f;
        // Normally the 2D view starts at 150; on a small window it moves up (and the image box above it gets shorter) so it ends
        // above the STT caption box instead of covering it.
        float oy  = Mathf.Clamp(DebugUi.TopOfCaptionBoxes - 6f - (s * 9f + pad * 2f + 6f), 96f, 150f);

        float gridW = s * 12f + pad * 3f;
        float gridBottom = oy + s * 9f + pad * 2f;

        // One backdrop behind the grid, the tab buttons and the text. It ends right under the grid so it
        // never reaches the STT/LLM caption boxes further down the left side.
        Color prevColor = GUI.color;
        GUI.color = new Color(0.16f, 0.18f, 0.22f, 0.92f);
        GUI.DrawTexture(new Rect(ox - 6f, oy - 20f, gridW + 12f, gridBottom + 6f - (oy - 20f)), Texture2D.whiteTexture);
        GUI.color = prevColor;

        // Legend in the empty space right of the U face (the tabs sit in the free space on its left).
        float legendX = ox + s * 6f + pad * 2f;
        GUI.Label(new Rect(legendX, oy, ox + gridW - legendX, s * 3f),
            "Left half: actual cube\nRight half: " + ReaderLabel + (vlmMode == 0 ? " memory" : " read")
            + "\nSquare by a letter: trust (lighter = higher)", _vlmTextStyle);

        hoverFacelet = -1;
        string source = VlmSource();

        DrawFace2D(2, ox + (s*3+pad),   oy,               s, "U", source, false, true);
        DrawFace2D(1, ox,               oy + (s*3+pad),   s, "L", source);
        DrawFace2D(4, ox + (s*9+pad*3), oy + (s*3+pad),   s, "F", source, true, false);
        DrawFace2D(0, ox + (s*6+pad*2), oy + (s*3+pad),   s, "R", source);
        DrawFace2D(5, ox + (s*3+pad),   oy + (s*3+pad),   s, "B", source, true, false);
        DrawFace2D(3, ox + (s*3+pad),   oy + (s*6+pad*2), s, "D", source, false, true);

        DrawVlmTabs(ox, oy + 2f, s * 3f + pad - 6f);

        // Text goes into the empty space below the R and F faces (right of the D face).
        float textX = ox + s * 6f + pad * 2f;
        float textY = oy + s * 6f + pad * 2f;
        DrawVlmText(textX, textY, ox + gridW - textX, gridBottom + 4f - textY, source);

        float panelX = ox + gridW + 18f;
        float panelW = 216f;
        float panelY = oy - 20f;
        float panelH = gridBottom + 6f - panelY;
        DrawTrustPanel(panelX, panelY, panelW, panelH);
        DrawVlmImageBox(panelX, 4f, panelW, panelY - 8f);

        DebugUi.View2D = new Rect(ox - 6f, 4f, panelX + panelW - (ox - 6f), panelY + panelH - 4f);
    }

    // Facelet index (0..53, order U R F D L B, row-major as looking straight at each face) of an app cell.
    // Verified against a real cube model: the app's faces line up with the standard facelet faces, except
    // that the app's Right and Left faces store their columns mirrored.
    private static readonly int[] StdFaceOfAppFace = CubeGroundTruth.StdFaceOfAppFace; // app: R L U D F B -> index in "URFDLB"

    static int FaceletIndex(int appFace, int row, int col)
    {
        return CubeGroundTruth.FaceletIndex(appFace, row, col);
    }

    // Letter (face whose center has this color) for an actual sticker color.
    static char ActualLetter(int colorIdx)
    {
        return colorIdx >= 1 && colorIdx <= 6 ? "RLUDFB"[colorIdx - 1] : '?';
    }

    // The 54 letters currently shown in the right halves ('?' = unknown), or null if there is nothing to show.
    string VlmSource()
    {
        if (vlmMode == 0) return memory != null ? memory.ToFaceletString() : null;
        return vlmMode - 1 < vlmSamples.Count ? vlmSamples[vlmMode - 1] : null;
    }

    void DrawFace2D(int face, float x, float y, float s, string label, string vlmSource, bool flipX = false, bool flipY = false)
    {
        GUI.color = Color.white;

        Color emptyHalf = new Color(0.25f, 0.27f, 0.3f);
        Color amber = new Color(1f, 0.75f, 0.1f);
        Color mismatch = new Color(0.95f, 0.25f, 0.25f);
        Vector2 mouse = Event.current.mousePosition;

        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            int dataR = flipY ? 2 - r : r;
            int dataC = flipX ? 2 - c : c;

            int colorIdx = (int)cube.GetColor(face, dataR, dataC);
            Color32 bg = colorIdx < faceColors.Length ? faceColors[colorIdx] : Color.magenta;

            Rect cell = new Rect(x + c*s, y + r*s, s-1, s-1);
            float half = Mathf.Floor(cell.width / 2f);

            // Left half: actual cube.
            GUI.color = bg;
            GUI.DrawTexture(new Rect(cell.x, cell.y, half, cell.height), Texture2D.whiteTexture);

            // Right half: the same sticker as the VLM remembers / read it ('?' = not seen).
            int fi = FaceletIndex(face, dataR, dataC);
            char vlmLetter = vlmSource != null ? vlmSource[fi] : '\0';
            if (vlmLetter == '?') vlmLetter = '\0';
            GUI.color = vlmLetter != '\0' ? VlmLetterColor(vlmLetter) : emptyHalf;
            GUI.DrawTexture(new Rect(cell.x + half, cell.y, cell.width - half, cell.height), Texture2D.whiteTexture);

            // Red outline: the VLM got this sticker wrong. Amber: the reads of the latest look disagreed here.
            bool wrong = vlmLetter != '\0' && vlmLetter != ActualLetter(colorIdx);
            if (wrong) DrawOutline(cell, mismatch, 2f);
            else if (vlmDisputed[fi]) DrawOutline(cell, amber, 2f);

            // On a disputed sticker, show every read's opinion as small swatches.
            if (vlmDisputed[fi])
            {
                int n = vlmSamples.Count;
                float sw = Mathf.Min(5f, (cell.width - 4f) / n - 1f);
                for (int k = 0; k < n; k++)
                {
                    if (vlmSamples[k] == null || vlmSamples[k][fi] == '?') continue;
                    Rect swatch = new Rect(cell.x + 3f + k * (sw + 1f), cell.yMax - sw - 3f, sw, sw);
                    GUI.color = Color.black;
                    GUI.DrawTexture(new Rect(swatch.x - 1f, swatch.y - 1f, sw + 2f, sw + 2f), Texture2D.whiteTexture);
                    GUI.color = VlmLetterColor(vlmSamples[k][fi]);
                    GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                }
            }

            if (cell.Contains(mouse)) hoverFacelet = fi;
        }

        GUI.color = new Color(0.88f, 0.9f, 0.94f); // light, since the labels sit on the dark backdrop
        GUI.Label(new Rect(x, y - 14, 40, 14), label);

        // Trust of this face as a small square (lighter = higher) with the percentage beside it.
        if (memory != null)
        {
            int stdFace = StdFaceOfAppFace[face];
            bool seen = memory.Face(stdFace) != null;
            float trust = memory.Trust(stdFace, drawNow);
            Color dark = new Color(0.1f, 0.11f, 0.14f);
            Rect box = new Rect(x + 16f, y - 13f, 11f, 11f);
            GUI.color = seen ? Color.Lerp(dark, Color.white, trust) : dark;
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            DrawOutline(box, new Color(0.45f, 0.48f, 0.55f), 1f);
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 30f, y - 15f, 48f, 15f), seen ? trust.ToString("P0") : "--", _vlmTextStyle);
        }

        GUI.color = Color.white;
    }

    // -------------------------------------------------------------------------
    // VLM look: data, tabs, text and the trust panel. The squares themselves are drawn by DrawFace2D.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by LLMHandler's solve flow with the independent reads of the latest look, each flattened to 54
    /// letters ('?' for faces that read did not report, null for an unusable read). Turns the 2D view on so the
    /// result is visible right away. The remembered state itself comes from the attached CubeVlmMemory.
    /// </summary>
    public void SetVlmObservation(IList<string> flatReads, string status, bool revealView = true)
    {
        vlmSamples = new List<string>();
        if (flatReads != null)
        {
            foreach (string s in flatReads)
                vlmSamples.Add(!string.IsNullOrEmpty(s) && s.Length == 54 ? s : null);
        }

        // A null status keeps the previous one (regular background looks don't have a status of their own).
        if (status != null) vlmStatus = status;
        vlmHasData = true;

        if (revealView)
        {
            vlmMode = 0;
            show2D = true;
        }
        else if (vlmMode > vlmSamples.Count)
        {
            vlmMode = 0; // the tab the user was on no longer exists
        }

        vlmDisputedCount = 0;
        for (int i = 0; i < 54; i++)
        {
            char first = '\0';
            bool disputed = false;
            foreach (string s in vlmSamples)
            {
                if (s == null || s[i] == '?') continue;
                if (first == '\0') first = s[i];
                else if (s[i] != first) disputed = true;
            }
            vlmDisputed[i] = disputed;
            if (disputed) vlmDisputedCount++;
        }
    }

    public void SetVlmStatus(string status)
    {
        vlmStatus = status ?? "";
    }

    public void AppendVlmStatus(string more)
    {
        if (!string.IsNullOrEmpty(more)) vlmStatus = (string.IsNullOrEmpty(vlmStatus) ? "" : vlmStatus + "\n") + more;
    }

    static Color VlmLetterColor(char letter)
    {
        int face = VlmFaceOrder.IndexOf(letter);
        if (face < 0) return new Color(0.4f, 0.4f, 0.4f);
        // U white, R red, F green, D yellow, L orange, B blue - same scheme as the cube itself.
        int[] colorIndexByFace = { 3, 1, 5, 4, 2, 6 };
        return faceColors[colorIndexByFace[face]];
    }

    static void DrawOutline(Rect r, Color color, float thickness)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - thickness, r.y, thickness, r.height), Texture2D.whiteTexture);
    }

    // Tab buttons (VLM memory, then each read of the latest look) stacked in the free corner left of the U face.
    void DrawVlmTabs(float x, float y, float width)
    {
        Color activeColor = new Color(0.29f, 0.47f, 0.82f);
        Color inactiveColor = new Color(0.22f, 0.24f, 0.28f);
        Color prevBg = GUI.backgroundColor;
        Color prevColor = GUI.color;
        GUI.color = Color.white;

        int tabCount = 1 + vlmSamples.Count;
        float tabH = Mathf.Min(20f, 80f / tabCount - 2f);
        for (int t = 0; t < tabCount; t++)
        {
            bool classical = memory != null && memory.Look.classical;
            string tabLabel = t == 0 ? (classical ? "Memory" : "VLM memory")
                : (classical ? "Classical read" : $"Read {t}") + (vlmSamples[t - 1] == null ? " (x)" : "");
            GUI.backgroundColor = vlmMode == t ? activeColor : inactiveColor;
            if (GUI.Button(new Rect(x, y + t * (tabH + 2f), width, tabH), tabLabel, _tabStyle)) vlmMode = t;
        }

        GUI.backgroundColor = prevBg;
        GUI.color = prevColor;
    }

    // Summary / hover / legend text underneath the grid.
    void DrawVlmText(float x, float y, float w, float h, string source)
    {
        int usable = 0;
        foreach (string smp in vlmSamples) if (smp != null) usable++;

        string text;
        if (!vlmHasData && (memory == null || memory.SeenCount == 0))
        {
            text = ReaderLabel + ": nothing seen yet. Ask it to solve the cube; the faces it sees are remembered here.";
        }
        else if (hoverFacelet >= 0 && source != null && source[hoverFacelet] != '?')
        {
            var reads = new List<string>();
            foreach (string smp in vlmSamples)
                reads.Add(smp != null && smp[hoverFacelet] != '?' ? smp[hoverFacelet].ToString() : "-");
            string remembered = memory != null ? memory.ToFaceletString()[hoverFacelet].ToString() : "?";
            text = $"{VlmFaceOrder[hoverFacelet / 9]}{hoverFacelet % 9 + 1}: reads {string.Join(" · ", reads)} -> memory {remembered}";
        }
        else
        {
            int known = memory != null ? memory.SeenCount : 0;
            string summary = vlmHasData
                ? $"{ReaderLabel}: {usable}/{vlmSamples.Count} reads, {known}/6 faces known, {vlmDisputedCount} disputed"
                : $"{ReaderLabel}: {known}/6 faces known";
            if (memory != null && memory.MovesDetected > 0)
                summary += $" · {memory.MovesDetected} move{(memory.MovesDetected == 1 ? "" : "s")} seen, last {drawNow - memory.LastMoveTime:0} s ago";
            string detail = source == null
                ? (vlmMode == 0 ? "" : "This read failed.")
                : (vlmMode == 0 ? "Red: VLM wrong. Amber: reads differ." : "Red: this read is wrong.");
            text = summary + (string.IsNullOrEmpty(vlmStatus) ? "" : "\n" + vlmStatus)
                + (detail.Length > 0 ? "\n" + detail : "");
        }

        Color prev = GUI.color;
        GUI.color = Color.white;
        GUI.Label(new Rect(x, y, w, h), text, _vlmTextStyle);
        GUI.color = prev;
    }

    // Small box above the trust panel: the last image the VLM was given, and a button to wipe its memory.
    void DrawVlmImageBox(float x, float y, float w, float h)
    {
        Color prev = GUI.color;
        GUI.color = new Color(0.16f, 0.18f, 0.22f, 0.92f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float captionH = 40f; // two rows: what the picture is, then the timer and the button
        Rect imageRect = new Rect(x + 6f, y + 4f, w - 12f, h - captionH - 8f);
        if (vlmImage != null)
        {
            GUI.DrawTexture(imageRect, vlmImage, ScaleMode.ScaleToFit);
        }
        else
        {
            GUI.Label(imageRect, $"No image has been sent to the {ReaderLabel} reader yet.", _vlmTextStyle);
        }

        float captionY = y + h - captionH - 2f;
        GUI.Label(new Rect(x + 6f, captionY, w - 12f, 16f), vlmImage != null ? vlmImageLabel : "", _vlmTextStyle);
        string since = vlmImage != null ? $"{drawNow - vlmImageTime:0} s ago" : "";
        GUI.Label(new Rect(x + 6f, captionY + 20f, w - 104f, 16f), since, _vlmTextStyle);
        if (GUI.Button(new Rect(x + w - 92f, captionY + 17f, 86f, 20f), "Clear memory", _tabStyle)) ClearVlm();

        GUI.color = prev;
    }

    // Trust weights (sliders) and the per-face numbers they produce.
    void DrawTrustPanel(float x, float y, float w, float h)
    {
        Color prev = GUI.color;
        GUI.color = new Color(0.16f, 0.18f, 0.22f, 0.92f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float px = x + 10f;
        float pw = w - 20f;
        float py = y + 6f;

        if (memory == null)
        {
            GUI.Label(new Rect(px, py, pw, 16f), "Trust = w·recency + (1-w)·stability", _vlmTextStyle);
            GUI.Label(new Rect(px, py + 22f, pw, 40f), "No VLM memory attached yet.", _vlmTextStyle);
            GUI.color = prev;
            return;
        }

        GUI.Label(new Rect(px, py, pw, 16f), "Reader: " + ReaderLabel, _vlmTextStyle);
        py += 20f;
        GUI.Label(new Rect(px, py, pw, 16f), "Face trust = recency x w + stability x (1 - w)", _vlmTextStyle);
        py += 18f;

        VlmTrustParams p = memory.Params;

        GUI.Label(new Rect(px, py, pw, 15f), $"Recency {p.recencyWeight:P0}  /  stability {1f - p.recencyWeight:P0}", _vlmTextStyle);
        py += 15f;
        p.recencyWeight = GUI.HorizontalSlider(new Rect(px, py + 2f, pw, 14f), p.recencyWeight, 0f, 1f);
        py += 20f;

        GUI.Label(new Rect(px, py, pw, 15f), $"Forget time (recency): {p.forgetSeconds:0} s", _vlmTextStyle);
        py += 15f;
        p.forgetSeconds = GUI.HorizontalSlider(new Rect(px, py + 2f, pw, 14f), p.forgetSeconds, 5f, 300f);
        py += 20f;

        GUI.Label(new Rect(px, py, pw, 15f), $"Settle time (stability): {p.settleSeconds:0} s", _vlmTextStyle);
        py += 15f;
        p.settleSeconds = GUI.HorizontalSlider(new Rect(px, py + 2f, pw, 14f), p.settleSeconds, 5f, 300f);
        py += 20f;

        GUI.Label(new Rect(px, py, pw, 15f), $"Ask the user below: {p.askBelow:P0}", _vlmTextStyle);
        py += 15f;
        p.askBelow = GUI.HorizontalSlider(new Rect(px, py + 2f, pw, 14f), p.askBelow, 0f, 1f);
        py += 20f;

        // How often the VLM glances at the cube on its own (in addition to looking when asked to solve).
        VlmLookSettings look = memory.Look;
        look.regularly = GUI.Toggle(new Rect(px, py, pw, 16f), look.regularly, $" Look every {look.intervalSeconds:0.#} s");
        py += 16f;
        look.intervalSeconds = GUI.HorizontalSlider(new Rect(px, py + 2f, pw, 14f), look.intervalSeconds, 1f, 20f);
        py += 22f;

        // Per face: trust, how long ago it was last watched and when its stickers last changed.
        for (int f = 0; f < 6; f++)
        {
            CubeVlmMemory.FaceState st = memory.Face(f);
            string name = CubeVlmMemory.FaceOrder[f] + " " + CubeVlmMemory.ColorName(CubeVlmMemory.FaceOrder[f]);
            string line;
            if (st == null)
            {
                line = $"{name}: not seen";
            }
            else
            {
                float trust = memory.Trust(f, drawNow);
                line = $"{name}: {trust:P0}  seen {drawNow - st.LastSeen:0}s, chg {drawNow - st.LastChanged:0}s"
                    + (trust < p.askBelow ? "  ask" : "");
            }
            GUI.Label(new Rect(px, py, pw, 15f), line, _vlmTextStyle);
            py += 12f;
        }

        GUI.color = prev;
    }
}
