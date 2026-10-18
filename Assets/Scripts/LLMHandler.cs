using LLMUnity;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using System;
using UnityEngine.Networking;
using UnityEngine.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;

public class LLMHandler : MonoBehaviour
{
    [Header("Scripts")]
    public TTSHandler ttsHandler;

    [Header("LLM")]
    public LLMAgent llmCharacter;
    public bool waitingForReply = false;

    [Header("Status")]
    public Image statusIndicator; // UI element to show status
    bool isConnected = false;

    [Header("Connection")]
    [Tooltip("How many extra times to retry the LLM warmup if the server isn't reachable yet (e.g. still " +
        "loading the model when Unity starts). Without this, a slow-starting server left Unity stuck with a " +
        "dead connection until it was restarted.")]
    public int warmupMaxRetries = 5;
    public float warmupRetryDelaySeconds = 3f;
    public TMP_Text llmResponseText;

    public TMP_Text subtitle;
    public float displayTime = 3f; // How long the subtitle stays fully visible
    public float fadeOutDuration = 0.5f;

    private Coroutine _activeFadeCoroutine;


    [Header("Test")]
    public string inputForLLM;
    public Texture2D imageForVLM;
    private DateTime startTime;
    float timer = 0.0f;
    public float timeout = 1.0f;

    [Header("Connection robustness")]
    [Tooltip("A chat or vision request that has not answered after this many seconds is cancelled, the user is told, and the " +
        "connection is checked again. Without it a request that never comes back leaves the assistant waiting - and the " +
        "microphone muted - for good.")]
    public float requestTimeoutSeconds = 90f;
    [Tooltip("While the LLM server cannot be reached, try again this often (the pause grows a little with every failure).")]
    public float reconnectIntervalSeconds = 8f;
    private string latestQuestion;
    private string LatestResponse;

    private int retryCount = 1;
    private int currentRetryCount = 0;

    [Header("Camera Source")]
    public bool useRealCamera = false;
    public string webCamDeviceName = ""; // empty = use the default device
    private WebCamTexture webCamTexture;
    [Tooltip("Captured frames are downscaled so neither dimension exceeds this before being sent to the VLM. " +
        "A full 1920x1080 capture costs ~2000 vision tokens and >1s of processing; 768 cuts that to ~350 tokens " +
        "and ~300ms with no measurable loss in description quality.")]
    public int vlmImageMaxDimension = 768;

    [Header("Conversation Memory")]
    [Tooltip("Chat history is trimmed to the last N messages after every response, so latency doesn't keep " +
        "growing across a session (generation cost scales with total context length). Set to 1 to keep only " +
        "the single most recent message; 0 clears history after every turn.")]
    public int maxHistoryMessages = 0;

    [Header("Cube Solver")]
    public CubeSolverClient cubeSolverClient;
    [Tooltip("How many times to independently re-read the visible faces via the VLM before voting per face.")]
    public int faceletSampleCount = 3;
    [Tooltip("Minimum average per-sticker agreement between the reads of a face before that face is accepted " +
        "into the memory. Faces below this count as 'not clearly seen' for that look.")]
    [Range(0f, 1f)] public float minFaceletConfidence = 0.6f;
    [Tooltip("Token limit for the face-reading calls. Reporting several faces as JSON needs more room than " +
        "the short conversational replies the scene's numPredict is sized for.")]
    public int faceReadNumPredict = 400;

    /// <summary>
    /// What the VLM has seen of each cube face so far, with per-face trust scores. Its weights are the ones
    /// the sliders in the debug overlay's 2D view edit.
    /// </summary>
    public CubeVlmMemory vlmMemory { get; } = new CubeVlmMemory();

    [Header("Prompts")]
    [Tooltip("Subfolder under StreamingAssets holding the prompt .txt files, loaded at startup. If a file " +
        "is missing or fails to load, the built-in fallback text below is used instead (or, for the main " +
        "system prompt, whatever is set in the Inspector).")]
    public string promptsFolder = "Prompts";

    // Fallback text, used only if the corresponding file under StreamingAssets/<promptsFolder>/ can't be
    // loaded - keeps the app working even with a missing/corrupt prompt file.
    private string visionOnlySystemPrompt =
        "You are a vision system. Follow only the specific instruction below about the image. Do not add "
        + "commentary, disclaimers, or unrelated information. Reply with exactly the JSON object requested, nothing else.";

    private string sceneDescriptionPrompt =
        "Describe this Rubik's Cube in 1-2 short sentences: the colors visible on each face and its current "
        + "orientation. Do not answer any question. Reply as {\"response\": \"<description>\"}.";

    // Fallbacks for the plain-letters face reading (StreamingAssets/Prompts/face_reading_plain_prompt.txt) and for the
    // prompt that asks the language model to combine several reads (merge_reads_prompt.txt).
    private string faceReadingPlainPrompt =
        "You are reading a Rubik's Cube from an image. Report only the faces you can actually see. Colors: W white, Y yellow, "
        + "R red, O orange, G green, B blue. For every clearly visible face write ONE line: "
        + "face center=<color of its middle sticker> up=<center color of the neighbouring visible face at its top edge, or none> "
        + "stickers=<its 9 colors, left to right, top row first, no spaces>. Only those lines, nothing else.";

    private string faceReadingWordsPrompt =
        "You are reading a Rubik's Cube from an image. Report only the faces you can actually see. The colors are white, "
        + "yellow, red, orange, green and blue: always write them as these full words. For every clearly visible face give "
        + "\"center\" (color of its middle sticker), \"up\" (center color of the visible neighbouring face at its top edge, or "
        + "null) and \"stickers\" (a list of its 9 colors, left to right, top row first). Reply with exactly one JSON object: "
        + "{\"faces\": [{\"center\": \"<color word>\", \"up\": \"<color word or null>\", \"stickers\": [\"<color word>\", ...]}]}";

    private string faceReadingPlainWordsPrompt =
        "You are reading a Rubik's Cube from an image. Report only the faces you can actually see. The colors are white, "
        + "yellow, red, orange, green and blue: always write them as these full words. For every clearly visible face write "
        + "ONE line: face center=<color word> up=<color word or none> stickers=<9 color words separated by spaces, left to "
        + "right, top row first>. Only those lines, nothing else.";

    private string mergeReadsPrompt =
        "Several readings of the same Rubik's Cube were made by a vision system, 9 letters per face (U R F D L B are the face "
        + "names). Each can be wrong in a few stickers and can miss faces.\n\n{READINGS}\n\n"
        + "Combine them into ONE answer: for every face in any reading give the 9 letters most likely correct (at each position the "
        + "letter most readings agree on, never a letter no reading has there; the 5th letter is the face's own letter). Reply with "
        + "exactly one JSON object: {\"faces\": {\"<face letter>\": \"<9 letters>\"}}";

    private const string mergeSystemPrompt =
        "You combine several noisy readings of the same Rubik's Cube into one. Follow the instruction exactly and reply with "
        + "exactly the JSON object requested, nothing else.";

    // Keep in sync with StreamingAssets/Prompts/face_reading_prompt.txt (this is only the fallback).
    private string faceReadingPrompt =
        "You are reading a Rubik's Cube from an image. Report only the faces you can actually see. Never guess a face "
        + "that is hidden or mostly hidden.\n\n"
        + "Colors: W white, Y yellow, R red, O orange, G green, B blue.\n\n"
        + "How this cube is built (the colors never change places - only the cube's position in the image does):\n"
        + "- Held with white on top and green facing you: white is up, yellow is down, green is front, blue is back, red is "
        + "on the right, orange is on the left.\n"
        + "- Opposite faces: white-yellow, red-orange, green-blue. So a camera never sees two opposite faces together, and "
        + "at most THREE faces are visible at once.\n"
        + "- Which color lies in which direction. Looking straight at a face in the standard way, the center colors of the "
        + "four faces around it are: white face: top blue, right red, bottom green, left orange; yellow face: top green, right "
        + "red, bottom blue, left orange; green face: top white, right red, bottom yellow, left orange; red face: top white, "
        + "right blue, bottom yellow, left green; blue face: top white, right orange, bottom yellow, left red; orange face: "
        + "top white, right green, bottom yellow, left blue. If you read a face turned another way, the same four colors move "
        + "one step round the circle top -> right -> bottom -> left per quarter turn (the white face read with red on top has "
        + "green on its right, orange at the bottom, blue on its left).\n\n"
        + "For every face that is clearly visible give: \"center\" (color of its middle sticker, which identifies the face), "
        + "\"up\" (color of the CENTER of the neighbouring visible face that touches this face's top edge as you read it; "
        + "null only if no neighbour is visible) and \"stickers\" (its 9 colors in that reading direction, left to right, "
        + "top row first).\n\n"
        + "Reply with exactly one JSON object and nothing else, in this format: "
        + "{\"faces\": [{\"center\": \"<color letter>\", \"up\": \"<color letter or null>\", \"stickers\": \"<9 color letters>\"}]}. "
        + "One entry per visible face; leave out faces you cannot see.";

    private CubeDebugOverlay debugOverlay;

    // Start is called before the first frame update
    void Start()
    {
        debugOverlay = FindFirstObjectByType<CubeDebugOverlay>();
        debugOverlay?.AttachMemory(vlmMemory);
        StartCoroutine(LoadPromptsThenWarmup());
    }

    private IEnumerator LoadPromptsThenWarmup()
    {
        yield return LoadPromptFile("vision_only_prompt.txt", text => visionOnlySystemPrompt = text);
        yield return LoadPromptFile("scene_description_prompt.txt", text => sceneDescriptionPrompt = text);
        yield return LoadPromptFile("face_reading_prompt.txt", text => faceReadingPrompt = text);
        yield return LoadPromptFile("face_reading_plain_prompt.txt", text => faceReadingPlainPrompt = text);
        yield return LoadPromptFile("face_reading_words_prompt.txt", text => faceReadingWordsPrompt = text);
        yield return LoadPromptFile("face_reading_plain_words_prompt.txt", text => faceReadingPlainWordsPrompt = text);
        yield return LoadPromptFile("merge_reads_prompt.txt", text => mergeReadsPrompt = text);
        yield return LoadPromptFile("main_system_prompt.txt", text => llmCharacter.systemPrompt = text);

        // The regular looks start right away: with the classical reader they need no model at all, so they must
        // not wait for (or depend on) the LLM server coming up. The VLM reader checks isConnected itself.
        StartCoroutine(LookRegularlyRoutine());

        yield return WarmupWithRetry();
    }

    // ------------------------------------------------------------------------------------------
    // Regular looking: besides looking when asked to solve, the VLM glances at the cube every few
    // seconds (interval and on/off are the sliders in the debug 2D view) so the per-face memory stays fresh.
    // ------------------------------------------------------------------------------------------

    [Tooltip("How many independent reads one regular look makes. One is cheap; changes to a face already in memory " +
        "are only applied once a second look confirms them, so a single noisy read can't overwrite it.")]
    public int regularLookReads = 1;

    // True while a regular look is talking to the model. The vision calls temporarily swap the agent's system
    // prompt and token limit, so a question must never start while one is in flight (see WaitForRegularLook).
    private bool lookInProgress;

    private IEnumerator LookRegularlyRoutine()
    {
        float waited = 0f;
        while (true)
        {
            yield return null;

            VlmLookSettings look = vlmMemory.Look;
            if (!look.regularly) { waited = 0f; continue; }

            waited += Time.deltaTime;
            if (waited < look.intervalSeconds) continue;

            // Never compete with a conversation turn for the model; try again in a second.
            bool modelNeeded = !look.classical;
            if (waitingForReply || lookInProgress || CubeBenchmark.Running || (modelNeeded && !isConnected))
            {
                waited = Mathf.Max(0f, look.intervalSeconds - 1f);
                continue;
            }

            waited = 0f;
            yield return LookOnce();
        }
    }

