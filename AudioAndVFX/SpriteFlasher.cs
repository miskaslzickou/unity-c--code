using System.Collections;
using UnityEngine;

/// <summary>
/// Flashes a sprite's tint (e.g. white/red on hit) via MaterialPropertyBlock —
/// no material instantiation, batching preserved.
/// NOTE: uses the "_Color" shader property (default Sprite shader + most custom
/// ones). Shaders exposing "_Tint" or "_BaseColor" will not flash — change
/// ColorProperty below if needed.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class SpriteFlasher : MonoBehaviour
{
    private SpriteRenderer spriteRenderer;
    private MaterialPropertyBlock propBlock;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    // FIX: original tint captured at startup — never assume it was white
    // (sprite may be color-coded or faded).
    private Color originalColor;

    // FIX: tracks the running flash so overlapping calls restart cleanly
    // instead of an old coroutine resetting the color mid-flash.
    private Coroutine flashRoutine;

    [Tooltip("If true, Flash() blinks on/off instead of one solid flash.")]
    [SerializeField] private bool blinkByDefault = false;
    [Tooltip("How many on/off blinks to perform when blinking.")]
    [SerializeField] private int blinkCount = 3;

    void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
        {
            spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        }
        propBlock = new MaterialPropertyBlock();

        if (spriteRenderer != null)
            originalColor = spriteRenderer.color;
    }

    /// <summary>Existing API — unchanged, current callers are not affected.</summary>
    public void Flash(Color flashColor, float duration)
    {
        Flash(flashColor, duration, blink: blinkByDefault, fadeOut: true);
    }

    /// <summary>
    /// Full-control overload; extra parameters are optional.
    /// fadeOut = true  -> old behavior: fades back to original color (or blink tail fade).
    /// fadeOut = false -> holds the flash color, then snaps back at the end.
    /// </summary>
    public void Flash(Color flashColor, float duration, bool blink, bool fadeOut)
    {
        if (spriteRenderer == null) return;

        if (flashRoutine != null)
            StopCoroutine(flashRoutine);

        flashRoutine = StartCoroutine(blink ? BlinkRoutine(flashColor, duration, fadeOut)
                                            : FlashRoutine(flashColor, duration, fadeOut));
    }

    private void ApplyColor(Color c)
    {
        spriteRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor(ColorProperty, c);
        spriteRenderer.SetPropertyBlock(propBlock);
    }

    /// <summary>Solid flash. Fades back (optional) or holds then snaps back.</summary>
    private IEnumerator FlashRoutine(Color flashColor, float duration, bool fadeOut)
    {
        if (duration <= 0f)
        {
            ApplyColor(originalColor);
            flashRoutine = null;
            yield break;
        }

        if (!fadeOut)
        {
            // Hold the flash color for the whole duration, then snap back.
            ApplyColor(flashColor);
            yield return new WaitForSeconds(duration);
        }
        else
        {
            // Old behavior: flash color immediately, then lerp back to original.
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                ApplyColor(Color.Lerp(flashColor, originalColor, Mathf.Clamp01(t / duration)));
                yield return null;
            }
        }

        ApplyColor(originalColor); // FIX: restore real original tint, not white.
        flashRoutine = null;
    }

    /// <summary>Blinking flash (on/off pulses), then restores the original color.</summary>
    private IEnumerator BlinkRoutine(Color flashColor, float duration, bool fadeOut)
    {
        int pulses = Mathf.Max(1, blinkCount);
        float pulseTime = duration / (pulses * 2f); // half on, half off

        for (int i = 0; i < pulses; i++)
        {
            ApplyColor(flashColor);
            yield return new WaitForSeconds(pulseTime);

            ApplyColor(originalColor);
            yield return new WaitForSeconds(pulseTime);
        }

        // Optional smooth tail fade from flash color back to normal.
        if (fadeOut && pulseTime > 0f)
        {
            float t = 0f;
            while (t < pulseTime)
            {
                t += Time.deltaTime;
                ApplyColor(Color.Lerp(flashColor, originalColor, Mathf.Clamp01(t / pulseTime)));
                yield return null;
            }
        }

        ApplyColor(originalColor);
        flashRoutine = null;
    }

    /// <summary>Public helper: instantly cancel any flash and restore the original tint.</summary>
    public void StopFlash()
    {
        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
            flashRoutine = null;
        }
        if (spriteRenderer != null)
            ApplyColor(originalColor);
    }

    void OnDisable()
    {
        // If disabled mid-flash, don't leave a corrupted tint behind
        // and stop any orphaned coroutine.
        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
            flashRoutine = null;
            ApplyColor(originalColor);
        }
    }
}
