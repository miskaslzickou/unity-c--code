using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.Roads
{
    // =========================================================================
    // 1. DATOVÉ STRUKTURY GRAFU SILNIC A SPLINE KŘIVEK
    // =========================================================================

    public enum RoadNodeType
    {
        Endpoint,  // Konec silnice (1 napojený segment)
        Straight,  // Plynulé pokračování (2 napojené segmenty)
        TJunction, // T-křižovatka (3 napojené segmenty)
        Cross4Way  // Křižovatka 4 cest (4 a více napojených segmentů)
    }

    /// <summary>
    /// Vzorek pro parametrizaci délky oblouku (Arc-Length Parameterization).
    /// Zajišťuje, že vozidla jezdí konstantní rychlostí bez ohledu na nerovnoměrné rozložení řídících bodů.
    /// </summary>
    public struct ArcLengthSample
    {
        public float T;        // 0.0 až 1.0 podél Bézierovy křivky
        public float Distance; // Kumulativní vzdálenost v metrech od začátku
    }

    /// <summary>
    /// Uzel silniční sítě (křižovatka nebo koncový bod).
    /// </summary>
    public class RoadNode
    {
        public int Id { get; }
        public Vector3 Position { get; set; }
        public List<RoadSegment> ConnectedSegments { get; }

        public RoadNodeType NodeType
        {
            get
            {
                int count = ConnectedSegments.Count;
                if (count <= 1) return RoadNodeType.Endpoint;
                if (count == 2) return RoadNodeType.Straight;
                if (count == 3) return RoadNodeType.TJunction;
                return RoadNodeType.Cross4Way;
            }
        }

        public RoadNode(int id, Vector3 position)
        {
            Id = id;
            Position = position;
            ConnectedSegments = new List<RoadSegment>(4);
        }
    }

    /// <summary>
    /// Silniční segment tvořený kubickou Bézierovou křivkou (Cubic Bézier Spline).
    /// Obsahuje řídící body, šířku, počet pruhů, lookup tabulku délek a procedurální mesh.
    /// </summary>
    public class RoadSegment
    {
        public int Id { get; }
        public RoadNode StartNode { get; set; }
        public RoadNode EndNode { get; set; }

        // Řídící body kubické Bézierovy křivky (P0, P1, P2, P3)
        public Vector3 P0 => StartNode.Position;
        public Vector3 P1 { get; set; }
        public Vector3 P2 { get; set; }
        public Vector3 P3 => EndNode.Position;

        public float Width { get; set; }
        public int LaneCount { get; set; }
        public float TotalLength { get; private set; }

        public ArcLengthSample[] ArcLengthTable { get; private set; }
        public GameObject MeshObject { get; set; }
        public Mesh GeneratedMesh { get; set; }

        public RoadSegment(int id, RoadNode start, RoadNode end, Vector3 p1, Vector3 p2, float width = 4f, int lanes = 2)
        {
            Id = id;
            StartNode = start;
            EndNode = end;
            P1 = p1;
            P2 = p2;
            Width = width;
            LaneCount = lanes;
            RecomputeArcLengthTable(50);
        }

        /// <summary>
        /// Přepočítá tabulku délek oblouku (Arc-Length Parameterization).
        /// </summary>
        public void RecomputeArcLengthTable(int sampleCount = 50)
        {
            sampleCount = Mathf.Max(10, sampleCount);
            ArcLengthTable = new ArcLengthSample[sampleCount + 1];

            float totalDist = 0f;
            Vector3 prevPoint = EvaluateCubicBezier(0f);
            ArcLengthTable[0] = new ArcLengthSample { T = 0f, Distance = 0f };

            for (int i = 1; i <= sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                Vector3 pt = EvaluateCubicBezier(t);
                totalDist += Vector3.Distance(prevPoint, pt);
                ArcLengthTable[i] = new ArcLengthSample { T = t, Distance = totalDist };
                prevPoint = pt;
            }

            TotalLength = totalDist;
        }

        public Vector3 EvaluateCubicBezier(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            return (uuu * P0) + (3f * uu * t * P1) + (3f * u * tt * P2) + (ttt * P3);
        }

        public Vector3 EvaluateTangent(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            Vector3 tangent = 3f * u * u * (P1 - P0) + 6f * u * t * (P2 - P1) + 3f * t * t * (P3 - P2);
            return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;
        }

        /// <summary>
        /// Získá pozici a tečnu podle ujeté vzdálenosti v metrech s konstantní rychlostí.
        /// Využívá binární vyhledávání v Arc-Length tabulce.
        /// </summary>
        public void GetPointAtDistance(float distance, out Vector3 position, out Vector3 tangent)
        {
            if (ArcLengthTable == null || ArcLengthTable.Length == 0)
            {
                position = P0;
                tangent = Vector3.forward;
                return;
            }

            distance = Mathf.Clamp(distance, 0f, TotalLength);

            // Binární vyhledávání v tabulce
            int low = 0;
            int high = ArcLengthTable.Length - 1;

            while (low < high - 1)
            {
                int mid = (low + high) / 2;
                if (ArcLengthTable[mid].Distance <= distance)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            ArcLengthSample s0 = ArcLengthTable[low];
            ArcLengthSample s1 = ArcLengthTable[high];

            float distSpan = s1.Distance - s0.Distance;
            float frac = distSpan > 0.0001f ? (distance - s0.Distance) / distSpan : 0f;
            float t = Mathf.Lerp(s0.T, s1.T, frac);

            position = EvaluateCubicBezier(t);
            tangent = EvaluateTangent(t);
        }
    }

    // =========================================================================
    // 2. HLAVNÍ BUILDER SILNIC A GRAFU (SPLINE ROAD BUILDER)
    // =========================================================================

    /// <summary>
    /// Procedurální budovač kubických Bézierových silnic, kolejí a dopravníků.
    /// Podporuje automatické přichytávání k uzlům, rozdělování existujících silnic
    /// na T-křižovatky a generování procedurálních 3D polygonových sítí (Mesh).
    /// </summary>
    [DisallowMultipleComponent]
    public class SplineRoadBuilder : MonoBehaviour
    {
        public static SplineRoadBuilder Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS PRO INTERSECTION CONTROLLER A LOGISTIKU)
        // =========================================================================

        /// <summary>Vyvoláno po vytvoření nového silničního segmentu.</summary>
        public static event Action<RoadSegment> OnRoadSegmentCreated;

        /// <summary>Vyvoláno při smazání silničního segmentu.</summary>
        public static event Action<RoadSegment> OnRoadSegmentDestroyed;

        /// <summary>Vyvoláno při vzniku nové křižovatky (napojení na TrafficIntersectionController).</summary>
        public static event Action<RoadNode> OnIntersectionCreated;

        /// <summary>Vyvoláno při rozdělení segmentu na dva při napojení nové silnice do jeho středu.</summary>
        public static event Action<RoadSegment, RoadSegment, RoadNode> OnRoadSegmentSplit;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Parametry silnice")]
        [SerializeField, Range(2f, 15f)] private float defaultRoadWidth = 4f;
        [SerializeField, Range(1, 4)] private int defaultLanes = 2;
        [SerializeField, Range(0.5f, 5f), Tooltip("Poloměr přichycení k existujícím uzlům a silnicím")]
        private float snapRadius = 2.0f;

        [Header("Materiály pro procedurální síť (Mesh)")]
        [SerializeField] private Material roadMaterial = null;
        [SerializeField, Range(4, 40)] private int meshSubdivisionsPerSegment = 16;
        [SerializeField, Min(0.1f)] private float textureTilingLength = 5f;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private bool drawLaneLines = true;

        // =========================================================================
        // VNITŘNÍ GRAPH REPREZENTACE
        // =========================================================================

        private readonly List<RoadNode> nodes = new List<RoadNode>(64);
        private readonly List<RoadSegment> segments = new List<RoadSegment>(128);
        private int nextNodeId = 1;
        private int nextSegmentId = 1;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public IReadOnlyList<RoadNode> Nodes => nodes;
        public IReadOnlyList<RoadSegment> Segments => segments;
        public float SnapRadius => snapRadius;

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

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // =========================================================================
        // VYTVÁŘENÍ A PROPOJOVÁNÍ SILNIC S AUTOMATICKÝM PŘICHYTÁVÁNÍM
        // =========================================================================

        /// <summary>
        /// Vytvoří novou silnici mezi dvěma body. Automaticky přichytí konce k blízkým uzlům
        /// nebo rozdělí existující segment na novou křižovatku.
        /// </summary>
        public RoadSegment CreateRoad(Vector3 startPos, Vector3 endPos, float width = -1f, int lanes = -1)
        {
            if (Vector3.Distance(startPos, endPos) < 1.0f) return null;

            width = width > 0f ? width : defaultRoadWidth;
            lanes = lanes > 0 ? lanes : defaultLanes;

            // 1. Zpracování startovního bodu (Snap nebo Split)
            RoadNode startNode = ResolveNodeAtPosition(startPos);

            // 2. Zpracování koncového bodu (Snap nebo Split)
            RoadNode endNode = ResolveNodeAtPosition(endPos);

            if (startNode == endNode) return null;

            // 3. Výpočet přirozených kontrolních bodů kubické křivky
            Vector3 diff = endNode.Position - startNode.Position;
            Vector3 p1 = startNode.Position + diff * 0.33f;
            Vector3 p2 = startNode.Position + diff * 0.66f;

            var segment = new RoadSegment(nextSegmentId++, startNode, endNode, p1, p2, width, lanes);
            startNode.ConnectedSegments.Add(segment);
            endNode.ConnectedSegments.Add(segment);
            segments.Add(segment);

            // Generování procedurální geometrie
            GenerateRoadMesh(segment);

            OnRoadSegmentCreated?.Invoke(segment);

            if (startNode.ConnectedSegments.Count >= 3) OnIntersectionCreated?.Invoke(startNode);
            if (endNode.ConnectedSegments.Count >= 3) OnIntersectionCreated?.Invoke(endNode);

            return segment;
        }

        private RoadNode ResolveNodeAtPosition(Vector3 pos)
        {
            // A. Zkusit přichytit k existujícímu uzlu
            RoadNode existingNode = FindNearestNode(pos, snapRadius);
            if (existingNode != null) return existingNode;

            // B. Zkusit rozdělit existující segment (T-křižovatka)
            RoadSegment nearestSeg = FindNearestSegment(pos, snapRadius, out _, out float splitT);
            if (nearestSeg != null && splitT > 0.1f && splitT < 0.9f)
            {
                if (SplitSegmentAtT(nearestSeg, splitT, out RoadNode splitNode, out _, out _))
                {
                    return splitNode;
                }
            }

            // C. Vytvořit zcela nový uzel
            var newNode = new RoadNode(nextNodeId++, pos);
            nodes.Add(newNode);
            return newNode;
        }

        // =========================================================================
        // ROZDĚLOVÁNÍ SILNIČNÍCH SEGMENTŮ (SPLIT NA T-KŘIŽOVATKY)
        // =========================================================================

        /// <summary>
        /// Rozdělí existující segment na dva menší segmenty a vloží mezi ně nový uzel (křižovatku).
        /// </summary>
        public bool SplitSegmentAtT(RoadSegment original, float t, out RoadNode splitNode, out RoadSegment segA, out RoadSegment segB)
        {
            splitNode = null;
            segA = null;
            segB = null;

            if (original == null || t <= 0.05f || t >= 0.95f) return false;

            Vector3 splitPos = original.EvaluateCubicBezier(t);
            splitNode = new RoadNode(nextNodeId++, splitPos);
            nodes.Add(splitNode);

            // De Casteljau algoritmus pro přesné rozdělení kubické křivky
            Vector3 p0 = original.P0;
            Vector3 p1 = original.P1;
            Vector3 p2 = original.P2;
            Vector3 p3 = original.P3;

            Vector3 p01 = Vector3.Lerp(p0, p1, t);
            Vector3 p12 = Vector3.Lerp(p1, p2, t);
            Vector3 p23 = Vector3.Lerp(p2, p3, t);

            Vector3 p012 = Vector3.Lerp(p01, p12, t);
            Vector3 p123 = Vector3.Lerp(p12, p23, t);

            Vector3 splitPoint = Vector3.Lerp(p012, p123, t);

            // Odpojení původního segmentu
            original.StartNode.ConnectedSegments.Remove(original);
            original.EndNode.ConnectedSegments.Remove(original);
            segments.Remove(original);

            if (original.MeshObject != null)
            {
                Destroy(original.MeshObject);
            }

            // Vytvoření segmentu A (Start -> Split)
            segA = new RoadSegment(nextSegmentId++, original.StartNode, splitNode, p01, p012, original.Width, original.LaneCount);
            original.StartNode.ConnectedSegments.Add(segA);
            splitNode.ConnectedSegments.Add(segA);
            segments.Add(segA);
            GenerateRoadMesh(segA);

            // Vytvoření segmentu B (Split -> End)
            segB = new RoadSegment(nextSegmentId++, splitNode, original.EndNode, p123, p23, original.Width, original.LaneCount);
            splitNode.ConnectedSegments.Add(segB);
            original.EndNode.ConnectedSegments.Add(segB);
            segments.Add(segB);
            GenerateRoadMesh(segB);

            OnRoadSegmentDestroyed?.Invoke(original);
            OnRoadSegmentCreated?.Invoke(segA);
            OnRoadSegmentCreated?.Invoke(segB);
            OnRoadSegmentSplit?.Invoke(segA, segB, splitNode);
            OnIntersectionCreated?.Invoke(splitNode);

            return true;
        }

        /// <summary>
        /// Odstraní silniční segment z grafu a uklidí mesh.
        /// </summary>
        public bool RemoveRoadSegment(RoadSegment segment)
        {
            if (segment == null || !segments.Contains(segment)) return false;

            segment.StartNode.ConnectedSegments.Remove(segment);
            segment.EndNode.ConnectedSegments.Remove(segment);
            segments.Remove(segment);

            if (segment.MeshObject != null)
            {
                Destroy(segment.MeshObject);
            }

            // Úklid osiřelých uzlů bez jakýchkoliv napojených silnic
            if (segment.StartNode.ConnectedSegments.Count == 0) nodes.Remove(segment.StartNode);
            if (segment.EndNode.ConnectedSegments.Count == 0) nodes.Remove(segment.EndNode);

            OnRoadSegmentDestroyed?.Invoke(segment);
            return true;
        }

        // =========================================================================
        // PROCEDURÁLNÍ GENEROVÁNÍ 3D MESH PRO SILNICE
        // =========================================================================

        public void GenerateRoadMesh(RoadSegment seg)
        {
            if (seg == null) return;

            int steps = meshSubdivisionsPerSegment;
            int vertCount = (steps + 1) * 2;
            int triCount = steps * 6;

            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] triangles = new int[triCount];

            float halfWidth = seg.Width * 0.5f;
            float totalLen = seg.TotalLength;

            int vIdx = 0;
            int tIdx = 0;

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector3 center = seg.EvaluateCubicBezier(t);
                Vector3 tangent = seg.EvaluateTangent(t);

                // Ortogonální normála v rovině XZ (-Tz, 0, Tx)
                Vector3 normal = new Vector3(-tangent.z, 0f, tangent.x).normalized;

                Vector3 left = center - normal * halfWidth;
                Vector3 right = center + normal * halfWidth;

                vertices[vIdx] = left;
                vertices[vIdx + 1] = right;

                float vCoord = (t * totalLen) / textureTilingLength;
                uvs[vIdx] = new Vector2(0f, vCoord);
                uvs[vIdx + 1] = new Vector2(1f, vCoord);

                if (i < steps)
                {
                    triangles[tIdx] = vIdx;
                    triangles[tIdx + 1] = vIdx + 2;
                    triangles[tIdx + 2] = vIdx + 1;

                    triangles[tIdx + 3] = vIdx + 1;
                    triangles[tIdx + 4] = vIdx + 2;
                    triangles[tIdx + 5] = vIdx + 3;

                    tIdx += 6;
                }

                vIdx += 2;
            }

            Mesh mesh = new Mesh
            {
                name = $"RoadMesh_Seg_{seg.Id}",
                vertices = vertices,
                uv = uvs,
                triangles = triangles
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            seg.GeneratedMesh = mesh;

            if (seg.MeshObject == null)
            {
                GameObject obj = new GameObject($"RoadSegment_{seg.Id}");
                obj.transform.SetParent(transform);
                var mf = obj.AddComponent<MeshFilter>();
                var mr = obj.AddComponent<MeshRenderer>();
                var col = obj.AddComponent<MeshCollider>();

                mf.sharedMesh = mesh;
                col.sharedMesh = mesh;
                if (roadMaterial != null) mr.material = roadMaterial;

                seg.MeshObject = obj;
            }
            else
            {
                seg.MeshObject.GetComponent<MeshFilter>().sharedMesh = mesh;
                seg.MeshObject.GetComponent<MeshCollider>().sharedMesh = mesh;
            }
        }

        // =========================================================================
        // PROSTOROVÉ VYHLEDÁVÁNÍ V GRAFU (QUERIES)
        // =========================================================================

        public RoadNode FindNearestNode(Vector3 worldPos, float maxRadius)
        {
            RoadNode nearest = null;
            float minSqr = maxRadius * maxRadius;

            for (int i = 0; i < nodes.Count; i++)
            {
                float dSqr = (nodes[i].Position - worldPos).sqrMagnitude;
                if (dSqr < minSqr)
                {
                    minSqr = dSqr;
                    nearest = nodes[i];
                }
            }

            return nearest;
        }

        public RoadSegment FindNearestSegment(Vector3 worldPos, float maxRadius, out float closestDist, out float closestT)
        {
            RoadSegment nearest = null;
            closestDist = float.MaxValue;
            closestT = 0f;

            for (int i = 0; i < segments.Count; i++)
            {
                RoadSegment seg = segments[i];
                // Vzorkování segmentu po 20 krocích
                for (int s = 0; s <= 20; s++)
                {
                    float t = (float)s / 20f;
                    Vector3 pt = seg.EvaluateCubicBezier(t);
                    float d = Vector3.Distance(worldPos, pt);

                    if (d < maxRadius && d < closestDist)
                    {
                        closestDist = d;
                        closestT = t;
                        nearest = seg;
                    }
                }
            }

            return nearest;
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            // Vykreslení uzlů podle typu křižovatky
            for (int i = 0; i < nodes.Count; i++)
            {
                RoadNode n = nodes[i];
                switch (n.NodeType)
                {
                    case RoadNodeType.Endpoint: Gizmos.color = Color.green; break;
                    case RoadNodeType.Straight: Gizmos.color = Color.cyan; break;
                    case RoadNodeType.TJunction: Gizmos.color = Color.yellow; break;
                    case RoadNodeType.Cross4Way: Gizmos.color = Color.red; break;
                }
                Gizmos.DrawSphere(n.Position, 0.4f);
            }

            // Vykreslení silničních křivek a pruhů
            for (int i = 0; i < segments.Count; i++)
            {
                RoadSegment seg = segments[i];
                Gizmos.color = Color.white;

                Vector3 prev = seg.P0;
                for (int s = 1; s <= 30; s++)
                {
                    float t = (float)s / 30f;
                    Vector3 pt = seg.EvaluateCubicBezier(t);
                    Gizmos.DrawLine(prev, pt);
                    prev = pt;
                }

                // Vykreslení středové čáry silnice
                if (drawLaneLines)
                {
                    Gizmos.color = Color.yellow;
                    for (int s = 0; s < 30; s += 2)
                    {
                        float t1 = (float)s / 30f;
                        float t2 = (float)(s + 1) / 30f;
                        Gizmos.DrawLine(seg.EvaluateCubicBezier(t1) + Vector3.up * 0.05f, seg.EvaluateCubicBezier(t2) + Vector3.up * 0.05f);
                    }
                }
            }
        }
    }
}
