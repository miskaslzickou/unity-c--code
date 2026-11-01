using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Piper.EditorTools
{
    /// <summary>
    /// Editor tool to download and manage Piper TTS voice models (.onnx and .onnx.json)
    /// from HuggingFace (rhasspy/piper-voices) directly into the project.
    /// </summary>
    public class PiperVoiceDownloader : EditorWindow
    {
        public class VoiceCatalogEntry
        {
            public string Key;
            public string DisplayName;
            public string LanguageCategory;
            public string Description;
            public string SizeApprox;
            public string RelativeHfPath;
        }

        private static readonly List<VoiceCatalogEntry> Catalog = new List<VoiceCatalogEntry>
        {
            // Čeština
            new VoiceCatalogEntry
            {
                Key = "cs_CZ-honza-high",
                DisplayName = "Honza (Vysoká kvalita / High)",
                LanguageCategory = "Čeština (Czech)",
                Description = "Komunitní high-quality model (Thomcles) natrénovaný na monospeaker korpusu.",
                SizeApprox = "~85 MB",
                RelativeHfPath = "https://huggingface.co/Thomcles/Piper-TTS-Czech/resolve/main/high/model"
            },
            new VoiceCatalogEntry
            {
                Key = "cs_CZ-jirka-medium",
                DisplayName = "Jirka (Střední)",
                LanguageCategory = "Čeština (Czech)",
                Description = "Kvalitní mužský hlas v češtině.",
                SizeApprox = "~63 MB",
                RelativeHfPath = "cs/cs_CZ/jirka/medium/cs_CZ-jirka-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "cs_CZ-kasandra-medium",
                DisplayName = "Kasandra (Střední)",
                LanguageCategory = "Čeština (Czech)",
                Description = "Příjemný ženský hlas v češtině.",
                SizeApprox = "~63 MB",
                RelativeHfPath = "cs/cs_CZ/kasandra/medium/cs_CZ-kasandra-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "cs_CZ-jirka-low",
                DisplayName = "Jirka (Rychlý / Low)",
                LanguageCategory = "Čeština (Czech)",
                Description = "Lehký a rychlý mužský hlas (menší paměť).",
                SizeApprox = "~16 MB",
                RelativeHfPath = "cs/cs_CZ/jirka/low/cs_CZ-jirka-low"
            },

            // Slovenčina
            new VoiceCatalogEntry
            {
                Key = "sk_SK-lili-medium",
                DisplayName = "Lili (Střední)",
                LanguageCategory = "Slovenčina (Slovak)",
                Description = "Čistý ženský hlas ve slovenštině.",
                SizeApprox = "~63 MB",
                RelativeHfPath = "sk/sk_SK/lili/medium/sk_SK-lili-medium"
            },

            // English (US)
            new VoiceCatalogEntry
            {
                Key = "en_US-lessac-medium",
                DisplayName = "Lessac (US Medium)",
                LanguageCategory = "English (US)",
                Description = "Nejoblíbenější čistý neutrální/ženský US hlas.",
                SizeApprox = "~60 MB",
                RelativeHfPath = "en/en_US/lessac/medium/en_US-lessac-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "en_US-ryan-medium",
                DisplayName = "Ryan (US Medium)",
                LanguageCategory = "English (US)",
                Description = "Hluboký, energický mužský US hlas (skvělý pro vojáky).",
                SizeApprox = "~60 MB",
                RelativeHfPath = "en/en_US/ryan/medium/en_US-ryan-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "en_US-amy-medium",
                DisplayName = "Amy (US Medium)",
                LanguageCategory = "English (US)",
                Description = "Přirozený ženský americký hlas.",
                SizeApprox = "~60 MB",
                RelativeHfPath = "en/en_US/amy/medium/en_US-amy-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "en_US-lessac-low",
                DisplayName = "Lessac (US Fast / Low)",
                LanguageCategory = "English (US)",
                Description = "Velmi rychlý a nenáročný model.",
                SizeApprox = "~16 MB",
                RelativeHfPath = "en/en_US/lessac/low/en_US-lessac-low"
            },

            // English (GB)
            new VoiceCatalogEntry
            {
                Key = "en_GB-alan-medium",
                DisplayName = "Alan (British Male)",
                LanguageCategory = "English (British)",
                Description = "Důrazný britský mužský hlas.",
                SizeApprox = "~63 MB",
                RelativeHfPath = "en/en_GB/alan/medium/en_GB-alan-medium"
            },
            new VoiceCatalogEntry
            {
                Key = "en_GB-alba-medium",
                DisplayName = "Alba (British Female)",
                LanguageCategory = "English (British)",
                Description = "Jemný britský ženský hlas.",
                SizeApprox = "~63 MB",
                RelativeHfPath = "en/en_GB/alba/medium/en_GB-alba-medium"
            }
        };

        private const string BaseHfUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
        private const string ModelDir = "Assets/Piper/Model";
        private const string ConfigDir = "Assets/StreamingAssets/Piper";

        private Vector2 scrollPos;
        private string customVoiceKey = "";
        private bool isDownloading = false;
        private string downloadStatus = "";
        private float downloadProgress = 0f;

        [MenuItem("Piper/Download Voices...")]
        public static void Open()
        {
            var win = GetWindow<PiperVoiceDownloader>("Piper Hlasy");
            win.minSize = new Vector2(580, 500);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("Piper Voice Downloader (HuggingFace)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Zde můžete jedním kliknutím stáhnout a nainstalovat další hlasy pro Piper TTS (české, slovenské i anglické). " +
                "Modely se automaticky naimportují do Unity a budou ihned dostupné v komponentě PiperTTS i v demo scéně.",
                MessageType.Info);

            EditorGUILayout.Space(6);

            if (isDownloading)
            {
                EditorGUILayout.HelpBox($"Stahování probíhá: {downloadStatus}", MessageType.Warning);
                EditorGUILayout.Space(4);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            var groups = Catalog.GroupBy(c => c.LanguageCategory);
            foreach (var group in groups)
            {
                EditorGUILayout.Space(10);
                GUILayout.Label($"— {group.Key} —", EditorStyles.boldLabel);

                foreach (var entry in group)
                {
                    DrawVoiceRow(entry);
                }
            }

            EditorGUILayout.Space(16);
            GUILayout.Label("— Vlastní hlas z HuggingFace —", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            customVoiceKey = EditorGUILayout.TextField("Klíč hlasu:", customVoiceKey);
            if (GUILayout.Button("Stáhnout", GUILayout.Width(90)) && !string.IsNullOrWhiteSpace(customVoiceKey))
            {
                DownloadCustomVoice(customVoiceKey.Trim());
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Příklad: de_DE-thorsten-medium, fr_FR-siwis-medium", MessageType.None);

            EditorGUILayout.Space(16);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Otevřít složku Modelů"))
            {
                EditorUtility.RevealInFinder(ModelDir);
            }
            if (GUILayout.Button("Otevřít složku Konfigurací"))
            {
                EditorUtility.RevealInFinder(ConfigDir);
            }
            if (GUILayout.Button("Obnovit seznam ve scéně"))
            {
                RefreshAllScenePiperTTS();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private void DrawVoiceRow(VoiceCatalogEntry entry)
        {
            bool isInstalled = IsVoiceInstalled(entry.Key);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(entry.DisplayName, EditorStyles.boldLabel, GUILayout.Width(200));
            GUILayout.Label(entry.SizeApprox, EditorStyles.miniLabel, GUILayout.Width(70));

            if (isInstalled)
            {
                var oldColor = GUI.color;
                GUI.color = Color.green;
                GUILayout.Label("✓ Nainstalováno", EditorStyles.boldLabel, GUILayout.Width(110));
                GUI.color = oldColor;

                if (GUILayout.Button("Přeinstalovat", GUILayout.Width(95)))
                {
                    if (EditorUtility.DisplayDialog("Přeinstalovat hlas?", $"Přejete si znovu stáhnout {entry.DisplayName}?", "Ano", "Ne"))
                    {
                        StartDownload(entry);
                    }
                }
            }
            else
            {
                GUILayout.Label("Nenainstalováno", EditorStyles.label, GUILayout.Width(110));
                if (GUILayout.Button("Stáhnout", GUILayout.Width(95)))
                {
                    StartDownload(entry);
                }
            }

            EditorGUILayout.EndHorizontal();

            GUILayout.Label(entry.Description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        public static bool IsVoiceInstalled(string voiceKey)
        {
            string onnx = Path.Combine(ModelDir, voiceKey + ".onnx");
            string json = Path.Combine(ConfigDir, voiceKey + ".onnx.json");
            return File.Exists(onnx) && File.Exists(json);
        }

        private void StartDownload(VoiceCatalogEntry entry)
        {
            isDownloading = true;
            downloadStatus = $"Stahuji {entry.DisplayName}...";
            downloadProgress = 0f;

            Task.Run(async () =>
            {
                try
                {
                    await DownloadVoiceFilesAsync(entry.RelativeHfPath, entry.Key, p =>
                    {
                        downloadProgress = p;
                        downloadStatus = $"Stahuji {entry.DisplayName}: {(int)(p * 100)}%";
                    });

                    EditorApplication.delayCall += () =>
                    {
                        isDownloading = false;
                        EditorUtility.ClearProgressBar();
                        AssetDatabase.Refresh();
                        RefreshAllScenePiperTTS();
                        EditorUtility.DisplayDialog("Hotovo", $"Hlas '{entry.DisplayName}' byl úspěšně stažen a naimportován!", "OK");
                        Repaint();
                    };
                }
                catch (Exception ex)
                {
                    EditorApplication.delayCall += () =>
                    {
                        isDownloading = false;
                        EditorUtility.ClearProgressBar();
                        Debug.LogError($"[PiperVoiceDownloader] Chyba při stahování {entry.Key}: {ex.Message}");
                        EditorUtility.DisplayDialog("Chyba stahování", $"Nepodařilo se stáhnout hlas: {ex.Message}", "OK");
                        Repaint();
                    };
                }
            });
        }

        private void DownloadCustomVoice(string voiceKey)
        {
            // Build expected relative HF path from key: e.g. cs_CZ-kasandra-medium -> cs/cs_CZ/kasandra/medium/cs_CZ-kasandra-medium
            var parts = voiceKey.Split('-');
            if (parts.Length < 3)
            {
                EditorUtility.DisplayDialog("Chyba", "Klíč hlasu musí mít formát: <lang_code>-<name>-<quality> (např. cs_CZ-kasandra-medium)", "OK");
                return;
            }
            string langCode = parts[0];
            string langFamily = langCode.Split('_')[0];
            string speaker = parts[1];
            string quality = parts[2];
            string relPath = $"{langFamily}/{langCode}/{speaker}/{quality}/{voiceKey}";

            var entry = new VoiceCatalogEntry
            {
                Key = voiceKey,
                DisplayName = voiceKey,
                LanguageCategory = "Custom",
                Description = "Uživatelský hlas.",
                SizeApprox = "",
                RelativeHfPath = relPath
            };

            StartDownload(entry);
        }

        private static async Task DownloadVoiceFilesAsync(string relHfPath, string voiceKey, Action<float> onProgress)
        {
            Directory.CreateDirectory(ModelDir);
            Directory.CreateDirectory(ConfigDir);

            string onnxUrl = relHfPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? $"{relHfPath}.onnx"
                : $"{BaseHfUrl}{relHfPath}.onnx";
            string jsonUrl = relHfPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? $"{relHfPath}.onnx.json"
                : $"{BaseHfUrl}{relHfPath}.onnx.json";

            string destOnnx = Path.Combine(ModelDir, voiceKey + ".onnx");
            string destJson = Path.Combine(ConfigDir, voiceKey + ".onnx.json");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);

                // 1. Download JSON config
                onProgress?.Invoke(0.05f);
                var jsonBytes = await client.GetByteArrayAsync(jsonUrl);
                await File.WriteAllBytesAsync(destJson, jsonBytes);

                // 2. Download ONNX model (large, stream with progress)
                using (var response = await client.GetAsync(onnxUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destOnnx, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long totalRead = 0;
                        int read;

                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read);
                            totalRead += read;
                            if (totalBytes > 0)
                            {
                                float p = 0.1f + 0.88f * ((float)totalRead / totalBytes);
                                onProgress?.Invoke(p);
                            }
                        }
                    }
                }

                // 3. Ensure ONNX .meta has dynamicDimConfigs for Inference Engine
                string metaPath = destOnnx + ".meta";
                if (!File.Exists(metaPath))
                {
                    string guid = Guid.NewGuid().ToString("N");
                    string metaContent =
$@"fileFormatVersion: 2
guid: {guid}
ScriptedImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 2
  userData: 
  assetBundleName: 
  assetBundleVariant: 
  script: {{fileID: 11500000, guid: f22407ba6b4157b4a93d0a670bd3dd57, type: 3}}
  dynamicDimConfigs:
  - name: batch_size
    size: -1
  - name: phonemes
    size: -1
";
                    await File.WriteAllTextAsync(metaPath, metaContent);
                }
            }

            onProgress?.Invoke(1f);
        }

        private static void RefreshAllScenePiperTTS()
        {
            var ttsObjects = UnityEngine.Object.FindObjectsByType<PiperTTS>();
            foreach (var tts in ttsObjects)
            {
                tts.RefreshVoiceList();
                EditorUtility.SetDirty(tts);
            }
            Debug.Log($"[PiperVoiceDownloader] Aktualizováno {ttsObjects.Length} objektů PiperTTS ve scéně.");
        }
    }
}
