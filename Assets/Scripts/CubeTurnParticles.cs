using UnityEngine;

/// <summary>
/// A small particle system for the sparks thrown off a turning layer. Particles are emitted one at a time with
/// explicit position, velocity and color (the caller decides where they come from), live in world space so they
/// stay behind as the layer moves on, and fade and shrink over their short lifetime. Needs no assets: the
/// material and its soft round texture are created in code.
/// </summary>
public class CubeTurnParticles
{
    private readonly ParticleSystem system;
    private readonly Material material;
    private readonly Texture2D texture;

    public CubeTurnParticles(Transform parent)
    {
        var go = new GameObject("Turn Particles");
        go.transform.SetParent(parent, false);

        system = go.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = system.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 2000;
        main.gravityModifier = 0.35f;
        main.startLifetime = 0.5f;
        main.startSize = 0.15f;

        var emission = system.emission;
        emission.enabled = false; // everything is emitted explicitly through Emit()

        var shape = system.shape;
        shape.enabled = false;

        var fade = system.colorOverLifetime;
        fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
        fade.color = gradient;

        var shrink = system.sizeOverLifetime;
        shrink.enabled = true;
        shrink.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 0.15f)));

        texture = MakeSoftDot(32);
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
            ?? Shader.Find("Particles/Standard Unlit")
            ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            material = new Material(shader) { mainTexture = texture };
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
        else
        {
            Debug.LogWarning("Turn particles: no particle shader found; the default material will be used.");
        }

        system.Play();
    }

    // A white disc with soft edges, so sparks look round instead of square.
    private static Texture2D MakeSoftDot(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                float alpha = Mathf.Clamp01(1f - d);
                alpha = alpha * alpha * (3f - 2f * alpha); // smoothstep falloff
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();
        return tex;
    }

    public void Emit(Vector3 position, Vector3 velocity, Color color, float size, float lifetime)
    {
        var p = new ParticleSystem.EmitParams
        {
            position = position,
            velocity = velocity,
            startColor = color,
            startSize = size,
            startLifetime = lifetime,
        };
        system.Emit(p, 1);
    }

    public void Dispose()
    {
        if (texture != null) Object.Destroy(texture);
        if (material != null) Object.Destroy(material);
    }
}
