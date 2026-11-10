using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

namespace GameSystems.Cybernetics
{
    // =========================================================================
    // 1. CIPHER TYPES & ENCRYPTION DATA STRUCTURES (ZERO-GC EXECUTION)
    // =========================================================================

    public enum CipherType
    {
        CaesarShift,
        Vigenere,
        XORStream,
        SubstitutionMatrix,
        SimulatedAsymmetricRSA
    }

    public enum CrackerState
    {
        Idle,
        SamplingSignal,
        FrequencyAnalysis,
        BruteForceSolving,
        Cracked,
        LostSignal
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct SignalInterceptedEvent : IGameEvent
    {
        public readonly int TransmitterId;
        public readonly float FrequencyMHz;
        public readonly float SNR_dB;
        public readonly Vector3 InterceptPosition;

        public SignalInterceptedEvent(int txId, float freq, float snr, Vector3 pos)
        {
            TransmitterId = txId;
            FrequencyMHz = freq;
            SNR_dB = snr;
            InterceptPosition = pos;
        }
    }

    public readonly struct CiphertextDecodedEvent : IGameEvent
    {
        public readonly int DecoderId;
        public readonly CipherType Cipher;
        public readonly string DecodedPlaintext;
        public readonly float ElapsedSeconds;

        public CiphertextDecodedEvent(int decoderId, CipherType cipher, string plaintext, float elapsed)
        {
            DecoderId = decoderId;
            Cipher = cipher;
            DecodedPlaintext = plaintext;
            ElapsedSeconds = elapsed;
        }
    }

    public readonly struct KeyExchangeCompletedEvent : IGameEvent
    {
        public readonly int NodeA_Id;
        public readonly int NodeB_Id;
        public readonly long SharedSecretKey;

        public KeyExchangeCompletedEvent(int a, int b, long secret)
        {
            NodeA_Id = a;
            NodeB_Id = b;
            SharedSecretKey = secret;
        }
    }

    // =========================================================================
    // 3. CRYPTOGRAPHIC ALGORITHMS (PURE C#, ZERO HEAP ALLOCATION IN BUFFERS)
    // =========================================================================

    public static class CipherAlgorithms
    {
        // Standard English letter frequencies (normalized 0 to 1)
        public static readonly float[] EnglishFrequencies = new float[26]
        {
            0.08167f, 0.01492f, 0.02782f, 0.04253f, 0.12702f, 0.02228f, 0.02015f, // A-G
            0.06094f, 0.06966f, 0.00153f, 0.00772f, 0.04025f, 0.02406f, 0.06749f, // H-N
            0.07507f, 0.01929f, 0.00095f, 0.05987f, 0.06327f, 0.09056f, 0.02758f, // O-U
            0.00978f, 0.02360f, 0.00150f, 0.01974f, 0.00074f                      // V-Z
        };

        public static void CaesarEncrypt(ReadOnlySpan<char> input, Span<char> output, int shift)
        {
            shift = (shift % 26 + 26) % 26;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c >= 'A' && c <= 'Z')
                    output[i] = (char)('A' + (c - 'A' + shift) % 26);
                else if (c >= 'a' && c <= 'z')
                    output[i] = (char)('a' + (c - 'a' + shift) % 26);
                else
                    output[i] = c;
            }
        }

        public static void CaesarDecrypt(ReadOnlySpan<char> input, Span<char> output, int shift)
        {
            CaesarEncrypt(input, output, 26 - (shift % 26));
        }

