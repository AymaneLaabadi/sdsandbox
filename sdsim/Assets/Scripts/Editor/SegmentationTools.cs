using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor helpers for the segmentation camera.
///
///   Tools/Segmentation/Add SegCam To Car Prefabs   wire the segmentation camera into car prefabs
///   Tools/Segmentation/Report Scene Renderers      list renderers/materials of the open scene
///
/// Road labelling of existing scenes happens at runtime (SceneSegRules.cs), not in the editor.
/// </summary>
public static class SegmentationTools
{
    [MenuItem("Tools/Segmentation/Report Scene Renderers")]
    static void ReportRenderers()
    {
        var sb = new StringBuilder();
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            sb.AppendLine("== " + scene.name);
            foreach (var rd in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
            {
                string mats = string.Join(", ", rd.sharedMaterials.Select(m =>
                    m == null ? "null" : m.name + " [" + (m.GetTag("RenderType", false, "-") ) + "]"));
                var lbl = rd.GetComponent<SegLabel>();
                sb.AppendLine(string.Format("{0}  ({1}, layer {2}{3}{4})  mats: {5}",
                    GetPath(rd.transform), rd.GetType().Name, rd.gameObject.layer,
                    rd.enabled && rd.gameObject.activeInHierarchy ? "" : ", HIDDEN",
                    lbl != null ? ", SegLabel " + lbl.classId : "", mats));
            }
        }
        Debug.Log(sb.ToString());
    }

    static string GetPath(Transform t)
    {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    // ------------------------------------------------------------------ step 7: car prefabs

    const string ShaderPath = "Assets/Resources/SegReplacement.shader";

    [MenuItem("Tools/Segmentation/Add SegCam To Car Prefabs")]
    static void AddSegCamToCarPrefabs()
    {
        // Every prefab that has a TcpCarHandler with a camSensor.
        var targets = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) continue;
            var handlers = asset.GetComponentsInChildren<tk.TcpCarHandler>(true);
            if (handlers.Any(h => h.camSensor != null))
                targets.Add(path);
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Segmentation", "No prefab with TcpCarHandler + camSensor found under Assets/Prefabs.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Segmentation - Add SegCam",
                "Add a 'SegCam' (disabled Camera + SegmentationSensor) next to the RGB CameraSensor and assign " +
                "TcpCarHandler.segSensor in:\n\n" + string.Join("\n", targets) +
                "\n\nPrefabs already wired are left unchanged.", "Apply", "Cancel"))
            return;

        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        var log = new StringBuilder("Add SegCam To Car Prefabs:\n");

        foreach (string path in targets)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool changed = false;
            try
            {
                foreach (var h in root.GetComponentsInChildren<tk.TcpCarHandler>(true))
                {
                    if (h.camSensor == null) continue;

                    if (h.segSensor != null && h.segSensor.rgbSensor == h.camSensor)
                    {
                        log.AppendLine("  " + path + ": already wired (" + GetPath(h.segSensor.transform) + ")");
                        continue;
                    }

                    Transform camT = h.camSensor.transform;
                    Transform parent = camT.parent != null ? camT.parent : root.transform;

                    Transform existing = parent.Find("SegCam");
                    GameObject segGo = existing != null ? existing.gameObject : new GameObject("SegCam");
                    segGo.transform.SetParent(parent, false);
                    segGo.transform.localPosition = camT.localPosition;
                    segGo.transform.localRotation = camT.localRotation;
                    segGo.transform.localScale = Vector3.one;
                    segGo.layer = camT.gameObject.layer;

                    Camera cam = segGo.GetComponent<Camera>();
                    if (cam == null) cam = segGo.AddComponent<Camera>();
                    cam.enabled = false;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0, 0, 0, 0);
                    cam.allowMSAA = false;
                    cam.allowHDR = false;

                    SegmentationSensor seg = segGo.GetComponent<SegmentationSensor>();
                    if (seg == null) seg = segGo.AddComponent<SegmentationSensor>();
                    seg.rgbSensor = h.camSensor;
                    seg.replacementShader = shader;

                    h.segSensor = seg;
                    changed = true;
                    log.AppendLine("  " + path + ": added " + GetPath(segGo.transform) + " -> rgb " + GetPath(camT));
                }

                if (changed)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString());
    }
}
