using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The Unity console on the screen: every log line, warning and error of the running app, in a panel over the cube view -
/// so it can be read (and recorded) without the Editor's Console window. Opened from Debug Tools -> Console. Starts
/// collecting as soon as the scene loads, so what happened before the panel was opened is there too.
/// </summary>
public class InGameConsole : MonoBehaviour
{
    private struct Entry
    {
        public LogType Type;
        public string Text;
        public float Time;
        public int Count;
    }

    private static InGameConsole instance;

    /// <summary>Whether the panel is shown.</summary>
    public static bool Visible
    {
        get => instance != null && instance.visible;
        set { if (instance == null) Create(); if (instance != null) instance.visible = value; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("InGameConsole");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<InGameConsole>();
    }

    private const int MaxEntries = 400;
    private const int MaxLength = 320;

    private readonly List<Entry> entries = new List<Entry>();
    private readonly Queue<Entry> incoming = new Queue<Entry>(); // log calls can come from other threads
    private readonly object gate = new object();
    private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew(); // log callbacks come from any thread: no Unity Time here

    private bool visible;
    private int levelFilter;                 // 0 all, 1 warnings and errors, 2 errors only
    private string textFilter = "";
    private int sizeChoice = 1;              // 0 small, 1 medium, 2 large
    private bool stickToBottom = true;
    private Vector2 scroll;
    private GUIStyle lineStyle;
    private int lastDrawnCount = -1;

    private static readonly float[] Heights = { 130f, 220f, 360f };

    private void OnEnable() => Application.logMessageReceivedThreaded += OnLog;
    private void OnDisable() => Application.logMessageReceivedThreaded -= OnLog;

    private void OnLog(string condition, string stackTrace, LogType type)
    {
        // Only the first line: the stack trace is long and the message is what matters on screen.
        string text = condition ?? "";
        int newline = text.IndexOf('\n');
        if (newline >= 0) text = text.Substring(0, newline);
        if (text.Length > MaxLength) text = text.Substring(0, MaxLength) + "…";
        lock (gate) incoming.Enqueue(new Entry { Type = type, Text = text, Time = (float)clock.Elapsed.TotalSeconds, Count = 1 });
    }

    private void Update()
    {
        lock (gate)
        {
            while (incoming.Count > 0)
            {
                Entry entry = incoming.Dequeue();
                // The same line again and again (a routine look every few seconds) is one line with a count.
                if (entries.Count > 0 && entries[entries.Count - 1].Type == entry.Type && entries[entries.Count - 1].Text == entry.Text)
                {
                    Entry last = entries[entries.Count - 1];
                    last.Count++;
                    last.Time = entry.Time;
                    entries[entries.Count - 1] = last;
                }
                else
                {
                    entries.Add(entry);
                    if (entries.Count > MaxEntries) entries.RemoveAt(0);
                }
            }
        }
    }

    private static Color ColorOf(LogType type)
    {
        switch (type)
        {
            case LogType.Warning: return new Color(1f, 0.82f, 0.3f);
            case LogType.Error:
            case LogType.Exception:
            case LogType.Assert: return new Color(1f, 0.45f, 0.45f);
            default: return new Color(0.88f, 0.9f, 0.94f);
        }
    }

    private bool Passes(Entry e)
    {
        bool isError = e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert;
        if (levelFilter == 2 && !isError) return false;
        if (levelFilter == 1 && !isError && e.Type != LogType.Warning) return false;
        return textFilter.Length == 0 || e.Text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void OnGUI()
    {
        if (!visible)
        {
            DebugUi.Console = Rect.zero;
            DebugUi.TextInputActive = false;
            return;
        }

        Matrix4x4 unscaled = DebugUi.Begin();
        try { DrawPanel(); }
        finally { DebugUi.End(unscaled); }
    }

    private void DrawPanel()
    {
        if (lineStyle == null)
        {
            lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = false, clipping = TextClipping.Clip };
            lineStyle.padding = new RectOffset(2, 2, 0, 0);
        }

        // Over the top of the cube view, left of the Debug Tools column.
        float scale = DebugUi.Scale;
        Rect pixels = Camera.main != null ? Camera.main.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
        float left = pixels.x / scale + 12f;
        float width = Mathf.Clamp(pixels.width / scale - 226f, 320f, 780f);
        float height = Mathf.Min(Heights[sizeChoice], DebugUi.Height - 40f);
        var panel = new Rect(left, 8f, width, height);
        DebugUi.Console = panel;
        DebugUi.Background(panel);

        float x = panel.x + 8f, y = panel.y + 6f, w = panel.width - 16f;

        // ---- header: level filter, search, size, clear, copy, close
        float rowH = 22f;
        levelFilter = DebugUi.Segmented(new Rect(x, y, 150f, rowH), new[] { "All", "Warn", "Error" }, levelFilter);
        GUI.SetNextControlName("ConsoleFilter");
        textFilter = GUI.TextField(new Rect(x + 158f, y, Mathf.Max(60f, w - 158f - 238f), rowH), textFilter);
        float bx = panel.xMax - 8f - 232f;
        sizeChoice = DebugUi.Segmented(new Rect(bx, y, 70f, rowH), new[] { "S", "M", "L" }, sizeChoice);
        if (DebugUi.Button(new Rect(bx + 76f, y, 50f, rowH), "Clear")) { lock (gate) { entries.Clear(); incoming.Clear(); } }
        if (DebugUi.Button(new Rect(bx + 130f, y, 46f, rowH), "Copy"))
            GUIUtility.systemCopyBuffer = string.Join("\n", entries.Where(Passes).Select(e => $"[{e.Time:0.0}] {e.Text}" + (e.Count > 1 ? $" (x{e.Count})" : "")));
        if (DebugUi.Button(new Rect(bx + 180f, y, 24f, rowH), "×")) visible = false;
        DebugUi.TextInputActive = GUI.GetNameOfFocusedControl() == "ConsoleFilter";
        y += rowH + 6f;

        // ---- the lines
        List<Entry> shown = entries.Where(Passes).ToList();
        const float lineH = 16f;
        float viewH = panel.yMax - y - 8f;
        float contentH = shown.Count * lineH;
        var view = new Rect(x, y, w, viewH);
        var content = new Rect(0, 0, w - 18f, Mathf.Max(contentH, viewH));

        // Follow the newest line, unless the user has scrolled up to read something.
        float maxScroll = Mathf.Max(0f, contentH - viewH);
        if (shown.Count != lastDrawnCount)
        {
            if (stickToBottom) scroll.y = maxScroll;
            lastDrawnCount = shown.Count;
        }

        scroll = GUI.BeginScrollView(view, scroll, content);
        int first = Mathf.Max(0, (int)(scroll.y / lineH) - 1);
        int last = Mathf.Min(shown.Count, first + (int)(viewH / lineH) + 3);
        Color before = GUI.color;
        for (int i = first; i < last; i++)
        {
            Entry e = shown[i];
            GUI.color = ColorOf(e.Type);
            string text = e.Count > 1 ? $"{e.Text}  (x{e.Count})" : e.Text;
            GUI.Label(new Rect(0, i * lineH, w - 18f, lineH), text, lineStyle);
        }
        GUI.color = before;
        GUI.EndScrollView();

        // At the bottom = follow; scrolled up = stay where the user is.
        stickToBottom = scroll.y >= maxScroll - 4f;
    }
}
