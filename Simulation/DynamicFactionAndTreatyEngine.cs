using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Active treaties and diplomatic statuses that can exist between two sovereign factions.
/// </summary>
[System.Flags]
public enum TreatyKind : ushort
{
    None = 0,
    NonAggressionPact = 1 << 0,
    DefensiveAlliance = 1 << 1,
    TradeAgreement = 1 << 2,
    TradeEmbargo = 1 << 3,
    OpenBorders = 1 << 4,
    Vassalage = 1 << 5,
    TotalWar = 1 << 6,
    Sanctions = 1 << 7,
    QuarantineBorderClosure = 1 << 8
}

/// <summary>
/// Justifications for declaring war (Casus Belli).
/// </summary>
public enum CasusBelliKind
{
    TerritorialFriction,
    TradeEmbargoRetaliation,
    TreatyViolation,
    IdeologicalContainment,
    PreemptiveStrike
}

/// <summary>
/// Terms negotiated at an automated peace conference.
/// </summary>
public enum PeaceTerms
{
    WhitePeace,
    WarReparations,
    DemilitarizedBufferZone,
    SubjugationVassalage,
    TotalCapitulation
}

/// <summary>
/// Bilateral diplomatic relationship between two specific factions.
/// </summary>
[System.Serializable]
public struct DiplomaticRelation
{
    public int FactionAId;
    public int FactionBId;
    [Range(-100f, 100f)] public float RelationScore; // -100 (Hostile) to +100 (Allied)
    [Range(0f, 100f)] public float Trust;            // 0 to 100
    [Range(0f, 100f)] public float PerceivedThreat;  // 0 to 100
    [Range(0f, 100f)] public float TradeDependency;  // 0 to 100
    public TreatyKind ActiveTreaties;
    public float WarDurationSeconds;
    [Range(0f, 100f)] public float WarExhaustionA;
    [Range(0f, 100f)] public float WarExhaustionB;

    public bool HasTreaty(TreatyKind treaty) => (ActiveTreaties & treaty) != 0;
    public bool IsAtWar => (ActiveTreaties & TreatyKind.TotalWar) != 0;
}

/// <summary>
/// Sovereign geopolitical faction entity.
/// </summary>
[System.Serializable]
public class FactionEntity
{
    public int FactionId;
    public string FactionName;
    public Color FactionColor;
    public Vector3 CapitalPosition;
    public float MilitaryPower;
    public float EconomicTreasury;
    public float TerritorialRadius;
    [Range(0f, 1f)] public float Belligerence; // Propensity to initiate conflict

    public FactionEntity(int id, string name, Color color, Vector3 capital, float military, float treasury, float territory, float belligerence = 0.3f)
    {
        FactionId = id;
        FactionName = name;
        FactionColor = color;
        CapitalPosition = capital;
        MilitaryPower = Mathf.Max(1f, military);
        EconomicTreasury = Mathf.Max(0f, treasury);
        TerritorialRadius = Mathf.Max(10f, territory);
        Belligerence = Mathf.Clamp01(belligerence);
    }

    public Vector3 GetWorldPosition(bool is2D)
    {
        return is2D ? new Vector3(CapitalPosition.x, CapitalPosition.y, 0f) : CapitalPosition;
    }
}

/// <summary>
/// Event raised when bilateral diplomatic relations or treaties are updated.
/// </summary>
public readonly struct FactionDiplomacyChangedEvent : IGameEvent
{
    public readonly int FactionAId;
    public readonly int FactionBId;
    public readonly float NewRelationScore;
    public readonly TreatyKind ActiveTreaties;

    public FactionDiplomacyChangedEvent(int aId, int bId, float score, TreatyKind treaties)
    {
        FactionAId = aId;
        FactionBId = bId;
        NewRelationScore = score;
        ActiveTreaties = treaties;
    }
}

