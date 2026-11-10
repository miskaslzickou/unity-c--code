using UnityEngine;

/// <summary>
/// Production-ready, zero-GC, physics-based 2D grappling hook and pendulum swing controller.
/// Does NOT use DistanceJoint2D/SpringJoint2D; implements manual velocity & position
/// cable constraints directly on Rigidbody2D for responsive, tight game feel.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
public class GrappleController2D : MonoBehaviour
{
    #region Serialized Fields - Configuration

    [Header("Aim & Raycast")]
    [Tooltip("Layer mask containing grappleable geometry.")]
    [SerializeField] private LayerMask grappleLayer;

    [Tooltip("Maximum distance the grappling hook can reach.")]
    [SerializeField] private float maxDistance = 25f;

    [Tooltip("Transform from which the grapple rope originates. Defaults to player transform if unassigned.")]
    [SerializeField] private Transform firePoint;

    [Tooltip("Camera used to calculate cursor world coordinates. Defaults to Camera.main if null.")]
    [SerializeField] private Camera targetCamera;

    [Tooltip("Mouse button index used to grapple (0 = Left Click, 1 = Right Click).")]
    [SerializeField] private int grappleMouseButton = 0;

    [Header("Cable & Pendulum Physics")]
    [Tooltip("Minimum allowable rope length when reeling in.")]
    [SerializeField] private float minRopeLength = 1.5f;

    [Tooltip("Maximum allowable rope length when letting rope out.")]
    [SerializeField] private float maxRopeLength = 30f;

    [Tooltip("Tangential acceleration force applied when pressing horizontal inputs (A/D).")]
    [SerializeField] private float swingForce = 35f;

    [Tooltip("Maximum tangential velocity achievable through player swing pumping.")]
    [SerializeField] private float maxSwingVelocity = 28f;

    [Tooltip("Linear damping applied to tangential swing velocity per second (0 = pure conservation).")]
    [Range(0f, 2f)]
    [SerializeField] private float swingDamping = 0.05f;

