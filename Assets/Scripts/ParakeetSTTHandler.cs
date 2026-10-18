using NativeWebSocket;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class ParakeetServerEventMessage
{
    public string vad;
    public string text;
}

public class ParakeetSTTHandler : MonoBehaviour
{
    [Header("LLM")]
    public LLMHandler llmhandler;
    public string testString = "";

    public string userRole = "";

    [Header("Connection Settings")]
    public string serverIP = "127.0.0.1";
    public string serverPort = "5092"; 

    private string sessionId;
    private string eventsUrl;
    private string audioUrl;

    [Header("Audio Settings")]
    public int recordingFrequency = 16000;
    public int recordingLengthSec = 1;

    [HideInInspector]
    public string selectedMicrophoneDeviceName = "";

    [Tooltip("The microphone device that will be used (selected via dropdown).")]
    [SerializeField]
    private string activeMicrophoneDeviceForInfo = "";

    [Header("Status")]
    public bool isEventsConnected = false;
    public bool isAudioConnected = false;
    public bool isRecording = false;
    public string lastError = "";

    [Header("Mute State")]
    public bool isMuted = true;

    [Header("TTS State")]
    public bool isTTSPlaying = false;
    public TMP_Text transcriptionText;
    public TMP_Text subtitle;
    public float displayTime = 3f;
    public float fadeOutDuration = 0.5f;

    private Coroutine _activeFadeCoroutine;

    [Header("Events")]
    public UnityEngine.Events.UnityEvent OnConnected;
    public UnityEngine.Events.UnityEvent OnDisconnected;

    [System.Serializable]
    public class TranscriptionEvent : UnityEngine.Events.UnityEvent<string, string> { }

    public TranscriptionEvent OnFinalTranscription;

    [Header("VAD Events")]
    public UnityEngine.Events.UnityEvent OnSpeechStartDetected;
    public UnityEngine.Events.UnityEvent OnSpeechStopDetected;

    private WebSocket eventsWs;
    private WebSocket audioWs;

    private string microphoneDevice;
    private bool wantsToConnect = false;
    private bool _onConnectedInvoked = false;
    private bool hasStarted = false;

    void Awake()
    {
        activeMicrophoneDeviceForInfo = selectedMicrophoneDeviceName;
    }

    void Start()
    {
        InitializeMicrophoneAndStart();

        if (OnFinalTranscription == null) OnFinalTranscription = new TranscriptionEvent();
        OnFinalTranscription.AddListener(HandleFinalTranscription);
        
        hasStarted = true;
    }

    void OnEnable()
    {
        if (hasStarted && Application.isPlaying)
        {
            if (!isRecording)
            {
                isRecording = true;
                StartCoroutine(ConsumeAndSendAudio());
            }
        }
    }

    void OnDisable()
    {
        if (Application.isPlaying)
        {
            isRecording = false;
            _ = Disconnect();
        }
    }

    async void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (eventsWs != null) eventsWs.DispatchMessageQueue();
        if (audioWs != null) audioWs.DispatchMessageQueue();
#endif

