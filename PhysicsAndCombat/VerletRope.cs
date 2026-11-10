using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Verlet-integration rope: chain of points with distance constraints, gravity, damping and
/// simple collider collision. Rendered with a LineRenderer. Works in 2D and 3D.
/// Pin both ends to Transforms for bridges/tow cables, or use GrapplingHookExample for swinging.
/// Genres: puzzle bridges, swing platformers, tail weapons, hanging decor, cut-the-rope puzzles.
/// </summary>
public class VerletRope : MonoBehaviour
{
    class Point
    {
        public Vector3 pos, prev;
        public bool pinned;
        public Transform bound; // if set, point follows this transform
    }

    [Header("Rope")]
    public int segments = 12;
    public float segmentLength = 0.4f;
    public Transform startAnchor;
    public Transform endAnchor;
    public float ropeWidth = 0.06f;

    [Header("Physics")]
    public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
    public int constraintIterations = 4;
    [Range(0.9f, 1f)] public float damping = 0.99f;
    [Range(0.1f, 1f)] public float stiffness = 1f;
    public float maxStretchBeforeSnap = 0f;   // 0 = unbreakable

    [Header("Collision")]
    public bool collide = true;
    public LayerMask collisionMask = ~0;
    public float collisionPadding = 0.05f;

    [Header("Rendering")]
    public LineRenderer lineRenderer;
    public bool autoCreateLineRenderer = true;

    public bool Snapped { get; private set; }
    public event System.Action OnSnap;

    readonly List<Point> pts = new List<Point>();
    readonly Vector3[] renderPositions = new Vector3[512];
    bool is2D;

    void Awake()
    {
        is2D = GetComponent<Rigidbody2D>() != null || (startAnchor != null && startAnchor.GetComponent<Rigidbody2D>() != null);

        pts.Clear();
        Vector3 start = startAnchor != null ? startAnchor.position : transform.position;
        Vector3 end = endAnchor != null ? endAnchor.position : start + Vector3.down * (segments * segmentLength);

        for (int i = 0; i <= segments; i++)
            pts.Add(new Point { pos = Vector3.Lerp(start, end, i / (float)segments), prev = Vector3.Lerp(start, end, i / (float)segments) });

        pts[0].pinned = true;
        pts[0].bound = startAnchor;
        if (endAnchor != null && pts.Count > 1)
        {
            pts[pts.Count - 1].pinned = true;
            pts[pts.Count - 1].bound = endAnchor;
        }

        if (lineRenderer == null && autoCreateLineRenderer)
        {
            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.startWidth = ropeWidth;
            lineRenderer.endWidth = ropeWidth;
            lineRenderer.useWorldSpace = true;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        }
        if (lineRenderer != null) lineRenderer.positionCount = pts.Count;
    }

    void FixedUpdate()
    {
        if (Snapped) return;
        Simulate();
        for (int i = 0; i < constraintIterations; i++) Constrain();
        if (collide) Collide();
        Render();
    }

    void Simulate()
    {
        float dt = Time.fixedDeltaTime;
        foreach (var p in pts)
        {
            if (p.pinned)
            {
                if (p.bound != null)
                {
                    p.prev = p.pos;
                    p.pos = p.bound.position;
                }
                continue;
            }

            Vector3 vel = (p.pos - p.prev) * damping;
            p.prev = p.pos;
            p.pos += vel + gravity * dt * dt;
        }
    }

