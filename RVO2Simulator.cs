using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;

namespace GameSystems.Crowd
{
    // =========================================================================
    // 1. DATOVÉ STRUKTURY PRO RVO2 AGENTY A STATICKÉ PŘEKÁŽKY
    // =========================================================================

    /// <summary>
    /// Data jednotlivého agenta v davové simulaci RVO2 / ORCA.
    /// Optimalizováno pro nulovou alokaci paměti (zero-GC).
    /// </summary>
    [Serializable]
    public struct RVOAgentData
    {
        public int Id;
        public Vector3 Position;
        public Vector3 CurrentVelocity;
        public Vector3 PreferredVelocity;
        public Vector3 ComputedVelocity;

        public float Radius;
        public float MaxSpeed;
        public float TimeHorizon;           // Časový horizont předvídání kolizí (v sekundách)
        public float ResponsibilityWeight;  // Podíl odpovědnosti (0.5 = 50/50, 1.0 = plně se vyhýbá, 0.0 = nepohyblivý)
        public bool IsStationary;
        public bool IsActive;

        public object UserData;             // Volitelná reference na Transform, GameObject nebo agenta
    }

    /// <summary>
    /// Úsečková statická překážka (zeď, budova, plot) v RVO2 prostoru.
    /// </summary>
    [Serializable]
    public struct RVOObstacleSegment
    {
        public Vector3 PointA;
        public Vector3 PointB;
        public Vector3 Direction;
        public float Length;

        public RVOObstacleSegment(Vector3 a, Vector3 b)
        {
            PointA = a;
            PointB = b;
            Vector3 diff = b - a;
            Length = diff.magnitude;
            Direction = Length > 0.0001f ? diff / Length : Vector3.forward;
        }
    }

    /// <summary>
    /// Polorovina omezující povolené rychlosti (ORCA linear constraint line).
    /// </summary>
    public struct ORCALine
    {
        public Vector2 Point;     // Bod na přímce
        public Vector2 Direction; // Směr přímky (dovolená polorovina je po levé straně)
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER RVO2 SIMULATOR
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný davový simulátor založený na principech Reciprocal Velocity Obstacles (RVO2 / ORCA).
    /// Zabraňuje zasekávání stovek až tisíců jednotek v úzkých koridorech, dveřích a při protisměrném pohybu.
    /// Každý agent přebírá reciproční podíl odpovědnosti za odvrácení srážky.
    /// </summary>
    [DisallowMultipleComponent]
    public class RVO2Simulator : MonoBehaviour, ITickable
    {
        public static RVO2Simulator Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno po dokončení kroku simulace s novou vypočtenou rychlostí agenta.</summary>
        public event Action<int, Vector3> OnAgentVelocityComputed;

        /// <summary>Vyvoláno po dokončení celého simulačního kroku davu.</summary>
        public event Action OnSimulationStepCompleted;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Výchozí parametry agentů")]
        [SerializeField, Range(0.1f, 3f)] private float defaultRadius = 0.5f;
        [SerializeField, Range(0.5f, 15f)] private float defaultMaxSpeed = 4f;
        [SerializeField, Range(0.5f, 5f)] private float defaultTimeHorizon = 2f;
        [SerializeField, Range(0.1f, 1f)] private float defaultResponsibility = 0.5f;

        [Header("Davové sousedství")]
        [SerializeField, Range(1f, 20f)] private float maxNeighborDistance = 8f;
        [SerializeField, Range(5, 50)] private int maxNeighborsPerAgent = 15;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private bool drawVelocityVectors = true;
        [SerializeField] private bool drawObstacles = true;

        // =========================================================================
        // VNITŘNÍ STRUKTURY A ZERO-GC BUFFERY
        // =========================================================================

        private readonly List<RVOAgentData> agents = new List<RVOAgentData>(256);
        private readonly List<RVOObstacleSegment> obstacles = new List<RVOObstacleSegment>(64);

        // Pre-alokované zásobníky pro výpočet jednoho agenta bez alokací na haldě
        private readonly List<int> neighborIndicesBuffer = new List<int>(64);
        private readonly List<ORCALine> orcaLinesBuffer = new List<ORCALine>(64);

