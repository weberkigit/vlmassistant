using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class RubiksCubeGame : MonoBehaviour
{
    [SerializeField] private RubiksCubeRenderer cubeRenderer;
    [SerializeField] private Transform cubeRoot;
    [SerializeField] private Animator cubeAnimator; // drag the mesh child here
    [SerializeField] private CubeDebugOverlay debugOverlay;

    private RubiksCube cube;
    private bool isAnimating = false;

    // Every quarter turn the cube can do: animation clip, and the logical move (axis 0/1/2 = X/Y/Z, layer 0/2 =
    // min/max side, cw) that is applied to the cube data after the clip has played. The clip names and cw values
    // are what the keyboard shortcuts have always used (F and B are the ones with cw reversed).
    private static readonly (string clip, int axis, int layer, bool cw)[] MoveTable =
    {
        ("R", 0, 2, true),  ("R_prime", 0, 2, false),
        ("L", 0, 0, true),  ("L_prime", 0, 0, false),
        ("U", 1, 2, true),  ("U_prime", 1, 2, false),
        ("D", 1, 0, true),  ("D_prime", 1, 0, false),
        ("F", 2, 2, false), ("F_prime", 2, 2, true),
        ("B", 2, 0, false), ("B_prime", 2, 0, true),
    };

    private void Start()
    {
        cube = new RubiksCube();
        cubeRenderer.Initialize(cube);
        StartCoroutine(CalibrateTurns());
    }

    // ------------------------------------------------------------------------------------------
    // Right mouse button: rotate the whole cube (view only, the puzzle state is untouched).
    // ------------------------------------------------------------------------------------------
    [Header("Mouse")]
    [Tooltip("Degrees of cube rotation per pixel of right-mouse drag.")]
    [SerializeField] private float mouseRotateSpeed = 0.35f;
    [Tooltip("How far (in pixels) a left-mouse drag must travel before it turns a layer.")]
    [SerializeField] private float layerDragThreshold = 10f;

    [Tooltip("Left-drag speed (pixels per second) at which the turn animation plays at normal speed. Dragging faster " +
        "plays it faster, slower plays it slower, so the turn feels like it follows the hand.")]
    [SerializeField] private float dragSpeedReference = 700f;
    [SerializeField] private float minTurnSpeed = 0.8f;
    [SerializeField] private float maxTurnSpeed = 3.5f;

    /// <summary>Draws a small square where the game thinks a left click landed (toggled in Debug Tools).</summary>
    public static bool ShowPickMarker { get; set; } = false;

    [Header("Turn Particles")]
    [Tooltip("Scales how many sparks a turning layer throws off (1 = default).")]
    [SerializeField, Range(0.2f, 3f)] private float particleAmount = 1f;

    /// <summary>Throws sparks off the turning layer, scaled with the turn speed (toggled in Debug Tools).</summary>
    public static bool ParticlesEnabled { get; set; } = false;

    private CubeTurnParticles turnParticles;
    private bool turnPlaying; // true from the start of a turn animation until it ends

    private int[] FindTurnSlots(string clipName)
    {
        foreach (ClipTurn turn in clipTurns)
            if (turn.clip == clipName) return turn.slots;
        return null;
    }

    // World position of the centre of each given sticker, from the vertices SkinWorldVertices just produced.
    private Vector3[] StickerWorldPositions(int[] slots)
    {
        var positions = new Vector3[slots.Length];
        for (int k = 0; k < slots.Length; k++)
        {
            int[] indices = submeshIndices[slots[k]];
            Vector3 sum = Vector3.zero;
            foreach (int i in indices) sum += worldVertices[i];
            positions[k] = indices.Length > 0 ? sum / indices.Length : Vector3.zero;
        }
        return positions;
    }

    // Emits sparks from the stickers that are turning, in their own colors, flying along the way they travel.
    // Everything scales with the turn speed: faster turns throw more, faster, bigger sparks.
    private IEnumerator EmitParticlesDuringTurn(int[] slots, float speed)
    {
        SkinnedMeshRenderer smr = cubeRenderer != null ? cubeRenderer.Skinned : null;
        if (!ParticlesEnabled || slots == null || slots.Length == 0 || smr == null) yield break;
        if (turnParticles == null) turnParticles = new CubeTurnParticles(transform);

        float unit = cubeHalf * LocalToWorldUnits; // roughly half the cube's width in world units
        float intensity = Mathf.Clamp(speed, 0.5f, 4f) * particleAmount;
        Vector3[] previous = null;
        Vector3[] latest = null;

        while (turnPlaying)
        {
            if (SkinWorldVertices(smr))
            {
                latest = StickerWorldPositions(slots);
                float dt = Mathf.Max(Time.deltaTime, 0.001f);
                int count = Mathf.Max(1, Mathf.RoundToInt(slots.Length * 0.3f * intensity));
                for (int n = 0; n < count; n++)
                {
                    int k = Random.Range(0, slots.Length);
                    Vector3 travel = previous != null ? (latest[k] - previous[k]) / dt : Vector3.zero;
                    Vector3 velocity = travel * 0.2f + Random.onUnitSphere * unit * (0.35f + 0.25f * intensity);
                    Color color = Color.Lerp(cubeRenderer.GetStickerColor(slots[k]), Color.white, 0.25f);
                    turnParticles.Emit(latest[k] + Random.insideUnitSphere * unit * 0.03f, velocity, color,
                        unit * Random.Range(0.035f, 0.07f) * (0.8f + 0.2f * intensity), Random.Range(0.3f, 0.6f));
                }
                previous = latest;
            }
            yield return null;
        }

        // A burst as the layer snaps into place.
        if (latest != null)
        {
            int burst = Mathf.RoundToInt(slots.Length * 0.9f * intensity);
            for (int n = 0; n < burst; n++)
            {
                int k = Random.Range(0, slots.Length);
                Vector3 outward = (latest[k] - cubeRoot.position).normalized;
                Vector3 velocity = (outward + Random.insideUnitSphere * 0.7f) * unit * (0.6f + 0.35f * intensity);
                Color color = Color.Lerp(cubeRenderer.GetStickerColor(slots[k]), Color.white, 0.35f);
                turnParticles.Emit(latest[k], velocity, color, unit * Random.Range(0.045f, 0.09f), Random.Range(0.35f, 0.7f));
            }
        }
    }

    [Header("Turn Sound")]
    [Tooltip("Volume of the whoosh and click that play when a layer turns (from keys or the mouse).")]
    [SerializeField, Range(0f, 1f)] private float turnSoundVolume = 0.5f;

    /// <summary>Plays a whoosh while a layer turns and a click when it snaps into place (toggled in Debug Tools).</summary>
    public static bool TurnSoundEnabled { get; set; } = true;

    private AudioSource whooshSource, clickSource;
    private AudioClip whooshClip, clickClip;

    private void EnsureTurnSounds()
    {
        if (clickClip != null) return;
        whooshClip = CubeTurnSound.CreateWhoosh();
        clickClip = CubeTurnSound.CreateClick();
        // Separate sources so the whoosh's pitch (it follows the turn speed) never bends the click.
        whooshSource = gameObject.AddComponent<AudioSource>();
        clickSource = gameObject.AddComponent<AudioSource>();
        foreach (AudioSource source in new[] { whooshSource, clickSource })
        {
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }
    }

    private void PlayTurnWhoosh(float speed)
    {
        if (!TurnSoundEnabled) return;
        EnsureTurnSounds();
        whooshSource.pitch = Mathf.Clamp(speed, 0.8f, 2f); // a faster turn also sounds faster
        whooshSource.PlayOneShot(whooshClip, turnSoundVolume);
    }

    private void PlayTurnClick()
    {
        if (!TurnSoundEnabled) return;
        EnsureTurnSounds();
        clickSource.PlayOneShot(clickClip, turnSoundVolume);
    }

    /// <summary>Lightens the grabbed face while dragging and the layer that is turning (toggled in Debug Tools).</summary>
    public static bool HighlightDraggedFace { get; set; } = false;

    private bool isDraggingView = false;

    private void HandleViewDrag()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || cubeRoot == null) return;

        if (mouse.rightButton.wasPressedThisFrame)
        {
            isDraggingView = IsOverCube(mouse.position.ReadValue());
        }
        else if (!mouse.rightButton.isPressed)
        {
            isDraggingView = false;
        }

        if (!isDraggingView) return;

        Vector2 delta = mouse.delta.ReadValue();
        if (delta == Vector2.zero) return;

        // Rotate around the camera's own axes so dragging always moves the cube the way the pointer moves,
        // regardless of how the view was previously rotated.
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        Vector3 up = cam != null ? cam.up : Vector3.up;
        Vector3 right = cam != null ? cam.right : Vector3.right;

        cubeRoot.Rotate(up, -delta.x * mouseRotateSpeed, Space.World);
        cubeRoot.Rotate(right, delta.y * mouseRotateSpeed, Space.World);
    }

    /// <summary>
    /// True if the screen position is inside the cube's camera viewport and not over a UI element
    /// (Canvas UI or the OnGUI conversation buttons).
    /// </summary>
    private bool IsOverCube(Vector2 screenPos)
    {
        Camera cam = Camera.main;
        if (cam != null && !cam.pixelRect.Contains(screenPos)) return false;

        if (UnityEngine.EventSystems.EventSystem.current != null &&
            UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()) return false;

        // The OnGUI controls (buttons, menu, 2D view, benchmark) are not part of the cube view.
        return !DebugUi.IsPointerOver(screenPos);
    }

    // ------------------------------------------------------------------------------------------
    // Left mouse button: grab a sticker and drag to turn its layer, like a real cube.
    //
    // Positions are worked out the way Unity itself draws a skinned mesh: every vertex is blended from the bones
    // that move it (bone matrix x bind pose x vertex), which gives its exact world position. That avoids the
    // renderer's own transform and BakeMesh's coordinate conventions - for a skinned mesh the bones, not the
    // renderer's transform, decide where it appears on screen, and mixing the two put picks in the wrong place.
    //
    // The turn animations are measured once at startup instead of assumed: each clip is played to its end, the
    // sticker positions are compared with the rest pose, and that says around which axis, on which side and in
    // which direction the clip turns the cube. A drag is then translated into the clip that turns the grabbed
    // layer the way the pointer is moving. All of this is done in the cube root's local space, so rotating the
    // whole cube with the right mouse button does not matter. Only the six outer layers can turn.
    // ------------------------------------------------------------------------------------------
    private struct ClipTurn
    {
        public string clip;
        public int axis, layer;
        public bool cw;
        public int axisIndex; // 0/1/2 = X/Y/Z of the cube root's local space
        public int side;      // +1 / -1: which end of that axis the turning layer is on
        public int sigma;     // +1 / -1: Quaternion.AngleAxis(sigma * 90, axis) is the way it turns
        public int[] slots;   // the sticker slots that move when this clip plays
    }

    private readonly List<ClipTurn> clipTurns = new List<ClipTurn>();
    private Vector3 cubeCenter;   // centre of the 54 sticker positions, in the cube root's local space
    private float cubeHalf;       // distance from that centre to the outer stickers along an axis (local units)

    // Cached mesh data for skinning (read once).
    private Mesh skinMesh;
    private Vector3[] bindVertices;
    private Matrix4x4[] bindPoses;
    private Matrix4x4[] boneMatrices;
    private Vector3[] worldVertices;
    private int[][] submeshIndices; // triangle indices per submesh; submesh 0 is the cube body, 1-54 the stickers

    private bool layerDragActive, layerDragDone, layerTurnStarted;
    private float layerDragSpeed;   // smoothed pointer speed in pixels per second while dragging
    private int layerHitSlot;       // sticker slot the drag grabbed
    private Vector2 layerDragStart;
    private Vector3 layerHitLocal, layerHitNormalLocal; // in the cube root's local space

    private static Vector3 Axis(int index) => index == 0 ? Vector3.right : (index == 1 ? Vector3.up : Vector3.forward);

    private float LocalToWorldUnits => cubeRoot != null ? Mathf.Max(0.0001f, cubeRoot.lossyScale.x) : 1f;

    // World position of every vertex of the cube mesh as it is posed right now.
    private bool SkinWorldVertices(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.sharedMesh == null) return false;

        if (skinMesh != smr.sharedMesh)
        {
            skinMesh = smr.sharedMesh;
            bindVertices = skinMesh.vertices;
            bindPoses = skinMesh.bindposes;
            worldVertices = new Vector3[bindVertices.Length];
            submeshIndices = new int[skinMesh.subMeshCount][];
            for (int s = 0; s < skinMesh.subMeshCount; s++) submeshIndices[s] = skinMesh.GetTriangles(s);
        }
        if (bindVertices.Length == 0) return false;

        Transform[] bones = smr.bones;
        if (boneMatrices == null || boneMatrices.Length != bones.Length) boneMatrices = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            boneMatrices[i] = bones[i] != null && i < bindPoses.Length
                ? bones[i].localToWorldMatrix * bindPoses[i]
                : smr.transform.localToWorldMatrix;
        }

        NativeArray<byte> bonesPerVertex = skinMesh.GetBonesPerVertex();
        if (bonesPerVertex.Length == 0)
        {
            // Not actually skinned: the renderer's transform positions it.
            Matrix4x4 m = smr.transform.localToWorldMatrix;
            for (int v = 0; v < bindVertices.Length; v++) worldVertices[v] = m.MultiplyPoint3x4(bindVertices[v]);
            return true;
        }

        NativeArray<BoneWeight1> weights = skinMesh.GetAllBoneWeights();
        int w = 0;
        for (int v = 0; v < bindVertices.Length; v++)
        {
            Vector3 position = Vector3.zero;
            int count = bonesPerVertex[v];
            for (int k = 0; k < count; k++, w++)
            {
                BoneWeight1 bw = weights[w];
                position += bw.weight * boneMatrices[bw.boneIndex].MultiplyPoint3x4(bindVertices[v]);
            }
            worldVertices[v] = position;
        }
        return true;
    }

    // Centre of every sticker (material slot 1-54; slot 0 is the cube body) in the cube root's local space, from
    // the vertices SkinWorldVertices just produced.
    private Vector3[] StickerCentroids()
    {
        if (submeshIndices == null || submeshIndices.Length < 55) return null;

        var centroids = new Vector3[55];
        for (int slot = 1; slot <= 54; slot++)
        {
            int[] indices = submeshIndices[slot];
            if (indices.Length == 0) return null;
            Vector3 sum = Vector3.zero;
            foreach (int i in indices) sum += worldVertices[i];
            centroids[slot] = cubeRoot.InverseTransformPoint(sum / indices.Length);
        }
        return centroids;
    }

    private IEnumerator CalibrateTurns()
    {
        yield return null; // let every Start() run first

        SkinnedMeshRenderer smr = cubeRenderer != null ? cubeRenderer.Skinned : null;
        if (smr == null || cubeAnimator == null || cubeRoot == null)
        {
            Debug.LogWarning("Layer dragging unavailable: the cube's skinned mesh, animator or root is not assigned.");
            yield break;
        }

        isAnimating = true; // keep input out while the clips are being sampled

        if (!SkinWorldVertices(smr))
        {
            Debug.LogWarning("Layer dragging unavailable: could not read the cube mesh. Enable 'Read/Write' in the import "
                + "settings of rubics_cube_anim.fbx (Model tab) and try again.");
            isAnimating = false;
            yield break;
        }
        Vector3[] rest = StickerCentroids();
        if (rest == null)
        {
            Debug.LogWarning("Layer dragging unavailable: the cube mesh does not have one submesh per sticker.");
            isAnimating = false;
            yield break;
        }

        Vector3 sum = Vector3.zero;
        for (int slot = 1; slot <= 54; slot++) sum += rest[slot];
        cubeCenter = sum / 54f;
        cubeHalf = 0f;
        for (int slot = 1; slot <= 54; slot++)
        {
            Vector3 p = rest[slot] - cubeCenter;
            cubeHalf = Mathf.Max(cubeHalf, Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
        }
        Debug.Log($"Cube mesh for dragging: {bindVertices.Length} vertices, {smr.bones.Length} bones, sticker centre "
            + $"{cubeCenter} (cube-local), half size {cubeHalf:0.###} local = {cubeHalf * LocalToWorldUnits:0.###} world units");

        foreach (var move in MoveTable)
        {
            // Jump to the very end of the clip and compare the sticker positions with the rest pose.
            cubeAnimator.Play(move.clip, 0, 0.999f);
            cubeAnimator.Update(0f);
            Vector3[] after = SkinWorldVertices(smr) ? StickerCentroids() : null;

            if (after != null && TryDescribeTurn(rest, after, out int axisIndex, out int side, out int sigma, out int[] movedSlots))
            {
                clipTurns.Add(new ClipTurn
                {
                    clip = move.clip, axis = move.axis, layer = move.layer, cw = move.cw,
                    axisIndex = axisIndex, side = side, sigma = sigma, slots = movedSlots,
                });
            }
            else
            {
                Debug.LogWarning($"Could not measure the '{move.clip}' animation; dragging will not use it.");
            }

            ResetAnimator();
        }

        cubeRenderer.ReapplyMaterials();
        cubeRenderer.Render();

        Debug.Log($"Layer dragging ready: measured {clipTurns.Count}/{MoveTable.Length} turn animations "
            + string.Join(", ", clipTurns.ConvertAll(t => $"{t.clip}(axis {t.axisIndex}, side {t.side}, turn {t.sigma})")));
        isAnimating = false;
    }

    private void ResetAnimator()
    {
        cubeAnimator.enabled = false;
        cubeAnimator.enabled = true;
        cubeAnimator.Rebind();
        cubeAnimator.Update(0f);
    }

    // Finds the single (axis, direction) that explains how every moved sticker travelled, plus which side moved.
    private bool TryDescribeTurn(Vector3[] rest, Vector3[] after, out int axisIndex, out int side, out int sigma, out int[] movedSlots)
    {
        axisIndex = side = sigma = 0;
        movedSlots = null;

        float movedMin = cubeHalf * 0.2f;
        float tolerance = cubeHalf * 0.3f;

        var moved = new List<int>();
        for (int slot = 1; slot <= 54; slot++)
            if ((after[slot] - rest[slot]).magnitude > movedMin) moved.Add(slot);
        if (moved.Count < 8) return false; // a quarter turn moves at least 8 stickers of the face plus its edge strips

        for (int a = 0; a < 3; a++)
        {
            for (int s = 1; s >= -1; s -= 2)
            {
                Quaternion turn = Quaternion.AngleAxis(s * 90f, Axis(a));
                bool fits = true;
                float along = 0f;
                foreach (int slot in moved)
                {
                    Vector3 p = rest[slot] - cubeCenter;
                    Vector3 q = after[slot] - cubeCenter;
                    if ((turn * p - q).magnitude > tolerance) { fits = false; break; }
                    along += Vector3.Dot(p, Axis(a));
                }
                if (!fits) continue;

                axisIndex = a;
                sigma = s;
                side = along >= 0f ? 1 : -1;
                movedSlots = moved.ToArray();
                return true;
            }
        }
        return false;
    }

    private void HandleLayerDrag()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || isAnimating || clipTurns.Count == 0) return;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 pos = mouse.position.ReadValue();
            layerDragDone = false;
            layerTurnStarted = false;
            layerDragSpeed = 0f;
            bool overCube = IsOverCube(pos);
            layerDragActive = overCube && TryRaycastSticker(pos);
            layerDragStart = pos;
            if (layerDragActive) HighlightFaceOf(layerHitSlot);
            return;
        }

        if (!layerDragActive) return;

        // Smoothed pointer speed, so the turn can play as fast as the drag was.
        float instantSpeed = mouse.delta.ReadValue().magnitude / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        layerDragSpeed = Mathf.Lerp(layerDragSpeed, instantSpeed, 0.35f);

        // Check the travelled distance every frame while the button is down AND on the frame it is released:
        // a quick flick presses, moves and releases within a frame or two, so the movement can arrive together
        // with the release and must still count.
        if (!layerDragDone)
        {
            Vector2 current = mouse.position.ReadValue();
            if ((current - layerDragStart).magnitude >= layerDragThreshold)
            {
                layerDragDone = true; // one turn per press, however far the mouse travels
                TryStartLayerTurn(current);
            }
        }

        if (!mouse.leftButton.isPressed)
        {
            layerDragActive = false;
            if (!layerTurnStarted) ClearHighlight(); // grabbed but nothing turned
        }
    }

    // ---- highlighting the grabbed face / turning layer (Debug Tools option) ----
    private const float FaceHighlight = 0.3f;
    private const float LayerHighlight = 0.5f;

    private void HighlightFaceOf(int slot)
    {
        if (!HighlightDraggedFace || cubeRenderer == null) return;

        int face = RubiksCubeRenderer.SlotToFaceRowCol(slot).face;
        var slots = new List<int>();
        for (int s = 1; s <= 54; s++)
            if (RubiksCubeRenderer.SlotToFaceRowCol(s).face == face) slots.Add(s);
        cubeRenderer.HighlightSlots(slots, FaceHighlight);
    }

    private void HighlightLayer(int[] slots)
    {
        if (!HighlightDraggedFace || cubeRenderer == null || slots == null) return;
        cubeRenderer.HighlightSlots(slots, LayerHighlight);
    }

    private void ClearHighlight()
    {
        if (cubeRenderer != null) cubeRenderer.ClearHighlight();
    }

    // Möller-Trumbore ray / triangle intersection.
    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (Mathf.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = inv * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = inv * Vector3.Dot(d, q);
        if (v < 0f || u + v > 1f) return false;
        t = inv * Vector3.Dot(e2, q);
        return t > 1e-6f;
    }

    // Casts a ray from the camera through the mouse position at the cube exactly as it is drawn right now.
    // Succeeds if a sticker is hit (or, on the black gap between stickers, the nearest sticker on the face turned
    // towards the camera) and remembers where and on which face. Everything is in world units here.
    private bool TryRaycastSticker(Vector2 screenPos)
    {
        Camera cam = Camera.main;
        SkinnedMeshRenderer smr = cubeRenderer != null ? cubeRenderer.Skinned : null;
        if (cam == null || smr == null || cubeRoot == null || !SkinWorldVertices(smr)) return false;

        Ray ray = cam.ScreenPointToRay(screenPos);

        // Nearest sticker hit and nearest body hit are tracked separately: the stickers lie slightly behind the rim
        // of each cubie, so the body is often hit just before the sticker the pointer is really on.
        float stickerT = float.MaxValue, bodyT = float.MaxValue;
        int stickerSlot = -1;
        Vector3 stickerNormalWorld = Vector3.zero;

        int submeshes = Mathf.Min(submeshIndices.Length, 55);
        for (int sub = 0; sub < submeshes; sub++)
        {
            int[] tris = submeshIndices[sub];
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = worldVertices[tris[i]], b = worldVertices[tris[i + 1]], c = worldVertices[tris[i + 2]];
                if (!RayTriangle(ray.origin, ray.direction, a, b, c, out float t)) continue;

                if (sub == 0)
                {
                    if (t < bodyT) bodyT = t;
                }
                else if (t < stickerT)
                {
                    stickerT = t;
                    stickerSlot = sub;
                    stickerNormalWorld = Vector3.Cross(b - a, c - a);
                }
            }
        }

        // The body only counts as "in front" if it is nearer than the sticker by more than the recess of the
        // stickers (about a tenth of the cube): then the ray went through a gap and the sticker found is on the far
        // side. Distances are world units because the ray direction is normalised.
        float margin = cubeHalf * LocalToWorldUnits * 0.35f;
        bool directHit = stickerSlot >= 1 && (stickerT - bodyT) <= margin;

        Vector3 rayDirLocal = cubeRoot.InverseTransformDirection(ray.direction);
        if (directHit)
        {
            Vector3 normalWorld = stickerNormalWorld.normalized;
            if (Vector3.Dot(normalWorld, ray.direction) > 0f) normalWorld = -normalWorld; // face the camera
            layerHitLocal = cubeRoot.InverseTransformPoint(ray.origin + ray.direction * stickerT);
            layerHitNormalLocal = cubeRoot.InverseTransformDirection(normalWorld).normalized;
            layerHitSlot = stickerSlot;
            ReportPick(cam, ray, screenPos, "sticker");
            return true;
        }

        // Pressed on the black gap between stickers: take the nearest sticker on the face turned to the camera.
        if (bodyT < float.MaxValue &&
            TryNearestFacingSticker(cubeRoot.InverseTransformPoint(ray.origin + ray.direction * bodyT), rayDirLocal,
                out Vector3 centroid, out Vector3 faceNormal, out int nearestSlot))
        {
            layerHitLocal = centroid;
            layerHitNormalLocal = faceNormal;
            layerHitSlot = nearestSlot;
            ReportPick(cam, ray, screenPos, "nearest sticker (gap)");
            return true;
        }
        return false;
    }

    // ---- diagnostics: does the picked point land under the cursor? ----
    private Vector3 lastPickWorld;
    private float lastPickTime = -100f;
    private string hintText = "";
    private float hintTime = -100f;

    private void ShowHint(string text)
    {
        hintText = text;
        hintTime = Time.time;
    }

    // Projects the picked point back onto the screen and compares it with the click. If the ray really starts at
    // the camera and hits the surface under the pointer, the error is a few pixels at most.
    private void ReportPick(Camera cam, Ray ray, Vector2 screenPos, string kind)
    {
        Vector3 hitWorld = cubeRoot.TransformPoint(layerHitLocal);
        lastPickWorld = hitWorld;
        lastPickTime = Time.time;
    }

    // Shows where the last pick landed (a ring for ~1.5 s) and any hint, so a mismatch with the cursor is visible.
    private void OnGUI()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        float scale = DebugUi.Scale;
        Matrix4x4 unscaled = DebugUi.Begin();
        try { DrawMarkerAndHint(cam, scale); }
        finally { DebugUi.End(unscaled); }
    }

    private void DrawMarkerAndHint(Camera cam, float scale)
    {
        if (ShowPickMarker && Time.time - lastPickTime <= 1.5f)
        {
            Vector3 sp = cam.WorldToScreenPoint(lastPickWorld);
            if (sp.z > 0f)
            {
                float size = 16f;
                Rect box = new Rect(sp.x / scale - size / 2f, (Screen.height - sp.y) / scale - size / 2f, size, size);
                Color prev = GUI.color;
                GUI.color = new Color(1f, 0.2f, 0.9f);
                GUI.DrawTexture(new Rect(box.x, box.y, size, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(box.x, box.yMax - 2f, size, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(box.x, box.y, 2f, size), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(box.xMax - 2f, box.y, 2f, size), Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }

        if (Time.time - hintTime <= 2f && hintText.Length > 0)
        {
            Rect pixels = cam.pixelRect;
            Rect area = new Rect(pixels.x / scale, pixels.y / scale, pixels.width / scale, pixels.height / scale);
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14 };
            style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            GUI.Label(new Rect(area.x, 12f, area.width, 28f), hintText, style);
        }
    }

    private bool TryNearestFacingSticker(Vector3 pointLocal, Vector3 rayDirLocal, out Vector3 centroid, out Vector3 faceNormal, out int slotFound)
    {
        centroid = faceNormal = Vector3.zero;
        slotFound = 0;
        Vector3[] centroids = StickerCentroids();
        if (centroids == null) return false;

        float best = cubeHalf * 0.5f; // about the distance from a sticker's centre to its edge plus a bit
        bool found = false;
        for (int slot = 1; slot <= 54; slot++)
        {
            Vector3 rel = centroids[slot] - cubeCenter;
            int axis = 0;
            for (int i = 1; i < 3; i++) if (Mathf.Abs(rel[i]) > Mathf.Abs(rel[axis])) axis = i;
            Vector3 n = Axis(axis) * (rel[axis] >= 0f ? 1f : -1f);
            if (Vector3.Dot(n, rayDirLocal) >= 0f) continue; // on a face turned away from the camera

            float distance = (centroids[slot] - pointLocal).magnitude;
            if (distance >= best) continue;
            best = distance;
            centroid = centroids[slot];
            faceNormal = n;
            slotFound = slot;
            found = true;
        }
        return found;
    }

    private void TryStartLayerTurn(Vector2 current)
    {
        Camera cam = Camera.main;
        if (cam == null || cubeRoot == null) return;

        // The drag, as a direction lying in the plane of the grabbed face.
        Vector3 hitWorld = cubeRoot.TransformPoint(layerHitLocal);
        Vector3 normalWorld = cubeRoot.TransformDirection(layerHitNormalLocal).normalized;
        var facePlane = new Plane(normalWorld, hitWorld);
        Ray startRay = cam.ScreenPointToRay(layerDragStart);
        Ray currentRay = cam.ScreenPointToRay(current);
        if (!facePlane.Raycast(startRay, out float e0) || !facePlane.Raycast(currentRay, out float e1))
        {
            ShowHint("Can't drag along a face seen edge-on - rotate the cube first");
            return;
        }
        Vector3 dragLocal = cubeRoot.InverseTransformDirection(currentRay.GetPoint(e1) - startRay.GetPoint(e0));

        // The layer turns about whichever in-plane axis is most perpendicular to the drag.
        int normalAxis = 0;
        for (int i = 1; i < 3; i++)
            if (Mathf.Abs(layerHitNormalLocal[i]) > Mathf.Abs(layerHitNormalLocal[normalAxis])) normalAxis = i;
        int axisIndex = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == normalAxis) continue;
            if (axisIndex < 0 || Mathf.Abs(dragLocal[i]) < Mathf.Abs(dragLocal[axisIndex])) axisIndex = i;
        }

        // Which layer along that axis did the grabbed sticker sit in? Only the outer two can turn.
        Vector3 p = layerHitLocal - cubeCenter;
        float along = p[axisIndex];
        if (Mathf.Abs(along) < cubeHalf * 0.34f)
        {
            ShowHint("That would turn a middle slice - drag along the other direction or grab an edge/corner sticker");
            return;
        }
        int side = along > 0f ? 1 : -1;

        // Which way must it turn so that the grabbed sticker moves along the drag?
        Vector3 moved = Quaternion.AngleAxis(20f, Axis(axisIndex)) * p;
        int sigma = Vector3.Dot(moved - p, dragLocal) >= 0f ? 1 : -1;

        foreach (ClipTurn turn in clipTurns)
        {
            if (turn.axisIndex == axisIndex && turn.side == side && turn.sigma == sigma)
            {
                // Play the turn as fast as the pointer was moving, so it feels like it follows the hand.
                float speed = Mathf.Clamp(layerDragSpeed / Mathf.Max(1f, dragSpeedReference), minTurnSpeed, maxTurnSpeed);
                layerTurnStarted = true;
                HighlightLayer(turn.slots);
                StartCoroutine(DoMove(turn.clip, turn.axis, turn.layer, turn.cw, speed));
                return;
            }
        }
        Debug.LogWarning("No measured turn animation matches that drag.");
        ShowHint("No turn animation matches that drag");
    }

    private void StartKeyMove(string face, bool prime)
    {
        string clip = prime ? face + "_prime" : face;
        foreach (var move in MoveTable)
        {
            if (move.clip != clip) continue;
            StartCoroutine(DoMove(move.clip, move.axis, move.layer, move.cw));
            return;
        }
    }

    private void Update()
    {
        HandleViewDrag();
        HandleLayerDrag();

        if (isAnimating) return;

        // Typing in a text box (the console's search field) must not turn the cube.
        if (DebugUi.TextInputActive) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

        if (kb.rKey.wasPressedThisFrame) StartKeyMove("R", shift);
        if (kb.lKey.wasPressedThisFrame) StartKeyMove("L", shift);
        if (kb.uKey.wasPressedThisFrame) StartKeyMove("U", shift);
        if (kb.dKey.wasPressedThisFrame) StartKeyMove("D", shift);
        if (kb.fKey.wasPressedThisFrame) StartKeyMove("F", shift);
        if (kb.bKey.wasPressedThisFrame) StartKeyMove("B", shift);

        // Cube rotation
        if (kb.qKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.up, -90f, 1f));
        if (kb.wKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.up,  90f, 1f));
        if (kb.aKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.right, -90f, 1f));
        if (kb.sKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.right,  90f, 1f));
        if (kb.xKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.forward, -90f, 1f));
        if (kb.zKey.wasPressedThisFrame) StartCoroutine(RotateOverTime(Vector3.forward,  90f, 1f));

        if (kb.eKey.wasPressedThisFrame) cubeRoot.rotation = Quaternion.identity;

        if (kb.cKey.wasPressedThisFrame)
        {
            cube.Reset();
            cubeRenderer.Render();
        }
    }

    private IEnumerator DoMove(string clipName, int axis, int layer, bool cw, float speed = 1f)
    {
        isAnimating = true;

        cubeAnimator.speed = speed;
        cubeAnimator.Play(clipName);
        PlayTurnWhoosh(speed);
        turnPlaying = true;
        if (ParticlesEnabled) StartCoroutine(EmitParticlesDuringTurn(FindTurnSlots(clipName), speed));
        yield return null;

        yield return new WaitUntil(() =>
        {
            AnimatorStateInfo info = cubeAnimator.GetCurrentAnimatorStateInfo(0);
            return info.IsName(clipName) && info.normalizedTime >= 1f;
        });

        turnPlaying = false;

        // Reset mesh transform before logical update
        cubeAnimator.speed = 1f;
        ResetAnimator();

        cubeRenderer.ReapplyMaterials();

        cubeRenderer.RotateFace(axis, layer, cw);
        PlayTurnClick();

        isAnimating = false;
    }

    private IEnumerator RotateOverTime(Vector3 axis, float angle, float duration)
    {
        Quaternion startRot = cubeRoot.rotation;
        Quaternion endRot = startRot * Quaternion.AngleAxis(angle, axis);

        float time = 0f;
        while (time < duration)
        {
            time += Time.deltaTime;
            cubeRoot.rotation = Quaternion.Slerp(startRot, endRot, time / duration);
            yield return null;
        }

        cubeRoot.rotation = endRot;
    }

    public RubiksCube GetCube() => cube;
    public string GetCubeState() => cube != null ? cube.GetStateString() : "";

    // ---- support for the benchmark (CubeBenchmark) ----

    public Transform CubeRootTransform => cubeRoot;
    public bool IsBusy => isAnimating;

    /// <summary>Applies random layer turns at once (no animation) and redraws the cube.</summary>
    public void ScrambleInstantly(int moves, System.Random rng)
    {
        if (cube == null || cubeRenderer == null) return;
        for (int i = 0; i < moves; i++) cube.Rotate(rng.Next(3), rng.Next(2) * 2, rng.Next(2) == 0);
        cubeRenderer.Render();
    }

    /// <summary>Puts a saved state back and redraws the cube.</summary>
    public void RestoreInstantly(FaceColor[] snapshot)
    {
        if (cube == null || cubeRenderer == null) return;
        cube.Restore(snapshot);
        cubeRenderer.Render();
    }

    /// <summary>
    /// The faces (index in "URFDLB") a camera at cam's position sees from the front, judged from where the stickers
    /// are right now: the angle between the face's outward direction and the line to the camera must be under
    /// acos(minCos). Null if the cube mesh can't be read.
    /// </summary>
    public List<int> VisibleStdFaces(Camera cam, float minCos)
    {
        List<FaceTowardsCamera> faces = FacesTowardsCamera(cam, minCos);
        return faces?.Select(f => f.Face).ToList();
    }

    /// <summary>A face (index in "URFDLB") that points towards the camera, how squarely, and where it sits on screen.</summary>
    public struct FaceTowardsCamera
    {
        public int Face;
        public float Facing;      // 1 = looking straight at the camera, 0 = edge-on
        public Vector2 Offset;    // the face's center on screen, relative to the cube's center (x right, y up, in pixels)
    }

    /// <summary>Every face whose outward direction is within acos(minCos) of the line to the camera, best-facing first.</summary>
    public List<FaceTowardsCamera> FacesTowardsCamera(Camera cam, float minCos)
    {
        SkinnedMeshRenderer smr = cubeRenderer != null ? cubeRenderer.Skinned : null;
        if (cam == null || smr == null || !SkinWorldVertices(smr) || submeshIndices == null || submeshIndices.Length < 55)
            return null;

        var slots = new int[54];
        for (int i = 0; i < 54; i++) slots[i] = i + 1;
        Vector3[] positions = StickerWorldPositions(slots);

        Vector3 centre = Vector3.zero;
        foreach (Vector3 p in positions) centre += p;
        centre /= 54f;

        var sum = new Vector3[6];
        var count = new int[6];
        for (int slot = 1; slot <= 54; slot++)
        {
            int face = RubiksCubeRenderer.SlotToFaceRowCol(slot).face;
            if (face < 0 || face > 5) continue;
            sum[face] += positions[slot - 1];
            count[face]++;
        }

        Vector3 centreOnScreen = cam.WorldToScreenPoint(centre);
        var visible = new List<FaceTowardsCamera>();
        for (int face = 0; face < 6; face++)
        {
            if (count[face] == 0) continue;
            Vector3 faceCentre = sum[face] / count[face];
            Vector3 outward = (faceCentre - centre).normalized;
            Vector3 toCamera = (cam.transform.position - faceCentre).normalized;
            float facing = Vector3.Dot(outward, toCamera);
            if (facing <= minCos) continue;
            Vector3 onScreen = cam.WorldToScreenPoint(faceCentre);
            visible.Add(new FaceTowardsCamera
            {
                Face = CubeGroundTruth.StdFaceOfAppFace[RubiksCubeRenderer.ModelFaceOfSlotFace(face)],
                Facing = facing,
                Offset = new Vector2(onScreen.x - centreOnScreen.x, onScreen.y - centreOnScreen.y),
            });
        }
        visible.Sort((a, b) => b.Facing.CompareTo(a.Facing));
        return visible;
    }
}
