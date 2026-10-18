using UnityEngine;

/// <summary>
/// Shared color palette for the STT/LLM/TTS/Solver server status indicators, so all four use the
/// same refined colors instead of each script hardcoding its own raw Color.red/Color.green.
/// </summary>
public static class ServerStatusColors
{
    public static readonly Color Connected = new Color(0.30f, 0.75f, 0.45f);
    public static readonly Color Disconnected = new Color(0.85f, 0.35f, 0.35f);
}
