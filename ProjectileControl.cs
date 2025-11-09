using UnityEngine;
using System.Collections; // Pro Invoke a Coroutines

[RequireComponent(typeof(Rigidbody2D))]
public class ProjectileControl : MonoBehaviour
{
    private Rigidbody2D rb;
    private float projectileSpeed;
    private Vector2 projectileDirection;
    // Pøíklad poškození - v Initialize se nastavuje, ale zde není promìnná pro uložení. Mùžeš pøidat:
    // private float projectileDamage; 

    public float lifetime = 1.5f;

    // NOVINKA: Zde budeme uchovávat referenci na originální prefab
    // aby ObjectPoolManager vìdìl, do kterého poolu objekt vrátit.
    private GameObject originalPrefab;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    // OnEnable se volá pokaždé, když se objekt aktivuje (vytvoøí nebo vytáhne z poolu)
    void OnEnable()
    {
        if (rb != null)
        {
            // Resetování rychlosti pøi aktivaci je dobré
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }

        // Zrušíme pøípadné pøedchozí Invoke a nastavíme nové pro životnost
        CancelInvoke("DeactivateProjectile"); // Ujisti se, že zrušíš konkrétní metodu
        Invoke("DeactivateProjectile", lifetime);
    }

    // OnDisable se volá, když se objekt deaktivuje (vrací do poolu nebo nièí)
    void OnDisable()
    {
        // Zrušíme všechny Invoke volání, aby se nespouštìly po vrácení do poolu
        CancelInvoke("DeactivateProjectile");
    }

    /// <summary>
    /// Inicializuje projektil s jeho parametry a referencí na originální prefab.
    /// </summary>
    /// <param name="speed">Rychlost projektilu.</param>
    /// <param name="direction">Smìr letu projektilu.</param>
    /// <param name="damage">Poškození zpùsobené projektilem.</param>
    /// <param name="prefab">Originální prefab tohoto projektilu, pro návrat do poolu.</param>
    public void Initialize(float speed, Vector2 direction, float damage, GameObject prefab)
    {
        projectileSpeed = speed;
        projectileDirection = direction.normalized;
        // projectileDamage = damage; // Pokud pøidáš privátní promìnnou pro damage

        this.originalPrefab = prefab; // Uložení reference na originální prefab

        if (rb != null)
        {
            rb.linearVelocity = projectileDirection * projectileSpeed;
        }
        else
        {
            Debug.LogError("ProjectileController: Rigidbody2D is null on " + gameObject.name + "!");
        }
    }

   
    // --- Volitelné: Pokud používáš pevné kolize místo Triggerù ---
    // Tato metoda se volá, když se Collider2D objektu srazí s jiným Collider2D (fyzická kolize).
    // Používej jen jednu z nich (OnTriggerEnter2D NEBO OnCollisionEnter2D) podle toho, jak máš nastaven Collider.
    void OnCollisionEnter2D(Collision2D collision) // Opraven název parametru
    {
        // Vyluè, aby projektil detekoval kolizi sám se sebou nebo s objektem, ze kterého byl vystøelen
        // if (collision.gameObject.CompareTag("Player") || collision.gameObject.CompareTag("Projectile")) return;

        Debug.Log("Projektil zasáhl (Kolize): " + collision.gameObject.name);

        // Zde mùžete pøidat logiku poškození cíle
        // Enemy enemy = collision.gameObject.GetComponent<Enemy>();
        // if (enemy != null)
        // {
        //     enemy.TakeDamage(projectileDamage);
        // }

        // Okamžitá deaktivace projektilu po kolizi
        DeactivateProjectile();
    }


    // Metoda pro deaktivaci/vrácení objektu do poolu
    private void DeactivateProjectile()
    {
        // Pøed vrácením do poolu vypneme GameObject
        // To je dùležité, protože pool pracuje s neaktivními objekty
        gameObject.SetActive(false);

        // Zavolejte ObjectPoolManager.Instance.ReturnObjectToPool()
        if (originalPrefab != null && ObjectPoolManager.Instance != null)
        {
            ObjectPoolManager.Instance.ReturnObjectToPool(gameObject, originalPrefab);
        }
        else
        {
            // Nouzové øešení, pokud nìco selže (nemìl by se stát s dobøe nastaveným poolingem)
            Debug.LogWarning("Nelze vrátit objekt do poolu. Možná chybí prefab nebo manažer poolu. Nièím pøímo.", this);
            Destroy(gameObject);
        }
    }
}