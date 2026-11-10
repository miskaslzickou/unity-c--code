using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;

namespace GameSystems.Logistics
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ A DATOVÉ STRUKTURY LOGISTICKÉHO SYSTÉMU
    // =========================================================================

    public enum JobStatus
    {
        PendingAssignment, // Čeká ve frontě na volného pracovníka
        EnRouteToPickup,   // Pracovník jede pro náklad ke zdroji
        Loading,           // Nakládání surovin
        EnRouteToDelivery, // Převoz nákladu k příjemci
        Unloading,         // Vykládání
        Completed,         // Úspěšně doručeno
        Cancelled          // Zrušeno (např. zničení pracovníka nebo skladu)
    }

    /// <summary>
    /// Rozhraní pro přepravní jednotku (dělník, nákladní vůz, dron, vlak).
    /// </summary>
    public interface ILogisticsWorker
    {
        int WorkerId { get; }
        Vector3 Position { get; }
        float CarryingCapacity { get; }
        bool IsAvailable { get; }

        void AssignJob(LogisticsJob job);
        void CancelJob();
    }

    /// <summary>
    /// Požadavek na surovinu (např. továrna potřebující železnou rudu nebo sklad).
    /// </summary>
    public class LogisticsRequester
    {
        public int Id { get; }
        public Vector3 Position { get; set; }
        public string ResourceId { get; set; }
        public float DesiredAmount { get; set; }
        public float CurrentAmount { get; set; }

        /// <summary>Množství, které je již na cestě v běžících přepravních zakázkách (brání přezásobení).</summary>
        public float ReservedIncoming { get; set; }

        /// <summary>Priorita 1 (nejvyšší, např. elektrárna) až 5 (nejnižší).</summary>
        public int Priority { get; set; }

        public float UnmetDemand => Mathf.Max(0f, DesiredAmount - (CurrentAmount + ReservedIncoming));

        public LogisticsRequester(int id, Vector3 pos, string resourceId, float desired, int priority = 3)
        {
            Id = id;
            Position = pos;
            ResourceId = resourceId;
            DesiredAmount = desired;
            CurrentAmount = 0f;
            ReservedIncoming = 0f;
            Priority = Mathf.Clamp(priority, 1, 5);
        }
    }

    /// <summary>
    /// Poskytovatel suroviny (např. důl, pila, výstupní rampa továrny).
    /// </summary>
    public class LogisticsProvider
    {
        public int Id { get; }
        public Vector3 Position { get; set; }
        public string ResourceId { get; set; }
        public float AvailableStock { get; set; }

        /// <summary>Množství již rezervované pro blížící se vozy (zabraňuje nájezdu 10 vozů na 1 bednu).</summary>
        public float ReservedOutgoing { get; set; }

        public float UnreservedStock => Mathf.Max(0f, AvailableStock - ReservedOutgoing);

        public LogisticsProvider(int id, Vector3 pos, string resourceId, float stock)
        {
            Id = id;
            Position = pos;
            ResourceId = resourceId;
            AvailableStock = stock;
            ReservedOutgoing = 0f;
        }
    }

    /// <summary>
    /// Konkrétní logistická přepravní zakázka (Job).
    /// </summary>
    public class LogisticsJob
    {
        public int JobId { get; }
        public LogisticsProvider Source { get; }
        public LogisticsRequester Destination { get; }
        public string ResourceId { get; }
        public float Amount { get; }
        public int Priority { get; }

        public JobStatus Status { get; set; }
        public ILogisticsWorker AssignedWorker { get; set; }
        public float CreationTime { get; }

        public LogisticsJob(int id, LogisticsProvider source, LogisticsRequester dest, string resId, float amount, int priority)
        {
            JobId = id;
            Source = source;
            Destination = dest;
            ResourceId = resId;
            Amount = amount;
            Priority = priority;
            Status = JobStatus.PendingAssignment;
            AssignedWorker = null;
            CreationTime = Time.time;
        }
    }

    // =========================================================================
    // 2. HLAVNÍ LOGISTICKÝ DISPEČINK (LOGISTICS JOB SYSTEM)
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný dispečerský systém logistických úkolů (hauling dispatcher)
    /// ve stylu RimWorld, Factorio a Workers & Resources.
    /// Zajišťuje párování poptávky/nabídky, rezervace skladů (prevence kolizí)
    /// a inteligentní přiřazování volným pracovníkům s laděním priorit (zero-GC hot path).
    /// </summary>
    [DisallowMultipleComponent]
    public class LogisticsJobSystem : MonoBehaviour, ITickable
    {
        public static LogisticsJobSystem Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno po vytvoření nové přepravní zakázky.</summary>
        public static event Action<LogisticsJob> OnJobCreated;

        /// <summary>Vyvoláno při přiřazení zakázky pracovníkovi.</summary>
        public static event Action<LogisticsJob, ILogisticsWorker> OnJobAssigned;

        /// <summary>Vyvoláno po úspěšném doručení nákladu.</summary>
        public static event Action<LogisticsJob> OnJobCompleted;

        /// <summary>Vyvoláno při zrušení zakázky a uvolnění rezervací.</summary>
        public static event Action<LogisticsJob> OnJobCancelled;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Váhy dispečerského skórování")]
        [SerializeField, Range(1f, 50f)] private float priorityScoreWeight = 20f;
        [SerializeField, Range(0.01f, 1f)] private float distanceCostWeight = 0.05f;
        [SerializeField, Range(0.1f, 10f)] private float capacityFitBonus = 5f;

        [Header("Ladění")]
        [SerializeField] private bool drawGizmos = true;

        // =========================================================================
        // VNITŘNÍ REGISTRY ENTIT A ZAKÁZEK (ZERO-GC)
        // =========================================================================

        private readonly List<LogisticsRequester> requesters = new List<LogisticsRequester>(128);
        private readonly List<LogisticsProvider> providers = new List<LogisticsProvider>(128);
        private readonly List<ILogisticsWorker> workers = new List<ILogisticsWorker>(64);

        private readonly List<LogisticsJob> pendingJobs = new List<LogisticsJob>(128);
        private readonly List<LogisticsJob> activeJobs = new List<LogisticsJob>(128);

        private int nextJobId = 1;
        private int nextNodeId = 1;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public int PendingJobCount => pendingJobs.Count;
        public int ActiveJobCount => activeJobs.Count;
        public int WorkerCount => workers.Count;

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
        // ITICKABLE IMPLEMENTACE: DISPEČINK A PÁROVÁNÍ (ZERO-GC HOT PATH)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Spárování neuspokojené poptávky s volnou nabídkou na skladech
            MatchSupplyAndDemand();

            // 2. Přiřazení čekajících zakázek volným pracovníkům
            DispatchPendingJobs();
        }

        // =========================================================================
        // 1. PÁROVÁNÍ NABÍDKY A POPTÁVKY (MATCHING)
        // =========================================================================

        private void MatchSupplyAndDemand()
        {
            int reqCount = requesters.Count;
            int provCount = providers.Count;

            for (int r = 0; r < reqCount; r++)
            {
                LogisticsRequester req = requesters[r];
                float unmet = req.UnmetDemand;
                if (unmet <= 0.01f) continue;

                // Vyhledání nejvhodnějšího poskytovatele stejné suroviny s volnou zásobou
                LogisticsProvider bestProv = null;
                float closestDistSqr = float.MaxValue;

                for (int p = 0; p < provCount; p++)
                {
                    LogisticsProvider prov = providers[p];
                    if (prov.ResourceId != req.ResourceId) continue;

                    float stock = prov.UnreservedStock;
                    if (stock <= 0.01f) continue;

                    float distSqr = (prov.Position - req.Position).sqrMagnitude;
                    if (distSqr < closestDistSqr)
                    {
                        closestDistSqr = distSqr;
                        bestProv = prov;
                    }
                }

                // Pokud máme shodu, vytvoří se nová zakázka a okamžitě se aplikují rezervace
                if (bestProv != null)
                {
                    float transportAmount = Mathf.Min(unmet, bestProv.UnreservedStock);

                    // Aplikace striktních rezervací (prevence souběhu více vozidel na stejné suroviny)
                    bestProv.ReservedOutgoing += transportAmount;
                    req.ReservedIncoming += transportAmount;

                    var job = new LogisticsJob(nextJobId++, bestProv, req, req.ResourceId, transportAmount, req.Priority);
                    pendingJobs.Add(job);
                    OnJobCreated?.Invoke(job);
                }
            }
        }

        // =========================================================================
        // 2. DISPEČINK A PŘIŘAZENÍ VOLNÝM PRACOVNÍKŮM (DISPATCHER)
        // =========================================================================

        private void DispatchPendingJobs()
        {
            if (pendingJobs.Count == 0 || workers.Count == 0) return;

            // Seřazení zakázek podle priority (1 má přednost před 5)
            pendingJobs.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            for (int j = pendingJobs.Count - 1; j >= 0; j--)
            {
                LogisticsJob job = pendingJobs[j];
                ILogisticsWorker bestWorker = null;
                float bestScore = float.MinValue;

                for (int w = 0; w < workers.Count; w++)
                {
                    ILogisticsWorker worker = workers[w];
                    if (!worker.IsAvailable) continue;

                    // Výpočet vhodnosti pracovníka
                    float dist = Vector3.Distance(worker.Position, job.Source.Position);
                    float priorityScore = (6 - job.Priority) * priorityScoreWeight;
                    float distancePenalty = dist * distanceCostWeight;
                    float capacityScore = (worker.CarryingCapacity >= job.Amount) ? capacityFitBonus : 0f;

                    float totalScore = priorityScore - distancePenalty + capacityScore;

                    if (totalScore > bestScore)
                    {
                        bestScore = totalScore;
                        bestWorker = worker;
                    }
                }

                if (bestWorker != null)
                {
                    // Úspěšné přiřazení
                    pendingJobs.RemoveAt(j);
                    activeJobs.Add(job);

                    job.Status = JobStatus.EnRouteToPickup;
                    job.AssignedWorker = bestWorker;
                    bestWorker.AssignJob(job);

                    OnJobAssigned?.Invoke(job, bestWorker);
                }
            }
        }

        // =========================================================================
        // DOKONČENÍ A BEZPEČNÉ ZRUŠENÍ ZAKÁZEK (CANCELLATION & RECOVERY)
        // =========================================================================

        /// <summary>
        /// Oznámení od pracovníka, že suroviny byly úspěšně doručeny na cílové místo.
        /// </summary>
        public void NotifyJobCompleted(int jobId)
        {
            for (int i = 0; i < activeJobs.Count; i++)
            {
                LogisticsJob job = activeJobs[i];
                if (job.JobId == jobId)
                {
                    job.Status = JobStatus.Completed;

                    // Snížení rezervací a navýšení reálného stavu příjemce
                    job.Source.AvailableStock = Mathf.Max(0f, job.Source.AvailableStock - job.Amount);
                    job.Source.ReservedOutgoing = Mathf.Max(0f, job.Source.ReservedOutgoing - job.Amount);

                    job.Destination.CurrentAmount += job.Amount;
                    job.Destination.ReservedIncoming = Mathf.Max(0f, job.Destination.ReservedIncoming - job.Amount);

                    activeJobs.RemoveAt(i);
                    OnJobCompleted?.Invoke(job);
                    return;
                }
            }
        }

        /// <summary>
        /// Oznámení od pracovníka (nebo při jeho zničení), že zakázka selhala.
        /// Rezervace jsou korektně vráceny a suroviny zůstávají ve hře.
        /// </summary>
        public void NotifyJobFailedOrCancelled(int jobId)
        {
            for (int i = 0; i < activeJobs.Count; i++)
            {
                LogisticsJob job = activeJobs[i];
                if (job.JobId == jobId)
                {
                    CancelAndRollbackJob(job);
                    activeJobs.RemoveAt(i);
                    return;
                }
            }

            for (int i = 0; i < pendingJobs.Count; i++)
            {
                LogisticsJob job = pendingJobs[i];
                if (job.JobId == jobId)
                {
                    CancelAndRollbackJob(job);
                    pendingJobs.RemoveAt(i);
                    return;
                }
            }
        }

        private void CancelAndRollbackJob(LogisticsJob job)
        {
            job.Status = JobStatus.Cancelled;

            // Vrácení rezervací
            if (job.Source != null)
            {
                job.Source.ReservedOutgoing = Mathf.Max(0f, job.Source.ReservedOutgoing - job.Amount);
            }
            if (job.Destination != null)
            {
                job.Destination.ReservedIncoming = Mathf.Max(0f, job.Destination.ReservedIncoming - job.Amount);
            }

            if (job.AssignedWorker != null)
            {
                job.AssignedWorker.CancelJob();
                job.AssignedWorker = null;
            }

            OnJobCancelled?.Invoke(job);
        }

        // =========================================================================
        // VEŘEJNÉ API PRO REGISTRACI SKLADŮ A JEDNOTEK
        // =========================================================================

        public LogisticsRequester RegisterRequester(Vector3 position, string resourceId, float desiredAmount, int priority = 3)
        {
            var req = new LogisticsRequester(nextNodeId++, position, resourceId, desiredAmount, priority);
            requesters.Add(req);
            return req;
        }

        public bool UnregisterRequester(LogisticsRequester req)
        {
            return requesters.Remove(req);
        }

        public LogisticsProvider RegisterProvider(Vector3 position, string resourceId, float availableStock)
        {
            var prov = new LogisticsProvider(nextNodeId++, position, resourceId, availableStock);
            providers.Add(prov);
            return prov;
        }

        public bool UnregisterProvider(LogisticsProvider prov)
        {
            return providers.Remove(prov);
        }

        public void RegisterWorker(ILogisticsWorker worker)
        {
            if (worker != null && !workers.Contains(worker))
            {
                workers.Add(worker);
            }
        }

        public void UnregisterWorker(ILogisticsWorker worker)
        {
            if (worker != null)
            {
                // Pokud pracovník vezl zakázku, bezpečně ji zrušíme
                for (int i = activeJobs.Count - 1; i >= 0; i--)
                {
                    if (activeJobs[i].AssignedWorker == worker)
                    {
                        NotifyJobFailedOrCancelled(activeJobs[i].JobId);
                    }
                }
                workers.Remove(worker);
            }
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            // Zdroje (Zelená)
            Gizmos.color = Color.green;
            for (int i = 0; i < providers.Count; i++)
            {
                Gizmos.DrawWireCube(providers[i].Position + Vector3.up * 0.5f, Vector3.one * 0.8f);
            }

            // Příjemci (Modrá)
            Gizmos.color = Color.blue;
            for (int i = 0; i < requesters.Count; i++)
            {
                Gizmos.DrawWireSphere(requesters[i].Position + Vector3.up * 0.5f, 0.5f);
            }

            // Aktivní přepravní linie zakázek (Žlutá spojnice)
            Gizmos.color = Color.yellow;
            for (int i = 0; i < activeJobs.Count; i++)
            {
                LogisticsJob j = activeJobs[i];
                Gizmos.DrawLine(j.Source.Position, j.Destination.Position);
            }
        }
    }
}
