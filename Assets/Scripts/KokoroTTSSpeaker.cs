using System.Collections;
using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections.Generic;

public class KokoroTTSSpeaker : MonoBehaviour, ITTSSpeaker
{
    [SerializeField] private string ttsUrl = "http://localhost:8880";  // TTS Server URL
    public string host = "localhost";
    public int port = 8880; // TTS Server Port
    [Header("Audio Settings")]
    public AudioSource audioSource;    // Audio source for playback
    public bool playInQueue = true; // Play audio immediately or queue
    private Queue<AudioClip> audioQueue = new Queue<AudioClip>(); // Queue for audio clips
    private bool isPlaying = false;

    public float gain = 4f;

    //public bool delayPlayback = false; // Delay playback for zoom demo

    [Header("Voice Settings")]
    public VoiceOption selectedVoice = VoiceOption.af_sky;  // Default voice in Inspector

    [Header("Tools")]
    public string text = "Hello, this is a test!";  // Default text to synthesize

    private const int sampleRate = 24000;               // PCM sample rate

    private ITTSSpeaker.AudioStatus audioStatus = ITTSSpeaker.AudioStatus.Idle; // Current audio status

    // Enum for voice options
    public enum VoiceOption
    {
        af_heart,  // Grade A
        af_bella,  // Grade A-
        af_sarah,  // Grade C+
        af_nicole, // Grade B-
        af_sky,    // Grade C-
        zf_xiaoxiao, // Grade C
        am_adam,   // Grade F+
        am_michael,// Grade C+
        am_fenrir, // Grade C+
        bf_emma,   // Grade B-
        bf_isabella,// Grade C
        bm_george, // Grade C
        bm_lewis // Grade D+
    }

    public void Speak(string textToSynthesize, string language, Action onComplete = null, Action onAudioStarted = null)
    {
        text = textToSynthesize;
        if (language == "Chinese")
        {
            selectedVoice = VoiceOption.zf_xiaoxiao; // Set to Chinese voice
        }
        else if (language == "English")
        {
            selectedVoice = VoiceOption.af_heart; // Set to English voice
        }

        audioStatus = ITTSSpeaker.AudioStatus.Playing;
        StartCoroutine(StreamTTS(text));
    }

    [ContextMenu("Speak Test")]
    // Call this function to start speaking text
    public void Speak()
    {
        Debug.Log("🔥 Kokoro Speak triggered");
        StartCoroutine(StreamTTS(text));
    }

    private IEnumerator StreamTTS(string text)
    {
        Debug.Log("StreamTTS STARTED");
        // Initialize stopwatch for latency measurement
        DateTime startTime = DateTime.Now;

        // Prepare JSON payload
        string jsonPayload = JsonUtility.ToJson(new TTSRequest
        {
            input = text,
            voice = selectedVoice.ToString(),
            response_format = "wav",
            stream = true
        });

        ttsUrl = $"http://{host}:{port}"; // Update TTS URL with host and port

        // Create POST request
        using (UnityWebRequest request = new UnityWebRequest(ttsUrl + "/v1/audio/speech", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            Debug.Log("Sending TTS request...");
            // Send the request and wait for data
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("TTS Response received, processing audio...");
                var latency = DateTime.Now - startTime;
                Debug.Log($"Kokoro TTS Latency: {latency.Milliseconds} ms.");
                byte[] audioData = request.downloadHandler.data;
                Debug.Log("First 20 bytes: " + BitConverter.ToString(audioData, 0, 20));

                // play immediatly or play in queue
                PlayPCMStream(audioData);
                if (!playInQueue)
                {
                    //PlayPCMStream(audioData);
                }
                else
                {
                    //EnqueueAudio(audioData);
                }
                StartCoroutine(WaitForPlaybackToFinish());
            }
            else
            {
                Debug.LogError($"Error: {request.responseCode} - {request.error}");
                audioStatus = ITTSSpeaker.AudioStatus.Error;
            }
        }
    }

    private IEnumerator WaitForPlaybackToFinish()
    {
        // Let AudioSource.Play() take effect before checking isPlaying.
        yield return null;
        yield return new WaitUntil(() => audioSource == null || !audioSource.isPlaying);
        if (audioStatus == ITTSSpeaker.AudioStatus.Playing)
        {
            audioStatus = ITTSSpeaker.AudioStatus.Completed;
        }
    }

