using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Logistics;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Compartments of the SEIR epidemic disease model.
/// </summary>
public enum SEIRState : byte
{
    Susceptible = 0,
    Exposed = 1,
    Infectious = 2,
    Recovered = 3,
    Deceased = 4
}

/// <summary>
/// Transmission vectors by which a pathogen can propagate.
/// </summary>
[System.Flags]
public enum TransmissionVector : byte
{
    None = 0,
    Airborne = 1 << 0,
    DirectContact = 1 << 1,
    Waterborne = 1 << 2,
    CargoTradeFomite = 1 << 3
}

/// <summary>
/// Discrete genetic pathogen strain with epidemiological parameters and antigen signature.
/// </summary>
[System.Serializable]
public struct PathogenStrain
{
    public int StrainId;
    public string StrainName;
    public TransmissionVector Vectors;
    [Range(0.01f, 5f)] public float BaseTransmissionRate; // beta / infectivity per second
    public float AirborneRadius;                          // transmission range in meters
    public float IncubationDuration;                      // duration in Exposed state (seconds)
    public float InfectionDuration;                       // duration in Infectious state (seconds)
    [Range(0f, 1f)] public float LethalityRate;           // probability of fatality upon resolution
    [Range(0f, 1f)] public float AsymptomaticChance;      // probability host is an asymptomatic super-spreader
    [Range(0f, 1f)] public float WaterContaminationLevel; // water supply hazard
    public byte AntigenSignature;                         // surface antigen for antibody match
    public int ParentStrainId;

    public PathogenStrain(int id, string name, TransmissionVector vectors, float transmissionRate, float radius,
        float incubation, float infectionDuration, float lethality, float asymptomatic, byte antigen, int parentId = -1)
    {
        StrainId = id;
        StrainName = name;
        Vectors = vectors;
        BaseTransmissionRate = transmissionRate;
        AirborneRadius = radius;
        IncubationDuration = Mathf.Max(0.5f, incubation);
        InfectionDuration = Mathf.Max(1f, infectionDuration);
        LethalityRate = Mathf.Clamp01(lethality);
        AsymptomaticChance = Mathf.Clamp01(asymptomatic);
        WaterContaminationLevel = 0f;
        AntigenSignature = antigen;
        ParentStrainId = parentId;
    }
}

/// <summary>
/// Individual host entity tracked within the epidemiological simulation.
/// </summary>
[System.Serializable]
public class HostEntity
{
    public int HostId;
    public string Name;
    public Vector3 Position;
    public Transform TargetTransform;
    public SEIRState State;
    public int CurrentStrainId;
    public float StateTimer;
    public float ImmunityLevel;      // 0..1 antibody resistance
    public byte KnownAntigenSignature;
    public bool IsAsymptomatic;
    public int InfectedByHostId;
    public bool IsQuarantined;

    public HostEntity(int id, string name, Vector3 pos)
    {
        HostId = id;
        Name = name;
        Position = pos;
        TargetTransform = null;
        State = SEIRState.Susceptible;
        CurrentStrainId = -1;
        StateTimer = 0f;
        ImmunityLevel = 0f;
        KnownAntigenSignature = 0;
        IsAsymptomatic = false;
        InfectedByHostId = -1;
        IsQuarantined = false;
    }

    public Vector3 GetWorldPosition(bool is2D)
    {
        if (TargetTransform != null)
        {
            Vector3 pos = TargetTransform.position;
            return is2D ? new Vector3(pos.x, pos.y, 0f) : pos;
        }
        return is2D ? new Vector3(Position.x, Position.y, 0f) : Position;
    }
}

/// <summary>
/// Geographical quarantine perimeter restricting infectious spread.
/// </summary>
[System.Serializable]
public class QuarantineZone
{
    public int ZoneId;
    public string ZoneName;
    public Bounds Bounds3D;
    [Range(0f, 1f)] public float IsolationEfficiency; // e.g. 0.95 = 95% transmission reduction
    public bool IsActive;

    public bool Contains(Vector3 position, bool is2D)
    {
        if (!IsActive) return false;
        if (is2D)
        {
            Vector3 min = Bounds3D.min;
            Vector3 max = Bounds3D.max;
            return position.x >= min.x && position.x <= max.x &&
                   position.y >= min.y && position.y <= max.y;
        }
        return Bounds3D.Contains(position);
    }
}

/// <summary>
/// Medical clinic or field hospital accelerating recovery and reducing mortality.
/// </summary>
[System.Serializable]
public struct MedicalFacility
{
    public Vector3 Position;
    public float Radius;
    public float CureSpeedMultiplier;
    public float MortalityReduction;
}

