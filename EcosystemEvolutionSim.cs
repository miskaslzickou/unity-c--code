using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Evolutionary ecosystem simulation: plants, herbivores and predators with DNA genes
/// (speed, size, sight, metabolism, aggression), hunger, reproduction with mutation,
/// and natural selection over generations. Works in 2D or 3D (spawns on a plane).
///
/// Use cases: living worlds in RPGs, god games, evolution education toys, emergent
/// background life in survival games, or a standalone simulator (watch ecosystems crash/boom).
/// Creatures use simple steering (seek food, flee predators) - no navmesh needed.
/// Visualize creatures as primitives (auto-generated) or assign prefabs.
/// </summary>
public class EcosystemEvolutionSim : MonoBehaviour
{
    public enum Dimension { TwoD, ThreeD }

    [Serializable]
    public class DNA
    {
        public float speed = 3f;      // max movement speed
        public float size = 1f;       // affects health & food value, costs energy
        public float sight = 8f;      // perception radius
        public float metabolism = 1f; // energy drain multiplier (lower = efficient)
        public float aggression = 0f; // herbivores: fight vs flee; predators: chase range bias

        public DNA Mutate(float rate)
        {
            var d = new DNA
            {
                speed = Mathf.Max(0.5f, speed * Uniform(rate)),
                size = Mathf.Max(0.3f, size * Uniform(rate)),
                sight = Mathf.Max(2f, sight * Uniform(rate)),
                metabolism = Mathf.Max(0.3f, metabolism * Uniform(rate)),
                aggression = Mathf.Clamp01(aggression + UnityEngine.Random.Range(-rate, rate))
            };
            return d;
        }

        static float Uniform(float rate) => 1f + UnityEngine.Random.Range(-rate, rate);
    }

    public enum Species { Plant, Herbivore, Predator }

    public class Creature
    {
        public Species species;
        public DNA dna;
        public GameObject body;
        public Vector3 position;
        public Vector3 heading;
        public float energy;
        public float maxEnergy;
        public int age;
        public int generation = 1;
        public float reproduceTimer;
        public bool IsAlive = true;
    }

    [Header("World")]
    public Dimension dimension = Dimension.TwoD;
    public Vector2 areaSize = new Vector2(60f, 60f); // centered on this transform
    public Transform areaCenter;

    [Header("Starting Population")]
    public int startingPlants = 80;
    public int startingHerbivores = 25;
    public int startingPredators = 6;

    [Header("Species Rules")]
    public float plantEnergy = 20f;
    public float plantRegrowPerSecond = 0.6f;   // plants spawned per second
    public float herbivoreStartEnergy = 60f;
    public float predatorStartEnergy = 90f;
    public float baseMetabolismDrain = 1.2f;    // energy/second at size 1, metabolism 1
    public float reproduceEnergyThreshold = 110f;
    public float reproduceCooldown = 8f;
    public float mutationRate = 0.15f;
    public int maxCreatures = 400;
    public int maxAge = 120;                    // seconds

    [Header("Behaviour")]
    public float decisionInterval = 0.4f;
    public float eatRadius = 0.8f;
    public float plantNutrition = 35f;
    public float meatNutrition = 55f;

    [Header("Optional Prefabs (else primitives are generated)")]
    public GameObject plantPrefab;
    public GameObject herbivorePrefab;
    public GameObject predatorPrefab;

    [Header("Debug / Stats")]
    public bool autoGUIStats = true;
    public bool drawGizmos = true;

    public int PlantCount { get; private set; }
    public int HerbivoreCount { get; private set; }
    public int PredatorCount { get; private set; }
    public int Generation { get; private set; }
    public event Action<Creature> OnCreatureBorn;
    public event Action<Creature> OnCreatureDied;

    readonly List<Creature> creatures = new List<Creature>();
    float nextDecision, nextPlant;

    void Start()
    {
        if (areaCenter == null) areaCenter = transform;
        for (int i = 0; i < startingPlants; i++) SpawnCreature(Species.Plant, RandomPoint(), new DNA { speed = 0f, sight = 0f });
        for (int i = 0; i < startingHerbivores; i++) SpawnCreature(Species.Herbivore, RandomPoint(), new DNA());
        for (int i = 0; i < startingPredators; i++) SpawnCreature(Species.Predator, RandomPoint(), new DNA { speed = 4.5f, size = 1.4f, aggression = 0.8f });
    }

