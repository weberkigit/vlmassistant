using NativeWebSocket;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// --- Data Structures for JSON communication ---
// Based on messages observed in the python script

[Serializable]
public class ControlCommand
{
    public string command;
    public string parameter;
    public object value; // Can be string, int, float, bool etc.
    public int request_id = -1; // Optional for get_parameter
    public string method;
    public List<object> args;
    public Dictionary<string, object> kwargs;
}

[Serializable]
public class ServerControlResponse
{
    public string status; // "success" or "error"
    public string message; // Error message
    public string parameter;
    public object value;
    public int request_id = -1;
}

[Serializable]
public class ServerDataMessage
{
    public string type; // "realtime", "fullSentence", "recording_start", etc.
    public string text;
    public string audio_bytes_base64; // For transcription_start
    public string language; // Language of the transcription
    // Add other fields as needed based on server messages
}

[Serializable]
public class AudioMetadata
{
    public int sampleRate;
}

public class WhisperHandler : MonoBehaviour
{
    [Header("LLM")]
    public LLMHandler llmhandler;
    public string testString = "";

    public string userRole = "";

    [Header("Connection Settings")]
    public List<string> serverIPs = new List<string> { "127.0.0.1", "10.23.0.207" }; // Default IPs
    public int selectedIPIndex = 0; // Index for the list above
    public string controlPort = "8011"; // Control port
    public string dataPort = "8012"; // Data port
    private string controlUrl = "ws://127.0.0.1:8011"; //
    private string dataUrl = "ws://127.0.0.1:8012"; //

    [Header("Audio Settings")]
    public int recordingFrequency = 16000; // Target frequency
    public int recordingLengthSec = 1; // How often to process audio chunks

    [HideInInspector]
    public string selectedMicrophoneDeviceName = "";

    [Tooltip("The microphone device that will be used (selected via dropdown).")]
    [SerializeField]
    private string activeMicrophoneDeviceForInfo = "";

    [Header("Status")]
    public bool isControlConnected = false;
    public bool isDataConnected = false;
    public bool isRecording = false;
    public string lastError = "";
    public bool lastServerCheckResult = false; 
    public bool isCheckingServer = false; 

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
    public class StringEvent : UnityEngine.Events.UnityEvent<string> { }
    [System.Serializable]
    public class TranscriptionEvent : UnityEngine.Events.UnityEvent<string, string> { }
    public StringEvent OnErrorReceived;
    public StringEvent OnRealtimeTranscriptionUpdate;
    public TranscriptionEvent OnFinalTranscription;
    public UnityEngine.Events.UnityEvent OnRecordingStarted; 
    public UnityEngine.Events.UnityEvent OnRecordingStopped; 

    [Header("VAD Events")]
    public UnityEngine.Events.UnityEvent OnSpeechStartDetected; 
    public UnityEngine.Events.UnityEvent OnSpeechStopDetected;  

    public class ServerCheckResultEvent : UnityEngine.Events.UnityEvent<bool> { }
    public ServerCheckResultEvent OnServerCheckCompleted; 

    public UnityEngine.Events.UnityEvent OnInterruptionDetected; 

    private WebSocket controlWs;
    private WebSocket dataWs;

    private string microphoneDevice;
    private bool wantsToConnect = false;

    // For GetParameter responses
    private Dictionary<int, TaskCompletionSource<object>> pendingParameterRequests = new Dictionary<int, TaskCompletionSource<object>>();
    private int requestCounter = 0;
    private bool hasStarted = false; // Track if Start() has run

    // --- Unity Lifecycle Methods ---
    void Awake()
    {
        activeMicrophoneDeviceForInfo = selectedMicrophoneDeviceName;
    }

    void Start()
    {
        UpdateConnectionUrls(); 
        PerformServerCheck();

        InitializeMicrophoneAndStart();

        if (OnInterruptionDetected == null) OnInterruptionDetected = new UnityEngine.Events.UnityEvent();
        if (OnFinalTranscription == null) OnFinalTranscription = new TranscriptionEvent();
        OnFinalTranscription.AddListener(HandleFinalTranscription);

        hasStarted = true;
    }

    void OnEnable()
    {
        if (hasStarted && Application.isPlaying)
        {
            Debug.Log("[WhisperHandler] OnEnable: Re-initializing after reactivation.");
            UpdateConnectionUrls();
            PerformServerCheck();

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
            Debug.Log("[WhisperHandler] OnDisable: Disconnecting and stopping recording.");
            isRecording = false;
            _ = Disconnect();
        }
    }

