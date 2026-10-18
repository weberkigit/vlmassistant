using UnityEngine;

/// <summary>
/// Synthesises the two sounds of a cube layer turning, so no audio files are needed: a short plastic "whoosh"
/// (filtered noise that swells and fades) while the layer moves, and a crisp "click" when it snaps into place.
/// </summary>
public static class CubeTurnSound
{
    private const int SampleRate = 44100;

    /// <summary>Short, crisp click: a bright noise transient plus two quickly damped tones.</summary>
    public static AudioClip CreateClick()
    {
        int length = (int)(0.09f * SampleRate);
        var data = new float[length];
        var rng = new System.Random(11);

        float lowpass = 0f;
        for (int i = 0; i < length; i++)
        {
            float t = i / (float)SampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lowpass += (noise - lowpass) * 0.25f;
            float highpassed = noise - lowpass; // keeps only the bright part, so the transient is crisp

            float transient = highpassed * Mathf.Exp(-t / 0.0035f) * 0.7f;
            float tone = Mathf.Sin(2f * Mathf.PI * 1850f * t) * Mathf.Exp(-t / 0.011f) * 0.35f
                       + Mathf.Sin(2f * Mathf.PI * 3250f * t) * Mathf.Exp(-t / 0.007f) * 0.20f;
            data[i] = transient + tone;
        }

        return MakeClip("cube click", data, 0.7f);
    }

    /// <summary>Soft whoosh with a faint rumble: low-passed noise whose brightness and loudness rise and fall.</summary>
    public static AudioClip CreateWhoosh()
    {
        int length = (int)(0.28f * SampleRate);
        var data = new float[length];
        var rng = new System.Random(23);

        float lowpass = 0f;
        for (int i = 0; i < length; i++)
        {
            float u = i / (float)length;
            float t = i / (float)SampleRate;
            float envelope = Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.8f));
            float openness = Mathf.Lerp(0.05f, 0.35f, Mathf.Sin(Mathf.PI * u)); // filter opens and closes
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lowpass += (noise - lowpass) * openness;
            float rumble = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 85f * t);
            data[i] = lowpass * envelope * rumble;
        }

        return MakeClip("cube whoosh", data, 0.35f);
    }

    private static AudioClip MakeClip(string name, float[] data, float peak)
    {
        float max = 0.0001f;
        foreach (float v in data) max = Mathf.Max(max, Mathf.Abs(v));
        float gain = peak / max;
        for (int i = 0; i < data.Length; i++) data[i] *= gain;

        AudioClip clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