    void Update()
    {
        // per-frame: metabolism, aging, movement
        for (int i = creatures.Count - 1; i >= 0; i--)
        {
            var c = creatures[i];
            if (!c.IsAlive) { creatures.RemoveAt(i); continue; }

            if (c.species != Species.Plant)
            {
                float drain = baseMetabolismDrain * c.dna.metabolism * c.dna.size * (c.heading.sqrMagnitude > 0.01f ? 1.3f : 1f);
                c.energy -= drain * Time.deltaTime;
                c.age += Mathf.RoundToInt(Time.deltaTime);

                Move(c);

                if (c.energy <= 0f || c.age > maxAge) { Kill(c); continue; }

                c.reproduceTimer += Time.deltaTime;
                if (c.energy >= reproduceEnergyThreshold && c.reproduceTimer >= reproduceCooldown)
                    Reproduce(c);
            }
        }

        // periodic: plant regrowth + AI decisions
        if (Time.time >= nextPlant)
        {
            nextPlant = Time.time + 1f / Mathf.Max(0.01f, plantRegrowPerSecond);
            if (PlantCount < maxCreatures) SpawnCreature(Species.Plant, RandomPoint(), new DNA { speed = 0f, sight = 0f });
        }

        if (Time.time >= nextDecision)
        {
            nextDecision = Time.time + decisionInterval;
            for (int i = 0; i < creatures.Count; i++)
                if (creatures[i].species != Species.Plant) Decide(creatures[i]);
        }
    }

    // ---------------- BEHAVIOUR ----------------

    void Decide(Creature c)
    {
        Creature bestFood = null, bestThreat = null;
        float bestFoodScore = float.MinValue, bestThreatDist = float.MaxValue;

        foreach (var o in creatures)
        {
            if (o == c || !o.IsAlive) continue;
            float d = Vector3.Distance(c.position, o.position);
            if (d > c.dna.sight) continue;

            bool wantsIt =
                (c.species == Species.Herbivore && o.species == Species.Plant) ||
                (c.species == Species.Predator && o.species == Species.Herbivore);

            if (wantsIt && d < bestThreatDist * 0f + float.MaxValue)
            {
                // prefer nearest food, biased by aggression for far targets
                float score = -d / Mathf.Max(1f, c.dna.sight) + (c.species == Species.Predator ? c.dna.aggression : 0f);
                if (score > bestFoodScore) { bestFoodScore = score; bestFood = o; }
            }

            if (c.species == Species.Herbivore && o.species == Species.Predator && d < bestThreatDist)
            {
                bestThreatDist = d;
                bestThreat = o;
            }
        }

        if (bestThreat != null && c.dna.aggression < 0.5f)
        {
            c.heading = (c.position - bestThreat.position).normalized; // flee
        }
        else if (bestFood != null)
        {
            c.heading = (bestFood.position - c.position).normalized;   // seek food
            if (Vector3.Distance(c.position, bestFood.position) < eatRadius + c.dna.size * 0.5f)
                Eat(c, bestFood);
        }
        else if (c.heading.sqrMagnitude < 0.01f || UnityEngine.Random.value < 0.15f)
        {
            c.heading = RandomDirection();                              // wander
        }
    }

