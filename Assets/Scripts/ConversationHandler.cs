using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ConversationHandler : MonoBehaviour
{
    [Header("Scripts")]
    public STTHandler sttHandler;
    public LLMHandler llmHandler;
    public TTSHandler ttsHandler;

    [Header("Settings")]
    public bool allowInterruptions = false;
    public string userRoleName = "Customer";

    [Header("Conversation")]
    public AudioSource audioSource;

    public Image statusIndicator; // UI element to show status
    private Coroutine conversationRoutine;
    private bool isAudioPlaying = false;

    [Tooltip("Play a short test tone at startup. Off by default; the 'Test Sound' button plays it on demand. " +
        "If the first spoken reply ever gets clipped on some audio device, turning this on warms the output up.")]
    public bool playStartupBeep = false;

    private AudioClip testTone;

    /// <summary>Plays a short, quiet 440 Hz tone through the conversation audio source to check the speakers.</summary>
    public void PlayTestSound()
    {
        AudioSource source = GetComponent<AudioSource>();
        if (source == null) source = audioSource;
        if (source == null) return;

        if (testTone == null)
        {
            const int sampleRate = 44100;
            int samples = sampleRate / 2;               // half a second
            int fade = sampleRate / 50;                 // 20 ms fades so it doesn't click
            testTone = AudioClip.Create("test tone", samples, 1, sampleRate, false);
            float[] data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float envelope = Mathf.Min(1f, Mathf.Min(i, samples - 1 - i) / (float)fade);
                data[i] = 0.25f * envelope * Mathf.Sin(2f * Mathf.PI * 440f * i / sampleRate);
            }
            testTone.SetData(data, 0);
        }

        source.PlayOneShot(testTone);
    }

    // Start is called before the first frame update
    void Start()
    {
        if (playStartupBeep) PlayTestSound();
        // sttHandler.SetUserRoleName(userRoleName);
        AECAudioProcessor.Instance.ProcessAEC = allowInterruptions;

        if (sttHandler != null)
        {
            sttHandler.OnSpeechStartDetected.AddListener(HandleVoiceInterruption);
        }
    }


    void OnGUI()
    {
        // Four rows in the top-right corner; the Debug Tools menu sits right below them.
        const float width = 176f, rowH = 22f, gap = 4f;
        Matrix4x4 unscaled = DebugUi.Begin();
        float x = DebugUi.Width - width - 6f;
        float y = 6f;
        DebugUi.ConversationButtons = new Rect(x, y, width, 4 * rowH + 3 * gap);

        if (DebugUi.Button(new Rect(x, y, width, rowH), "Start conversation")) StartConversation();
        y += rowH + gap;
        if (DebugUi.Button(new Rect(x, y, width, rowH), "Stop conversation")) StopConversation();
        y += rowH + gap;
        if (sttHandler != null)
        {
            if (DebugUi.Toggle(new Rect(x, y, width, rowH), "Mic muted", sttHandler.IsUserMuted)) sttHandler.ToggleUserMute();
        }
        y += rowH + gap;
        if (ttsHandler != null)
        {
            if (DebugUi.Toggle(new Rect(x, y, width, rowH), "Voice muted", ttsHandler.IsVoiceMuted)) ttsHandler.ToggleVoiceMute();
        }
        DebugUi.End(unscaled);
    }

    public void TriggerConversation()
    {
        if (conversationRoutine == null)
        {
            llmHandler.QuestionAboutCube("System: Start the Conversation");
            StartConversation();
            statusIndicator.color = Color.blue; 
        }
        else
        {
            StopConversation();
            statusIndicator.color = Color.white; 
        }
    }

    public void RedoLastRequest()
    {
        StopConversation();
        StartConversation();
        llmHandler.ReTry();

    }

    public void ToggleInterruptions()
    {
        allowInterruptions = !allowInterruptions;
        AECAudioProcessor.Instance.ProcessAEC = allowInterruptions;
    }
    private void StartConversation()
    {
        if ((sttHandler == null) || (llmHandler == null) || (audioSource == null) || conversationRoutine != null) return;

        conversationRoutine = StartCoroutine(ListeningRoutine());
    }

    private void StopConversation()
    {
        if (conversationRoutine != null)
        {
            StopCoroutine(conversationRoutine);
            conversationRoutine = null;
        }

        // _ = sttHandler.Disconnect();
        sttHandler.Mute();

        try
        {
            if (llmHandler != null)
            {
                llmHandler.CancelReply();
                // Reset conversation history here so it doesn't grow unbounded across the whole
                // session - a long-lived history makes every future generation progressively
                // slower, since each new token has to attend over an ever-larger context.
                if (llmHandler.llmCharacter != null) _ = llmHandler.llmCharacter.ClearHistory();
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error cancelling LLM Handler reply: {ex.Message}");
        }

        // llmHandler.CancelReply();
        ttsHandler.CancelSpeak();

    }

    public void HandleVoiceInterruption()
    {
        if (!allowInterruptions) return;

        Debug.Log("Voice interruption detected.");
        if (llmHandler != null) llmHandler.CancelReply();
        if (ttsHandler != null) ttsHandler.CancelSpeak();
    }

    string GetTimeOfDay()
    {
        System.DateTime now = System.DateTime.Now;
        int hour = now.Hour;

        if (hour >= 5 && hour < 12) return "Morning";
        else if (hour >= 12 && hour < 17) return "Afternoon";
        else if (hour >= 17 && hour < 21) return "Evening";
        else return "Night";
    }

    string GetGreeting(string timeOfDay, string yourName, string myName, string template)
    {
        string result = string.Format("Good {0}, {1}. I'm {2}. {3}", timeOfDay, yourName, myName, template);

        return result;
    }

    private IEnumerator ListeningRoutine()
    {
        while (true)
        {
            if (allowInterruptions)
            {
               // If interruptions are allowed, STT should generally always be recording.
               // The interruption is handled when STT detects speech while TTS is active.
               sttHandler.Unmute();
            }
            else
            {
                if (audioSource.isPlaying || llmHandler.waitingForReply || ttsHandler.waitingForAudioSynthesize())
                {
                    sttHandler.Mute();
                }
                else
                {
                    sttHandler.Unmute();
                }
            }

            yield return null;
        }
    }
}