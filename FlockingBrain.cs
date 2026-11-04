using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Boids-style flocking AI. Works in 2D and 3D automatically.
/// Drop on any agent (Rigidbody/Rigidbody2D or just a Transform).
/// Uses: bird flocks, fish schools, zombie swarms, drone squads, crowd extras.
/// </summary>
[DisallowMultipleComponent]
public class FlockingBrain : MonoBehaviour
{
    public enum MoveMode { Transform, Rigidbody3D, Rigidbody2D }

    [Header("Movement")]
    public MoveMode mode = MoveMode.Transform;
    public float maxSpeed = 4f;
    public float maxSteerForce = 8f;

    [Header("Boid Weights")]
    public float separationWeight = 1.5f;
    public float alignmentWeight = 1f;
    public float cohesionWeight = 1f;

    [Header("Ranges")]
    public float neighborRadius = 3f;
    public float separationRadius = 1.2f;

    [Header("Optional Target Attraction (food / player / goal)")]
    public Transform attractor;
    public float attractorWeight = 0f;

    [Header("Obstacle Avoidance (3D raycast / 2D circlecast)")]
    public float obstacleAvoidWeight = 2f;
    public float obstacleLookAhead = 2f;
    public LayerMask obstacleMask = ~0;

    [Header("Debug")]
    public bool drawGizmos = false;

    static readonly List<FlockingBrain> all = new List<FlockingBrain>();
    readonly List<FlockingBrain> neighbors = new List<FlockingBrain>();

    Rigidbody rb3d;
    Rigidbody2D rb2d;
    Vector3 velocity;
    bool is2D;

    void Awake()
    {
        rb3d = GetComponent<Rigidbody>();
        rb2d = GetComponent<Rigidbody2D>();
        is2D = rb2d != null && rb3d == null;
        if (rb3d != null) mode = MoveMode.Rigidbody3D;
        else if (rb2d != null) mode = MoveMode.Rigidbody2D;
    }

    void OnEnable() { all.Add(this); }
    void OnDisable() { all.Remove(this); }

    void FixedUpdate()
    {
        FindNeighbors();

        Vector3 accel = Vector3.zero;
        accel += SteerTowards(Separation().normalized) * separationWeight;
        accel += SteerTowards(Alignment().normalized) * alignmentWeight;
        accel += SteerTowards(Cohesion().normalized) * cohesionWeight;
        if (attractor != null && attractorWeight > 0f)
            accel += SteerTowards(((Vector3)DirectionTo(attractor.position)).normalized) * attractorWeight;
        accel += AvoidObstacles() * obstacleAvoidWeight;

        velocity = Vector3.ClampMagnitude(velocity + accel * Time.fixedDeltaTime, maxSpeed);
        ApplyVelocity();
    }

    void FindNeighbors()
    {
        neighbors.Clear();
        float sqr = neighborRadius * neighborRadius;
        for (int i = 0; i < all.Count; i++)
        {
            var other = all[i];
            if (other == this) continue;
            if ((other.transform.position - transform.position).sqrMagnitude < sqr)
                neighbors.Add(other);
        }
    }

    Vector3 Separation()
    {
        Vector3 steer = Vector3.zero;
        int count = 0;
        for (int i = 0; i < neighbors.Count; i++)
        {
            Vector3 diff = transform.position - neighbors[i].transform.position;
            float d = diff.magnitude;
            if (d > 0f && d < separationRadius)
            {
                steer += diff / (d * d); // closer neighbors push harder
                count++;
            }
        }
        return count > 0 ? steer / count : Vector3.zero;
    }

    Vector3 Alignment()
    {
        if (neighbors.Count == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < neighbors.Count; i++) sum += neighbors[i].velocity;
        return sum / neighbors.Count;
    }

    Vector3 Cohesion()
    {
        if (neighbors.Count == 0) return Vector3.zero;
        Vector3 center = Vector3.zero;
        for (int i = 0; i < neighbors.Count; i++) center += neighbors[i].transform.position;
        center /= neighbors.Count;
        return (Vector3)DirectionTo(center);
    }

    Vector3 AvoidObstacles()
    {
        if (obstacleAvoidWeight <= 0f || velocity.sqrMagnitude < 0.01f) return Vector3.zero;
        Vector3 dir = velocity.normalized;
        if (is2D)
        {
            var hit = Physics2D.CircleCast(transform.position, 0.4f, dir, obstacleLookAhead, obstacleMask);
            if (hit.collider != null && hit.collider.transform != transform)
            {
                Vector2 away = ((Vector2)transform.position - hit.point).normalized;
                return new Vector3(away.x, away.y, 0f) * maxSteerForce;
            }
        }
        else
        {
            if (Physics.Raycast(transform.position, dir, out RaycastHit hit, obstacleLookAhead, obstacleMask)
                && hit.transform != transform)
            {
                return Vector3.Reflect(dir, hit.normal) * maxSteerForce;
            }
        }
        return Vector3.zero;
    }

    Vector3 SteerTowards(Vector3 desiredDir)
    {
        Vector3 desired = desiredDir * maxSpeed - velocity;
        return Vector3.ClampMagnitude(desired, maxSteerForce);
    }

    void ApplyVelocity()
    {
        switch (mode)
        {
            case MoveMode.Rigidbody3D:
                if (rb3d != null) rb3d.linearVelocity = velocity;
                break;
            case MoveMode.Rigidbody2D:
                if (rb2d != null) rb2d.linearVelocity = new Vector2(velocity.x, velocity.y);
                break;
            default:
                transform.position += velocity * Time.fixedDeltaTime;
                break;
        }

        if (velocity.sqrMagnitude > 0.05f)
        {
            if (is2D)
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg - 90f);
            else
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(velocity.normalized), 5f * Time.fixedDeltaTime);
        }
    }

    Vector2 DirectionTo(Vector3 worldPos)
    {
        Vector3 d = worldPos - transform.position;
        return is2D ? new Vector2(d.x, d.y) : d;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, neighborRadius);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, separationRadius);
    }
}