    private IEnumerator LookOnce()
    {
        lookInProgress = true;
        try
        {
            byte[] jpg = null;
            yield return CaptureImage(bytes => jpg = bytes, vlmMemory.Look.classical);
            if (jpg == null || waitingForReply) yield break; // a turn started while capturing

            var reads = new List<Dictionary<int, string>>();
            int readCount = vlmMemory.Look.classical ? 1 : Mathf.Max(1, regularLookReads);
            for (int i = 0; i < readCount; i++)
            {
                Dictionary<int, string> read = null;
                bool readDone = false;
                ReadFacesAsync(jpg, result =>
                {
                    read = result;
                    readDone = true;
                });
                bool readTimedOut = false;
                yield return WaitOrTimeout(() => readDone, requestTimeoutSeconds, t => readTimedOut = t);
                if (readTimedOut) { CancelStuckRequest("The regular look"); read = null; }
                reads.Add(ApplyReadChecks(read));
            }

            // A question arrived and cancelled this look: its reads are empty, so there is nothing to learn.
            if (waitingForReply) yield break;

            CubeVlmMemory.Observation observation =
                vlmMemory.Observe(reads, Time.time, minFaceletConfidence, confirmChanges: true);
            ReportMemoryEvents(observation);
            Debug.Log($"Regular {ReaderName} look: {observation.ReadsUsable}/{reads.Count} reads usable, accepted "
                + $"[{string.Join(",", observation.Accepted.Select(f => CubeVlmMemory.FaceOrder[f]))}], waiting for confirmation "
                + $"[{string.Join(",", observation.Pending.Select(f => CubeVlmMemory.FaceOrder[f]))}]");

            if (debugOverlay == null)
            {
                debugOverlay = FindFirstObjectByType<CubeDebugOverlay>();
                debugOverlay?.AttachMemory(vlmMemory);
            }
            // Don't pop the 2D view open or overwrite the last solve status for a background look.
            debugOverlay?.SetVlmObservation(ReadsForDisplay(reads), null, revealView: false);
            if (vlmMemory.Look.classical && classicalNote.Length > 0) debugOverlay?.AppendVlmStatus(classicalNote);
        }
        finally
        {
            lookInProgress = false;
        }
    }

    /// <summary>
    /// Cancels a regular look that is mid-flight and waits for it to wind down, so the caller can use the model
    /// (and its system prompt / token limit) without being interleaved with it.
    /// </summary>
    private IEnumerator WaitForRegularLook()
    {
        if (!lookInProgress) yield break;
        deliberateCancelUntil = Time.realtimeSinceStartup + 5f;
        try { llmCharacter.CancelRequests(); }
        catch (Exception ex) { Debug.LogWarning($"Could not cancel the regular look: {ex.Message}"); }
        while (lookInProgress) yield return null;
    }

