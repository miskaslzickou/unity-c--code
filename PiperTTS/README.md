# Piper TTS for Unity (Offline Czech & English Text-to-Speech)

A lightweight, completely offline neural Text-to-Speech (TTS) integration for Unity powered by **Unity Sentis / Unity Inference** and **Piper VITS ONNX models**.

> 📖 **Kompletní českou dokumentaci a podrobnou API referenci najdete v [DOCUMENTATION.md](DOCUMENTATION.md).**

---

## 🌟 Highlights
- **100% Offline & Local**: Runs entirely on the CPU in real-time, zero external server, Python, or API keys required.
- **Czech & English Support**: Ready-to-use models for Czech (*Jirka* male, *Kasandra* female) and English (*Lessac* female).
- **Full Sentence & Punctuation Support**: Multi-clause eSpeak phonemization loop supporting exclamation marks (`!`), question marks (`?`), commas, and periods without mid-sentence audio cuts.
- **Modern C# API**: Supports `Speak(...)`, `await SpeakAsync(...)`, and `await SpeakAndWaitAsync(...)` for sequential dialogues!
- **Audio Post-Processing**:
  - DC offset centering.
  - Peak normalization (92%) to avoid clipping and ensure consistent volume.
  - 12ms anti-click edge fade-in / fade-out.
  - Text pre-processing (expands common gaming abbreviations like HP, XP, DMG).
- **In-Editor Voice Downloader**: Easily download new voices from Hugging Face via `Piper -> Download Voices...`.
- **Interactive UI Toolkit Demo**: Includes ready-to-test sliders for Speed, Intonation, and Pitch + presets (*Přirozený dabing*, *Worms*, *Drsný voják*).

---

## 📁 Package Structure

```text
PiperTTS/
└── Assets/
    ├── Piper/
    │   ├── Demo/                     // UI Toolkit demo scene, UI Document & scripts
    │   ├── Editor/
    │   │   ├── PiperVoiceDownloader.cs // Downloader under menu 'Piper -> Download Voices...'
    │   │   └── PiperDemoSceneBuilder.cs
    │   ├── Model/                    // ONNX models (Honza High, Jirka, Kasandra, Lessac)
    │   ├── Plugins/Windows/          // espeak-ng.dll native library
    │   └── Scripts/
    │       ├── PiperTTS.cs           // Core TTS worker & audio synthesis engine
    │       ├── ESpeakNative.cs       // Native P/Invoke for eSpeak-NG phonemization
    │       ├── PiperVoiceConfig.cs   // Config JSON data structures
    │       └── VoiceLines.cs         // Example lines & presets
    └── StreamingAssets/
        └── Piper/
            ├── espeak-ng-data/       // Phoneme dictionaries required by eSpeak-NG
            ├── cs_CZ-honza-high.onnx.json
            ├── cs_CZ-jirka-medium.onnx.json
            ├── cs_CZ-kasandra-medium.onnx.json
            └── en_US-lessac-medium.onnx.json
```

---

## 📦 Requirements

1. **Unity 2022.3 LTS, 2023, or Unity 6**
2. **Unity Inference / Sentis Package**:
   - Open **Window -> Package Manager**
   - Click **+ -> Add package by name...**
   - Enter `com.unity.ai.inference` (Unity 6) or `com.unity.sentis` (Unity 2022/2023).

---

## 🚀 Installation

1. Copy the `Assets` folder from this directory and merge it into your Unity project's `Assets/` directory.
   - Verify that `Assets/Piper` and `Assets/StreamingAssets/Piper` exist in your project.
2. In Unity, open the demo scene at `Assets/Piper/Demo/PiperDemo.unity` and press **Play** to test.

---

## 💻 Quick Start in C#

```csharp
using UnityEngine;
using Piper;

[RequireComponent(typeof(AudioSource))]
public class ExampleVoiceActor : MonoBehaviour
{
    [SerializeField] private PiperTTS tts;

    private async void Start()
    {
        if (tts == null) tts = GetComponent<PiperTTS>();

        // 1. Simple fire-and-forget:
        tts.Speak("Pozor, nepřítel!");

        // 2. Sequential dialogues (awaits until the voice finishes playing):
        await tts.SpeakAndWaitAsync("Vítej ve velitelském stanu, vojíne.");
        await tts.SpeakAndWaitAsync("Připrav se na útok!");
    }
}
```

### Adjusting Voice Settings at Runtime

```csharp
// Natural Czech Dubbing
tts.lengthScale = 1.12f; // Speed (higher = slower, more natural)
tts.noiseScale = 0.72f;  // Intonation variance
audioSource.pitch = 1.0f;

// High-pitched "Worms" style:
audioSource.pitch = 1.45f;
tts.lengthScale = 0.95f;

// Deep Tough Soldier style:
audioSource.pitch = 0.82f;
tts.lengthScale = 1.25f;
```

---

## 🎙️ Adding More Voices

You can download any official Piper voice (over 40+ languages):
1. In the Unity Editor menu, select **`Piper -> Download Voices...`**.
2. Pick any voice from the catalog (or add new ones) and click **Download**.
3. The tool automatically places `.onnx` in `Assets/Piper/Model/` and `.onnx.json` in `Assets/StreamingAssets/Piper/`.
