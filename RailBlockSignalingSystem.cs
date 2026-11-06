using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Roads;
using GameSystems.Simulation;

namespace GameSystems.Railways
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A STRUKTURY ŽELEZNIČNÍHO SIGNALIZAČNÍHO SYSTÉMU
    // =========================================================================

    public enum RailSignalType
    {
        /// <summary>Běžné návěstidlo: hlídá pouze bezprostřední blok za sebou.</summary>
        RegularBlockSignal,

        /// <summary>Řetězové návěstidlo: dívá se dopředu podél plánované trasy vlaku (zabraňuje ucpání křižovatek).</summary>
        ChainSignal
    }

    public enum RailSignalState
    {
        Red,    // Traťový blok je obsazen vlakem (stůj)
        Yellow, // Traťový blok je rezervován pro jiný blížící se vlak
        Green,  // Volno – blok je zcela volný
        Blue    // Řetězový stav: některé odbočující cesty jsou volné, jiné obsazené
    }

    public enum RailBlockOccupancy
    {
        Free,
        Reserved,
        Occupied
    }

    /// <summary>
    /// Návěstidlo umístěné u koleje rozdělující trať na samostatné bloky.
    /// </summary>
    public class RailSignal
    {
        public int Id { get; }
        public RailSignalType SignalType { get; set; }
        public RailSignalState CurrentState { get; set; }
        public Vector3 WorldPosition { get; set; }
        public int TrackSegmentId { get; set; }

        public int InBlockId { get; set; }  // Blok před návěstidlem
        public int OutBlockId { get; set; } // Blok za návěstidlem

        public RailSignal(int id, Vector3 pos, int segmentId, RailSignalType type)
        {
            Id = id;
            WorldPosition = pos;
            TrackSegmentId = segmentId;
            SignalType = type;
            CurrentState = RailSignalState.Green;
            InBlockId = -1;
            OutBlockId = -1;
        }
    }

    /// <summary>
    /// Diskrétní traťový blok (úsek trati ohraničený návěstidly).
    /// V jeden okamžik se v něm smí nacházet nejvýše jeden vlak!
    /// </summary>
    public class RailBlock
    {
        public int Id { get; }
        public List<int> SegmentIds { get; }
        public List<RailSignal> EntrySignals { get; }
        public List<RailSignal> ExitSignals { get; }

        public RailBlockOccupancy Occupancy { get; set; }
        public int OccupyingTrainId { get; set; }
        public int ReservingTrainId { get; set; }

        public RailBlock(int id)
        {
            Id = id;
            SegmentIds = new List<int>(4);
            EntrySignals = new List<RailSignal>(2);
            ExitSignals = new List<RailSignal>(2);
            Occupancy = RailBlockOccupancy.Free;
            OccupyingTrainId = -1;
            ReservingTrainId = -1;
        }
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER ŽELEZNIČNÍCH NÁVĚSTIDEL A BLOKŮ
    // =========================================================================

    /// <summary>
    /// Železniční blokový signalizační systém ve stylu Factorio a OpenTTD.
    /// Dělí graf kolejí na kolizně bezpečné bloky s běžnými a řetězovými návěstidly (Chain Signals).
    /// </summary>
    [DisallowMultipleComponent]
    public class RailBlockSignalingSystem : MonoBehaviour, ITickable
    {
        public static RailBlockSignalingSystem Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při změně stavu barvy návěstidla.</summary>
        public static event Action<RailSignal, RailSignalState> OnSignalStateChanged;

        /// <summary>Vyvoláno při změně obsazenosti traťového bloku.</summary>
        public static event Action<RailBlock, RailBlockOccupancy, int> OnBlockOccupancyChanged;

        /// <summary>Vyvoláno při vjezdu vlaku do bloku.</summary>
        public static event Action<int, int> OnTrainEnteredBlock; // trainId, blockId

        /// <summary>Vyvoláno po vyklizení bloku vlakem.</summary>
        public static event Action<int, int> OnTrainExitedBlock; // trainId, blockId

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField, Range(0.2f, 1.5f)] private float signalGizmoRadius = 0.5f;

        // =========================================================================
        // VNITŘNÍ GRAPH REPREZENTACE A BUFFER
        // =========================================================================

        private readonly List<RailSignal> signals = new List<RailSignal>(64);
        private readonly List<RailBlock> blocks = new List<RailBlock>(32);
        private readonly Dictionary<int, RailBlock> blocksById = new Dictionary<int, RailBlock>(32);
        private readonly Dictionary<int, RailSignal> signalsById = new Dictionary<int, RailSignal>(64);

        private int nextSignalId = 1;
        private int nextBlockId = 1;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public IReadOnlyList<RailSignal> Signals => signals;
        public IReadOnlyList<RailBlock> Blocks => blocks;

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
        // ITICKABLE IMPLEMENTACE: AKTUALIZACE STAVŮ NÁVĚSTIDEL
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            UpdateAllSignals();
        }

        /// <summary>
        /// Pravidelně přepočítává barvy všech návěstidel na základě obsazenosti bloků.
        /// </summary>
        public void UpdateAllSignals()
        {
            int signalCount = signals.Count;
            for (int i = 0; i < signalCount; i++)
            {
                RailSignal sig = signals[i];
                RailSignalState newState;

                if (!blocksById.TryGetValue(sig.OutBlockId, out RailBlock outBlock))
                {
                    newState = RailSignalState.Green;
                }
                else
                {
                    if (sig.SignalType == RailSignalType.RegularBlockSignal)
                    {
                        // Běžné návěstidlo
                        switch (outBlock.Occupancy)
                        {
                            case RailBlockOccupancy.Occupied:
                                newState = RailSignalState.Red;
                                break;
                            case RailBlockOccupancy.Reserved:
                                newState = RailSignalState.Yellow;
                                break;
                            case RailBlockOccupancy.Free:
                            default:
                                newState = RailSignalState.Green;
                                break;
                        }
                    }
                    else
                    {
                        // Řetězové návěstidlo (Chain Signal): dívá se i za výstupní blok
                        newState = EvaluateChainSignal(sig, outBlock);
                    }
                }

                if (sig.CurrentState != newState)
                {
                    sig.CurrentState = newState;
                    OnSignalStateChanged?.Invoke(sig, newState);
                }
            }
        }

        private RailSignalState EvaluateChainSignal(RailSignal chainSig, RailBlock directOutBlock)
        {
            // Pokud je bezprostřední blok za řetězovým návěstidlem plný
            if (directOutBlock.Occupancy == RailBlockOccupancy.Occupied)
            {
                return RailSignalState.Red;
            }
            if (directOutBlock.Occupancy == RailBlockOccupancy.Reserved)
            {
                return RailSignalState.Yellow;
            }

            // Vyhodnocení všech výstupních návěstidel z tohoto křižovatkového bloku
            var exits = directOutBlock.ExitSignals;
            if (exits.Count == 0) return RailSignalState.Green;

            int greenCount = 0;
            int totalExits = exits.Count;

            for (int i = 0; i < totalExits; i++)
            {
                RailSignal exitSig = exits[i];
                if (exitSig.CurrentState == RailSignalState.Green)
                {
                    greenCount++;
                }
            }

            if (greenCount == totalExits) return RailSignalState.Green;
            if (greenCount > 0) return RailSignalState.Blue; // Část cest volná, část obsazená
            return RailSignalState.Red; // Žádná výjezdová cesta není volná
        }

        // =========================================================================
        // UMÍSŤOVÁNÍ A SPRÁVA NÁVĚSTIDEL
        // =========================================================================

        /// <summary>
        /// Umístí návěstidlo k danému segmentu trati a znovu zrekonstruuje traťové bloky.
        /// </summary>
        public RailSignal PlaceSignal(Vector3 worldPos, int trackSegmentId, RailSignalType type)
        {
            var sig = new RailSignal(nextSignalId++, worldPos, trackSegmentId, type);
            signals.Add(sig);
            signalsById[sig.Id] = sig;

            RebuildRailBlocks();
            return sig;
        }

        /// <summary>
        /// Odstraní návěstidlo a přepočítá bloky.
        /// </summary>
        public bool RemoveSignal(int signalId)
        {
            if (signalsById.TryGetValue(signalId, out RailSignal sig))
            {
                signals.Remove(sig);
                signalsById.Remove(signalId);
                RebuildRailBlocks();
                return true;
            }
            return false;
        }

        // =========================================================================
        // AUTOMATICKÉ ČLENĚNÍ TRATI NA BLOKY (GRAPH PARTITIONING)
        // =========================================================================

        /// <summary>
        /// Projde graf kolejí (ze SplineRoadBuilder) a rozdělí jej do diskrétních RailBlock instancí.
        /// </summary>
        public void RebuildRailBlocks()
        {
            blocks.Clear();
            blocksById.Clear();

            if (SplineRoadBuilder.Instance == null)
            {
                // Vytvoření zástupného bloku pro testování bez silničního stavitele
                CreateFallbackBlocks();
                return;
            }

            var segments = SplineRoadBuilder.Instance.Segments;
            var visitedSegments = new HashSet<int>();

            for (int i = 0; i < segments.Count; i++)
            {
                RoadSegment seg = segments[i];
                if (visitedSegments.Contains(seg.Id)) continue;

                // Vytvoření nového bloku a naplnění propojenými segmenty až k návěstidlům
                var block = new RailBlock(nextBlockId++);
                TraverseBlockDFS(seg, block, visitedSegments);

                blocks.Add(block);
                blocksById[block.Id] = block;
            }

            // Propojení návěstidel s bloky
            MapSignalsToBlocks();
            UpdateAllSignals();
        }

        private void TraverseBlockDFS(RoadSegment currentSeg, RailBlock block, HashSet<int> visited)
        {
            visited.Add(currentSeg.Id);
            block.SegmentIds.Add(currentSeg.Id);

            // Pokud je na tomto segmentu návěstidlo, blok končí na této hranici
            if (HasSignalOnSegment(currentSeg.Id)) return;

            // Jinak pokračujeme na sousední segmenty
            TraverseNodeSegments(currentSeg.StartNode, block, visited);
            TraverseNodeSegments(currentSeg.EndNode, block, visited);
        }

        private void TraverseNodeSegments(RoadNode node, RailBlock block, HashSet<int> visited)
        {
            if (node == null) return;
            for (int i = 0; i < node.ConnectedSegments.Count; i++)
            {
                RoadSegment neighbor = node.ConnectedSegments[i];
                if (!visited.Contains(neighbor.Id) && !HasSignalOnSegment(neighbor.Id))
                {
                    TraverseBlockDFS(neighbor, block, visited);
                }
            }
        }

        private bool HasSignalOnSegment(int segId)
        {
            for (int i = 0; i < signals.Count; i++)
            {
                if (signals[i].TrackSegmentId == segId) return true;
            }
            return false;
        }

        private void MapSignalsToBlocks()
        {
            for (int i = 0; i < signals.Count; i++)
            {
                RailSignal sig = signals[i];
                for (int b = 0; b < blocks.Count; b++)
                {
                    RailBlock block = blocks[b];
                    if (block.SegmentIds.Contains(sig.TrackSegmentId))
                    {
                        sig.OutBlockId = block.Id;
                        block.EntrySignals.Add(sig);
                        break;
                    }
                }
            }
        }

        private void CreateFallbackBlocks()
        {
            var b1 = new RailBlock(nextBlockId++);
            var b2 = new RailBlock(nextBlockId++);
            blocks.Add(b1);
            blocks.Add(b2);
            blocksById[b1.Id] = b1;
            blocksById[b2.Id] = b2;

            if (signals.Count > 0)
            {
                signals[0].InBlockId = b1.Id;
                signals[0].OutBlockId = b2.Id;
                b1.ExitSignals.Add(signals[0]);
                b2.EntrySignals.Add(signals[0]);
            }
        }

        // =========================================================================
        // REZERVACE A OBSAZOVÁNÍ BLOKŮ VLAKY (PUBLIC API)
        // =========================================================================

        /// <summary>
        /// Pokusí se rezervovat blok pro blížící se vlak.
        /// </summary>
        public bool TryReserveBlock(int trainId, int blockId)
        {
            if (!blocksById.TryGetValue(blockId, out RailBlock block)) return false;

            if (block.Occupancy == RailBlockOccupancy.Free)
            {
                block.Occupancy = RailBlockOccupancy.Reserved;
                block.ReservingTrainId = trainId;
                OnBlockOccupancyChanged?.Invoke(block, RailBlockOccupancy.Reserved, trainId);
                return true;
            }

            return block.ReservingTrainId == trainId;
        }

        /// <summary>
        /// Vlak vjel do bloku (změna z Reserved/Free na Occupied).
        /// </summary>
        public void OccupyBlock(int trainId, int blockId)
        {
            if (!blocksById.TryGetValue(blockId, out RailBlock block)) return;

            block.Occupancy = RailBlockOccupancy.Occupied;
            block.OccupyingTrainId = trainId;
            OnBlockOccupancyChanged?.Invoke(block, RailBlockOccupancy.Occupied, trainId);
            OnTrainEnteredBlock?.Invoke(trainId, blockId);
        }

        /// <summary>
        /// Vlak zcela opustil blok (návrat stavu do Free).
        /// </summary>
        public void ReleaseBlock(int trainId, int blockId)
        {
            if (!blocksById.TryGetValue(blockId, out RailBlock block)) return;

            if (block.OccupyingTrainId == trainId || block.ReservingTrainId == trainId)
            {
                block.Occupancy = RailBlockOccupancy.Free;
                block.OccupyingTrainId = -1;
                block.ReservingTrainId = -1;
                OnBlockOccupancyChanged?.Invoke(block, RailBlockOccupancy.Free, trainId);
                OnTrainExitedBlock?.Invoke(trainId, blockId);
            }
        }

        /// <summary>
        /// Vyhodnotí řetězový průjezd (Chain Signal Passage):
        /// Povolí vjezd pouze v případě, že křižovatkový blok i následný výjezdový blok jsou volné!
        /// </summary>
        public bool RequestChainPassage(int trainId, int chainSignalId, IReadOnlyList<int> plannedBlockSequence)
        {
            if (!signalsById.TryGetValue(chainSignalId, out RailSignal sig)) return false;
            if (plannedBlockSequence == null || plannedBlockSequence.Count < 2) return false;

            // Kontrola všech bloků v křižovatkové sekvenci (např. IntersectionBlock + ExitBlock)
            for (int i = 0; i < plannedBlockSequence.Count; i++)
            {
                int bId = plannedBlockSequence[i];
                if (!blocksById.TryGetValue(bId, out RailBlock blk)) return false;

                if (blk.Occupancy != RailBlockOccupancy.Free && blk.ReservingTrainId != trainId)
                {
                    return false; // Některý z bloků v sekvenci je zablokován
                }
            }

            // Rezervace celé sekvence najednou (garantovaný bezpečný průjezd bez uváznutí uvnitř křižovatky)
            for (int i = 0; i < plannedBlockSequence.Count; i++)
            {
                TryReserveBlock(trainId, plannedBlockSequence[i]);
            }

            return true;
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            for (int i = 0; i < signals.Count; i++)
            {
                RailSignal sig = signals[i];
                switch (sig.CurrentState)
                {
                    case RailSignalState.Green: Gizmos.color = Color.green; break;
                    case RailSignalState.Yellow: Gizmos.color = Color.yellow; break;
                    case RailSignalState.Red: Gizmos.color = Color.red; break;
                    case RailSignalState.Blue: Gizmos.color = Color.blue; break;
                }

                Gizmos.DrawSphere(sig.WorldPosition + Vector3.up * 1.5f, signalGizmoRadius);

                // Značka typu návěstidla (Krychle pro Chain, Koule pro Regular)
                if (sig.SignalType == RailSignalType.ChainSignal)
                {
                    Gizmos.color = Color.white;
                    Gizmos.DrawWireCube(sig.WorldPosition + Vector3.up * 2f, Vector3.one * 0.4f);
                }
            }
        }
    }
}
