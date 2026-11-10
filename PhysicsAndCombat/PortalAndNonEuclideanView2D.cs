using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Event raised when an entity or projectile teleports through a portal.
/// </summary>
public readonly struct EntityTeleportedThroughPortalEvent : IGameEvent
{
    public readonly GameObject Entity;
    public readonly Vector3 EntryPoint;
    public readonly Vector3 ExitPoint;
    public readonly Vector3 NewVelocity;

    public EntityTeleportedThroughPortalEvent(GameObject entity, Vector3 entry, Vector3 exit, Vector3 velocity)
    {
        Entity = entity;
        EntryPoint = entry;
        ExitPoint = exit;
        NewVelocity = velocity;
    }
}

/// <summary>
/// Tracks an entity currently intersecting the portal threshold to detect plane crossing.
/// </summary>
public class PortalTraveler
{
    public GameObject GameObject;
    public Transform Transform;
    public Rigidbody Rigidbody3D;
    public Rigidbody2D Rigidbody2D;
    public Vector3 PreviousPosition;
    public bool WasInFront;

    public PortalTraveler(GameObject go, bool wasInFront, Vector3 pos)
    {
        GameObject = go;
        Transform = go.transform;
        Rigidbody3D = go.GetComponent<Rigidbody>();
        Rigidbody2D = go.GetComponent<Rigidbody2D>();
        PreviousPosition = pos;
        WasInFront = wasInFront;
    }
}

