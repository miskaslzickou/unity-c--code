using System;
using UnityEngine;
using GameSystems.Persistence;

/// <summary>
/// Supported basic oscillator waveforms.
/// </summary>
public enum SynthWaveform
{
    Sine,
    Sawtooth,
    Square,
    Triangle,
    Pulse,
    WhiteNoise,
    PinkNoise
}

/// <summary>
/// Operational preset models for procedural generation.
/// </summary>
public enum SynthPreset
{
    Custom,
    MultiCylinderEngine,
    RadarWarningReceiver,
    ReactorSubBassHum,
    SciFiLaserTransient,
    SonarAcousticPing
}

/// <summary>
/// Standard 4-stage ADSR envelope generator.
/// </summary>
[System.Serializable]
public struct ADSREnvelope
{
    public float AttackTime;  // seconds
    public float DecayTime;   // seconds
    public float SustainLevel; // 0..1
    public float ReleaseTime; // seconds

    private float currentLevel;
    private int state; // 0: Idle, 1: Attack, 2: Decay, 3: Sustain, 4: Release
    private float sampleRate;

    public ADSREnvelope(float attack, float decay, float sustain, float release, float sampleRate = 48000f)
    {
        AttackTime = Mathf.Max(0.001f, attack);
        DecayTime = Mathf.Max(0.001f, decay);
        SustainLevel = Mathf.Clamp01(sustain);
        ReleaseTime = Mathf.Max(0.001f, release);
        currentLevel = 0f;
        state = 0;
        this.sampleRate = Mathf.Max(1000f, sampleRate);
    }

    public void SetSampleRate(float sr)
    {
        sampleRate = Mathf.Max(1000f, sr);
    }

    public void NoteOn()
    {
        state = 1; // Attack
    }

    public void NoteOff()
    {
        if (state != 0) state = 4; // Release
    }

    public float EvaluateNextSample()
    {
        switch (state)
        {
            case 1: // Attack
                currentLevel += 1f / (AttackTime * sampleRate);
                if (currentLevel >= 1f)
                {
                    currentLevel = 1f;
                    state = 2; // Transition to Decay
                }
                break;

            case 2: // Decay
                currentLevel -= (1f - SustainLevel) / (DecayTime * sampleRate);
                if (currentLevel <= SustainLevel)
                {
                    currentLevel = SustainLevel;
                    state = 3; // Sustain
                }
                break;

            case 3: // Sustain
                currentLevel = SustainLevel;
                break;

            case 4: // Release
                currentLevel -= SustainLevel / (ReleaseTime * sampleRate);
                if (currentLevel <= 0f)
                {
                    currentLevel = 0f;
                    state = 0; // Idle
                }
                break;

            default:
                currentLevel = 0f;
                break;
        }

        return currentLevel;
    }

    public bool IsActive => state != 0;
}

/// <summary>
/// Direct Form I / II Biquad digital filter (Low-Pass, High-Pass, Band-Pass).
/// </summary>
public struct BiquadFilter
{
    public enum FilterType { LowPass, HighPass, BandPass, Notch }

    private float b0, b1, b2, a1, a2;
    private float x1, x2, y1, y2;

    public void Configure(FilterType type, float cutoffFrequency, float q, float sampleRate)
    {
        cutoffFrequency = Mathf.Clamp(cutoffFrequency, 20f, sampleRate * 0.49f);
        q = Mathf.Max(0.1f, q);

        float omega = 2f * Mathf.PI * cutoffFrequency / sampleRate;
        float alpha = Mathf.Sin(omega) / (2f * q);
        float cosOmega = Mathf.Cos(omega);

        float a0 = 1f + alpha;

        switch (type)
        {
            case FilterType.LowPass:
                b0 = (1f - cosOmega) * 0.5f / a0;
                b1 = (1f - cosOmega) / a0;
                b2 = (1f - cosOmega) * 0.5f / a0;
                a1 = -2f * cosOmega / a0;
                a2 = (1f - alpha) / a0;
                break;

            case FilterType.HighPass:
                b0 = (1f + cosOmega) * 0.5f / a0;
                b1 = -(1f + cosOmega) / a0;
                b2 = (1f + cosOmega) * 0.5f / a0;
                a1 = -2f * cosOmega / a0;
                a2 = (1f - alpha) / a0;
                break;

            case FilterType.BandPass:
                b0 = alpha / a0;
                b1 = 0f;
                b2 = -alpha / a0;
                a1 = -2f * cosOmega / a0;
                a2 = (1f - alpha) / a0;
                break;

            case FilterType.Notch:
                b0 = 1f / a0;
                b1 = -2f * cosOmega / a0;
                b2 = 1f / a0;
                a1 = -2f * cosOmega / a0;
                a2 = (1f - alpha) / a0;
                break;
        }
    }

