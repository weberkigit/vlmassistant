using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public enum STTServerType
{
    Parakeet,
    Whisper
}

[RequireComponent(typeof(ParakeetSTTHandler))]
[RequireComponent(typeof(WhisperHandler))]
public class STTHandler : MonoBehaviour
{
    [Header("Server Type Selection")]
    public STTServerType activeServerType = STTServerType.Parakeet;

    [Header("Connection Settings")]
    public string serverIP = "127.0.0.1";

    [Header("Common References")]
    public LLMHandler llmhandler;
    [Tooltip("How long the subtitle stays fully visible")]
    public float displayTime = 3f;
    public float fadeOutDuration = 0.5f;
    public TMP_Text transcriptionText;
    public TMP_Text subtitle;
    public Image statusIndicator;

    public bool lastServerCheckResult = false; 

    [Header("Audio Settings")]
    public int recordingFrequency = 16000;
    public int recordingLengthSec = 1;
    public string userRole = "";

    [Header("Events")]
    public UnityEvent OnConnected;
    public UnityEvent OnDisconnected;

    [System.Serializable]
    public class StringEvent : UnityEvent<string> { }
    [System.Serializable]
    public class TranscriptionEvent : UnityEvent<string, string> { }

    public StringEvent OnErrorReceived;
    public StringEvent OnRealtimeTranscriptionUpdate;
    public TranscriptionEvent OnFinalTranscription;
    public UnityEvent OnRecordingStarted;
    public UnityEvent OnRecordingStopped;

    [Header("VAD Events")]
    public UnityEvent OnSpeechStartDetected;
    public UnityEvent OnSpeechStopDetected;

    public class ServerCheckResultEvent : UnityEvent<bool> { }
    public ServerCheckResultEvent OnServerCheckCompleted;

    public UnityEvent OnInterruptionDetected;

    public string selectedMicrophoneDeviceName
    {
        get 
        {
            if (activeServerType == STTServerType.Parakeet && parakeetHandler != null) return parakeetHandler.selectedMicrophoneDeviceName;
            if (activeServerType == STTServerType.Whisper && whisperHandler != null) return whisperHandler.selectedMicrophoneDeviceName;
            return "";
        }
    }

    // Internal References to Handlers
    [HideInInspector] public ParakeetSTTHandler parakeetHandler;
    [HideInInspector] public WhisperHandler whisperHandler;

    private void Awake()
    {
        parakeetHandler = GetComponent<ParakeetSTTHandler>();
        whisperHandler = GetComponent<WhisperHandler>();

        // Ensure both handlers are initialized but we will only enable one
        SyncSettingsToHandlers();
        RegisterEvents();

        if (activeServerType == STTServerType.Parakeet)
        {
            parakeetHandler.enabled = true;
            whisperHandler.enabled = false;
        }
        else
        {
            parakeetHandler.enabled = false;
            whisperHandler.enabled = true;
        }
    }

    void Start()
    {
        RequestConnect();
    }
    
    public void SetUserRoleName(string roleName)
    {
        userRole = roleName + ": ";
        if (parakeetHandler != null) parakeetHandler.SetUserRoleName(roleName);
        if (whisperHandler != null) whisperHandler.SetUserRoleName(roleName);
    }

    public void Connected()
    {
        lastServerCheckResult = true;
    }

    private void SyncSettingsToHandlers()
    {
        // Parakeet sync
        parakeetHandler.llmhandler = llmhandler;
        parakeetHandler.serverIP = serverIP;
        parakeetHandler.displayTime = displayTime;
        parakeetHandler.fadeOutDuration = fadeOutDuration;
        parakeetHandler.transcriptionText = transcriptionText;
        parakeetHandler.subtitle = subtitle;
        parakeetHandler.recordingFrequency = recordingFrequency;
        parakeetHandler.recordingLengthSec = recordingLengthSec;
        parakeetHandler.userRole = userRole;

        // Whisper sync
        whisperHandler.llmhandler = llmhandler;
        whisperHandler.serverIPs = new List<string> { serverIP };
        whisperHandler.selectedIPIndex = 0;
        whisperHandler.displayTime = displayTime;
        whisperHandler.fadeOutDuration = fadeOutDuration;
        whisperHandler.transcriptionText = transcriptionText;
        whisperHandler.subtitle = subtitle;
        whisperHandler.recordingFrequency = recordingFrequency;
        whisperHandler.recordingLengthSec = recordingLengthSec;
        whisperHandler.userRole = userRole;
    }

