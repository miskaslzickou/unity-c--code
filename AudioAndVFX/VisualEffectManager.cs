using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Zobrazuje plovoucí texty (damage čísla, heal, popisky...) pomocí UI Toolkitu.
/// Labely se recyklují přes pool, takže se za běhu téměř nealokuje.
/// Použití: VisualEffectManager.Instance.ApplyEffect("-25", enemy.transform, Color.red, 1f);
/// </summary>
public class VisualEffectManager : MonoBehaviour
{
    [Header("Reference")]
    [SerializeField] private Camera cam;
    [SerializeField] private UIDocument uiDocument;

    [Header("Animace")]
    [Tooltip("O kolik pixelů label za dobu životnosti vystoupá nahoru.")]
    [SerializeField] private float riseDistance = 60f;



    // Pool právě nepoužívaných labelů. Stack stačí, nezáleží na pořadí.
    private readonly Stack<Label> pool = new Stack<Label>();

    // Kořen UI stromu, do kterého labely přidáváme. Viz vlastnost Root níže.
    private VisualElement root;

    public static VisualEffectManager Instance { get; private set; }

    /// <summary>
    /// Lazy přístup ke kořenu UI. UIDocument vytváří rootVisualElement až ve svém OnEnable,
    /// takže při čtení v Awake/Start může být null v závislosti na pořadí inicializace.
    /// Tímto způsobem ho načteme až ve chvíli, kdy ho opravdu potřebujeme.
    /// </summary>
    private VisualElement Root
    {
        get
        {
            if (root == null && uiDocument != null)
                root = uiDocument.rootVisualElement;
            return root;
        }
    }

    void Awake()
    {
        Instance = this;
        if (cam == null) cam = Camera.main;
    }

