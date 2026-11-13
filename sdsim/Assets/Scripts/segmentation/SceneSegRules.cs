using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Labels the road of existing hand-made scenes at load time (no scene files are edited).
/// RoadBuilder roads (generated_road, generated_track, warren) and start lines label themselves;
/// this only covers roads that are plain meshes. One rule per scene, matched by object name or
/// material name. See docs/SEGMENTATION.md for which scenes are confirmed.
/// </summary>
public static class SceneSegRules
{
    struct Rule
    {
        public string scene, objectName, materialName;
        public Rule(string scene, string objectName, string materialName)
        {
            this.scene = scene; this.objectName = objectName; this.materialName = materialName;
        }
    }

    static readonly Rule[] Rules =
    {
        new Rule("mini_monaco", "miniMonacoRoad", null),        // RoadBuilder-style mesh, textured road
        new Rule("mountain_track", null, "MountainTrackMat"),    // road sub-mesh of long_road.fbx
        new Rule("circuit_launch", "CircuitLaunchRoad", null),   // road mesh of CircuitLaunch.fbx
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        foreach (Rule rule in Rules)
        {
            if (rule.scene != scene.name) continue;
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    if (Matches(r, rule)) { Label(r, rule); n++; }
            Debug.Log("SceneSegRules: " + scene.name + ": labelled " + n + " renderer(s) as road");
        }
    }

    static bool Matches(Renderer r, Rule rule)
    {
        if (rule.objectName != null)
            return r.gameObject.name == rule.objectName;
        foreach (Material m in r.sharedMaterials)
            if (m != null && m.name.Contains(rule.materialName)) return true;
        return false;
    }

    static void Label(Renderer r, Rule rule)
    {
        SegLabel label = r.GetComponent<SegLabel>();
        if (label == null) label = r.gameObject.AddComponent<SegLabel>();
        label.classId = SegLabel.CLASS_ROAD;
        label.materialNameFilter = rule.materialName ?? "";
        // textured RoadBuilder-style meshes: keep painted shoulders out of the mask
        if (rule.objectName != null && r.sharedMaterial != null)
            label.drivableBandV = SegRoadStyles.GetDrivableBand(r.sharedMaterial.mainTexture);
        label.Apply();
    }
}
