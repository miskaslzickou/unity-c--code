using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace UniversalAStarPathfinding
{
    public enum EnemyMovementBehavior
    {
        StandardFollow,     // Smooth walking/running along path waypoints
        TeleporterEnemy,    // Periodically teleports along the A* path toward the target
        ChargingEnemy,      // Slow approach until close, then bursts in speed
        RangedKitingEnemy   // Maintains a preferred distance from the target while pathfinding
    }

    public class UniversalPathfindingAgent : MonoBehaviour
    {
        [Header("Behavior Type")]
        public EnemyMovementBehavior behaviorType = EnemyMovementBehavior.StandardFollow;

        [Header("Target & Movement")]
        public Transform target;
        public float moveSpeed = 5.5f;
        public float waypointReachThreshold = 0.8f;
        public float updateInterval = 0.4f;

        [Header("Teleporter Specific Settings")]
        public float teleportInterval = 3.0f;
        public float teleportRange = 4.0f;
        private float teleportTimer = 0f;

        [Header("Charger Specific Settings")]
        public float chargeDistance = 5.0f;
        public float chargeMultiplier = 2.0f;

        [Header("Ranged Kiting Settings")]
        public float preferredKitingDistance = 4.0f;

        [Header("References")]
        public Tilemap targetTilemap;

        private List<Vector3> currentPath = new List<Vector3>();
        private int currentWaypointIndex = 0;
        private float updateTimer = 0f;
        private Rigidbody2D rb2d;
        private Rigidbody rb3d;
        private SpriteRenderer spriteRenderer;

        void Start()
        {
            if (target == null)
            {
                GameObject playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null) target = playerObj.transform;
            }

            rb2d = GetComponent<Rigidbody2D>();
            rb3d = GetComponent<Rigidbody>();
            spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer == null) spriteRenderer = GetComponentInChildren<SpriteRenderer>();

            if (targetTilemap == null && AStarPathfinder.Instance != null)
            {
                targetTilemap = AStarPathfinder.Instance.obstacleTilemap;
            }

            teleportTimer = teleportInterval;
        }

        void Update()
        {
            if (target == null) return;

            updateTimer -= Time.deltaTime;
            if (updateTimer <= 0f)
            {
                updateTimer = updateInterval;
                RecalculatePath();
            }

            switch (behaviorType)
            {
                case EnemyMovementBehavior.TeleporterEnemy:
                    HandleTeleporterBehavior();
                    break;
                case EnemyMovementBehavior.ChargingEnemy:
                    HandleChargerBehavior();
                    break;
                case EnemyMovementBehavior.RangedKitingEnemy:
                    HandleKitingBehavior();
                    break;
                default:
                    FollowPath(moveSpeed);
                    break;
            }
        }

        void RecalculatePath()
        {
            if (AStarPathfinder.Instance == null) return;
            currentPath = AStarPathfinder.Instance.FindPath(transform.position, target.position, targetTilemap);
            currentWaypointIndex = 0;
        }

        void FollowPath(float currentSpeed)
        {
            if (currentPath == null || currentPath.Count == 0 || currentWaypointIndex >= currentPath.Count)
            {
                StopMovement();
                return;
            }

            Vector3 waypoint = currentPath[currentWaypointIndex];
            float dist = Vector3.Distance(transform.position, waypoint);

            if (dist < waypointReachThreshold)
            {
                currentWaypointIndex++;
                return;
            }

            Vector3 direction = (waypoint - transform.position).normalized;
            ApplyMovement(direction, currentSpeed);
            UpdateFacing(direction.x);
        }

        void HandleTeleporterBehavior()
        {
            teleportTimer -= Time.deltaTime;
            if (teleportTimer <= 0f)
            {
                teleportTimer = teleportInterval;

                if (currentPath != null && currentPath.Count > 0)
                {
                    int targetIndex = Mathf.Min(currentWaypointIndex + 2, currentPath.Count - 1);
                    Vector3 jumpTarget = currentPath[targetIndex];

                    if (Vector3.Distance(transform.position, jumpTarget) <= teleportRange)
                    {
                        transform.position = jumpTarget;
                        currentWaypointIndex = targetIndex + 1;
                    }
                }
            }
            else
            {
                FollowPath(moveSpeed);
            }
        }

        void HandleChargerBehavior()
        {
            float distToTarget = Vector3.Distance(transform.position, target.position);
            float currentSpeed = moveSpeed;

            if (distToTarget <= chargeDistance)
            {
                currentSpeed *= chargeMultiplier;
            }

            FollowPath(currentSpeed);
        }

        void HandleKitingBehavior()
        {
            float distToTarget = Vector3.Distance(transform.position, target.position);

            if (currentPath == null || currentPath.Count == 0) return;

            Vector3 direction = (currentPath[currentWaypointIndex] - transform.position).normalized;

            if (distToTarget < preferredKitingDistance - 0.5f)
            {
                ApplyMovement(-direction, moveSpeed);
            }
            else if (distToTarget > preferredKitingDistance + 0.5f)
            {
                FollowPath(moveSpeed);
            }
            else
            {
                StopMovement();
            }
        }

        void ApplyMovement(Vector3 direction, float speed)
        {
            if (rb2d != null)
            {
                if (AStarPathfinder.Instance.dimension == PathfindingDimension.Platformer2D)
                {
                    rb2d.linearVelocity = new Vector2(direction.x * speed, rb2d.linearVelocity.y);
                    if (direction.y > 0.5f && Mathf.Abs(rb2d.linearVelocity.y) < 0.05f)
                    {
                        rb2d.linearVelocity = new Vector2(rb2d.linearVelocity.x, 7f);
                    }
                }
                else
                {
                    rb2d.linearVelocity = new Vector2(direction.x * speed, direction.y * speed);
                }
            }
            else if (rb3d != null)
            {
                rb3d.linearVelocity = new Vector3(direction.x * speed, rb3d.linearVelocity.y, direction.z * speed);
            }
            else
            {
                transform.position += direction * speed * Time.deltaTime;
            }
        }

        void UpdateFacing(float dirX)
        {
            if (Mathf.Abs(dirX) > 0.05f && spriteRenderer != null)
            {
                spriteRenderer.flipX = dirX < 0f;
            }
        }

        void StopMovement()
        {
            if (rb2d != null) rb2d.linearVelocity = new Vector2(0, rb2d.linearVelocity.y);
            if (rb3d != null) rb3d.linearVelocity = new Vector3(0, rb3d.linearVelocity.y, 0);
        }
    }
}
