using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// AI Director (Left 4 Dead style): watches the player's stress & performance and paces the
/// game dynamically - building tension with spawns/ambushes, then granting relief periods.
/// Genre-agnostic (2D/3D): horror games, roguelikes, wave shooters, racing rubber-banding,
/// tower defense pacing, boss-fight phase tuning.
///
/// Feed it signals (ReportDamage, ReportKill, ReportPickup...), and consume its outputs:
///   - director.Intensity (0..1) to scale enemy count/speed/aggression
///   - director.ShouldSpawnWave / ShouldSpawnAmbush / IsReliefPeriod booleans
///   - SpawnBudget this frame (how many enemies you may spawn)
///   - events OnWaveStart / OnAmbushStart / OnReliefStart
/// </summary>
public class AIDirector : MonoBehaviour
{
    public enum Phase { BuildUp, Peak, Relax, Relief }

    [Header("Player")]
    public Transform player;
    public MonoBehaviour playerHealth; // optional: anything with a 'health' or 'currentHealth' field

    [Header("Intensity Model")]
    [Range(0f, 1f)] public float intensity = 0.2f;
    public float passiveBuildRate = 0.02f;    // per second, baseline tension growth
    public float passiveDecayRate = 0.03f;    // per second when relaxing
    public float dangerDecayRate = 0.08f;     // per second after peak passes

    [Header("Player Performance Tracking")]
    public float averageWindow = 20f;         // seconds of rolling stats
    public float skillDamageTakenWeight = 1f; // damage lowers intensity target (skilled = more game)
    public float skillKillWeight = 0.5f;

    [Header("Phases")]
    public float peakIntensity = 0.85f;       // intensity that triggers Peak
    public float peakDurationMin = 12f;
    public float peakDurationMax = 25f;
    public float relaxDurationMin = 8f;
    public float relaxDurationMax = 15f;
    public float reliefDurationMin = 20f;     // full calm after peak
    public float reliefDurationMax = 35f;

    [Header("Spawning Control")]
    public int maxConcurrentEnemies = 30;
    public float spawnIntervalAtZeroIntensity = 4f;
    public float spawnIntervalAtMaxIntensity = 0.8f;
    public LayerMask enemyMask = ~0;
    public float ambushChanceDuringBuildUp = 0.02f; // per second
    public int ambushEnemyCount = 6;

    [Header("Events")]
    public event Action OnWaveStart;
    public event Action OnAmbushStart;
    public event Action OnReliefStart;
    public event Action<Phase> OnPhaseChanged;

    // ---- Runtime state ----
    public Phase CurrentPhase { get; private set; } = Phase.BuildUp;
    public float PhaseTimer { get; private set; }
    public float PlayerSkill { get; private set; } = 0.5f; // 0 struggling .. 1 dominating
    public int SpawnBudget { get; private set; }
    public bool IsReliefPeriod => CurrentPhase == Phase.Relief;
    public bool ShouldSpawnWave => !IsReliefPeriod && Time.time >= nextSpawnTime;

    readonly Queue<(float time, float damage)> damageLog = new Queue<(float, float)>();
    readonly Queue<float> killLog = new Queue<float>();
    float nextSpawnTime;
    float nextAmbushRoll;
    float peakDuration, relaxDuration, reliefDuration; // rolled each cycle

    void Start()
    {
        if (player == null)
        {
            var go = GameObject.FindGameObjectWithTag("Player");
            if (go != null) player = go.transform;
        }
        EnterPhase(Phase.BuildUp);
    }

    void Update()
    {
        PhaseTimer += Time.deltaTime;
        PruneLogs();
        UpdatePlayerSkill();
        UpdatePhaseMachine();
        UpdateIntensity();
        UpdateSpawning();
    }

    // ---------------- SIGNALS (call these from gameplay code) ----------------

    public void ReportDamage(float amount)
    {
        damageLog.Enqueue((Time.time, amount));
        intensity = Mathf.Min(1f, intensity + amount * 0.004f); // damage spikes tension
    }

    public void ReportKill() => killLog.Enqueue(Time.time);

    public void ReportPickup() => intensity = Mathf.Min(1f, intensity + 0.03f);

    public void ReportScriptedScare(float amount01) => intensity = Mathf.Min(1f, intensity + amount01);

    // ---------------- MODEL ----------------

    void PruneLogs()
    {
        float cutoff = Time.time - averageWindow;
        while (damageLog.Count > 0 && damageLog.Peek().Item1 < cutoff) damageLog.Dequeue();
        while (killLog.Count > 0 && killLog.Peek() < cutoff) killLog.Dequeue();
    }

