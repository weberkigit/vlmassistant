using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Centralized color-to-material mapping system.
/// All color definitions and material assignments happen here.
/// Easy to modify: just change materials in the inspector or add new color mappings.
/// </summary>
/// 
public enum FaceColor
{
    None = 0,
    Red,
    Orange,
    White,
    Yellow,
    Green,
    Blue,
    Pink
}
public class CubeColorManager : MonoBehaviour
{
    [Header("Face Materials")]
    [SerializeField] private Material whiteMaterial;
    [SerializeField] private Material yellowMaterial;
    [SerializeField] private Material redMaterial;
    [SerializeField] private Material orangeMaterial;
    [SerializeField] private Material blueMaterial;
    [SerializeField] private Material greenMaterial;
    [SerializeField] private Material blackMaterial;
    [SerializeField] private Material pinkMaterial;

    /// <summary>
    /// Dictionary for quick color-to-material lookup.
    /// Initialize on first use to cache the mappings.
    /// </summary>
    private Dictionary<FaceColor, Material> colorToMaterialCache;

    private void OnEnable()
    {
        InitializeCache();
    }

    private void InitializeCache()
    {
        colorToMaterialCache = new Dictionary<FaceColor, Material>
        {
            { FaceColor.White,  whiteMaterial },
            { FaceColor.Yellow, yellowMaterial },
            { FaceColor.Red,    redMaterial },
            { FaceColor.Orange, orangeMaterial },
            { FaceColor.Blue,   blueMaterial },
            { FaceColor.Green,  greenMaterial },
            { FaceColor.None,   blackMaterial },
            { FaceColor.Pink,   pinkMaterial }
        };
    }

    /// <summary>
    /// Gets the material for a given color.
    /// Handles exact color matches and approximate matches (e.g., orange).
    /// Returns black material as fallback.
    /// </summary>
    public Material GetMaterialForColor(FaceColor color)
    {
        if (colorToMaterialCache.TryGetValue(color, out var material))
            return material;

        return pinkMaterial;
    }

    /// <summary>
    /// Gets all configured materials. Useful for debugging or UI.
    /// </summary>
    public IEnumerable<Material> GetAllMaterials()
    {
        yield return whiteMaterial;
        yield return yellowMaterial;
        yield return redMaterial;
        yield return orangeMaterial;
        yield return blueMaterial;
        yield return greenMaterial;
        yield return blackMaterial;
        yield return pinkMaterial;
    }
    public Color GetColorForFaceColor(FaceColor color)
    {
        return GetMaterialForColor(color).color;
    }
}
