using UnityEngine;
using System.Collections.Generic;

public class RubiksCubeRenderer : MonoBehaviour
{
    [SerializeField] private CubeColorManager colorManager;
    [SerializeField] private GameObject cubePrefab;

    private SkinnedMeshRenderer mr;
    [SerializeField] private GameObject meshGO;


    private RubiksCube cube;

    //orange rot gruen blau gelb weiss
    private static readonly int[] faceRemap = { 1, 0, 4, 5, 3, 2 };

    private static readonly (bool cw, bool ccw, bool r180, bool fh, bool fv)[] faceTransforms =
    {
        (true, false, false, false, false), // face 0 - Right
        (true, false, false, true, false), // face 1 - Left
        (true, false, false, false, false), // face 2 - Up
        (true, false, false, true, false), // face 3 - Down
        (true, false, false, true, false), // face 4 - Front
        (false, true, false, false, true), // face 5 - Back
    };



    private Material[] stickerMats; // one cloned material per slot (index 1-54)

    public void Initialize(RubiksCube rubiksCube)
    {
        cube = rubiksCube;

        Material[] mats = mr.materials;
        stickerMats = new Material[mats.Length];
        stickerMats[0] = mats[0]; 

        for (int slot = 1; slot < mats.Length; slot++)
        {
            stickerMats[slot] = new Material(mats[slot]);
            stickerMats[slot].name = slot.ToString();
        }

        mr.materials = stickerMats;
        Render();
    }

    private static (int row, int col) TransformCoords(
        int row,
        int col,
        bool rotate90CW = false,
        bool rotate90CCW = false,
        bool rotate180 = false,
        bool flipHorizontal = false,
        bool flipVertical = false)
    {
        if (rotate90CW)
        {
            int r = col;
            int c = 2 - row;
            row = r;
            col = c;
        }

        if (rotate90CCW)
        {
            int r = 2 - col;
            int c = row;
            row = r;
            col = c;
        }

        if (rotate180)
        {
            row = 2 - row;
            col = 2 - col;
        }

        if (flipHorizontal)
        {
            col = 2 - col;
        }

        if (flipVertical)
        {
            row = 2 - row;
        }

        return (row, col);
    }

    private void Awake()
    {
        if (meshGO != null) mr = meshGO.GetComponent<SkinnedMeshRenderer>();
    }
    public void ReapplyMaterials()
    {
        mr.materials = stickerMats;
    }

    public void Render()
    {
        if (cube == null || mr == null) return;

        for (int slot = 1; slot < stickerMats.Length; slot++)
        {
            var (face, row, col) = SlotToFaceRowCol(slot);
            if (face < 0) continue;

            int remappedFace = faceRemap[face];
            var t = faceTransforms[face];
            var (tr, tc) = TransformCoords(row, col, t.cw, t.ccw, t.r180, t.fh, t.fv);

            FaceColor color = cube.GetColor(remappedFace, tr, tc);
            stickerMats[slot].color = colorManager.GetColorForFaceColor(color);
        }
    }

    /// <summary>Lightens the given sticker slots towards white (until the next Render), e.g. to show a dragged layer.</summary>
    public void HighlightSlots(IEnumerable<int> slots, float amount)
    {
        if (stickerMats == null) return;
        Render(); // start from the true colors so repeated highlights don't pile up
        foreach (int slot in slots)
        {
            if (slot < 1 || slot >= stickerMats.Length) continue;
            stickerMats[slot].color = Color.Lerp(stickerMats[slot].color, Color.white, amount);
        }
    }

    /// <summary>The color a sticker slot is currently drawn in.</summary>
    public Color GetStickerColor(int slot)
    {
        return stickerMats != null && slot >= 1 && slot < stickerMats.Length ? stickerMats[slot].color : Color.white;
    }

    /// <summary>Removes any highlight by redrawing every sticker in its real color.</summary>
    public void ClearHighlight()
    {
        if (stickerMats != null) Render();
    }

    public void RotateFace(int axis, int layer, bool clockwise)
    {
        cube.Rotate(axis, layer, clockwise);
        Render();
    }

    /// <summary>The cube's skinned mesh (one material slot per sticker), for baking and picking.</summary>
    public SkinnedMeshRenderer Skinned => mr;

    public RubiksCube GetCube() => cube;

    /// <summary>The model face (0 R, 1 L, 2 U, 3 D, 4 F, 5 B) whose colors a face of the 3D model's sticker slots shows.</summary>
    public static int ModelFaceOfSlotFace(int slotFace) => faceRemap[slotFace];
    public Transform CubeTransform => cubePrefab.transform;