    public float Process(float inSample)
    {
        float outSample = b0 * inSample + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1;
        x1 = inSample;
        y2 = y1;
        y1 = outSample;

        // Prevent floating point denormals
        if (Mathf.Abs(outSample) < 1e-15f) outSample = 0f;
        return outSample;
    }

    public void Reset()
    {
        x1 = x2 = y1 = y2 = 0f;
    }
}

/// <summary>
/// Real-time zero-GC procedural audio synthesizer via OnAudioFilterRead.
/// Synthesizes combustion engine roar, radar warning tones, reactor humming, and sci-fi transients.
/// </summary>
[RequireComponent(typeof(AudioSource))]
[DisallowMultipleComponent]
public class ProceduralAudioSynthesizer : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Preset")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private SynthPreset currentPreset = SynthPreset.MultiCylinderEngine;
    [SerializeField, Range(0f, 1f)] private float masterVolume = 0.5f;

    [Header("Engine Synthesis Parameters")]
    [SerializeField, Range(500f, 9000f)] private float engineRPM = 2500f;
    [SerializeField, Range(0f, 1f)] private float engineLoad = 0.5f;
    [SerializeField, Range(1, 16)] private int cylinderCount = 4;

    [Header("FM / Sub-Bass Parameters")]
    [SerializeField, Range(20f, 2000f)] private float carrierFrequency = 60f;
    [SerializeField, Range(1f, 1000f)] private float modulatorFrequency = 120f;
    [SerializeField, Range(0f, 50f)] private float fmIndex = 5f;

    [Header("Filter & Envelope")]
    [SerializeField] private BiquadFilter.FilterType filterType = BiquadFilter.FilterType.LowPass;
    [SerializeField, Range(40f, 16000f)] private float filterCutoff = 2500f;
    [SerializeField, Range(0.1f, 10f)] private float filterResonance = 1.0f;
    [SerializeField] private ADSREnvelope envelope = new ADSREnvelope(0.01f, 0.1f, 0.7f, 0.3f);

    // DSP Runtime State (Runs on Audio Thread - No GC Allocations Allowed)
    private float sampleRate = 48000f;
    private double phaseCarrier = 0.0;
    private double phaseModulator = 0.0;
    private double phaseSub1 = 0.0;
    private double phaseSub2 = 0.0;

    // Filter instance
    private BiquadFilter filter;

    // Pink noise Voss-McCartney state
    private float pinkB0, pinkB1, pinkB2, pinkB3, pinkB4, pinkB5, pinkB6;

    // RWR warning tone generator state
    private float rwrTimer = 0f;
    private bool rwrPulsing = false;
    private bool isMissileLock = false;

    // Transient laser sweep state
    private float transientPitchMult = 1f;
    private float transientDecayRate = 0f;

    // Unity AudioSource reference
    private AudioSource audioSource;

    public string SaveKey => "ProceduralAudioSynthesizer_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public SynthPreset CurrentPreset => currentPreset;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000f;

        envelope.SetSampleRate(sampleRate);
        filter.Configure(filterType, filterCutoff, filterResonance, sampleRate);

        ApplySpatialMode();

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }
    }

    private void OnDestroy()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    private void Start()
    {
        if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }
        envelope.NoteOn();
    }

    public void ApplySpatialMode()
    {
        if (audioSource != null)
        {
            audioSource.spatialBlend = defaultTo2D ? 0f : 1f;
            audioSource.loop = true;
        }
    }

    // =========================================================================
    // RUNTIME CONTROL APIS
    // =========================================================================

    public void SetPreset(SynthPreset preset)
    {
        currentPreset = preset;
        switch (preset)
        {
            case SynthPreset.MultiCylinderEngine:
                filterCutoff = 1800f;
                filterResonance = 0.8f;
                break;
            case SynthPreset.RadarWarningReceiver:
                carrierFrequency = 800f;
                break;
            case SynthPreset.ReactorSubBassHum:
                carrierFrequency = 50f;
                modulatorFrequency = 100f;
                fmIndex = 8f;
                filterCutoff = 350f;
                break;
        }
    }

    public void SetEngineParameters(float rpm, float load)
    {
        engineRPM = Mathf.Clamp(rpm, 400f, 12000f);
        engineLoad = Mathf.Clamp01(load);
    }

    public void TriggerLaserTransient(float baseFrequency = 1200f, float sweepDuration = 0.18f)
    {
        currentPreset = SynthPreset.SciFiLaserTransient;
        carrierFrequency = baseFrequency;
        transientPitchMult = 1f;
        transientDecayRate = 1f / Mathf.Max(0.01f, sweepDuration);
        envelope = new ADSREnvelope(0.002f, sweepDuration, 0.0f, 0.02f, sampleRate);
        envelope.NoteOn();
    }

    public void TriggerRWRWarning(bool missileHardLock)
    {
        currentPreset = SynthPreset.RadarWarningReceiver;
        isMissileLock = missileHardLock;
        rwrTimer = 0f;
        envelope.NoteOn();
    }

    public void TriggerSonarPing(float frequency = 1400f, float pingDuration = 0.6f)
    {
        currentPreset = SynthPreset.SonarAcousticPing;
        carrierFrequency = frequency;
        envelope = new ADSREnvelope(0.01f, pingDuration * 0.7f, 0.1f, pingDuration * 0.3f, sampleRate);
        envelope.NoteOn();
    }

    public void NoteOn() => envelope.NoteOn();
    public void NoteOff() => envelope.NoteOff();

    // =========================================================================
    // AUDIO THREAD DSP PIPELINE (ZERO-GC ALLOCATIONS)
    // =========================================================================

    private void OnAudioFilterRead(float[] data, int channels)
    {
        int dataLen = data.Length;
        filter.Configure(filterType, filterCutoff, filterResonance, sampleRate);

        for (int i = 0; i < dataLen; i += channels)
        {
            float envSample = envelope.EvaluateNextSample();
            float rawSample = 0f;

            switch (currentPreset)
            {
                case SynthPreset.MultiCylinderEngine:
                    rawSample = GenerateEngineSample();
                    break;

                case SynthPreset.RadarWarningReceiver:
                    rawSample = GenerateRWRSample();
                    break;

                case SynthPreset.ReactorSubBassHum:
                    rawSample = GenerateReactorSample();
                    break;

                case SynthPreset.SciFiLaserTransient:
                    rawSample = GenerateLaserSample();
                    break;

                case SynthPreset.SonarAcousticPing:
                    rawSample = GenerateSonarSample();
                    break;

                default:
                    rawSample = GenerateFMSample(carrierFrequency, modulatorFrequency, fmIndex);
                    break;
            }

            // Process through biquad filter
            float filteredSample = filter.Process(rawSample);
            float finalSample = Mathf.Clamp(filteredSample * envSample * masterVolume, -1f, 1f);

            // Write to all channels (Stereo or Surround)
            for (int c = 0; c < channels; c++)
            {
                data[i + c] = finalSample;
            }
        }
    }

    // =========================================================================
    // PRESET DSP GENERATORS
    // =========================================================================

    /// <summary>
    /// Synthesizes multi-cylinder internal combustion engine pulses using asymmetric sawtooth + noise exhaust rumble.
    /// </summary>
    private float GenerateEngineSample()
    {
        // Fundamental firing frequency: F_fire = (RPM / 60) * (Cylinders / 2)
        float firingFreq = (engineRPM / 60f) * (cylinderCount * 0.5f);
        double phaseInc = firingFreq / sampleRate;

        phaseCarrier += phaseInc;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        // Asymmetric pulse train
        float pulse = (float)(2.0 * phaseCarrier - 1.0);
        // Cylinder pressure peak (asymmetrical wave shaping)
        float combustion = Mathf.Pow(Mathf.Abs(pulse), 1.5f + (engineLoad * 2.0f)) * Mathf.Sign(pulse);

        // Sub-harmonic crank vibration
        phaseSub1 += (phaseInc * 0.5);
        if (phaseSub1 >= 1.0) phaseSub1 -= 1.0;
        float subHarmonic = Mathf.Sin((float)(phaseSub1 * 2.0 * Math.PI));

        // Intake/exhaust turbulent noise
        float noise = GeneratePinkNoise() * (0.05f + (engineLoad * 0.2f));

        return (combustion * 0.65f) + (subHarmonic * 0.25f) + noise;
    }

    /// <summary>
    /// Synthesizes aviation Radar Warning Receiver threat tones (pulsed square wave at 800 Hz or 1600 Hz).
    /// </summary>
    private float GenerateRWRSample()
    {
        float targetFreq = isMissileLock ? 1600f : 800f;
        float pulseRate = isMissileLock ? 12f : 3f; // Fast chirp vs steady pulse

        rwrTimer += 1f / sampleRate;
        if (rwrTimer >= (1f / pulseRate))
        {
            rwrTimer = 0f;
            rwrPulsing = !rwrPulsing;
        }

        if (!rwrPulsing && !isMissileLock) return 0f;

        phaseCarrier += targetFreq / sampleRate;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        // Band-limited square wave approximation
        return (phaseCarrier < 0.5) ? 0.7f : -0.7f;
    }

    /// <summary>
    /// Generates deep sci-fi reactor or warp core sub-bass humming with 50/60Hz mains hum harmonics.
    /// </summary>
    private float GenerateReactorSample()
    {
        double modInc = modulatorFrequency / sampleRate;
        phaseModulator += modInc;
        if (phaseModulator >= 1.0) phaseModulator -= 1.0;

        float modVal = Mathf.Sin((float)(phaseModulator * 2.0 * Math.PI)) * fmIndex;

        double carInc = (carrierFrequency + modVal) / sampleRate;
        phaseCarrier += carInc;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        float carrier = Mathf.Sin((float)(phaseCarrier * 2.0 * Math.PI));

        // 50Hz sub-bass rumble
        phaseSub1 += 50.0 / sampleRate;
        if (phaseSub1 >= 1.0) phaseSub1 -= 1.0;
        float hum50 = Mathf.Sin((float)(phaseSub1 * 2.0 * Math.PI)) * 0.4f;

        return (carrier * 0.6f) + hum50;
    }

    /// <summary>
    /// Fast downward pitch sweep simulating laser blaster or projectile crack.
    /// </summary>
    private float GenerateLaserSample()
    {
        transientPitchMult = Mathf.Max(0.05f, transientPitchMult - (transientDecayRate / sampleRate));
        float currentFreq = carrierFrequency * transientPitchMult;

        phaseCarrier += currentFreq / sampleRate;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        float wave = (float)(2.0 * phaseCarrier - 1.0); // Sawtooth snap
        return wave * transientPitchMult;
    }

    /// <summary>
    /// Clean sinusoidal acoustic sonar ping with slight frequency modulation.
    /// </summary>
    private float GenerateSonarSample()
    {
        phaseCarrier += carrierFrequency / sampleRate;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        return Mathf.Sin((float)(phaseCarrier * 2.0 * Math.PI));
    }

    /// <summary>
    /// Generic 2-operator Frequency Modulation generator.
    /// </summary>
    private float GenerateFMSample(float carrierHz, float modHz, float index)
    {
        phaseModulator += modHz / sampleRate;
        if (phaseModulator >= 1.0) phaseModulator -= 1.0;

        float mod = Mathf.Sin((float)(phaseModulator * 2.0 * Math.PI)) * index;

        phaseCarrier += (carrierHz + mod) / sampleRate;
        if (phaseCarrier >= 1.0) phaseCarrier -= 1.0;

        return Mathf.Sin((float)(phaseCarrier * 2.0 * Math.PI));
    }

    /// <summary>
    /// Voss-McCartney 1/f Pink Noise generator (Zero allocations).
    /// </summary>
    private float GeneratePinkNoise()
    {
        float white = (UnityEngine.Random.value * 2f) - 1f;

        pinkB0 = 0.99886f * pinkB0 + white * 0.0555179f;
        pinkB1 = 0.99332f * pinkB1 + white * 0.0750759f;
        pinkB2 = 0.96900f * pinkB2 + white * 0.1538520f;
        pinkB3 = 0.86650f * pinkB3 + white * 0.3104856f;
        pinkB4 = 0.55000f * pinkB4 + white * 0.5329522f;
        pinkB5 = -0.7616f * pinkB5 - white * 0.0168980f;

        float pink = pinkB0 + pinkB1 + pinkB2 + pinkB3 + pinkB4 + pinkB5 + pinkB6 + white * 0.5362f;
        pinkB6 = white * 0.115926f;
        return pink * 0.11f;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public SynthPreset Preset;
        public float MasterVolume;
        public float EngineRPM;
        public float EngineLoad;
        public float CarrierFrequency;
        public float FilterCutoff;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Preset = currentPreset,
            MasterVolume = masterVolume,
            EngineRPM = engineRPM,
            EngineLoad = engineLoad,
            CarrierFrequency = carrierFrequency,
            FilterCutoff = filterCutoff
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentPreset = container.Preset;
        masterVolume = container.MasterVolume;
        engineRPM = container.EngineRPM;
        engineLoad = container.EngineLoad;
        carrierFrequency = container.CarrierFrequency;
        filterCutoff = container.FilterCutoff;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
        float radius = audioSource != null ? audioSource.maxDistance : 15f;

        if (defaultTo2D)
        {
            Vector3 pos = new Vector3(transform.position.x, transform.position.y, 0f);
            Gizmos.DrawWireSphere(pos, radius);
        }
        else
        {
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