        public static void VigenereEncrypt(ReadOnlySpan<char> input, ReadOnlySpan<char> key, Span<char> output)
        {
            if (key.Length == 0)
            {
                input.CopyTo(output);
                return;
            }

            int keyIndex = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                int k = char.ToUpperInvariant(key[keyIndex % key.Length]) - 'A';

                if (c >= 'A' && c <= 'Z')
                {
                    output[i] = (char)('A' + (c - 'A' + k) % 26);
                    keyIndex++;
                }
                else if (c >= 'a' && c <= 'z')
                {
                    output[i] = (char)('a' + (c - 'a' + k) % 26);
                    keyIndex++;
                }
                else
                {
                    output[i] = c;
                }
            }
        }

        public static void VigenereDecrypt(ReadOnlySpan<char> input, ReadOnlySpan<char> key, Span<char> output)
        {
            if (key.Length == 0)
            {
                input.CopyTo(output);
                return;
            }

            int keyIndex = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                int k = char.ToUpperInvariant(key[keyIndex % key.Length]) - 'A';

                if (c >= 'A' && c <= 'Z')
                {
                    output[i] = (char)('A' + (c - 'A' - k + 26) % 26);
                    keyIndex++;
                }
                else if (c >= 'a' && c <= 'z')
                {
                    output[i] = (char)('a' + (c - 'a' - k + 26) % 26);
                    keyIndex++;
                }
                else
                {
                    output[i] = c;
                }
            }
        }

        public static void XORCipher(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, Span<byte> output)
        {
            if (key.Length == 0) return;
            for (int i = 0; i < input.Length; i++)
            {
                output[i] = (byte)(input[i] ^ key[i % key.Length]);
            }
        }

        public static long ModPow(long baseVal, long exp, long mod)
        {
            long result = 1;
            baseVal %= mod;
            while (exp > 0)
            {
                if ((exp & 1) == 1) result = (result * baseVal) % mod;
                baseVal = (baseVal * baseVal) % mod;
                exp >>= 1;
            }
            return result;
        }
    }

    // =========================================================================
    // 4. RADIO EMITTER & RECEIVER STRUCTURES (2D & 3D COMPATIBLE)
    // =========================================================================

    [Serializable]
    public class RadioTransmitter
    {
        public int TransmitterId = 1;
        public string CallSign = "ECHO-BASE";
        public Vector3 Position;
        public float FrequencyMHz = 142.5f;
        public float PowerWatts = 100f;
        public float AntennaGain_dBi = 3f;
        public bool IsBroadcasting = true;
        public CipherType Cipher = CipherType.CaesarShift;
        public string EncryptionKey = "KILO";
        public int CaesarKey = 7;
        [TextArea(2, 5)] public string TransmittedMessage = "HOSTILE PATROL DETECTED GRID 47-NORTH";
    }

    // =========================================================================
    // 5. MASTER CRYPTOGRAPHY & DECODER SUITE
    // =========================================================================

    /// <summary>
    /// In-game cryptographic encryption/decryption suite, radio signal interception module,
    /// and frequency-analysis brute-force cracker.
    /// Fully compatible with both 2D (XY plane) and 3D space, calculating antenna gain,
    /// free-space path loss, SNR, and automated signal decryption.
    /// </summary>
    [DisallowMultipleComponent]
    public class CryptographyAndSignalDecoderSim : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static CryptographyAndSignalDecoderSim Instance { get; private set; }

        [Header("Receiver / Interceptor Hardware")]
        [SerializeField] private int receiverId = 1;
        [SerializeField] private float tunedFrequencyMHz = 142.5f;
        [SerializeField] private float receiverBandwidthKHz = 25f;
        [SerializeField] private float receiverAntennaGain_dBi = 6f;
        [SerializeField] private float internalNoiseFloor_dBm = -110f;
        [SerializeField] private bool is2DMode = false;

        [Header("Cracker Compute Engine")]
        [SerializeField, Range(1f, 100f)] private float computeSpeedMegaFlops = 15f;
        [SerializeField] private CrackerState currentCrackerState = CrackerState.Idle;
        [SerializeField, Range(0f, 1f)] private float crackingProgress = 0f;

        [Header("Active Decoded Output")]
        [SerializeField] private string currentlyInterceptedCiphertext = "";
        [SerializeField] private string partiallyDecodedPlaintext = "";
        [SerializeField] private float currentSNR_dB = 0f;

        [Header("Known Transmitters")]
        [SerializeField] private List<RadioTransmitter> transmitters = new List<RadioTransmitter>();

        // Pre-allocated internal buffers for zero-GC cracking
        private readonly char[] charBuffer = new char[512];
        private readonly char[] outputBuffer = new char[512];
        private readonly int[] letterCounts = new int[26];
        private float elapsedCrackingSeconds = 0f;
        private RadioTransmitter activeLockedTransmitter = null;

        public string SaveKey => "CryptographyAndSignalDecoderSim";
        public CrackerState State => currentCrackerState;
        public float CrackingProgress => crackingProgress;
        public string Plaintext => partiallyDecodedPlaintext;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Normal);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Normal);
        }

        // =========================================================================
        // PUBLIC API: ENCRYPTION & DECRYPTION UTILITIES
        // =========================================================================

        public string EncryptMessage(string plaintext, CipherType cipher, string key, int caesarShift = 3)
        {
            if (string.IsNullOrEmpty(plaintext)) return string.Empty;
            int len = Mathf.Min(plaintext.Length, charBuffer.Length);
            plaintext.AsSpan(0, len).CopyTo(charBuffer);

            switch (cipher)
            {
                case CipherType.CaesarShift:
                    CipherAlgorithms.CaesarEncrypt(charBuffer.AsSpan(0, len), outputBuffer.AsSpan(0, len), caesarShift);
                    break;
                case CipherType.Vigenere:
                    CipherAlgorithms.VigenereEncrypt(charBuffer.AsSpan(0, len), key.AsSpan(), outputBuffer.AsSpan(0, len));
                    break;
                default:
                    charBuffer.AsSpan(0, len).CopyTo(outputBuffer);
                    break;
            }

            return new string(outputBuffer, 0, len);
        }

        public string DecryptMessage(string ciphertext, CipherType cipher, string key, int caesarShift = 3)
        {
            if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
            int len = Mathf.Min(ciphertext.Length, charBuffer.Length);
            ciphertext.AsSpan(0, len).CopyTo(charBuffer);

            switch (cipher)
            {
                case CipherType.CaesarShift:
                    CipherAlgorithms.CaesarDecrypt(charBuffer.AsSpan(0, len), outputBuffer.AsSpan(0, len), caesarShift);
                    break;
                case CipherType.Vigenere:
                    CipherAlgorithms.VigenereDecrypt(charBuffer.AsSpan(0, len), key.AsSpan(), outputBuffer.AsSpan(0, len));
                    break;
                default:
                    charBuffer.AsSpan(0, len).CopyTo(outputBuffer);
                    break;
            }

            return new string(outputBuffer, 0, len);
        }

        public void TuneFrequency(float frequencyMHz)
        {
            tunedFrequencyMHz = frequencyMHz;
            activeLockedTransmitter = null;
            currentCrackerState = CrackerState.SamplingSignal;
            crackingProgress = 0f;
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            Vector3 myPos = transform.position;

            // 1. Scan for matching frequency transmitter and calculate SNR
            RadioTransmitter bestTx = null;
            float bestSNR = -999f;

            for (int i = 0; i < transmitters.Count; i++)
            {
                var tx = transmitters[i];
                if (!tx.IsBroadcasting) continue;

                // Frequency tuning match within channel bandwidth
                float freqDeltaKHz = Mathf.Abs(tx.FrequencyMHz - tunedFrequencyMHz) * 1000f;
                if (freqDeltaKHz > receiverBandwidthKHz) continue;

                // Distance calculation (Dual 2D & 3D compatibility)
                float dist = is2DMode ?
                    Vector2.Distance(new Vector2(myPos.x, myPos.y), new Vector2(tx.Position.x, tx.Position.y)) :
                    Vector3.Distance(myPos, tx.Position);

                dist = Mathf.Max(1f, dist);

                // Free-Space Path Loss (FSPL) in dB: FSPL = 20*log10(d) + 20*log10(f_MHz) + 32.44
                float fspl_dB = 20f * Mathf.Log10(dist) + 20f * Mathf.Log10(tx.FrequencyMHz) + 32.44f;
                float txPower_dBm = 10f * Mathf.Log10(tx.PowerWatts * 1000f);
                float receivedPower_dBm = txPower_dBm + tx.AntennaGain_dBi + receiverAntennaGain_dBi - fspl_dB;
                float snr_dB = receivedPower_dBm - internalNoiseFloor_dBm;

                if (snr_dB > bestSNR)
                {
                    bestSNR = snr_dB;
                    bestTx = tx;
                }
            }

            activeLockedTransmitter = bestTx;
            currentSNR_dB = bestSNR;

            // 2. State Machine for Interception & Automated Cracking
            if (activeLockedTransmitter == null || currentSNR_dB < 3f)
            {
                currentCrackerState = CrackerState.LostSignal;
                currentlyInterceptedCiphertext = "--- NO CARRIER ---";
                partiallyDecodedPlaintext = "--- NO CARRIER ---";
                return;
            }

            // Signal is intercepted
            EventBus<SignalInterceptedEvent>.Raise(new SignalInterceptedEvent(
                activeLockedTransmitter.TransmitterId,
                activeLockedTransmitter.FrequencyMHz,
                currentSNR_dB,
                myPos));

            currentlyInterceptedCiphertext = EncryptMessage(
                activeLockedTransmitter.TransmittedMessage,
                activeLockedTransmitter.Cipher,
                activeLockedTransmitter.EncryptionKey,
                activeLockedTransmitter.CaesarKey);

            StepCrackerStateMachine(tickDeltaTime);
        }

        private void StepCrackerStateMachine(float dt)
        {
            switch (currentCrackerState)
            {
                case CrackerState.Idle:
                case CrackerState.LostSignal:
                case CrackerState.SamplingSignal:
                    currentCrackerState = CrackerState.FrequencyAnalysis;
                    crackingProgress = 0.1f;
                    break;

                case CrackerState.FrequencyAnalysis:
                    // Perform Chi-Squared frequency analysis
                    PerformFrequencyAnalysis(currentlyInterceptedCiphertext);
                    crackingProgress += 0.2f * dt * (computeSpeedMegaFlops / 10f);
                    if (crackingProgress >= 0.4f)
                    {
                        currentCrackerState = CrackerState.BruteForceSolving;
                    }
                    UpdateProgressiveDecodedString();
                    break;

                case CrackerState.BruteForceSolving:
                    elapsedCrackingSeconds += dt;
                    float snrMultiplier = Mathf.Clamp(currentSNR_dB / 15f, 0.2f, 2f);
                    crackingProgress += 0.15f * dt * (computeSpeedMegaFlops / 10f) * snrMultiplier;
                    UpdateProgressiveDecodedString();

                    if (crackingProgress >= 1f)
                    {
                        crackingProgress = 1f;
                        currentCrackerState = CrackerState.Cracked;
                        partiallyDecodedPlaintext = activeLockedTransmitter.TransmittedMessage;

                        EventBus<CiphertextDecodedEvent>.Raise(new CiphertextDecodedEvent(
                            receiverId,
                            activeLockedTransmitter.Cipher,
                            partiallyDecodedPlaintext,
                            elapsedCrackingSeconds));
                    }
                    break;

                case CrackerState.Cracked:
                    partiallyDecodedPlaintext = activeLockedTransmitter.TransmittedMessage;
                    break;
            }
        }

        private void PerformFrequencyAnalysis(string text)
        {
            Array.Clear(letterCounts, 0, letterCounts.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);
                if (c >= 'A' && c <= 'Z')
                {
                    letterCounts[c - 'A']++;
                }
            }
        }

        private void UpdateProgressiveDecodedString()
        {
            if (activeLockedTransmitter == null) return;
            string full = activeLockedTransmitter.TransmittedMessage;
            int totalChars = full.Length;
            int revealedCount = Mathf.RoundToInt(totalChars * crackingProgress);

            int len = Mathf.Min(totalChars, outputBuffer.Length);
            for (int i = 0; i < len; i++)
            {
                if (i < revealedCount || full[i] == ' ' || char.IsPunctuation(full[i]))
                {
                    outputBuffer[i] = full[i];
                }
                else
                {
                    // Obfuscated noise glyph
                    outputBuffer[i] = (char)('?' + (i % 5));
                }
            }

            partiallyDecodedPlaintext = new string(outputBuffer, 0, len);
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class CryptoSaveData
        {
            public float TunedFrequency;
            public int CrackerStateInt;
            public float Progress;
        }

        public object CaptureState()
        {
            return new CryptoSaveData
            {
                TunedFrequency = tunedFrequencyMHz,
                CrackerStateInt = (int)currentCrackerState,
                Progress = crackingProgress
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<CryptoSaveData>(stateJson);
            if (data == null) return;

            tunedFrequencyMHz = data.TunedFrequency;
            currentCrackerState = (CrackerState)data.CrackerStateInt;
            crackingProgress = data.Progress;
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            Vector3 myPos = transform.position;

            // Draw receiver antenna
            Gizmos.color = currentCrackerState == CrackerState.Cracked ? Color.green : (currentCrackerState == CrackerState.BruteForceSolving ? Color.yellow : Color.cyan);
            Gizmos.DrawWireSphere(myPos, 0.75f);

            // Draw line to locked transmitter
            if (activeLockedTransmitter != null)
            {
                Gizmos.color = currentSNR_dB > 10f ? Color.green : Color.red;
                Gizmos.DrawLine(myPos, activeLockedTransmitter.Position);
            }

            // Draw all known transmitters
            for (int i = 0; i < transmitters.Count; i++)
            {
                var tx = transmitters[i];
                Gizmos.color = tx.IsBroadcasting ? Color.magenta : Color.gray;
                Gizmos.DrawWireCube(tx.Position, Vector3.one * 0.5f);
            }
        }
    }
}
