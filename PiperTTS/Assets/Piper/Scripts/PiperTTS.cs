using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Piper
{
    /// <summary>
    /// Offline Czech (or any Piper-language) text-to-speech, fully local:
    /// phonemization via eSpeak NG (native lib), synthesis via the Piper VITS
    /// ONNX model running on Unity Inference Engine. No network access.
    /// </summary>
    public class PiperTTS : MonoBehaviour
    {
        [Header("Voices: .onnx in Assets/Piper/Model, .onnx.json in Assets/StreamingAssets/Piper")]
        [Tooltip("All discovered voice models (auto-filled in the editor from Assets/Piper/Model).")]
        public List<ModelAsset> voiceModels = new List<ModelAsset>();
        [Tooltip("Voice loaded on start (asset name without .onnx, e.g. cs_CZ-jirka-medium).")]
        [SerializeField] private string activeVoice = "";
        [SerializeField] private string espeakDataPath = "Piper/espeak-ng-data";

        /// <summary>
        /// Backwards compatibility for single-model scripts (e.g. PiperDemoSceneBuilder).
        /// Points to the active voice model or the first available model.
        /// </summary>
        public ModelAsset modelAsset
        {
            get => FindModel(activeVoice) ?? (voiceModels.Count > 0 ? voiceModels[0] : null);
            set
            {
                if (value != null)
                {
                    if (!voiceModels.Contains(value))
                        voiceModels.Add(value);
                    activeVoice = value.name;
                }
            }
        }

        /// <summary>Invoked whenever a voice finishes loading or changes.</summary>
        public event Action<string> OnVoiceChanged;

        [Tooltip("CPU is the safest backend for VITS models. GPUCompute may work on some GPUs.")]
        [SerializeField] private BackendType backend = BackendType.CPU;

        [Header("Inference scales (defaults are loaded from the voice JSON in Start)")]
        public float lengthScale = 1f;      // higher = slower speech
        public float noiseScale = 0.667f;
        public float noiseW = 0.8f;

        [Tooltip("Same text + same settings -> reuse the generated AudioClip.")]
        public bool cacheClips = true;

        [Tooltip("Log phonemize/inference timings to the Console (useful for benchmarking).")]
        [SerializeField] private bool logTimings = true;

        /// <summary>True once the model and eSpeak NG are initialized.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Parsed voice config from the .onnx.json (sample rate, espeak voice, ...).</summary>
        public PiperVoiceConfig Config { get; private set; }

        /// <summary>Names of all available voices (asset names without .onnx).</summary>
        public IReadOnlyList<string> VoiceNames => voiceModels.Where(v => v != null).Select(v => v.name).ToList();

        /// <summary>Voice currently loaded (or being loaded).</summary>
        public string ActiveVoice => activeVoice;

        private Worker worker;
        private bool hasSidInput;
        private string[] modelOutputs = Array.Empty<string>();
        private readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();
        private readonly Queue<KeyValuePair<string, Action<AudioClip>>> pending =
            new Queue<KeyValuePair<string, Action<AudioClip>>>();
        private Coroutine generationRoutine;

        // Statics survive play-mode restarts when domain reload is disabled
        // ("Reload Scene only" enter-play-mode settings), so they are refcounted
        // and reset explicitly below.
        private static int espeakRefcount;
        private static bool espeakReady;
        private bool espeakCounted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            espeakRefcount = 0;
            espeakReady = false;
        }

        /// <summary>
        /// Re-scans Assets/Piper/Model for all ModelAsset files and updates the voiceModels list.
        /// </summary>
        public void RefreshVoiceList()
        {
#if UNITY_EDITOR
            var ids = UnityEditor.AssetDatabase.FindAssets("t:ModelAsset", new[] { "Assets/Piper/Model" });
            var found = ids
                .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .Select(UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>)
                .Where(a => a != null)
                .ToList();
            found = found.OrderBy(v => v.name.StartsWith("cs_CZ") ? 0 : 1).ThenBy(v => v.name).ToList();
            if (found.Count > 0)
            {
                voiceModels = found;
                if (string.IsNullOrEmpty(activeVoice) || FindModel(activeVoice) == null)
                    activeVoice = voiceModels[0].name;
                UnityEditor.EditorUtility.SetDirty(this);
            }
#endif
        }

#if UNITY_EDITOR
        // Self-heal: auto-discover all voice models in Assets/Piper/Model so this
        // component works when dropped on any object without manual assignment.
        private void OnValidate()
        {
            var ids = UnityEditor.AssetDatabase.FindAssets("t:ModelAsset", new[] { "Assets/Piper/Model" });
            if (ids.Length != voiceModels.Count || voiceModels.Any(v => v == null))
            {
                RefreshVoiceList();
            }
            if (string.IsNullOrEmpty(activeVoice) && voiceModels.Count > 0 && voiceModels[0] != null)
            {
                activeVoice = voiceModels[0].name;
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }

        private void Reset() => RefreshVoiceList();
#endif

        private IEnumerator Start()
        {
#if UNITY_EDITOR
            if (voiceModels.Count == 0 || voiceModels.Any(v => v == null))
            {
                RefreshVoiceList();
            }
#endif
            if (voiceModels.Count == 0)
            {
                Debug.LogError("[PiperTTS] No voice models found. Put .onnx files in Assets/Piper/Model/ (imported as ModelAsset).");
                yield break;
            }
            if (string.IsNullOrEmpty(activeVoice) || FindModel(activeVoice) == null)
                activeVoice = voiceModels[0].name;

            yield return LoadVoiceRoutine(activeVoice);
        }

        /// <summary>
        /// Switch to another voice (name from VoiceNames). Unloads the current model
        /// and loads the new one; IsReady is false until loading finishes.
        /// </summary>
        public void SwitchVoice(string voiceName)
        {
            if (voiceName == activeVoice && IsReady) return;
            if (FindModel(voiceName) == null)
            {
                Debug.LogError($"[PiperTTS] Voice '{voiceName}' not found. Available: [{string.Join(", ", VoiceNames)}]");
                return;
            }

            if (generationRoutine != null) { StopCoroutine(generationRoutine); generationRoutine = null; }
            pending.Clear();
            clipCache.Clear();
            activeVoice = voiceName;
            StartCoroutine(LoadVoiceRoutine(voiceName));
        }

        private ModelAsset FindModel(string name) =>
            voiceModels.FirstOrDefault(v => v != null && v.name == name);

        private IEnumerator LoadVoiceRoutine(string voiceName)
        {
            IsReady = false;
            var modelAsset = FindModel(voiceName);

            // --- Voice config from JSON (StreamingAssets/Piper/<voice>.onnx.json) ---
            string jsonPath = Path.Combine(Application.streamingAssetsPath, "Piper", voiceName + ".onnx.json");
            if (!File.Exists(jsonPath))
            {
                Debug.LogError($"[PiperTTS] Voice config not found: {jsonPath}");
                yield break;
            }
            Config = PiperVoiceConfig.FromJson(File.ReadAllText(jsonPath));
            if (Config == null) yield break;

            // Apply slightly warmer, more natural default scales for gaming (user can still tweak in Inspector / UI).
            lengthScale = Config.lengthScale > 0.01f ? Mathf.Max(Config.lengthScale * 1.10f, 1.10f) : 1.12f;
            noiseScale = Config.noiseScale > 0.01f ? Mathf.Max(Config.noiseScale * 1.08f, 0.72f) : 0.72f;
            noiseW = Config.noiseW > 0.01f ? Config.noiseW : 0.8f;
            Debug.Log($"[PiperTTS] Voice '{voiceName}': sampleRate={Config.sampleRate}, espeakVoice='{Config.espeakVoice}', " +
                      $"scales (noise={noiseScale}, length={lengthScale}, noiseW={noiseW}), speakers={Config.numSpeakers}");

            // --- eSpeak NG (native, main thread) ---
            string dataPath = Path.Combine(Application.streamingAssetsPath, espeakDataPath);
            if (!InitEspeakOnce(dataPath)) yield break;
            if (!espeakCounted) { espeakRefcount++; espeakCounted = true; }
            if (!SetEspeakVoice(Config.espeakVoice)) yield break;

            if (modelAsset == null)
            {
                Debug.LogError($"[PiperTTS] ModelAsset for voice '{voiceName}' missing.");
                yield break;
            }
            yield return null; // let Awake/Start of callers finish before the (heavy) load

            Model model;
            try
            {
                model = ModelLoader.Load(modelAsset);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PiperTTS] Failed to load ONNX model '{voiceName}': {e.Message}");
                yield break;
            }

            worker?.Dispose();
            worker = new Worker(model, backend);
            hasSidInput = model.inputs.Count > 3 && model.inputs[3].name == "sid";
            modelOutputs = model.outputs.Select(o => o.name).ToArray();
            IsReady = true;
            Debug.Log($"[PiperTTS] Ready ('{voiceName}'). Backend={backend}, inputs={model.inputs.Count}, sid={hasSidInput}, " +
                      $"outputs=[{string.Join(", ", modelOutputs)}]");
            OnVoiceChanged?.Invoke(voiceName);

            // Anything that was requested during initialization.
            if (pending.Count > 0 && generationRoutine == null)
                generationRoutine = StartCoroutine(GenerateNext());
        }


        /// <summary>Generates speech for the text and returns an AudioClip (no playback).</summary>
        public void Generate(string text, Action<AudioClip> onReady)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                onReady?.Invoke(null);
                return;
            }

            if (cacheClips && clipCache.TryGetValue(CacheKey(text), out var cached) && cached != null)
            {
                onReady?.Invoke(cached);
                return;
            }

            pending.Enqueue(new KeyValuePair<string, Action<AudioClip>>(text, onReady));
            if (IsReady && generationRoutine == null)
                generationRoutine = StartCoroutine(GenerateNext());
        }

        /// <summary>Convenience: generate and play the clip on an AudioSource (defaults to GetComponent<AudioSource>).</summary>
        public void Speak(string text, AudioSource target = null)
        {
            if (target == null) target = GetComponent<AudioSource>();
            Generate(text, clip =>
            {
                if (clip != null && target != null) target.PlayOneShot(clip);
            });
        }

        /// <summary>Async version of Generate: returns the synthesized AudioClip without playing.</summary>
        public System.Threading.Tasks.Task<AudioClip> GenerateAsync(string text)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<AudioClip>();
            Generate(text, clip => tcs.SetResult(clip));
            return tcs.Task;
        }

        /// <summary>Async version of Speak: awaits synthesis and starts playback on the target AudioSource.</summary>
        public async System.Threading.Tasks.Task SpeakAsync(string text, AudioSource target = null)
        {
            if (target == null) target = GetComponent<AudioSource>();
            var clip = await GenerateAsync(text);
            if (clip != null && target != null)
            {
                target.PlayOneShot(clip);
            }
        }

        /// <summary>Generates speech, starts playback, and awaits until the audio clip finishes playing.</summary>
        public async System.Threading.Tasks.Task SpeakAndWaitAsync(string text, AudioSource target = null)
        {
            if (target == null) target = GetComponent<AudioSource>();
            var clip = await GenerateAsync(text);
            if (clip != null && target != null)
            {
                target.PlayOneShot(clip);
                float pitch = target.pitch > 0.01f ? target.pitch : 1f;
                int durationMs = Mathf.CeilToInt((clip.length / pitch) * 1000f);
                await System.Threading.Tasks.Task.Delay(durationMs);
            }
        }

        private IEnumerator GenerateNext()
        {
            while (pending.Count > 0)
            {
                if (!IsReady) yield break; // Start() resumes the queue once ready
                var request = pending.Dequeue();
                yield return GenerateRoutine(request.Key, request.Value);
            }
            generationRoutine = null;
        }

        private IEnumerator GenerateRoutine(string text, Action<AudioClip> onReady)
        {
            var swTotal = Stopwatch.StartNew();

            string cleanedText = PreprocessText(text);

            // --- Phonemize (fast, main thread) ---
            var sw = Stopwatch.StartNew();
            string phonemes = Phonemize(cleanedText);
            int[] tokens = Config.Tokenize(phonemes);
            if (logTimings)
                Debug.Log($"[PiperTTS] Phonemize: {sw.ElapsedMilliseconds} ms ({tokens.Length} tokens) for \"{text}\"");
            if (tokens.Length <= 3)
            {
                Debug.LogWarning($"[PiperTTS] No phonemes produced for \"{text}\".");
                onReady?.Invoke(null);
                yield break;
            }

            // --- Inference (iterative: work is spread over frames, no long freeze) ---
            sw.Restart();
            using (var phonemeTensor = new Tensor<int>(new TensorShape(1, tokens.Length), tokens))
            using (var lengthTensor = new Tensor<int>(new TensorShape(1), new int[] { tokens.Length }))
            using (var scalesTensor = new Tensor<float>(new TensorShape(3),
                new float[] { noiseScale, lengthScale, noiseW }))
            using (var sidTensor = hasSidInput ? new Tensor<int>(new TensorShape(1), new int[] { 0 }) : null)
            {
                worker.SetInput("input", phonemeTensor);
                worker.SetInput("input_lengths", lengthTensor);
                worker.SetInput("scales", scalesTensor);
                if (hasSidInput) worker.SetInput("sid", sidTensor);

                var iterator = worker.ScheduleIterable();
                // MoveNext() == true  -> more layers remain; false -> done.
                // Run layers until ~8 ms of frame budget is used, then yield.
                var swFrame = Stopwatch.StartNew();
                while (iterator.MoveNext())
                {
                    if (swFrame.ElapsedMilliseconds >= 8)
                    {
                        yield return null;
                        swFrame.Restart();
                    }
                }

                var output = worker.PeekOutput("output") as Tensor<float>;
                if (output == null)
                {
                    // fall back: try every model output until one resolves
                    foreach (var o in modelOutputs)
                    {
                        output = worker.PeekOutput(o) as Tensor<float>;
                        if (output != null) break;
                    }
                }
                if (output == null)
                {
                    Debug.LogError($"[PiperTTS] No float output tensor found. Model outputs: [{string.Join(", ", modelOutputs)}]");
                    onReady?.Invoke(null);
                    yield break;
                }
                using var cpuCopy = output.ReadbackAndClone();
                float[] audio = cpuCopy.DownloadToArray();

                // Post-processing: DC offset removal, peak normalization, edge anti-click fades
                PostProcessAudio(audio, Config.sampleRate);

                if (logTimings)
                    Debug.Log($"[PiperTTS] Inference: {sw.ElapsedMilliseconds} ms, {audio.Length} samples " +
                              $"({(float)audio.Length / Config.sampleRate:F2}s audio) on {backend}");

                var clip = AudioClip.Create("piper_" + text.GetHashCode(), audio.Length, 1, Config.sampleRate, false);
                clip.SetData(audio, 0);

                if (cacheClips) clipCache[CacheKey(text)] = clip;

                swTotal.Stop();
                if (logTimings) Debug.Log($"[PiperTTS] Total: {swTotal.ElapsedMilliseconds} ms");
                onReady?.Invoke(clip);
            }
        }

        // ------------------------------------------------------------------ //

        private string PreprocessText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string processed = input.Trim();

            // Pokud text nekončí interpunkcí, přidáme tečku pro čisté doznění věty a správnou intonaci
            char last = processed[processed.Length - 1];
            if (last != '.' && last != '!' && last != '?' && last != ';' && last != ':')
            {
                processed += ".";
            }

            // Rozšíření běžných herních zkratek
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bHP\b", "há pé", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bXP\b", "iks pé", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bDMG\b", "déméč", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bGG\b", "dží dží", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bEZ\b", "ízí", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\bWorms\b", "Vórms", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return processed;
        }

        private static void PostProcessAudio(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0) return;

            // 1. Odstranění DC offsetu (středění signálu)
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++) sum += samples[i];
            float mean = sum / samples.Length;
            for (int i = 0; i < samples.Length; i++) samples[i] -= mean;

            // 2. Normalizace hlasitosti (Peak Normalization na ~0.92)
            float maxAbs = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > maxAbs) maxAbs = abs;
            }

            if (maxAbs > 0.001f)
            {
                float targetPeak = 0.92f;
                float gain = Mathf.Clamp(targetPeak / maxAbs, 0.8f, 2.8f);
                for (int i = 0; i < samples.Length; i++)
                {
                    float val = samples[i] * gain;
                    // Měkký limit proti digitálnímu ořezu (soft-clipping)
                    samples[i] = Mathf.Clamp(val, -0.98f, 0.98f);
                }
            }

            // 3. Plynulý Fade-in a Fade-out na okrajích (zabraňuje lupnutí / prasknutí na začátku a konci)
            int fadeSamples = Mathf.Min(samples.Length / 10, (int)(sampleRate * 0.012f)); // 12ms fade
            for (int i = 0; i < fadeSamples; i++)
            {
                float factor = (float)i / fadeSamples;
                samples[i] *= factor;
                samples[samples.Length - 1 - i] *= factor;
            }
        }

        private string Phonemize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            IntPtr textPtr = IntPtr.Zero;
            try
            {
                byte[] textBytes = Encoding.UTF8.GetBytes(text + "\0");
                textPtr = Marshal.AllocHGlobal(textBytes.Length);
                Marshal.Copy(textBytes, 0, textPtr, textBytes.Length);

                IntPtr pointerToText = textPtr;
                var sb = new StringBuilder();

                // espeak_TextToPhonemes zpracovává text po jednotlivých větách/klauzích oddělených interpunkcí (!, ?, ., ;, atd.)
                // a posouvá pointerToText na zbývající text. Je nutné procházet text ve smyčce,
                // jinak se po prvním vykřičníku nebo tečce zbytek věty zahodí!
                while (pointerToText != IntPtr.Zero && Marshal.ReadByte(pointerToText) != 0)
                {
                    IntPtr resultPtr = ESpeakNative.espeak_TextToPhonemes(ref pointerToText, 0, 2);
                    if (resultPtr == IntPtr.Zero) break;

                    string clausePhonemes = PtrToUtf8(resultPtr);
                    if (!string.IsNullOrWhiteSpace(clausePhonemes))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(" ");
                        }
                        sb.Append(clausePhonemes.Trim());
                    }
                }

                return sb.ToString();
            }
            finally
            {
                if (textPtr != IntPtr.Zero) Marshal.FreeHGlobal(textPtr);
            }
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }

        private string CacheKey(string text)
        {
            return $"{activeVoice}|{text}|{noiseScale:F3}|{lengthScale:F3}|{noiseW:F3}|{backend}";
        }

        private static bool InitEspeakOnce(string dataPath)
        {
            if (!espeakReady)
            {
                if (!Directory.Exists(dataPath))
                {
                    Debug.LogError($"[PiperTTS] espeak-ng-data not found: {dataPath}");
                    return false;
                }
                int code = ESpeakNative.espeak_Initialize(0, 0, dataPath, 0);
                if (code <= 0)
                {
                    Debug.LogError($"[PiperTTS] espeak_Initialize failed with code {code}.");
                    return false;
                }
                espeakReady = true;
            }
            return true;
        }

        private static bool SetEspeakVoice(string voice)
        {
            int voiceCode = ESpeakNative.espeak_SetVoiceByName(voice);
            if (voiceCode != 0)
                Debug.LogError($"[PiperTTS] espeak_SetVoiceByName('{voice}') failed with code {voiceCode}.");
            return voiceCode == 0;
        }

        private void OnDestroy()
        {
            if (generationRoutine != null)
            {
                StopCoroutine(generationRoutine);
                generationRoutine = null;
            }
            worker?.Dispose();
            worker = null;
            IsReady = false;

            espeakRefcount--;
            if (espeakRefcount <= 0 && espeakReady)
            {
                ESpeakNative.espeak_Terminate();
                espeakReady = false;
                espeakRefcount = 0;
            }
        }
    }
}
