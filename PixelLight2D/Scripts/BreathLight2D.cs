using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Controls the organic "breathing" / pulsing effect for 2D pixel lights.
/// Faithfully reproduces the sine-wave scale modulation from Moka-Wars,
/// with optional light intensity & radius pulsing and time-offset desynchronization.
/// </summary>
[AddComponentMenu("Pixel Light 2D/Breath Light 2D")]
[DisallowMultipleComponent]
public class BreathLight2D : MonoBehaviour
{
    [Header("Wave Motion")]
    [Tooltip("Frequency of the breathing cycle in radians/sec.")]
    [SerializeField] private float waveInterval = 1.0f;

    [Tooltip("Overall strength / magnitude factor of the wave.")]
    [SerializeField] private float wavePower = 1.0f;

    [Tooltip("Relative scale change factor (0.2 matches Moka-Wars default).")]
    [Range(0f, 1f)]
    [SerializeField] private float scaleMultiplier = 0.2f;

    [Header("Behavior Toggles")]
    [Tooltip("Pulse the transform local scale.")]
    [SerializeField] private bool pulseTransformScale = true;

    [Tooltip("Also pulse the 2D Light intensity if a Light2D component is present.")]
    [SerializeField] private bool pulseLightIntensity = false;

    [Tooltip("Intensity pulse range (+/- around base intensity).")]
    [SerializeField] private float intensityDelta = 0.25f;

    [Tooltip("Randomize starting phase so multiple lights do not breathe identically.")]
    [SerializeField] private bool randomizePhase = true;

    [Tooltip("Use unscaled time (ignores Time.timeScale pausing/slowdown).")]
    [SerializeField] private bool useUnscaledTime = false;

    // Internal state
    private Vector3 initialScale;
    private float phaseOffset;
    private Component light2DComponent;
    private PropertyInfo intensityProp;
    private float baseIntensity = 1f;

    public float WaveInterval
    {
        get => waveInterval;
        set => waveInterval = value;
    }

    public float WavePower
    {
        get => wavePower;
        set => wavePower = value;
    }

    public float ScaleMultiplier
    {
        get => scaleMultiplier;
        set => scaleMultiplier = value;
    }

    private void Awake()
    {
        initialScale = transform.localScale;

        if (randomizePhase)
        {
            phaseOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        }

        CacheLight2D();
    }

    private void OnEnable()
    {
        if (initialScale == Vector3.zero)
        {
            initialScale = transform.localScale != Vector3.zero ? transform.localScale : Vector3.one;
        }
    }

    private void Update()
    {
        float time = (useUnscaledTime ? Time.unscaledTime : Time.time) * waveInterval + phaseOffset;
        float wave = Mathf.Sin(time) * wavePower;

        // 1. Modulate Transform Scale (Moka-Wars formula: 1 + sin(t * interval) * power * 0.2)
        if (pulseTransformScale)
        {
            float scaleFactor = 1f + (wave * scaleMultiplier);
            transform.localScale = new Vector3(
                initialScale.x * scaleFactor,
                initialScale.y * scaleFactor,
                initialScale.z
            );
        }

        // 2. Optional: Modulate Light2D intensity
        if (pulseLightIntensity && light2DComponent != null && intensityProp != null)
        {
            float targetIntensity = Mathf.Max(0f, baseIntensity + (wave * intensityDelta));
            intensityProp.SetValue(light2DComponent, targetIntensity, null);
        }
    }

    private void CacheLight2D()
    {
        // Reflection-based lookup ensures this compiles cleanly in any Unity project
        // even if Universal RP / Light2D package is not in the compilation pipeline.
        Component[] components = GetComponents<Component>();
        foreach (Component comp in components)
        {
            if (comp == null) continue;
            Type type = comp.GetType();
            if (type.FullName == "UnityEngine.Rendering.Universal.Light2D")
            {
                light2DComponent = comp;
                intensityProp = type.GetProperty("intensity", BindingFlags.Public | BindingFlags.Instance);
                if (intensityProp != null)
                {
                    object val = intensityProp.GetValue(light2DComponent, null);
                    if (val is float f) baseIntensity = f;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Resets transform scale and light intensity to their baseline values.
    /// </summary>
    public void ResetToBase()
    {
        if (initialScale != Vector3.zero)
        {
            transform.localScale = initialScale;
        }

        if (light2DComponent != null && intensityProp != null)
        {
            intensityProp.SetValue(light2DComponent, baseIntensity, null);
        }
    }

    private void OnDisable()
    {
        ResetToBase();
    }
}