    async void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (controlWs != null)
        {
            controlWs.DispatchMessageQueue();
        }
        if (dataWs != null)
        {
            dataWs.DispatchMessageQueue();
        }
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

    private void UpdateConnectionUrls()
    {
        if (serverIPs == null || serverIPs.Count == 0)
        {
            if (string.IsNullOrEmpty(controlUrl))
                Debug.LogError("Server IP list is empty!");
            controlUrl = "";
            dataUrl = "";
            return;
        }

        selectedIPIndex = Mathf.Clamp(selectedIPIndex, 0, serverIPs.Count - 1);

        string selectedIP = serverIPs[selectedIPIndex];
        string newControlUrl = $"ws://{selectedIP}:{controlPort}";
        string newDataUrl = $"ws://{selectedIP}:{dataPort}";

        if (controlUrl != newControlUrl)
        {
            controlUrl = newControlUrl;
            Debug.Log($"Control URL set to: {controlUrl}");
        }
        if (dataUrl != newDataUrl)
        {
            dataUrl = newDataUrl;
            Debug.Log($"Data URL set to: {dataUrl}");
        }
    }

    private void InitializeMicrophoneAndStart()
    {
        // Microphone is now handled by AECAudioProcessor
        Debug.Log("[WhisperHandler] Audio input is managed by AECAudioProcessor.");
        
        if (!isRecording)
        {
            isRecording = true;
            StartCoroutine(ConsumeAndSendAudio());
        }

        // Just cache the device for display
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

    [ContextMenu("Test Server Check")]
    private void PerformServerCheck()
    {
        TriggerServerCheck(3.0f);
    }

    public void TriggerServerCheck(float timeoutSeconds = 3.0f)
    {
        if (!isCheckingServer) 
        {
            StartCoroutine(PerformServerCheckCoroutine(timeoutSeconds));
        }
        else
        {
            Debug.LogWarning("Server check already in progress.");
        }
    }

    private IEnumerator PerformServerCheckCoroutine(float timeoutSeconds = 3.0f)
    {
        isCheckingServer = true;
        Debug.Log($"Performing server sanity check for {controlUrl}...");

        Task<bool> checkTask = CheckServerStatus(timeoutSeconds);
        yield return new WaitUntil(() => checkTask.IsCompleted);

        bool result = false;
        if (checkTask.IsFaulted)
        {
            Debug.LogError($"Server check failed with exception: {checkTask.Exception?.InnerException?.Message ?? checkTask.Exception?.Message}");
            result = false;
        }
        else if (checkTask.IsCanceled)
        {
            Debug.LogWarning("Server check was cancelled (likely timeout).");
            result = false;
        }
        else
        {
            result = checkTask.Result;
            Debug.Log($"STT Server check completed. STT Server is {(result ? "reachable" : "NOT reachable")}.");
        }

        lastServerCheckResult = result;
        isCheckingServer = false;
        OnServerCheckCompleted?.Invoke(result); 
    }

    public async Task<bool> CheckServerStatus(float timeoutSeconds = 3.0f)
    {
        if (string.IsNullOrEmpty(controlUrl))
        {
            Debug.LogError("Cannot check server status: Control URL is not set (check IP list).");
            return false;
        }

        WebSocket tempWs = null; 
        try
        {
            tempWs = new WebSocket(controlUrl);

            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false); 

            tempWs.OnOpen += () =>
            {
                Debug.Log("Sanity Check: Connection opened.");
                tempWs?.Close(); 
                tcs.TrySetResult(true); 
            };

            tempWs.OnError += (e) =>
            {
                Debug.LogError($"Sanity Check Error: {e}");
                tcs.TrySetResult(false); 
            };

            tempWs.OnClose += (e) =>
            {
                Debug.Log($"Sanity Check: Connection closed ({e}).");
                tcs.TrySetResult(false); 
            };

            Debug.Log($"Sanity Check: Attempting temporary connection to {controlUrl}...");
            await tempWs.Connect(); 

            bool success = await tcs.Task; 
            return success;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Sanity Check Exception: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempWs != null && tempWs.State == WebSocketState.Open)
            {
                await tempWs.Close(); 
            }
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
        HandleFinalTranscription(testString);
    }