    void Constrain()
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];

            Vector3 delta = b.pos - a.pos;
            float dist = delta.magnitude;
            if (dist < 0.00001f) continue;
            float error = dist - segmentLength;

            if (maxStretchBeforeSnap > 0f && error > maxStretchBeforeSnap)
            {
                Snapped = true;
                OnSnap?.Invoke();
                if (lineRenderer != null) lineRenderer.enabled = false;
                return;
            }

            Vector3 correction = (delta / dist) * error * stiffness;

            if (a.pinned && b.pinned) continue;
            if (a.pinned) b.pos -= correction;
            else if (b.pinned) a.pos += correction;
            else
            {
                a.pos += correction * 0.5f;
                b.pos -= correction * 0.5f;
            }
        }
    }

    void Collide()
    {
        // Push rope points out of nearby colliders (sphere approximation from bounds).
        var cols = Physics.OverlapSphere(transform.position, segments * segmentLength + 2f, collisionMask);
        foreach (var col in cols)
        {
            if (col.transform == transform || col.isTrigger) continue;
            Bounds b = col.bounds;
            Vector3 c = b.center;
            float r = Mathf.Max(b.extents.x, b.extents.y) + collisionPadding;

            foreach (var p in pts)
            {
                Vector3 d = p.pos - c;
                if (is2D) d.z = 0f;
                float dist = d.magnitude;
                if (dist < r && dist > 0.0001f)
                    p.pos = c + d * (r / dist);
            }
        }
    }

    void Render()
    {
        if (lineRenderer == null) return;
        int n = Mathf.Min(pts.Count, renderPositions.Length);
        for (int i = 0; i < n; i++) renderPositions[i] = pts[i].pos;
        lineRenderer.SetPositions(renderPositions);
    }

    // ---------------- PUBLIC API ----------------

    public Vector3 GetPoint(int i) => pts[Mathf.Clamp(i, 0, pts.Count - 1)].pos;
    public int PointCount => pts.Count;
    public float Length => segmentLength * segments;

    /// <summary>Grab the rope at world position: nearest point becomes pinned to 'holder'.</summary>
    public void Grab(Vector3 worldPos, Transform holder)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            float d = (pts[i].pos - worldPos).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        pts[best].pinned = true;
        pts[best].bound = holder;
    }

    /// <summary>Unpin a point (falls freely again).</summary>
    public void Release(int index)
    {
        var p = pts[Mathf.Clamp(index, 0, pts.Count - 1)];
        p.pinned = false;
        p.bound = null;
    }

    /// <summary>Unpin every grabbed point (keeps original anchors).</summary>
    public void ReleaseAll()
    {
        for (int i = 1; i < pts.Count - 1; i++) Release(i);
    }
}

/// <summary>
/// EXAMPLE: grappling hook. Hold Fire1 (mouse) to fire a ray and swing on a spawned rope,
/// W/S to reel in/out, A/D to swing laterally. Attach to the player.
/// </summary>
public class GrapplingHookExample : MonoBehaviour
{
    [Header("Hook")]
    public float maxRange = 25f;
    public LayerMask grappleMask = ~0;
    public KeyCode grappleKey = KeyCode.Mouse0;
    public float reelSpeed = 0.6f;

    [Header("Swing Force")]
    public float swingForce = 12f;
    public KeyCode swingLeftKey = KeyCode.A;
    public KeyCode swingRightKey = KeyCode.D;

    Rigidbody rb3;
    Rigidbody2D rb2d;
    VerletRope rope;
    GameObject hookPin;

    void Awake()
    {
        rb3 = GetComponent<Rigidbody>();
        rb2d = GetComponent<Rigidbody2D>();
    }

    void Update()
    {
        if (Input.GetKeyDown(grappleKey)) TryAttach();
        if (Input.GetKeyUp(grappleKey)) Detach();

        if (rope != null)
        {
            float reel = (Input.GetKey(KeyCode.W) ? -1f : 0f) + (Input.GetKey(KeyCode.S) ? 1f : 0f);
            rope.segmentLength = Mathf.Max(0.15f, rope.segmentLength + reel * reelSpeed * Time.deltaTime);

            float steer = (Input.GetKey(swingRightKey) ? 1f : 0f) - (Input.GetKey(swingLeftKey) ? 1f : 0f);
            if (steer != 0f)
            {
                if (rb3 != null) rb3.AddForce(transform.right * steer * swingForce);
                if (rb2d != null) rb2d.AddForce(new Vector2(steer, 0f) * swingForce);
            }
        }
    }

    void TryAttach()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);

        Vector3 point;
        if (rb2d != null && rb3 == null)
        {
            var hit2d = Physics2D.Raycast(ray.origin, ray.direction, maxRange, grappleMask);
            if (hit2d.collider == null) return;
            point = hit2d.point;
        }
        else
        {
            if (!Physics.Raycast(ray, out RaycastHit hit, maxRange, grappleMask)) return;
            point = hit.point;
        }

        hookPin = new GameObject("HookPin");
        hookPin.transform.position = point;

        rope = gameObject.AddComponent<VerletRope>();
        rope.segments = Mathf.Max(4, Mathf.RoundToInt(Vector3.Distance(transform.position, point) / 0.5f));
        rope.segmentLength = 0.5f;
        rope.gravity = Vector3.zero; // player rigidbody provides gravity; rope carries tension
        rope.collide = false;
        rope.startAnchor = transform;
        rope.endAnchor = hookPin.transform;
    }

    void Detach()
    {
        if (rope != null) Destroy(rope);
        if (hookPin != null) Destroy(hookPin);
        rope = null;
        hookPin = null;
    }
}
