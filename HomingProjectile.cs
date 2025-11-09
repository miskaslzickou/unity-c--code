using UnityEngine;

public class HomingProjectile : MonoBehaviour
{

    private Rigidbody2D rb;
    private float projectileSpeed;
    private Vector2 projectileDirection;

    public float lifetime = 1.5f;
    private Transform Target; // Cíl pro homing projektily
    Vector2 targetDirection; // Smìr k cíli pro homing
    private GameObject originalPrefab;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void OnEnable()
    {
        if (rb != null)
        {
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }
        CancelInvoke();
        Invoke("DeactivateProjectile", lifetime);
    }

    void OnDisable()
    {
        CancelInvoke();
    }
    public void Initialize(float speed, Vector2 direction, float damage, GameObject prefab, Transform target ) 
    {
        projectileSpeed = speed;
        projectileDirection = direction.normalized;
        // Uložte si referenci na originální prefab
        this.originalPrefab = prefab; // <-- Uložení reference
        Target = target; // Uložení cíle pro homing
        if (rb != null)
        {
            rb.AddForce(projectileDirection * (projectileSpeed*0.3f), ForceMode2D.Impulse);
           
        }
        else
        {
            Debug.LogError("ProjectileController: Rigidbody2D is null!");
        }
    }

  

    // Metoda pro deaktivaci/vrácení objektu do poolu
    private void DeactivateProjectile()
    {
        // NOVINKA: Zavolejte ObjectPoolManager.Instance.ReturnObjectToPool()
        if (originalPrefab != null && ObjectPoolManager.Instance != null)
        {
            ObjectPoolManager.Instance.ReturnObjectToPool(gameObject, originalPrefab);
        }
        else
        {
            // Nouzové øešení, pokud nìco selže (nemìl by se stát s dobøe nastaveným poolingen)
            Debug.LogWarning("Nelze vrátit objekt do poolu. Možná chybí prefab nebo manažer poolu. Nièím.");
            Destroy(gameObject);
        }
    }
    void OnCollisionEnter2D(Collision2D collision) // Opraven název parametru
    {
     
        Debug.Log("Projektil zasáhl (Kolize): " + collision.gameObject.name);

     
        DeactivateProjectile();
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        rb.linearVelocity = transform.up * projectileSpeed;
        Debug.Log(Target);
        if (Target != null)
        {
            
            Vector2 directionToTarget = Target.position - transform.position;

            // 2. Vypoèítáme cílový úhel (ve stupních)
            // Toto je úhel, na který by se mìla transform.up objektu otoèit, aby smìøovala k cíli.
            float targetAngle = Vector2.SignedAngle(Vector2.up, directionToTarget);

            // 3. Vypoèítáme aktuální úhel Rigidbody2D
            // rb.rotation je úhel kolem Z osy (ve stupních)
            float currentAngle = rb.rotation;

            // 4. Plynule interpolujeme mezi aktuálním a cílovým úhlem
            // Mathf.LerpAngle je skvìlá pro interpolaci úhlù, protože správnì zvládá pøechody pøes 360/0 stupòù.
            float newAngle = Mathf.LerpAngle(currentAngle, targetAngle, 20 * Time.fixedDeltaTime);

            // 5. Nastavíme novou rotaci Rigidbody2D
            rb.MoveRotation(newAngle);
        }
    }
}