    void OnDisable()
    {
        // Při vypnutí (nebo znovuvytvoření UIDocumentu) se UI strom zahodí a s ním i naše labely.
        // Pool i root proto vyčistíme, ať nepracujeme s osiřelými prvky; příště se vše vytvoří znovu.
        StopAllCoroutines();
        pool.Clear();
        root = null;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------------
    // Veřejné API
    // ---------------------------------------------------------------------

    /// <summary>
    /// Efekt na pevné pozici ve světě. Pozice se převede na UI souřadnice jednou při spawnu,
    /// label pak stoupá nahoru z tohoto místa (hodí se např. pro výbuch nebo sebraný předmět).
    /// </summary>
    /// <param name="text">Zobrazený text.</param>
    /// <param name="worldPos">Pozice ve světě, kde se efekt zobrazí.</param>
    /// <param name="color">Barva textu.</param>
    /// <param name="duration">Doba životnosti efektu v sekundách.</param>
    /// <param name="size">Velikost písma.</param>
    /// <param name="shadowColor">Barva stínu textu. Výchozí je černá.</param>
    /// <param name="shadowOffset">Posun stínu textu v pixelech.</param>
    /// <param name="blurRadius">Rozmazání stínu.</param>
  
    public void ApplyEffect(string text, Vector3 worldPos, Color color, float duration,
                            float size = 12f, Color? shadowColor = null, Vector2? shadowOffset = null, float blurRadius = 1f)
    {
        if (!CanSpawn()) return;

        Label label = GetOrCreateLabel();
        SetupLabel(label, text, color, size, shadowColor, shadowOffset, blurRadius);
        StartCoroutine(AnimateAtPosition(label, worldPos, duration));
    }

    /// <summary>
    /// Efekt, který sleduje pohybující se cíl (např. damage číslo nad hlavou nepřítele).
    /// Pokud cíl během animace zanikne, label se předčasně vrátí do poolu.
    /// </summary>
    /// <param name="text">Zobrazený text.</param>
    /// <param name="targetTransform">Cíl, který label sleduje.</param>
    /// <param name="color">Barva textu.</param>
    /// <param name="duration">Doba životnosti efektu v sekundách.</param>
    /// <param name="size">Velikost písma.</param>
    /// <param name="shadowColor">Barva stínu textu. Výchozí je černá.</param>
    /// <param name="shadowOffset">Posun stínu textu v pixelech.</param>
    /// <param name="blurRadius">Rozmazání stínu.</param>
    /// <param name="followOffset">Posun labelu od pivotu cíle. Když není zadán, použije se výška z Inspectoru.</param>
    public void ApplyEffect(string text, Transform targetTransform, Color color, float duration,
                            float size = 12f, Color? shadowColor = null, Vector2? shadowOffset = null,
                            float blurRadius = 1f, Vector3? followOffset = null)
    {
        // ...
    }
    public void ApplyEffect(string text, Transform targetTransform, Color color, float duration,
                            float size = 12f, Color? shadowColor = null, Vector2? shadowOffset = null, float blurRadius = 1f, Vector2 followOffset = new Vector2())
    {
        if (targetTransform == null || !CanSpawn()) return;

        Label label = GetOrCreateLabel();
        SetupLabel(label, text, color, size, shadowColor, shadowOffset, blurRadius);
        StartCoroutine(AnimateFollow(label, targetTransform, duration, followOffset));
    }

    // ---------------------------------------------------------------------
    // Pool a nastavení labelů
    // ---------------------------------------------------------------------

    /// <summary>
    /// Ověří, že máme vše potřebné pro zobrazení efektu. Kontrola probíhá hned při volání,
    /// takže se label vůbec nevytáhne z poolu, pokud by ho nešlo zobrazit (žádný únik labelů).
    /// </summary>
    private bool CanSpawn()
    {
        if (Root == null || cam == null)
        {
            Debug.LogWarning("VisualEffectManager: chybí UIDocument nebo kamera, efekt se nezobrazí.", this);
            return false;
        }
        return true;
    }

    /// <summary>Vezme label z poolu, nebo vytvoří nový, a připraví ho k zobrazení.</summary>
    private Label GetOrCreateLabel()
    {
        Label label = pool.Count > 0 ? pool.Pop() : NewLabel();
        label.style.display = DisplayStyle.Flex;

        // Recyklovaný label má po minulém použití opacity ~0. Bez resetu by byl první snímek neviditelný.
        label.style.opacity = 1f;
        return label;
    }

    /// <summary>Vytvoří nový label. Ignoruje myš, aby neblokoval kliknutí na UI pod sebou.</summary>
    private Label NewLabel()
    {
        var label = new Label { pickingMode = PickingMode.Ignore };
        label.style.position = Position.Absolute;
        Root.Add(label);
        return label;
    }

    /// <summary>Společné nastavení vzhledu labelu. Nepovinné hodnoty mají rozumné výchozí hodnoty.</summary>
    private void SetupLabel(Label label, string text, Color color, float size,
                            Color? shadowColor, Vector2? offset, float blurRadius)
    {
        label.text = text;
        label.style.color = color;
        label.style.fontSize = size;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;

        label.style.textShadow = new TextShadow
        {
            color = shadowColor ?? Color.black,
            offset = offset ?? Vector2.zero,
            blurRadius = blurRadius
        };
    }

    /// <summary>Skryje label a vrátí ho do poolu k dalšímu použití.</summary>
    private void ReturnLabelToPool(Label label)
    {
        label.style.display = DisplayStyle.None;
        pool.Push(label);
    }

    // ---------------------------------------------------------------------
    // Animace
    // ---------------------------------------------------------------------

    /// <summary>Statický efekt: pozice se spočítá jednou, pak label stoupá a mizí.</summary>
    private IEnumerator AnimateAtPosition(Label label, Vector3 worldPos, float duration)
    {
        // Pozici ve světě převedeme na souřadnice UI panelu jen jednou, při spawnu.
        Vector2 start = RuntimePanelUtils.CameraTransformWorldToPanel(Root.panel, worldPos, cam);

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            ApplyAnimationFrame(label, start, t / duration);
            yield return null;
        }

        ReturnLabelToPool(label);
    }

    /// <summary>Sledující efekt: pozice cíle se přepočítává každý snímek, takže label jede s ním.</summary>
    private IEnumerator AnimateFollow(Label label, Transform targetTransform, float duration,Vector2 offset= new Vector2())
    {
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            // Cíl mohl být zničen (např. nepřítel zemřel), nebo se mezitím zrušil UI panel.
            // V obou případech animaci ukončíme, label se vrátí do poolu níže.
            if (targetTransform == null || Root == null || Root.panel == null || cam == null) break;

            Vector3 worldPos = targetTransform.position+ (Vector3)offset;
            Vector2 screenPos = RuntimePanelUtils.CameraTransformWorldToPanel(Root.panel, worldPos, cam);

            ApplyAnimationFrame(label, screenPos, t / duration);
            yield return null;
        }

        ReturnLabelToPool(label);
    }

    /// <summary>
    /// Jeden snímek animace: umístí label na výchozí bod, posune ho nahoru o podíl riseDistance
    /// a zprůhlední. Parametr progress je 0 na začátku a 1 na konci životnosti.
    /// </summary>
    private void ApplyAnimationFrame(Label label, Vector2 basePos, float progress)
    {
        label.style.left = basePos.x;
        label.style.top = basePos.y - riseDistance * progress; // stoupání nahoru
        label.style.opacity = 1f - progress;                    // postupné mizení
    }
}