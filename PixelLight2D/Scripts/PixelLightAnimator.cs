using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// General animator for 2D lights: supports steady breathing, torch/candle flickering,
/// stuttering neon signs, and pulsing with customizable noise and curves.
/// </summary>
[AddComponentMenu("Pixel Light 2D/Pixel Light Animator")]
public class PixelLightAnimator : MonoBehaviour
{
    public enum AnimationMode
    {
        Breathe,    // Smooth sine wave (scale or intensity)
        Flicker,    // Random jitter / candle / fire effect
        Stutter,    // Periodic electrical glitch / neon flicker
        Pulse       // Sharp beat / heartbeat pulse
    }

    [Header("Preset & Profile")]
    [SerializeField] private AnimationMode mode = AnimationMode.Breathe;
    [SerializeField] private float speed = 1.5f;
    [Range(0f, 2f)]
    [SerializeField] private float intensityVariation = 0.35f;

    [Header("Targets")]
    [Tooltip("Modulate transform scale.")]
    [SerializeField] private bool modulateScale = false;
    [Tooltip("Modulate Light2D intensity.")]
    [SerializeField] private bool modulateIntensity = true;

    [Header("Flicker Fine-Tuning")]
    [Range(0f, 1f)]
    [SerializeField] private float flickerRoughness = 0.65f;

    private Component light2D;
    private PropertyInfo intensityProp;
    private float baseIntensity = 1f;
    private Vector3 baseScale;
    private float seed;

    private void Awake()
    {
        baseScale = transform.localScale;
        seed = UnityEngine.Random.Range(0f, 1000f);
        FindLight2D();
    }

    private void FindLight2D()
    {
        Component[] comps = GetComponents<Component>();
        foreach (Component c in comps)
        {
            if (c != null && c.GetType().FullName == "UnityEngine.Rendering.Universal.Light2D")
            {
                light2D = c;
                intensityProp = c.GetType().GetProperty("intensity", BindingFlags.Public | BindingFlags.Instance);
                if (intensityProp != null)
                {
                    object v = intensityProp.GetValue(light2D, null);
                    if (v is float val) baseIntensity = val;
                }
                break;
            }
        }
    }

    private void Update()
    {
        float t = Time.time * speed + seed;
        float factor = 0f;

        switch (mode)
        {
            case AnimationMode.Breathe:
                factor = Mathf.Sin(t);
                break;

            case AnimationMode.Flicker:
                float perlin = Mathf.PerlinNoise(t * 3f, seed);
                float jitter = UnityEngine.Random.value < flickerRoughness ? (UnityEngine.Random.value - 0.5f) : 0f;
                factor = (perlin * 2f - 1f) + jitter;
                break;

            case AnimationMode.Stutter:
                factor = (Mathf.Sin(t) > 0.85f || UnityEngine.Random.value < 0.05f) ? -0.8f : 0.1f;
                break;

            case AnimationMode.Pulse:
                float frac = (t % 1f);
                factor = Mathf.Exp(-frac * 4f) * Mathf.Sin(frac * Mathf.PI * 2f);
                break;
        }

        if (modulateIntensity && light2D != null && intensityProp != null)
        {
            float newIntensity = Mathf.Max(0f, baseIntensity + (factor * intensityVariation));
            intensityProp.SetValue(light2D, newIntensity, null);
        }

        if (modulateScale)
        {
            float scaleMul = 1f + (factor * intensityVariation * 0.2f);
            transform.localScale = new Vector3(baseScale.x * scaleMul, baseScale.y * scaleMul, baseScale.z);
        }
    }
}
