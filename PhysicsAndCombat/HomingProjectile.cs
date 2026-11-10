using UnityEngine;

public class HomingProjectile : MonoBehaviour
{

    private Rigidbody2D rb;
    private float projectileSpeed;
    private Vector2 projectileDirection;

    public float lifetime = 1.5f;
    private Transform Target; // C�l pro homing projektily
    Vector2 targetDirection; // Sm�r k c�li pro homing
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
        // Ulo�te si referenci na origin�ln� prefab
        this.originalPrefab = prefab; // <-- Ulo�en� reference
        Target = target; // Ulo�en� c�le pro homing
        if (rb != null)
        {
            rb.AddForce(projectileDirection * (projectileSpeed*0.3f), ForceMode2D.Impulse);
           
        }
        else
        {
            Debug.LogError("ProjectileController: Rigidbody2D is null!");
        }
    }

  

    // Metoda pro deaktivaci/vr�cen� objektu do poolu
    private void DeactivateProjectile()
    {
        // NOVINKA: Zavolejte ObjectPoolManager.Instance.ReturnObjectToPool()
        if (originalPrefab != null && ObjectPoolManager.Instance != null)
        {
            ObjectPoolManager.Instance.ReturnObjectToPool(gameObject, originalPrefab);
        }
        else
        {
            // Nouzov� �e�en�, pokud n�co sel�e (nem�l by se st�t s dob�e nastaven�m poolingen)
            Debug.LogWarning("Nelze vr�tit objekt do poolu. Mo�n� chyb� prefab nebo mana�er poolu. Ni��m.");
            Destroy(gameObject);
        }
    }
    void OnCollisionEnter2D(Collision2D collision) // Opraven n�zev parametru
    {
     
        Debug.Log("Projektil zas�hl (Kolize): " + collision.gameObject.name);

     
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

            // 2. Vypo��t�me c�lov� �hel (ve stupn�ch)
            // Toto je �hel, na kter� by se m�la transform.up objektu oto�it, aby sm��ovala k c�li.
            float targetAngle = Vector2.SignedAngle(Vector2.up, directionToTarget);

            // 3. Vypo��t�me aktu�ln� �hel Rigidbody2D
            // rb.rotation je �hel kolem Z osy (ve stupn�ch)
            float currentAngle = rb.rotation;

            // 4. Plynule interpolujeme mezi aktu�ln�m a c�lov�m �hlem
            // Mathf.LerpAngle je skv�l� pro interpolaci �hl�, proto�e spr�vn� zvl�d� p�echody p�es 360/0 stup��.
            float newAngle = Mathf.LerpAngle(currentAngle, targetAngle, 20 * Time.fixedDeltaTime);

            // 5. Nastav�me novou rotaci Rigidbody2D
            rb.MoveRotation(newAngle);
        }
    }
}

