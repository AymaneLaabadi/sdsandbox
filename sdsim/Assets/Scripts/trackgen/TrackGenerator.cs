using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Parsed gen_track request (pure, can be parsed on the socket thread).</summary>
public class GenTrackRequest
{
    public string seedText = "0";
    public ulong seed;
    public double? roadWidth;     // null = from seed
    // null = from seed
    public string surface, surfaceVariant, edgeColor, centreMode, centreColor, ground;
    public bool returnNodes;
}

/// <summary>
/// gen_track: builds a seeded closed-loop track in generated_road (CarPath, road mesh with
/// procedural markings, ground, start line, car at node 0) and returns a short track_info.
/// Created on demand, no scene edit.
/// Never uses UnityEngine.Random.
/// </summary>
public class TrackGenerator : MonoBehaviour
{
    public const string SupportedScene = "generated_road";
    const float CarHeightAboveRoad = 0.58f;   // donkey_start is 0.58 m above the generated_road surface
    const float StartLineLength = 0.6f;
    const float StartLineLift = 0.01f;

    readonly Dictionary<Car, Pose> spawnPoses = new Dictionary<Car, Pose>();
    struct Pose { public Vector3 pos; public Quaternion rot; }
    public bool HasGenerated { get; private set; }
    static Texture2D checkerTex;
    static Material roadMaterial;
    Renderer groundRenderer;
    Material groundOriginal;
    Material carpetGround;

    public static TrackGenerator GetOrCreate()
    {
        TrackGenerator g = FindObjectOfType<TrackGenerator>();
        return g != null ? g : new GameObject("TrackGenerator").AddComponent<TrackGenerator>();
    }

    static string Field(JSONObject json, string key)
    {
        if (json == null || !json.HasField(key)) return null;
        JSONObject f = json.GetField(key);
        string s = f.type == JSONObject.Type.STRING ? f.str
                 : f.type == JSONObject.Type.NUMBER ? f.n.ToString("R", CultureInfo.InvariantCulture)
                 : null;
        return string.IsNullOrEmpty(s) ? null : s.Trim();
    }

    public static GenTrackRequest ParseRequest(JSONObject json)
    {
        var r = new GenTrackRequest();
        string s;
        if ((s = Field(json, "seed")) != null) r.seedText = s;
        r.seed = SeededRng.ParseSeed(r.seedText);
        if ((s = Field(json, "road_width")) != null) r.roadWidth = double.Parse(s, CultureInfo.InvariantCulture);
        r.surface = Field(json, "surface");
        r.surfaceVariant = Field(json, "surface_variant");
        r.edgeColor = Field(json, "edge_color");
        r.centreMode = Field(json, "centre_mode");
        r.centreColor = Field(json, "centre_color");
        r.ground = Field(json, "ground");
        r.returnNodes = Field(json, "return_nodes") == "1";
        return r;
    }

