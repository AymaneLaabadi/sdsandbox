using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Labels the road of existing hand-made scenes at load time (no scene files are edited).
/// RoadBuilder roads (generated_road, generated_track, warren) and start lines label themselves;
/// this covers roads that are plain meshes or that are only painted into a floor texture.
/// Rules match renderers by object name or material name. A rule can carry a label texture
/// (Resources/SegMasks/&lt;name&gt;, made by tools/make_seg_masks.py) in the UV space of the renderer's
/// main texture: then only the painted track (between the outer edges of its boundary lines) is
/// road, whatever the mesh covers. See docs/SEGMENTATION.md for which scenes are confirmed.
/// </summary>
public static class SceneSegRules
{
    struct Rule
    {
        public string scene, objectName, materialName, mask;
        public bool ignoreAlpha;
        public Rule(string scene, string objectName, string materialName, string mask = null, bool ignoreAlpha = false)
        {
            this.scene = scene; this.objectName = objectName; this.materialName = materialName;
            this.mask = mask; this.ignoreAlpha = ignoreAlpha;
        }
    }

    static readonly Rule[] Rules =
    {
        new Rule("mini_monaco", "miniMonacoRoad", null),        // RoadBuilder-style mesh, textured road
        new Rule("mountain_track", null, "MountainTrackMat"),    // road sub-mesh of long_road.fbx
        // circuit_launch: the track is painted into CircuitLaunchDiffuse; the road ribbon and the carpet
        // use that same texture and overlap in depth, so both get the same label texture.
        new Rule("circuit_launch", "CircuitLaunchRoad", null, "circuit_launch"),
        new Rule("circuit_launch", "CircuitLaunchCarpet", null, "circuit_launch"),
        // roboracingleague_1: white tape lines on an alpha-cutout overlay quad above the floor; draw the
        // whole quad in the mask and keep only the ring between the two tape loops.
        new Rule("roboracingleague_1", null, "track_lines3", "roboracingleague_1", true),
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
            Texture mask = null;
            if (rule.mask != null)
            {
                mask = Resources.Load<Texture>("SegMasks/" + rule.mask);
                if (mask == null)
                {
                    Debug.LogWarning("SceneSegRules: label texture SegMasks/" + rule.mask + " missing, rule skipped");
                    continue;
                }
            }
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    if (Matches(r, rule)) { Label(r, rule, mask); n++; }
            Debug.Log("SceneSegRules: " + scene.name + ": labelled " + n + " renderer(s) as road (" +
                      (rule.objectName ?? rule.materialName) + ")");
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

    static void Label(Renderer r, Rule rule, Texture mask)
    {
        SegLabel label = r.GetComponent<SegLabel>();
        if (label == null) label = r.gameObject.AddComponent<SegLabel>();
        label.classId = SegLabel.CLASS_ROAD;
        label.materialNameFilter = rule.materialName ?? "";
        label.ignoreAlpha = rule.ignoreAlpha;
        label.labelMask = mask;
        // textured RoadBuilder-style meshes: keep painted shoulders out of the mask
        if (mask == null && rule.objectName != null && r.sharedMaterial != null)
            label.drivableBandV = SegRoadStyles.GetDrivableBand(r.sharedMaterial.mainTexture);
        label.Apply();
    }
}