/// <summary>
/// Contaminated cargo carrying fomite pathogens via logistics chains.
/// </summary>
[System.Serializable]
public struct ContaminatedCargo
{
    public int JobId;
    public int StrainId;
    public float PersistenceRemaining;
}

/// <summary>
/// Event raised when a host transitions into the Exposed or Infectious state.
/// </summary>
public readonly struct HostInfectedEvent : IGameEvent
{
    public readonly int HostId;
    public readonly int StrainId;
    public readonly int SourceHostId;
    public readonly TransmissionVector Vector;

    public HostInfectedEvent(int hostId, int strainId, int sourceHostId, TransmissionVector vector)
    {
        HostId = hostId;
        StrainId = strainId;
        SourceHostId = sourceHostId;
        Vector = vector;
    }
}

/// <summary>
/// Event raised when an infected host recovers (granting immunity) or succumbs to disease.
/// </summary>
public readonly struct HostResolutionEvent : IGameEvent
{
    public readonly int HostId;
    public readonly int StrainId;
    public readonly bool Survived;

    public HostResolutionEvent(int hostId, int strainId, bool survived)
    {
        HostId = hostId;
        StrainId = strainId;
        Survived = survived;
    }
}

/// <summary>
/// Event raised when a pathogen mutates into a novel strain.
/// </summary>
public readonly struct PathogenMutatedEvent : IGameEvent
{
    public readonly int NewStrainId;
    public readonly int ParentStrainId;
    public readonly string StrainName;
    public readonly float NewTransmissionRate;
    public readonly float NewLethality;

    public PathogenMutatedEvent(int newStrainId, int parentStrainId, string name, float transRate, float lethality)
    {
        NewStrainId = newStrainId;
        ParentStrainId = parentStrainId;
        StrainName = name;
        NewTransmissionRate = transRate;
        NewLethality = lethality;
    }
}