    private void HandleFinalTranscription(string transcribedText, string language = "en")
    {
        Debug.Log($"Final transcription received: '{language}': '{transcribedText}'. Sending to LLM Handler.");

        if (llmhandler != null)
        {
            if (!string.IsNullOrEmpty(transcribedText))
            {
                HandleTranslationResult(transcribedText); 
                
                if (transcriptionText != null)
                {
                    transcriptionText.text = transcribedText; 
                }

                if (subtitle != null)
                {
                    subtitle.text = transcribedText; 
                    subtitle.gameObject.SetActive(true); 

                    Color subtitleColor = subtitle.color;
                    subtitleColor.a = 1f; 
                    subtitle.color = subtitleColor;

                    if (_activeFadeCoroutine != null)
                    {
                        StopCoroutine(_activeFadeCoroutine);
                    }

                    _activeFadeCoroutine = StartCoroutine(FadeOutSubtitleAfterDelay());
                }
            }
            else
            {
                Debug.Log("Skipping LLM call because transcribed text is empty.");
            }
        }
        else
        {
            Debug.LogError("Cannot send transcription to LLM: LlmHandler reference is not set!", this);
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

    // --- Public Control Methods ---

    public void RequestConnect()
    {
        if (!isControlConnected && !isDataConnected)
        {
            wantsToConnect = true;
        }
        else
        {
            Debug.LogWarning("Already connected or connecting.");
        }
    }

    public async Task Disconnect()
    {
        wantsToConnect = false;

        List<Task> closeTasks = new List<Task>();
        if (controlWs != null)
        {
            Debug.Log("Closing Control WebSocket...");
            closeTasks.Add(controlWs.Close());
        }
        if (dataWs != null)
        {
            Debug.Log("Closing Data WebSocket...");
            closeTasks.Add(dataWs.Close());
        }

        try
        {
            await Task.WhenAll(closeTasks);
            Debug.Log("WebSocket closing tasks completed.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Exception during Task.WhenAll for closing sockets: {ex.Message}");
        }
        finally
        {
            controlWs = null;
            dataWs = null;
            isControlConnected = false;
            isDataConnected = false;
            _onConnectedInvoked = false; 

            Debug.Log("Disconnect process finished.");
        }
    }

    public void Mute()
    {
        if (!isMuted)
        {
            Debug.Log("Microphone Muted.");
            isMuted = true;
        }
    }

    public void Unmute()
    {
        if (isMuted)
        {
            Debug.Log("Microphone Unmuted. Sending audio data.");
            isMuted = false;
        }
    }

    public void SetParameter(string parameterName, object value)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot set parameter.");
            return;
        }

        ControlCommand cmd = new ControlCommand
        {
            command = "set_parameter",
            parameter = parameterName,
            value = value
        };
        string jsonCmd = JsonUtility.ToJson(cmd);
        Debug.Log($"Sending SetParameter: {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    public async Task<object> GetParameter(string parameterName, float timeoutSeconds = 5.0f)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot get parameter.");
            return null;
        }

        int requestId = Interlocked.Increment(ref requestCounter);
        var tcs = new TaskCompletionSource<object>();
        pendingParameterRequests[requestId] = tcs;

