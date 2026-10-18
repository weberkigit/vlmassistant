using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using App.Audio;

public class TTSHandler : MonoBehaviour
{
    public enum TTSOption
    {
        KokoroTTS,
        ElevenLabsTTS
    }

    [Header("Select Local TTS")]
    public TTSOption selectedTTS = TTSOption.KokoroTTS;
    public UnityEngine.UI.Image statusIndicator;

    [Header("TTS Speakers")]
    public KokoroTTSSpeaker kokoroTTSSpeaker;
    public ElevenLabsTTS elevenLabsTTS;

    [Header("Scripts")]
    [Tooltip("Notified when TTS starts/stops speaking so STT can mute/ignore its own voice.")]
    public STTHandler sttHandler;

    private ITTSSpeaker TSSpeaker;
    private Coroutine ttsStateCoroutine;

    void Awake()
    {
        TSSpeaker = GetSelectedSpeaker();

        if (selectedTTS == TTSOption.KokoroTTS)
        {
            if (kokoroTTSSpeaker != null) kokoroTTSSpeaker.enabled = true;
            if (elevenLabsTTS != null) elevenLabsTTS.enabled = false;
        }
        else if (selectedTTS == TTSOption.ElevenLabsTTS)
        {
            if (kokoroTTSSpeaker != null) kokoroTTSSpeaker.enabled = false;
            if (elevenLabsTTS != null) elevenLabsTTS.enabled = true;
            var streamAudioSource = GetComponent<SyncAudioSource>();
            if (streamAudioSource != null) streamAudioSource.enabled = true;
        }
    }

    // Start is called before the first frame update
    void Start()
    {
        TestTTSConnection();
    }

    private void TestTTSConnection()
    {
        TSSpeaker.TestConnection((isSuccess) => {
            if (statusIndicator != null)
            {
                statusIndicator.color = isSuccess ? ServerStatusColors.Connected : ServerStatusColors.Disconnected;
            }
        });
    }

    private ITTSSpeaker GetSelectedSpeaker()
    {
        switch (selectedTTS)
        {
            case TTSOption.KokoroTTS:
                return kokoroTTSSpeaker;
            case TTSOption.ElevenLabsTTS:
                return elevenLabsTTS; 
            default:
                return kokoroTTSSpeaker;
        }
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void Speak(string input, string language = "English")
    {
        sttHandler?.NotifyTTSStarted();
        TSSpeaker.Speak(input, language);

        if (ttsStateCoroutine != null) StopCoroutine(ttsStateCoroutine);
        ttsStateCoroutine = StartCoroutine(NotifySTTWhenPlaybackEnds());
    }

    private IEnumerator NotifySTTWhenPlaybackEnds()
    {
        yield return new WaitUntil(() => !waitingForAudioSynthesize());
        sttHandler?.NotifyTTSStopped();
        ttsStateCoroutine = null;
    }

    public bool waitingForAudioSynthesize()
    {
        return TSSpeaker.GetStatus() == ITTSSpeaker.AudioStatus.Playing;
    }

    /// <summary>True while the assistant's spoken voice is silenced via the UI button.</summary>
    public bool IsVoiceMuted { get; private set; }

    /// <summary>
    /// Silences (or restores) the assistant's voice by muting the AudioSources the TTS engines play
    /// through. Muting the source rather than skipping Speak() keeps playback timing intact, so captions
    /// still appear and the conversation loop still waits for the reply to "finish" as usual.
    /// </summary>
    public void SetVoiceMuted(bool muted)
    {
        IsVoiceMuted = muted;

        if (kokoroTTSSpeaker != null && kokoroTTSSpeaker.audioSource != null)
            kokoroTTSSpeaker.audioSource.mute = muted;

        var streamAudioSource = GetComponent<SyncAudioSource>();
        if (streamAudioSource != null)
        {
            var unitySource = streamAudioSource.GetComponent<AudioSource>();
            if (unitySource != null) unitySource.mute = muted;
        }
    }

    public void ToggleVoiceMute() => SetVoiceMuted(!IsVoiceMuted);

    public void ReportError()
    {
        TSSpeaker.Speak("I'm sorry, could you repeat that?");
    }

    public void CancelSpeak()
    {
        TSSpeaker.Stop();

        if (ttsStateCoroutine != null)
        {
            StopCoroutine(ttsStateCoroutine);
            ttsStateCoroutine = null;
        }
        sttHandler?.NotifyTTSStopped();
    }
}