using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Validates and combines VLM-read facelet strings for a Rubik's Cube.
/// Facelet order: 9 stickers of U, then R, F, D, L, B (each read left-to-right,
/// top-to-bottom looking directly at that face) - matches cube_solver_server.py
/// and the FaceletReadingPrompt in LLMHandler.
/// </summary>
public static class FaceletValidator
{
    private static readonly char[] FaceOrder = { 'U', 'R', 'F', 'D', 'L', 'B' };

    /// <summary>
    /// Checks that a facelet string is a structurally legal cube reading: 54 characters,
    /// only U/R/F/D/L/B, exactly 9 of each, and each face's center matches its own letter.
    /// Does not check solvability (permutation/orientation parity) - the solver server
    /// itself will reject a structurally valid but physically impossible cube.
    /// </summary>
    public static bool IsValid(string facelets, out string error)
    {
        if (string.IsNullOrEmpty(facelets) || facelets.Length != 54)
        {
            error = $"facelet string must be 54 characters, got {facelets?.Length ?? 0}";
            return false;
        }

        var counts = new Dictionary<char, int>();
        foreach (char c in facelets)
        {
            if (Array.IndexOf(FaceOrder, c) < 0)
            {
                error = $"unexpected character '{c}', only U R F D L B are allowed";
                return false;
            }
            counts.TryGetValue(c, out int existing);
            counts[c] = existing + 1;
        }

        foreach (char face in FaceOrder)
        {
            counts.TryGetValue(face, out int count);
            if (count != 9)
            {
                error = $"face '{face}' appears {count} times, expected 9";
                return false;
            }
        }

        for (int faceIndex = 0; faceIndex < FaceOrder.Length; faceIndex++)
        {
            int centerPos = faceIndex * 9 + 4;
            if (facelets[centerPos] != FaceOrder[faceIndex])
            {
                error = $"center of the {FaceOrder[faceIndex]} face (position {centerPos + 1}) reads "
                    + $"'{facelets[centerPos]}' instead of '{FaceOrder[faceIndex]}'";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Combines several readings of the same cube state via per-sticker majority vote.
    /// perPositionConfidence[i] is the winning letter's agreement fraction (0-1) at that
    /// position; overallConfidence is their average. Returns null if no sample was a
    /// usable 54-character string.
    /// </summary>
    public static string MajorityVote(List<string> samples, out float overallConfidence, out float[] perPositionConfidence)
    {
        perPositionConfidence = new float[54];
        overallConfidence = 0f;

        List<string> validSamples = samples?.Where(s => !string.IsNullOrEmpty(s) && s.Length == 54).ToList()
            ?? new List<string>();
        if (validSamples.Count == 0) return null;

        char[] result = new char[54];
        for (int pos = 0; pos < 54; pos++)
        {
            var votes = new Dictionary<char, int>();
            foreach (string sample in validSamples)
            {
                char c = sample[pos];
                votes.TryGetValue(c, out int existing);
                votes[c] = existing + 1;
            }

            char winner = ' ';
            int winnerCount = 0;
            foreach (var kv in votes)
            {
                if (kv.Value > winnerCount)
                {
                    winner = kv.Key;
                    winnerCount = kv.Value;
                }
            }

            result[pos] = winner;
            perPositionConfidence[pos] = (float)winnerCount / validSamples.Count;
        }

        overallConfidence = perPositionConfidence.Average();
        return new string(result);
    }
}
