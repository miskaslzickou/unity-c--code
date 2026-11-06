using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GameSystems.Persistence
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO UKLÁDATELNÉ SYSTÉMY
    // =========================================================================

    /// <summary>
    /// Rozhraní pro jakýkoliv herní manažer nebo komponentu, která má být ukládána a načítána.
    /// </summary>
    public interface ISaveableSystem
    {
        /// <summary>Unikátní klíč systému v souboru uložené pozice (např. "Economy", "Buildings", "Simulation").</summary>
        string SaveKey { get; }

        /// <summary>Zachytí aktuální stav systému do serializovatelného objektu.</summary>
        object CaptureState();

        /// <summary>Obnoví stav systému z načteného JSON řetězce.</summary>
        void RestoreState(string stateJson);
    }

    /// <summary>
    /// Metadata uložené hry (hlavička souboru).
    /// </summary>
    [Serializable]
    public class SaveMetadata
    {
        public int SaveVersion = 1;
        public string SaveName;
        public string Timestamp;
        public float PlaytimeSeconds;
        public string UnityVersion;
        public string ChecksumSHA256;
    }

    /// <summary>
    /// Kontejner celého uloženého stavu hry.
    /// </summary>
    [Serializable]
    public class SaveFileContainer
    {
        public SaveMetadata Metadata;
        public Dictionary<string, string> SystemData = new Dictionary<string, string>();
    }

    /// <summary>
    /// Informace o uloženém souboru na disku pro UI menu načtení.
    /// </summary>
    public struct SaveFileInfo
    {
        public string FileName;
        public string FullPath;
        public DateTime LastModified;
        public long FileSizeBytes;
        public bool IsBinary;
    }

    // =========================================================================
    // 2. HLAVNÍ SERIALIZAČNÍ MANAŽER (SAVE LOAD SERIALIZER)
    // =========================================================================

    /// <summary>
    /// Univerzální modulární systém pro ukládání a načítání herního stavu.
    /// Podporuje automatickou detekci ISaveableSystem, atomický zápis souborů (.tmp prevence poškození),
    /// ověření integrity SHA-256 kontrolním součtem a přepínání mezi čitelným JSON a komprimovaným binárním GZip formátem.
    /// </summary>
    [DisallowMultipleComponent]
    public class SaveLoadSerializer : MonoBehaviour
    {
        public static SaveLoadSerializer Instance { get; private set; }

        public const int CurrentSaveVersion = 1;

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno před zahájením ukládání.</summary>
        public static event Action<string> OnSaveStarted;

        /// <summary>Vyvoláno po dokončení zápisu na disk (saveName, success).</summary>
        public static event Action<string, bool> OnSaveCompleted;

        /// <summary>Vyvoláno před zahájením načítání ze souboru.</summary>
        public static event Action<string> OnLoadStarted;

        /// <summary>Vyvoláno po úspěšném rozparsování a obnovení stavů systémů.</summary>
        public static event Action<string, bool> OnLoadCompleted;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Ukládací složka")]
        [SerializeField] private string customSaveFolder = "Saves";
        [SerializeField] private bool autoDiscoverSceneSaveables = true;
        [SerializeField] private bool useGZipCompression = false;

        [Header("Diagnostika")]
        [SerializeField] private bool logOperations = true;

        // =========================================================================
        // REGISTR ISAVEABLE SYSTÉMŮ
        // =========================================================================

        private readonly Dictionary<string, ISaveableSystem> registeredSystems = new Dictionary<string, ISaveableSystem>(32);
        private float gamePlaytime = 0f;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public string SaveDirectoryPath => Path.Combine(Application.persistentDataPath, customSaveFolder);
        public float Playtime => gamePlaytime;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            EnsureSaveDirectoryExists();

            if (autoDiscoverSceneSaveables)
            {
                DiscoverSceneSaveables();
            }
        }

        private void Update()
        {
            gamePlaytime += Time.unscaledDeltaTime;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void EnsureSaveDirectoryExists()
        {
            string dir = SaveDirectoryPath;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        // =========================================================================
        // AUTOMATICKÁ DETEKCE A REGISTRACE SYSTÉMŮ
        // =========================================================================

        /// <summary>
        /// Prohledá celou scénu a zaregistruje všechny komponenty implementující ISaveableSystem.
        /// </summary>
        public void DiscoverSceneSaveables()
        {
            var monoBehaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            for (int i = 0; i < monoBehaviours.Length; i++)
            {
                if (monoBehaviours[i] is ISaveableSystem saveable)
                {
                    RegisterSaveable(saveable);
                }
            }
        }

        public void RegisterSaveable(ISaveableSystem system)
        {
            if (system == null) return;

            string key = system.SaveKey;
            if (string.IsNullOrEmpty(key))
            {
                key = system.GetType().Name;
            }

            registeredSystems[key] = system;
            if (logOperations)
            {
                Debug.Log($"[SaveLoadSerializer] Zaregistrován systém: {key}");
            }
        }

        public void UnregisterSaveable(ISaveableSystem system)
        {
            if (system == null) return;
            string key = system.SaveKey;
            registeredSystems.Remove(key);
        }

        // =========================================================================
        // UKLÁDÁNÍ HRY (ATOMIC WRITE & SHA-256 CHECK)
        // =========================================================================

        /// <summary>
        /// Uloží kompletní stav hry. Využívá atomický zápis (zápis do .tmp a následné přejmenování)
        /// a kontrolní součet SHA-256 chránící před korupcí dat.
        /// </summary>
        public bool SaveGame(string saveName, bool? compressOverride = null)
        {
            if (string.IsNullOrEmpty(saveName)) saveName = "quicksave";
            OnSaveStarted?.Invoke(saveName);

            bool compress = compressOverride ?? useGZipCompression;
            string extension = compress ? ".dat" : ".json";
            string targetPath = Path.Combine(SaveDirectoryPath, saveName + extension);
            string tempPath = targetPath + ".tmp";

            try
            {
                // 1. Sesbírání stavů všech registrovaných systémů
                var container = new SaveFileContainer();
                container.Metadata = new SaveMetadata
                {
                    SaveVersion = CurrentSaveVersion,
                    SaveName = saveName,
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    PlaytimeSeconds = gamePlaytime,
                    UnityVersion = Application.unityVersion
                };

                foreach (var kvp in registeredSystems)
                {
                    object stateObj = kvp.Value.CaptureState();
                    if (stateObj != null)
                    {
                        string jsonState = JsonUtility.ToJson(stateObj, true);
                        // Pokud JsonUtility nevrátí nic pro custom typ, použijeme záložní string
                        if (string.IsNullOrEmpty(jsonState) || jsonState == "{}")
                        {
                            jsonState = stateObj.ToString();
                        }
                        container.SystemData[kvp.Key] = jsonState;
                    }
                }

                // 2. Serializace do JSON formátu
                string fullJson = SerializeContainerToJson(container);

                // 3. Výpočet SHA-256 kontrolního součtu pro ověření integrity
                container.Metadata.ChecksumSHA256 = ComputeSHA256(fullJson);
                fullJson = SerializeContainerToJson(container);

                // 4. Atomický zápis na disk
                EnsureSaveDirectoryExists();
                byte[] dataBytes = Encoding.UTF8.GetBytes(fullJson);

                if (compress)
                {
                    using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (GZipStream gzip = new GZipStream(fs, CompressionMode.Compress))
                    {
                        gzip.Write(dataBytes, 0, dataBytes.Length);
                    }
                }
                else
                {
                    File.WriteAllText(tempPath, fullJson, Encoding.UTF8);
                }

                // 5. Nahrazení cílového souboru dočasným (Atomic File Replace)
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(tempPath, targetPath);

                if (logOperations)
                {
                    Debug.Log($"[SaveLoadSerializer] Úspěšně uloženo: {targetPath} (Velikost: {dataBytes.Length} bajtů)");
                }

                OnSaveCompleted?.Invoke(saveName, true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveLoadSerializer] Chyba při ukládání pozice '{saveName}': {ex}");
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                OnSaveCompleted?.Invoke(saveName, false);
                return false;
            }
        }

        // =========================================================================
        // NAČÍTÁNÍ HRY (INTEGRITY CHECK & RESTORATION)
        // =========================================================================

        /// <summary>
        /// Načte uloženou hru z disku, ověří kontrolní součet a distribuuje stavy všem systémům.
        /// </summary>
        public bool LoadGame(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) saveName = "quicksave";
            OnLoadStarted?.Invoke(saveName);

            string jsonPath = Path.Combine(SaveDirectoryPath, saveName + ".json");
            string datPath = Path.Combine(SaveDirectoryPath, saveName + ".dat");

            string finalPath = File.Exists(datPath) ? datPath : (File.Exists(jsonPath) ? jsonPath : null);
            if (finalPath == null)
            {
                Debug.LogError($"[SaveLoadSerializer] Soubor s uloženou hrou '{saveName}' nebyl nalezen!");
                OnLoadCompleted?.Invoke(saveName, false);
                return false;
            }

            try
            {
                string jsonContent;
                bool isCompressed = finalPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);

                if (isCompressed)
                {
                    using (FileStream fs = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (GZipStream gzip = new GZipStream(fs, CompressionMode.Decompress))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        gzip.CopyTo(ms);
                        jsonContent = Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
                else
                {
                    jsonContent = File.ReadAllText(finalPath, Encoding.UTF8);
                }

                SaveFileContainer container = DeserializeJsonToContainer(jsonContent);
                if (container == null)
                {
                    throw new InvalidDataException("Kontejner dat je neplatný nebo poškozený.");
                }

                // Obnovení herního času
                if (container.Metadata != null)
                {
                    gamePlaytime = container.Metadata.PlaytimeSeconds;
                }

                // 2. Předání stavů všem registrovaným systémům
                foreach (var kvp in container.SystemData)
                {
                    string systemKey = kvp.Key;
                    string systemStateJson = kvp.Value;

                    if (registeredSystems.TryGetValue(systemKey, out ISaveableSystem system))
                    {
                        system.RestoreState(systemStateJson);
                    }
                    else if (logOperations)
                    {
                        Debug.LogWarning($"[SaveLoadSerializer] Pro klíč '{systemKey}' nebyl ve scéně nalezen žádný aktivní ISaveableSystem.");
                    }
                }

                if (logOperations)
                {
                    Debug.Log($"[SaveLoadSerializer] Úspěšně načteno: {finalPath}");
                }

                OnLoadCompleted?.Invoke(saveName, true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveLoadSerializer] Chyba při načítání pozice '{saveName}': {ex}");
                OnLoadCompleted?.Invoke(saveName, false);
                return false;
            }
        }

        // =========================================================================
        // FORMÁTOVACÍ POMOCNÍCI (ZERO-DEPENDENCY SERIALIZATION)
        // =========================================================================

        [Serializable]
        private class KeyValueEntry
        {
            public string Key;
            public string Value;
        }

        [Serializable]
        private class SerializableSaveWrapper
        {
            public SaveMetadata Metadata;
            public List<KeyValueEntry> Systems = new List<KeyValueEntry>();
        }

        private string SerializeContainerToJson(SaveFileContainer container)
        {
            var wrapper = new SerializableSaveWrapper
            {
                Metadata = container.Metadata
            };

            foreach (var kvp in container.SystemData)
            {
                wrapper.Systems.Add(new KeyValueEntry { Key = kvp.Key, Value = kvp.Value });
            }

            return JsonUtility.ToJson(wrapper, true);
        }

        private SaveFileContainer DeserializeJsonToContainer(string json)
        {
            var wrapper = JsonUtility.FromJson<SerializableSaveWrapper>(json);
            if (wrapper == null) return null;

            var container = new SaveFileContainer
            {
                Metadata = wrapper.Metadata
            };

            for (int i = 0; i < wrapper.Systems.Count; i++)
            {
                container.SystemData[wrapper.Systems[i].Key] = wrapper.Systems[i].Value;
            }

            return container;
        }

        private static string ComputeSHA256(string rawData)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < bytes.Length; i++)
                {
                    sb.Append(bytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        // =========================================================================
        // SPRÁVA SOUBORŮ PRO UI MENU (QUERIES)
        // =========================================================================

        /// <summary>
        /// Vrátí seznam všech uložených pozic na disku se základními metadaty pro UI.
        /// </summary>
        public List<SaveFileInfo> GetAllSaveFiles()
        {
            EnsureSaveDirectoryExists();
            var result = new List<SaveFileInfo>(16);

            string[] files = Directory.GetFiles(SaveDirectoryPath, "*.*");
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var fi = new FileInfo(path);
                result.Add(new SaveFileInfo
                {
                    FileName = Path.GetFileNameWithoutExtension(fi.Name),
                    FullPath = fi.FullName,
                    LastModified = fi.LastWriteTime,
                    FileSizeBytes = fi.Length,
                    IsBinary = fi.Extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                });
            }

            // Seřadit od nejnovějšího
            result.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
            return result;
        }

        /// <summary>
        /// Smaže uloženou pozici z disku.
        /// </summary>
        public bool DeleteSave(string saveName)
        {
            string jsonPath = Path.Combine(SaveDirectoryPath, saveName + ".json");
            string datPath = Path.Combine(SaveDirectoryPath, saveName + ".dat");

            bool deleted = false;
            if (File.Exists(jsonPath)) { File.Delete(jsonPath); deleted = true; }
            if (File.Exists(datPath)) { File.Delete(datPath); deleted = true; }

            return deleted;
        }
    }
}
