using UnityEngine;

/// <summary>
/// Renders a pixel-aligned segmentation mask of exactly the same view as an RGB CameraSensor.
///
/// Lives on its own (disabled) Camera, normally a child "SegCam" of the car prefab.
/// At capture time it copies pose, projection and culling from rgbSensor.sensorCam,
/// renders with the Hidden/SegReplacement replacement shader into a linear, non-MSAA,
/// point-filtered RenderTexture and returns an RGB24 PNG whose RED channel is the class id
/// (0 = background, 1 = road). G and B are always 0.
///
/// The RGB CameraSensor is never modified.
/// </summary>
[RequireComponent(typeof(Camera))]
public class SegmentationSensor : MonoBehaviour
{
    [Tooltip("The RGB camera sensor whose frame this mask must align with.")]
    public CameraSensor rgbSensor;

    [Tooltip("Optional. Defaults to Resources/SegReplacement (Hidden/SegReplacement).")]
    public Shader replacementShader;

    Camera segCam;
    RenderTexture ren;
    Texture2D tex;
    bool warnedFisheye = false;

    /// <summary>
    /// Runtime fallback used by TcpCarHandler if a car prefab was not wired in the editor.
    /// Must be called on the main thread.
    /// </summary>
    public static SegmentationSensor CreateFor(CameraSensor rgb)
    {
        GameObject go = new GameObject("SegCam");
        Transform parent = rgb.transform.parent != null ? rgb.transform.parent : rgb.transform;
        go.transform.SetParent(parent, false);
        Camera cam = go.AddComponent<Camera>();
        cam.enabled = false;
        SegmentationSensor s = go.AddComponent<SegmentationSensor>();
        s.rgbSensor = rgb;
        return s;
    }

    void Awake()
    {
        segCam = GetComponent<Camera>();
        segCam.enabled = false; // only renders when Capture() is called

        if (replacementShader == null)
            replacementShader = Resources.Load<Shader>("SegReplacement");
        if (replacementShader == null)
            replacementShader = Shader.Find("Hidden/SegReplacement");
        if (replacementShader == null)
            Debug.LogError("SegmentationSensor: Hidden/SegReplacement shader not found (expected Assets/Resources/SegReplacement.shader).");

        // Make sure unlabelled objects are class 0 even if the static init did not run.
        SegLabel.InitGlobalDefault();

        // No image effects / post-processing may run on the mask camera.
        foreach (MonoBehaviour mb in GetComponents<MonoBehaviour>())
        {
            if (mb != this && mb.enabled)
            {
                Debug.LogWarning("SegmentationSensor: disabling " + mb.GetType().Name + " on " + name + " (no effects allowed on the segmentation camera).");
                mb.enabled = false;
            }
        }
    }

    void EnsureTargets(int w, int h, int depthBits)
    {
        if (ren != null && ren.width == w && ren.height == h && ren.depth == depthBits)
            return;

        if (ren != null)
        {
            segCam.targetTexture = null;
            ren.Release();
            Destroy(ren);
        }
        if (tex != null)
            Destroy(tex);

        ren = new RenderTexture(w, h, depthBits, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        ren.antiAliasing = 1;
        ren.filterMode = FilterMode.Point;
        ren.useMipMap = false;
        ren.autoGenerateMips = false;
        ren.name = "SegCamRT";
        ren.Create();

        tex = new Texture2D(w, h, TextureFormat.RGB24, false, true); // linear = true
        tex.filterMode = FilterMode.Point;
    }

    void SyncWithRgbCamera(Camera src)
    {
        Transform st = src.transform;
        transform.SetPositionAndRotation(st.position, st.rotation);

        segCam.orthographic = src.orthographic;
        segCam.orthographicSize = src.orthographicSize;
        segCam.fieldOfView = src.fieldOfView;
        segCam.nearClipPlane = src.nearClipPlane;
        segCam.farClipPlane = src.farClipPlane;
        segCam.cullingMask = src.cullingMask;
        segCam.useOcclusionCulling = src.useOcclusionCulling;
        segCam.layerCullDistances = src.layerCullDistances;
        segCam.layerCullSpherical = src.layerCullSpherical;
        segCam.rect = new Rect(0f, 0f, 1f, 1f);

        segCam.clearFlags = CameraClearFlags.SolidColor;
        segCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        segCam.allowMSAA = false;
        segCam.allowHDR = false;
        segCam.allowDynamicResolution = false;
        segCam.renderingPath = RenderingPath.Forward; // replacement passes have no LightMode

        segCam.targetTexture = ren;

        // Use the exact same projection matrix as the RGB camera (covers aspect,
        // lens shift / physical camera settings and any custom projection).
        segCam.projectionMatrix = src.projectionMatrix;
    }

    /// <summary>Render the mask for the current frame. Red channel = class id.</summary>
    public Texture2D Capture()
    {
        if (rgbSensor == null || rgbSensor.sensorCam == null || replacementShader == null)
            return null;

        if (segCam == null)
            segCam = GetComponent<Camera>();

        Camera src = rgbSensor.sensorCam;
        RenderTexture srcRT = src.targetTexture;
        int w = srcRT != null ? srcRT.width : rgbSensor.width;
        int h = srcRT != null ? srcRT.height : rgbSensor.height;
        int depthBits = srcRT != null && srcRT.depth > 0 ? srcRT.depth : 24;

        if (!warnedFisheye)
        {
            UnityStandardAssets.ImageEffects.Fisheye fe = src.GetComponent<UnityStandardAssets.ImageEffects.Fisheye>();
            if (fe != null && fe.enabled && (fe.strengthX != 0f || fe.strengthY != 0f))
            {
                Debug.LogWarning("SegmentationSensor: fisheye is enabled on the RGB camera; the mask is NOT distorted and will not align. Use fish_eye 0 or warp masks offline.");
                warnedFisheye = true;
            }
        }

        EnsureTargets(w, h, depthBits);
        SyncWithRgbCamera(src);

        RenderTexture prev = RenderTexture.active;

        segCam.SetReplacementShader(replacementShader, "RenderType");
        segCam.Render();
        segCam.ResetReplacementShader();

        RenderTexture.active = ren;
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        tex.Apply(false);
        RenderTexture.active = prev;

        return tex;
    }

    /// <summary>PNG (RGB24, lossless) of the mask. Class id in the red channel.</summary>
    public byte[] CapturePng()
    {
        Texture2D t = Capture();
        return t != null ? t.EncodeToPNG() : null;
    }

    void OnDestroy()
    {
        if (segCam != null)
            segCam.targetTexture = null;
        if (ren != null)
        {
            ren.Release();
            Destroy(ren);
        }
        if (tex != null)
            Destroy(tex);
    }
}
