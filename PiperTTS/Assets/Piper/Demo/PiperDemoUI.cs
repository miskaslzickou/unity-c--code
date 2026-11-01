using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Piper
{
    /// <summary>
    /// Demo UI (UI Toolkit). The layout lives in PiperDemoUI.uxml assigned to the
    /// UIDocument's Source Asset; wires controls to PiperTTS for voice selection,
    /// text input, sample phrases, and synthesis.
    /// </summary>
    public class PiperDemoUI : MonoBehaviour
    {
        public PiperTTS tts;
        public UIDocument document;

        private const string BumTestCz = "Bum! Tohle bude bolet, ty červe!";
        private const string BumTestEn = "Boom! That's gonna leave a mark!";
        private const string PangramCz = "Příliš žluťoučký kůň úpěl ďábelské ódy.";
        private const string PangramEn = "The quick brown fox jumps over the lazy dog.";

        private DropdownField voiceDropdown;
        private Label statusLabel;
        private TextField inputField;
        private Button speakButton;

        private void Awake()
        {
            EnsureDependencies();
        }

        private void EnsureDependencies()
        {
            if (tts == null) tts = GetComponent<PiperTTS>() ?? FindAnyObjectByType<PiperTTS>();
            if (document == null) document = GetComponent<UIDocument>() ?? FindAnyObjectByType<UIDocument>();
            if (document != null)
            {
#if UNITY_EDITOR
                if (document.visualTreeAsset == null)
                {
                    var uxml = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Piper/Demo/PiperDemoUI.uxml");
                    if (uxml != null) document.visualTreeAsset = uxml;
                }
                if (document.panelSettings == null)
                {
                    var panel = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Piper/Demo/PiperPanelSettings.asset");
                    if (panel != null) document.panelSettings = panel;
                }
#endif
            }
        }

        private void OnEnable()
        {
            EnsureDependencies();
            if (document == null)
            {
                Debug.LogError("[PiperDemoUI] 'Document' (UIDocument) is not assigned on " + name);
                return;
            }
            if (document.panelSettings == null)
            {
                Debug.LogError("[PiperDemoUI] The UIDocument has no Panel Settings assigned.");
                return;
            }
            if (document.rootVisualElement == null)
            {
                Debug.LogError("[PiperDemoUI] UIDocument has no root visual element (Source Asset missing?).");
                return;
            }

            var root = document.rootVisualElement;
            inputField = root.Q<TextField>("piper-input");
            speakButton = root.Q<Button>("btn-speak");
            var bum = root.Q<Button>("btn-bum");
            voiceDropdown = root.Q<DropdownField>("voice-dropdown");
            statusLabel = root.Q<Label>("status-label");

            var btnPangramCz = root.Q<Button>("btn-pangram-cz");
            var btnCombatCz = root.Q<Button>("btn-combat-cz");
            var btnPangramEn = root.Q<Button>("btn-pangram-en");
            var btnCombatEn = root.Q<Button>("btn-combat-en");

            // Quality and style sliders
            var sliderSpeed = root.Q<Slider>("slider-speed");
            var sliderNoise = root.Q<Slider>("slider-noise");
            var sliderPitch = root.Q<Slider>("slider-pitch");
            var audioSource = GetComponent<AudioSource>();

            if (sliderSpeed != null && tts != null)
            {
                sliderSpeed.value = tts.lengthScale;
                sliderSpeed.RegisterValueChangedCallback(evt => tts.lengthScale = evt.newValue);
            }
            if (sliderNoise != null && tts != null)
            {
                sliderNoise.value = tts.noiseScale;
                sliderNoise.RegisterValueChangedCallback(evt => tts.noiseScale = evt.newValue);
            }
            if (sliderPitch != null && audioSource != null)
            {
                sliderPitch.value = audioSource.pitch;
                sliderPitch.RegisterValueChangedCallback(evt => audioSource.pitch = evt.newValue);
            }

            // Presets
            var btnNatural = root.Q<Button>("btn-preset-natural");
            var btnWorms = root.Q<Button>("btn-preset-worms");
            var btnSoldier = root.Q<Button>("btn-preset-soldier");

            if (btnNatural != null) btnNatural.clicked += () =>
            {
                if (tts != null) { tts.lengthScale = 1.12f; tts.noiseScale = 0.72f; }
                if (audioSource != null) audioSource.pitch = 1.0f;
                if (sliderSpeed != null) sliderSpeed.value = 1.12f;
                if (sliderNoise != null) sliderNoise.value = 0.72f;
                if (sliderPitch != null) sliderPitch.value = 1.0f;
                if (statusLabel != null) statusLabel.text = "Nastaven styl: Přirozený dabing";
            };

            if (btnWorms != null) btnWorms.clicked += () =>
            {
                if (tts != null) { tts.lengthScale = 1.04f; tts.noiseScale = 0.82f; }
                if (audioSource != null) audioSource.pitch = 1.28f;
                if (sliderSpeed != null) sliderSpeed.value = 1.04f;
                if (sliderNoise != null) sliderNoise.value = 0.82f;
                if (sliderPitch != null) sliderPitch.value = 1.28f;
                if (statusLabel != null) statusLabel.text = "Nastaven styl: 🪱 Komický červík (Worms)";
            };

            if (btnSoldier != null) btnSoldier.clicked += () =>
            {
                if (tts != null) { tts.lengthScale = 1.16f; tts.noiseScale = 0.65f; }
                if (audioSource != null) audioSource.pitch = 0.88f;
                if (sliderSpeed != null) sliderSpeed.value = 1.16f;
                if (sliderNoise != null) sliderNoise.value = 0.65f;
                if (sliderPitch != null) sliderPitch.value = 0.88f;
                if (statusLabel != null) statusLabel.text = "Nastaven styl: 🎖️ Drsný voják";
            };

            if (inputField == null || speakButton == null)
            {
                Debug.LogError("[PiperDemoUI] UXML elements missing. Check PiperDemoUI.uxml.");
                return;
            }

            if (tts != null)
            {
                tts.OnVoiceChanged += OnVoiceLoaded;
            }

            SetupVoiceDropdown();

            speakButton.clicked += () => Speak(inputField.value);
            if (bum != null) bum.clicked += () =>
            {
                string text = (tts != null && tts.ActiveVoice.StartsWith("en_")) ? BumTestEn : BumTestCz;
                Speak(text);
            };

            if (btnPangramCz != null) btnPangramCz.clicked += () => SetTextAndSelectVoice(PangramCz, "cs_");
            if (btnCombatCz != null) btnCombatCz.clicked += () => SetTextAndSelectVoice(BumTestCz, "cs_");
            if (btnPangramEn != null) btnPangramEn.clicked += () => SetTextAndSelectVoice(PangramEn, "en_");
            if (btnCombatEn != null) btnCombatEn.clicked += () => SetTextAndSelectVoice(BumTestEn, "en_");

            UpdateStatusText();
        }

        private void OnDisable()
        {
            if (tts != null)
            {
                tts.OnVoiceChanged -= OnVoiceLoaded;
            }
        }

        private void SetupVoiceDropdown()
        {
            if (voiceDropdown == null || tts == null) return;

            var voices = tts.VoiceNames.ToList();
            if (voices.Count == 0 && tts.voiceModels.Count > 0)
            {
                voices = tts.voiceModels.Where(v => v != null).Select(v => v.name).ToList();
            }

            voiceDropdown.choices = voices;
            if (!string.IsNullOrEmpty(tts.ActiveVoice))
            {
                voiceDropdown.value = tts.ActiveVoice;
            }
            else if (voices.Count > 0)
            {
                voiceDropdown.value = voices[0];
            }

            voiceDropdown.RegisterValueChangedCallback(evt =>
            {
                if (string.IsNullOrEmpty(evt.newValue) || evt.newValue == tts.ActiveVoice) return;
                if (statusLabel != null) statusLabel.text = $"Načítám model hlasu: {evt.newValue}...";
                tts.SwitchVoice(evt.newValue);
            });
        }

        private void SetTextAndSelectVoice(string text, string langPrefix)
        {
            if (inputField != null) inputField.value = text;

            if (tts != null && !tts.ActiveVoice.StartsWith(langPrefix))
            {
                var matchingVoice = tts.VoiceNames.FirstOrDefault(v => v.StartsWith(langPrefix));
                if (!string.IsNullOrEmpty(matchingVoice))
                {
                    if (voiceDropdown != null) voiceDropdown.value = matchingVoice;
                    tts.SwitchVoice(matchingVoice);
                }
            }
        }

        private void OnVoiceLoaded(string voiceName)
        {
            if (voiceDropdown != null && voiceDropdown.value != voiceName)
            {
                voiceDropdown.value = voiceName;
            }
            UpdateStatusText();
        }

        private void UpdateStatusText()
        {
            if (statusLabel == null) return;
            if (tts == null)
            {
                statusLabel.text = "Chyba: PiperTTS nenalezen.";
                return;
            }

            if (tts.IsReady)
            {
                statusLabel.text = $"✓ Hlas připraven: {tts.ActiveVoice} ({tts.Config?.sampleRate ?? 22050} Hz, {tts.Config?.espeakVoice ?? "cs"})";
            }
            else
            {
                statusLabel.text = $"Načítání hlasu '{tts.ActiveVoice}'...";
            }
        }

        private void Speak(string text)
        {
            if (tts == null)
            {
                Debug.LogError("[PiperDemoUI] 'Tts' (PiperTTS) is not assigned on " + name);
                return;
            }

            if (statusLabel != null)
                statusLabel.text = $"Generuji řeč pro: \"{(text.Length > 35 ? text.Substring(0, 35) + "..." : text)}\"...";

            var audioSource = GetComponent<AudioSource>();
            tts.Generate(text, clip =>
            {
                if (clip != null)
                {
                    if (audioSource != null) audioSource.PlayOneShot(clip);
                    if (statusLabel != null)
                        statusLabel.text = $"✓ Přehrávám ({clip.length:F2}s, {clip.frequency} Hz) [{tts.ActiveVoice}]";
                }
                else
                {
                    if (statusLabel != null)
                        statusLabel.text = "Syntéza nevrátila žádný zvuk (zkontrolujte konzoli).";
                }
            });
        }
    }
}