    public static (int face, int row, int col) SlotToFaceRowCol(int slot)
    {
        switch (slot)
        {
            // ---- Up (face 2) - read from 2D grid row/col ----
            case 36: return (2, 0, 0); // weiss_vol  -> Up [0,0]
            case  3: return (2, 0, 1); // weiss_vl   -> Up [0,1]
            case 48: return (2, 0, 2); // weiss_vul  -> Up [0,2]
            case 11: return (2, 1, 0); // weiss_vo   -> Up [1,0]
            case 25: return (2, 1, 1); // weiss_v    -> Up [1,1]
            case 14: return (2, 1, 2); // weiss_vu   -> Up [1,2]
            case 40: return (2, 2, 0); // weiss_vor  -> Up [2,0]
            case  6: return (2, 2, 1); // weiss_vr   -> Up [2,1]
            case 52: return (2, 2, 2); // weiss_vur  -> Up [2,2]

            // ---- Left (face 1) ----
            case 33: return (1, 0, 0); // orange_hol -> Left [0,0]
            case  1: return (1, 0, 1); // orange_hl  -> Left [0,1]
            case 45: return (1, 0, 2); // orange_hul -> Left [0,2]
            case 18: return (1, 1, 0); // orange_ol  -> Left [1,0]
            case 26: return (1, 1, 1); // orange_l   -> Left [1,1]
            case 15: return (1, 1, 2); // orange_uk  -> Left [1,2]
            case 37: return (1, 2, 0); // orange_vol -> Left [2,0]
            case  4: return (1, 2, 1); // orange_vl  -> Left [2,1]
            case 49: return (1, 2, 2); // orange_vul -> Left [2,2]

            // ---- Front (face 4) ----
            case 50: return (4, 0, 0); // gruen_vur  -> Front [0,0]
            case 21: return (4, 0, 1); // gruen_ur   -> Front [0,1]
            case 41: return (4, 0, 2); // gruen_hur  -> Front [0,2]
            case 13: return (4, 1, 0); // gruen_vu   -> Front [1,0]
            case 27: return (4, 1, 1); // gruen_u    -> Front [1,1]
            case  8: return (4, 1, 2); // gruen_hu   -> Front [1,2]
            case 47: return (4, 2, 0); // gruen_vul  -> Front [2,0]
            case 16: return (4, 2, 1); // gruen_ul   -> Front [2,1]
            case 44: return (4, 2, 2); // gruen_hul  -> Front [2,2]

            // ---- Right (face 0) - from image 2 ----
            case 31: return (0, 0, 0); // rot_hor    -> Right [0,0]
            case 54: return (0, 0, 1); // rot_hr     -> Right [0,1]
            case 43: return (0, 0, 2); // rot_hur    -> Right [0,2]
            case 19: return (0, 1, 0); // rot_or     -> Right [1,0]
            case 24: return (0, 1, 1); // rot_r      -> Right [1,1]
            case 22: return (0, 1, 2); // rot_ur     -> Right [1,2]
            case 39: return (0, 2, 0); // rot_vor    -> Right [2,0]
            case  5: return (0, 2, 1); // rot_vr     -> Right [2,1]
            case 51: return (0, 2, 2); // rot_vur    -> Right [2,2]

            // ---- Down (face 3) - from 2D grid ----
            case 34: return (3, 0, 0); // gelb_hor   -> Down [0,0] 
            case  2: return (3, 0, 1); // gelb_hl    -> Down [0,1]
            case 46: return (3, 0, 2); // gelb_hul   -> Down [0,2]
            case 10: return (3, 1, 0); // gelb_ho    -> Down [1,0]
            case 23: return (3, 1, 1); // gelb_h     -> Down [1,1]
            case  7: return (3, 1, 2); // gelb_hu    -> Down [1,2]
            case 30: return (3, 2, 0); // gelb_hor   -> Down [2,0] 
            case 53: return (3, 2, 1); // gelb_hr    -> Down [2,1]
            case 42: return (3, 2, 2); // gelb_hur   -> Down [2,2]

            // ---- Back (face 5) - derived from corners/edges ----
            case 29: return (5, 0, 0); // blau_hor   -> Back [0,0]
            case 20: return (5, 0, 1); // blau_or    -> Back [0,1]
            case 38: return (5, 0, 2); // blau_vor   -> Back [0,2]
            case  9: return (5, 1, 0); // blau_ho    -> Back [1,0]
            case 28: return (5, 1, 1); // blau_o     -> Back [1,1]
            case 12: return (5, 1, 2); // blau_vo    -> Back [1,2]
            case 32: return (5, 2, 0); // blau_hol   -> Back [2,0]
            case 17: return (5, 2, 1); // blau_ol    -> Back [2,1]
            case 35: return (5, 2, 2); // blau_vol   -> Back [2,2]

            default: return (-1, -1, -1);
        }
    }
}
