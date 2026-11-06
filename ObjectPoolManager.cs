using System.Collections.Generic;
using UnityEngine;

// T��da pro nastaven� jednotliv�ch pool� v Inspectoru
[System.Serializable]
public class Pool
{
    public GameObject prefab;
    public int size; // Po�et objekt�, kter� se maj� p�edvytvo�it do poolu
    public Transform parent; // Rodi�ovsk� transform pro poolovan� objekty (v� "Pool" GameObject)
}

public class ObjectPoolManager : MonoBehaviour
{
    public static ObjectPoolManager Instance { get; private set; } // Singleton pattern pro snadn� p��stup

    public List<Pool> pools; // Seznam v�ech pool�, kter� chceme spravovat

    // Slovn�k pro ukl�d�n� samotn�ch pool� (kl��: prefab, hodnota: seznam dostupn�ch objekt�)
    private Dictionary<GameObject, Queue<GameObject>> poolDictionary;

    void Awake()
    {
        // Implementace Singletonu
        if (Instance == null)
        {
            Instance = this;
            // DontDestroyOnLoad(gameObject); // Pokud chcete, aby pool p�e�il na��t�n� sc�n
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        poolDictionary = new Dictionary<GameObject, Queue<GameObject>>();

        // Inicializace v�ech pool�
        foreach (Pool pool in pools)
        {
            // Vytvo��me Frontu (Queue) pro dan� typ prefabu
            Queue<GameObject> objectPool = new Queue<GameObject>();

            // Pokud nen� nastaven� rodi�, vytvo��me pro tento pool nov� pr�zdn� GameObject
            if (pool.parent == null)
            {
                GameObject parentGO = new GameObject(pool.prefab.name + "PoolParent");
                parentGO.transform.SetParent(this.transform); // Rodi�em je ObjectPoolManager
                pool.parent = parentGO.transform;
            }

            for (int i = 0; i < pool.size; i++)
            {
                GameObject obj = Instantiate(pool.prefab);
                obj.transform.SetParent(pool.parent); // Nastav�me rodi�e pro po��dek v hierarchii
                obj.SetActive(false); // Deaktivujeme objekt, proto�e nen� zrovna pou��v�n
                objectPool.Enqueue(obj); // P�id�me objekt do fronty poolu
            }

            // P�id�me frontu do slovn�ku, aby k n� bylo mo�n� p�istupovat podle prefabu
            poolDictionary.Add(pool.prefab, objectPool);
        }
    }

    /// <summary>
    /// Z�sk� aktivn� objekt z poolu.
    /// </summary>
    /// <param name="prefab">Prefab, pro kter� chceme z�skat objekt.</param>
    /// <param name="position">Pozice, na kterou se objekt m� p�esunout.</param>
    /// <param name="rotation">Rotace, kterou objekt m� m�t.</param>
    /// <returns>Aktivovan� objekt z poolu, nebo null pokud pool neexistuje/je pr�zdn� a nem��e se roz���it.</returns>
    public GameObject GetPooledObject(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (!poolDictionary.ContainsKey(prefab))
        {
            Debug.LogWarning($"Pool pro prefab '{prefab.name}' neexistuje!");
            return null;
        }

        // Pokud je fronta pr�zdn�, m��ete se rozhodnout bu� vr�tit null,
        // nebo dynamicky pool roz���it (vytvo�it nov� objekt).
        if (poolDictionary[prefab].Count == 0)
        {
            Debug.LogWarning($"Pool pro prefab '{prefab.name}' je pr�zdn�! Roz�i�uji pool...");
            // Voliteln� logika pro roz���en� poolu:
            GameObject newObj = Instantiate(prefab);
            newObj.transform.SetParent(pools.Find(p => p.prefab == prefab).parent); // Najdeme spr�vn�ho rodi�e
                                                                                    // N�vratov� hodnota je tento nov� vytvo�en� objekt
                                                                                    // newObj.SetActive(true); // Aktivujeme ho hned
                                                                                    // return newObj;
                                                                                    // Nebo: V p��pad� nedostatku prost� nevracet nic:
            return null; // Z�le�� na va�� logice
        }

        GameObject obj = poolDictionary[prefab].Dequeue(); // Vybereme objekt z fronty

        obj.transform.position = position;
        obj.transform.rotation = rotation;
        obj.SetActive(true); // Aktivujeme ho

        return obj;
    }

    /// <summary>
    /// Vr�t� objekt zp�t do poolu.
    /// </summary>
    /// <param name="obj">Objekt, kter� se m� vr�tit.</param>
    /// <param name="prefabUsedToPool">Prefab, kter� byl pou�it k vytvo�en� tohoto objektu, aby se v�d�lo, do kter�ho poolu ho vr�tit.</param>
    public void ReturnObjectToPool(GameObject obj, GameObject prefabUsedToPool)
    {
        if (!poolDictionary.ContainsKey(prefabUsedToPool))
        {
            Debug.LogWarning($"Pool pro prefab '{prefabUsedToPool.name}' neexistuje! Ni��m objekt.");
            Destroy(obj); // Pokud nev�me, kam vr�tit, zni��me
            return;
        }

        obj.SetActive(false); // Deaktivujeme objekt
        poolDictionary[prefabUsedToPool].Enqueue(obj); // Vr�t�me ho zp�t do fronty poolu
    }
}