    /// <summary>
    /// Reads a prompt file from StreamingAssets/&lt;promptsFolder&gt;/ via UnityWebRequest, which (unlike
    /// File.ReadAllText) works whether StreamingAssets is a plain folder (desktop) or packed inside the
    /// build (Android). Leaves the target unset - keeping its current fallback value - on any failure.
    /// </summary>
    private IEnumerator LoadPromptFile(string filename, Action<string> onLoaded)
    {
        string path = Path.Combine(Application.streamingAssetsPath, promptsFolder, filename);
        using (UnityWebRequest www = UnityWebRequest.Get(path))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success)
            {
                onLoaded?.Invoke(www.downloadHandler.text);
            }
            else
            {
                Debug.LogWarning($"Could not load prompt file '{filename}' from StreamingAssets/{promptsFolder}: {www.error}. Using built-in fallback.");
            }
        }
    }

    private IEnumerator WarmupWithRetry()
    {
        for (int attempt = 0; attempt <= warmupMaxRetries; attempt++)
        {
            isConnected = false;
            Task warmupTask = llmCharacter.Warmup(WarmUpCallback);
            yield return new WaitUntil(() => warmupTask.IsCompleted);

            if (isConnected) yield break;

            if (statusIndicator != null) statusIndicator.color = ServerStatusColors.Disconnected;

            if (attempt < warmupMaxRetries)
            {
                Debug.LogWarning($"LLM warmup failed (attempt {attempt + 1}/{warmupMaxRetries + 1}), retrying in {warmupRetryDelaySeconds}s...");
                yield return new WaitForSeconds(warmupRetryDelaySeconds);
            }
            else
            {
                Debug.LogError("LLM warmup failed after all retries. Is the LLM server running? "
                    + $"Will keep trying every {reconnectIntervalSeconds:0.#} s or more, so it connects as soon as the server is up.");
            }
        }
        EnsureReconnecting(immediate: false);
    }

    // ------------------------------------------------------------------------------------------
    // Staying connected: a server that takes a while to load its model, or that was restarted, must be picked up without
    // restarting the app; a request that never answers must not leave the assistant stuck.
    // ------------------------------------------------------------------------------------------

    private Coroutine reconnecting;

    private void EnsureReconnecting(bool immediate)
    {
        if (isConnected || reconnecting != null) return;
        reconnecting = StartCoroutine(ReconnectRoutine(immediate));
    }

    private IEnumerator ReconnectRoutine(bool immediate)
    {
        int attempts = 0;
        while (!isConnected)
        {
            if (!(immediate && attempts == 0))
                yield return new WaitForSeconds(Mathf.Min(30f, reconnectIntervalSeconds * (1f + attempts / 3f)));
            if (isConnected) break;

            attempts++;
            Task warmupTask = llmCharacter.Warmup(WarmUpCallback);
            yield return new WaitUntil(() => warmupTask.IsCompleted);

            if (!isConnected && (attempts == 1 || attempts % 10 == 0))
                Debug.LogWarning($"The LLM server is still not answering (attempt {attempts}); trying again later.");
        }
        reconnecting = null;
        Debug.Log("LLM server connected.");
    }

    /// <summary>A request failed or never answered: mark the connection as down and check it again right away.</summary>
    private float deliberateCancelUntil; // requests we cancelled on purpose answer with nothing - that is not a failure

    private void NoteFailedRequest(string reason)
    {
        if (Time.realtimeSinceStartup < deliberateCancelUntil) return;
        Debug.LogWarning($"LLM request failed ({reason}) - checking the connection.");
        isConnected = false;
        if (statusIndicator != null) statusIndicator.color = ServerStatusColors.Disconnected;
        EnsureReconnecting(immediate: true);
    }

    // Waits until done() is true, or the time runs out (timedOut is then true).
    private IEnumerator WaitOrTimeout(Func<bool> done, float seconds, Action<bool> timedOut)
    {
        float start = Time.realtimeSinceStartup;
        while (!done())
        {
            if (Time.realtimeSinceStartup - start > seconds)
            {
                timedOut?.Invoke(true);
                yield break;
            }
            yield return null;
        }
        timedOut?.Invoke(false);
    }

    private void CancelStuckRequest(string what)
    {
        Debug.LogError($"{what} got no answer within {requestTimeoutSeconds:0} s - cancelling it.");
        try { llmCharacter.CancelRequests(); }
        catch (Exception ex) { Debug.LogWarning($"Could not cancel the request: {ex.Message}"); }
        NoteFailedRequest(what + " timed out");
    }

    private void OnDestroy()
    {
        if (webCamTexture != null && webCamTexture.isPlaying) webCamTexture.Stop();
    }

    // Update is called once per frame
    private bool timeoutWarned;

    void Update()
    {
        // How long the current question has been waiting; starts again with every question.
        if (!waitingForReply)
        {
            timer = 0f;
            timeoutWarned = false;
            return;
        }

        timer += Time.deltaTime;
        if (timer >= timeout && !timeoutWarned)
        {
            Debug.LogWarning($"LLM is taking a while ({timeout:0.#} s and counting)...");
            timeoutWarned = true;
        }

        // Last resort, whatever the question is stuck on: give up, so the assistant is not left silent and muted.
        if (timer >= requestTimeoutSeconds + 30f)
        {
            Debug.LogError($"The question has had no answer for {timer:0} s - giving up on it.");
            try { llmCharacter.CancelRequests(); }
            catch (Exception ex) { Debug.LogWarning($"Could not cancel the stuck request: {ex.Message}"); }
            NoteFailedRequest("no answer");
            ProcessLLMResponse(null);
        }
    }

    private void WarmUpCallback()
    {
        isConnected = true;
        if (statusIndicator != null) statusIndicator.color = ServerStatusColors.Connected;
    }

    [ContextMenu("Test LLM")]
    private void TestLLM()
    {
        QuestionAboutCube(inputForLLM);
    }

    [ContextMenu("Test VLM")]
    private async void TestVLM()
    {
        byte[] jpg = imageForVLM.EncodeToJPG(90);
        string response = await llmCharacter.ChatWithImageBytes(
            inputForLLM,
            jpg,
            "image/jpeg"
        );
        ProcessLLMResponse(response);
    }

    public void ReTry()
    {
        QuestionAboutCube(latestQuestion);
    }


    public void QuestionAboutCubeWithVision(string inputString)
    {
        StartCoroutine(CaptureSceneCoroutine(inputString));
    }

    private IEnumerator CaptureSceneCoroutine(string inputString)
    {
        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes);

        SendSceneToVLM(inputString, jpg);
    }

    /// <summary>
    /// Captures a single frame as JPEG bytes, either from a real webcam
    /// (when useRealCamera is enabled) or from a render of the in-scene camera.
    /// </summary>
    private IEnumerator CaptureImage(Action<byte[]> onCaptured, bool plainBackground = false)
    {
        if (useRealCamera)
        {
            if (webCamTexture == null)
            {
                webCamTexture = string.IsNullOrEmpty(webCamDeviceName)
                    ? new WebCamTexture()
                    : new WebCamTexture(webCamDeviceName);
            }
            if (!webCamTexture.isPlaying) webCamTexture.Play();

            // Wait until the device has produced a real frame (width starts at 16 while initializing).
            while (webCamTexture.width <= 16) yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D frame = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGB24, false);
            frame.SetPixels32(webCamTexture.GetPixels32());
            frame.Apply();

            Texture2D scaled = DownscaleForVLM(frame, vlmImageMaxDimension);
            onCaptured?.Invoke(PublishCapture(scaled.EncodeToJPG(85)));
            if (scaled != frame) Destroy(frame);
            Destroy(scaled);
        }
        else
        {
            yield return new WaitForEndOfFrame();

            Texture2D shot = RenderCubeCloseUp(Camera.main, plainBackground);
            Texture2D scaled = DownscaleForVLM(shot, vlmImageMaxDimension);
            onCaptured?.Invoke(PublishCapture(scaled.EncodeToJPG(85)));
            if (scaled != shot) Destroy(shot);
            Destroy(scaled);
        }
    }

    [Header("Render Capture")]
    [Tooltip("Capture a square, tightly framed close-up of the cube instead of the whole window. The window shot " +
        "left the cube a few percent of the image (and the left third black), so every sticker was a handful of " +
        "pixels - far too little for a small vision model to read.")]
    public bool frameCubeForCapture = true;
    [Tooltip("1 = the cube's bounding sphere just touches the image edge; larger leaves more empty margin.")]
    [Range(1f, 1.6f)] public float captureMargin = 1.05f;
    [Tooltip("Lift the ambient light for the capture only, so faces turned away from the light don't read as dark " +
        "brown (orange looked like that) - dark, muddy colors are what small vision models confuse most.")]
    public bool brightenForCapture = true;
    [Range(0f, 1f)] public float captureAmbient = 0.55f;

    private RubiksCubeRenderer cubeRendererRef;

    // Bounding sphere of the visible cube, independent of how it is currently rotated.
    private bool TryGetCubeSphere(out Vector3 center, out float radius)
    {
        center = Vector3.zero;
        radius = 0f;

        if (cubeRendererRef == null) cubeRendererRef = FindFirstObjectByType<RubiksCubeRenderer>();
        Transform root = cubeRendererRef != null ? cubeRendererRef.CubeTransform : null;
        if (root == null) return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return false;

        Bounds union = renderers[0].bounds;
        foreach (Renderer r in renderers) union.Encapsulate(r.bounds);
        center = union.center;

        foreach (Renderer r in renderers)
        {
            Vector3 scale = r.transform.lossyScale;
            Vector3 e = r.localBounds.extents;
            float extent = new Vector3(e.x * Mathf.Abs(scale.x), e.y * Mathf.Abs(scale.y), e.z * Mathf.Abs(scale.z)).magnitude;
            radius = Mathf.Max(radius, Vector3.Distance(center, r.bounds.center) + extent);
        }
        return radius > 0.001f;
    }

    /// <summary>
    /// Renders the cube from the main camera's current viewpoint into a square, anti-aliased texture with the cube
    /// filling the frame. The camera is aimed at the cube and zoomed for this one render and restored right
    /// after, so nothing changes on screen. Falls back to the whole window if the cube can't be located.
    /// </summary>
    private Texture2D RenderCubeCloseUp(Camera cam, bool plainBackground = false)
    {
        Vector3 center = Vector3.zero;
        float radius = 0f;
        bool framed = frameCubeForCapture && TryGetCubeSphere(out center, out radius);

        int size = vlmImageMaxDimension > 0 ? vlmImageMaxDimension : 768;
        int width = framed ? size : Screen.width;
        int height = framed ? size : Screen.height;

        RenderTexture rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };

        // Everything touched below is restored in the finally block.
        Rect oldRect = cam.rect;
        float oldFov = cam.fieldOfView;
        Quaternion oldRotation = cam.transform.rotation;
        RenderTexture oldTarget = cam.targetTexture;
        var oldAmbientMode = RenderSettings.ambientMode;
        Color oldAmbient = RenderSettings.ambientLight;
        CameraClearFlags oldClear = cam.clearFlags;
        Color oldBackground = cam.backgroundColor;
        CubeBackdrop backdrop = plainBackground ? CubeBackdrop.Instance : null;
        bool backdropWasVisible = backdrop != null && backdrop.Visible;

        try
        {
            if (framed)
            {
                Vector3 toCube = center - cam.transform.position;
                float distance = Mathf.Max(0.01f, toCube.magnitude);
                cam.rect = new Rect(0f, 0f, 1f, 1f);
                cam.transform.rotation = Quaternion.LookRotation(toCube, cam.transform.up);
                cam.fieldOfView = Mathf.Clamp(
                    2f * Mathf.Atan(radius * captureMargin / distance) * Mathf.Rad2Deg, 5f, 120f);
            }

            // The classical reader wants the cube on a plain black background: no backdrop, nothing else drawn.
            if (plainBackground)
            {
                if (backdropWasVisible) backdrop.Visible = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }

            if (brightenForCapture)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.white * captureAmbient;
            }

            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            return tex;
        }
        finally
        {
            cam.targetTexture = oldTarget;
            cam.clearFlags = oldClear;
            cam.backgroundColor = oldBackground;
            if (backdropWasVisible) backdrop.Visible = true;
            cam.rect = oldRect;
            cam.fieldOfView = oldFov;
            cam.transform.rotation = oldRotation;
            RenderSettings.ambientMode = oldAmbientMode;
            RenderSettings.ambientLight = oldAmbient;
            RenderTexture.active = null;
            Destroy(rt);
        }
    }

    // Hands the captured JPEG to the debug overlay so the box there always shows what the VLM was given.
    private byte[] PublishCapture(byte[] jpg)
    {
        if (debugOverlay == null)
        {
            debugOverlay = FindFirstObjectByType<CubeDebugOverlay>();
            debugOverlay?.AttachMemory(vlmMemory);
        }
        debugOverlay?.SetVlmImage(jpg, vlmMemory.Look.classical ? "Camera image" : "Sent to VLM");
        return jpg;
    }

    /// <summary>
    /// Downscales a texture (GPU-side, via Graphics.Blit) so neither dimension exceeds maxDimension,
    /// preserving aspect ratio. Returns the same instance unchanged if it's already small enough.
    /// </summary>
    private Texture2D DownscaleForVLM(Texture2D source, int maxDimension)
    {
        int width = source.width;
        int height = source.height;
        if (maxDimension <= 0 || Mathf.Max(width, height) <= maxDimension)
        {
            return source;
        }

        float scale = (float)maxDimension / Mathf.Max(width, height);
        int newWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        int newHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));

        RenderTexture rt = RenderTexture.GetTemporary(newWidth, newHeight, 0);
        RenderTexture previousActive = RenderTexture.active;
        Graphics.Blit(source, rt);
        RenderTexture.active = rt;

        Texture2D result = new Texture2D(newWidth, newHeight, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
        result.Apply();

        RenderTexture.active = previousActive;
        RenderTexture.ReleaseTemporary(rt);

        return result;
    }

    private async void SendSceneToVLM(string inputString, byte[] jpg)
    {
        Debug.Log("Asking VLM with scene capture: " + inputString);
        if (llmCharacter == null) return;

        latestQuestion = inputString;
        waitingForReply = true;
        startTime = DateTime.Now;

        // No direct pass of RubiksCubeGame's ground-truth state - the image itself, sent directly
        // to the VLM below, is the only source of cube information here.
        string fullQuestion = "the user question: \"" + inputString + "\"";

        string response = await llmCharacter.ChatWithImageBytes(fullQuestion, jpg, "image/jpeg");
        ProcessLLMResponse(response);
    }

    public async void Question(string inputString)
    {
        Debug.Log("Asking LLM (normal): " + inputString);
        if (llmCharacter == null) return;

        latestQuestion = inputString;

        waitingForReply = true;
        startTime = DateTime.Now;

        if (lookInProgress)
        {
            try { llmCharacter.CancelRequests(); }
            catch (Exception ex) { Debug.LogWarning($"Could not cancel the regular look: {ex.Message}"); }
            while (lookInProgress) await Task.Yield();
        }

        string response = await llmCharacter.Chat(inputString, null, ReplyCompleted);
        ProcessLLMResponse(response);
    }

    /// <summary>
    /// Phrases that route a question to SolveCube() (VLM facelet read + solver server) instead of the
    /// plain conversational path. Keeps SolveCube's cost (multiple VLM calls) limited to when the user
    /// actually seems to want a solve, per the on-demand-only design - not every single question.
    /// </summary>
    private static readonly string[] SolveKeywords =
    {
        "solve", "solution", "next move", "what move", "how do i finish", "help me finish",
        "fix this", "figure this out", "what do i do next"
    };

    private static bool LooksLikeSolveRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string lower = text.ToLowerInvariant();
        return SolveKeywords.Any(keyword => lower.Contains(keyword));
    }

    public void QuestionAboutCube(string inputString)
    {
        if (LooksLikeSolveRequest(inputString))
        {
            Debug.Log("Routing to SolveCube (solve-related phrasing detected): " + inputString);
            StartCoroutine(SolveCubeCoroutine(inputString));
        }
        else
        {
            StartCoroutine(QuestionAboutCubeCoroutine(inputString));
        }
    }

    private IEnumerator QuestionAboutCubeCoroutine(string inputString)
    {
        Debug.Log("Asking LLM (cube): " + inputString);
        if (llmCharacter == null) yield break;

        latestQuestion = inputString;
        waitingForReply = true;
        startTime = DateTime.Now;

        yield return WaitForRegularLook();

        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes);

        string visualDescription = null;
        bool vlmDone = false;

        AskVLMAsync(jpg, result =>
        {
            visualDescription = result;
            vlmDone = true;
        });

        bool vlmTimedOut = false;
        yield return WaitOrTimeout(() => vlmDone, requestTimeoutSeconds, t => vlmTimedOut = t);
        if (vlmTimedOut)
        {
            CancelStuckRequest("The scene description");
            ProcessLLMResponse(null); // says "could you repeat that" and frees the microphone
            yield break;
        }

        Debug.Log("VLM Scene Description: " + visualDescription);

        // Deliberately no direct pass of RubiksCubeGame's ground-truth state here - the answer
        // must be grounded entirely in what the VLM actually perceived from the render, not a
        // peek at the internal cube data structure.
        string fullQuestion = "the user question: \"" + inputString + "\""
            + "\n\nVisual description of the current scene:\n" + visualDescription
            + DescribeCameraView();

        Debug.Log("Asking LLM Full Question: " + fullQuestion);

        string finalResponse = null;
        bool llmDone = false;

        AskLLMAsync(fullQuestion, result =>
        {
            finalResponse = result;
            llmDone = true;
        });

        bool llmTimedOut = false;
        yield return WaitOrTimeout(() => llmDone, requestTimeoutSeconds, t => llmTimedOut = t);
        if (llmTimedOut) { CancelStuckRequest("The answer"); finalResponse = null; }

        Debug.Log("LLM Response: " + finalResponse);
        ProcessLLMResponse(finalResponse);
    }

    /// <summary>
    /// System prompt used for image-only sub-calls (visual description, facelet reading) instead of the
    /// full conversational one. The long cube-conversation system prompt is completely irrelevant to these
    /// single-purpose vision tasks, but was being resent and reprocessed on every one of these calls anyway -
    /// including 3+ times per SolveCube request via the facelet-voting loop. Prompt processing time scales
    /// directly with token count (confirmed via live testing earlier this session), so this is pure wasted
    /// latency avoided by swapping in a much shorter prompt just for the duration of these calls.
    /// </summary>
    private async Task<string> ChatWithImageUsingVisionPrompt(string instruction, byte[] jpg, int numPredictOverride = 0)
    {
        string previousPrompt = llmCharacter.systemPrompt;
        int previousNumPredict = llmCharacter.numPredict;
        llmCharacter.systemPrompt = visionOnlySystemPrompt;
        if (numPredictOverride > 0) llmCharacter.numPredict = numPredictOverride;
        try
        {
            // CreateMultimodalContent throws synchronously (before ChatContent's own try/catch even
            // starts) if jpg is null/empty, e.g. a failed capture - caught here so callers always get a
            // value instead of an unhandled exception escaping their async-void caller, which would leave
            // a coroutine's WaitUntil(vlmDone) waiting forever and waitingForReply stuck true.
            return await llmCharacter.ChatWithImageBytes(instruction, jpg, "image/jpeg", addToHistory: false);
        }
        catch (Exception ex)
        {
            Debug.LogError($"VLM call failed: {ex.Message}");
            return "";
        }
        finally
        {
            llmCharacter.systemPrompt = previousPrompt;
            llmCharacter.numPredict = previousNumPredict;
        }
    }

    private async void AskVLMAsync(byte[] jpg, Action<string> onComplete)
    {
        string response = await ChatWithImageUsingVisionPrompt(sceneDescriptionPrompt, jpg);
        if (string.IsNullOrEmpty(response)) NoteFailedRequest("empty vision reply");
        onComplete?.Invoke(ExtractResponseField(response));
    }

    /// <summary>
    /// Reads a named string field out of a JSON object, trying the whole text first and then a
    /// best-effort search for an embedded {...} object within it. This exists because the JSON
    /// grammar constraint on the LLM server is confirmed non-functional on this LlamaLib build
    /// (verified: sending the grammar - and separately, response_format:json_object - does not
    /// stop the model from returning plain markdown when it decides to ignore the system prompt's
    /// format instruction), so nothing here can assume the model's output is well-formed JSON.
    /// </summary>
    private static string TryGetJsonField(string text, string fieldName)
    {
        try
        {
            var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);
            if (json != null && json.TryGetValue(fieldName, out string value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch (Exception)
        {
            // Not valid JSON - caller decides how to fall back.
        }
        return null;
    }

    private static string ExtractEmbeddedJsonObject(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
    }

    private static string TryGetJsonFieldWithFallback(string text, string fieldName)
    {
        if (string.IsNullOrEmpty(text)) return null;

        string direct = TryGetJsonField(text, fieldName);
        if (direct != null) return direct;

        string embedded = ExtractEmbeddedJsonObject(text);
        return (embedded != null && embedded != text) ? TryGetJsonField(embedded, fieldName) : null;
    }

    /// <summary>
    /// Strips common markdown artifacts (headers, bold/italic markers, code fences, bullet points,
    /// emoji) from text before it's spoken via TTS - a last-resort cleanup for when the model
    /// abandons the JSON contract entirely and returns raw markdown instead.
    /// </summary>
    private static string StripMarkdownForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = text.Replace("`", "");
        text = Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = text.Replace("**", "").Replace("__", "");
        text = Regex.Replace(text, @"(?<!\w)\*(?!\s)([^*]+?)\*(?!\w)", "$1");
        text = Regex.Replace(text, @"(?<!\w)_(?!\s)([^_]+?)_(?!\w)", "$1");
        text = Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "");
        text = Regex.Replace(text, @"(?m)^\s*\d+\.\s+", "");
        text = Regex.Replace(text, @"[\uD83C-\uDBFF][\uDC00-\uDFFF]", ""); // surrogate-pair emoji
        text = Regex.Replace(text, @"\s*\n\s*", " ");
        text = Regex.Replace(text, @"\s{2,}", " ");

        return text.Trim();
    }

    private string ExtractResponseField(string response)
    {
        return TryGetJsonFieldWithFallback(response, "response") ?? response;
    }

    private const int MaxSpokenSentences = 3;
    private const int MaxSpokenWords = 50;

    /// <summary>
    /// Backstop against pathological rambling, applied after JSON/markdown extraction regardless of
    /// what the model produced - confirmed via live testing that the system prompt's length rule alone
    /// is not reliably followed by this model. Deliberately looser than the prompt's 1-2 sentence /
    /// 20-25 word target so it only clips real outliers instead of double-trimming ordinary replies
    /// (an earlier, tighter version of both the prompt and this cap made normal answers feel curt).
    /// </summary>
    private static string TruncateForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sentences = Regex.Matches(text, @"[^.!?]+[.!?]+(\s+|$)|[^.!?]+$");
        var kept = new List<string>();
        int wordCount = 0;
        foreach (Match m in sentences)
        {
            string sentence = m.Value.Trim();
            if (sentence.Length == 0) continue;
            kept.Add(sentence);
            wordCount += sentence.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (kept.Count >= MaxSpokenSentences || wordCount >= MaxSpokenWords) break;
        }

        string result = string.Join(" ", kept).Trim();
        return string.IsNullOrEmpty(result) ? text : result;
    }

    private async void AskLLMAsync(string fullQuestion, Action<string> onComplete)
    {
        string response = await llmCharacter.Chat(fullQuestion, null, ReplyCompleted);
        if (string.IsNullOrEmpty(response)) NoteFailedRequest("empty chat reply");
        onComplete?.Invoke(response);
    }

    /// <summary>
    /// Looks at the cube through the VLM - several independent reads of whichever faces are visible - and
    /// merges what it saw into vlmMemory (per-face trust from recency and stability). Once every face is
    /// trusted enough, the assembled state goes to the solver server and the solution is folded into the next
    /// LLM question. If a face is still unknown or has gone stale, the LLM is told which one(s), so it can ask
    /// the user to show them. Called automatically by QuestionAboutCube() when the phrasing looks solve-related
    /// (see LooksLikeSolveRequest) - can still be called directly too, e.g. from a dedicated "Solve" UI button.
    /// </summary>
    public void SolveCube(string inputString)
    {
        StartCoroutine(SolveCubeCoroutine(inputString));
    }

    private static string JoinFaceTitles(IEnumerable<int> faceIndices)
    {
        return string.Join(", ", faceIndices.Select(CubeVlmMemory.FaceTitle));
    }

    private IEnumerator SolveCubeCoroutine(string inputString)
    {
        Debug.Log("Asking LLM (solve): " + inputString);
        if (llmCharacter == null) yield break;

        latestQuestion = inputString;
        waitingForReply = true;
        startTime = DateTime.Now;

        yield return WaitForRegularLook();

        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes, vlmMemory.Look.classical);

        // Read the visible faces several times independently; each face is then voted on separately, so a
        // single bad read can't overwrite what the memory already knows.
        var reads = new List<Dictionary<int, string>>();
        // The classical reader is deterministic, so repeating it on the same picture would only repeat itself.
        int sampleCount = vlmMemory.Look.classical ? 1 : Mathf.Max(1, faceletSampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            Dictionary<int, string> read = null;
            bool readDone = false;
            ReadFacesAsync(jpg, result =>
            {
                read = result;
                readDone = true;
            });
            bool solveReadTimedOut = false;
            yield return WaitOrTimeout(() => readDone, requestTimeoutSeconds, t => solveReadTimedOut = t);
            if (solveReadTimedOut)
            {
                CancelStuckRequest("The face reading");
                reads.Add(null);
                break;
            }

            Debug.Log($"{ReaderName} face read {i + 1}/{sampleCount}: "
                + (read == null ? "unusable" : string.Join(" ", read.Select(kv => $"{CubeVlmMemory.FaceOrder[kv.Key]}={kv.Value}"))));
            reads.Add(ApplyReadChecks(read));
        }

        // Option: the language model writes the combined answer from all reads, in place of the majority vote.
        var displayReads = reads;
        var observeReads = reads;
        if (vlmMemory.Look.llmMerge && !vlmMemory.Look.classical && reads.Count(r => r != null) >= 2)
        {
            Dictionary<int, string> merged = null;
            bool mergeDone = false;
            MergeReadsWithLLM(reads, result => { merged = result; mergeDone = true; });
            bool mergeTimedOut = false;
            yield return WaitOrTimeout(() => mergeDone, requestTimeoutSeconds, t => mergeTimedOut = t);
            if (mergeTimedOut) { CancelStuckRequest("Merging the reads"); merged = null; }
            if (merged != null && merged.Count > 0)
            {
                observeReads = new List<Dictionary<int, string>> { merged };
                displayReads = new List<Dictionary<int, string>>(reads) { merged };
            }
        }

        float now = Time.time;
        CubeVlmMemory.Observation observation = vlmMemory.Observe(observeReads, now, minFaceletConfidence);
        ReportMemoryEvents(observation);
        Debug.Log($"{ReaderName} observation: {observation.ReadsUsable}/{reads.Count} reads usable, accepted faces "
            + $"[{string.Join(",", observation.Accepted.Select(f => CubeVlmMemory.FaceOrder[f]))}], unclear "
            + $"[{string.Join(",", observation.Unclear.Select(f => CubeVlmMemory.FaceOrder[f]))}]");

        // Show what the VLM saw (every read plus its memory) in the debug overlay's 2D view.
        if (debugOverlay == null)
        {
            debugOverlay = FindFirstObjectByType<CubeDebugOverlay>();
            debugOverlay?.AttachMemory(vlmMemory);
        }
        debugOverlay?.SetVlmObservation(ReadsForDisplay(displayReads), "Checking the views...");

        string vlmStatus;
        string solutionSection;
        string cubeState = "";
        string offlineReply = null; // what to say if the language model isn't available (classical reader only)
        Func<string, bool> replyCheck = null; // does what the model said fit this situation? (small models copy examples)

        List<int> needLook = vlmMemory.FacesNeedingLook(now);
        if (needLook.Count > 0)
        {
            // Completely uncertain about at least one face: don't guess, have the user show it.
            string names = JoinFaceTitles(needLook);
            vlmStatus = "Not solved yet - need a look at: " + names;
            Debug.LogWarning("VLM memory is too uncertain to solve; needs a look at: " + names);
            offlineReply = "I can't see these faces clearly yet: " + names + ". Please turn the cube so they are visible.";
            debugOverlay?.SetSolution(null, null, "Not solved yet - the camera needs a look at: " + names + ". Turn the cube so they face it.");
            replyCheck = ReplyAsksFor(needLook);
            solutionSection = "\n\nSuggested Solution: not available yet - the camera has not seen these cube faces clearly "
                + "enough (or not recently): " + names + ". Ask the user, in one short sentence, to turn the cube so "
                + "those faces are visible to the camera.";
        }
        else
        {
            string facelets = vlmMemory.ToFaceletString();
            string leastTrusted = JoinFaceTitles(vlmMemory.LeastTrusted(now, 2));

            if (!FaceletValidator.IsValid(facelets, out string validationError))
            {
                vlmStatus = $"The faces don't fit together ({validationError}). Least trusted: {leastTrusted}";
                Debug.LogWarning($"Assembled facelet state is not a legal cube: {validationError}");
                offlineReply = "The faces I see don't fit together as one cube. Please show again: " + leastTrusted + ".";
                debugOverlay?.SetSolution(null, null, $"The faces seen don't fit together as one cube ({validationError}). Show again: {leastTrusted}.");
                replyCheck = ReplyAsksFor(vlmMemory.LeastTrusted(now, 2));
                solutionSection = "\n\nSuggested Solution: the faces seen so far do not fit together as one cube, so no solution "
                    + "could be computed. Ask the user, in one short sentence, to show again: " + leastTrusted + ".";
            }
            else if (cubeSolverClient == null)
            {
                vlmStatus = "Solver is not configured.";
                offlineReply = "The solver is not configured.";
                debugOverlay?.SetSolution(null, null, "The solver is not configured.");
                replyCheck = reply => !SaysAMove(reply);
                Debug.LogWarning("SolveCube called but no CubeSolverClient is assigned.");
                solutionSection = "\n\nSuggested Solution: the solver is not configured.";
            }
            else
            {
                cubeState = "\n\nCube State (assembled from the camera views, "
                    + $"{vlmMemory.AverageTrust(now):P0} average trust):\n" + facelets;

                string solverResult = null;
                bool solverSuccess = false;

                // The solver package answers a cube that is already solved with a long move sequence that does nothing
                // (R L U2 R L' B2 ...), so a solved cube is recognised here and never sent.
                bool alreadySolved = facelets == CubeGroundTruth.SolvedState;
                if (alreadySolved)
                {
                    solverSuccess = true;
                    solverResult = "";
                }
                else
                {
                    yield return cubeSolverClient.Solve(facelets, (success, resultText) =>
                    {
                        solverSuccess = success;
                        solverResult = resultText;
                    });
                }

                Debug.Log($"Cube solver result - success: {solverSuccess}, result: {solverResult}" + (alreadySolved ? " (already solved)" : ""));

                if (solverSuccess && string.IsNullOrWhiteSpace(solverResult))
                {
                    vlmStatus = "The cube is already solved.";
                    offlineReply = "Your cube is already solved - there is nothing left to do!";
                    debugOverlay?.SetSolution(null, null, "The cube is already solved - there is nothing to do.");
                    replyCheck = reply => reply.ToLowerInvariant().Contains("solved");
                    solutionSection = "\n\nSuggested Solution: none needed - the cube is already solved. Tell the user, in one short "
                        + "sentence, that it is already solved.";
                }
                else
                {
                    // The cube in the scene is the mirror image of a standard cube, so the solver's moves turn the other way
                    // on screen; the moves shown and passed on are the ones to make on the cube as the user sees it. (A real
                    // cube in front of a real camera is a standard cube: its moves are the solver's as they are.)
                    string screenMoves = !solverSuccess ? "" : MirrorPicture ? CubeMoves.ToScreenNotation(solverResult) : solverResult;

                    vlmStatus = solverSuccess ? $"Solver: {solverResult} -> on screen: {screenMoves}" : $"Solver failed: {solverResult}";
                    string nextMoves = DescribeNextMoves(screenMoves);
                    int totalMoves = screenMoves.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    offlineReply = solverSuccess
                        ? (nextMoves.Length > 0 ? "Here is how to start. " + nextMoves + " The full solution is on the screen."
                                                : "The solution is on the screen.")
                        : "I couldn't compute a solution. Please show again: " + leastTrusted + ".";
                    if (solverSuccess) debugOverlay?.SetSolution(screenMoves, solverResult, null);
                    else debugOverlay?.SetSolution(null, null, $"The solver could not solve it ({solverResult}). Show again: {leastTrusted}.");
                    replyCheck = solverSuccess ? ReplyDescribesMove(screenMoves) : (reply => !SaysAMove(reply));

                    solutionSection = solverSuccess
                        ? $"\n\nSuggested Solution: the cube needs {totalMoves} moves in all (they are shown on the user's screen). "
                            + "The next moves, in words: " + nextMoves + " Say only these, in your own words, keeping each face and "
                            + "each direction exactly as written."
                        : $"\n\nSuggested Solution: failed to compute a solution ({solverResult}). Ask the user, in one short "
                            + "sentence, to show again: " + leastTrusted + ".";
                }
            }
        }

        debugOverlay?.SetVlmStatus(vlmStatus);

        // Classical reader and no language model running: answer straight from the result instead of waiting on a
        // server that isn't there - unless the language model is meant to write every reply (then it is tried first).
        if (!vlmMemory.Look.llmSpeaks && vlmMemory.Look.classical && !isConnected && offlineReply != null)
        {
            ProcessLLMResponse(new JObject { ["response"] = offlineReply }.ToString());
            yield break;
        }

        // Deliberately no direct pass of RubiksCubeGame's ground-truth state here - the solve must be
        // grounded entirely in what the VLM actually saw of the render, assembled face by face above.
        string fullQuestion = "the user question: \"" + inputString + "\""
            + "\nother necessary information:" + cubeState
            + solutionSection
            + DescribeCameraView();

        Debug.Log("Asking LLM Full Question (solve): " + fullQuestion);

        string finalResponse = null;
        bool llmDone = false;

        AskLLMAsync(fullQuestion, result =>
        {
            finalResponse = result;
            llmDone = true;
        });

        // A server that is known to be down is given a short time only; one that is up gets the full time.
        bool solveAnswerTimedOut = false;
        float answerWait = isConnected ? requestTimeoutSeconds : Mathf.Min(10f, requestTimeoutSeconds);
        yield return WaitOrTimeout(() => llmDone, answerWait, t => solveAnswerTimedOut = t);
        if (solveAnswerTimedOut) { CancelStuckRequest("The answer"); finalResponse = null; }

        // A small model sometimes answers with the example from its instructions instead of the situation, or with
        // broken JSON. A reply that does not fit what is really the case is replaced by the sentence made in code.
        // (Not when the language model is meant to write every reply: then what it says is spoken as it is.)
        if (!vlmMemory.Look.llmSpeaks && !string.IsNullOrWhiteSpace(finalResponse) && replyCheck != null && offlineReply != null)
        {
            string said = TryGetJsonFieldWithFallback(finalResponse, "response") ?? SalvageResponseText(finalResponse) ?? "";
            if (!replyCheck(said))
            {
                Debug.LogWarning($"The model's reply does not fit the situation: '{said}' - speaking the fixed sentence instead.");
                finalResponse = null;
            }
        }

        // No answer from the model: the sentence made in code is better than "could you repeat that" (not with llmSpeaks).
        if (!vlmMemory.Look.llmSpeaks && string.IsNullOrWhiteSpace(finalResponse) && offlineReply != null)
        {
            Debug.LogWarning("The language model gave no answer; speaking the fixed reply instead.");
            finalResponse = new JObject { ["response"] = offlineReply }.ToString();
        }

        ProcessLLMResponse(finalResponse);
    }

    private string ReaderName => vlmMemory.Look.classical ? "Classical" : "VLM";

    // ------------------------------------------------------------------------------------------
    // Checking what the model said against what is really the case (see SolveCubeCoroutine).
    // ------------------------------------------------------------------------------------------

    private static bool SaysAMove(string reply)
    {
        string l = reply.ToLowerInvariant();
        return l.Contains("clockwise") || l.Contains("half turn") || l.Contains("quarter turn") || l.Contains("180");
    }

    // The reply names at least one of the faces it should ask to see - and does not tell a move.
    private static Func<string, bool> ReplyAsksFor(IEnumerable<int> faces)
    {
        List<string> colors = faces.Select(f => CubeVlmMemory.ColorName(CubeVlmMemory.FaceOrder[f])).ToList();
        return reply =>
        {
            string l = reply.ToLowerInvariant();
            return !SaysAMove(l) && colors.Any(l.Contains);
        };
    }

    // The reply names the face of the first move and the way it turns.
    private static Func<string, bool> ReplyDescribesMove(string screenMoves)
    {
        string first = screenMoves.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first) || CubeVlmMemory.FaceOrder.IndexOf(first[0]) < 0) return null;
        string color = CubeVlmMemory.ColorName(first[0]);
        return reply =>
        {
            string l = reply.ToLowerInvariant();
            if (!l.Contains(color)) return false;
            if (first.EndsWith("2")) return l.Contains("half") || l.Contains("180") || l.Contains("twice") || l.Contains("two");
            if (first.EndsWith("'")) return l.Contains("counter") || l.Contains("anti");
            return l.Contains("clockwise");
        };
    }

    // The text of a reply that started as {"response": "..." but was cut off or broken afterwards.
    private static string SalvageResponseText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        Match m = Regex.Match(raw, "\"response\"\\s*:\\s*\"(?<t>(?:[^\"\\\\]|\\\\.)*)");
        if (!m.Success) return null;
        string text = m.Groups["t"].Value;
        try { text = Regex.Unescape(text); } catch (ArgumentException) { /* keep it as it is */ }
        text = text.Trim();
        return text.Length > 0 ? text : null;
    }

    // ------------------------------------------------------------------------------------------
    // The direction the cube is turned: which faces point at the camera, so the assistant can name a move by what the user
    // sees ("the green-centered face in front of you") instead of by a letter. It says which faces are towards the camera,
    // not what is on them.
    // ------------------------------------------------------------------------------------------

    private RubiksCubeGame gameRef;

    private string DescribeCameraView()
    {
        // With a real camera the user holds a real cube: the cube in the scene says nothing about how that one is turned.
        if (useRealCamera) return "";
        if (gameRef == null) gameRef = FindFirstObjectByType<RubiksCubeGame>();
        if (gameRef == null || Camera.main == null) return "";

        List<RubiksCubeGame.FaceTowardsCamera> faces = gameRef.FacesTowardsCamera(Camera.main, 0.2f);
        if (faces == null || faces.Count == 0) return "";

        var parts = new List<string>();
        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            string name = CubeVlmMemory.ColorName(CubeVlmMemory.FaceOrder[face.Face]) + "-centered face";
            string where;
            if (i == 0) where = face.Facing >= 0.85f ? "points straight at the camera" : "is the one turned most towards the camera";
            else
            {
                string side = Mathf.Abs(face.Offset.y) > Mathf.Abs(face.Offset.x)
                    ? (face.Offset.y > 0 ? "on top" : "at the bottom")
                    : (face.Offset.x > 0 ? "on the right" : "on the left");
                where = side + (face.Facing < 0.5f ? " (seen at a steep angle)" : "");
            }
            parts.Add($"the {name} {where}");
        }
        return "\n\nCamera view: " + string.Join("; ", parts) + ". The other faces point away from the camera.";
    }

    // The next moves of a solution in plain words from where the user is looking (made in code, see MoveSpeech), so the
    // language model - or the text to speech - never has to read "R' U2" and guess.
    private string DescribeNextMoves(string screenMoves, int count = 2)
    {
        if (string.IsNullOrWhiteSpace(screenMoves)) return "";
        if (gameRef == null) gameRef = FindFirstObjectByType<RubiksCubeGame>();

        // No view (real camera, or no cube in the scene): the moves are said without positions.
        List<MoveSpeech.ViewFace> view = null;
        if (!useRealCamera && gameRef != null && Camera.main != null)
        {
            view = gameRef.FacesTowardsCamera(Camera.main, 0.2f)?.Select(f => new MoveSpeech.ViewFace
            {
                Face = f.Face, Facing = f.Facing, OffsetX = f.Offset.x, OffsetY = f.Offset.y,
            }).ToList();
        }
        return MoveSpeech.Describe(screenMoves.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), count, view);
    }

    // Tells the log and the debug view when the memory forgot faces because a move was detected, or dropped faces
    // that contradicted the rest.
    private void ReportMemoryEvents(CubeVlmMemory.Observation observation)
    {
        if (observation.MoveDetected)
        {
            Debug.Log(observation.MoveNote);
            debugOverlay?.AppendVlmStatus(observation.MoveNote);
            debugOverlay?.MarkSolutionStale();
        }
        if (observation.Dropped.Count > 0)
        {
            Debug.LogWarning("Memory consistency: " + observation.ConflictNote);
            debugOverlay?.AppendVlmStatus("Dropped " + observation.ConflictNote);
        }
    }

    /// <summary>"Classical" or "VLM": the reader that is switched on.</summary>
    public string ActiveReader => ReaderName;

    // ------------------------------------------------------------------------------------------
    // Answer checks: a read that breaks the rules of a real cube (see ReadChecks) is wrong somewhere, so the faces
    // involved are dropped before they can reach the memory.
    // ------------------------------------------------------------------------------------------

    [Tooltip("Drop the faces of a read that break the rules of a real cube (impossible edge or corner pieces, " +
        "a color more than 9 times, a wrong center) before they reach the memory.")]
    public bool enableReadChecks = true;

    /// <summary>How many reads were checked, how many failed, and how many faces were dropped for it.</summary>
    public int ReadsChecked { get; private set; }
    public int ReadsFailedChecks { get; private set; }
    public int FacesDroppedByChecks { get; private set; }

    private Dictionary<int, string> ApplyReadChecks(Dictionary<int, string> read)
    {
        if (!enableReadChecks || read == null) return read;
        ReadsChecked++;

        Dictionary<int, string> kept = ReadChecks.Filter(read, out ReadChecks.Report report);
        if (report.Ok) return read;

        ReadsFailedChecks++;
        FacesDroppedByChecks += read.Count - (kept?.Count ?? 0);
        string dropped = string.Join(",", report.SuspectFaces.Select(f => CubeVlmMemory.FaceOrder[f]));
        Debug.LogWarning($"{ReaderName} read failed the checks ({report}); dropped faces [{dropped}]");
        debugOverlay?.AppendVlmStatus($"Checks dropped {dropped}: {report}");
        return kept;
    }

    // ------------------------------------------------------------------------------------------
    // Benchmark support: one complete look - capture, read, checks - reported back with its timing.
    // ------------------------------------------------------------------------------------------

    public class LookSample
    {
        public byte[] Picture;                          // what was captured (before any mirroring)
        public Dictionary<int, string> Raw;             // what the reader returned (null = nothing usable)
        public Dictionary<int, string> Checked;         // after the answer checks
        public ReadChecks.Report Report;
        public float Seconds;
        public bool TimedOut;
    }

    /// <summary>The look settings (reader, format, move estimator ...) the app is using right now.</summary>
    public VlmLookSettings LookSettings => vlmMemory.Look;

    public class SolveLookResult
    {
        public byte[] Picture;
        public CubeVlmMemory.Observation Observation;
        public int ReadsUsable;
        public float Seconds;
        public bool TimedOut;
    }

    /// <summary>
    /// One look as a solve makes it - capture, the reads of the active reader (several for the VLM, one for the classical
    /// reader), the answer checks - merged into the given memory exactly as SolveCube merges them into the app's own.
    /// </summary>
    public IEnumerator BenchmarkSolveLook(CubeVlmMemory memory, float now, Action<SolveLookResult> done, float timeoutSeconds = 180f)
    {
        var result = new SolveLookResult();
        float started = Time.realtimeSinceStartup;

        yield return WaitForRegularLook();

        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes, vlmMemory.Look.classical);
        result.Picture = jpg;

        var reads = new List<Dictionary<int, string>>();
        int sampleCount = vlmMemory.Look.classical ? 1 : Mathf.Max(1, faceletSampleCount);
        for (int i = 0; i < sampleCount && jpg != null; i++)
        {
            Dictionary<int, string> read = null;
            bool finished = false;
            ReadFacesAsync(jpg, r => { read = r; finished = true; });
            while (!finished && Time.realtimeSinceStartup - started < timeoutSeconds) yield return null;
            if (!finished) { result.TimedOut = true; break; }
            reads.Add(ApplyReadChecks(read));
        }

        result.ReadsUsable = reads.Count(r => r != null);
        result.Observation = memory.Observe(reads, now, minFaceletConfidence);
        result.Seconds = Time.realtimeSinceStartup - started;
        done?.Invoke(result);
    }

    /// <summary>One way of asking the VLM: JSON or plain lines, color letters or full words.</summary>
    public class FormatVariant
    {
        public string Name;
        public bool Plain, Words;
    }

    /// <summary>The four ways the VLM can be asked, for comparing them on the same pictures.</summary>
    public static readonly FormatVariant[] AllFormats =
    {
        new FormatVariant { Name = "JSON, letters", Plain = false, Words = false },
        new FormatVariant { Name = "JSON, words", Plain = false, Words = true },
        new FormatVariant { Name = "plain, letters", Plain = true, Words = false },
        new FormatVariant { Name = "plain, words", Plain = true, Words = true },
    };

    /// <summary>"JSON, letters" / "plain, words" ...: how the VLM is asked right now (the classical reader has no format).</summary>
    public string ActiveFormat => vlmMemory.Look.classical ? "-"
        : (vlmMemory.Look.plainFormat ? "plain" : "JSON") + ", " + (vlmMemory.Look.colorWords ? "words" : "letters");

    /// <summary>
    /// One picture, read once per variant (null variants = the current settings, read once). The settings are put back
    /// afterwards. The samples come back in the order of the variants, with the picture that was used.
    /// </summary>
    public IEnumerator BenchmarkLookVariants(IList<FormatVariant> variants, Action<byte[], List<LookSample>> done,
        float timeoutSeconds = 120f)
    {
        var samples = new List<LookSample>();
        float started = Time.realtimeSinceStartup;

        yield return WaitForRegularLook();

        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes, vlmMemory.Look.classical);
        float captureSeconds = Time.realtimeSinceStartup - started;

        bool plainBefore = vlmMemory.Look.plainFormat, wordsBefore = vlmMemory.Look.colorWords;
        try
        {
            int count = variants == null || variants.Count == 0 ? 1 : variants.Count;
            for (int v = 0; v < count; v++)
            {
                if (variants != null && variants.Count > 0)
                {
                    vlmMemory.Look.plainFormat = variants[v].Plain;
                    vlmMemory.Look.colorWords = variants[v].Words;
                }

                var sample = new LookSample { Picture = jpg };
                float readStarted = Time.realtimeSinceStartup;
                if (jpg != null)
                {
                    Dictionary<int, string> read = null;
                    bool finished = false;
                    ReadFacesAsync(jpg, result => { read = result; finished = true; });
                    while (!finished && Time.realtimeSinceStartup - readStarted < timeoutSeconds) yield return null;
                    sample.TimedOut = !finished;
                    sample.Raw = finished ? read : null;
                }
                sample.Report = ReadChecks.Check(sample.Raw);
                sample.Checked = ReadChecks.Filter(sample.Raw, out _);
                sample.Seconds = captureSeconds + (Time.realtimeSinceStartup - readStarted);
                samples.Add(sample);
            }
        }
        finally
        {
            vlmMemory.Look.plainFormat = plainBefore;
            vlmMemory.Look.colorWords = wordsBefore;
        }
        done?.Invoke(jpg, samples);
    }

    /// <summary>Makes one look with the active reader and hands the outcome to done.</summary>
    public IEnumerator BenchmarkLook(Action<LookSample> done, float timeoutSeconds = 120f)
    {
        var sample = new LookSample();
        float started = Time.realtimeSinceStartup;

        yield return WaitForRegularLook();

        byte[] jpg = null;
        yield return CaptureImage(bytes => jpg = bytes, vlmMemory.Look.classical);
        sample.Picture = jpg;

        if (jpg != null)
        {
            Dictionary<int, string> read = null;
            bool finished = false;
            ReadFacesAsync(jpg, result => { read = result; finished = true; });
            while (!finished && Time.realtimeSinceStartup - started < timeoutSeconds) yield return null;
            sample.TimedOut = !finished;
            sample.Raw = finished ? read : null;
        }

        sample.Report = ReadChecks.Check(sample.Raw);
        sample.Checked = ReadChecks.Filter(sample.Raw, out _);
        sample.Seconds = Time.realtimeSinceStartup - started;
        done?.Invoke(sample);
    }

    // What the classical reader returned in its latest read, ready for the 2D view: every face it read, including
    // ones it could not orient (shown as seen, rotation unknown) - those are NOT fed into the memory.
    private string classicalDisplay;
    private string classicalNote = "";

    // The reads to show in the 2D view's read tabs: the raw classical result, or the VLM's reads.
    private List<string> ReadsForDisplay(List<Dictionary<int, string>> reads)
    {
        if (vlmMemory.Look.classical) return new List<string> { classicalDisplay };
        return reads.Select(r => r == null ? null : CubeVlmMemory.Flatten(r)).ToList();
    }

    /// <summary>Reads the faces from a picture with whichever reader is switched on (VLM or classical).</summary>
    private void ReadFacesAsync(byte[] jpg, Action<Dictionary<int, string>> onComplete)
    {
        if (vlmMemory.Look.classical) onComplete?.Invoke(ReadFacesClassical(jpg));
        else AskVLMForFacesAsync(jpg, onComplete);
    }

    // Same contract as the VLM path: standard-orientation faces by face index, null when nothing usable was found.
    private Dictionary<int, string> ReadFacesClassical(byte[] jpg)
    {
        Texture2D image = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (jpg == null || !image.LoadImage(jpg, false)) return null;
            Color32[] pixels = image.GetPixels32();
            var rgb = new byte[pixels.Length * 3];
            // Texture rows run bottom-up; the reader (like the VLM) wants the picture top-down.
            for (int y = 0; y < image.height; y++)
            {
                int src = (image.height - 1 - y) * image.width;
                for (int x = 0; x < image.width; x++)
                {
                    Color32 c = pixels[src + x];
                    int o = (y * image.width + (MirrorPicture ? image.width - 1 - x : x)) * 3;
                    rgb[o] = c.r; rgb[o + 1] = c.g; rgb[o + 2] = c.b;
                }
            }

            SaveClassicalCapture(jpg);
            ClassicalCubeReader.Result found = ClassicalCubeReader.Read(rgb, image.width, image.height);
            Debug.Log("Classical cube reader: " + found.Report);
            PublishClassicalView(image, found);

            var result = new Dictionary<int, string>();
            var unoriented = new Dictionary<int, string>();
            foreach (CubeVlmMemory.RawFace raw in found.Faces)
            {
                if (CubeVlmMemory.TryOrient(raw, out int faceIndex, out string standard9, out string error))
                {
                    if (!result.ContainsKey(faceIndex)) result[faceIndex] = standard9;
                }
                else
                {
                    Debug.LogWarning("Classical face skipped: " + error);
                    char centerFace = CubeVlmMemory.FaceFromColor(raw.center);
                    int index = centerFace == '\0' ? -1 : CubeVlmMemory.FaceOrder.IndexOf(centerFace);
                    if (index >= 0 && raw.stickers != null && raw.stickers.Length == 9)
                    {
                        var faceLetters = new char[9];
                        for (int k = 0; k < 9; k++)
                        {
                            char f = CubeVlmMemory.FaceFromColor(raw.stickers[k].ToString());
                            faceLetters[k] = f == '\0' ? '?' : f;
                        }
                        unoriented[index] = new string(faceLetters);
                    }
                }
            }

            // For the 2D view: oriented faces plus the ones that could only be read without a known rotation.
            var display = new Dictionary<int, string>(result);
            foreach (var kv in unoriented) if (!display.ContainsKey(kv.Key)) display[kv.Key] = kv.Value;
            classicalDisplay = CubeVlmMemory.Flatten(display);
            classicalNote = unoriented.Count > 0
                ? "Rotation unknown (needs a neighbouring face in view): "
                    + string.Join(", ", unoriented.Keys.Where(k => !result.ContainsKey(k)).Select(CubeVlmMemory.FaceTitle))
                : "";
            if (result.Count == 0) return null;

            if (!CubeVlmMemory.IsPlausibleView(result.Keys.ToList(), out string implausible))
            {
                Debug.LogWarning("Classical face read rejected: " + implausible);
                return null;
            }
            return result;
        }
        finally
        {
            Destroy(image);
        }
    }

    [Tooltip("The cube in the scene is the mirror image of a standard cube (its colors sit on the faces the other way " +
        "round, so around a corner they run counter-clockwise where a real cube's run clockwise). Both face readers " +
        "(VLM and classical) therefore get the picture flipped left-to-right, which turns it into a standard cube. " +
        "Not applied to a real camera.")]
    public bool mirrorRenderedCube = true;

    // Only the scene's own render is mirrored; a real camera looking at a real cube is not.
    private bool MirrorPicture => mirrorRenderedCube && !useRealCamera;

    // The same picture flipped left-to-right, as a JPEG.
    private static byte[] MirrorJpg(byte[] jpg)
    {
        Texture2D image = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            if (!image.LoadImage(jpg, false)) return jpg;
            int w = image.width, h = image.height;
            Color32[] px = image.GetPixels32();
            for (int y = 0; y < h; y++) Array.Reverse(px, y * w, w);
            image.SetPixels32(px);
            image.Apply();
            return image.EncodeToJPG(90);
        }
        finally
        {
            Destroy(image);
        }
    }

    [Tooltip("Writes the picture the classical reader gets to DebugCaptures/classical_last.jpg next to Assets, so a " +
        "misread can be reproduced and tuned offline.")]
    public bool saveClassicalCapture = true;

    private void SaveClassicalCapture(byte[] jpg)
    {
        if (!saveClassicalCapture || jpg == null) return;
        try
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DebugCaptures"));
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "classical_last.jpg"), jpg);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Could not save the classical capture: " + ex.Message);
        }
    }

    // Replaces the picture in the debug box with what the classical reader made of it: the cube outline it found
    // (white), each sticker candidate as a dot in the color it was classified as, ringed green when it became part
    // of a read face and red when it was left out.
    private void PublishClassicalView(Texture2D image, ClassicalCubeReader.Result found)
    {
        if (debugOverlay == null)
        {
            debugOverlay = FindFirstObjectByType<CubeDebugOverlay>();
            debugOverlay?.AttachMemory(vlmMemory);
        }
        if (debugOverlay == null) return;

        int w = image.width, h = image.height;
        Color32[] px = image.GetPixels32();

        void Plot(int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            if (MirrorPicture) x = w - 1 - x; // the reader saw the picture flipped left-to-right
            px[(h - 1 - y) * w + x] = c; // reader coordinates are top-down, texture rows bottom-up
        }
        void Line(float x0, float y0, float x1, float y1, Color32 c)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0))));
            for (int i = 0; i <= steps; i++)
                Plot(Mathf.RoundToInt(Mathf.Lerp(x0, x1, i / (float)steps)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, i / (float)steps)), c);
        }
        Color32 LetterColor(char letter)
        {
            switch (letter)
            {
                case 'W': return new Color32(255, 255, 255, 255);
                case 'Y': return new Color32(255, 230, 0, 255);
                case 'R': return new Color32(230, 20, 20, 255);
                case 'O': return new Color32(255, 130, 0, 255);
                case 'G': return new Color32(20, 200, 40, 255);
                case 'B': return new Color32(30, 80, 240, 255);
                default: return new Color32(128, 128, 128, 255);
            }
        }

        var white = new Color32(255, 255, 255, 255);
        for (int i = 0; i < found.Silhouette.Count; i++)
        {
            float[] a = found.Silhouette[i], b = found.Silhouette[(i + 1) % found.Silhouette.Count];
            Line(a[0], a[1], b[0], b[1], white);
        }
        int radius = Mathf.Max(3, w / 90);
        foreach (ClassicalCubeReader.Mark m in found.Marks)
        {
            Color32 ring = m.InFace ? new Color32(0, 255, 0, 255) : new Color32(255, 0, 0, 255);
            for (int dy = -radius - 2; dy <= radius + 2; dy++)
                for (int dx = -radius - 2; dx <= radius + 2; dx++)
                {
                    int edge = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                    if (edge > radius + 2) continue;
                    if (edge <= radius - 1) Plot(Mathf.RoundToInt(m.X) + dx, Mathf.RoundToInt(m.Y) + dy, LetterColor(m.Letter));
                    else if (edge >= radius + 1) Plot(Mathf.RoundToInt(m.X) + dx, Mathf.RoundToInt(m.Y) + dy, ring);
                }
        }

        var annotated = new Texture2D(w, h, TextureFormat.RGB24, false);
        annotated.SetPixels32(px);
        annotated.Apply();
        debugOverlay.SetVlmImage(annotated.EncodeToJPG(85), "Classical reader saw");
        Destroy(annotated);

        debugOverlay.SetVlmStatus("Classical: " + found.Report);
    }

    // ------------------------------------------------------------------------------------------
    // Option: let the language model combine several VLM reads into one answer (instead of the majority vote in code).
    // The model's answer is held to what the reads actually said: a sticker letter that none of the reads had at that
    // position is replaced by the reads' majority, faces that no read had are dropped and faces it left out are filled in.
    // ------------------------------------------------------------------------------------------

    private async Task<string> ChatTextWithSystemPrompt(string systemPrompt, string instruction, int numPredictOverride)
    {
        string previousPrompt = llmCharacter.systemPrompt;
        int previousNumPredict = llmCharacter.numPredict;
        llmCharacter.systemPrompt = systemPrompt;
        if (numPredictOverride > 0) llmCharacter.numPredict = numPredictOverride;
        try
        {
            return await llmCharacter.Chat(instruction, null, null, false);
        }
        catch (Exception ex)
        {
            Debug.LogError($"LLM merge call failed: {ex.Message}");
            return "";
        }
        finally
        {
            llmCharacter.systemPrompt = previousPrompt;
            llmCharacter.numPredict = previousNumPredict;
        }
    }

    // The code's own answer: per sticker the letter most reads agree on (ties go to the earlier read).
    private static Dictionary<int, string> MajorityOfReads(List<Dictionary<int, string>> reads)
    {
        var merged = new Dictionary<int, string>();
        for (int f = 0; f < 6; f++)
        {
            var seenIn = reads.Where(r => r != null && r.ContainsKey(f)).ToList();
            if (seenIn.Count == 0) continue;
            var letters = new char[9];
            for (int k = 0; k < 9; k++)
                letters[k] = seenIn.GroupBy(r => r[f][k]).OrderByDescending(g => g.Count()).First().Key;
            merged[f] = new string(letters);
        }
        return merged;
    }

    private async void MergeReadsWithLLM(List<Dictionary<int, string>> reads, Action<Dictionary<int, string>> onComplete)
    {
        var usable = reads.Where(r => r != null).ToList();
        Dictionary<int, string> vote = MajorityOfReads(usable);

        var readingLines = new System.Text.StringBuilder();
        for (int i = 0; i < usable.Count; i++)
        {
            readingLines.Append($"Reading {i + 1}: ");
            readingLines.AppendLine(string.Join(" ", usable[i].OrderBy(kv => kv.Key)
                .Select(kv => $"{CubeVlmMemory.FaceOrder[kv.Key]}={kv.Value}")));
        }
        string instruction = mergeReadsPrompt.Replace("{READINGS}", readingLines.ToString().TrimEnd());

        string response = await ChatTextWithSystemPrompt(mergeSystemPrompt, instruction, faceReadNumPredict);
        JObject root = string.IsNullOrEmpty(response) ? null : ParseJsonObject(response);
        JToken facesToken = root?["faces"] ?? root;

        var merged = new Dictionary<int, string>();
        int replaced = 0;
        if (facesToken is JObject faces)
        {
            foreach (var property in faces.Properties())
            {
                int f = property.Name.Length == 1 ? CubeVlmMemory.FaceOrder.IndexOf(char.ToUpperInvariant(property.Name[0])) : -1;
                string letters = property.Value?.ToString();
                if (f < 0 || !vote.ContainsKey(f) || letters == null) continue;
                letters = new string(letters.Where(ch => CubeVlmMemory.FaceOrder.IndexOf(char.ToUpperInvariant(ch)) >= 0)
                    .Select(char.ToUpperInvariant).ToArray());
                if (letters.Length != 9) continue;

                var fixedLetters = letters.ToCharArray();
                for (int k = 0; k < 9; k++)
                {
                    bool offered = usable.Any(r => r.ContainsKey(f) && r[f][k] == fixedLetters[k]);
                    if (!offered) { fixedLetters[k] = vote[f][k]; replaced++; }
                }
                merged[f] = new string(fixedLetters);
            }
        }
        else
        {
            Debug.LogWarning("LLM merge gave no usable JSON, using the majority vote: " + response);
        }

        foreach (var kv in vote) if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;

        int differs = 0;
        foreach (var kv in merged) for (int k = 0; k < 9; k++) if (kv.Value[k] != vote[kv.Key][k]) differs++;
        Debug.Log($"LLM merge of {usable.Count} reads: {differs} stickers differ from the majority vote, {replaced} invented letters replaced");
        onComplete?.Invoke(merged);
    }

    private async void AskVLMForFacesAsync(byte[] jpg, Action<Dictionary<int, string>> onComplete)
    {
        // The scene's cube is a mirror image of a standard cube (see mirrorRenderedCube); show the model the
        // flipped picture so what it reads is a standard cube.
        byte[] seen = MirrorPicture ? MirrorJpg(jpg) : jpg;
        if (MirrorPicture) debugOverlay?.SetVlmImage(seen, "Sent to VLM (mirrored)"); // show exactly what the model gets
        bool plain = vlmMemory.Look.plainFormat;
        bool words = vlmMemory.Look.colorWords;
        string instruction = plain ? (words ? faceReadingPlainWordsPrompt : faceReadingPlainPrompt)
                                   : (words ? faceReadingWordsPrompt : faceReadingPrompt);
        string response = await ChatWithImageUsingVisionPrompt(instruction, seen, faceReadNumPredict);
        if (string.IsNullOrEmpty(response)) NoteFailedRequest("empty vision reply");
        onComplete?.Invoke(plain ? ParseFaceReadingPlain(response) : ParseFaceReading(response));
    }

    private static JObject ParseJsonObject(string text)
    {
        try { return JObject.Parse(text.Trim()); }
        catch (Exception) { /* try the embedded-object search below */ }

        string embedded = ExtractEmbeddedJsonObject(text);
        if (embedded == null) return null;
        try { return JObject.Parse(embedded); }
        catch (Exception) { return null; }
    }

    // "stickers" may come back as one 9-letter string, a list of 9 letters, or three row strings - join them all.
    private static string StickersToString(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JArray array) return string.Join(" ", array.Select(element => element.ToString()));
        return token.ToString();
    }

    private static string TokenToString(JToken token)
    {
        return token == null || token.Type == JTokenType.Null ? null : token.ToString();
    }

    /// <summary>
    /// Turns the VLM's JSON ({"faces": [{center, up, stickers}, ...]}) into standard-orientation faces keyed by
    /// face index (see CubeVlmMemory.FaceOrder). Faces that don't parse or are internally inconsistent are
    /// skipped individually; null means nothing usable came back at all.
    /// </summary>
    private static Dictionary<int, string> ParseFaceReading(string response)
    {
        if (string.IsNullOrEmpty(response)) return null;

        JArray faceArray = ExtractFaceArray(response);
        if (faceArray == null)
        {
            Debug.LogWarning("VLM face read had no usable JSON 'faces' list: " + response);
            return null;
        }

        var raws = new List<CubeVlmMemory.RawFace>();
        foreach (JToken faceToken in faceArray)
        {
            try
            {
                raws.Add(new CubeVlmMemory.RawFace
                {
                    center = TokenToString(faceToken["center"]),
                    up = TokenToString(faceToken["up"]),
                    stickers = StickersToString(faceToken["stickers"]),
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("VLM face skipped: not a face object (" + ex.Message + ")");
            }
        }
        return OrientFaces(raws);
    }

    // The list of faces out of the model's answer. Small models often leave out the {"faces": ...} wrapper: they answer with a
    // bare list of face objects, or with one face object, usually inside a ```json fence. All of these are accepted.
    private static JArray ExtractFaceArray(string text)
    {
        JObject root = ParseJsonObject(text);
        if (root?["faces"] is JArray faces) return faces;

        int open = text.IndexOf('['), close = text.LastIndexOf(']');
        if (open >= 0 && close > open)
        {
            try
            {
                JArray list = JArray.Parse(text.Substring(open, close - open + 1));
                if (list.Count > 0 && list[0] is JObject) return list;
            }
            catch (Exception) { /* not a list: try a single face below */ }
        }

        return root != null && root["center"] != null ? new JArray(root) : null;
    }

    private static readonly System.Text.RegularExpressions.Regex PlainCenter =
        new System.Text.RegularExpressions.Regex(@"center\s*[=:]\s*""?([A-Za-z ]+?)""?\s*[,;]?\s*(?=\bup\s*[=:]|stickers?\s*[=:]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex PlainUp =
        new System.Text.RegularExpressions.Regex(@"\bup\s*[=:]\s*""?([A-Za-z\- ]+?)""?\s*[,;]?\s*(?=stickers?\s*[=:]|center\s*[=:]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex PlainStickers =
        new System.Text.RegularExpressions.Regex(@"stickers?\s*[=:]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The color letters in a stickers field: runs like "WWR" or "W W R" or names like "white", up to the first word that
    // is neither (so trailing remarks don't add letters).
    private static string StickerLetters(string field)
    {
        var letters = new System.Text.StringBuilder();
        foreach (string token in System.Text.RegularExpressions.Regex.Split(field, @"[^A-Za-z]+"))
        {
            if (token.Length == 0) continue;
            string upper = token.ToUpperInvariant();
            if (upper == "DARK" || upper == "LIGHT" || upper == "BRIGHT" || upper == "PALE" || upper == "DEEP") continue;
            if (token.Length > 1 && CubeVlmMemory.TryColorWord(token, out char face))
                letters.Append(CubeVlmMemory.ColorLetterOfFace(face));
            else if (upper.All(ch => "WYROGB".IndexOf(ch) >= 0)) letters.Append(upper);
            else break;
        }
        return letters.ToString();
    }

    /// <summary>
    /// The plain-letters answer: one line per face, "face center=W up=B stickers=WWRWWGYWR". Lines that do not carry
    /// a center and nine sticker letters are skipped; everything after that is the same as for the JSON answer.
    /// </summary>
    private static Dictionary<int, string> ParseFaceReadingPlain(string response)
    {
        if (string.IsNullOrEmpty(response)) return null;

        var raws = new List<CubeVlmMemory.RawFace>();
        foreach (string line in response.Split('\n'))
        {
            var center = PlainCenter.Match(line);
            var stickersMatch = PlainStickers.Match(line);
            if (!center.Success || !stickersMatch.Success) continue;

            string stickers = StickerLetters(stickersMatch.Groups[1].Value);
            if (stickers.Length != 9)
            {
                Debug.LogWarning($"VLM face skipped: {stickers.Length} sticker letters in '{line.Trim()}'");
                continue;
            }

            string up = PlainUp.Match(line).Success ? PlainUp.Match(line).Groups[1].Value : null;
            if (up != null && (up.Equals("none", StringComparison.OrdinalIgnoreCase) || up.Equals("null", StringComparison.OrdinalIgnoreCase)
                || up == "-" || up.Equals("no", StringComparison.OrdinalIgnoreCase) || up.Equals("na", StringComparison.OrdinalIgnoreCase)))
                up = null;

            raws.Add(new CubeVlmMemory.RawFace { center = center.Groups[1].Value, up = up, stickers = stickers });
        }

        if (raws.Count == 0)
        {
            Debug.LogWarning("VLM face read had no usable 'face center=.. stickers=..' line: " + response);
            return null;
        }
        return OrientFaces(raws);
    }

    // Turns raw face readings (center, up neighbour, stickers) into standard-orientation faces keyed by face index.
    private static Dictionary<int, string> OrientFaces(List<CubeVlmMemory.RawFace> raws)
    {
        var result = new Dictionary<int, string>();
        foreach (CubeVlmMemory.RawFace raw in raws)
        {
            if (CubeVlmMemory.TryOrient(raw, out int faceIndex, out string standard9, out string error))
            {
                if (!result.ContainsKey(faceIndex)) result[faceIndex] = standard9;
            }
            else
            {
                Debug.LogWarning("VLM face skipped: " + error);
            }
        }

        if (result.Count == 0) return null;

        // A camera sees at most 3 faces, never opposite ones. A read that claims more has guessed hidden faces,
        // so none of it is trusted.
        if (!CubeVlmMemory.IsPlausibleView(result.Keys.ToList(), out string implausible))
        {
            Debug.LogWarning("VLM face read rejected: " + implausible);
            return null;
        }

        return result;
    }

    public void ProcessLLMResponse(string response)
    {
        // LLMAgent.Chat()/ChatContent() swallow exceptions internally and skip their completionCallback
        // on failure (confirmed by reading the LLMUnity plugin source), so waitingForReply must be reset
        // here unconditionally rather than relying on that callback - otherwise a single dropped request
        // leaves it stuck true forever, which permanently mutes the mic (see ConversationHandler's
        // ListeningRoutine) until the whole app is restarted.
        waitingForReply = false;

        var latency = DateTime.Now - startTime;
        Debug.Log($"LLM Latency: {latency.TotalSeconds:0.0} s.");

        string responseText = TryGetJsonFieldWithFallback(response, "response") ?? SalvageResponseText(response);
        string spokenText;
        if (responseText != null)
        {
            Debug.Log($"Parsed Response - Text: {responseText}");
            spokenText = TruncateForSpeech(responseText);
        }
        else if (!string.IsNullOrEmpty(response))
        {
            // The model abandoned the JSON contract entirely (confirmed: the grammar constraint that's
            // supposed to prevent this doesn't actually work on this LlamaLib build). Speak a cleaned-up
            // version rather than reading markdown syntax/emoji out loud verbatim.
            Debug.LogWarning("Response has no usable JSON 'response' field. Speaking markdown-stripped raw text.");
            spokenText = TruncateForSpeech(StripMarkdownForSpeech(response));
            if (!vlmMemory.Look.llmSpeaks && (spokenText == null || spokenText.Count(char.IsLetter) < 3))
                spokenText = "Sorry, could you repeat that?";
        }
        else
        {
            spokenText = "Sorry, could you repeat that?";
        }

        // Show the same text that actually gets spoken/truncated, not the raw untruncated response -
        // otherwise the caption box displays more than what's read aloud, looking like playback cut off
        // partway through.
        llmResponseText.text = spokenText;
        HandlePatientReply(spokenText);

        TrimHistory();
    }

    /// <summary>
    /// Keeps conversation history bounded to maxHistoryMessages so per-turn latency doesn't keep
    /// growing across a session (generation cost scales with total context length, confirmed via
    /// live testing: latency climbed from ~1s to ~5s over just 6 turns with unbounded history).
    /// </summary>
    private void TrimHistory()
    {
        if (llmCharacter == null || maxHistoryMessages < 0) return;

        var history = llmCharacter.chat;
        if (history.Count > maxHistoryMessages)
        {
            llmCharacter.chat = history.Skip(history.Count - maxHistoryMessages).ToList();
        }
    }


    void HandlePatientReply(string reply)
    {
        if (ttsHandler == null) return;

        //if (reply == LatestResponse) return;
        //LatestResponse = reply;

        Action<string, string> updateUIAndSpeak = (text, language) =>
        {
            if (subtitle != null)
            {
                subtitle.text = text; // Update subtitle text field
            }
            Debug.Log($"Input for Patient TTS: {text}");
            ttsHandler.Speak(text, language);
        };

        ShowSubtitle();
        updateUIAndSpeak(reply, "English");
        
    }

    public void ShowSubtitle()
    {
        if (subtitle != null)
        {
            subtitle.gameObject.SetActive(true); // Ensure the GameObject is active

            // Reset alpha to fully opaque
            Color subtitleColor = subtitle.color;
            subtitleColor.a = 1f; // 1 is fully opaque
            subtitle.color = subtitleColor;

            // If there's an old fade coroutine running, stop it
            if (_activeFadeCoroutine != null)
            {
                StopCoroutine(_activeFadeCoroutine);
            }

            // Start the new fade coroutine
            //_activeFadeCoroutine = StartCoroutine(FadeOutSubtitleAfterDelay());
        }
    }

    /// <summary>
    /// Public method to be called from another script to initiate the subtitle fade-out.
    /// </summary>
    public void TriggerSubtitleFadeOut()
    {
        // Check if the subtitle is active, has text, and is not already fading or fully transparent
        if (subtitle != null && subtitle.gameObject.activeInHierarchy && !string.IsNullOrEmpty(subtitle.text) && subtitle.color.a > 0)
        {
            if (_activeFadeCoroutine != null)
            {
                StopCoroutine(_activeFadeCoroutine); // Stop any existing fade if this is called again
            }
            _activeFadeCoroutine = StartCoroutine(FadeOutEffect());
        }
        else
        {
            if (subtitle != null && subtitle.color.a <= 0)
            {
                Debug.Log("Subtitle is already faded out or transparent. Fade-out trigger ignored.");
            }
            else
            {
                Debug.LogWarning("Cannot trigger subtitle fade-out: Subtitle is not active, has no text, or reference is missing.");
            }
        }
    }

    private IEnumerator FadeOutEffect()
    {
        // Fade out the subtitle
        float currentTime = 0f;
        Color startColor = subtitle.color; // Current color (should be opaque or partially faded if re-triggered)

        while (currentTime < fadeOutDuration)
        {
            currentTime += Time.deltaTime;
            // Calculate the new alpha value by linearly interpolating from current alpha to 0 (transparent)
            float alpha = Mathf.Lerp(startColor.a, 0f, currentTime / fadeOutDuration);

            subtitle.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null; // Wait for the next frame before continuing the loop
        }

        // Ensure alpha is exactly 0 at the end
        Color finalColor = subtitle.color;
        finalColor.a = 0f;
        subtitle.color = finalColor;

        // Optional: Deactivate the subtitle GameObject after it has faded out
        // subtitle.gameObject.SetActive(false);

        _activeFadeCoroutine = null; // Clear the reference to the completed coroutine
    }


    private IEnumerator FadeOutSubtitleAfterDelay()
    {
        // 1. Wait for the initial display time (3 seconds)
        yield return new WaitForSeconds(displayTime);

        // 2. Fade out the subtitle
        float currentTime = 0f;
        Color startColor = subtitle.color; // Should be fully opaque here

        while (currentTime < fadeOutDuration)
        {
            currentTime += Time.deltaTime;
            // Calculate the new alpha value by linearly interpolating from 1 (opaque) to 0 (transparent)
            float alpha = Mathf.Lerp(startColor.a, 0f, currentTime / fadeOutDuration);

            subtitle.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null; // Wait for the next frame before continuing the loop
        }

        // Ensure alpha is exactly 0 at the end
        subtitle.color = new Color(startColor.r, startColor.g, startColor.b, 0f);

        // Optional: Deactivate the subtitle GameObject after it has faded out
        subtitle.gameObject.SetActive(false);

        _activeFadeCoroutine = null; // Clear the reference to the completed coroutine
    }


    IEnumerator ExecuteCommandCoroutine(string command)
    {
        yield return null; // Ensures it does not block immediately
        ExecuteCommand(command);
    }

    public void ExecuteCommand(string command)
    {


    }


    void ReplyCompleted()
    {
        waitingForReply = false;
    }

    public void CancelReply()
    {
        deliberateCancelUntil = Time.realtimeSinceStartup + 5f;
        try
        {
            llmCharacter.CancelRequests();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error cancelling LLM Handler reply: {ex.Message}");
        }
       
        waitingForReply = false;
    }
}
