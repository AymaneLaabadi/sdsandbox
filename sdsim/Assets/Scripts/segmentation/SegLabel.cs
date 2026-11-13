using UnityEngine;

/// <summary>
/// Assigns a segmentation class id to the renderer(s) of this GameObject.
/// The id is written into the shader property _SegId (red channel = classId / 255)
/// through a per-material MaterialPropertyBlock, which is only read by the
/// Hidden/SegReplacement shader used by SegmentationSensor. Normal (RGB) rendering
/// does not use _SegId, so it is unaffected.
///
/// Class ids: 0 = background (default for every unlabelled object), 1 = drivable road.
/// </summary>
[DisallowMultipleComponent]
public class SegLabel : MonoBehaviour
{
    public const int CLASS_BACKGROUND = 0;
    public const int CLASS_ROAD = 1;

    static readonly int SegIdProp = Shader.PropertyToID("_SegId");
    static readonly int SegBandProp = Shader.PropertyToID("_SegBand");
    public static readonly Vector2 FullBand = new Vector2(0f, 1f);

    [Tooltip("Segmentation class id written to the mask (0..255). 1 = drivable road.")]
    public int classId = CLASS_ROAD;

    [Tooltip("Optional. If set, only material slots whose material name contains this text " +
             "(case-insensitive) are labelled; other slots keep the background id.")]
    public string materialNameFilter = "";

    [Tooltip("Also label renderers on child objects.")]
    public bool includeChildren = false;

    [Tooltip("Label the whole mesh even where an alpha-cutout material discards pixels. " +
             "Use for overlay meshes whose texture only shows painted lines (e.g. TransparentRoad) " +
             "but whose geometry is the drivable lane.")]
    public bool ignoreAlpha = false;

    [Tooltip("Drivable band across the road in texture V (0..1, texture space of _MainTex). " +
             "Pixels outside it are background. (0,1) = whole object. Set automatically for " +
             "RoadBuilder roads from SegRoadStyles.")]
    public Vector2 drivableBandV = new Vector2(0f, 1f);

    MaterialPropertyBlock mpb;

    /// <summary>
    /// Step 3 of the pipeline: everything that has no SegLabel renders as class 0.
    /// Runs automatically once at startup, no scene wiring needed.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void InitGlobalDefault()
    {
        Shader.SetGlobalVector(SegIdProp, Vector4.zero);
        Shader.SetGlobalVector(SegBandProp, new Vector4(0f, 1f, 0f, 0f));
    }

    public static Vector4 ClassToVector(int id, bool ignoreAlpha = false)
    {
        // SetVector (not SetColor): in Linear colour space SetColor would convert the
        // value sRGB->linear and 1/255 would no longer come out as exactly 1.
        // .g is a flag read by the TransparentCutout pass, it is never written to the mask.
        return new Vector4(Mathf.Clamp(id, 0, 255) / 255f, ignoreAlpha ? 1f : 0f, 0f, 1f);
    }

    void OnEnable()
    {
        Apply();
    }

    void OnDisable()
    {
        Write(Vector4.zero, new Vector4(0f, 1f, 0f, 0f), true);
    }

    /// <summary>
    /// (Re)apply the label. Call this again if materials are swapped at runtime.
    /// </summary>
    public void Apply()
    {
        Write(ClassToVector(classId, ignoreAlpha), new Vector4(drivableBandV.x, drivableBandV.y, 0f, 0f), false);
    }

    void Write(Vector4 value, Vector4 band, bool ignoreFilter)
    {
        if (mpb == null)
            mpb = new MaterialPropertyBlock();

        Renderer[] renderers = includeChildren
            ? GetComponentsInChildren<Renderer>(true)
            : GetComponents<Renderer>();

        bool useFilter = !ignoreFilter && !string.IsNullOrEmpty(materialNameFilter);
        string filter = useFilter ? materialNameFilter.ToLowerInvariant() : null;

        foreach (Renderer r in renderers)
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (useFilter)
                {
                    Material m = mats[i];
                    if (m == null || !m.name.ToLowerInvariant().Contains(filter))
                        continue;
                }

                // Preserve any other per-material properties already in a block.
                r.GetPropertyBlock(mpb, i);
                mpb.SetVector(SegIdProp, value);
                mpb.SetVector(SegBandProp, band);
                r.SetPropertyBlock(mpb, i);
            }
        }
    }
}
