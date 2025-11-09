using System.Collections.Generic;
using UnityEngine;

// Tøída pro nastavení jednotlivých poolù v Inspectoru
[System.Serializable]
public class Pool
{
    public GameObject prefab;
    public int size; // Poèet objektù, které se mají pøedvytvoøit do poolu
    public Transform parent; // Rodièovský transform pro poolované objekty (váš "Pool" GameObject)
}

public class ObjectPoolManager : MonoBehaviour
{
    public static ObjectPoolManager Instance { get; private set; } // Singleton pattern pro snadný pøístup

    public List<Pool> pools; // Seznam všech poolù, které chceme spravovat

    // Slovník pro ukládání samotných poolù (klíè: prefab, hodnota: seznam dostupných objektù)
    private Dictionary<GameObject, Queue<GameObject>> poolDictionary;

    void Awake()
    {
        // Implementace Singletonu
        if (Instance == null)
        {
            Instance = this;
            // DontDestroyOnLoad(gameObject); // Pokud chcete, aby pool pøežil naèítání scén
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        poolDictionary = new Dictionary<GameObject, Queue<GameObject>>();

        // Inicializace všech poolù
        foreach (Pool pool in pools)
        {
            // Vytvoøíme Frontu (Queue) pro daný typ prefabu
            Queue<GameObject> objectPool = new Queue<GameObject>();

            // Pokud není nastavený rodiè, vytvoøíme pro tento pool nový prázdný GameObject
            if (pool.parent == null)
            {
                GameObject parentGO = new GameObject(pool.prefab.name + "PoolParent");
                parentGO.transform.SetParent(this.transform); // Rodièem je ObjectPoolManager
                pool.parent = parentGO.transform;
            }

            for (int i = 0; i < pool.size; i++)
            {
                GameObject obj = Instantiate(pool.prefab);
                obj.transform.SetParent(pool.parent); // Nastavíme rodièe pro poøádek v hierarchii
                obj.SetActive(false); // Deaktivujeme objekt, protože není zrovna používán
                objectPool.Enqueue(obj); // Pøidáme objekt do fronty poolu
            }

            // Pøidáme frontu do slovníku, aby k ní bylo možné pøistupovat podle prefabu
            poolDictionary.Add(pool.prefab, objectPool);
        }
    }

    /// <summary>
    /// Získá aktivní objekt z poolu.
    /// </summary>
    /// <param name="prefab">Prefab, pro který chceme získat objekt.</param>
    /// <param name="position">Pozice, na kterou se objekt má pøesunout.</param>
    /// <param name="rotation">Rotace, kterou objekt má mít.</param>
    /// <returns>Aktivovaný objekt z poolu, nebo null pokud pool neexistuje/je prázdný a nemùže se rozšíøit.</returns>
    public GameObject GetPooledObject(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (!poolDictionary.ContainsKey(prefab))
        {
            Debug.LogWarning($"Pool pro prefab '{prefab.name}' neexistuje!");
            return null;
        }

        // Pokud je fronta prázdná, mùžete se rozhodnout buï vrátit null,
        // nebo dynamicky pool rozšíøit (vytvoøit nový objekt).
        if (poolDictionary[prefab].Count == 0)
        {
            Debug.LogWarning($"Pool pro prefab '{prefab.name}' je prázdný! Rozšiøuji pool...");
            // Volitelná logika pro rozšíøení poolu:
            GameObject newObj = Instantiate(prefab);
            newObj.transform.SetParent(pools.Find(p => p.prefab == prefab).parent); // Najdeme správného rodièe
                                                                                    // Návratová hodnota je tento novì vytvoøený objekt
                                                                                    // newObj.SetActive(true); // Aktivujeme ho hned
                                                                                    // return newObj;
                                                                                    // Nebo: V pøípadì nedostatku prostì nevracet nic:
            return null; // Záleží na vaší logice
        }

        GameObject obj = poolDictionary[prefab].Dequeue(); // Vybereme objekt z fronty

        obj.transform.position = position;
        obj.transform.rotation = rotation;
        obj.SetActive(true); // Aktivujeme ho

        return obj;
    }

    /// <summary>
    /// Vrátí objekt zpìt do poolu.
    /// </summary>
    /// <param name="obj">Objekt, který se má vrátit.</param>
    /// <param name="prefabUsedToPool">Prefab, který byl použit k vytvoøení tohoto objektu, aby se vìdìlo, do kterého poolu ho vrátit.</param>
    public void ReturnObjectToPool(GameObject obj, GameObject prefabUsedToPool)
    {
        if (!poolDictionary.ContainsKey(prefabUsedToPool))
        {
            Debug.LogWarning($"Pool pro prefab '{prefabUsedToPool.name}' neexistuje! Nièím objekt.");
            Destroy(obj); // Pokud nevíme, kam vrátit, znièíme
            return;
        }

        obj.SetActive(false); // Deaktivujeme objekt
        poolDictionary[prefabUsedToPool].Enqueue(obj); // Vrátíme ho zpìt do fronty poolu
    }
}