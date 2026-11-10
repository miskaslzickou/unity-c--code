using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Roads;
using GameSystems.Simulation;

namespace GameSystems.Traffic
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A STRUKTURY ŘÍZENÍ KŘIŽOVATEK
    // =========================================================================

    public enum IntersectionControlMode
    {
        PriorityRightOfWay, // Přednost v jízdě (hlavní silnice má přednost před vedlejší)
        StopSignFIFO,       // Stopka pro všechny (vozidla projíždějí v pořadí, jak zastavila)
        TimedTrafficLights  // Řízené semafory (Zelená -> Žlutá -> Červená fáze)
    }

    public enum TrafficLightSignal
    {
        Red,
        Yellow,
        Green
    }

    /// <summary>
    /// Fáze semaforu určující, které příjezdové segmenty mají zelenou současně.
    /// </summary>
    [Serializable]
    public class TrafficLightPhase
    {
        public string PhaseName;
        public List<int> ActiveSegmentIds = new List<int>(2);
        public float GreenDuration = 10f;
        public float YellowDuration = 3f;
    }

    /// <summary>
    /// Rezervace kolizní zóny uvnitř křižovatky konkrétním vozidlem.
    /// </summary>
    public struct IntersectionReservation
    {
        public int VehicleId;
        public int IncomingSegmentId;
        public int OutgoingSegmentId;
        public float ReservationTime;
    }

    // =========================================================================
    // 2. STAVOVÝ MANAŽER KŘIŽOVATKY (INTERSECTION CONTROLLER)
    // =========================================================================

    /// <summary>
    /// Řídící systém křižovatek napojený na SplineRoadBuilder.
    /// Spravuje semafory, stopky, kolizní zóny a anti-gridlock ochranu proti ucpání výjezdů.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrafficIntersectionController : MonoBehaviour, ITickable
    {
        public static TrafficIntersectionController Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při přepnutí barvy semaforu na konkrétním rameni křižovatky.</summary>
        public static event Action<int, int, TrafficLightSignal> OnSignalChanged; // nodeId, segmentId, newSignal

        /// <summary>Vyvoláno při vjezdu vozidla do kolizní zóny křižovatky.</summary>
        public static event Action<int, int> OnVehicleEnteredIntersection; // vehicleId, nodeId

        /// <summary>Vyvoláno po bezpečném opuštění křižovatky vozidlem.</summary>
        public static event Action<int, int> OnVehicleExitedIntersection; // vehicleId, nodeId

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Výchozí parametry semaforů")]
        [SerializeField, Range(3f, 30f)] private float defaultGreenDuration = 8f;
        [SerializeField, Range(1f, 5f)] private float defaultYellowDuration = 2.5f;
        [SerializeField, Range(0.5f, 4f)] private float allRedClearance = 1.0f;

        [Header("Anti-Gridlock ochrana")]
        [SerializeField, Tooltip("Minimální volný prostor na výjezdovém segmentu potřebný pro povolení vjezdu"), Range(3f, 20f)]
        private float minExitFreeSpaceRequired = 6.0f;

        [Header("Ladění")]
        [SerializeField] private bool drawGizmos = true;

        // =========================================================================
        // VNITŘNÍ STAVY KŘIŽOVATEK
        // =========================================================================

        public class IntersectionData
        {
            public RoadNode Node;
            public IntersectionControlMode Mode;
            public List<TrafficLightPhase> Phases = new List<TrafficLightPhase>(4);
            public int CurrentPhaseIndex;
            public float PhaseTimer;
            public TrafficLightSignal CurrentSignalState;

            // FIFO fronta vozidel čekajících na stopce
            public readonly Queue<int> StopSignFIFOQueue = new Queue<int>(16);

            // Aktivní rezervace kolizních tras uvnitř křižovatky
            public readonly List<IntersectionReservation> ActiveReservations = new List<IntersectionReservation>(4);
        }

        private readonly Dictionary<int, IntersectionData> intersections = new Dictionary<int, IntersectionData>(32);

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

            // Automatické napojení na tvorbu křižovatek ve SplineRoadBuilder
            SplineRoadBuilder.OnIntersectionCreated += HandleNewIntersectionCreated;
            DiscoverExistingIntersections();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            SplineRoadBuilder.OnIntersectionCreated -= HandleNewIntersectionCreated;

            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
            }
        }

        // =========================================================================
        // REGISTRACE A AUTOMATICKÁ DETEKCE KŘIŽOVATEK
        // =========================================================================

        public void DiscoverExistingIntersections()
        {
            if (SplineRoadBuilder.Instance == null) return;

            var nodes = SplineRoadBuilder.Instance.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].ConnectedSegments.Count >= 3)
                {
                    RegisterIntersection(nodes[i]);
                }
            }
        }

        private void HandleNewIntersectionCreated(RoadNode node)
        {
            RegisterIntersection(node);
        }

        public void RegisterIntersection(RoadNode node, IntersectionControlMode mode = IntersectionControlMode.TimedTrafficLights)
        {
            if (node == null || intersections.ContainsKey(node.Id)) return;

            var data = new IntersectionData
            {
                Node = node,
                Mode = mode,
                CurrentPhaseIndex = 0,
                PhaseTimer = 0f,
                CurrentSignalState = TrafficLightSignal.Green
            };

            // Automatické vygenerování fází semaforů pro připojené segmenty
            BuildDefaultPhases(data);
            intersections[node.Id] = data;
        }

        private void BuildDefaultPhases(IntersectionData data)
        {
            data.Phases.Clear();
            var segments = data.Node.ConnectedSegments;

            // Pro T-křižovatku nebo 4-cestnou křižovatku rozdělíme ramena do 2 protisměrných fází
            int segCount = segments.Count;
            if (segCount == 0) return;

            var phaseA = new TrafficLightPhase
            {
                PhaseName = "Fáze 1 (Sever-Jih / Hlavní)",
                GreenDuration = defaultGreenDuration,
                YellowDuration = defaultYellowDuration
            };
            phaseA.ActiveSegmentIds.Add(segments[0].Id);
            if (segCount > 1) phaseA.ActiveSegmentIds.Add(segments[1].Id);
            data.Phases.Add(phaseA);

            if (segCount > 2)
            {
                var phaseB = new TrafficLightPhase
                {
                    PhaseName = "Fáze 2 (Východ-Západ / Vedlejší)",
                    GreenDuration = defaultGreenDuration,
                    YellowDuration = defaultYellowDuration
                };
                for (int i = 2; i < segCount; i++)
                {
                    phaseB.ActiveSegmentIds.Add(segments[i].Id);
                }
                data.Phases.Add(phaseB);
            }
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE: BĚH CYKLŮ SEMAFORŮ A VYPRŠENÍ REZERVACÍ
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            UpdateAllIntersections(tickDeltaTime);
        }

        private void UpdateAllIntersections(float dt)
        {
            foreach (var kvp in intersections)
            {
                IntersectionData data = kvp.Value;
                if (data.Mode != IntersectionControlMode.TimedTrafficLights || data.Phases.Count == 0) continue;

                data.PhaseTimer += dt;
                TrafficLightPhase currentPhase = data.Phases[data.CurrentPhaseIndex];

                switch (data.CurrentSignalState)
                {
                    case TrafficLightSignal.Green:
                        if (data.PhaseTimer >= currentPhase.GreenDuration)
                        {
                            data.CurrentSignalState = TrafficLightSignal.Yellow;
                            data.PhaseTimer = 0f;
                            BroadcastPhaseSignals(data, TrafficLightSignal.Yellow);
                        }
                        break;

                    case TrafficLightSignal.Yellow:
                        if (data.PhaseTimer >= currentPhase.YellowDuration)
                        {
                            data.CurrentSignalState = TrafficLightSignal.Red;
                            data.PhaseTimer = 0f;
                            BroadcastPhaseSignals(data, TrafficLightSignal.Red);
                        }
                        break;

                    case TrafficLightSignal.Red:
                        // Vyčkání na vyklizení křižovatky (All-Red) před přepnutím další fáze
                        if (data.PhaseTimer >= allRedClearance)
                        {
                            data.CurrentPhaseIndex = (data.CurrentPhaseIndex + 1) % data.Phases.Count;
                            data.CurrentSignalState = TrafficLightSignal.Green;
                            data.PhaseTimer = 0f;
                            BroadcastPhaseSignals(data, TrafficLightSignal.Green);
                        }
                        break;
                }
            }
        }

        private void BroadcastPhaseSignals(IntersectionData data, TrafficLightSignal activeSignal)
        {
            TrafficLightPhase currentPhase = data.Phases[data.CurrentPhaseIndex];
            var segs = data.Node.ConnectedSegments;

            for (int i = 0; i < segs.Count; i++)
            {
                int segId = segs[i].Id;
                bool isPhaseActive = currentPhase.ActiveSegmentIds.Contains(segId);
                TrafficLightSignal signal = isPhaseActive ? activeSignal : TrafficLightSignal.Red;

                OnSignalChanged?.Invoke(data.Node.Id, segId, signal);
            }
        }

        // =========================================================================
        // KOLIZNÍ ZÓNA A ANTI-GRIDLOCK REZERVACE (PUBLIC API)
        // =========================================================================

        /// <summary>
        /// Zkontroluje, zda má vozidlo povoleno projet křižovatkou (zelená / volná stopka,
        /// žádná kolizní dráha a volné místo na výjezdu).
        /// </summary>
        public bool RequestIntersectionPassage(int vehicleId, int nodeId, RoadSegment inSeg, RoadSegment outSeg, float exitLaneOccupancy)
        {
            if (!intersections.TryGetValue(nodeId, out IntersectionData data))
            {
                return true; // Neřízený uzel – volný průjezd
            }

            // 1. Anti-Gridlock kontrola: Je na výjezdovém pruhu dostatek volného místa?
            if (exitLaneOccupancy < minExitFreeSpaceRequired)
            {
                return false; // Výjezd je ucpán kolonou – zákaz vjezdu do křižovatky!
            }

            // 2. Kontrola podle režimu řízení
            if (data.Mode == IntersectionControlMode.TimedTrafficLights)
            {
                TrafficLightSignal signal = GetSignalForSegment(data, inSeg.Id);
                if (signal != TrafficLightSignal.Green) return false;
            }
            else if (data.Mode == IntersectionControlMode.StopSignFIFO)
            {
                if (data.StopSignFIFOQueue.Count > 0 && data.StopSignFIFOQueue.Peek() != vehicleId)
                {
                    return false; // Jiné vozidlo přijelo dříve a má přednost
                }
            }

            // 3. Kontrola kolizních drah s jinými projíždějícími vozidly
            for (int i = 0; i < data.ActiveReservations.Count; i++)
            {
                IntersectionReservation res = data.ActiveReservations[i];
                if (res.VehicleId == vehicleId) continue;

                // Pokud se dráhy kříží (protínající se vjezd/výjezd)
                if (res.IncomingSegmentId != inSeg.Id && res.OutgoingSegmentId == outSeg.Id)
                {
                    return false;
                }
            }

            // Úspěšná rezervace
            data.ActiveReservations.Add(new IntersectionReservation
            {
                VehicleId = vehicleId,
                IncomingSegmentId = inSeg.Id,
                OutgoingSegmentId = outSeg.Id,
                ReservationTime = Time.time
            });

            if (data.Mode == IntersectionControlMode.StopSignFIFO && data.StopSignFIFOQueue.Count > 0 && data.StopSignFIFOQueue.Peek() == vehicleId)
            {
                data.StopSignFIFOQueue.Dequeue();
            }

            OnVehicleEnteredIntersection?.Invoke(vehicleId, nodeId);
            return true;
        }

        /// <summary>
        /// Oznámí, že vozidlo bezpečně opustilo křižovatku a uvolnilo kolizní zónu.
        /// </summary>
        public void ReleaseIntersectionPassage(int vehicleId, int nodeId)
        {
            if (!intersections.TryGetValue(nodeId, out IntersectionData data)) return;

            for (int i = data.ActiveReservations.Count - 1; i >= 0; i--)
            {
                if (data.ActiveReservations[i].VehicleId == vehicleId)
                {
                    data.ActiveReservations.RemoveAt(i);
                    OnVehicleExitedIntersection?.Invoke(vehicleId, nodeId);
                    break;
                }
            }
        }

        public void NotifyVehicleArrivedAtStopSign(int vehicleId, int nodeId)
        {
            if (intersections.TryGetValue(nodeId, out IntersectionData data))
            {
                if (!data.StopSignFIFOQueue.Contains(vehicleId))
                {
                    data.StopSignFIFOQueue.Enqueue(vehicleId);
                }
            }
        }

        public TrafficLightSignal GetSignalForSegment(int nodeId, int segmentId)
        {
            if (!intersections.TryGetValue(nodeId, out IntersectionData data)) return TrafficLightSignal.Green;
            return GetSignalForSegment(data, segmentId);
        }

        private TrafficLightSignal GetSignalForSegment(IntersectionData data, int segmentId)
        {
            if (data.Mode != IntersectionControlMode.TimedTrafficLights || data.Phases.Count == 0)
            {
                return TrafficLightSignal.Green;
            }

            TrafficLightPhase currentPhase = data.Phases[data.CurrentPhaseIndex];
            if (currentPhase.ActiveSegmentIds.Contains(segmentId))
            {
                return data.CurrentSignalState;
            }

            return TrafficLightSignal.Red;
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            foreach (var kvp in intersections)
            {
                IntersectionData data = kvp.Value;
                Vector3 nodePos = data.Node.Position;

                var segs = data.Node.ConnectedSegments;
                for (int i = 0; i < segs.Count; i++)
                {
                    RoadSegment seg = segs[i];
                    TrafficLightSignal sig = GetSignalForSegment(data, seg.Id);

                    switch (sig)
                    {
                        case TrafficLightSignal.Green: Gizmos.color = Color.green; break;
                        case TrafficLightSignal.Yellow: Gizmos.color = Color.yellow; break;
                        case TrafficLightSignal.Red: Gizmos.color = Color.red; break;
                    }

                    Vector3 dir = (seg.EvaluateCubicBezier(0.1f) - nodePos).normalized;
                    Vector3 lightPos = nodePos + dir * 1.5f + Vector3.up * 1.5f;
                    Gizmos.DrawSphere(lightPos, 0.35f);
                }
            }
        }
    }

    // =========================================================================
    // 3. INTELLIGENT DRIVER MODEL (TRAFFIC VEHICLE AGENT)
    // =========================================================================

    /// <summary>
    /// Lehký kontroler vozidla využívající model IDM (Intelligent Driver Model).
    /// Plynule zrychluje, dodržuje bezpečný odstup za vozidlem před sebou,
    /// plynule brzdí před semafory a stopkami a sleduje Bézierovu trasu.
    /// </summary>
    public class TrafficVehicleAgent : MonoBehaviour
    {
        [Header("IDM parametry (Intelligent Driver Model)")]
        [SerializeField, Range(5f, 35f)] private float desiredSpeed = 12f;
        [SerializeField, Range(0.5f, 3f)] private float safeTimeHeadway = 1.5f;
        [SerializeField, Range(1f, 5f)] private float minJamDistance = 2.0f;
        [SerializeField, Range(0.5f, 5f)] private float maxAcceleration = 2.5f;
        [SerializeField, Range(0.5f, 6f)] private float comfortableBraking = 3.0f;

        [Header("Trasa a stav")]
        [SerializeField] private float currentSpeed = 0f;
        [SerializeField] private float currentDistanceAlongSegment = 0f;

        private RoadSegment currentSegment;
        private RoadSegment nextPlannedSegment;
        private int vehicleId;
        private bool isInsideIntersection = false;

        public int VehicleId => vehicleId;
        public float CurrentSpeed => currentSpeed;
        public RoadSegment CurrentSegment => currentSegment;

        private void Awake()
        {
            vehicleId = GetInstanceID();
        }

        public void Initialize(RoadSegment startSegment, float startDistance = 0f)
        {
            currentSegment = startSegment;
            currentDistanceAlongSegment = startDistance;
            currentSpeed = desiredSpeed * 0.5f;
        }

        private void Update()
        {
            if (currentSegment == null) return;

            float dt = Time.deltaTime;
            StepVehiclePhysics(dt);
        }

        private void StepVehiclePhysics(float dt)
        {
            // 1. Vyhodnocení vzdálenosti k překážce / semaforu před vozidlem
            float distToStop = float.MaxValue;
            float deltaV = 0f;

            RoadNode endNode = currentSegment.EndNode;
            float distToEndOfSegment = currentSegment.TotalLength - currentDistanceAlongSegment;

            // Kontrola před křižovatkou
            if (!isInsideIntersection && distToEndOfSegment < 25f && endNode != null)
            {
                TrafficLightSignal signal = TrafficIntersectionController.Instance != null
                    ? TrafficIntersectionController.Instance.GetSignalForSegment(endNode.Id, currentSegment.Id)
                    : TrafficLightSignal.Green;

                if (signal == TrafficLightSignal.Red || signal == TrafficLightSignal.Yellow)
                {
                    distToStop = distToEndOfSegment;
                    deltaV = currentSpeed; // Cíl má nulovou rychlost (stojí na semaforu)
                }
                else if (signal == TrafficLightSignal.Green && distToEndOfSegment < 3f)
                {
                    // Požadavek na průjezd křižovatkou (Anti-Gridlock check)
                    bool canEnter = TrafficIntersectionController.Instance == null ||
                                    TrafficIntersectionController.Instance.RequestIntersectionPassage(vehicleId, endNode.Id, currentSegment, nextPlannedSegment, 20f);

                    if (canEnter)
                    {
                        isInsideIntersection = true;
                    }
                    else
                    {
                        distToStop = distToEndOfSegment;
                        deltaV = currentSpeed;
                    }
                }
            }

            // 2. IDM Rovnice zrychlení
            float sStar = minJamDistance + currentSpeed * safeTimeHeadway +
                          (currentSpeed * deltaV) / (2f * Mathf.Sqrt(maxAcceleration * comfortableBraking));

            float freeRoadTerm = 1f - Mathf.Pow(currentSpeed / Mathf.Max(0.1f, desiredSpeed), 4f);
            float interactionTerm = distToStop < 100f ? Mathf.Pow(sStar / Mathf.Max(0.1f, distToStop), 2f) : 0f;

            float acceleration = maxAcceleration * (freeRoadTerm - interactionTerm);

            // Integrace rychlosti a pozice
            currentSpeed = Mathf.Max(0f, currentSpeed + acceleration * dt);
            currentDistanceAlongSegment += currentSpeed * dt;

            // 3. Přechod na další segment po projetí křižovatky
            if (currentDistanceAlongSegment >= currentSegment.TotalLength)
            {
                if (isInsideIntersection && endNode != null && TrafficIntersectionController.Instance != null)
                {
                    TrafficIntersectionController.Instance.ReleaseIntersectionPassage(vehicleId, endNode.Id);
                    isInsideIntersection = false;
                }

                // Přepnutí na další segment
                if (nextPlannedSegment != null)
                {
                    currentSegment = nextPlannedSegment;
                    currentDistanceAlongSegment = 0f;
                    nextPlannedSegment = null;
                }
                else
                {
                    currentDistanceAlongSegment = currentSegment.TotalLength;
                    currentSpeed = 0f;
                }
            }

            // 4. Nastavení polohy a orientace podél křivky
            currentSegment.GetPointAtDistance(currentDistanceAlongSegment, out Vector3 worldPos, out Vector3 tangent);
            transform.position = worldPos;
            if (tangent.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(tangent);
            }
        }
    }
}