        ControlCommand cmd = new ControlCommand
        {
            command = "get_parameter",
            parameter = parameterName,
            request_id = requestId
        };
        string jsonCmd = JsonUtility.ToJson(cmd);
        Debug.Log($"Sending GetParameter (ID: {requestId}): {jsonCmd}");
        await controlWs.SendText(jsonCmd); 

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == tcs.Task)
        {
            pendingParameterRequests.Remove(requestId);
            return await tcs.Task; 
        }
        else
        {
            Debug.LogError($"Timeout waiting for GetParameter response for '{parameterName}' (ID: {requestId}).");
            pendingParameterRequests.Remove(requestId);
            return null; 
        }
    }

    public void CallMethod(string methodName, List<object> args = null, Dictionary<string, object> kwargs = null)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot call method.");
            return;
        }

        ControlCommand cmd = new ControlCommand
        {
            command = "call_method",
            method = methodName,
            args = args ?? new List<object>(),
            kwargs = kwargs ?? new Dictionary<string, object>()
        };

        string jsonCmd = JsonConvert.SerializeObject(cmd, Formatting.None,
                                     new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        Debug.Log($"Sending CallMethod: {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    public void NotifyTTSStarted()
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogWarning("Cannot notify TTS started: Control WebSocket not connected.");
            return;
        }

        isTTSPlaying = true;
        Debug.Log("STTHandler: Notifying server that TTS has started.");

        var ttsStateCommand = new { command = "set_tts_state", active = true };
        string jsonCmd = JsonConvert.SerializeObject(ttsStateCommand);

        Debug.Log($"Sending TTS State (Active: true): {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    public void NotifyTTSStopped()
    {
        isTTSPlaying = false;
        
        if (isControlConnected && controlWs != null && controlWs.State == WebSocketState.Open)
        {
            Debug.Log("STTHandler: Notifying server that TTS has stopped.");
            var ttsStateCommand = new { command = "set_tts_state", active = false };
            string jsonCmd = JsonConvert.SerializeObject(ttsStateCommand);

            Debug.Log($"Sending TTS State (Active: false): {jsonCmd}");
            controlWs.SendText(jsonCmd);
        }
    }


    private async Task Connect()
    {
        lastError = "";

        // --- Setup and Initiate Control WebSocket Connection ---
        controlWs = new WebSocket(controlUrl);

        controlWs.OnOpen += () =>
        {
            Debug.Log("Control WebSocket connection opened.");
            CheckOverallConnectionStatus(true, isDataConnected);
        };

        controlWs.OnError += (e) =>
        {
            Debug.LogError($"Control WebSocket error: {e}");
            lastError = $"Control WS Error: {e}";
            OnErrorReceived?.Invoke(lastError);
            isControlConnected = false; 
            CheckOverallConnectionStatus(false, isDataConnected); 
        };

        controlWs.OnClose += (e) =>
        {
            if (controlWs != null)
            {
                Debug.Log($"Control WebSocket connection closed: {e}");
                isControlConnected = false;
                if (!isDataConnected)
                {
                    OnDisconnected?.Invoke();
                }
            }
        };

        controlWs.OnMessage += (bytes) =>
        {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            HandleControlMessage(message);
        };

        Debug.Log("Attempting to connect Control WebSocket...");
        var controlConnectTask = controlWs.Connect();

        // --- Setup and Initiate Data WebSocket Connection ---
        dataWs = new WebSocket(dataUrl);

        dataWs.OnOpen += () =>
        {
            Debug.Log("Data WebSocket connection opened.");
            CheckOverallConnectionStatus(isControlConnected, true);
        };

        dataWs.OnError += (e) =>
        {
            Debug.LogError($"Data WebSocket error: {e}");
            lastError = $"Data WS Error: {e}";
            OnErrorReceived?.Invoke(lastError);
            isDataConnected = false; 
            CheckOverallConnectionStatus(isControlConnected, false); 
        };

        dataWs.OnClose += (e) =>
        {
            if (dataWs != null)
            {
                Debug.Log($"Data WebSocket connection closed: {e}");
                isDataConnected = false;
                if (!isControlConnected)
                {
                    OnDisconnected?.Invoke();
                }
            }
        };

        dataWs.OnMessage += (bytes) =>
        {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            HandleDataMessage(message);
        };

        Debug.Log("Attempting to connect Data WebSocket...");
        var dataConnectTask = dataWs.Connect();

        try
        {
            await Task.WhenAll(controlConnectTask, dataConnectTask);
            Debug.Log("Initial connection attempts completed.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Exception during Task.WhenAll for connection attempts: {ex.Message}");
        }

        if (controlWs != null) isControlConnected = controlWs.State == WebSocketState.Open;
        if (dataWs != null) isDataConnected = dataWs.State == WebSocketState.Open;

        CheckOverallConnectionStatus(isControlConnected, isDataConnected);
    }

    private bool _onConnectedInvoked = false; 

    private void CheckOverallConnectionStatus(bool ctrlConnected, bool dataConnected)
    {
        bool previouslyConnected = isControlConnected && isDataConnected;

        isControlConnected = ctrlConnected;
        isDataConnected = dataConnected;

        bool currentlyConnected = isControlConnected && isDataConnected;

        if (currentlyConnected && !previouslyConnected && !_onConnectedInvoked)
        {
            Debug.Log("Both WebSockets connected successfully.");
            OnConnected?.Invoke();
            _onConnectedInvoked = true; 
        }
        else if (!currentlyConnected && previouslyConnected)
        {
            Debug.Log("A WebSocket disconnected, overall status is now disconnected.");
            OnDisconnected?.Invoke();
            _onConnectedInvoked = false; 
        }
    }


    private void HandleControlMessage(string message)
    {
        try
        {
            ServerControlResponse response = JsonConvert.DeserializeObject<ServerControlResponse>(message);

            if (response.request_id >= 0 && pendingParameterRequests.ContainsKey(response.request_id))
            {
                if (response.status == "success")
                {
                    Debug.Log($"Received response for GetParameter '{response.parameter}' (ID: {response.request_id}): {response.value}");
                    pendingParameterRequests[response.request_id].TrySetResult(response.value);
                }
                else
                {
                    Debug.LogError($"Server error for GetParameter '{response.parameter}' (ID: {response.request_id}): {response.message}");
                    pendingParameterRequests[response.request_id].TrySetException(new Exception(response.message ?? "Unknown server error"));
                }
            }
            else if (response.status == "success")
            {
                Debug.Log($"Server success: Parameter '{response.parameter}' set or method called.");
            }
            else if (response.status == "error")
            {
                Debug.LogError($"Server error: {response.message}");
                lastError = $"Server Error: {response.message}";
                OnErrorReceived?.Invoke(lastError);
            }
            else
            {
                Debug.LogWarning($"Received unknown control message: {message}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse control message: {message}\nError: {e}");
        }
    }

    private void HandleDataMessage(string message)
    {
        try
        {
            ServerDataMessage dataMessage = JsonConvert.DeserializeObject<ServerDataMessage>(message);

            switch (dataMessage.type)
            {
                case "realtime":
                    OnRealtimeTranscriptionUpdate?.Invoke(dataMessage.text);
                    break;
                case "fullSentence":
                    Debug.Log($"Final: {dataMessage.text}");
                    OnFinalTranscription?.Invoke(dataMessage.text, dataMessage.language);
                    break;
                case "recording_start":
                    Debug.Log("Server indicated recording started.");
                    OnRecordingStarted?.Invoke();
                    break;
                case "recording_stop":
                    Debug.Log("Server indicated recording stopped.");
                    OnRecordingStopped?.Invoke();
                    break;
                case "vad_detect_start":
                    Debug.Log("User stopped talking. VAD Start detecting for next speech");
                    OnSpeechStopDetected?.Invoke();
                    break;
                case "vad_detect_stop":
                    Debug.Log("User started talking. VAD Stop detecting");
                    OnSpeechStartDetected?.Invoke();
                    break;
                case "interruption_detected": 
                    Debug.LogWarning("STTHandler: Interruption detected by server!");
                    OnInterruptionDetected?.Invoke();
                    isTTSPlaying = false; 
                    break;

                default:
                    //Debug.LogWarning($"Received unknown data message type: {dataMessage.type} - {message}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse data message: {message}\nError: {e}");
        }
    }

    private void StartMicrophone()
    {
        if (isRecording) return;
        Debug.Log("[WhisperHandler] Resuming audio consumption.");
        isRecording = true;
        StartCoroutine(ConsumeAndSendAudio());
    }

    private void StopMicrophone()
    {
        if (!isRecording) return;
        Debug.Log("[WhisperHandler] Pausing audio consumption.");
        isRecording = false;
    }

    private IEnumerator ConsumeAndSendAudio()
    {
        while (isRecording)
        {
            if (AECAudioProcessor.Instance != null)
            {
                while (AECAudioProcessor.Instance.OutputQueue.TryDequeue(out short[] chunk))
                {
                    if (!isMuted && isDataConnected && dataWs != null && dataWs.State == WebSocketState.Open && chunk.Length > 0)
                    {
                        SendAudioChunk(chunk, chunk.Length);
                    }
                }
            }
            yield return null;
        }
    }

    private async void SendAudioChunk(short[] samples, int sampleCount)
    {
        if (!isDataConnected || dataWs == null || dataWs.State != WebSocketState.Open || sampleCount == 0)
        {
            return;
        }

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
        
        AudioMetadata metadata = new AudioMetadata { sampleRate = this.recordingFrequency };
        string metadataJson = JsonUtility.ToJson(metadata);
        byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

        int metadataLength = metadataBytes.Length;
        byte[] lengthBytes = BitConverter.GetBytes(metadataLength);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes); 
        }

        byte[] messageBytes = new byte[lengthBytes.Length + metadataBytes.Length + pcmData.Length];
        Buffer.BlockCopy(lengthBytes, 0, messageBytes, 0, lengthBytes.Length);
        Buffer.BlockCopy(metadataBytes, 0, messageBytes, lengthBytes.Length, metadataBytes.Length);
        Buffer.BlockCopy(pcmData, 0, messageBytes, lengthBytes.Length + metadataBytes.Length, pcmData.Length);

        if (dataWs != null && dataWs.State == WebSocketState.Open)
        {
            try
            {
                await dataWs.Send(messageBytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error sending audio data: {e.Message}");
                lastError = $"Send Error: {e.Message}";
                OnErrorReceived?.Invoke(lastError);
            }
        }
    }
}