    [Tooltip("Baumgarte position correction factor (0 to 1) applied each FixedUpdate to eliminate rope stretch.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float positionCorrectionRate = 0.5f;

    [Header("Rope Reel Controls")]
    [Tooltip("Speed in units/sec at which the rope shortens (W) or lengthens (S).")]
    [SerializeField] private float reelSpeed = 10f;

    [Tooltip("Inward velocity pulse applied while actively reeling in to assist momentum.")]
    [SerializeField] private float reelInImpulse = 4f;

    [Header("Release & Momentum Boost")]
    [Tooltip("Multiplier applied to player's current velocity upon releasing the grapple.")]
    [SerializeField] private float releaseVelocityMultiplier = 1.15f;

    [Tooltip("Upward velocity kick added upon release, scaled by tangential speed.")]
    [SerializeField] private float releaseUpwardBoost = 3.5f;

    [Tooltip("Clamp on the maximum release velocity magnitude to prevent launching out of bounds.")]
    [SerializeField] private float maxReleaseVelocity = 40f;

    [Header("Visuals & Procedural Wave")]
    [Tooltip("LineRenderer component used to display the rope.")]
    [SerializeField] private LineRenderer lineRenderer;

    [Tooltip("Number of vertices in the procedural rope line renderer.")]
    [Range(4, 64)]
    [SerializeField] private int ropeResolution = 24;

    [Tooltip("Peak amplitude of the sinusoidal latch wave.")]
    [SerializeField] private float waveAmplitude = 0.6f;

    [Tooltip("Frequency of spatial oscillations along the rope during latch phase.")]
    [SerializeField] private float waveFrequency = 3f;

    [Tooltip("Travel speed of the procedural wave along the rope.")]
    [SerializeField] private float waveSpeed = 12f;

    [Tooltip("Duration in seconds for the latch wave to settle into a straight taut cable.")]
    [SerializeField] private float waveSettleDuration = 0.35f;

    #endregion

    #region Private State & Buffers

    // Component references
    private Rigidbody2D rb;

    // Grapple state
    private bool isGrappled;
    private Vector2 anchorPoint;
    private float currentRopeLength;

    // Input state (polled in Update, consumed in FixedUpdate)
    private float horizontalInput;
    private float verticalInput;

    // Procedural wave animation state
    private float waveTimer;
    private bool isWaving;

    // Zero-GC Preallocated line renderer buffer
    private Vector3[] ropePositions;

    #endregion

    #region Properties

    /// <summary>True when currently attached to an anchor point.</summary>
    public bool IsGrappled => isGrappled;

    /// <summary>Current world coordinates of the grappling hook anchor.</summary>
    public Vector2 AnchorPoint => anchorPoint;

    /// <summary>Current target length of the taut cable constraint.</summary>
    public float CurrentRopeLength => currentRopeLength;

    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();

        if (firePoint == null)
        {
            firePoint = transform;
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        // Initialize and preallocate LineRenderer vertex buffer (Zero GC during runtime)
        InitializeLineRenderer();
    }

    private void Update()
    {
        CacheInput();
        HandleGrappleInput();
        UpdateRopeVisuals();
    }

    private void FixedUpdate()
    {
        if (!isGrappled) return;

        HandleReeling();
        ApplyPendulumConstraint();
        ApplySwingInput();
    }

    #endregion

    #region Initialization

    private void InitializeLineRenderer()
    {
        if (lineRenderer == null)
        {
            lineRenderer = GetComponent<LineRenderer>();
            if (lineRenderer == null)
            {
                lineRenderer = gameObject.AddComponent<LineRenderer>();
            }
        }

        if (ropeResolution < 2)
        {
            ropeResolution = 2;
        }

        ropePositions = new Vector3[ropeResolution];
        lineRenderer.positionCount = ropeResolution;
        lineRenderer.useWorldSpace = true;
        lineRenderer.enabled = false;
    }

    #endregion

    #region Input Handling

    private void CacheInput()
    {
        horizontalInput = Input.GetAxisRaw("Horizontal");
        verticalInput = Input.GetAxisRaw("Vertical");
    }

    private void HandleGrappleInput()
    {
        if (Input.GetMouseButtonDown(grappleMouseButton))
        {
            TryFireGrapple();
        }
        else if (Input.GetMouseButtonUp(grappleMouseButton) && isGrappled)
        {
            ReleaseGrapple();
        }
    }

    private void TryFireGrapple()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
            if (targetCamera == null) return;
        }

        Vector3 mouseScreen = Input.mousePosition;
        Vector3 mouseWorld3D = targetCamera.ScreenToWorldPoint(mouseScreen);
        Vector2 mouseWorld = new Vector2(mouseWorld3D.x, mouseWorld3D.y);

        Vector2 origin = firePoint.position;
        Vector2 direction = mouseWorld - origin;
        float distanceToMouse = direction.magnitude;

        if (distanceToMouse < 0.001f) return;
        direction /= distanceToMouse; // normalize without GC

        // Physics2D.Raycast returns RaycastHit2D struct (zero GC allocation)
        RaycastHit2D hit = Physics2D.Raycast(origin, direction, maxDistance, grappleLayer);

        if (hit.collider != null)
        {
            AttachGrapple(hit.point);
        }
    }

    private void AttachGrapple(Vector2 hitPoint)
    {
        isGrappled = true;
        anchorPoint = hitPoint;

        Vector2 origin = firePoint.position;
        float initialDist = Vector2.Distance(origin, anchorPoint);
        currentRopeLength = Mathf.Clamp(initialDist, minRopeLength, maxRopeLength);

        // Start procedural latch wave
        waveTimer = 0f;
        isWaving = true;
        lineRenderer.enabled = true;
    }