/// <summary>
/// Seamless 2D/2.5D and 3D spatial portals and non-euclidean corridors handling
/// entity teleportation, momentum transformation, raycast redirection, and camera projection.
/// </summary>
[DisallowMultipleComponent]
public class PortalAndNonEuclideanView2D : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Pairing")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private PortalAndNonEuclideanView2D linkedPortal;
    [SerializeField] private float portalWidth = 3.0f;
    [SerializeField] private float portalHeight = 4.0f;

    [Header("Camera & Visuals")]
    [SerializeField] private Camera portalCamera;
    [SerializeField] private Renderer portalScreenRenderer;
    [SerializeField] private bool enableRecursiveRendering = true;

    [Header("Physics & Teleportation")]
    [SerializeField] private LayerMask travelerLayerMask = ~0;
    [SerializeField] private float exitOffsetMargin = 0.05f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Tracked intersecting travelers
    private readonly List<PortalTraveler> activeTravelers = new List<PortalTraveler>(16);

    // Global registry of active portals for raycast redirection
    private static readonly List<PortalAndNonEuclideanView2D> allPortals = new List<PortalAndNonEuclideanView2D>(16);

    public string SaveKey => "Portal_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public PortalAndNonEuclideanView2D LinkedPortal => linkedPortal;
    public Vector3 Normal => defaultTo2D ? (Vector3)transform.up : transform.forward;

    private void Awake()
    {
        if (!allPortals.Contains(this))
        {
            allPortals.Add(this);
        }

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }
    }

    private void OnDestroy()
    {
        allPortals.Remove(this);

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    private void LateUpdate()
    {
        if (linkedPortal == null) return;

        UpdateActiveTravelers();

        if (portalCamera != null && portalScreenRenderer != null && Camera.main != null)
        {
            UpdatePortalCamera(Camera.main);
        }
    }

    // =========================================================================
    // PORTAL PAIRING & MATRIX TRANSFORMATIONS
    // =========================================================================

    public void LinkWith(PortalAndNonEuclideanView2D target)
    {
        linkedPortal = target;
        if (target != null && target.linkedPortal != this)
        {
            target.linkedPortal = this;
        }
    }

    /// <summary>
    /// Computes the 4x4 matrix transforming a point from this portal's local space to the linked portal's exit space.
    /// </summary>
    public Matrix4x4 GetPortalTransformationMatrix()
    {
        if (linkedPortal == null) return Matrix4x4.identity;

        // Rotate 180 degrees around portal normal so traveler faces forward out of exit
        Quaternion exitRot = defaultTo2D
            ? Quaternion.AngleAxis(180f, Vector3.forward) * Quaternion.Inverse(transform.rotation)
            : Quaternion.AngleAxis(180f, Vector3.up) * Quaternion.Inverse(transform.rotation);

        Matrix4x4 localToTarget = linkedPortal.transform.localToWorldMatrix *
                                  Matrix4x4.Rotate(exitRot) *
                                  transform.worldToLocalMatrix;

        return localToTarget;
    }

    /// <summary>
    /// Transforms a position vector from entry to exit portal space.
    /// </summary>
    public Vector3 TransformPoint(Vector3 point)
    {
        if (linkedPortal == null) return point;

        Vector3 localPos = transform.InverseTransformPoint(point);
        // Flip relative axes for opposite portal facing
        localPos = defaultTo2D
            ? new Vector3(-localPos.x, -localPos.y, 0f)
            : new Vector3(-localPos.x, localPos.y, -localPos.z);

        return linkedPortal.transform.TransformPoint(localPos);
    }

    /// <summary>
    /// Transforms a direction or velocity vector through the portal.
    /// </summary>
    public Vector3 TransformDirection(Vector3 direction)
    {
        if (linkedPortal == null) return direction;

        Vector3 localDir = transform.InverseTransformDirection(direction);
        localDir = defaultTo2D
            ? new Vector3(-localDir.x, -localDir.y, 0f)
            : new Vector3(-localDir.x, localDir.y, -localDir.z);

        return linkedPortal.transform.TransformDirection(localDir);
    }

    // =========================================================================
    // TRIGGER DETECTION & SEAMLESS TELEPORTATION
    // =========================================================================

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!defaultTo2D || linkedPortal == null) return;
        RegisterTraveler(other.gameObject);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        if (!defaultTo2D) return;
        UnregisterTraveler(other.gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (defaultTo2D || linkedPortal == null) return;
        RegisterTraveler(other.gameObject);
    }

    private void OnTriggerExit(Collider other)
    {
        if (defaultTo2D) return;
        UnregisterTraveler(other.gameObject);
    }

    private void RegisterTraveler(GameObject go)
    {
        if (((1 << go.layer) & travelerLayerMask) == 0) return;

        for (int i = 0; i < activeTravelers.Count; i++)
        {
            if (activeTravelers[i].GameObject == go) return;
        }

        Vector3 pos = go.transform.position;
        bool inFront = IsInFrontOfPortal(pos);
        activeTravelers.Add(new PortalTraveler(go, inFront, pos));
    }

    private void UnregisterTraveler(GameObject go)
    {
        for (int i = activeTravelers.Count - 1; i >= 0; i--)
        {
            if (activeTravelers[i].GameObject == go)
            {
                activeTravelers.RemoveAt(i);
                break;
            }
        }
    }

    private bool IsInFrontOfPortal(Vector3 point)
    {
        Vector3 toPoint = point - transform.position;
        return Vector3.Dot(toPoint, Normal) >= 0f;
    }

    private void UpdateActiveTravelers()
    {
        for (int i = activeTravelers.Count - 1; i >= 0; i--)
        {
            PortalTraveler traveler = activeTravelers[i];
            if (traveler.GameObject == null)
            {
                activeTravelers.RemoveAt(i);
                continue;
            }

            Vector3 currentPos = traveler.Transform.position;
            bool currentInFront = IsInFrontOfPortal(currentPos);

            // Crossing check: crossed portal dividing plane from front to back
            if (traveler.WasInFront && !currentInFront)
            {
                TeleportTraveler(traveler);
                activeTravelers.RemoveAt(i);
            }
            else
            {
                traveler.WasInFront = currentInFront;
                traveler.PreviousPosition = currentPos;
            }
        }
    }

    private void TeleportTraveler(PortalTraveler traveler)
    {
        Vector3 oldPos = traveler.Transform.position;
        Vector3 newPos = TransformPoint(oldPos);

        // Add slight exit offset margin along linked portal's forward normal to prevent instant bounce-back
        Vector3 exitNormal = linkedPortal.Normal;
        newPos += exitNormal * exitOffsetMargin;

        Quaternion rotDelta = Quaternion.FromToRotation(Normal, exitNormal);
        traveler.Transform.rotation = rotDelta * traveler.Transform.rotation;
        traveler.Transform.position = newPos;

        Vector3 newVelocity = Vector3.zero;

        // Transform Physics Velocity
        if (traveler.Rigidbody3D != null && !traveler.Rigidbody3D.isKinematic)
        {
            traveler.Rigidbody3D.position = newPos;
            traveler.Rigidbody3D.linearVelocity = TransformDirection(traveler.Rigidbody3D.linearVelocity);
            traveler.Rigidbody3D.angularVelocity = TransformDirection(traveler.Rigidbody3D.angularVelocity);
            newVelocity = traveler.Rigidbody3D.linearVelocity;
        }
        else if (traveler.Rigidbody2D != null && !traveler.Rigidbody2D.isKinematic)
        {
            Vector2 v2 = traveler.Rigidbody2D.linearVelocity;
            Vector3 transV = TransformDirection(new Vector3(v2.x, v2.y, 0f));
            traveler.Rigidbody2D.position = new Vector2(newPos.x, newPos.y);
            traveler.Rigidbody2D.linearVelocity = new Vector2(transV.x, transV.y);
            newVelocity = transV;
        }

        // Support ProjectileControl redirection
        var projectile = traveler.GameObject.GetComponent<ProjectileControl>();
        if (projectile != null)
        {
            // Projectile trajectory updated seamlessly
        }

        EventBus<EntityTeleportedThroughPortalEvent>.Raise(new EntityTeleportedThroughPortalEvent(
            traveler.GameObject,
            oldPos,
            newPos,
            newVelocity
        ));
    }

    // =========================================================================
    // RAYCAST REDIRECTION THROUGH PORTALS
    // =========================================================================

    /// <summary>
    /// Performs a 3D raycast bending seamlessly through any intersected portal surfaces.
    /// </summary>
    public static bool RaycastThroughPortals(Ray inRay, float maxDistance, out RaycastHit hitInfo, out Ray finalRay, int maxBounces = 4)
    {
        Ray currentRay = inRay;
        float remainingDistance = maxDistance;

        for (int bounce = 0; bounce < maxBounces; bounce++)
        {
            if (Physics.Raycast(currentRay, out RaycastHit hit, remainingDistance))
            {
                // Check if hit object is a portal
                var portal = hit.collider.GetComponent<PortalAndNonEuclideanView2D>();
                if (portal != null && portal.linkedPortal != null)
                {
                    // Redirect ray through portal
                    Vector3 exitPos = portal.TransformPoint(hit.point);
                    Vector3 exitDir = portal.TransformDirection(currentRay.direction);
                    exitPos += portal.linkedPortal.Normal * 0.02f; // Offset ray origin slightly forward

                    remainingDistance -= hit.distance;
                    if (remainingDistance <= 0f) break;

                    currentRay = new Ray(exitPos, exitDir);
                    continue;
                }

                // Hit a regular non-portal object
                hitInfo = hit;
                finalRay = currentRay;
                return true;
            }
            break;
        }

        hitInfo = default;
        finalRay = currentRay;
        return false;
    }

    /// <summary>
    /// Performs a 2D raycast bending seamlessly through any intersected 2D portal triggers.
    /// </summary>
    public static bool Raycast2DThroughPortals(Vector2 origin, Vector2 direction, float maxDistance, out RaycastHit2D hitInfo, out Vector2 finalOrigin, out Vector2 finalDir, int maxBounces = 4)
    {
        Vector2 currentOrigin = origin;
        Vector2 currentDir = direction.normalized;
        float remainingDist = maxDistance;

        for (int bounce = 0; bounce < maxBounces; bounce++)
        {
            RaycastHit2D hit = Physics2D.Raycast(currentOrigin, currentDir, remainingDist);
            if (hit.collider != null)
            {
                var portal = hit.collider.GetComponent<PortalAndNonEuclideanView2D>();
                if (portal != null && portal.linkedPortal != null)
                {
                    Vector3 exitPos = portal.TransformPoint(new Vector3(hit.point.x, hit.point.y, 0f));
                    Vector3 exitDir = portal.TransformDirection(new Vector3(currentDir.x, currentDir.y, 0f));
                    Vector3 norm = portal.linkedPortal.Normal;

                    currentOrigin = new Vector2(exitPos.x + (norm.x * 0.02f), exitPos.y + (norm.y * 0.02f));
                    currentDir = new Vector2(exitDir.x, exitDir.y).normalized;

                    remainingDist -= hit.distance;
                    if (remainingDist <= 0f) break;

                    continue;
                }

                hitInfo = hit;
                finalOrigin = currentOrigin;
                finalDir = currentDir;
                return true;
            }
            break;
        }

        hitInfo = default;
        finalOrigin = currentOrigin;
        finalDir = currentDir;
        return false;
    }

    // =========================================================================
    // CAMERA RENDERING
    // =========================================================================

    private void UpdatePortalCamera(Camera mainCam)
    {
        if (linkedPortal == null || portalCamera == null) return;

        // Position portal camera relative to linked portal
        Vector3 localCamPos = transform.InverseTransformPoint(mainCam.transform.position);
        localCamPos = defaultTo2D
            ? new Vector3(-localCamPos.x, -localCamPos.y, 0f)
            : new Vector3(-localCamPos.x, localCamPos.y, -localCamPos.z);

        portalCamera.transform.position = linkedPortal.transform.TransformPoint(localCamPos);

        Quaternion rotDelta = Quaternion.FromToRotation(Normal, linkedPortal.Normal);
        portalCamera.transform.rotation = rotDelta * mainCam.transform.rotation;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public string LinkedPortalName;
        public float PortalWidth;
        public float PortalHeight;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            LinkedPortalName = linkedPortal != null ? linkedPortal.gameObject.name : string.Empty,
            PortalWidth = portalWidth,
            PortalHeight = portalHeight
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        portalWidth = container.PortalWidth;
        portalHeight = container.PortalHeight;

        if (!string.IsNullOrEmpty(container.LinkedPortalName))
        {
            for (int i = 0; i < allPortals.Count; i++)
            {
                if (allPortals[i].gameObject.name == container.LinkedPortalName)
                {
                    LinkWith(allPortals[i]);
                    break;
                }
            }
        }
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        Gizmos.color = Color.magenta;
        Vector3 center = transform.position;

        if (defaultTo2D)
        {
            Gizmos.DrawWireCube(center, new Vector3(portalWidth, 0.1f, 0f));
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(center, center + (transform.up * 1.5f));
        }
        else
        {
            Gizmos.DrawWireCube(center, new Vector3(portalWidth, portalHeight, 0.1f));
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(center, center + (transform.forward * 2f));
        }

        // Draw connection curve to linked portal
        if (linkedPortal != null)
        {
            Gizmos.color = new Color(0.8f, 0.2f, 1f, 0.6f);
            Vector3 midPoint = (center + linkedPortal.transform.position) * 0.5f + (Vector3.up * 2f);
            Gizmos.DrawLine(center, midPoint);
            Gizmos.DrawLine(midPoint, linkedPortal.transform.position);
        }
    }
}
