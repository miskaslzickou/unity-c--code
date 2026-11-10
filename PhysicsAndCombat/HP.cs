using System;
using UnityEngine;
using GameSystems.Events;

/// <summary>
/// Správce zdraví, poškození a léčení entity.
/// Integrováno s ZeroGCEventBus a plně kompatibilní s UtilityBrain a dalšími AI/fyzikálními systémy.
/// </summary>
[DisallowMultipleComponent]
public class HP : MonoBehaviour
{
    [Header("Health Settings")]
    [Tooltip("Maximální možná hodnota zdraví.")]
    [SerializeField] private float _maxHealth = 100f;

    [Tooltip("Aktuální hodnota zdraví entity.")]
    [SerializeField] private float _currentHealth = 100f;

    [Header("State")]
    [Tooltip("Zda je entita nesmrtelná (ignoruje poškození).")]
    [SerializeField] private bool _invulnerable = false;

    // Veřejné přístupy požadované UtilityBrain a AI systémy (reflection i přímé volání)
    public float health
    {
        get => _currentHealth;
        set => SetHealth(value);
    }

    public float maxHealth
    {
        get => _maxHealth;
        set => _maxHealth = Mathf.Max(1f, value);
    }

    public bool isDead => _currentHealth <= 0f;
    public bool isInvulnerable { get => _invulnerable; set => _invulnerable = value; }
    public float NormalizedHealth => _maxHealth > 0f ? Mathf.Clamp01(_currentHealth / _maxHealth) : 0f;

    // Události (C# Action)
    public event Action<float, float> OnHealthChanged; // (current, max)
    public event Action<GameObject> OnDied;             // (killer)

    private void Awake()
    {
        _currentHealth = _maxHealth;
    }

    /// <summary>
    /// Aplikuje poškození na entitu a publikuje události.
    /// </summary>
    public void TakeDamage(float damage, GameObject attacker = null)
    {
        if (_invulnerable || isDead || damage <= 0f) return;

        float oldHealth = _currentHealth;
        _currentHealth = Mathf.Max(0f, _currentHealth - damage);

        // Notifikace lokálních listenerů
        OnHealthChanged?.Invoke(_currentHealth, _maxHealth);

        // Notifikace globální ZeroGCEventBus sběrnice bez alokací
        ZeroGCEventBus.Raise(new HealthChangedEvent(gameObject, oldHealth, _currentHealth, _maxHealth, attacker));

        if (_currentHealth <= 0f)
        {
            Die(attacker);
        }
    }

    /// <summary>
    /// Vyléčí entitu o specifikovanou hodnotu.
    /// </summary>
    public void Heal(float amount, GameObject healer = null)
    {
        if (isDead || amount <= 0f) return;

        float oldHealth = _currentHealth;
        _currentHealth = Mathf.Min(_maxHealth, _currentHealth + amount);

        if (Mathf.Approximately(oldHealth, _currentHealth)) return;

        OnHealthChanged?.Invoke(_currentHealth, _maxHealth);
        ZeroGCEventBus.Raise(new HealthChangedEvent(gameObject, oldHealth, _currentHealth, _maxHealth, healer));
    }

    /// <summary>
    /// Přímo nastaví hodnotu zdraví v mezích [0, maxHealth].
    /// </summary>
    public void SetHealth(float value)
    {
        float clamped = Mathf.Clamp(value, 0f, _maxHealth);
        if (Mathf.Approximately(_currentHealth, clamped)) return;

        float oldHealth = _currentHealth;
        _currentHealth = clamped;

        OnHealthChanged?.Invoke(_currentHealth, _maxHealth);
        ZeroGCEventBus.Raise(new HealthChangedEvent(gameObject, oldHealth, _currentHealth, _maxHealth, null));

        if (_currentHealth <= 0f && oldHealth > 0f)
        {
            Die(null);
        }
    }

    private void Die(GameObject killer)
    {
        OnDied?.Invoke(killer);
        ZeroGCEventBus.Raise(new EntityDiedEvent(gameObject, killer, transform.position));
    }

    private void Reset()
    {
        _maxHealth = 100f;
        _currentHealth = 100f;
        _invulnerable = false;
    }
}
