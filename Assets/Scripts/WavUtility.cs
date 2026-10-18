using System;
using UnityEngine;

public static class WavUtility
{
    public static AudioClip ToAudioClip(byte[] wavFile)
    {
        if (wavFile == null || wavFile.Length < 44)
        {
            Debug.LogError("Invalid WAV file");
            return null;
        }

        // Read sample rate (offset 24)
        int sampleRate = BitConverter.ToInt32(wavFile, 24);

        // Find data chunk
        int pos = 12;
        while (!(wavFile[pos] == 100 && wavFile[pos + 1] == 97 &&
                 wavFile[pos + 2] == 116 && wavFile[pos + 3] == 97))
        {
            pos += 4;
            int chunkSize = BitConverter.ToInt32(wavFile, pos);
            pos += 4 + chunkSize;
        }

        pos += 8;

        int sampleCount = (wavFile.Length - pos) / 2;
        float[] samples = new float[sampleCount];

        int i = 0;
        while (pos < wavFile.Length)
        {
            short s = BitConverter.ToInt16(wavFile, pos);
            samples[i] = s / 32768f;
            pos += 2;
            i++;
        }

        AudioClip clip = AudioClip.Create("wav", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}