    private void EnqueueAudio(byte[] audioData)
    {
         if (audioData == null || audioData.Length == 0)
        {
            Debug.LogWarning("EnqueueAudio: No audio data to enqueue.");
            return;
        }
        float[] floatData = ConvertPCMToFloat(audioData);
        AudioClip clip = AudioClip.Create("TTS_Audio", floatData.Length, 1, sampleRate, false);
        clip.SetData(floatData, 0);
        audioQueue.Enqueue(clip);

        if (!isPlaying)
        {
            StartCoroutine(PlayQueuedAudio());
        }
    }

    private IEnumerator PlayQueuedAudio()
    {
        yield return new WaitUntil(() => audioQueue.Count > 0);

        while (audioQueue.Count > 0)
        {
            isPlaying = true;
            AudioClip clip = audioQueue.Dequeue();
            audioSource.clip = clip;
            audioSource.Play();
            yield return new WaitForSeconds(clip.length);
        }
        isPlaying = false;

    }
    private void PlayPCMStream(byte[] audioData)
    {
    Debug.LogError("trying to play audio in wav format");
    if (audioSource == null)
    {
        Debug.LogError("AudioSource is missing!");
        return;
    }

    AudioClip clip = WavUtility.ToAudioClip(audioData);

    if (clip == null)
    {
        Debug.LogError("Failed to decode WAV audio!");
        return;
    }

    audioSource.clip = clip;
    Debug.LogError("Playing audio in wav format");
    audioSource.Play();
    }
    public void Stop()
    {
        StopAllCoroutines();
        audioQueue.Clear();
        if (audioSource != null)
        {
            audioSource.Stop();
        }
        isPlaying = false;
        audioStatus = ITTSSpeaker.AudioStatus.Completed;
    }
    private float[] ConvertPCMToFloat(byte[] pcmData)
    {
        int sampleCount = pcmData.Length / 2;  // 16-bit PCM
        float[] floatData = new float[sampleCount];


        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)((pcmData[i * 2 + 1] << 8) | pcmData[i * 2]);
            float normalized = sample / 32768.0f;
            normalized *= gain;  // Apply gain multiplier
            normalized = Mathf.Clamp(normalized, -1f, 1f);  // Prevent clipping
            floatData[i] = normalized;
        }
        return floatData;
    }

    public ITTSSpeaker.AudioStatus GetStatus()
    {
        return audioStatus;
    }

    [ContextMenu("Test Connection")]
    public void TestConnection(Action<bool> onResult = null)
    {
        StartCoroutine(TestHealthCheck(onResult));
    }

    private IEnumerator TestHealthCheck(Action<bool> onResult)
    {
        ttsUrl = $"http://{host}:{port}"; // Update TTS URL with host and port
        using (UnityWebRequest request = UnityWebRequest.Get(ttsUrl + "/health"))
        {
            request.timeout = 3;  // Set timeout duration

            // Send GET request to /health endpoint
            yield return request.SendWebRequest();

            // Handle different response results
            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Health check response: " + request.downloadHandler.text);

                // Check if the response matches {"status":"healthy"}
                if (request.downloadHandler.text.Contains("\"status\":\"healthy\""))
                {
                    Debug.Log("Server is healthy and responding.");
                    onResult?.Invoke(true);
                }
                else
                {
                    Debug.LogWarning("Server responded, but health check failed.");
                    onResult?.Invoke(false);
                }
            }
            else if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError($"Failed to connect to server: {request.responseCode} - {request.error}");
                onResult?.Invoke(false);
            }
            else if (request.result == UnityWebRequest.Result.DataProcessingError)
            {
                Debug.LogError("Data processing error during the health check.");
                onResult?.Invoke(false);
            }
            else
            {
                Debug.LogError("Unknown error during the health check.");
                onResult?.Invoke(false);
            }
        }
    }

    // Helper class to create JSON payload
    [System.Serializable]
    private class TTSRequest
    {
        public string input;
        public string voice;
        public string response_format;
        public bool stream = true;
    }
}