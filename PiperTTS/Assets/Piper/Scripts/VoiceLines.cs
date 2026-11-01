using System;
using System.Collections.Generic;
using UnityEngine;

namespace Piper
{
    /// <summary>
    /// Named voice events ("explosion", "death", "win", ...) with several Czech
    /// lines each. Play(eventName) picks a random line; PiperTTS generates it
    /// once and caches it, so repeated calls are instant.
    /// </summary>
    public class VoiceLines : MonoBehaviour
    {
        [Serializable]
        public class VoiceEvent
        {
            public string eventName;
            [TextArea] public string[] lines;
        }

        [SerializeField] private PiperTTS tts;

        [Tooltip("If true, all lines are generated at scene start (spread over frames) so Play() is instant later.")]
        [SerializeField] private bool preWarmOnStart = false;

        [SerializeField] private List<VoiceEvent> events = new List<VoiceEvent>
        {
            new VoiceEvent { eventName = "explosion", lines = new[] { "Bum! Tohle bude bolet.", "Au! To byla rána!", "Nálož klasika, pánové!" } },
            new VoiceEvent { eventName = "death", lines = new[] { "Ahoj, krásný světe...", "To bylo rychlé.", "Umíráám!" } },
            new VoiceEvent { eventName = "win", lines = new[] { "Vítězství! Moka je naše!", "Vyhráli jsme, spoluhráči!", "Konečně po čtyřech hodinách!" } },
        };

        public bool PreWarmFinished { get; private set; }

        private System.Collections.IEnumerator Start()
        {
            if (preWarmOnStart && tts != null)
            {
                // Wait until the model is ready, then generate every line once.
                while (!tts.IsReady) yield return null;
                foreach (var ev in events)
                    foreach (var line in ev.lines)
                        tts.Generate(line, null);
            }
        }

        /// <summary>Picks a random line of the event, generates it (cached) and plays it.</summary>
        public void Play(string eventName, AudioSource target)
        {
            if (tts == null)
            {
                Debug.LogWarning("[VoiceLines] No PiperTTS assigned.");
                return;
            }
            var ev = events.Find(e => e.eventName == eventName);
            if (ev == null || ev.lines == null || ev.lines.Length == 0)
            {
                Debug.LogWarning($"[VoiceLines] Unknown or empty event '{eventName}'.");
                return;
            }
            tts.Speak(ev.lines[UnityEngine.Random.Range(0, ev.lines.Length)], target);
        }
    }
}