        private int nextAgentId = 1;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public int ActiveAgentCount => agents.Count;
        public int ObstacleCount => obstacles.Count;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
            }
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE (DETERMINISTICKÝ KROK SIMULACE)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            StepSimulation(tickDeltaTime);
        }

        // =========================================================================
        // JÁDRO RVO2 / ORCA VÝPOČTU (ZERO-GC HOT-PATH)
        // =========================================================================

        /// <summary>
        /// Provede jeden simulační krok davu: najde sousedy, sestaví ORCA omezení a vyřeší kolizně bezpečné rychlosti.
        /// </summary>
        public void StepSimulation(float deltaTime)
        {
            int agentCount = agents.Count;
            if (agentCount == 0) return;

            // 1. Fáze: Výpočet nových rychlostí pro všechny aktivní agenty
            for (int i = 0; i < agentCount; i++)
            {
                RVOAgentData agent = agents[i];
                if (!agent.IsActive || agent.IsStationary) continue;

                // Nalezení nejbližších sousedů
                FindNeighbors(i, agent.Position, maxNeighborDistance, maxNeighborsPerAgent, neighborIndicesBuffer);

                // Vygenerování ORCA polorovin z překážek i sousedních agentů
                orcaLinesBuffer.Clear();

                // Přidání statických překážek
                GenerateObstacleConstraints(agent, orcaLinesBuffer);

                // Přidání sousedních agentů
                int neighborCount = neighborIndicesBuffer.Count;
                for (int n = 0; n < neighborCount; n++)
                {
                    int neighborIdx = neighborIndicesBuffer[n];
                    RVOAgentData other = agents[neighborIdx];
                    AddAgentORCAConstraint(agent, other, orcaLinesBuffer);
                }

                // Lineární programování: Nalezení nejbližší rychlosti k PreferredVelocity splňující ORCA linie
                Vector2 prefVel2D = new Vector2(agent.PreferredVelocity.x, agent.PreferredVelocity.z);
                Vector2 computedVel2D = SolveLinearProgram(orcaLinesBuffer, agent.MaxSpeed, prefVel2D);

                agent.ComputedVelocity = new Vector3(computedVel2D.x, 0f, computedVel2D.y);
                agents[i] = agent;

                OnAgentVelocityComputed?.Invoke(agent.Id, agent.ComputedVelocity);
            }

            // 2. Fáze: Aplikace rychlostí a integrace pozice
            for (int i = 0; i < agentCount; i++)
            {
                RVOAgentData agent = agents[i];
                if (!agent.IsActive || agent.IsStationary) continue;

                agent.CurrentVelocity = agent.ComputedVelocity;
                agent.Position += agent.CurrentVelocity * deltaTime;
                agents[i] = agent;
            }

            OnSimulationStepCompleted?.Invoke();
        }

        // =========================================================================
        // NALÉZÁNÍ SOUSEDŮ (PROSTOROVÉ VYHLEDÁVÁNÍ)
        // =========================================================================

        private void FindNeighbors(int agentIndex, Vector3 pos, float range, int maxCount, List<int> results)
        {
            results.Clear();
            float rangeSqr = range * range;
            int total = agents.Count;

            for (int i = 0; i < total; i++)
            {
                if (i == agentIndex) continue;
                RVOAgentData other = agents[i];
                if (!other.IsActive) continue;

                float dx = other.Position.x - pos.x;
                float dz = other.Position.z - pos.z;
                float distSqr = dx * dx + dz * dz;

                if (distSqr < rangeSqr)
                {
                    results.Add(i);
                    if (results.Count >= maxCount) break;
                }
            }
        }

        // =========================================================================
        // SESTAVENÍ ORCA OMEZENÍ (AGENT VS AGENT)
        // =========================================================================

