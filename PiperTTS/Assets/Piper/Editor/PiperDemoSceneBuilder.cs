using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using Unity.InferenceEngine;
using UnityEngine.UIElements;

namespace Piper.EditorTools
{
    /// <summary>
    /// Builds Assets/Piper/Demo/PiperDemo.unity from code. UI is runtime
    /// UI Toolkit (UIDocument) - no TMP, no uGUI text/font assets.
    /// </summary>
    public static class PiperDemoSceneBuilder
    {
        private const string ScenePath = "Assets/Piper/Demo/PiperDemo.unity";
        private const string ThemePath = "Assets/Piper/Demo/UnityDefaultRuntimeTheme.tss";
        private const string PanelPath = "Assets/Piper/Demo/PiperPanelSettings.asset";

        [MenuItem("Piper/Build Demo Scene")]
        public static void Build()
        {
            var panel = EnsurePanelSettings();

            // ONNX model must be imported as a ModelAsset by the IE importer
            const string modelPath = "Assets/Piper/Model/cs_CZ-jirka-medium.onnx";
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
            var modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
            Debug.Log($"[PiperDemoSceneBuilder] ModelAsset import: {(modelAsset != null ? "OK" : "FAILED")} for {modelPath}");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera
            var camGo = new GameObject("Main Camera");
            camGo.tag = "Main Camera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.12f);
            camGo.AddComponent<AudioListener>();

            // EventSystem with Input System module (drives UI Toolkit input)
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGo.AddComponent<InputSystemUIInputModule>();

            // UI Toolkit document
            var uiGo = new GameObject("PiperUI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            const string uxmlPath = "Assets/Piper/Demo/PiperDemoUI.uxml";
            AssetDatabase.ImportAsset(uxmlPath);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (uxml != null)
            {
                doc.visualTreeAsset = uxml;
                var docSo = new SerializedObject(doc);
                var prop = docSo.FindProperty("sourceAsset") ?? docSo.FindProperty("m_VisualTreeAsset") ?? docSo.FindProperty("m_SourceAsset");
                if (prop != null)
                {
                    prop.objectReferenceValue = uxml;
                    docSo.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(doc);
                Debug.Log($"[PiperDemoSceneBuilder] Assigned UIDocument sourceAsset: {uxml.name}");
            }
            else
            {
                Debug.LogError($"[PiperDemoSceneBuilder] Could not load {uxmlPath}");
            }

            // Piper host object
            var piperGo = new GameObject("PiperTTS");
            var audioSource = piperGo.AddComponent<AudioSource>();
            var tts = piperGo.AddComponent<PiperTTS>();
            tts.RefreshVoiceList();
            if (tts.voiceModels.Count == 0 && modelAsset != null)
            {
                tts.modelAsset = modelAsset;
            }
            Debug.Log($"[PiperDemoSceneBuilder] Assigned modelAsset on PiperTTS: {(tts.modelAsset != null ? "OK" : "NULL")}, Total voices: {tts.voiceModels.Count}");
            var demo = piperGo.AddComponent<PiperDemoUI>();
            demo.tts = tts;
            demo.document = doc;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[PiperDemoSceneBuilder] Scene saved to {ScenePath}");
        }

        /// <summary>Entry point for command line: Unity -batchmode -executeMethod Piper.EditorTools.PiperDemoSceneBuilder.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            Build();
            EditorApplication.Exit(0);
        }

        private static PanelSettings EnsurePanelSettings()
        {
            Directory.CreateDirectory("Assets/Piper/Demo");

            // Minimal runtime theme (this is what Unity's own menu item creates)
            if (!File.Exists(ThemePath))
            {
                File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(ThemePath);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelPath);
            }
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();
            return panel;
        }
    }
}