        if (wantsToConnect)
        {
            wantsToConnect = false;
            await Connect();
        }
    }

    async void OnDestroy()
    {
        await Disconnect();
        StopMicrophone();
    }

    async void OnApplicationQuit()
    {
        await Disconnect();
        StopMicrophone();
    }

    public void SetUserRoleName(string roleName)
    {
        userRole = roleName + ": ";
    }

    public string GetCurrentMicrophoneDevice()
    {
        return microphoneDevice;
    }

    public void OnMicrophoneSelectedInEditor(string deviceName)
    {
        selectedMicrophoneDeviceName = deviceName;
        activeMicrophoneDeviceForInfo = deviceName;
    }

    private void InitializeMicrophoneAndStart()
    {
        Debug.Log("[ParakeetSTTHandler] Audio input is managed by AECAudioProcessor.");

        if (!isRecording)
        {
            isRecording = true;
            StartCoroutine(ConsumeAndSendAudio());
        }

        if (Microphone.devices.Length > 0)
        {
            bool found = false;
            if (!string.IsNullOrEmpty(selectedMicrophoneDeviceName))
            {
                foreach (string dev in Microphone.devices)
                {
                    if (dev == selectedMicrophoneDeviceName)
                    {
                        microphoneDevice = dev;
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
            {
                microphoneDevice = Microphone.devices[0];
            }
            activeMicrophoneDeviceForInfo = microphoneDevice;
        }
    }

    private void HandleTranslationResult(string translatedText)
    {
        if (llmhandler != null)
        {
            llmhandler.QuestionAboutCube(userRole + translatedText);
        }
    }

    [ContextMenu("Test STT")]
    private void TestSTT()
    {
        HandleFinalTranscription(testString, "en");
    }

    private void HandleFinalTranscription(string transcribedText, string language = "en")
    {
        Debug.Log($"[STT] Final transcription received: '{transcribedText}'.");

        if (llmhandler != null && !string.IsNullOrEmpty(transcribedText))
        {
            HandleTranslationResult(transcribedText);

            if (transcriptionText != null) transcriptionText.text = transcribedText;

            if (subtitle != null)
            {
                subtitle.text = transcribedText;
                subtitle.gameObject.SetActive(true);

                Color subtitleColor = subtitle.color;
                subtitleColor.a = 1f;
                subtitle.color = subtitleColor;

                if (_activeFadeCoroutine != null) StopCoroutine(_activeFadeCoroutine);
                _activeFadeCoroutine = StartCoroutine(FadeOutSubtitleAfterDelay());
            }
        }
    }

    private IEnumerator FadeOutSubtitleAfterDelay()
    {
        yield return new WaitForSeconds(displayTime);

        float currentTime = 0f;
        Color startColor = subtitle.color;

        while (currentTime < fadeOutDuration)
        {
            currentTime += Time.deltaTime;
            float alpha = Mathf.Lerp(startColor.a, 0f, currentTime / fadeOutDuration);
            subtitle.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null;
        }

        subtitle.color = new Color(startColor.r, startColor.g, startColor.b, 0f);
        subtitle.gameObject.SetActive(false);
        _activeFadeCoroutine = null;
    }

    public void RequestConnect()
    {
        if (!isEventsConnected && !isAudioConnected)
        {
            wantsToConnect = true;
        }
    }

    public async Task Disconnect()
    {
        wantsToConnect = false;

        List<Task> closeTasks = new List<Task>();
        if (eventsWs != null) closeTasks.Add(eventsWs.Close());
        if (audioWs != null) closeTasks.Add(audioWs.Close());

        try
        {
            await Task.WhenAll(closeTasks);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Exception closing sockets: {ex.Message}");
        }
        finally
        {
            eventsWs = null;
            audioWs = null;
            isEventsConnected = false;
            isAudioConnected = false;
            _onConnectedInvoked = false;
        }
    }

    public void Mute()
    {
        if (!isMuted)
        {
            isMuted = true;
        }
    }

    public void Unmute()
    {
        if (isMuted)
        {
            isMuted = false;
        }
    }

    public void NotifyTTSStarted() { isTTSPlaying = true; }
    public void NotifyTTSStopped() { isTTSPlaying = false; }

    private async Task Connect()
    {
        lastError = "";

        sessionId = System.Guid.NewGuid().ToString();
        eventsUrl = $"ws://{serverIP}:{serverPort}/ws/events/{sessionId}";
        audioUrl = $"ws://{serverIP}:{serverPort}/ws/audio/{sessionId}";

        eventsWs = new WebSocket(eventsUrl);
        eventsWs.OnOpen += () => { CheckOverallConnectionStatus(true, isAudioConnected); };
        eventsWs.OnError += (e) => { 
            isEventsConnected = false; 
            CheckOverallConnectionStatus(false, isAudioConnected); 
        };
        eventsWs.OnClose += (e) => { 
            isEventsConnected = false; 
            if (!isAudioConnected) OnDisconnected?.Invoke(); 
        };
        eventsWs.OnMessage += (bytes) => {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            HandleEventsMessage(message);
        };
        var evtTask = eventsWs.Connect();

        audioWs = new WebSocket(audioUrl);
        audioWs.OnOpen += () => { CheckOverallConnectionStatus(isEventsConnected, true); };
        audioWs.OnError += (e) => { 
            isAudioConnected = false; 
            CheckOverallConnectionStatus(isEventsConnected, false); 
        };
        audioWs.OnClose += (e) => { 
            isAudioConnected = false; 
            if (!isEventsConnected) OnDisconnected?.Invoke(); 
        };
        var audTask = audioWs.Connect();

        try
        {
            await Task.WhenAll(evtTask, audTask);
        }
        catch (Exception) { }

        if (eventsWs != null) isEventsConnected = eventsWs.State == WebSocketState.Open;
        if (audioWs != null) isAudioConnected = audioWs.State == WebSocketState.Open;
        CheckOverallConnectionStatus(isEventsConnected, isAudioConnected);
    }

    private void CheckOverallConnectionStatus(bool evtsConnected, bool audConnected)
    {
        bool previouslyConnected = isEventsConnected && isAudioConnected;
        isEventsConnected = evtsConnected;
        isAudioConnected = audConnected;
        bool currentlyConnected = isEventsConnected && isAudioConnected;

        if (currentlyConnected && !previouslyConnected && !_onConnectedInvoked)
        {
            Debug.Log("STTHandler connected successfully to Parakeet WS Server!");
            OnConnected?.Invoke();
            _onConnectedInvoked = true;
        }
        else if (!currentlyConnected && previouslyConnected)
        {
            Debug.Log("STTHandler disconnected from server.");
            OnDisconnected?.Invoke();
            _onConnectedInvoked = false;
        }
    }

    private void HandleEventsMessage(string message)
    {
        try
        {
            ParakeetServerEventMessage evt = JsonConvert.DeserializeObject<ParakeetServerEventMessage>(message);

            if (!string.IsNullOrEmpty(evt.vad))
            {
                if (evt.vad == "speech_start")
                {
                    Debug.Log("VAD: Speech Start Detected");
                    OnSpeechStartDetected?.Invoke();
                }
                else if (evt.vad == "speech_end")
                {
                    Debug.Log("VAD: Speech End Detected");
                    OnSpeechStopDetected?.Invoke();
                }
            }

            if (!string.IsNullOrEmpty(evt.text))
            {
                OnFinalTranscription?.Invoke(evt.text, "en");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse event message: {message}\nError: {e}");
        }
    }

    private void StopMicrophone()
    {
        if (!isRecording) return;
        isRecording = false;
    }

    private IEnumerator ConsumeAndSendAudio()
    {
        int chunkSamples = 512;
        List<short> sampleAccumulator = new List<short>();

        while (isRecording)
        {
            if (AECAudioProcessor.Instance != null)
            {
                while (AECAudioProcessor.Instance.OutputQueue.TryDequeue(out short[] chunk))
                {
                    if (chunk.Length > 0)
                    {
                        sampleAccumulator.AddRange(chunk);

                        while (sampleAccumulator.Count >= chunkSamples)
                        {
                            short[] sendChunk = sampleAccumulator.GetRange(0, chunkSamples).ToArray();
                            sampleAccumulator.RemoveRange(0, chunkSamples);
                            
                            if (!isMuted && isAudioConnected && audioWs != null && audioWs.State == WebSocketState.Open)
                            {
                                SendAudioChunk(sendChunk, chunkSamples);
                            }
                        }
                    }
                }
            }
            yield return null;
        }
    }

    private async void SendAudioChunk(short[] samples, int sampleCount)
    {
        if (!isAudioConnected || audioWs == null || audioWs.State != WebSocketState.Open || sampleCount == 0) return;

        byte[] pcmData = new byte[sampleCount * 2];
        Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);
        
        if (!BitConverter.IsLittleEndian)
        {
             for(int i=0; i<pcmData.Length; i+=2) {
                 byte temp = pcmData[i];
                 pcmData[i] = pcmData[i+1];
                 pcmData[i+1] = temp;
             }
        }
        
        try
        {
            await audioWs.Send(pcmData);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending audio: {e.Message}");
        }
    }
}
