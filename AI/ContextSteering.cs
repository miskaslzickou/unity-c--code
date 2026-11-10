using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Spatial;

namespace GameSystems.AI
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO MODULÁRNÍ VYHODNOCOVAČE (EVALUATORS)
    // =========================================================================

    /// <summary>
    /// Rozhraní pro rozšiřitelné kontextové vyhodnocovače chování (Seek, Flee, Obstacle Avoidance, Cohesion).
    /// </summary>
    public interface IContextEvaluator
    {
        /// <summary>
        /// Vyhodnotí a zapíše váhy do map zájmu (interest) a nebezpečí (danger).
        /// </summary>
        void Evaluate(ContextSteering agent, float[] interestMap, float[] dangerMap, Vector3[] directions);
    }

    /// <summary>
    /// Počet diskrétních paprsků v kontextové mapě (8, 16 nebo 32 směrů).
    /// </summary>
    public enum ContextResolution
    {
        Low_8 = 8,
        Medium_16 = 16,
        High_32 = 32
    }

    // =========================================================================
    // 2. HLAVNÍ KOMPONENTA CONTEXT STEERING
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný systém lokálního vyhýbání překážkám a sledování cílů (Context Steering).
    /// Pracuje s diskrétními mapami zájmu (Interest) a nebezpečí (Danger).
    /// Zero-GC v hot-path, plně kompatibilní se SpatialHashGrid, FlowFieldPathfinder a FormationBrain.
    /// </summary>
    [DisallowMultipleComponent]
    public class ContextSteering : MonoBehaviour
    {
        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při každém výpočtu nového vektoru pohybu (výsledný vyhlazený směr).</summary>
        public event Action<Vector3> OnSteeringComputed;

        /// <summary>Vyvoláno, pokud agent narazí na situaci, kdy jsou všechny směry zablokovány nebezpečím.</summary>
        public event Action OnSteeringTrapped;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Rozlišení a geometrie")]
        [SerializeField] private ContextResolution resolution = ContextResolution.Medium_16;
        [SerializeField, Tooltip("Zda se agent pohybuje v XZ rovině (3D RTS/strategie) nebo XY rovině (2D)")]
        private bool isPlanarXZ = true;

        [Header("Vyhýbání překážkám (Physics & SpatialHash)")]
        [SerializeField, Range(0.5f, 15f)] private float obstacleDetectionRadius = 3f;
        [SerializeField] private LayerMask obstacleLayerMask = ~0;
        [SerializeField, Range(0.1f, 1f)] private float dangerThreshold = 0.75f;
        [SerializeField] private bool querySpatialHashGrid = true;

        [Header("Pohybové parametry")]
        [SerializeField, Range(0.5f, 20f)] private float maxSpeed = 5f;
        [SerializeField, Range(1f, 30f)] private float turnSmoothRate = 12f;
        [SerializeField, Range(0.1f, 2f)] private float agentRadius = 0.5f;

        [Header("Váhy chování")]
        [SerializeField, Range(0f, 5f)] private float seekTargetWeight = 2.0f;
        [SerializeField, Range(0f, 5f)] private float fleeThreatWeight = 3.0f;
        [SerializeField, Range(0f, 5f)] private float obstacleAvoidanceWeight = 4.0f;
        [SerializeField, Range(0f, 5f)] private float alignmentWeight = 1.0f;

        [Header("Cíle a hrozby")]
        [SerializeField] private Transform targetTransform;
        [SerializeField] private Transform threatTransform;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField, Range(0.5f, 5f)] private float gizmoScale = 2f;

        // =========================================================================
        // PAMĚŤOVÉ BUFFERY (ZERO-GC)
        // =========================================================================

        private int rayCount;
        private Vector3[] rayDirections;
        private float[] interestMap;
        private float[] dangerMap;

        // Pre-alokované pole pro raycasty (zero-GC)
        private readonly RaycastHit[] raycastHits = new RaycastHit[1];
        private readonly List<Transform> spatialNeighborsBuffer = new List<Transform>(32);

        // Seznam uživatelských / modulárních evaluátorů
        private readonly List<IContextEvaluator> evaluators = new List<IContextEvaluator>(4);

        // Výsledný stav řízení
        private Vector3 currentChosenDirection = Vector3.forward;
        private Vector3 currentVelocity = Vector3.zero;
        private bool isTrapped = false;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        public Vector3 ChosenDirection => currentChosenDirection;
        public Vector3 SteeringVelocity => currentVelocity;
        public float MaxSpeed => maxSpeed;
        public float AgentRadius => agentRadius;
        public bool IsTrapped => isTrapped;

        public Transform Target
        {
            get => targetTransform;
            set => targetTransform = value;
        }

        public Transform Threat
        {
            get => threatTransform;
            set => threatTransform = value;
        }

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            InitializeRayBuffers();
        }

        private void Update()
        {
            ComputeSteering(Time.deltaTime);
        }

        // =========================================================================
        // INICIALIZACE DISKRÉTNÍCH PAPRSKŮ
        // =========================================================================

        private void InitializeRayBuffers()
        {
            rayCount = (int)resolution;
            rayDirections = new Vector3[rayCount];
            interestMap = new float[rayCount];
            dangerMap = new float[rayCount];

            float angleStep = (Mathf.PI * 2f) / rayCount;

            for (int i = 0; i < rayCount; i++)
            {
                float angle = i * angleStep;
                if (isPlanarXZ)
                {
                    rayDirections[i] = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)).normalized;
                }
                else
                {
                    rayDirections[i] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f).normalized;
                }
            }
        }

        // =========================================================================
        // JÁDRO CONTEXT STEERING ALGORITMU
        // =========================================================================

        /// <summary>
        /// Vypočítá optimální směr a rychlost vyhýbání v aktuálním snímku.
        /// </summary>
        public Vector3 ComputeSteering(float deltaTime)
        {
            // 1. Reset map do nuly (zero GC)
            Array.Clear(interestMap, 0, rayCount);
            Array.Clear(dangerMap, 0, rayCount);

            Vector3 currentPos = transform.position;

            // 2. Evaluace: Cíl (Seek)
            if (targetTransform != null)
            {
                Vector3 toTarget = (targetTransform.position - currentPos);
                if (isPlanarXZ) toTarget.y = 0f; else toTarget.z = 0f;
                Vector3 targetDir = toTarget.normalized;

                for (int i = 0; i < rayCount; i++)
                {
                    float dot = Vector3.Dot(rayDirections[i], targetDir);
                    if (dot > 0f)
                    {
                        interestMap[i] += dot * seekTargetWeight;
                    }
                }
            }

            // 3. Evaluace: Hrozba (Flee)
            if (threatTransform != null)
            {
                Vector3 fromThreat = (currentPos - threatTransform.position);
                if (isPlanarXZ) fromThreat.y = 0f; else fromThreat.z = 0f;
                float threatDist = fromThreat.magnitude;

                if (threatDist < obstacleDetectionRadius * 2f && threatDist > 0.001f)
                {
                    Vector3 threatDir = -fromThreat.normalized; // Směr k nebezpečí
                    float intensity = Mathf.Clamp01(1f - (threatDist / (obstacleDetectionRadius * 2f)));

                    for (int i = 0; i < rayCount; i++)
                    {
                        float dot = Vector3.Dot(rayDirections[i], threatDir);
                        if (dot > 0f)
                        {
                            dangerMap[i] += dot * intensity * fleeThreatWeight;
                        }
                    }
                }
            }

            // 4. Evaluace: Překážky (Obstacle Avoidance přes Raycasty)
            EvaluateObstaclesPhysics(currentPos);

            // 5. Evaluace: Sousední agenti (přes SpatialHashGrid, pokud je k dispozici)
            if (querySpatialHashGrid && SpatialHashGridManager.Instance != null)
            {
                EvaluateSpatialNeighbors(currentPos);
            }

            // 6. Evaluace: Dodatečné uživatelské evaluátory
            for (int i = 0; i < evaluators.Count; i++)
            {
                evaluators[i].Evaluate(this, interestMap, dangerMap, rayDirections);
            }

            // 7. Maskování a odečítání nebezpečí od zájmu
            Vector3 finalTargetDir = Vector3.zero;
            float maxFilteredInterest = 0f;
            int bestRayIndex = -1;

            for (int i = 0; i < rayCount; i++)
            {
                float danger = dangerMap[i];

                if (danger >= dangerThreshold)
                {
                    interestMap[i] = 0f; // Směr je zcela zablokován
                }
                else
                {
                    interestMap[i] = Mathf.Clamp01(interestMap[i] - danger);
                }

                if (interestMap[i] > maxFilteredInterest)
                {
                    maxFilteredInterest = interestMap[i];
                    bestRayIndex = i;
                }
            }

            // 8. Interpolace mezi sousedními paprsky (Sub-angle smoothing)
            if (bestRayIndex != -1 && maxFilteredInterest > 0.001f)
            {
                isTrapped = false;

                // Vyhlazený průměr kolem nejlepšího paprsku a jeho sousedů
                int prev = (bestRayIndex - 1 + rayCount) % rayCount;
                int next = (bestRayIndex + 1) % rayCount;

                Vector3 blendedDir = rayDirections[bestRayIndex] * interestMap[bestRayIndex] +
                                     rayDirections[prev] * interestMap[prev] +
                                     rayDirections[next] * interestMap[next];

                if (blendedDir.sqrMagnitude > 0.001f)
                {
                    finalTargetDir = blendedDir.normalized;
                }
                else
                {
                    finalTargetDir = rayDirections[bestRayIndex];
                }
            }
            else
            {
                // Pokud jsou všechny směry nebezpečné
                isTrapped = true;
                OnSteeringTrapped?.Invoke();
                finalTargetDir = Vector3.zero;
            }

            // 9. Plynulé vyhlazení výsledné rychlosti
            if (finalTargetDir != Vector3.zero)
            {
                currentChosenDirection = Vector3.Slerp(currentChosenDirection, finalTargetDir, deltaTime * turnSmoothRate);
                currentVelocity = currentChosenDirection * (maxSpeed * Mathf.Clamp01(maxFilteredInterest));
            }
            else
            {
                currentVelocity = Vector3.MoveTowards(currentVelocity, Vector3.zero, deltaTime * turnSmoothRate * maxSpeed);
            }

            OnSteeringComputed?.Invoke(currentVelocity);
            return currentVelocity;
        }

        // =========================================================================
        // VYHODNOCENÍ PŘEKÁŽEK
        // =========================================================================

        private void EvaluateObstaclesPhysics(Vector3 origin)
        {
            for (int i = 0; i < rayCount; i++)
            {
                Vector3 dir = rayDirections[i];
                float checkDist = obstacleDetectionRadius;

                // Fyzikální SphereCast/Raycast bez alokace paměti
                int hitCount = agentRadius > 0.05f
                    ? Physics.SphereCastNonAlloc(origin, agentRadius, dir, raycastHits, checkDist, obstacleLayerMask, QueryTriggerInteraction.Ignore)
                    : Physics.RaycastNonAlloc(origin, dir, raycastHits, checkDist, obstacleLayerMask, QueryTriggerInteraction.Ignore);

                if (hitCount > 0)
                {
                    RaycastHit hit = raycastHits[0];
                    float dist = hit.distance;
                    float dangerVal = Mathf.Clamp01(1f - (dist / checkDist)) * obstacleAvoidanceWeight;

                    dangerMap[i] = Mathf.Max(dangerMap[i], dangerVal);

                    // Rozšíření nebezpečí na sousední paprsky (bezpečnostní okraj pro poloměr agenta)
                    int prev = (i - 1 + rayCount) % rayCount;
                    int next = (i + 1) % rayCount;
                    dangerMap[prev] = Mathf.Max(dangerMap[prev], dangerVal * 0.5f);
                    dangerMap[next] = Mathf.Max(dangerMap[next], dangerVal * 0.5f);
                }
            }
        }

        private void EvaluateSpatialNeighbors(Vector3 origin)
        {
            var grid = SpatialHashGridManager.Instance.TransformGrid;
            if (grid == null) return;

            grid.QueryRadius(origin, obstacleDetectionRadius, spatialNeighborsBuffer, (t) => t != transform);
            int count = spatialNeighborsBuffer.Count;

            for (int n = 0; n < count; n++)
            {
                Transform neighbor = spatialNeighborsBuffer[n];
                if (neighbor == null) continue;

                Vector3 toNeighbor = neighbor.position - origin;
                if (isPlanarXZ) toNeighbor.y = 0f; else toNeighbor.z = 0f;

                float dist = toNeighbor.magnitude;
                if (dist > 0.001f && dist < obstacleDetectionRadius)
                {
                    Vector3 dir = toNeighbor.normalized;
                    float dangerIntensity = Mathf.Clamp01(1f - (dist / obstacleDetectionRadius)) * alignmentWeight;

                    for (int i = 0; i < rayCount; i++)
                    {
                        float dot = Vector3.Dot(rayDirections[i], dir);
                        if (dot > 0.5f)
                        {
                            dangerMap[i] = Mathf.Max(dangerMap[i], dot * dangerIntensity);
                        }
                    }
                }
            }
        }

        // =========================================================================
        // VEŘEJNÉ API PRO ROZŠÍŘENÍ A EXTERNÍ ŘÍZENÍ
        // =========================================================================

        /// <summary>
        /// Nastaví externí cíl jako vektor světové pozice (např. z FlowFieldPathfinder).
        /// </summary>
        public void SetTargetPosition(Vector3 worldPos)
        {
            if (targetTransform == null)
            {
                GameObject dummy = new GameObject($"ContextTarget_{gameObject.name}");
                dummy.transform.position = worldPos;
                targetTransform = dummy.transform;
            }
            else
            {
                targetTransform.position = worldPos;
            }
        }

        /// <summary>
        /// Nastaví přímo požadovaný směrový vektor (např. z FlowFieldPathfinder.SampleFlowDirection).
        /// </summary>
        public void SetDirectSeekDirection(Vector3 direction, float weightMultiplier = 1f)
        {
            if (direction.sqrMagnitude < 0.001f) return;
            Vector3 norm = direction.normalized;

            for (int i = 0; i < rayCount; i++)
            {
                float dot = Vector3.Dot(rayDirections[i], norm);
                if (dot > 0f)
                {
                    interestMap[i] += dot * seekTargetWeight * weightMultiplier;
                }
            }
        }

        /// <summary>
        /// Přidá modulární vlastní evaluátor (např. pro formace nebo speciální taktiku).
        /// </summary>
        public void AddEvaluator(IContextEvaluator evaluator)
        {
            if (evaluator != null && !evaluators.Contains(evaluator))
            {
                evaluators.Add(evaluator);
            }
        }

        public void RemoveEvaluator(IContextEvaluator evaluator)
        {
            if (evaluator != null)
            {
                evaluators.Remove(evaluator);
            }
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos || rayDirections == null) return;

            Vector3 pos = transform.position;

            // Zobrazení paprsků zájmu (Zelená) a nebezpečí (Červená)
            for (int i = 0; i < rayCount; i++)
            {
                Vector3 dir = rayDirections[i];
                float interest = interestMap != null && i < interestMap.Length ? interestMap[i] : 0f;
                float danger = dangerMap != null && i < dangerMap.Length ? dangerMap[i] : 0f;

                // Nebezpečí - červené paprsky
                if (danger > 0.01f)
                {
                    Gizmos.color = new Color(1f, 0f, 0f, Mathf.Clamp01(danger));
                    Gizmos.DrawRay(pos, dir * (danger * gizmoScale));
                }

                // Zájem - zelené paprsky
                if (interest > 0.01f)
                {
                    Gizmos.color = new Color(0f, 1f, 0.2f, Mathf.Clamp01(interest));
                    Gizmos.DrawRay(pos, dir * (interest * gizmoScale));
                }
            }

            // Výsledný zvolený vyhlazený vektor (Žlutá šipka)
            if (currentChosenDirection.sqrMagnitude > 0.01f)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(pos, currentChosenDirection * (gizmoScale * 1.2f));
                Gizmos.DrawWireSphere(pos + currentChosenDirection * (gizmoScale * 1.2f), 0.15f);
            }

            // Dosah detekce překážek
            Gizmos.color = new Color(1f, 1f, 1f, 0.2f);
            Gizmos.DrawWireSphere(pos, obstacleDetectionRadius);
        }
    }
}