    private void RegisterEvents()
    {
        // Connection status tracking
        OnConnected?.AddListener(() => {
            lastServerCheckResult = true;
            if (statusIndicator != null) statusIndicator.color = ServerStatusColors.Connected;
        });
        OnDisconnected?.AddListener(() => {
            lastServerCheckResult = false;
            if (statusIndicator != null) statusIndicator.color = ServerStatusColors.Disconnected;
        });

        // Parakeet events
        parakeetHandler.OnConnected?.AddListener(() => OnConnected?.Invoke());
        parakeetHandler.OnDisconnected?.AddListener(() => OnDisconnected?.Invoke());
        parakeetHandler.OnFinalTranscription?.AddListener((text, lang) => OnFinalTranscription?.Invoke(text, lang));
        parakeetHandler.OnSpeechStartDetected?.AddListener(() => OnSpeechStartDetected?.Invoke());
        parakeetHandler.OnSpeechStopDetected?.AddListener(() => OnSpeechStopDetected?.Invoke());

        // Whisper events
        whisperHandler.OnConnected?.AddListener(() => OnConnected?.Invoke());
        whisperHandler.OnDisconnected?.AddListener(() => OnDisconnected?.Invoke());
        whisperHandler.OnErrorReceived?.AddListener((error) => OnErrorReceived?.Invoke(error));
        whisperHandler.OnRealtimeTranscriptionUpdate?.AddListener((text) => OnRealtimeTranscriptionUpdate?.Invoke(text));
        whisperHandler.OnFinalTranscription?.AddListener((text, lang) => OnFinalTranscription?.Invoke(text, lang));
        whisperHandler.OnRecordingStarted?.AddListener(() => OnRecordingStarted?.Invoke());
        whisperHandler.OnRecordingStopped?.AddListener(() => OnRecordingStopped?.Invoke());
        whisperHandler.OnSpeechStartDetected?.AddListener(() => OnSpeechStartDetected?.Invoke());
        whisperHandler.OnSpeechStopDetected?.AddListener(() => OnSpeechStopDetected?.Invoke());
        whisperHandler.OnServerCheckCompleted?.AddListener((result) => {
            lastServerCheckResult = result;
            if (statusIndicator != null) statusIndicator.color = result ? ServerStatusColors.Connected : ServerStatusColors.Disconnected;
            OnServerCheckCompleted?.Invoke(result);
        });
        whisperHandler.OnInterruptionDetected?.AddListener(() => OnInterruptionDetected?.Invoke());
    }

    // --- Public API for other scripts ---

    public void RequestConnect()
    {
        if (activeServerType == STTServerType.Parakeet) parakeetHandler.RequestConnect();
        else whisperHandler.RequestConnect();
    }

    public async Task Disconnect()
    {
        if (activeServerType == STTServerType.Parakeet) await parakeetHandler.Disconnect();
        else await whisperHandler.Disconnect();
    }

    public void Mute()
    {
        if (activeServerType == STTServerType.Parakeet) parakeetHandler.Mute();
        else whisperHandler.Mute();
    }

    public void Unmute()
    {
        // A manual mute from the UI wins over the automatic unmuting done by ConversationHandler's
        // listening loop, which would otherwise re-open the mic on the very next frame.
        if (IsUserMuted) return;

        if (activeServerType == STTServerType.Parakeet) parakeetHandler.Unmute();
        else whisperHandler.Unmute();
    }

    /// <summary>True while the user has muted the microphone via the UI button.</summary>
    public bool IsUserMuted { get; private set; }

    public void SetUserMuted(bool muted)
    {
        IsUserMuted = muted;
        if (muted) Mute();
    }

    public void ToggleUserMute() => SetUserMuted(!IsUserMuted);

    public void NotifyTTSStarted()
    {
        if (activeServerType == STTServerType.Parakeet) parakeetHandler.NotifyTTSStarted();
        else whisperHandler.NotifyTTSStarted();
    }

    public void NotifyTTSStopped()
    {
        if (activeServerType == STTServerType.Parakeet) parakeetHandler.NotifyTTSStopped();
        else whisperHandler.NotifyTTSStopped();
    }

    public string GetCurrentMicrophoneDevice()
    {
        if (activeServerType == STTServerType.Parakeet) return parakeetHandler.GetCurrentMicrophoneDevice();
        else return whisperHandler.GetCurrentMicrophoneDevice();
    }

    public void SetParameter(string parameterName, object value)
    {
        if (activeServerType == STTServerType.Whisper) whisperHandler.SetParameter(parameterName, value);
    }

    public async Task<object> GetParameter(string parameterName, float timeoutSeconds = 5.0f)
    {
        if (activeServerType == STTServerType.Whisper) return await whisperHandler.GetParameter(parameterName, timeoutSeconds);
        return null;
    }

    public void CallMethod(string methodName, List<object> args = null, Dictionary<string, object> kwargs = null)
    {
        if (activeServerType == STTServerType.Whisper) whisperHandler.CallMethod(methodName, args, kwargs);
    }
}