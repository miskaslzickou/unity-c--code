using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Piper
{
    /// <summary>
    /// Everything a Piper voice needs, parsed from the voice's .onnx.json file.
    /// Nothing about the voice (sample rate, espeak voice code, scales, phoneme
    /// id map) is hardcoded here - it all comes from the JSON.
    /// </summary>
    public class PiperVoiceConfig
    {
        public int sampleRate;
        public string espeakVoice;
        public string phonemeType;
        public int numSpeakers;
        public float noiseScale;
        public float lengthScale;
        public float noiseW;

        private Dictionary<string, int[]> phonemeIdMap;

        public static PiperVoiceConfig FromJson(string json)
        {
            var dto = JsonConvert.DeserializeObject<VoiceConfigDto>(json);
            if (dto?.audio == null || dto.espeak == null || dto.inference == null || dto.phonemeIdMap == null)
            {
                Debug.LogError("[PiperVoiceConfig] Voice JSON is missing required fields (audio/espeak/inference/phoneme_id_map).");
                return null;
            }

            return new PiperVoiceConfig
            {
                sampleRate = dto.audio.sample_rate,
                espeakVoice = dto.espeak.voice,
                phonemeType = dto.phoneme_type,
                numSpeakers = dto.num_speakers,
                noiseScale = dto.inference.noise_scale,
                lengthScale = dto.inference.length_scale,
                noiseW = dto.inference.noise_w,
                phonemeIdMap = dto.phonemeIdMap,
            };
        }

        /// <summary>
        /// Converts a phoneme string (IPA characters from eSpeak NG) into model
        /// token ids using the standard Piper scheme:
        /// [BOS=1, PAD=0] (phoneme, PAD)* [EOS=2].
        /// </summary>
        public int[] Tokenize(string phonemes)
        {
            var tokens = new List<int>(phonemes.Length * 2 + 3) { 1, 0 };
            foreach (var ch in phonemes)
            {
                var key = ch.ToString();
                if (phonemeIdMap.TryGetValue(key, out var ids) && ids.Length > 0)
                {
                    tokens.Add(ids[0]);
                    tokens.Add(0);
                }
                // Unknown phonemes are skipped silently - eSpeak IPA output
                // contains stress/separator marks that are not in every map.
            }
            tokens.Add(2);
            return tokens.ToArray();
        }

#pragma warning disable 0649 // DTO fields are filled by Newtonsoft.Json
        private class AudioDto { public int sample_rate; public string quality; }
        private class EspeakDto { public string voice; }
        private class InferenceDto { public float noise_scale; public float length_scale; public float noise_w; }
        private class VoiceConfigDto
        {
            public AudioDto audio;
            public EspeakDto espeak;
            public InferenceDto inference;
            public string phoneme_type;
            public int num_speakers;
            [JsonProperty("phoneme_id_map")]
            public Dictionary<string, int[]> phonemeIdMap;
        }
#pragma warning restore 0649
    }
}