    private void ReleaseGrapple()
    {
        isGrappled = false;
        lineRenderer.enabled = false;

        // Apply directional momentum boost
        Vector2 currentVel = rb.velocity;
        float speed = currentVel.magnitude;

        if (speed > 0.1f)
        {
            Vector2 launchVelocity = currentVel * releaseVelocityMultiplier;

            // Add directional upward boost when moving upward/horizontally
            if (currentVel.y > -2f)
            {
                float verticalFactor = Mathf.Clamp01((currentVel.y + 2f) / 10f);
                launchVelocity.y += releaseUpwardBoost * (1f + verticalFactor);
            }

            // Clamp max velocity to prevent numerical tunneling
            if (launchVelocity.magnitude > maxReleaseVelocity)
            {
                launchVelocity = launchVelocity.normalized * maxReleaseVelocity;
            }

            rb.velocity = launchVelocity;
        }
    }

    #endregion

    #region Physics Simulation & Constraints

    /// <summary>
    /// Lengthens or shortens the rope target length based on W/S inputs.
    /// </summary>
    private void HandleReeling()
    {
        if (Mathf.Abs(verticalInput) < 0.01f) return;

        // Positive verticalInput (W): shorten rope (reel in)
        // Negative verticalInput (S): lengthen rope (let out)
        float reelDelta = -verticalInput * reelSpeed * Time.fixedDeltaTime;
        currentRopeLength = Mathf.Clamp(currentRopeLength + reelDelta, minRopeLength, maxRopeLength);

        // Active reel-in inward impulse to make vertical reeling responsive
        if (verticalInput > 0.01f)
        {
            Vector2 origin = firePoint.position;
            Vector2 toAnchor = anchorPoint - origin;
            float dist = toAnchor.magnitude;
            if (dist > 0.001f)
            {
                Vector2 inwardNormal = toAnchor / dist;
                rb.AddForce(inwardNormal * (reelInImpulse * verticalInput), ForceMode2D.Force);
            }
        }
    }

    /// <summary>
    /// Custom cable/pendulum constraint solver operating directly on Rigidbody2D.
    /// Acts as an inelastic rope: rigid when taut (dist >= currentRopeLength),
    /// but allows complete free movement when slack (dist < currentRopeLength).
    /// </summary>
    private void ApplyPendulumConstraint()
    {
        Vector2 origin = firePoint.position;
        Vector2 delta = origin - anchorPoint; // Anchor -> Player vector
        float currentDist = delta.magnitude;

        if (currentDist < 0.0001f) return;

        Vector2 normal = delta / currentDist; // Unit radial vector pointing outward

        // Constraint only engages when rope is taut
        if (currentDist >= currentRopeLength)
        {
            Vector2 velocity = rb.velocity;
            float radialVelocity = Vector2.Dot(velocity, normal);

            // 1. Velocity Constraint: cancel any outward velocity away from anchor
            if (radialVelocity > 0f)
            {
                // Remove outward radial velocity component
                velocity -= normal * radialVelocity;

                // Apply light tangential damping if configured
                if (swingDamping > 0f)
                {
                    Vector2 tangent = new Vector2(-normal.y, normal.x);
                    float tangentialVelocity = Vector2.Dot(velocity, tangent);
                    tangentialVelocity *= Mathf.Clamp01(1f - swingDamping * Time.fixedDeltaTime);
                    velocity = tangent * tangentialVelocity;
                }

                rb.velocity = velocity;
            }

            // 2. Position Constraint (Baumgarte Stabilization):
            // Firmly prevent rope stretch/sagging beyond ropeLength without bouncy oscillation
            float stretch = currentDist - currentRopeLength;
            if (stretch > 0f)
            {
                Vector2 correction = normal * (stretch * positionCorrectionRate);
                rb.position -= correction;
            }
        }
    }