/// <summary>
/// Event raised when war is declared between two factions.
/// </summary>
public readonly struct WarDeclaredEvent : IGameEvent
{
    public readonly int AttackerFactionId;
    public readonly int DefenderFactionId;
    public readonly CasusBelliKind CasusBelli;

    public WarDeclaredEvent(int attacker, int defender, CasusBelliKind casusBelli)
    {
        AttackerFactionId = attacker;
        DefenderFactionId = defender;
        CasusBelli = casusBelli;
    }
}

/// <summary>
/// Event raised when hostilities cease through a negotiated peace treaty.
/// </summary>
public readonly struct PeaceTreatySignedEvent : IGameEvent
{
    public readonly int FactionAId;
    public readonly int FactionBId;
    public readonly PeaceTerms Terms;
    public readonly float ReparationsAmount;

    public PeaceTreatySignedEvent(int aId, int bId, PeaceTerms terms, float reparations)
    {
        FactionAId = aId;
        FactionBId = bId;
        Terms = terms;
        ReparationsAmount = reparations;
    }
}

/// <summary>
/// Geopolitical diplomacy matrix, border friction, casus belli, war exhaustion,
/// and automated peace treaty negotiation engine with dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class DynamicFactionAndTreatyEngine : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Execution Order")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 165;

    [Header("Diplomacy Dynamics")]
    [SerializeField, Range(0.001f, 0.05f)] private float warExhaustionGrowthRate = 0.015f;
    [SerializeField, Range(0.001f, 0.05f)] private float naturalRelationDriftRate = 0.005f;
    [SerializeField] private float borderFrictionDistanceMultiplier = 1.5f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float capitalGizmoRadius = 2.0f;

    // Faction Registry and N x N Diplomacy Matrix
    private readonly List<FactionEntity> factions = new List<FactionEntity>(16);
    private readonly Dictionary<int, FactionEntity> factionMap = new Dictionary<int, FactionEntity>(16);

    // Symmetric matrix key helper: (min(A,B) << 16) | max(A,B)
    private readonly Dictionary<int, DiplomaticRelation> relationMatrix = new Dictionary<int, DiplomaticRelation>(128);

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "DynamicFactionAndTreatyEngine_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public int FactionCount => factions.Count;

    private void Awake()
    {
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

    // =========================================================================
    // FACTION REGISTRATION & DIPLOMATIC MATRIX
    // =========================================================================

    public void RegisterFaction(FactionEntity faction)
    {
        if (faction == null || factionMap.ContainsKey(faction.FactionId))
            return;

        factions.Add(faction);
        factionMap[faction.FactionId] = faction;

        // Initialize bilateral relations with all existing factions
        for (int i = 0; i < factions.Count - 1; i++)
        {
            FactionEntity existing = factions[i];
            int key = GetMatrixKey(existing.FactionId, faction.FactionId);

            var relation = new DiplomaticRelation
            {
                FactionAId = existing.FactionId,
                FactionBId = faction.FactionId,
                RelationScore = 0f,
                Trust = 50f,
                PerceivedThreat = 10f,
                TradeDependency = 0f,
                ActiveTreaties = TreatyKind.None,
                WarDurationSeconds = 0f,
                WarExhaustionA = 0f,
                WarExhaustionB = 0f
            };

            relationMatrix[key] = relation;
        }
    }

    public bool TryGetFaction(int factionId, out FactionEntity faction)
    {
        return factionMap.TryGetValue(factionId, out faction);
    }

    private int GetMatrixKey(int idA, int idB)
    {
        int min = Mathf.Min(idA, idB);
        int max = Mathf.Max(idA, idB);
        return (min << 16) | (max & 0xFFFF);
    }

    public bool TryGetRelation(int factionAId, int factionBId, out DiplomaticRelation relation)
    {
        int key = GetMatrixKey(factionAId, factionBId);
        return relationMatrix.TryGetValue(key, out relation);
    }

    public void SetTreaty(int factionAId, int factionBId, TreatyKind treaty, bool active)
    {
        int key = GetMatrixKey(factionAId, factionBId);
        if (relationMatrix.TryGetValue(key, out DiplomaticRelation rel))
        {
            if (active)
            {
                rel.ActiveTreaties |= treaty;
                if (treaty == TreatyKind.TotalWar)
                {
                    // War cancels alliances, pacts, and trade
                    rel.ActiveTreaties &= ~(TreatyKind.NonAggressionPact | TreatyKind.DefensiveAlliance | TreatyKind.TradeAgreement | TreatyKind.OpenBorders);
                }
            }
            else
            {
                rel.ActiveTreaties &= ~treaty;
            }

            relationMatrix[key] = rel;
            EventBus<FactionDiplomacyChangedEvent>.Raise(new FactionDiplomacyChangedEvent(factionAId, factionBId, rel.RelationScore, rel.ActiveTreaties));
        }
    }

    public void AdjustRelationScore(int factionAId, int factionBId, float delta)
    {
        int key = GetMatrixKey(factionAId, factionBId);
        if (relationMatrix.TryGetValue(key, out DiplomaticRelation rel))
        {
            rel.RelationScore = Mathf.Clamp(rel.RelationScore + delta, -100f, 100f);
            relationMatrix[key] = rel;
            EventBus<FactionDiplomacyChangedEvent>.Raise(new FactionDiplomacyChangedEvent(factionAId, factionBId, rel.RelationScore, rel.ActiveTreaties));
        }
    }

    // =========================================================================
    // CASUS BELLI & CONFLICT DECLARATION
    // =========================================================================

    public bool DeclareWar(int attackerId, int defenderId, CasusBelliKind casusBelli)
    {
        if (attackerId == defenderId || !factionMap.ContainsKey(attackerId) || !factionMap.ContainsKey(defenderId))
            return false;

        int key = GetMatrixKey(attackerId, defenderId);
        if (relationMatrix.TryGetValue(key, out DiplomaticRelation rel))
        {
            if (rel.IsAtWar) return false;

            rel.ActiveTreaties |= TreatyKind.TotalWar;
            rel.ActiveTreaties &= ~(TreatyKind.NonAggressionPact | TreatyKind.DefensiveAlliance | TreatyKind.TradeAgreement | TreatyKind.OpenBorders);
            rel.RelationScore = -100f;
            rel.Trust = Mathf.Max(0f, rel.Trust - 50f);
            rel.WarDurationSeconds = 0f;
            rel.WarExhaustionA = 0f;
            rel.WarExhaustionB = 0f;

            relationMatrix[key] = rel;

            EventBus<WarDeclaredEvent>.Raise(new WarDeclaredEvent(attackerId, defenderId, casusBelli));

            // Call in defensive allies
            TriggerDefensiveAllies(defenderId, attackerId);
            return true;
        }

        return false;
    }

    private void TriggerDefensiveAllies(int victimId, int aggressorId)
    {
        for (int i = 0; i < factions.Count; i++)
        {
            int allyId = factions[i].FactionId;
            if (allyId == victimId || allyId == aggressorId) continue;

            int key = GetMatrixKey(victimId, allyId);
            if (relationMatrix.TryGetValue(key, out DiplomaticRelation rel) && rel.HasTreaty(TreatyKind.DefensiveAlliance))
            {
                // Ally enters war against aggressor
                DeclareWar(allyId, aggressorId, CasusBelliKind.TreatyViolation);
            }
        }
    }

    // =========================================================================
    // AUTOMATED PEACE CONFERENCE ENGINE
    // =========================================================================

    /// <summary>
    /// Evaluates whether two warring factions will agree to sign peace terms
    /// based on accumulated war exhaustion, treasury, and relative military strength.
    /// </summary>
    public bool EvaluatePeaceConference(int factionAId, int factionBId, out PeaceTerms agreedTerms, out float reparations)
    {
        agreedTerms = PeaceTerms.WhitePeace;
        reparations = 0f;

        int key = GetMatrixKey(factionAId, factionBId);
        if (!relationMatrix.TryGetValue(key, out DiplomaticRelation rel) || !rel.IsAtWar)
            return false;

        if (!factionMap.TryGetValue(factionAId, out FactionEntity facA) || !factionMap.TryGetValue(factionBId, out FactionEntity facB))
            return false;

        // Peace willingness scales with war exhaustion
        float willingnessA = rel.WarExhaustionA / 100f;
        float willingnessB = rel.WarExhaustionB / 100f;

        // If neither side is tired of war, conference breaks down
        if (willingnessA < 0.6f && willingnessB < 0.6f)
            return false;

        float militaryRatio = facA.MilitaryPower / Mathf.Max(1f, facB.MilitaryPower);

        if (willingnessA >= 0.85f && militaryRatio < 0.4f)
        {
            // Faction A capitulates or pays heavy reparations
            agreedTerms = PeaceTerms.WarReparations;
            reparations = Mathf.Min(facA.EconomicTreasury * 0.4f, 5000f);
            facA.EconomicTreasury -= reparations;
            facB.EconomicTreasury += reparations;
        }
        else if (willingnessB >= 0.85f && militaryRatio > 2.5f)
        {
            // Faction B capitulates
            agreedTerms = PeaceTerms.WarReparations;
            reparations = Mathf.Min(facB.EconomicTreasury * 0.4f, 5000f);
            facB.EconomicTreasury -= reparations;
            facA.EconomicTreasury += reparations;
        }
        else
        {
            // Status Quo / Mutual exhaustion
            agreedTerms = PeaceTerms.WhitePeace;
        }

        // Hostilities cease
        rel.ActiveTreaties &= ~TreatyKind.TotalWar;
        rel.RelationScore = -25f; // Lingering cold war bitterness
        rel.WarDurationSeconds = 0f;
        rel.WarExhaustionA = 0f;
        rel.WarExhaustionB = 0f;
        relationMatrix[key] = rel;

        EventBus<PeaceTreatySignedEvent>.Raise(new PeaceTreatySignedEvent(factionAId, factionBId, agreedTerms, reparations));
        return true;
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // Update bilateral relations and war progress
        var keys = new List<int>(relationMatrix.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            int key = keys[i];
            DiplomaticRelation rel = relationMatrix[key];

            if (rel.IsAtWar)
            {
                rel.WarDurationSeconds += deltaTime;
                rel.WarExhaustionA = Mathf.Clamp(rel.WarExhaustionA + (warExhaustionGrowthRate * deltaTime), 0f, 100f);
                rel.WarExhaustionB = Mathf.Clamp(rel.WarExhaustionB + (warExhaustionGrowthRate * deltaTime), 0f, 100f);

                // Check automated peace conditions if war is prolonged
                if (rel.WarExhaustionA >= 75f || rel.WarExhaustionB >= 75f)
                {
                    EvaluatePeaceConference(rel.FactionAId, rel.FactionBId, out _, out _);
                    continue;
                }
            }
            else
            {
                // Natural relation drift towards neutral 0
                if (Mathf.Abs(rel.RelationScore) > 1f)
                {
                    rel.RelationScore -= Mathf.Sign(rel.RelationScore) * naturalRelationDriftRate * deltaTime;
                }

                // Border friction calculation based on spatial proximity
                if (factionMap.TryGetValue(rel.FactionAId, out FactionEntity a) &&
                    factionMap.TryGetValue(rel.FactionBId, out FactionEntity b))
                {
                    Vector3 posA = a.GetWorldPosition(defaultTo2D);
                    Vector3 posB = b.GetWorldPosition(defaultTo2D);
                    float dist = defaultTo2D
                        ? Vector2.Distance(new Vector2(posA.x, posA.y), new Vector2(posB.x, posB.y))
                        : Vector3.Distance(posA, posB);

                    float combinedTerritory = (a.TerritorialRadius + b.TerritorialRadius) * borderFrictionDistanceMultiplier;
                    if (dist < combinedTerritory)
                    {
                        // Border overlap creates friction
                        float frictionIntensity = 1f - Mathf.Clamp01(dist / combinedTerritory);
                        rel.RelationScore = Mathf.Clamp(rel.RelationScore - (frictionIntensity * 0.02f * deltaTime), -100f, 100f);
                        rel.PerceivedThreat = Mathf.Clamp(rel.PerceivedThreat + (frictionIntensity * 0.05f * deltaTime), 0f, 100f);

                        // If tension is extreme, war may be triggered by belligerent factions
                        if (rel.RelationScore <= -80f && (a.Belligerence > 0.7f || b.Belligerence > 0.7f) && !rel.HasTreaty(TreatyKind.NonAggressionPact))
                        {
                            DeclareWar(a.Belligerence > b.Belligerence ? a.FactionId : b.FactionId,
                                       a.Belligerence > b.Belligerence ? b.FactionId : a.FactionId,
                                       CasusBelliKind.TerritorialFriction);
                        }
                    }
                }
            }

            relationMatrix[key] = rel;
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<FactionEntity> Factions = new List<FactionEntity>();
        public List<DiplomaticRelation> Relations = new List<DiplomaticRelation>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        container.Factions.AddRange(factions);
        foreach (var kvp in relationMatrix)
        {
            container.Relations.Add(kvp.Value);
        }
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        factions.Clear();
        factionMap.Clear();
        relationMatrix.Clear();

        for (int i = 0; i < container.Factions.Count; i++)
        {
            RegisterFaction(container.Factions[i]);
        }

        for (int i = 0; i < container.Relations.Count; i++)
        {
            DiplomaticRelation rel = container.Relations[i];
            int key = GetMatrixKey(rel.FactionAId, rel.FactionBId);
            relationMatrix[key] = rel;
        }
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Capitals and Territory Perimeters
        for (int i = 0; i < factions.Count; i++)
        {
            FactionEntity faction = factions[i];
            Vector3 pos = faction.GetWorldPosition(defaultTo2D);

            Gizmos.color = faction.FactionColor;
            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(pos, capitalGizmoRadius);
                Gizmos.color = new Color(faction.FactionColor.r, faction.FactionColor.g, faction.FactionColor.b, 0.25f);
                Gizmos.DrawWireSphere(pos, faction.TerritorialRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, capitalGizmoRadius);
                Gizmos.color = new Color(faction.FactionColor.r, faction.FactionColor.g, faction.FactionColor.b, 0.25f);
                Gizmos.DrawWireSphere(pos, faction.TerritorialRadius);
            }
        }

        // Draw Diplomatic Lines between Capitals
        foreach (var kvp in relationMatrix)
        {
            DiplomaticRelation rel = kvp.Value;
            if (!factionMap.TryGetValue(rel.FactionAId, out FactionEntity a) ||
                !factionMap.TryGetValue(rel.FactionBId, out FactionEntity b))
                continue;

            Vector3 posA = a.GetWorldPosition(defaultTo2D);
            Vector3 posB = b.GetWorldPosition(defaultTo2D);

            Color lineColor;
            if (rel.IsAtWar)
            {
                lineColor = Color.red; // War
            }
            else if (rel.HasTreaty(TreatyKind.DefensiveAlliance))
            {
                lineColor = Color.green; // Allies
            }
            else if (rel.HasTreaty(TreatyKind.NonAggressionPact))
            {
                lineColor = Color.cyan; // Non-aggression
            }
            else if (rel.HasTreaty(TreatyKind.TradeAgreement))
            {
                lineColor = Color.yellow; // Trade
            }
            else if (rel.RelationScore < -30f)
            {
                lineColor = new Color(0.8f, 0.2f, 0.2f, 0.4f); // Cold relations
            }
            else
            {
                lineColor = new Color(1f, 1f, 1f, 0.2f); // Neutral
            }

            Gizmos.color = lineColor;
            Gizmos.DrawLine(posA, posB);
        }
    }
}
