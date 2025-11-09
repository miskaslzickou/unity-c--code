
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System;

public class WeaponController : MonoBehaviour
{
    private PlayerControls playerControls;
    private Rigidbody2D rb;
    private PolygonCollider2D playerCollider;

    [Header("Projectile Settings")]
    public float projectileSpeed = 20f;
    public float projectileDamage = 10f;
    public float homingSpeed = 5f;

    public Transform[] firePoints;
    public GameObject blasterShot;

    private Camera mainCamera;

    [Header("Fire Rate Settings")]
    public float fireRate = 0.5f;
    private float nextFireTime = 0f;

    [Header("Homing setting")]
    private bool homing = false;
    private Transform currentHomingTarget;
    public GameObject homingShot;
    public float raycastOffsetFromPlayer = 0.1f;
    public LayerMask homingLayerMask;
    public float maximumHomingRange = 8f;
    public float fireRateHoming = 0.5f;
    private float nextHomingFireTime = 0f;
    [Header("Lock-on Settings")]
    public float lockOnDuration = 4f;
  
    public GameObject lockOnIndicatorObj;

    private void Awake()
    {
        mainCamera = Camera.main;
        rb = GetComponent<Rigidbody2D>();
        playerCollider = GetComponent<PolygonCollider2D>();

        playerControls = new PlayerControls();

        playerControls.Player.Fire.started += ctx =>
        {
          
            if(!homing)
            {
                return;
            }
            if (currentHomingTarget == null)
            {
                StartCoroutine(LockOnTargetCoroutine());
                
            }
            else
            {
                if(Time.time >= nextHomingFireTime)
                {
                    FireHomingShot(firePoints[0]);
                    nextHomingFireTime = Time.time + fireRateHoming;
                }
               
            }
        };

        playerControls.Player.Homingrockets.started += ctx =>
        {
            homing = !homing;
            currentHomingTarget = null;
            lockOnIndicatorObj.SetActive(false); // Hide the lock-on indicator when toggling homing
            lockOnIndicatorObj.transform.parent = null; // Reset parent to avoid following the target

        };

        playerControls.Player.Cancellockontarget.started += ctx =>
        {
            if (homing)
            {
                currentHomingTarget = null;
                lockOnIndicatorObj.SetActive(false);
                lockOnIndicatorObj.transform.parent = null; // Reset parent to avoid following the target

            }
        };
    }

    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Player.Disable();
       
    }

    private IEnumerator LockOnTargetCoroutine()
    {
        Transform foundTarget = null;


       

        foundTarget = PerformTargetRaycast();
        if (foundTarget!=null)
        {   
            lockOnIndicatorObj.transform.parent = foundTarget;
            lockOnIndicatorObj.transform.position = foundTarget.position;
            lockOnIndicatorObj.SetActive(true);
          
        }
        yield return new WaitForSeconds(lockOnDuration);
        currentHomingTarget = foundTarget;

    }

    private Transform PerformTargetRaycast()
    {
        if (mainCamera == null)
        {
            return null;
        }

        Vector2 worldMousePos = mainCamera.ScreenToWorldPoint(Mouse.current.position.ReadValue());
        Vector2 directionFromPlayerToMouse = (worldMousePos - (Vector2)transform.position).normalized;

        Vector2 raycastOrigin = (Vector2)transform.position + directionFromPlayerToMouse * raycastOffsetFromPlayer;

        RaycastHit2D hit = Physics2D.Raycast(raycastOrigin, directionFromPlayerToMouse, maximumHomingRange, homingLayerMask);

        if (hit.collider != null)
        {
            return hit.transform;
        }
        return null;
    }

    private void OnTargetLocked(Transform lockedTarget)
    {
        currentHomingTarget = lockedTarget;
        if (currentHomingTarget != null)
        {
            FireHomingShot(firePoints[0]);
        }
    }

    public void FireBlasterShot(Transform point)
    {
        if (ObjectPoolManager.Instance == null)
        {
            return;
        }

        GameObject projectileGO = ObjectPoolManager.Instance.GetPooledObject(blasterShot, point.position, point.rotation);
        if (projectileGO == null) return;

        ProjectileControl projectile = projectileGO.GetComponent<ProjectileControl>();
        if (projectile != null)
        {
            projectile.Initialize(projectileSpeed, point.up, projectileDamage, blasterShot);
            Physics2D.IgnoreCollision(playerCollider, projectileGO.GetComponent<Collider2D>(), true);
        }
    }

    public void FireHomingShot(Transform point)
    {
        if (ObjectPoolManager.Instance == null)
        {
            return;
        }
        if (currentHomingTarget == null)
        {
            return;
        }

        GameObject projectileGO = ObjectPoolManager.Instance.GetPooledObject(homingShot, point.position, point.rotation);
        if (projectileGO == null) return;

        HomingProjectile homingProjectile = projectileGO.GetComponent<HomingProjectile>();
        if (homingProjectile != null)
        {
            homingProjectile.Initialize(homingSpeed, point.up, projectileDamage, homingShot, currentHomingTarget);
            Physics2D.IgnoreCollision(playerCollider, projectileGO.GetComponent<Collider2D>(), true);
        }
    }

    public void FireBlasterShots()
    {
        if (Time.time < nextFireTime)
        {
            return;
        }

        foreach (Transform firePoint in firePoints)
        {
            FireBlasterShot(firePoint);
        }

        nextFireTime = Time.time + fireRate;
    }

    void FixedUpdate()
    {
        if (!homing && playerControls.Player.Fire.IsPressed() && Time.time >= nextFireTime)
        {
            FireBlasterShots();
        }
    }
}