    void UpdatePlayerSkill()
    {
        float dmg = 0f;
        foreach (var d in damageLog) dmg += d.Item2;
        float kills = killLog.Count;

        // High damage taken & low kills => struggling (skill->0). Clean play => skill->1.
        float target = Mathf.Clamp01(0.5f + kills * 0.05f * skillKillWeight - dmg * 0.01f * skillDamageTakenWeight);
        PlayerSkill = Mathf.MoveTowards(PlayerSkill, target, Time.deltaTime * 0.1f);
    }

    void UpdateIntensity()
    {
        float rate;
        switch (CurrentPhase)
        {
            case Phase.BuildUp:
                // Skill bias: struggling players build tension slower, pros faster.
                rate = passiveBuildRate * Mathf.Lerp(0.5f, 1.5f, PlayerSkill);
                break;
            case Phase.Peak:
                rate = 0f; // hold intensity high during the peak
                intensity = Mathf.MoveTowards(intensity, 1f, Time.deltaTime * 0.2f);
                return;
            case Phase.Relax:
                rate = -dangerDecayRate;
                break;
            case Phase.Relief:
            default:
                rate = -passiveDecayRate * 3f;
                break;
        }
        intensity = Mathf.Clamp01(intensity + rate * Time.deltaTime);
    }

    void UpdatePhaseMachine()
    {
        switch (CurrentPhase)
        {
            case Phase.BuildUp:
                if (intensity >= peakIntensity)
                {
                    EnterPhase(Phase.Peak);
                    OnWaveStart?.Invoke();
                }
                else if (Time.time >= nextAmbushRoll)
                {
                    nextAmbushRoll = Time.time + 1f;
                    if (UnityEngine.Random.value < ambushChanceDuringBuildUp * Mathf.Max(0.2f, intensity))
                    {
                        OnAmbushStart?.Invoke();
                        RequestAmbushSpawn();
                    }
                }
                break;

            case Phase.Peak:
                if (PhaseTimer >= peakDuration)
                {
                    EnterPhase(Phase.Relax);
                }
                break;

            case Phase.Relax:
                if (PhaseTimer >= relaxDuration)
                {
                    EnterPhase(Phase.Relief);
                    OnReliefStart?.Invoke();
                }
                break;

            case Phase.Relief:
                if (PhaseTimer >= reliefDuration)
                {
                    EnterPhase(Phase.BuildUp);
                }
                break;
        }
    }

    void UpdateSpawning()
    {
        if (IsReliefPeriod) { SpawnBudget = 0; return; }

        if (Time.time >= nextSpawnTime)
        {
            float t = intensity;
            float interval = Mathf.Lerp(spawnIntervalAtZeroIntensity, spawnIntervalAtMaxIntensity, t);
            nextSpawnTime = Time.time + interval;

            int alive = CountEnemies();
            int allowed = Mathf.Min(maxConcurrentEnemies, Mathf.RoundToInt(1 + t * 4f));
            SpawnBudget = Mathf.Max(0, allowed - alive);
        }
    }

    void RequestAmbushSpawn() => SpawnBudget += ambushEnemyCount;

    int CountEnemies()
    {
        if (player == null) return 0;
        return Physics.OverlapSphere(player.position, 100f, enemyMask).Length;
    }

    void EnterPhase(Phase p)
    {
        CurrentPhase = p;
        PhaseTimer = 0f;
        switch (p)
        {
            case Phase.Peak:
                peakDuration = UnityEngine.Random.Range(peakDurationMin, peakDurationMax);
                break;
            case Phase.Relax:
                relaxDuration = UnityEngine.Random.Range(relaxDurationMin, relaxDurationMax);
                break;
            case Phase.Relief:
                reliefDuration = UnityEngine.Random.Range(reliefDurationMin, reliefDurationMax);
                break;
        }
        OnPhaseChanged?.Invoke(p);
    }

    // ---------------- DEBUG ----------------

    void OnGUI()
    {
        if (!debugUI) return;
        GUILayout.BeginArea(new Rect(10f, 10f, 260f, 200f), GUI.skin.box);
        GUILayout.Label($"Phase: {CurrentPhase} ({PhaseTimer:F1}s)");
        GUILayout.Label($"Intensity: {intensity:F2}");
        GUILayout.Label($"PlayerSkill: {PlayerSkill:F2}");
        GUILayout.Label($"SpawnBudget: {SpawnBudget}");
        GUILayout.EndArea();
    }

    public bool debugUI = false;

    void OnDrawGizmosSelected()
    {
        if (player == null) return;
        Gizmos.color = Color.Lerp(Color.green, Color.red, intensity);
        Gizmos.DrawWireSphere(player.position, 2f + intensity * 6f);
    }
}
