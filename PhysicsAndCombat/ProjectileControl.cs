using UnityEngine;
using System.Collections; // Pro Invoke a Coroutines

[RequireComponent(typeof(Rigidbody2D))]
public class ProjectileControl : MonoBehaviour
{
    private Rigidbody2D rb;
    private float projectileSpeed;
    private Vector2 projectileDirection;
    // P��klad po�kozen� - v Initialize se nastavuje, ale zde nen� prom�nn� pro ulo�en�. M��e� p�idat:
    // private float projectileDamage; 

    public float lifetime = 1.5f;

    // NOVINKA: Zde budeme uchov�vat referenci na origin�ln� prefab
    // aby ObjectPoolManager v�d�l, do kter�ho poolu objekt vr�tit.
    private GameObject originalPrefab;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    // OnEnable se vol� poka�d�, kdy� se objekt aktivuje (vytvo�� nebo vyt�hne z poolu)
    void OnEnable()
    {
        if (rb != null)
        {
            // Resetov�n� rychlosti p�i aktivaci je dobr�
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }

        // Zru��me p��padn� p�edchoz� Invoke a nastav�me nov� pro �ivotnost
        CancelInvoke("DeactivateProjectile"); // Ujisti se, �e zru�� konkr�tn� metodu
        Invoke("DeactivateProjectile", lifetime);
    }

    // OnDisable se vol�, kdy� se objekt deaktivuje (vrac� do poolu nebo ni��)
    void OnDisable()
    {
        // Zru��me v�echny Invoke vol�n�, aby se nespou�t�ly po vr�cen� do poolu
        CancelInvoke("DeactivateProjectile");
    }

    /// <summary>
    /// Inicializuje projektil s jeho parametry a referenc� na origin�ln� prefab.
    /// </summary>
    /// <param name="speed">Rychlost projektilu.</param>
    /// <param name="direction">Sm�r letu projektilu.</param>
    /// <param name="damage">Po�kozen� zp�soben� projektilem.</param>
    /// <param name="prefab">Origin�ln� prefab tohoto projektilu, pro n�vrat do poolu.</param>
    public void Initialize(float speed, Vector2 direction, float damage, GameObject prefab)
    {
        projectileSpeed = speed;
        projectileDirection = direction.normalized;
        // projectileDamage = damage; // Pokud p�id� priv�tn� prom�nnou pro damage

        this.originalPrefab = prefab; // Ulo�en� reference na origin�ln� prefab

        if (rb != null)
        {
            rb.linearVelocity = projectileDirection * projectileSpeed;
        }
        else
        {
            Debug.LogError("ProjectileController: Rigidbody2D is null on " + gameObject.name + "!");
        }
    }

   
    // --- Voliteln�: Pokud pou��v� pevn� kolize m�sto Trigger� ---
    // Tato metoda se vol�, kdy� se Collider2D objektu sraz� s jin�m Collider2D (fyzick� kolize).
    // Pou��vej jen jednu z nich (OnTriggerEnter2D NEBO OnCollisionEnter2D) podle toho, jak m� nastaven Collider.
    void OnCollisionEnter2D(Collision2D collision) // Opraven n�zev parametru
    {
        // Vylu�, aby projektil detekoval kolizi s�m se sebou nebo s objektem, ze kter�ho byl vyst�elen
        // if (collision.gameObject.CompareTag("Player") || collision.gameObject.CompareTag("Projectile")) return;

        Debug.Log("Projektil zas�hl (Kolize): " + collision.gameObject.name);

        // Zde m��ete p�idat logiku po�kozen� c�le
        // Enemy enemy = collision.gameObject.GetComponent<Enemy>();
        // if (enemy != null)
        // {
        //     enemy.TakeDamage(projectileDamage);
        // }

        // Okam�it� deaktivace projektilu po kolizi
        DeactivateProjectile();
    }


    // Metoda pro deaktivaci/vr�cen� objektu do poolu
    private void DeactivateProjectile()
    {
        // P�ed vr�cen�m do poolu vypneme GameObject
        // To je d�le�it�, proto�e pool pracuje s neaktivn�mi objekty
        gameObject.SetActive(false);

        // Zavolejte ObjectPoolManager.Instance.ReturnObjectToPool()
        if (originalPrefab != null && ObjectPoolManager.Instance != null)
        {
            ObjectPoolManager.Instance.ReturnObjectToPool(gameObject, originalPrefab);
        }
        else
        {
            // Nouzov� �e�en�, pokud n�co sel�e (nem�l by se st�t s dob�e nastaven�m poolingem)
            Debug.LogWarning("Nelze vr�tit objekt do poolu. Mo�n� chyb� prefab nebo mana�er poolu. Ni��m p��mo.", this);
            Destroy(gameObject);
        }
    }
}