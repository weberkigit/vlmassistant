using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// HTTP client for the standalone cube_solver_server.py process (see repo root).
/// POSTs a 54-character facelet string to /solve and returns the move sequence.
/// </summary>
public class CubeSolverClient : MonoBehaviour
{
    [Header("Cube Solver Server")]
    public string host = "127.0.0.1";
    public int port = 8090;
    public float timeoutSeconds = 5f;

    [Header("Status")]
    public Image statusIndicator;

    void Start()
    {
        TestConnection();
    }

    /// <summary>
    /// Pings the solver server's /health endpoint and updates statusIndicator - same pattern used
    /// by TTSHandler/STTHandler for their own connection indicators.
    /// </summary>
    public void TestConnection(Action<bool> onResult = null)
    {
        StartCoroutine(TestHealthCheck(onResult));
    }

    [Serializable]
    private class HealthResponse
    {
        public string status;
    }

    private IEnumerator TestHealthCheck(Action<bool> onResult)
    {
        using (UnityWebRequest request = UnityWebRequest.Get($"http://{host}:{port}/health"))
        {
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            yield return request.SendWebRequest();

            bool isHealthy = false;
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    HealthResponse health = JsonConvert.DeserializeObject<HealthResponse>(request.downloadHandler.text);
                    isHealthy = health != null && health.status == "healthy";
                }
                catch (Exception)
                {
                    isHealthy = false;
                }
            }

            if (statusIndicator != null)
            {
                statusIndicator.color = isHealthy ? ServerStatusColors.Connected : ServerStatusColors.Disconnected;
            }
            onResult?.Invoke(isHealthy);
        }
    }

    [Serializable]
    private class SolveRequest
    {
        public string facelets;
    }

    [Serializable]
    private class SolveResponse
    {
        public bool success;
        public string solution;
        public int length;
        public string error;
    }

    /// <summary>
    /// Solves the cube described by a 54-character URFDLB facelet string.
    /// onComplete receives (true, solutionMoves) on success, or (false, errorMessage) on failure.
    /// </summary>
    public IEnumerator Solve(string facelets, Action<bool, string> onComplete)
    {
        string url = $"http://{host}:{port}/solve";
        string body = JsonConvert.SerializeObject(new SolveRequest { facelets = facelets });

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(body);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(false, $"Solver request failed: {request.error}");
                yield break;
            }

            SolveResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<SolveResponse>(request.downloadHandler.text);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(false, $"Failed to parse solver response: {ex.Message}");
                yield break;
            }

            if (response != null && response.success)
            {
                onComplete?.Invoke(true, response.solution);
            }
            else
            {
                onComplete?.Invoke(false, response?.error ?? "Unknown solver error");
            }
        }
    }
}