        private void AddAgentORCAConstraint(RVOAgentData a, RVOAgentData b, List<ORCALine> lines)
        {
            Vector2 relPos = new Vector2(b.Position.x - a.Position.x, b.Position.z - a.Position.z);
            Vector2 relVel = new Vector2(a.CurrentVelocity.x - b.CurrentVelocity.x, a.CurrentVelocity.z - b.CurrentVelocity.z);

            float distSqr = relPos.sqrMagnitude;
            float combinedRadius = a.Radius + b.Radius;
            float combinedRadiusSqr = combinedRadius * combinedRadius;

            Vector2 u;
            Vector2 direction;

            float invTimeHorizon = 1f / Mathf.Max(0.01f, a.TimeHorizon);

            if (distSqr > combinedRadiusSqr)
            {
                // Agenti zatím nekolidují – předpověď kolize v čase TimeHorizon
                Vector2 w = relVel - invTimeHorizon * relPos;
                float wLengthSqr = w.sqrMagnitude;
                float dot = Vector2.Dot(w, relPos);

                if (dot < 0f && dot * dot > combinedRadiusSqr * wLengthSqr)
                {
                    // Promítnutí na špičku omezujícího kuželu
                    float wLength = Mathf.Sqrt(wLengthSqr);
                    Vector2 unitW = w / wLength;
                    direction = new Vector2(unitW.y, -unitW.x);
                    u = (combinedRadius * invTimeHorizon - wLength) * unitW;
                }
                else
                {
                    // Promítnutí na ramena omezujícího kuželu
                    float leg = Mathf.Sqrt(Mathf.Max(0f, distSqr - combinedRadiusSqr));
                    if (Determinant(relPos, w) > 0f)
                    {
                        direction = new Vector2(relPos.x * leg - relPos.y * combinedRadius, relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }
                    else
                    {
                        direction = -new Vector2(relPos.x * leg + relPos.y * combinedRadius, -relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }

                    float dotW = Vector2.Dot(relVel, direction);
                    u = dotW * direction - relVel;
                }
            }
            else
            {
                // Agenti již kolidují – urgentní odtlačení v čase 1/dt
                float invTimeStep = 5f; // Přísné odtlačení
                Vector2 w = relVel - invTimeStep * relPos;
                float wLength = w.magnitude;
                Vector2 unitW = wLength > 0.0001f ? w / wLength : Vector2.up;

                direction = new Vector2(unitW.y, -unitW.x);
                u = (combinedRadius * invTimeStep - wLength) * unitW;
            }

            // Rozdělení odpovědnosti mezi oba agenty (ResponsibilityWeight)
            float weight = a.ResponsibilityWeight;
            if (b.IsStationary) weight = 1f; // Do statických agentů se narážet nesmí

            Vector2 currentVelA = new Vector2(a.CurrentVelocity.x, a.CurrentVelocity.z);
            Vector2 linePoint = currentVelA + weight * u;

            lines.Add(new ORCALine { Point = linePoint, Direction = direction });
        }

        // =========================================================================
        // STATICKÉ PŘEKÁŽKY (OBSTACLE CONSTRAINTS)
        // =========================================================================

        private void GenerateObstacleConstraints(RVOAgentData agent, List<ORCALine> lines)
        {
            Vector2 agentPos = new Vector2(agent.Position.x, agent.Position.z);
            Vector2 agentVel = new Vector2(agent.CurrentVelocity.x, agent.CurrentVelocity.z);
            int count = obstacles.Count;

            for (int i = 0; i < count; i++)
            {
                RVOObstacleSegment obs = obstacles[i];
                Vector2 p1 = new Vector2(obs.PointA.x, obs.PointA.z);
                Vector2 p2 = new Vector2(obs.PointB.x, obs.PointB.z);

                Vector2 seg = p2 - p1;
                float segLenSqr = seg.sqrMagnitude;
                if (segLenSqr < 0.0001f) continue;

                // Nejbližší bod úsečky k agentovi
                float t = Mathf.Clamp01(Vector2.Dot(agentPos - p1, seg) / segLenSqr);
                Vector2 closest = p1 + t * seg;
                Vector2 toAgent = agentPos - closest;
                float distSqr = toAgent.sqrMagnitude;

                float safeDist = agent.Radius + 0.2f;
                if (distSqr < safeDist * safeDist)
                {
                    // Příliš blízko překážce – vytvoří se pevná odmítavá polorovina
                    float dist = Mathf.Sqrt(distSqr);
                    Vector2 normal = dist > 0.001f ? toAgent / dist : new Vector2(-seg.y, seg.x).normalized;

                    lines.Add(new ORCALine
                    {
                        Point = normal * (safeDist - dist) * 5f,
                        Direction = new Vector2(normal.y, -normal.x)
                    });
                }
            }
        }

        // =========================================================================
        // LINEÁRNÍ PROGRAMOVÁNÍ PRO ŘEŠENÍ ORCA POLOROVIN
        // =========================================================================

        private Vector2 SolveLinearProgram(List<ORCALine> lines, float maxSpeed, Vector2 prefVel)
        {
            Vector2 result = prefVel;
            if (result.sqrMagnitude > maxSpeed * maxSpeed)
            {
                result = result.normalized * maxSpeed;
            }

            int lineCount = lines.Count;
            for (int i = 0; i < lineCount; i++)
            {
                ORCALine line = lines[i];

                // Pokud bod leží v zakázané polorovině
                if (Determinant(line.Direction, line.Point - result) > 0f)
                {
                    // Promítnutí výsledku na hranici poloroviny
                    Vector2 tempResult = result;
                    if (!SolveLineConstraint(lines, i, maxSpeed, prefVel, ref result))
                    {
                        // Pokud není možné splnit všechna omezení, zachová se alespoň předchozí nejlepší projekce
                        result = tempResult;
                    }
                }
            }

            return result;
        }

        private bool SolveLineConstraint(List<ORCALine> lines, int lineIndex, float maxSpeed, Vector2 prefVel, ref Vector2 result)
        {
            ORCALine line = lines[lineIndex];

            float dot = Vector2.Dot(line.Point, line.Direction);
            float discriminant = dot * dot + maxSpeed * maxSpeed - line.Point.sqrMagnitude;

            if (discriminant < 0f)
            {
                // Kruh maximální rychlosti neprotíná tuto přímku
                return false;
            }

            float sqrtDisc = Mathf.Sqrt(discriminant);
            float tLeft = -dot - sqrtDisc;
            float tRight = -dot + sqrtDisc;

            for (int i = 0; i < lineIndex; i++)
            {
                ORCALine prevLine = lines[i];
                float denominator = Determinant(line.Direction, prevLine.Direction);
                float numerator = Determinant(prevLine.Direction, line.Point - prevLine.Point);

                if (Mathf.Abs(denominator) < 0.0001f)
                {
                    // Přímky jsou rovnoběžné
                    if (numerator > 0f) return false;
                    continue;
                }

                float t = numerator / denominator;
                if (denominator > 0f)
                {
                    tRight = Mathf.Min(tRight, t);
                }
                else
                {
                    tLeft = Mathf.Max(tLeft, t);
                }

                if (tLeft > tRight) return false;
            }

            // Výběr t nejbližšího preferované rychlosti
            float tPref = Vector2.Dot(line.Direction, prefVel - line.Point);
            float tFinal = Mathf.Clamp(tPref, tLeft, tRight);
            result = line.Point + tFinal * line.Direction;
            return true;
        }

        private static float Determinant(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // =========================================================================
        // VEŘEJNÉ API PRO REGISTRACI A OVLÁDÁNÍ AGENTŮ
        // =========================================================================

        /// <summary>
        /// Zaregistruje nového agenta do davové simulace a vrátí jeho unikátní ID.
        /// </summary>
        public int RegisterAgent(Vector3 position, float radius = -1f, float maxSpeed = -1f, float timeHorizon = -1f, float responsibility = -1f, object userData = null)
        {
            int id = nextAgentId++;
            var agent = new RVOAgentData
            {
                Id = id,
                Position = position,
                CurrentVelocity = Vector3.zero,
                PreferredVelocity = Vector3.zero,
                ComputedVelocity = Vector3.zero,
                Radius = radius > 0f ? radius : defaultRadius,
                MaxSpeed = maxSpeed > 0f ? maxSpeed : defaultMaxSpeed,
                TimeHorizon = timeHorizon > 0f ? timeHorizon : defaultTimeHorizon,
                ResponsibilityWeight = responsibility >= 0f ? responsibility : defaultResponsibility,
                IsStationary = false,
                IsActive = true,
                UserData = userData
            };

            agents.Add(agent);
            return id;
        }

        /// <summary>
        /// Odregistruje agenta ze simulace.
        /// </summary>
        public bool UnregisterAgent(int agentId)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i].Id == agentId)
                {
                    agents.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Nastaví preferovanou rychlost agenta (např. směr k cíli z FlowFieldPathfinder).
        /// </summary>
        public void SetAgentPreferredVelocity(int agentId, Vector3 preferredVelocity)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i].Id == agentId)
                {
                    RVOAgentData d = agents[i];
                    d.PreferredVelocity = preferredVelocity;
                    agents[i] = d;
                    return;
                }
            }
        }

        /// <summary>
        /// Aktualizuje skutečnou pozici agenta ve světě.
        /// </summary>
        public void SetAgentPosition(int agentId, Vector3 position)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i].Id == agentId)
                {
                    RVOAgentData d = agents[i];
                    d.Position = position;
                    agents[i] = d;
                    return;
                }
            }
        }

        /// <summary>
        /// Získá poslední vypočtenou bezpečnou rychlost pro zadaného agenta.
        /// </summary>
        public Vector3 GetAgentComputedVelocity(int agentId)
        {
            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i].Id == agentId)
                {
                    return agents[i].ComputedVelocity;
                }
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Přidá statickou úsečkovou překážku (zeď, plot, budovu).
        /// </summary>
        public void AddObstacleSegment(Vector3 pointA, Vector3 pointB)
        {
            obstacles.Add(new RVOObstacleSegment(pointA, pointB));
        }

        /// <summary>
        /// Přidá obdélníkovou překážku (např. budovu z GridPlacementSystem).
        /// </summary>
        public void AddObstacleBox(Vector3 center, Vector2 size)
        {
            float hx = size.x * 0.5f;
            float hz = size.y * 0.5f;

            Vector3 c1 = center + new Vector3(-hx, 0f, -hz);
            Vector3 c2 = center + new Vector3(hx, 0f, -hz);
            Vector3 c3 = center + new Vector3(hx, 0f, hz);
            Vector3 c4 = center + new Vector3(-hx, 0f, hz);

            AddObstacleSegment(c1, c2);
            AddObstacleSegment(c2, c3);
            AddObstacleSegment(c3, c4);
            AddObstacleSegment(c4, c1);
        }

        public void ClearObstacles()
        {
            obstacles.Clear();
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            // Vykreslení statických překážek
            if (drawObstacles)
            {
                Gizmos.color = Color.red;
                for (int i = 0; i < obstacles.Count; i++)
                {
                    RVOObstacleSegment obs = obstacles[i];
                    Gizmos.DrawLine(obs.PointA, obs.PointB);
                    Gizmos.DrawSphere(obs.PointA, 0.1f);
                    Gizmos.DrawSphere(obs.PointB, 0.1f);
                }
            }

            // Vykreslení agentů a jejich vektorů rychlosti
            int count = agents.Count;
            for (int i = 0; i < count; i++)
            {
                RVOAgentData agent = agents[i];
                if (!agent.IsActive) continue;

                Gizmos.color = agent.IsStationary ? Color.gray : Color.cyan;
                Gizmos.DrawWireSphere(agent.Position, agent.Radius);

                if (drawVelocityVectors && !agent.IsStationary)
                {
                    // Zelená = Preferred velocity, Žlutá = Computed velocity
                    Gizmos.color = Color.green;
                    Gizmos.DrawRay(agent.Position, agent.PreferredVelocity);

                    Gizmos.color = Color.yellow;
                    Gizmos.DrawRay(agent.Position, agent.ComputedVelocity);
                }
            }
        }
    }
}
