# Piper TTS (offline, Multi-voice CZ/EN/SK) — MOKA Wars integration

Fully **offline** runtime text-to-speech using Piper VITS voices (ONNX) on
**Unity Inference Engine** (`com.unity.ai.inference` 2.3.0, namespace
`Unity.InferenceEngine`). Phonemes come from **eSpeak NG** (native `espeak-ng.dll`).

## Files / layout

| Path | What |
|---|---|
| `Assets/Piper/Scripts/PiperTTS.cs` | Main component: `SwitchVoice(name)`, `Generate(text, onReady)`, `Speak(text, source)`, scales, caching, timings |
| `Assets/Piper/Scripts/PiperVoiceConfig.cs` | Parses the voice `.onnx.json` (sample rate, espeak voice, scales, phoneme_id_map) |
| `Assets/Piper/Scripts/ESpeakNative.cs` | P/Invoke bindings for eSpeak NG |
| `Assets/Piper/Scripts/VoiceLines.cs` | Named events (explosion/death/win) with random Czech/English lines + pre-warm |
| `Assets/Piper/Demo/PiperDemoUI.cs` | Demo UI logic with voice selection, pangrams & combat phrases |
| `Assets/Piper/Demo/PiperDemoUI.uxml` | UI Toolkit layout with DropdownField voice selector and modern dark styling |
| `Assets/Piper/Editor/PiperDemoSceneBuilder.cs` | Menu **Piper → Build Demo Scene** creates `Assets/Piper/Demo/PiperDemo.unity` |
| `Assets/Piper/Editor/PiperVoiceDownloader.cs` | Menu **Piper → Download Voices...** downloader window for CZ, EN, SK & custom HuggingFace voices |
| `Assets/Piper/Plugins/Windows/espeak-ng.dll` | eSpeak NG native library (GPLv3) |
| `Assets/StreamingAssets/Piper/espeak-ng-data/…` | eSpeak NG data (GPLv3) |
| `Assets/Piper/Model/*.onnx` | Voice models imported as `ModelAsset` |
| `Assets/StreamingAssets/Piper/*.onnx.json` | Voice configs for each model |

## Included Voices

1. **`cs_CZ-jirka-medium`**: Čeština - Mužský hlas (Jirka, medium quality, 22050 Hz)
2. **`cs_CZ-kasandra-medium`**: Čeština - Ženský hlas (Kasandra, medium quality, 22050 Hz)
3. **`en_US-lessac-medium`**: English (US) - Čistý neutrální/ženský hlas (Lessac, medium quality, 22050 Hz)

Další hlasy (např. americký mužský `en_US-ryan-medium`, britský `en_GB-alan-medium`, slovenský `sk_SK-lili-medium`, nebo jakýkoliv jiný) lze kdykoliv stáhnout přes Unity menu: **Piper → Download Voices...**.

## Usage

```csharp
// Přepnutí hlasu za běhu:
piperTTS.SwitchVoice("cs_CZ-kasandra-medium"); // nebo "en_US-lessac-medium"

// Přehrání řeči:
piperTTS.Speak("Bum! Tohle bude bolet.", audioSource);
piperTTS.Generate("Hello world", clip => audioSource.PlayOneShot(clip));
```

Generation runs over multiple frames via `Worker.ScheduleIterable()` — the main
thread is not blocked. Default backend is `CPU` (safest for VITS).
