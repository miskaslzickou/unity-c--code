using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Mechanism of dominance for a given genetic locus.
/// </summary>
public enum DominanceType : byte
{
    MendelianComplete = 0,   // Dominant allele completely masks recessive
    IncompleteCodominant = 1, // Heterozygous produces blended intermediate value
    Overdominance = 2,       // Heterozygous exhibits fitness superior to either homozygote (heterosis)
    EpistaticModifier = 3    // Acts as an on/off gate or modifier for downstream loci
}

/// <summary>
/// Discrete gene locus representing an allele pair (diploid) on homologous chromosomes.
/// </summary>
[System.Serializable]
public struct GeneLocus
{
    public byte LocusId;
    public byte AlleleMaternal; // 0 to 255
    public byte AllelePaternal; // 0 to 255
    public DominanceType Dominance;
    public float RelativePosition; // 0..1 position on chromosome (for linkage disequilibrium)

    public GeneLocus(byte locusId, byte maternal, byte paternal, DominanceType dominance, float position)
    {
        LocusId = locusId;
        AlleleMaternal = maternal;
        AllelePaternal = paternal;
        Dominance = dominance;
        RelativePosition = Mathf.Clamp01(position);
    }

    /// <summary>
    /// Checks if this locus is homozygous (both alleles identical).
    /// </summary>
    public bool IsHomozygous => AlleleMaternal == AllelePaternal;

    /// <summary>
    /// Computes the expressed numerical phenotype value [0..1] for this locus.
    /// </summary>
    public float Express()
    {
        float valM = AlleleMaternal / 255f;
        float valP = AllelePaternal / 255f;

        switch (Dominance)
        {
            case DominanceType.MendelianComplete:
                // Higher allele value is dominant
                return Mathf.Max(valM, valP);

            case DominanceType.IncompleteCodominant:
                // Intermediate additive blend
                return (valM + valP) * 0.5f;

            case DominanceType.Overdominance:
                // Heterozygote advantage bonus
                float avg = (valM + valP) * 0.5f;
                float diff = Mathf.Abs(valM - valP);
                return Mathf.Clamp01(avg + (diff * 0.25f));

            case DominanceType.EpistaticModifier:
                // If either is zero or below threshold, inactive (recessive null)
                return (valM > 0.2f && valP > 0.2f) ? 1f : 0f;

            default:
                return (valM + valP) * 0.5f;
        }
    }
}

/// <summary>
/// Chromosome containing an array of gene loci with linkage positions.
/// </summary>
[System.Serializable]
public struct Chromosome
{
    public int ChromosomeId;
    public GeneLocus[] Loci;

    public Chromosome(int id, int lociCount)
    {
        ChromosomeId = id;
        Loci = new GeneLocus[lociCount];
        for (int i = 0; i < lociCount; i++)
        {
            float pos = (float)i / Mathf.Max(1, lociCount - 1);
            Loci[i] = new GeneLocus((byte)i, 128, 128, DominanceType.IncompleteCodominant, pos);
        }
    }
}

/// <summary>
/// Full genome encoding diploid karyotype, generation count, and inbreeding coefficient.
/// </summary>
[System.Serializable]
public struct Genome
{
    public int EntityId;
    public int ParentAId;
    public int ParentBId;
    public int Generation;
    public float InbreedingCoefficientF;
    public Chromosome[] Chromosomes;

    public static Genome CreateDefault(int entityId, int chromosomeCount = 2, int lociPerChromosome = 8)
    {
        var g = new Genome
        {
            EntityId = entityId,
            ParentAId = -1,
            ParentBId = -1,
            Generation = 1,
            InbreedingCoefficientF = 0f,
            Chromosomes = new Chromosome[chromosomeCount]
        };

        for (int c = 0; c < chromosomeCount; c++)
        {
            g.Chromosomes[c] = new Chromosome(c, lociPerChromosome);
        }

        return g;
    }
}