    public JSONObject Generate(GenTrackRequest req, tk.TcpCarHandler requester)
    {
        var info = new JSONObject(JSONObject.Type.OBJECT);
        info.AddField("msg_type", "track_info");
        info.AddField("seed", req.seedText);
        try
        {
            if (SceneManager.GetActiveScene().name != SupportedScene)
                throw new InvalidOperationException("gen_track only works in scene " + SupportedScene);
            PathManager pm = FindObjectOfType<PathManager>();
            RoadBuilder rb = FindObjectOfType<RoadBuilder>();
            if (pm == null || rb == null)
                throw new InvalidOperationException("scene has no PathManager/RoadBuilder");

            LayoutResult L = TrackLayouts.Generate(req.seed, req.roadWidth);
            if (L == null)
                throw new InvalidOperationException("no layout satisfied the rules in " + TrackLayouts.MaxAttempts + " attempts");

            TrackMarkings mk = TrackMarkings.Sample(req.seed, L.width, req.surface, req.surfaceVariant, req.edgeColor,
                                                    req.centreMode, req.centreColor, req.ground);
            if (roadMaterial == null)
                roadMaterial = new Material(Resources.Load<Shader>("ProceduralRoad"));

            // ---- path: node 0 at the scene start position, flat at y = 0.5 (as MakeRandomPath)
            Vector3 origin = pm.startPos != null ? pm.startPos.position : Vector3.zero;
            int n = L.nodes.Count;
            var world = new Vector3[n];
            for (int i = 0; i < n; i++)
                world[i] = new Vector3((float)(L.nodes[i].x - L.nodes[0].x) + origin.x, 0.5f,
                                       (float)(L.nodes[i].z - L.nodes[0].z) + origin.z);
            var path = new CarPath();
            for (int i = 0; i < n; i++)
            {
                Vector3 dir = world[(i + 1) % n] - world[(i - 1 + n) % n];
                path.nodes.Add(new PathNode { pos = world[i], rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) });
            }

            // ---- replace road, start line, things that follow the path (race cameras)
            foreach (GameObject old in GameObject.FindGameObjectsWithTag("road_mesh"))
                Destroy(old);
            pm.carPath = path;
            GameObject road = rb.BuildGeneratedRoad(path, (float)(L.width / 2.0), roadMaterial, mk.Band);
            road.name = "generated_track_road";
            mk.Apply(road.GetComponent<Renderer>());
            SetGround(mk);
            CreateStartLine(road.transform, path.nodes[0], path.nodes[0].pos.y + rb.roadHeightOffset, (float)L.width);
            foreach (GameObject go in pm.initAfterCarPathLoaded)
            {
                IWaitCarPath w = go != null ? go.GetComponent<IWaitCarPath>() : null;
                if (w != null && !(w is RoadBuilder)) w.Init();
            }

            // ---- cars: requester at node 0, others 4 nodes apart behind it
            var handlers = FindObjectsOfType<tk.TcpCarHandler>().Where(h => h != requester).ToList();
            handlers.Insert(0, requester);
            for (int k = 0; k < handlers.Count; k++)
            {
                PathNode pn = path.nodes[((n - 4 * k) % n + n) % n];
                PlaceCar(handlers[k], pn.pos + Vector3.up * (rb.roadHeightOffset + CarHeightAboveRoad), pn.rotation);
            }
            HasGenerated = true;

            // ---- track_info
            string paramText = string.Format(CultureInfo.InvariantCulture, "shape={0};width={1:R};", L.shape, L.width) + mk.Describe();
            info.AddField("ok", true);
            info.AddField("shape", L.shape);
            info.AddField("corners", L.corners);
            info.AddField("road_width", (float)L.width);
            info.AddField("length", (float)L.length);
            info.AddField("min_radius", (float)L.minRadius);
            info.AddField("totalNodes", n);
            info.AddField("surface", mk.family);
            info.AddField("surface_variant", mk.variant);
            info.AddField("edge_width", (float)mk.edgeWidth);
            info.AddField("edge_color", mk.edgeColorName);
            info.AddField("centre_mode", mk.centreMode);
            info.AddField("centre_width", (float)mk.centreWidth);
            info.AddField("centre_color", mk.centreColorName);
            info.AddField("dash", (float)mk.dash);
            info.AddField("gap", (float)mk.gap);
            info.AddField("ground", mk.ground);
            var band = new JSONObject(JSONObject.Type.ARRAY);
            band.Add(mk.Band.x);
            band.Add(mk.Band.y);
            info.AddField("drivable_band", band);
            info.AddField("hash", Hash(path, paramText).ToString("x16"));
            if (req.returnNodes)
            {
                var arr = new JSONObject(JSONObject.Type.ARRAY);
                foreach (PathNode pn in path.nodes)
                {
                    var p = new JSONObject(JSONObject.Type.ARRAY);
                    p.Add(pn.pos.x);
                    p.Add(pn.pos.z);
                    arr.Add(p);
                }
                info.AddField("nodes", arr);
            }
        }
        catch (Exception e)
        {
            info.AddField("ok", false);
            info.AddField("error", e.Message);
            Debug.LogWarning("gen_track failed: " + e);
        }
        return info;
    }

    /// <summary>FNV-1a over node positions (mm) and the resolved parameters.</summary>
    static ulong Hash(CarPath path, string paramText)
    {
        var bytes = new List<byte>();
        foreach (PathNode pn in path.nodes)
        {
            bytes.AddRange(BitConverter.GetBytes((long)Math.Round(pn.pos.x * 1000.0)));
            bytes.AddRange(BitConverter.GetBytes((long)Math.Round(pn.pos.z * 1000.0)));
        }
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(paramText));
        return SeededRng.Fnv1a64(bytes.ToArray());
    }

    void CreateStartLine(Transform parent, PathNode node0, float roadY, float width)
    {
        GameObject line = GameObject.CreatePrimitive(PrimitiveType.Quad);
        DestroyImmediate(line.GetComponent<Collider>());
        line.name = "starting_line";
        line.transform.SetParent(parent, true);
        Vector3 pos = node0.pos;
        pos.y = roadY + StartLineLift;
        // Quad faces -Z; +90 deg about X lays it flat facing up, local Y along the heading.
        line.transform.SetPositionAndRotation(pos, node0.rotation * Quaternion.Euler(90.0f, 0.0f, 0.0f));
        line.transform.localScale = new Vector3(width, StartLineLength, 1.0f);

        if (checkerTex == null)
        {
            checkerTex = new Texture2D(16, 2, TextureFormat.RGB24, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 2; y++)
                    checkerTex.SetPixel(x, y, (x + y) % 2 == 0 ? Color.white : Color.black);
            checkerTex.Apply();
        }
        line.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard")) { mainTexture = checkerTex };

        BoxCollider bc = line.AddComponent<BoxCollider>();   // lap trigger, 2 m high (local -Z = up)
        bc.isTrigger = true;
        bc.center = new Vector3(0.0f, 0.0f, -1.0f);
        bc.size = new Vector3(1.0f, 1.0f, 2.0f);
        line.AddComponent<startingLine>();                    // its Awake adds SegLabel class 1
    }

    void PlaceCar(tk.TcpCarHandler h, Vector3 pos, Quaternion rot)
    {
        Car car = h != null && h.carObj != null ? h.carObj.GetComponent<Car>() : null;
        if (car == null) return;
        if (!spawnPoses.ContainsKey(car))
            spawnPoses[car] = new Pose { pos = car.startPos, rot = car.startRot };
        car.startPos = pos;   // reset_car returns here
        car.startRot = rot;
        car.Set(pos, rot);
        car.rb.velocity = Vector3.zero;
        car.rb.angularVelocity = Vector3.zero;
        car.RequestThrottle(0.0f);
        car.RequestSteering(0.0f);
        car.RequestFootBrake(1.0f);
        h.ResetActiveSpan();
    }

    /// <summary>Ground: carpet tracks get the same carpet as floor; others an outdoor ground by name
    /// ("sand" = the scene's own ground material).</summary>
    void SetGround(TrackMarkings mk)
    {
        if (groundRenderer == null)
        {
            GameObject g = GameObject.Find("ground");
            if (g == null) return;
            groundRenderer = g.GetComponent<Renderer>();
            groundOriginal = groundRenderer.sharedMaterial;
        }
        if (carpetGround != null) Destroy(carpetGround);
        carpetGround = null;
        Material m;
        if (mk.LinesOnly)
            m = carpetGround = mk.MakeCarpetGround();
        else
            m = mk.ground == "sand" ? null : Resources.Load<Material>("Ground/" + mk.ground);
        groundRenderer.sharedMaterial = m != null ? m : groundOriginal;
    }

    /// <summary>Before regen_road: scene spawn and ground back (legacy behaviour).</summary>
    public void RestoreSpawnPoses()
    {
        foreach (var kv in spawnPoses)
            if (kv.Key != null) { kv.Key.startPos = kv.Value.pos; kv.Key.startRot = kv.Value.rot; }
        if (groundRenderer != null) groundRenderer.sharedMaterial = groundOriginal;
        HasGenerated = false;
    }
}