    /// <summary>
    /// Tangential swing pump force driven by horizontal input (A/D).
    /// </summary>
    private void ApplySwingInput()
    {
        if (Mathf.Abs(horizontalInput) < 0.01f) return;

        Vector2 origin = firePoint.position;
        Vector2 delta = origin - anchorPoint;
        float currentDist = delta.magnitude;

        if (currentDist < 0.0001f) return;

        Vector2 normal = delta / currentDist;

        // Tangent perpendicular to the rope pointing clockwise: (-normal.y, normal.x)
        Vector2 tangent = new Vector2(-normal.y, normal.x);

        // Align tangent direction with horizontal input intent (left vs right)
        // If tangent.x and horizontalInput have opposite signs, flip tangent
        if (Vector2.Dot(tangent, Vector2.right * horizontalInput) < 0f)
        {
            tangent = -tangent;
        }

        // Check if player has already exceeded max swing speed in this direction
        float currentTangentialSpeed = Vector2.Dot(rb.velocity, tangent);
        if (currentTangentialSpeed < maxSwingVelocity)
        {
            float forceMagnitude = swingForce * Mathf.Abs(horizontalInput);
            rb.AddForce(tangent * forceMagnitude, ForceMode2D.Force);
        }
    }

    #endregion

    #region Visuals & Rope Procedural Generation

    /// <summary>
    /// Updates the LineRenderer vertices. Uses a procedural traveling damped sine wave
    /// when latched that naturally decays into a taut straight line.
    /// Strictly zero GC allocations.
    /// </summary>
    private void UpdateRopeVisuals()
    {
        if (!isGrappled) return;

        Vector2 startPoint = firePoint.position;
        Vector2 endPoint = anchorPoint;
        Vector2 lineDir = endPoint - startPoint;
        float lineLength = lineDir.magnitude;

        if (lineLength < 0.0001f) return;

        // Perpendicular vector for transverse wave oscillations
        Vector2 perp = new Vector2(-lineDir.y, lineDir.x) / lineLength;

        // Update latch wave timer
        float waveWeight = 0f;
        if (isWaving)
        {
            waveTimer += Time.deltaTime;
            if (waveTimer >= waveSettleDuration)
            {
                isWaving = false;
            }
            else
            {
                // Smooth cubic dampening factor from 1 down to 0
                float t = waveTimer / waveSettleDuration;
                waveWeight = (1f - t) * (1f - t);
            }
        }

        // Populate pre-allocated buffer
        int count = ropeResolution;
        for (int i = 0; i < count; i++)
        {
            float fraction = (float)i / (count - 1);
            Vector2 basePosition = Vector2.Lerp(startPoint, endPoint, fraction);

            if (isWaving && waveWeight > 0.001f)
            {
                // Boundary envelope: zero displacement at endpoints (fraction 0 and 1)
                float envelope = Mathf.Sin(fraction * Mathf.PI);

                // Traveling sinusoidal phase along rope length
                float phase = (fraction * waveFrequency * Mathf.PI * 2f) - (waveTimer * waveSpeed);
                float displacement = Mathf.Sin(phase) * waveAmplitude * envelope * waveWeight;

                basePosition += perp * displacement;
            }

            ropePositions[i].x = basePosition.x;
            ropePositions[i].y = basePosition.y;
            ropePositions[i].z = 0f;
        }

        // Set positions from preallocated array (zero GC allocation)
        lineRenderer.SetPositions(ropePositions);
    }

    #endregion

    #region Editor Debugging & Gizmos

    private void OnDrawGizmosSelected()
    {
        Transform originTransform = (firePoint != null) ? firePoint : transform;

        // Draw max grapple reach circle
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
        Gizmos.DrawWireSphere(originTransform.position, maxDistance);

        // Draw current anchor and line if in play mode & grappled
        if (Application.isPlaying && isGrappled)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(anchorPoint, 0.25f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(originTransform.position, anchorPoint);

            // Draw current taut radius circle
            Gizmos.color = new Color(1f, 0.92f, 0.016f, 0.2f);
            Gizmos.DrawWireSphere(anchorPoint, currentRopeLength);
        }
    }

    #endregion
}
