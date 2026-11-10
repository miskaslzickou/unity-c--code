using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Event raised when player health or stamina changes.
/// </summary>
public readonly struct PlayerStatsChangedEvent : IGameEvent
{
    public readonly float Health;
    public readonly float MaxHealth;
    public readonly float Stamina;
    public readonly float MaxStamina;

    public PlayerStatsChangedEvent(float health, float maxHealth, float stamina, float maxStamina)
    {
        Health = health;
        MaxHealth = maxHealth;
        Stamina = stamina;
        MaxStamina = maxStamina;
    }
}

/// <summary>
/// Serializable runtime and configuration data for the player character.
/// Interconnects with Movement.cs, ISaveableSystem, and ZeroGCEventBus.
/// </summary>
[DisallowMultipleComponent]
public class PlayerStats : MonoBehaviour, ISaveableSystem
{
    [Header("Dynamic Movement Telemetry")]
    [Tooltip("Current throttle value applied by Movement controller.")]
    public float throttle;

    [Tooltip("Current linear speed calculated from Rigidbody2D.")]
    public float speed;

    [Tooltip("Current heading angle in degrees.")]
    public float heading;

    [Header("Movement & Physics Attributes")]
    [Tooltip("Base movement speed.")]
    public float moveSpeed = 10f;

    [Tooltip("Jump force applied on impulse.")]
    public float jumpForce = 5f;

    [Tooltip("Acceleration factor.")]
    public float acceleration = 8f;

    [Tooltip("Dash impulse or burst speed.")]
    public float dashSpeed = 15f;

    [Header("Health & Stamina")]
    [SerializeField] private float maxHealth = 100f;
    [SerializeField] private float currentHealth = 100f;
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float currentStamina = 100f;
    [SerializeField] private float staminaRegenRate = 15f;

    public float Health => currentHealth;
    public float MaxHealth => maxHealth;
    public float Stamina => currentStamina;
    public float MaxStamina => maxStamina;

    public string SaveKey => "PlayerStats_" + gameObject.name;

    private void Awake()
    {
        currentHealth = maxHealth;
        currentStamina = maxStamina;
    }

    private void Update()
    {
        if (currentStamina < maxStamina)
        {
            currentStamina = Mathf.Min(maxStamina, currentStamina + staminaRegenRate * Time.deltaTime);
        }
    }

    public void ModifyHealth(float delta)
    {
        currentHealth = Mathf.Clamp(currentHealth + delta, 0f, maxHealth);
        ZeroGCEventBus.Publish(new PlayerStatsChangedEvent(currentHealth, maxHealth, currentStamina, maxStamina));
    }

    public bool ConsumeStamina(float amount)
    {
        if (currentStamina >= amount)
        {
            currentStamina -= amount;
            ZeroGCEventBus.Publish(new PlayerStatsChangedEvent(currentHealth, maxHealth, currentStamina, maxStamina));
            return true;
        }
        return false;
    }

    // =========================================================================
    // ISaveableSystem Implementation
    // =========================================================================

    [Serializable]
    private class PlayerStatsSaveData
    {
        public float Health;
        public float Stamina;
        public float Throttle;
        public float Speed;
        public float Heading;
    }

    public object CaptureState()
    {
        return new PlayerStatsSaveData
        {
            Health = currentHealth,
            Stamina = currentStamina,
            Throttle = throttle,
            Speed = speed,
            Heading = heading
        };
    }

    public void RestoreState(string stateJson)
    {
        if (string.IsNullOrEmpty(stateJson)) return;
        var data = JsonUtility.FromJson<PlayerStatsSaveData>(stateJson);
        if (data == null) return;

        currentHealth = data.Health;
        currentStamina = data.Stamina;
        throttle = data.Throttle;
        speed = data.Speed;
        heading = data.Heading;
    }
}