/// <summary>
/// Evaluated physical and physiological traits resulting from genome expression.
/// </summary>
[System.Serializable]
public struct PhenotypeTraits
{
    public float Speed;
    public float Size;
    public float Sight;
    public float Metabolism;
    public float Aggression;
    public float MaxHealth;
    public Color FurColor;
    public bool IsAlbino;
    public float InbreedingDepression;
}

/// <summary>
/// Event raised when an offspring genome is successfully bred from two parents.
/// </summary>
public readonly struct CreatureCrossbredEvent : IGameEvent
{
    public readonly int OffspringId;
    public readonly int ParentAId;
    public readonly int ParentBId;
    public readonly float InbreedingF;

    public CreatureCrossbredEvent(int offspringId, int parentAId, int parentBId, float inbreedingF)
    {
        OffspringId = offspringId;
        ParentAId = parentAId;
        ParentBId = parentBId;
        InbreedingF = inbreedingF;
    }
}

/// <summary>
/// Event raised when a point mutation occurs during meiotic crossover.
/// </summary>
public readonly struct MutationOccurredEvent : IGameEvent
{
    public readonly int EntityId;
    public readonly int ChromosomeIndex;
    public readonly int LocusIndex;
    public readonly byte OldAllele;
    public readonly byte NewAllele;

    public MutationOccurredEvent(int entityId, int chromIdx, int locusIdx, byte oldAllele, byte newAllele)
    {
        EntityId = entityId;
        ChromosomeIndex = chromIdx;
        LocusIndex = locusIdx;
        OldAllele = oldAllele;
        NewAllele = newAllele;
    }
}

