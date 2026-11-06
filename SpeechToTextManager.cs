using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using GameSystems.Commands;
using GameSystems.Construction;
using GameSystems.RTS;
using GameSystems.Simulation;

namespace GameSystems.Speech
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO SPEECH-TO-TEXT PROVIDERY
    // =========================================================================

    /// <summary>
    /// Rozhraní pro poskytovatele přepisu řeči na text (např. lokální Whisper HTTP server, ONNX, offline API).
    /// </summary>
    public interface ISpeechToTextProvider
    {
        Task<string> TranscribeAsync(byte[] wavData, float[] rawSamples, string language);
    }

    /// <summary>
    /// Pravidlo hlasového příkazu pro herní akce (např. "postav elektrárnu" -> GridPlacementSystem).
    /// </summary>
    public class VoiceCommandRule
    {
        public string TriggerPhrase { get; }
        public string CommandTag { get; }
        public Action Callback { get; }

        public VoiceCommandRule(string phrase, Action callback, string tag = "")
        {
            TriggerPhrase = phrase.ToLowerInvariant().Trim();
            Callback = callback;
            CommandTag = string.IsNullOrEmpty(tag) ? TriggerPhrase : tag;
        }

        public bool Matches(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf(TriggerPhrase, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // =========================================================================
    // 2. VÝCHOZÍ LOKÁLNÍ HTTP WHISPER PROVIDER
    // =========================================================================

    /// <summary>
    /// Klient pro komunikaci s lokálním Whisper API serverem (např. Whisper.cpp, Whisper-FastAPI, OpenAI compatible).
    /// Běží zcela offline na localhostu (ideální protějšek k PiperTTS).
    /// </summary>
    public class LocalHttpWhisperProvider : ISpeechToTextProvider
    {
        private readonly string serverUrl;

        public LocalHttpWhisperProvider(string url = "http://localhost:5000/v1/audio/transcriptions")
        {
            serverUrl = url;
        }

        public async Task<string> TranscribeAsync(byte[] wavData, float[] rawSamples, string language)
        {
            if (wavData == null || wavData.Length == 0) return string.Empty;

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", wavData, "voice_input.wav", "audio/wav"),
                new MultipartFormDataSection("model", "whisper-1"),
                new MultipartFormDataSection("language", string.IsNullOrEmpty(language) ? "cs" : language)
            };

            using (UnityWebRequest request = UnityWebRequest.Post(serverUrl, form))
            {
                request.timeout = 10;
                var op = request.SendWebRequest();

                while (!op.isDone)
                {
                    await Task.Yield();
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string jsonResponse = request.downloadHandler.text;
                    return ParseTranscriptFromJson(jsonResponse);
                }
                else
                {
                    Debug.LogWarning($"[LocalHttpWhisperProvider] Server vrátil chybu ({request.error}): {request.downloadHandler?.text}");
                    return string.Empty;
                }
            }
        }

        [Serializable]
        private class WhisperJsonResponse
        {
#pragma warning disable 0649
            public string text;
#pragma warning restore 0649
        }

        private string ParseTranscriptFromJson(string json)
        {
            try
            {
                var parsed = JsonUtility.FromJson<WhisperJsonResponse>(json);
                return parsed?.text ?? json;
            }
            catch
            {
                return json;
            }
        }
    }

    // =========================================================================
    // 3. HLAVNÍ MANAŽER HLASOVÉHO VSTUPU (SPEECH TO TEXT MANAGER)
    // =========================================================================

    /// <summary>
    /// Kompletní systém hlasového vstupu: záznam z mikrofonu do kruhového AudioClip bufferu,
    /// detekce hlasové aktivity (VAD / RMS energy), kódování do 16-bit PCM WAV v paměti (zero-disk-I/O),
    /// asynchronní přepis a parser hlasových povelů propojující řeč se stavěním, výběrem a rozkazy.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpeechToTextManager : MonoBehaviour
    {
        public static SpeechToTextManager Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno, jakmile hráč začne mluvit (VAD překročí práh).</summary>
        public static event Action OnSpeechStarted;

        /// <summary>Vyvoláno, když hráč domluví (ticho delší než práh).</summary>
        public static event Action OnSpeechEnded;

        /// <summary>Vyvoláno po úspěšném přepisu řeči na text.</summary>
        public static event Action<string> OnTranscriptionCompleted;

        /// <summary>Vyvoláno při rozpoznání zaregistrovaného herního povelu (tag, plný text).</summary>
        public static event Action<string, string> OnVoiceCommandRecognized;

        /// <summary>Pravidelně vyvoláváno s aktuální RMS hladinou hlasitosti (pro UI indikátor mikrofonu).</summary>
        public static event Action<float> OnAudioLevelChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Konfigurace mikrofonu")]
        [SerializeField, Tooltip("Vzorkovací frekvence (16000 Hz je standard pro Whisper)")]
        private int sampleRate = 16000;

        [SerializeField, Range(1, 15), Tooltip("Délka kruhového záznamového bufferu v sekundách")]
        private int clipBufferDurationSeconds = 10;

        [SerializeField] private string targetLanguage = "cs";

        [Header("Detekce hlasu (Voice Activity Detection - VAD)")]
        [SerializeField] private bool usePushToTalk = false;
        [SerializeField] private KeyCode pushToTalkKey = KeyCode.V;

        [SerializeField, Range(0.001f, 0.1f), Tooltip("Hladina RMS energie, od které je detekován hlas")]
        private float vadEnergyThreshold = 0.015f;

        [SerializeField, Range(0.3f, 2.0f), Tooltip("Délka ticha v sekundách nutná pro ukončení promluvy")]
        private float silenceDurationThreshold = 0.8f;

        [Header("Lokální server")]
        [SerializeField] private string localWhisperUrl = "http://localhost:5000/v1/audio/transcriptions";

        // =========================================================================
        // VNITŘNÍ STAVY ZÁZNAMU A BUFFERŮ (ZERO-GC)
        // =========================================================================

        private AudioClip micClip;
        private string micDeviceName = null;
        private bool isListening = false;
        private bool isSpeaking = false;

        private int lastSamplePosition = 0;
        private float silenceTimer = 0f;
        private float currentRMS = 0f;

        // Zásobník vzorků pro aktuální promluvu
        private readonly List<float> recordedUtteranceSamples = new List<float>(64000); // 4 sekundy @ 16kHz
        private readonly float[] sampleAnalysisBuffer = new float[512];

        // Poskytovatel STT
        private ISpeechToTextProvider currentProvider;

        // Seznam registrovaných hlasových pravidel
        private readonly List<VoiceCommandRule> voiceCommandRules = new List<VoiceCommandRule>(16);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        public bool IsListening => isListening;
        public bool IsSpeaking => isSpeaking;
        public float CurrentRMS => currentRMS;
        public string MicDeviceName => micDeviceName;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            currentProvider = new LocalHttpWhisperProvider(localWhisperUrl);
            RegisterDefaultCommands();
        }

        private void Start()
        {
            StartListening();
        }

        private void Update()
        {
            if (!isListening) return;

            ProcessAudioInput();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            StopListening();
        }

        // =========================================================================
        // SPRÁVA ZÁZNAMU Z MIKROFONU
        // =========================================================================

        /// <summary>
        /// Zahájí nahrávání z výchozího systémového mikrofonu do kruhového bufferu.
        /// </summary>
        public void StartListening()
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[SpeechToTextManager] V systému nebyl nalezen žádný mikrofon!");
                return;
            }

            micDeviceName = Microphone.devices[0];
            micClip = Microphone.Start(micDeviceName, true, clipBufferDurationSeconds, sampleRate);
            lastSamplePosition = 0;
            isListening = true;
            isSpeaking = false;
            silenceTimer = 0f;
            recordedUtteranceSamples.Clear();
        }

        /// <summary>
        /// Zastaví nahrávání a uvolní AudioClip.
        /// </summary>
        public void StopListening()
        {
            if (isListening)
            {
                Microphone.End(micDeviceName);
                isListening = false;
                isSpeaking = false;
            }
        }

        // =========================================================================
        // HLASOVÁ AKTIVITA (VAD) A EXTRAKCE PROMLUVY
        // =========================================================================

        private void ProcessAudioInput()
        {
            int currentPos = Microphone.GetPosition(micDeviceName);
            if (currentPos < 0 || currentPos == lastSamplePosition) return;

            // Spočtení počtu nových vzorků
            int samplesAvailable = (currentPos >= lastSamplePosition)
                ? (currentPos - lastSamplePosition)
                : (micClip.samples - lastSamplePosition + currentPos);

            if (samplesAvailable <= 0) return;

            // Čtení nových vzorků po blocích
            float[] tempBuffer = new float[samplesAvailable];
            micClip.GetData(tempBuffer, lastSamplePosition);
            lastSamplePosition = currentPos;

            // Výpočet RMS energie (hlasitosti)
            currentRMS = CalculateRMS(tempBuffer);
            OnAudioLevelChanged?.Invoke(currentRMS);

            // Režim Push-to-Talk vs Automatický VAD
            bool speechActive = usePushToTalk
                ? Input.GetKey(pushToTalkKey)
                : (currentRMS >= vadEnergyThreshold);

            if (speechActive)
            {
                silenceTimer = 0f;

                if (!isSpeaking)
                {
                    isSpeaking = true;
                    recordedUtteranceSamples.Clear();
                    OnSpeechStarted?.Invoke();
                }

                recordedUtteranceSamples.AddRange(tempBuffer);
            }
            else
            {
                if (isSpeaking)
                {
                    recordedUtteranceSamples.AddRange(tempBuffer);
                    silenceTimer += Time.deltaTime;

                    if (silenceDurationThreshold <= silenceTimer)
                    {
                        // Hráč domluvil – ukončení promluvy a odeslání k přepisu
                        isSpeaking = false;
                        OnSpeechEnded?.Invoke();
                        TriggerTranscription();
                    }
                }
            }
        }

        private float CalculateRMS(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Mathf.Sqrt(sum / samples.Length);
        }

        // =========================================================================
        // KÓDOVÁNÍ DO 16-BIT PCM WAV V PAMĚTI (ZERO DISK I/O)
        // =========================================================================

        private async void TriggerTranscription()
        {
            if (recordedUtteranceSamples.Count < sampleRate * 0.3f)
            {
                // Příliš krátký zvuk (< 0.3s), pravděpodobně lupnutí nebo šum
                recordedUtteranceSamples.Clear();
                return;
            }

            float[] samples = recordedUtteranceSamples.ToArray();
            recordedUtteranceSamples.Clear();

            // Převedení float vzorků do 16-bit PCM WAV formátu v paměti
            byte[] wavBytes = EncodeTo16BitWav(samples, 1, sampleRate);

            if (currentProvider != null)
            {
                string transcript = await currentProvider.TranscribeAsync(wavBytes, samples, targetLanguage);
                if (!string.IsNullOrEmpty(transcript))
                {
                    transcript = transcript.Trim();
                    OnTranscriptionCompleted?.Invoke(transcript);
                    ParseAndExecuteCommands(transcript);
                }
            }
        }

        /// <summary>
        /// Převede pole float PCM vzorků (-1.0 až 1.0) do standardního 16-bitového PCM WAV souboru v RAM.
        /// </summary>
        public static byte[] EncodeTo16BitWav(float[] samples, int channels, int sampleRate)
        {
            using (MemoryStream stream = new MemoryStream(44 + samples.Length * 2))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int byteRate = sampleRate * channels * 2;
                int dataSize = samples.Length * 2;

                // RIFF hlavička
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt chunk
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // Chunk size
                writer.Write((short)1); // PCM format
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)(channels * 2)); // Block align
                writer.Write((short)16); // Bits per sample

                // data chunk
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);

                // Zápis 16-bit PCM vzorků
                for (int i = 0; i < samples.Length; i++)
                {
                    short pcmSample = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                    writer.Write(pcmSample);
                }

                return stream.ToArray();
            }
        }

        // =========================================================================
        // PARSER HLASOVÝCH PŘÍKAZŮ PRO PROPOJENÍ S HRACÍMI SYSTÉMY
        // =========================================================================

        public void RegisterVoiceCommand(string phrase, Action callback, string tag = "")
        {
            if (string.IsNullOrEmpty(phrase) || callback == null) return;
            voiceCommandRules.Add(new VoiceCommandRule(phrase, callback, tag));
        }

        private void ParseAndExecuteCommands(string recognizedText)
        {
            for (int i = 0; i < voiceCommandRules.Count; i++)
            {
                VoiceCommandRule rule = voiceCommandRules[i];
                if (rule.Matches(recognizedText))
                {
                    OnVoiceCommandRecognized?.Invoke(rule.CommandTag, recognizedText);
                    rule.Callback?.Invoke();
                }
            }
        }

        private void RegisterDefaultCommands()
        {
            // Příkazy pro CommandBufferUndoSystem
            RegisterVoiceCommand("zpět", () => CommandBufferUndoSystem.Instance?.Undo(), "CMD_UNDO");
            RegisterVoiceCommand("undo", () => CommandBufferUndoSystem.Instance?.Undo(), "CMD_UNDO");
            RegisterVoiceCommand("znovu", () => CommandBufferUndoSystem.Instance?.Redo(), "CMD_REDO");
            RegisterVoiceCommand("redo", () => CommandBufferUndoSystem.Instance?.Redo(), "CMD_REDO");

            // Příkazy pro výběr (RTSSelectionAndGroupManager)
            RegisterVoiceCommand("zrušit výběr", () => RTSSelectionAndGroupManager.Instance?.ClearSelection(), "CMD_CLEAR_SELECTION");
            RegisterVoiceCommand("vybrat vše", () => RTSSelectionAndGroupManager.Instance?.SelectAllVisibleOfType("unit_marine", true), "CMD_SELECT_ALL");

            // Příkazy pro pauzu a rychlost (TickSimulationManager)
            RegisterVoiceCommand("pauza", () => TickSimulationManager.Instance?.TogglePause(), "CMD_PAUSE");
            RegisterVoiceCommand("stop", () => TickSimulationManager.Instance?.SetSpeed(SimulationSpeed.Paused), "CMD_STOP");
            RegisterVoiceCommand("start", () => TickSimulationManager.Instance?.SetSpeed(SimulationSpeed.Normal_1x), "CMD_RESUME");
            RegisterVoiceCommand("zrychlit", () => TickSimulationManager.Instance?.SetSpeed(SimulationSpeed.Faster_5x), "CMD_FAST_FORWARD");
        }

        public void SetProvider(ISpeechToTextProvider provider)
        {
            if (provider != null)
            {
                currentProvider = provider;
            }
        }
    }
}
