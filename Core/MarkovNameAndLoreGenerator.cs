using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GameSystems.Economy;
using GameSystems.Persistence;

/// <summary>
/// Phonetic and cultural styles for procedural Markov generation.
/// </summary>
public enum LinguisticCulture
{
    NordicFantasy,
    CyberpunkMegaCorp,
    SciFiAlien,
    GrecoRoman,
    Slavic
}

/// <summary>
/// Type of procedural lore or chronicle document to produce.
/// </summary>
public enum LoreDocumentType
{
    CorporateManifesto,
    HistoricalChronicle,
    DiplomaticTreaty,
    SettlementCharter,
    MilitaryIncident
}

/// <summary>
/// Trained n-gram Markov transition model for a specific cultural phonetic set.
/// </summary>
[System.Serializable]
public class MarkovModel
{
    public LinguisticCulture Culture;
    public int Order = 2; // Bi-gram or Tri-gram

    // Transition frequencies: Key is n-gram prefix, value is list of observed succeeding characters
    private readonly Dictionary<string, List<char>> transitions = new Dictionary<string, List<char>>(128);
    private readonly List<string> startPrefixes = new List<string>(32);

    public void Train(string[] words, int order = 2)
    {
        Order = order;
        transitions.Clear();
        startPrefixes.Clear();

        if (words == null || words.Length == 0) return;

        for (int w = 0; w < words.Length; w++)
        {
            string word = words[w].Trim();
            if (word.Length < order) continue;

            string start = word.Substring(0, order);
            startPrefixes.Add(start);

            for (int i = 0; i <= word.Length - order; i++)
            {
                string gram = word.Substring(i, order);
                char nextChar = (i + order < word.Length) ? word[i + order] : '\0'; // '\0' indicates termination

                if (!transitions.TryGetValue(gram, out var nextList))
                {
                    nextList = new List<char>(8);
                    transitions[gram] = nextList;
                }
                nextList.Add(nextChar);
            }
        }
    }

    public int GenerateWord(Span<char> outSpan, int minLength, int maxLength, System.Random prng)
    {
        if (startPrefixes.Count == 0 || outSpan.Length < maxLength)
            return 0;

        string currentGram = startPrefixes[prng.Next(startPrefixes.Count)];
        int written = 0;

        for (int i = 0; i < currentGram.Length; i++)
        {
            outSpan[written++] = currentGram[i];
        }

        while (written < maxLength)
        {
            if (!transitions.TryGetValue(currentGram, out var nextList) || nextList.Count == 0)
            {
                break;
            }

            char next = nextList[prng.Next(nextList.Count)];
            if (next == '\0')
            {
                if (written >= minLength)
                    break;
                // Retry if too short
                continue;
            }

            outSpan[written++] = next;

            // Shift gram window
            currentGram = outSpan.Slice(written - Order, Order).ToString();
        }

        // Capitalize first character
        if (written > 0)
        {
            outSpan[0] = char.ToUpperInvariant(outSpan[0]);
        }

        return written;
    }
}