    void Move(Creature c)
    {
        Vector3 vel = c.heading * c.dna.speed;
        if (dimension == Dimension.TwoD) vel.z = 0f;
        c.position += vel * Time.deltaTime;
        c.position = ClampToArea(c.position);
        if (c.body != null)
        {
            c.body.transform.position = c.position;
            if (vel.sqrMagnitude > 0.01f && dimension == Dimension.ThreeD)
                c.body.transform.rotation = Quaternion.Slerp(c.body.transform.rotation, Quaternion.LookRotation(vel), 4f * Time.deltaTime);
            else if (vel.sqrMagnitude > 0.01f)
                c.body.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(vel.y, vel.x) * Mathf.Rad2Deg - 90f);
        }
    }

    void Eat(Creature eater, Creature food)
    {
        float nutrition = food.species == Species.Plant ? plantNutrition : meatNutrition;
        eater.energy += nutrition * Mathf.Max(0.5f, food.dna.size);
        Kill(food);
    }

    void Reproduce(Creature parent)
    {
        if (creatures.Count >= maxCreatures) return;
        parent.energy *= 0.5f; // cost of raising young
        parent.reproduceTimer = 0f;

        var child = SpawnCreature(parent.species, parent.position + RandomDirection() * 1.5f, parent.dna.Mutate(mutationRate));
        child.generation = parent.generation + 1;
        child.energy = parent.energy;
        Generation = Mathf.Max(Generation, child.generation);
        OnCreatureBorn?.Invoke(child);
    }

    void Kill(Creature c)
    {
        if (!c.IsAlive) return;
        c.IsAlive = false;
        if (c.body != null) Destroy(c.body);
        switch (c.species)
        {
            case Species.Plant: PlantCount--; break;
            case Species.Herbivore: HerbivoreCount--; break;
            case Species.Predator: PredatorCount--; break;
        }
        OnCreatureDied?.Invoke(c);
    }

    // ---------------- SPAWNING HELPERS ----------------

    Creature SpawnCreature(Species s, Vector3 pos, DNA dna)
    {
        var c = new Creature
        {
            species = s,
            dna = dna,
            position = ClampToArea(pos),
            heading = s == Species.Plant ? Vector3.zero : RandomDirection(),
            maxEnergy = s == Species.Plant ? plantEnergy : s == Species.Herbivore ? herbivoreStartEnergy : predatorStartEnergy,
            body = MakeBody(s, dna)
        };
        c.energy = c.maxEnergy;
        c.body.transform.position = c.position;
        if (s == Species.Plant) c.body.name = "Plant";
        creatures.Add(c);
        switch (s)
        {
            case Species.Plant: PlantCount++; break;
            case Species.Herbivore: HerbivoreCount++; break;
            case Species.Predator: PredatorCount++; break;
        }
        OnCreatureBorn?.Invoke(c);
        return c;
    }

    GameObject MakeBody(Species s, DNA dna)
    {
        GameObject prefab = s == Species.Plant ? plantPrefab : s == Species.Herbivore ? herbivorePrefab : predatorPrefab;
        GameObject go;
        if (prefab != null)
        {
            go = Instantiate(prefab, areaCenter);
        }
        else if (dimension == Dimension.TwoD)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.GetComponent<Collider>().isTrigger = true; // don't fight 2D physics
            go.GetComponent<Renderer>().material.color = s == Species.Plant ? Color.green : s == Species.Herbivore ? Color.yellow : Color.red;
        }
        else
        {
            go = GameObject.CreatePrimitive(s == Species.Plant ? PrimitiveType.Cube : PrimitiveType.Capsule);
            var col = go.GetComponent<Collider>();
            if (s == Species.Plant) col.isTrigger = true;
            go.GetComponent<Renderer>().material.color = s == Species.Plant ? Color.green : s == Species.Herbivore ? Color.yellow : Color.red;
        }
        float scale = Mathf.Clamp(dna.size, 0.3f, 3f);
        go.transform.localScale = Vector3.one * scale * (dimension == Dimension.TwoD ? 0.6f : 1f);
        go.name = s.ToString();
        return go;
    }

    Vector3 RandomPoint()
    {
        Vector3 c = areaCenter != null ? areaCenter.position : Vector3.zero;
        Vector2 half = areaSize * 0.5f;
        Vector3 p = c + new Vector3(UnityEngine.Random.Range(-half.x, half.x), dimension == Dimension.TwoD ? 0f : UnityEngine.Random.Range(-half.y * 0.3f, half.y * 0.3f), dimension == Dimension.TwoD ? UnityEngine.Random.Range(-half.y, half.y) : UnityEngine.Random.Range(-half.y, half.y));
        return dimension == Dimension.TwoD ? new Vector3(p.x, p.y, 0f) : p;
    }

    Vector3 RandomDirection()
    {
        Vector2 d = UnityEngine.Random.insideUnitCircle.normalized;
        return dimension == Dimension.TwoD ? new Vector3(d.x, d.y, 0f) : new Vector3(d.x, 0f, d.y);
    }

    Vector3 ClampToArea(Vector3 p)
    {
        Vector3 c = areaCenter != null ? areaCenter.position : Vector3.zero;
        Vector2 half = areaSize * 0.5f;
        if (dimension == Dimension.TwoD)
            return new Vector3(Mathf.Clamp(p.x, c.x - half.x, c.x + half.x), Mathf.Clamp(p.y, c.y - half.y, c.y + half.y), p.z);
        return new Vector3(Mathf.Clamp(p.x, c.x - half.x, c.x + half.x), p.y, Mathf.Clamp(p.z, c.z - half.y, c.z + half.y));
    }

    // ---------------- STATS UI ----------------

    void OnGUI()
    {
        if (!autoGUIStats) return;
        GUILayout.BeginArea(new Rect(Screen.width - 230f, 10f, 220f, 130f), GUI.skin.box);
        GUILayout.Label($"Plants:     {PlantCount}");
        GUILayout.Label($"Herbivores: {HerbivoreCount}");
        GUILayout.Label($"Predators:  {PredatorCount}");
        GUILayout.Label($"Max gen:    {Generation}");
        GUILayout.Label($"Total:      {creatures.Count}/{maxCreatures}");
        GUILayout.EndArea();
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || areaCenter == null) return;
        Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
        Vector3 c = areaCenter.position;
        if (dimension == Dimension.TwoD)
            Gizmos.DrawWireCube(new Vector3(c.x, c.y, 0f), new Vector3(areaSize.x, areaSize.y, 0.5f));
        else
            Gizmos.DrawWireCube(new Vector3(c.x, c.y, c.z), new Vector3(areaSize.x, 1f, areaSize.y));
    }
}
