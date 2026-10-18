using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// A photo-like background behind the cube: a large unlit quad placed behind it, facing the main camera, so the
/// backdrop appears both on screen and in what the VLM is shown (the capture renders the same scene).
///
/// The image comes from StreamingAssets/Backgrounds/background.jpg (or .png) if present - drop in any real photo
/// to replace it - and otherwise from ProceduralBackdrop. The component creates itself when the scene loads, so
/// nothing needs wiring in the scene.
/// </summary>
public class CubeBackdrop : MonoBehaviour
{
    public static CubeBackdrop Instance { get; private set; }

    [Tooltip("Subfolder under StreamingAssets that is checked for a replacement photo.")]
    public string folder = "Backgrounds";
    public string[] fileNames = { "background.jpg", "background.png" };

    private GameObject quad;
    private Material material;
    private Texture2D texture;
    private RubiksCubeRenderer cubeRenderer;
    private bool visible = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<CubeBackdrop>() != null) return;
        new GameObject("CubeBackdrop").AddComponent<CubeBackdrop>();
    }

    /// <summary>Shows or hides the backdrop (the Debug Tools menu uses this, e.g. to compare VLM reads without it).</summary>
    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            if (quad != null) quad.SetActive(value);
        }
    }

    private void Awake()
    {
        Instance = this;
    }

    private IEnumerator Start()
    {
        yield return LoadTexture();
        BuildQuad();
    }

    private IEnumerator LoadTexture()
    {
        foreach (string fileName in fileNames)
        {
            string path = Path.Combine(Application.streamingAssetsPath, folder, fileName);
            using (UnityWebRequest www = UnityWebRequest.Get(path))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) continue;

                var loaded = new Texture2D(2, 2, TextureFormat.RGB24, true);
                if (loaded.LoadImage(www.downloadHandler.data))
                {
                    texture = loaded;
                    Debug.Log($"Cube backdrop: using {fileName} from StreamingAssets/{folder}.");
                    yield break;
                }
                Destroy(loaded);
            }
        }

        // No photo supplied: generate the soft-focus workspace backdrop off the main thread (it takes a moment).
        const int width = 1024, height = 576;
        Task<byte[]> generate = Task.Run(() => ProceduralBackdrop.GenerateRgb(width, height));
        yield return new WaitUntil(() => generate.IsCompleted);
        if (generate.IsFaulted) yield break;

        // The generator writes rows top-down; textures are stored bottom-up.
        byte[] topDown = generate.Result;
        byte[] bottomUp = new byte[topDown.Length];
        int stride = width * 3;
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(topDown, y * stride, bottomUp, (height - 1 - y) * stride, stride);

        texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        texture.LoadRawTextureData(bottomUp);
        texture.Apply();
    }

    private void BuildQuad()
    {
        if (texture == null) return;

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        // Unlit, so scene lighting can't tint or shade it. Render-pipeline specific shaders are tried in turn.
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Texture")
            ?? Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogWarning("Cube backdrop: no unlit shader found, skipping the background.");
            return;
        }

        material = new Material(shader);
        material.mainTexture = texture;
        material.SetTexture("_BaseMap", texture); // URP's name for the main texture
        material.SetTexture("_MainTex", texture);

        quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "Backdrop";
        quad.transform.SetParent(transform, false);
        Destroy(quad.GetComponent<Collider>());

        var meshRenderer = quad.GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        quad.SetActive(visible);
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (quad == null || cam == null) return;

        if (cubeRenderer == null) cubeRenderer = FindFirstObjectByType<RubiksCubeRenderer>();
        Transform cube = cubeRenderer != null ? cubeRenderer.CubeTransform : null;

        // Far enough behind the cube that no part of it can poke through, however it is rotated.
        float cubeDepth = cube != null
            ? Mathf.Max(1f, Vector3.Dot(cube.position - cam.transform.position, cam.transform.forward))
            : 10f;
        float distance = cubeDepth * 1.6f + 2f;

        // Cover the whole view, with extra margin because the VLM capture briefly aims the camera at the cube.
        float height = 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.4f;
        float width = height * Mathf.Max(cam.aspect, 1.78f);

        quad.transform.SetPositionAndRotation(
            cam.transform.position + cam.transform.forward * distance, cam.transform.rotation);
        quad.transform.localScale = new Vector3(width, height, 1f);
    }

    private void OnDestroy()
    {
        if (texture != null) Destroy(texture);
        if (material != null) Destroy(material);
    }
}