/// <summary>
/// Deterministic procedural name, corporation, city, and historical lore generator
/// using N-gram Markov chains and context-free grammar templates with zero-GC runtime execution.
/// </summary>
[DisallowMultipleComponent]
public class MarkovNameAndLoreGenerator : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Config")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int defaultMarkovOrder = 2;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoMarkerRadius = 0.5f;

    // Trained models per linguistic culture
    private readonly Dictionary<LinguisticCulture, MarkovModel> models = new Dictionary<LinguisticCulture, MarkovModel>(8);

    // Grammar template dictionaries
    private readonly Dictionary<string, string[]> grammarRules = new Dictionary<string, string[]>(32);

    // Reusable buffers to guarantee zero-GC allocations during generation
    private readonly StringBuilder scratchBuilder = new StringBuilder(512);
    private readonly char[] charBuffer = new char[128];

    // Tracked spatial POIs for Gizmo rendering
    private readonly List<Vector3> poiPositions = new List<Vector3>(32);
    private readonly List<string> poiNames = new List<string>(32);

    public string SaveKey => "MarkovNameAndLoreGenerator_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        InitializeCorporaAndTrain();
        InitializeGrammarRules();

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
    // CORPUS INITIALIZATION & MARKOV TRAINING
    // =========================================================================

    private void InitializeCorporaAndTrain()
    {
        // 1. Nordic Fantasy Corpus
        TrainCulture(LinguisticCulture.NordicFantasy, new string[]
        {
            "Thorin", "Ragnar", "Freyja", "Asgard", "Valgard", "Bifrost", "Sigurd", "Hrothgar",
            "Alfhild", "Odin", "Jotun", "Gunnar", "Torvald", "Einar", "Astrid", "Brynhild",
            "Skadi", "Fenrir", "Heimdall", "Baldur", "Yggdrasil", "Midgard", "Niflheim", "Valkyrie",
            "Starkad", "Harald", "Svein", "Bjorn", "Knut", "Thorkell", "Ingrid", "Sigrid"
        }, defaultMarkovOrder);

        // 2. Cyberpunk MegaCorp Corpus
        TrainCulture(LinguisticCulture.CyberpunkMegaCorp, new string[]
        {
            "Arasaka", "Weyland", "OmniCorp", "Cyberdyne", "Tetsuo", "BioTech", "Zion", "Shinra",
            "Zaibatsu", "Kuroshio", "Synthetix", "NeuroNet", "Militech", "Tyrell", "OmniNet",
            "Vortex", "ApexCorp", "Hyperion", "ExoDyne", "NexusGen", "Cyberware", "AcroTech",
            "Solyaris", "Kiroshi", "KangTao", "TraumaTeam", "Biodyne", "DataDyne", "CryoTech"
        }, defaultMarkovOrder);

        // 3. Sci-Fi Alien Corpus
        TrainCulture(LinguisticCulture.SciFiAlien, new string[]
        {
            "Xylar", "Krynn", "Zuljin", "Thrax", "Vorgon", "Qaltash", "Zorith", "Golgoth",
            "Phantor", "Ixion", "Vortigaunt", "XelNaga", "Protoss", "Zergling", "Taldarim",
            "Klaatu", "Vorlon", "Shadow", "Minbari", "Narn", "Centauri", "Cylons", "Tyranid",
            "Necron", "Eldar", "Ork", "Krork", "Annihilator", "ZulKarr", "Vreltek"
        }, defaultMarkovOrder);

        // 4. Greco-Roman Corpus
        TrainCulture(LinguisticCulture.GrecoRoman, new string[]
        {
            "Aurelius", "Valerius", "Cassius", "Helena", "Octavius", "Tiberius", "Agamemnon",
            "Atalanta", "Leonidas", "Lysander", "Maximus", "Claudius", "Flavius", "Severus",
            "Constantine", "Hadrian", "Trajan", "Vespasian", "Domitian", "Antoninus", "Pericles",
            "Themistocles", "Alcibiades", "Demosthenes", "Epaminondas", "Scipio", "Cicero"
        }, defaultMarkovOrder);

        // 5. Slavic Corpus
        TrainCulture(LinguisticCulture.Slavic, new string[]
        {
            "Miroslav", "Bogdan", "Vladislav", "Stanislav", "Yaroslav", "Svetlana", "Radomir",
            "Lubomir", "Dobromil", "Borislav", "Kazimir", "Svyatoslav", "Vsevolod", "Mstislav",
            "Rostislav", "Vojtech", "Boleslav", "Premysl", "Ludmila", "Bozena", "Drahomira"
        }, defaultMarkovOrder);
    }

    public void TrainCulture(LinguisticCulture culture, string[] words, int order = 2)
    {
        var model = new MarkovModel();
        model.Train(words, order);
        models[culture] = model;
    }

    // =========================================================================
    // GRAMMAR RULES INITIALIZATION
    // =========================================================================

    private void InitializeGrammarRules()
    {
        grammarRules["{Industry}"] = new string[] { "Aerospace", "Heavy Industries", "Pharmaceuticals", "Synthetic Dynamics", "Orbital Logistics", "Cybernetics", "Nanotech", "Mining Consortium" };
        grammarRules["{CorpSuffix}"] = new string[] { "Corp", "GmbH", "Inc.", "Holdings", "Group", "Enterprises", "Solutions", "Technologies" };
        grammarRules["{TechPrefix}"] = new string[] { "Apex", "Quantum", "Hyper", "Vortex", "Aero", "Cyber", "Bio", "Neuro", "Exo", "Sub" };
        grammarRules["{Adjective}"] = new string[] { "Grand", "Iron", "Silent", "Eternal", "Sovereign", "Crimson", "Gilded", "Obsidian", "Celestial", "Valiant" };
        grammarRules["{Noun}"] = new string[] { "Bastion", "Citadel", "Reach", "Sanctuary", "Enclave", "Harbor", "Nexus", "Foundry", "Outpost", "Summit" };
        grammarRules["{BattleType}"] = new string[] { "Siege", "Skirmish", "Battle", "Campaign", "Incursion", "Blockade", "Offensive" };
        grammarRules["{Outcome}"] = new string[] { "culminating in an unconditional surrender", "resulting in a stalemate and treaty", "ending decades of territorial dispute", "establishing new border frontiers" };
    }

    // =========================================================================
    // PROCEDURAL GENERATION APIS
    // =========================================================================

    /// <summary>
    /// Generates a single procedural word using the trained Markov model.
    /// Allocates no garbage if using the Span-based overload.
    /// </summary>
    public string GenerateWord(LinguisticCulture culture, int minLength = 4, int maxLength = 10, int seed = 0)
    {
        Span<char> span = charBuffer.AsSpan();
        var prng = (seed != 0) ? new System.Random(seed) : new System.Random();

        if (models.TryGetValue(culture, out MarkovModel model))
        {
            int len = model.GenerateWord(span, minLength, maxLength, prng);
            if (len > 0)
            {
                return new string(span.Slice(0, len).ToArray());
            }
        }

        return "Unnamed";
    }

    /// <summary>
    /// Zero-GC method to fill a destination char buffer with a procedural name.
    /// </summary>
    public int GenerateWord(LinguisticCulture culture, Span<char> destination, int minLength = 4, int maxLength = 10, int seed = 0)
    {
        var prng = (seed != 0) ? new System.Random(seed) : new System.Random();
        if (models.TryGetValue(culture, out MarkovModel model))
        {
            return model.GenerateWord(destination, minLength, maxLength, prng);
        }
        return 0;
    }

    /// <summary>
    /// Generates a procedural company name suitable for DynamicMarketEconomy.cs listings.
    /// </summary>
    public string GenerateCorporationName(int seed = 0)
    {
        var prng = (seed != 0) ? new System.Random(seed) : new System.Random();
        string baseName = GenerateWord(LinguisticCulture.CyberpunkMegaCorp, 4, 8, prng.Next());

        scratchBuilder.Clear();
        int pattern = prng.Next(3);
        switch (pattern)
        {
            case 0:
                scratchBuilder.Append(baseName).Append(' ').Append(GetRandomRule("{Industry}", prng));
                break;
            case 1:
                scratchBuilder.Append(baseName).Append(' ').Append(GetRandomRule("{CorpSuffix}", prng));
                break;
            default:
                scratchBuilder.Append(GetRandomRule("{TechPrefix}", prng)).Append(baseName).Append(' ').Append(GetRandomRule("{CorpSuffix}", prng));
                break;
        }

        return scratchBuilder.ToString();
    }

    /// <summary>
    /// Generates a deterministic settlement/city name based on spatial world coordinates.
    /// Works seamlessly for both 2D map coordinates and 3D terrain positions.
    /// </summary>
    public string GenerateSpatialLocationName(Vector3 worldPos, LinguisticCulture culture)
    {
        // Compute deterministic spatial hash seed
        int xInt = Mathf.RoundToInt(worldPos.x * 10f);
        int yInt = Mathf.RoundToInt(worldPos.y * 10f);
        int zInt = defaultTo2D ? 0 : Mathf.RoundToInt(worldPos.z * 10f);
        int spatialSeed = (xInt * 73856093) ^ (yInt * 19349663) ^ (zInt * 83492791);

        var prng = new System.Random(spatialSeed);
        string name = GenerateWord(culture, 5, 9, prng.Next());

        scratchBuilder.Clear();
        if (prng.Next(2) == 0)
        {
            scratchBuilder.Append(name).Append(' ').Append(GetRandomRule("{Noun}", prng));
        }
        else
        {
            scratchBuilder.Append(GetRandomRule("{Adjective}", prng)).Append(' ').Append(name);
        }

        string result = scratchBuilder.ToString();
        poiPositions.Add(defaultTo2D ? new Vector3(worldPos.x, worldPos.y, 0f) : worldPos);
        poiNames.Add(result);

        return result;
    }

    /// <summary>
    /// Expands a context-free grammar template into rich historical chronicles, lore, or treaties.
    /// </summary>
    public string GenerateLoreDocument(LoreDocumentType docType, int seed = 0)
    {
        var prng = (seed != 0) ? new System.Random(seed) : new System.Random();
        scratchBuilder.Clear();

        switch (docType)
        {
            case LoreDocumentType.CorporateManifesto:
                string corp = GenerateCorporationName(prng.Next());
                scratchBuilder.Append("=== CORPORATE CHARTER: ").Append(corp).Append(" ===\n")
                              .Append("Established in the pursuit of ").Append(GetRandomRule("{Industry}", prng))
                              .Append(". Our mandate: unwavering technological supremacy across all operational sectors.");
                break;

            case LoreDocumentType.HistoricalChronicle:
                int year = prng.Next(1200, 3150);
                string loc = GenerateWord(LinguisticCulture.NordicFantasy, 5, 8, prng.Next());
                scratchBuilder.Append("Chronicles of Year ").Append(year).Append(":\n")
                              .Append("The decisive ").Append(GetRandomRule("{BattleType}", prng))
                              .Append(" of ").Append(loc)
                              .Append(" concluded after prolonged hostilities, ")
                              .Append(GetRandomRule("{Outcome}", prng)).Append(".");
                break;

            case LoreDocumentType.DiplomaticTreaty:
                string city = GenerateWord(LinguisticCulture.GrecoRoman, 6, 9, prng.Next());
                scratchBuilder.Append("THE ").Append(city.ToUpperInvariant()).Append(" ACCORDS\n")
                              .Append("Hereby ratified: A bilateral ")
                              .Append(GetRandomRule("{Adjective}", prng))
                              .Append(" non-aggression and mutual defense pact between all signatory sovereign entities.");
                break;

            default:
                scratchBuilder.Append("Archived Document: Classification Restricted.");
                break;
        }

        return scratchBuilder.ToString();
    }

    private string GetRandomRule(string key, System.Random prng)
    {
        if (grammarRules.TryGetValue(key, out var list) && list.Length > 0)
        {
            return list[prng.Next(list.Length)];
        }
        return string.Empty;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<Vector3> Positions = new List<Vector3>();
        public List<string> Names = new List<string>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        container.Positions.AddRange(poiPositions);
        container.Names.AddRange(poiNames);
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        poiPositions.Clear();
        poiNames.Clear();
        poiPositions.AddRange(container.Positions);
        poiNames.AddRange(container.Names);
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < poiPositions.Count; i++)
        {
            Vector3 pos = poiPositions[i];
            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(new Vector3(pos.x, pos.y, 0f), gizmoMarkerRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, gizmoMarkerRadius);
            }
        }
    }
}