/// <summary>
/// Epidemiological SEIR and pathogen mutation simulator with multi-vector transmission
/// (airborne, contact, waterborne, trade cargo) and dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class EpidemicAndPathogenSpreadSim : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Execution Order")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 170;

    [Header("Mutation & Immunity Dynamics")]
    [SerializeField, Range(0.0001f, 0.05f)] private float strainMutationChance = 0.005f;
    [SerializeField, Range(0.001f, 0.05f)] private float immunityDecayRate = 0.01f;
    [SerializeField] private float waterSanitationLevel = 0.8f; // 0 = open cesspool, 1 = sterile purified

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float hostGizmoRadius = 0.35f;

    // Strains and Hosts
    private readonly Dictionary<int, PathogenStrain> strains = new Dictionary<int, PathogenStrain>(32);
    private readonly List<HostEntity> allHosts = new List<HostEntity>(256);
    private readonly Dictionary<int, HostEntity> hostMap = new Dictionary<int, HostEntity>(256);

    // Containment and Infrastructure
    private readonly List<QuarantineZone> quarantineZones = new List<QuarantineZone>(16);
    private readonly List<MedicalFacility> medicalFacilities = new List<MedicalFacility>(16);
    private readonly List<ContaminatedCargo> contaminatedCargos = new List<ContaminatedCargo>(32);

    // Live SEIR Tallies
    public int CountSusceptible { get; private set; }
    public int CountExposed { get; private set; }
    public int CountInfectious { get; private set; }
    public int CountRecovered { get; private set; }
    public int CountDeceased { get; private set; }

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "EpidemicAndPathogenSpreadSim_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private int nextStrainId = 1;

    private void Awake()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }

        // Initialize default wild-type pathogen strain
        RegisterStrain(new PathogenStrain(
            id: nextStrainId++,
            name: "Alpha-Strain (Wild Type)",
            vectors: TransmissionVector.Airborne | TransmissionVector.DirectContact | TransmissionVector.CargoTradeFomite,
            transmissionRate: 0.35f,
            radius: 3.5f,
            incubation: 5f,
            infectionDuration: 12f,
            lethality: 0.08f,
            asymptomatic: 0.25f,
            antigen: 42
        ));
    }

    private void OnDestroy()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    // =========================================================================
    // STRAIN & HOST REGISTRATION
    // =========================================================================

    public void RegisterStrain(PathogenStrain strain)
    {
        strains[strain.StrainId] = strain;
        if (strain.StrainId >= nextStrainId)
        {
            nextStrainId = strain.StrainId + 1;
        }
    }

    public void RegisterHost(HostEntity host)
    {
        if (host == null) return;
        if (!hostMap.ContainsKey(host.HostId))
        {
            allHosts.Add(host);
            hostMap[host.HostId] = host;
        }
    }

    public bool TryGetHost(int hostId, out HostEntity host)
    {
        return hostMap.TryGetValue(hostId, out host);
    }

    public void AddQuarantineZone(QuarantineZone zone)
    {
        if (zone != null && !quarantineZones.Contains(zone))
        {
            quarantineZones.Add(zone);
        }
    }

    public void AddMedicalFacility(MedicalFacility facility)
    {
        medicalFacilities.Add(facility);
    }

    // =========================================================================
    // INFECTION & FOMITE MECHANICS
    // =========================================================================

    /// <summary>
    /// Explicitly inoculates or infects a host with a specific pathogen strain.
    /// </summary>
    public bool InfectHost(int hostId, int strainId, int sourceHostId = -1, TransmissionVector vector = TransmissionVector.DirectContact)
    {
        if (!hostMap.TryGetValue(hostId, out HostEntity host) || !strains.TryGetValue(strainId, out PathogenStrain strain))
            return false;

        if (host.State == SEIRState.Infectious || host.State == SEIRState.Deceased)
            return false;

        // Calculate immune evasion
        float effectiveImmunity = 0f;
        if (host.ImmunityLevel > 0f)
        {
            // Antigen distance calculation
            int antigenDiff = Mathf.Abs((int)host.KnownAntigenSignature - (int)strain.AntigenSignature);
            float crossProtection = Mathf.Clamp01(1f - (antigenDiff / 128f));
            effectiveImmunity = host.ImmunityLevel * crossProtection;
        }

        if (UnityEngine.Random.value < effectiveImmunity)
        {
            // Host immunity neutralized pathogen exposure
            return false;
        }

        host.State = SEIRState.Exposed;
        host.CurrentStrainId = strainId;
        host.StateTimer = strain.IncubationDuration;
        host.InfectedByHostId = sourceHostId;
        host.IsAsymptomatic = (UnityEngine.Random.value < strain.AsymptomaticChance);

        EventBus<HostInfectedEvent>.Raise(new HostInfectedEvent(hostId, strainId, sourceHostId, vector));
        return true;
    }

    /// <summary>
    /// Contaminates a logistics trade shipment or courier crate with pathogen fomites.
    /// </summary>
    public void ContaminateCargo(int jobId, int strainId, float persistenceSeconds = 60f)
    {
        contaminatedCargos.Add(new ContaminatedCargo
        {
            JobId = jobId,
            StrainId = strainId,
            PersistenceRemaining = persistenceSeconds
        });
    }

    // =========================================================================
    // SIMULATION LOOP (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        UpdateCargoFomites(deltaTime);

        int s = 0, e = 0, inf = 0, r = 0, d = 0;

        // Process hosts
        for (int i = 0; i < allHosts.Count; i++)
        {
            HostEntity host = allHosts[i];
            Vector3 hostPos = host.GetWorldPosition(defaultTo2D);

            // Update quarantine membership
            host.IsQuarantined = IsPointInAnyQuarantine(hostPos);

            switch (host.State)
            {
                case SEIRState.Susceptible:
                    s++;
                    // Natural immunity decay
                    if (host.ImmunityLevel > 0f)
                    {
                        host.ImmunityLevel = Mathf.Max(0f, host.ImmunityLevel - (immunityDecayRate * deltaTime));
                    }
                    break;

                case SEIRState.Exposed:
                    e++;
                    host.StateTimer -= deltaTime;
                    if (host.StateTimer <= 0f)
                    {
                        // Progress from incubation to infectious
                        if (strains.TryGetValue(host.CurrentStrainId, out PathogenStrain strain))
                        {
                            host.State = SEIRState.Infectious;
                            host.StateTimer = strain.InfectionDuration;
                        }
                        else
                        {
                            host.State = SEIRState.Susceptible;
                        }
                    }
                    break;

                case SEIRState.Infectious:
                    inf++;
                    if (strains.TryGetValue(host.CurrentStrainId, out PathogenStrain activeStrain))
                    {
                        // Transmit to nearby susceptible hosts
                        PropagateTransmission(host, activeStrain, hostPos, deltaTime);

                        // Check medical facility influence
                        float cureSpeedMult = 1f;
                        float lethalityReduction = 0f;
                        for (int m = 0; m < medicalFacilities.Count; m++)
                        {
                            MedicalFacility med = medicalFacilities[m];
                            float dist = defaultTo2D
                                ? Vector2.Distance(new Vector2(hostPos.x, hostPos.y), new Vector2(med.Position.x, med.Position.y))
                                : Vector3.Distance(hostPos, med.Position);

                            if (dist <= med.Radius)
                            {
                                cureSpeedMult = Mathf.Max(cureSpeedMult, med.CureSpeedMultiplier);
                                lethalityReduction = Mathf.Max(lethalityReduction, med.MortalityReduction);
                            }
                        }

                        host.StateTimer -= (deltaTime * cureSpeedMult);

                        if (host.StateTimer <= 0f)
                        {
                            // Resolution: mortality check vs recovery
                            float effectiveLethality = Mathf.Clamp01(activeStrain.LethalityRate - lethalityReduction);
                            if (UnityEngine.Random.value < effectiveLethality)
                            {
                                host.State = SEIRState.Deceased;
                                EventBus<HostResolutionEvent>.Raise(new HostResolutionEvent(host.HostId, activeStrain.StrainId, survived: false));
                            }
                            else
                            {
                                host.State = SEIRState.Recovered;
                                host.ImmunityLevel = 1.0f;
                                host.KnownAntigenSignature = activeStrain.AntigenSignature;
                                EventBus<HostResolutionEvent>.Raise(new HostResolutionEvent(host.HostId, activeStrain.StrainId, survived: true));
                            }
                        }
                    }
                    break;

                case SEIRState.Recovered:
                    r++;
                    // Acquired immunity decay
                    host.ImmunityLevel = Mathf.Max(0f, host.ImmunityLevel - (immunityDecayRate * deltaTime));
                    if (host.ImmunityLevel <= 0.05f)
                    {
                        host.State = SEIRState.Susceptible;
                    }
                    break;

                case SEIRState.Deceased:
                    d++;
                    break;
            }
        }

        CountSusceptible = s;
        CountExposed = e;
        CountInfectious = inf;
        CountRecovered = r;
        CountDeceased = d;
    }

    private void PropagateTransmission(HostEntity sourceHost, in PathogenStrain strain, Vector3 sourcePos, float deltaTime)
    {
        float transmissionScale = 1f;
        if (sourceHost.IsQuarantined)
        {
            transmissionScale *= 0.1f; // 90% suppression in quarantine
        }

        // Airborne and direct contact transmission
        if ((strain.Vectors & (TransmissionVector.Airborne | TransmissionVector.DirectContact)) != 0)
        {
            float radius = strain.AirborneRadius;
            float sqrRadius = radius * radius;

            for (int i = 0; i < allHosts.Count; i++)
            {
                HostEntity target = allHosts[i];
                if (target.State != SEIRState.Susceptible) continue;

                Vector3 targetPos = target.GetWorldPosition(defaultTo2D);
                float sqrDist = defaultTo2D
                    ? (new Vector2(targetPos.x - sourcePos.x, targetPos.y - sourcePos.y)).sqrMagnitude
                    : (targetPos - sourcePos).sqrMagnitude;

                if (sqrDist <= sqrRadius)
                {
                    float dist = Mathf.Sqrt(sqrDist);
                    float distanceAttenuation = 1f - Mathf.Clamp01(dist / radius);
                    float infectProb = strain.BaseTransmissionRate * distanceAttenuation * transmissionScale * deltaTime;

                    if (UnityEngine.Random.value < infectProb)
                    {
                        InfectHost(target.HostId, strain.StrainId, sourceHost.HostId, TransmissionVector.Airborne);

                        // Pathogen mutation chance during transmission event
                        if (UnityEngine.Random.value < strainMutationChance)
                        {
                            MutatePathogen(strain);
                        }
                    }
                }
            }
        }

        // Waterborne vector
        if ((strain.Vectors & TransmissionVector.Waterborne) != 0)
        {
            float waterRisk = (1f - waterSanitationLevel) * strain.BaseTransmissionRate * 0.2f * deltaTime;
            if (UnityEngine.Random.value < waterRisk && allHosts.Count > 0)
            {
                int randomIdx = UnityEngine.Random.Range(0, allHosts.Count);
                if (allHosts[randomIdx].State == SEIRState.Susceptible)
                {
                    InfectHost(allHosts[randomIdx].HostId, strain.StrainId, sourceHost.HostId, TransmissionVector.Waterborne);
                }
            }
        }
    }

    private void MutatePathogen(in PathogenStrain parent)
    {
        int newId = nextStrainId++;
        float transDelta = UnityEngine.Random.Range(-0.1f, 0.2f);
        float lethalityDelta = UnityEngine.Random.Range(-0.05f, 0.05f);
        byte newAntigen = (byte)((parent.AntigenSignature + UnityEngine.Random.Range(20, 60)) % 256);

        var mutated = new PathogenStrain(
            id: newId,
            name: $"Strain-{newId} (Variant of {parent.StrainName})",
            vectors: parent.Vectors,
            transmissionRate: Mathf.Clamp(parent.BaseTransmissionRate + transDelta, 0.05f, 5f),
            radius: Mathf.Clamp(parent.AirborneRadius * UnityEngine.Random.Range(0.9f, 1.25f), 1f, 15f),
            incubation: Mathf.Clamp(parent.IncubationDuration * UnityEngine.Random.Range(0.8f, 1.2f), 1f, 30f),
            infectionDuration: parent.InfectionDuration,
            lethality: Mathf.Clamp01(parent.LethalityRate + lethalityDelta),
            asymptomatic: Mathf.Clamp01(parent.AsymptomaticChance + UnityEngine.Random.Range(-0.1f, 0.15f)),
            antigen: newAntigen,
            parentId: parent.StrainId
        );

        RegisterStrain(mutated);
        EventBus<PathogenMutatedEvent>.Raise(new PathogenMutatedEvent(newId, parent.StrainId, mutated.StrainName, mutated.BaseTransmissionRate, mutated.LethalityRate));
    }

    private void UpdateCargoFomites(float deltaTime)
    {
        for (int i = contaminatedCargos.Count - 1; i >= 0; i--)
        {
            ContaminatedCargo cargo = contaminatedCargos[i];
            cargo.PersistenceRemaining -= deltaTime;
            if (cargo.PersistenceRemaining <= 0f)
            {
                contaminatedCargos.RemoveAt(i);
            }
            else
            {
                contaminatedCargos[i] = cargo;
            }
        }
    }

    private bool IsPointInAnyQuarantine(Vector3 pos)
    {
        for (int i = 0; i < quarantineZones.Count; i++)
        {
            if (quarantineZones[i].Contains(pos, defaultTo2D))
            {
                return true;
            }
        }
        return false;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<PathogenStrain> Strains = new List<PathogenStrain>();
        public List<HostEntity> Hosts = new List<HostEntity>();
        public List<QuarantineZone> Quarantines = new List<QuarantineZone>();
        public int NextStrainId;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            NextStrainId = nextStrainId
        };
        foreach (var kvp in strains) container.Strains.Add(kvp.Value);
        container.Hosts.AddRange(allHosts);
        container.Quarantines.AddRange(quarantineZones);

        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        nextStrainId = container.NextStrainId;
        strains.Clear();
        allHosts.Clear();
        hostMap.Clear();
        quarantineZones.Clear();

        for (int i = 0; i < container.Strains.Count; i++)
        {
            RegisterStrain(container.Strains[i]);
        }

        for (int i = 0; i < container.Hosts.Count; i++)
        {
            RegisterHost(container.Hosts[i]);
        }

        quarantineZones.AddRange(container.Quarantines);
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Quarantine perimeters
        for (int i = 0; i < quarantineZones.Count; i++)
        {
            var zone = quarantineZones[i];
            if (!zone.IsActive) continue;

            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            if (defaultTo2D)
            {
                Vector3 center = new Vector3(zone.Bounds3D.center.x, zone.Bounds3D.center.y, 0f);
                Vector3 size = new Vector3(zone.Bounds3D.size.x, zone.Bounds3D.size.y, 0.1f);
                Gizmos.DrawWireCube(center, size);
            }
            else
            {
                Gizmos.DrawWireCube(zone.Bounds3D.center, zone.Bounds3D.size);
            }
        }

        // Draw Hosts with SEIR status coloring
        for (int i = 0; i < allHosts.Count; i++)
        {
            HostEntity host = allHosts[i];
            Vector3 pos = host.GetWorldPosition(defaultTo2D);

            Color hostColor;
            switch (host.State)
            {
                case SEIRState.Susceptible: hostColor = Color.blue; break;
                case SEIRState.Exposed: hostColor = Color.yellow; break;
                case SEIRState.Infectious: hostColor = host.IsAsymptomatic ? new Color(1f, 0.4f, 0f) : Color.red; break;
                case SEIRState.Recovered: hostColor = Color.green; break;
                case SEIRState.Deceased: hostColor = Color.black; break;
                default: hostColor = Color.white; break;
            }

            Gizmos.color = hostColor;
            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(pos, hostGizmoRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, hostGizmoRadius);
            }

            // Draw transmission line if infectious
            if (host.State == SEIRState.Infectious && strains.TryGetValue(host.CurrentStrainId, out PathogenStrain strain))
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.2f);
                Gizmos.DrawWireSphere(pos, strain.AirborneRadius);
            }
        }
    }
}
