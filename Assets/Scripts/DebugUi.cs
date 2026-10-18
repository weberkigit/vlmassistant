using UnityEngine;

/// <summary>
/// The look and the screen areas of the on-screen controls (conversation buttons, Debug Tools menu, 2D view,
/// benchmark panel), so they all share one style and the cube knows where a mouse press is a button click and not
/// the start of a drag. Everything here is meant to be called from OnGUI.
/// </summary>
public static class DebugUi
{
    public static readonly Color Accent = new Color(0.29f, 0.47f, 0.82f);
    public static readonly Color Off = new Color(0.22f, 0.24f, 0.28f);
    public static readonly Color Action = new Color(0.30f, 0.34f, 0.42f);
    public static readonly Color Panel = new Color(0.16f, 0.18f, 0.22f, 0.92f);
    public static readonly Color Muted = new Color(0.62f, 0.66f, 0.74f);

    // ---- scale ----------------------------------------------------------------------------------------------------
    //
    // The scene's Canvas scales with the window (reference 1920x1080, halfway between width and height). The OnGUI controls
    // are scaled the same way, so on a 4K screen they are not half the size of the canvas boxes next to them, and on a small
    // window they do not cover them. A floor keeps the text readable in a small Editor Game view. All the controls are laid
    // out in "GUI units": Width and Height are the screen in those units.

    public const float ReferenceWidth = 1920f, ReferenceHeight = 1080f, MinimumScale = 0.75f, MaximumScale = 3f;

    public static float Scale
    {
        get
        {
            float w = Screen.width / ReferenceWidth, h = Screen.height / ReferenceHeight;
            return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0.0001f, w * h)), MinimumScale, MaximumScale);
        }
    }

    // The scale the scene's Canvas itself uses (no floor).
    public static float CanvasScale => Mathf.Sqrt(Mathf.Max(0.0001f, (Screen.width / ReferenceWidth) * (Screen.height / ReferenceHeight)));

    private static RectTransform captionBox;
    private static bool captionBoxLooked;

    /// <summary>
    /// Where the canvas boxes at the bottom left (STT and LLM captions) start, from the top of the screen, in GUI units: what
    /// the 2D view has to stay above so it does not cover them. Read from the scene, with the designed position as fallback.
    /// </summary>
    public static float TopOfCaptionBoxes
    {
        get
        {
            if (!captionBoxLooked)
            {
                captionBoxLooked = true;
                GameObject go = GameObject.Find("STT Background");
                if (go != null) captionBox = go.GetComponent<RectTransform>();
            }

            float topPixels;
            if (captionBox != null)
            {
                var corners = new Vector3[4];
                captionBox.GetWorldCorners(corners);      // 0 bottom left, 1 top left; pixels on a screen-space canvas
                topPixels = Screen.height - corners[1].y;
            }
            else
            {
                topPixels = Screen.height - 550f * CanvasScale; // the STT box: 290 up from the bottom, 260 high, in 1080p units
            }
            return topPixels / Scale;
        }
    }

    public static float Width => Screen.width / Scale;
    public static float Height => Screen.height / Scale;

    /// <summary>Starts drawing in GUI units; pass the result to End when the controls are drawn.</summary>
    public static Matrix4x4 Begin()
    {
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
        return previous;
    }

    public static void End(Matrix4x4 previous) => GUI.matrix = previous;

    // ---- areas that swallow mouse presses (GUI units: origin top-left) ----

    public static Rect ConversationButtons = Rect.zero;
    public static Rect Menu = Rect.zero;
    public static Rect View2D = Rect.zero;
    public static Rect Benchmark = Rect.zero;
    public static Rect Solution = Rect.zero;
    public static Rect Console = Rect.zero;

    /// <summary>True while a text box has the keyboard: the cube's key shortcuts (R, L, U ...) must not fire then.</summary>
    public static bool TextInputActive;

    /// <summary>True if a mouse position from the Input System (origin bottom-left) is over any of the controls.</summary>
    public static bool IsPointerOver(Vector2 inputScreenPos)
    {
        float scale = Scale;
        Vector2 gui = new Vector2(inputScreenPos.x / scale, (Screen.height - inputScreenPos.y) / scale);
        return ConversationButtons.Contains(gui) || Menu.Contains(gui) || View2D.Contains(gui) || Benchmark.Contains(gui) || Solution.Contains(gui) || Console.Contains(gui);
    }

    // ---- styles ----

    private static GUIStyle row, rowToggle, header, section, label;

    private static void EnsureStyles()
    {
        if (row != null) return;
        row = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        rowToggle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(28, 6, 3, 3),
        };
        header = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        section = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
        section.normal.textColor = Muted;
        label = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        label.normal.textColor = new Color(0.88f, 0.9f, 0.94f);
    }

    public static GUIStyle Label { get { EnsureStyles(); return label; } }

    // ---- controls ----

    /// <summary>A plain button. Returns true when clicked.</summary>
    public static bool Button(Rect rect, string text, bool highlighted = false)
    {
        EnsureStyles();
        Color previous = GUI.backgroundColor;
        GUI.backgroundColor = highlighted ? Accent : Action;
        bool clicked = GUI.Button(rect, text, row);
        GUI.backgroundColor = previous;
        return clicked;
    }

    /// <summary>A switch: the whole row is a button with a check box on its left. Returns true when clicked.</summary>
    public static bool Toggle(Rect rect, string text, bool on)
    {
        EnsureStyles();
        Color previous = GUI.backgroundColor;
        GUI.backgroundColor = on ? Accent : Off;
        bool clicked = GUI.Button(rect, text, rowToggle);
        GUI.backgroundColor = previous;

        float size = Mathf.Min(12f, rect.height - 8f);
        var box = new Rect(rect.x + 9f, rect.y + (rect.height - size) / 2f, size, size);
        Color before = GUI.color;
        GUI.color = on ? Color.white : new Color(0.1f, 0.11f, 0.14f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        if (on)
        {
            GUI.color = Accent;
            GUI.DrawTexture(new Rect(box.x + 3f, box.y + 3f, box.width - 6f, box.height - 6f), Texture2D.whiteTexture);
        }
        GUI.color = before;
        return clicked;
    }

    /// <summary>One of several exclusive choices side by side. Returns the selected index.</summary>
    public static int Segmented(Rect rect, string[] options, int selected)
    {
        EnsureStyles();
        float gap = 3f;
        float width = (rect.width - gap * (options.Length - 1)) / options.Length;
        for (int i = 0; i < options.Length; i++)
        {
            var cell = new Rect(rect.x + i * (width + gap), rect.y, width, rect.height);
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = i == selected ? Accent : Off;
            if (GUI.Button(cell, options[i], row)) selected = i;
            GUI.backgroundColor = previous;
        }
        return selected;
    }

    /// <summary>A small grey caption above a group of controls.</summary>
    public static void Section(Rect rect, string text)
    {
        EnsureStyles();
        GUI.Label(rect, text.ToUpperInvariant(), section);
    }

    /// <summary>The wide button that opens or closes a group of controls.</summary>
    public static bool Header(Rect rect, string text, bool open)
    {
        EnsureStyles();
        Color previous = GUI.backgroundColor;
        GUI.backgroundColor = open ? Accent : Off;
        bool clicked = GUI.Button(rect, (open ? "▲ " : "▼ ") + text, header);
        GUI.backgroundColor = previous;
        return clicked;
    }

    /// <summary>A dark rounded-looking background for a panel.</summary>
    public static void Background(Rect rect)
    {
        Color previous = GUI.color;
        GUI.color = Panel;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