/// <summary>
/// Mendelian and polygenic inheritance, chromosomes, dominant/recessive alleles,
/// crossbreeding, and mutation engine with dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class GeneticsAndTraitCrossbreeder : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Execution Order")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 175;

    [Header("Genetics Configuration")]
    [SerializeField, Range(0f, 0.5f)] private float defaultMutationRate = 0.05f;
    [SerializeField, Range(0f, 0.5f)] private float pointMutationMagnitude = 0.2f;
    [SerializeField, Range(0.1f, 2f)] private float crossoverRate = 1.0f;
    [SerializeField] private bool enableInbreedingDepression = true;

    [Header("Ecosystem Interop")]
    [SerializeField] private EcosystemEvolutionSim ecosystemSim;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float pedigreeGizmoRadius = 0.4f;

    // Cache of entity genomes and spatial positions for pedigree visualization
    private readonly Dictionary<int, Genome> registeredGenomes = new Dictionary<int, Genome>(256);
    private readonly Dictionary<int, Vector3> registeredPositions = new Dictionary<int, Vector3>(256);

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "GeneticsAndTraitCrossbreeder_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        if (ecosystemSim == null)
        {
            ecosystemSim = FindFirstObjectByType<EcosystemEvolutionSim>();
        }

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
    // REGISTRATION & TRACKING
    // =========================================================================

    public void RegisterGenome(in Genome genome, Vector3 position)
    {
        registeredGenomes[genome.EntityId] = genome;
        registeredPositions[genome.EntityId] = defaultTo2D ? new Vector3(position.x, position.y, 0f) : position;
    }

    public void UpdateEntityPosition(int entityId, Vector3 position)
    {
        if (registeredPositions.ContainsKey(entityId))
        {
            registeredPositions[entityId] = defaultTo2D ? new Vector3(position.x, position.y, 0f) : position;
        }
    }

    public bool TryGetGenome(int entityId, out Genome genome)
    {
        return registeredGenomes.TryGetValue(entityId, out genome);
    }

    // =========================================================================
    // CROSSBREEDING & MEIOSIS ENGINE
    // =========================================================================

    /// <summary>
    /// Performs sexual reproduction using Meiosis crossover with linkage disequilibrium and point mutations.
    /// </summary>
    public bool Crossbreed(in Genome parentA, in Genome parentB, int offspringId, ref Genome offspring, float mutationRateOverride = -1f)
    {
        if (parentA.Chromosomes == null || parentB.Chromosomes == null)
            return false;

        float mutationRate = (mutationRateOverride >= 0f) ? mutationRateOverride : defaultMutationRate;
        int chromCount = Mathf.Min(parentA.Chromosomes.Length, parentB.Chromosomes.Length);

        if (offspring.Chromosomes == null || offspring.Chromosomes.Length != chromCount)
        {
            offspring.Chromosomes = new Chromosome[chromCount];
        }

        offspring.EntityId = offspringId;
        offspring.ParentAId = parentA.EntityId;
        offspring.ParentBId = parentB.EntityId;
        offspring.Generation = Mathf.Max(parentA.Generation, parentB.Generation) + 1;

        // Perform meiosis for each homologous chromosome pair
        for (int c = 0; c < chromCount; c++)
        {
            Chromosome chromA = parentA.Chromosomes[c];
            Chromosome chromB = parentB.Chromosomes[c];
            int lociCount = Mathf.Min(chromA.Loci.Length, chromB.Loci.Length);

            if (offspring.Chromosomes[c].Loci == null || offspring.Chromosomes[c].Loci.Length != lociCount)
            {
                offspring.Chromosomes[c] = new Chromosome(c, lociCount);
            }

            // Generate haploid gamete chromatid from Parent A (maternal contribution)
            byte[] gameteA = ProduceGameteChromatid(chromA, crossoverRate, mutationRate, offspringId, c);

            // Generate haploid gamete chromatid from Parent B (paternal contribution)
            byte[] gameteB = ProduceGameteChromatid(chromB, crossoverRate, mutationRate, offspringId, c);

            // Combine into diploid offspring chromosome
            for (int l = 0; l < lociCount; l++)
            {
                GeneLocus locus = chromA.Loci[l]; // Inherit locus configuration/dominance type
                locus.AlleleMaternal = gameteA[l];
                locus.AllelePaternal = gameteB[l];
                offspring.Chromosomes[c].Loci[l] = locus;
            }
        }

        // Calculate Wright's Coefficient of Inbreeding (F)
        offspring.InbreedingCoefficientF = CalculateInbreedingCoefficient(offspring);

        EventBus<CreatureCrossbredEvent>.Raise(new CreatureCrossbredEvent(
            offspring.EntityId,
            offspring.ParentAId,
            offspring.ParentBId,
            offspring.InbreedingCoefficientF
        ));

        return true;
    }

    /// <summary>
    /// Simulates meiotic recombination across a chromosome pair with linkage disequilibrium.
    /// Alleles closer together have a lower probability of crossing over between them.
    /// </summary>
    private byte[] ProduceGameteChromatid(in Chromosome chrom, float crossRate, float mutRate, int entityId, int chromIdx)
    {
        int length = chrom.Loci.Length;
        byte[] gamete = new byte[length];

        // Randomly choose which strand begins (maternal or paternal chromatid)
        bool useMaternalStrand = UnityEngine.Random.value > 0.5f;

        float lastPosition = 0f;
        for (int i = 0; i < length; i++)
        {
            float currentPos = chrom.Loci[i].RelativePosition;
            float distance = Mathf.Max(0.001f, currentPos - lastPosition);
            lastPosition = currentPos;

            // Linkage disequilibrium: crossover probability scales with genetic distance
            float crossoverProb = 1f - Mathf.Exp(-distance * crossRate);
            if (UnityEngine.Random.value < crossoverProb)
            {
                useMaternalStrand = !useMaternalStrand;
            }

            byte selectedAllele = useMaternalStrand ? chrom.Loci[i].AlleleMaternal : chrom.Loci[i].AllelePaternal;

            // Point mutation check
            if (UnityEngine.Random.value < mutRate)
            {
                byte oldAllele = selectedAllele;
                float delta = UnityEngine.Random.Range(-pointMutationMagnitude, pointMutationMagnitude) * 255f;
                int mutated = Mathf.Clamp(Mathf.RoundToInt(selectedAllele + delta), 0, 255);
                selectedAllele = (byte)mutated;

                EventBus<MutationOccurredEvent>.Raise(new MutationOccurredEvent(entityId, chromIdx, i, oldAllele, selectedAllele));
            }

            gamete[i] = selectedAllele;
        }

        return gamete;
    }

    /// <summary>
    /// Computes Wright's inbreeding coefficient F based on the proportion of homozygous loci.
    /// F = 1 - (Observed Heterozygosity / Expected Heterozygosity).
    /// </summary>
    public float CalculateInbreedingCoefficient(in Genome genome)
    {
        if (genome.Chromosomes == null || genome.Chromosomes.Length == 0)
            return 0f;

        int totalLoci = 0;
        int homozygousLoci = 0;

        for (int c = 0; c < genome.Chromosomes.Length; c++)
        {
            var loci = genome.Chromosomes[c].Loci;
            for (int l = 0; l < loci.Length; l++)
            {
                totalLoci++;
                // Loci with identical or near-identical alleles (< 5% difference) indicate identical-by-descent homozygosity
                if (Mathf.Abs(loci[l].AlleleMaternal - loci[l].AllelePaternal) <= 12)
                {
                    homozygousLoci++;
                }
            }
        }

        if (totalLoci == 0) return 0f;

        float observedHeterozygosity = 1f - ((float)homozygousLoci / totalLoci);
        float expectedHeterozygosity = 0.5f; // Baseline for panmictic biallelic random distribution

        float f = 1f - (observedHeterozygosity / expectedHeterozygosity);
        return Mathf.Clamp01(f);
    }

    // =========================================================================
    // PHENOTYPE EVALUATION & ECOSYSTEM INTEGRATION
    // =========================================================================

    /// <summary>
    /// Evaluates the complete physical phenotype from a genome, including epistatic masking
    /// and inbreeding depression penalties.
    /// </summary>
    public PhenotypeTraits EvaluatePhenotype(in Genome genome)
    {
        var traits = new PhenotypeTraits
        {
            Speed = 3f,
            Size = 1f,
            Sight = 8f,
            Metabolism = 1f,
            Aggression = 0.5f,
            MaxHealth = 100f,
            FurColor = Color.white,
            IsAlbino = false,
            InbreedingDepression = 1f
        };

        if (genome.Chromosomes == null || genome.Chromosomes.Length < 1)
            return traits;

        // Chromosome 0: Core Physical Traits
        var c0 = genome.Chromosomes[0];
        if (c0.Loci != null)
        {
            if (c0.Loci.Length > 0) traits.Speed = Mathf.Lerp(1.5f, 6.0f, c0.Loci[0].Express());
            if (c0.Loci.Length > 1) traits.Size = Mathf.Lerp(0.5f, 2.5f, c0.Loci[1].Express());
            if (c0.Loci.Length > 2) traits.Sight = Mathf.Lerp(4.0f, 16.0f, c0.Loci[2].Express());
            if (c0.Loci.Length > 3) traits.Metabolism = Mathf.Lerp(0.5f, 1.8f, c0.Loci[3].Express());
            if (c0.Loci.Length > 4) traits.Aggression = Mathf.Clamp01(c0.Loci[4].Express());
        }

        // Chromosome 1: Morphological & Pigment Traits
        if (genome.Chromosomes.Length > 1)
        {
            var c1 = genome.Chromosomes[1];
            if (c1.Loci != null)
            {
                float r = (c1.Loci.Length > 0) ? c1.Loci[0].Express() : 0.5f;
                float g = (c1.Loci.Length > 1) ? c1.Loci[1].Express() : 0.5f;
                float b = (c1.Loci.Length > 2) ? c1.Loci[2].Express() : 0.5f;

                // Epistatic locus: Locus 3 controls pigment synthesis (tyrosinase gene)
                // If inactive, entity is albino
                bool pigmentActive = (c1.Loci.Length > 3) ? (c1.Loci[3].Express() > 0.15f) : true;
                if (!pigmentActive)
                {
                    traits.IsAlbino = true;
                    traits.FurColor = new Color(0.95f, 0.95f, 0.98f);
                    traits.Sight *= 0.8f; // Photophobia / visual acuity deficit in albinism
                }
                else
                {
                    traits.FurColor = new Color(r, g, b);
                }
            }
        }

        // Inbreeding Depression Penalties (Wright's coefficient F)
        if (enableInbreedingDepression && genome.InbreedingCoefficientF > 0.2f)
        {
            float depressionPenalty = (genome.InbreedingCoefficientF - 0.2f) / 0.8f; // 0..1 scale
            traits.InbreedingDepression = 1f - (depressionPenalty * 0.45f);

            traits.MaxHealth *= traits.InbreedingDepression;
            traits.Speed *= (0.8f + (0.2f * traits.InbreedingDepression));
            traits.Metabolism *= (1.2f - (0.2f * traits.InbreedingDepression)); // Less efficient metabolic conversion
        }

        return traits;
    }

    /// <summary>
    /// Translates a complex diploid genome directly into an EcosystemEvolutionSim.DNA instance.
    /// </summary>
    public EcosystemEvolutionSim.DNA ToEcosystemDNA(in Genome genome)
    {
        PhenotypeTraits p = EvaluatePhenotype(genome);

        var dna = new EcosystemEvolutionSim.DNA
        {
            speed = p.Speed,
            size = p.Size,
            sight = p.Sight,
            metabolism = p.Metabolism,
            aggression = p.Aggression
        };

        return dna;
    }

    /// <summary>
    /// Applies genome phenotypic expressions directly to an existing EcosystemEvolutionSim creature.
    /// </summary>
    public void ApplyToCreature(in Genome genome, EcosystemEvolutionSim.Creature creature)
    {
        if (creature == null) return;

        creature.dna = ToEcosystemDNA(genome);

        if (creature.body != null)
        {
            // Scale body according to phenotypic size
            creature.body.transform.localScale = Vector3.one * creature.dna.size;

            // Apply coat color
            var renderer = creature.body.GetComponent<Renderer>();
            if (renderer != null)
            {
                PhenotypeTraits p = EvaluatePhenotype(genome);
                renderer.material.color = p.FurColor;
            }
        }
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // Periodic check or maintenance if required
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<Genome> Genomes = new List<Genome>();
        public List<Vector3> Positions = new List<Vector3>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        foreach (var kvp in registeredGenomes)
        {
            container.Genomes.Add(kvp.Value);
            if (registeredPositions.TryGetValue(kvp.Key, out Vector3 pos))
            {
                container.Positions.Add(pos);
            }
            else
            {
                container.Positions.Add(Vector3.zero);
            }
        }

        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        registeredGenomes.Clear();
        registeredPositions.Clear();

        for (int i = 0; i < container.Genomes.Count; i++)
        {
            Genome g = container.Genomes[i];
            Vector3 pos = (i < container.Positions.Count) ? container.Positions[i] : Vector3.zero;
            RegisterGenome(g, pos);
        }
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        foreach (var kvp in registeredGenomes)
        {
            int entityId = kvp.Key;
            Genome genome = kvp.Value;

            if (!registeredPositions.TryGetValue(entityId, out Vector3 pos))
                continue;

            // Color node by inbreeding coefficient (green = outbred, red = severe inbreeding)
            Color nodeCol = Color.Lerp(Color.green, Color.red, genome.InbreedingCoefficientF * 1.5f);
            Gizmos.color = nodeCol;

            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(new Vector3(pos.x, pos.y, 0f), pedigreeGizmoRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, pedigreeGizmoRadius);
            }

            // Draw pedigree inheritance lines to parents
            if (genome.ParentAId >= 0 && registeredPositions.TryGetValue(genome.ParentAId, out Vector3 parentAPos))
            {
                Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.6f);
                Vector3 pA = defaultTo2D ? new Vector3(parentAPos.x, parentAPos.y, 0f) : parentAPos;
                Gizmos.DrawLine(pos, pA);
            }

            if (genome.ParentBId >= 0 && registeredPositions.TryGetValue(genome.ParentBId, out Vector3 parentBPos))
            {
                Gizmos.color = new Color(1f, 0.4f, 0.7f, 0.6f);
                Vector3 pB = defaultTo2D ? new Vector3(parentBPos.x, parentBPos.y, 0f) : parentBPos;
                Gizmos.DrawLine(pos, pB);
            }
        }
    }
}
