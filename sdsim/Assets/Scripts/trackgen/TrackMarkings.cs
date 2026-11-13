using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Seeded road markings and ground for gen_track roads (shader Custom/ProceduralRoad).
///
/// Segmentation rule: class 1 spans from the outer edge of the left edge line to the outer edge
/// of the right edge line: at_edge and none -> whole road (V 0..1); inset -> V [inset, 1-inset].
/// Centre lines are always inside class 1.
/// </summary>
public class TrackMarkings
{
    // base surfaces: line-free window [vMin, vMax] of an existing road texture + tile size (m)
    struct BaseDef
    {
        public string name, texture;
        public float vMin, vMax, tileU, tileV;
        public BaseDef(string name, string texture, float vMin, float vMax, float tileU, float tileV)
        {
            this.name = name; this.texture = texture; this.vMin = vMin; this.vMax = vMax; this.tileU = tileU; this.tileV = tileV;
        }
    }

    static readonly BaseDef[] Bases =
    {
        new BaseDef("asphalt_grey", "TexturesCom_Roads0086_1_seamless_S 1", 0.52f, 0.87f, 12f, 4f),
        new BaseDef("asphalt_coarse", "UdacitySimRoad", 0.32f, 0.70f, 17f, 5.5f),
        new BaseDef("dirt", "TexturesCom_Roads0075_1_seamless_S", 0.23f, 0.63f, 16f, 4f),
        new BaseDef("cobblestone", "UdacityRoadBridge", 0.25f, 0.74f, 12f, 6f),
        new BaseDef("sand", "SandAlbedo", 0f, 1f, 6f, 6f),
    };

    static readonly string[] PaletteNames = { "white", "yellow", "orange", "red", "blue" };
    static readonly Color[] Palette =
    {
        new Color(0.95f, 0.95f, 0.95f), new Color(0.95f, 0.80f, 0.10f), new Color(1.00f, 0.50f, 0.05f),
        new Color(0.85f, 0.10f, 0.10f), new Color(0.10f, 0.30f, 0.90f),
    };

    public static readonly string[] EdgeModes = { "at_edge", "inset", "none" };
    static readonly double[] EdgeModeP = { 0.45, 0.40, 0.15 };
    public static readonly string[] CentreModes = { "none", "solid", "dashed" };
    static readonly double[] CentreModeP = { 0.35, 0.25, 0.40 };
    public static readonly string[] Grounds = { "sand", "grass", "mud", "concrete", "carpet" };   // sand = scene default

    public string baseName, ground, edgeMode, centreMode, edgeColorName, centreColorName;
    public Texture baseTex;
    public Vector4 baseWin;
    public Color tint, edgeColor, centreColor;
    public double width, edgeInset, edgeWidth, centreWidth, dash, gap, wear;   // inset = fraction of width, widths in m

    /// <summary>Drivable band in V (also the outer edges of the edge lines).</summary>
    public Vector2 Band { get { return edgeMode == "inset" ? new Vector2((float)edgeInset, (float)(1.0 - edgeInset)) : new Vector2(0f, 1f); } }

    static int Pick(SeededRng rng, double[] p)
    {
        double x = rng.Uniform(), acc = 0.0;
        for (int i = 0; i < p.Length; i++) { acc += p[i]; if (x < acc) return i; }
        return p.Length - 1;
    }

    static Color Jitter(SeededRng rng, Color c, double lo, double hi)
    {
        float b = (float)rng.Range(lo, hi);
        return new Color(c.r * b * (float)rng.Range(0.93, 1.07), c.g * b * (float)rng.Range(0.93, 1.07),
                         c.b * b * (float)rng.Range(0.93, 1.07), 1f);
    }

    /// <summary>Every value is always drawn, then replaced by an override if one is given.</summary>
    public static TrackMarkings Sample(ulong seed, double width, Material[] roadMaterials,
                                       string baseO, string edgeModeO, double? edgeInsetO, string centreModeO, string groundO)
    {
        var avail = new List<BaseDef>();
        foreach (BaseDef b in Bases)
            if (FindTexture(roadMaterials, b.texture) != null) avail.Add(b);
        if (avail.Count == 0) throw new InvalidOperationException("no base road textures found in RoadBuilder.roadMaterials");

        SeededRng rng = SeededRng.Sub(seed, "markings");
        var m = new TrackMarkings { width = width };
        BaseDef bd = avail[rng.IntIncl(0, avail.Count - 1)];
        if (baseO != null) bd = avail.Find(b => b.name == baseO);
        if (bd.name == null) throw new ArgumentException("unknown base '" + baseO + "'");
        m.baseName = bd.name;
        m.baseTex = FindTexture(roadMaterials, bd.texture);
        m.baseWin = new Vector4(bd.vMin, bd.vMax, bd.tileU, bd.tileV);
        m.tint = Jitter(rng, Color.white, 0.7, 1.15);

        m.edgeMode = EdgeModes[Pick(rng, EdgeModeP)];
        m.edgeInset = rng.Range(0.03, 0.15);
        m.edgeWidth = Math.Min(rng.Range(0.12, 0.45), 0.12 * width);
        int ec = rng.IntIncl(0, Palette.Length - 1);
        m.edgeColorName = PaletteNames[ec];
        m.edgeColor = Jitter(rng, Palette[ec], 0.85, 1.1);

        m.centreMode = CentreModes[Pick(rng, CentreModeP)];
        m.centreWidth = Math.Min(rng.Range(0.10, 0.30), 0.08 * width);
        int cc = rng.IntIncl(0, Palette.Length - 1);
        m.centreColorName = PaletteNames[cc];
        m.centreColor = Jitter(rng, Palette[cc], 0.85, 1.1);
        m.dash = rng.Range(1.0, 4.0);
        m.gap = rng.Range(1.0, 4.0);
        m.wear = rng.Range(0.0, 0.35);

        int g = SeededRng.Sub(seed, "ground").IntIncl(0, Grounds.Length - 1);
        if (Grounds[g] == "sand" && m.baseName == "sand") g = 1;   // keep the road visible
        m.ground = Grounds[g];

        if (edgeModeO != null)
        {
            if (Array.IndexOf(EdgeModes, edgeModeO) < 0) throw new ArgumentException("unknown edge_mode '" + edgeModeO + "'");
            m.edgeMode = edgeModeO;
        }
        if (edgeInsetO.HasValue) m.edgeInset = edgeInsetO.Value;
        if (centreModeO != null)
        {
            if (Array.IndexOf(CentreModes, centreModeO) < 0) throw new ArgumentException("unknown centre_mode '" + centreModeO + "'");
            m.centreMode = centreModeO;
        }
        if (groundO != null)
        {
            if (Array.IndexOf(Grounds, groundO) < 0) throw new ArgumentException("unknown ground '" + groundO + "'");
            m.ground = groundO;
        }
        return m;
    }

    static Texture FindTexture(Material[] mats, string name)
    {
        foreach (Material mat in mats)
            if (mat != null && mat.mainTexture != null && mat.mainTexture.name == name) return mat.mainTexture;
        return null;
    }

    /// <summary>Writes the parameters into the renderer's per-material block (merged with SegLabel's).</summary>
    public void Apply(Renderer r)
    {
        var mpb = new MaterialPropertyBlock();
        r.GetPropertyBlock(mpb, 0);
        mpb.SetTexture("_BaseTex", baseTex);
        mpb.SetVector("_BaseWin", baseWin);
        mpb.SetColor("_Tint", tint);
        mpb.SetFloat("_RoadWidth", (float)width);
        mpb.SetVector("_Edge", new Vector4(edgeMode == "none" ? 0f : 1f, edgeMode == "inset" ? (float)edgeInset : 0f,
                                           (float)(edgeWidth / width), 0f));
        mpb.SetColor("_EdgeColor", edgeColor);
        mpb.SetVector("_Centre", new Vector4(Array.IndexOf(CentreModes, centreMode), (float)(centreWidth / width),
                                             (float)dash, (float)gap));
        mpb.SetColor("_CentreColor", centreColor);
        mpb.SetFloat("_Wear", (float)wear);
        r.SetPropertyBlock(mpb, 0);
    }

    public string Describe()
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "base={0};edge={1};inset={2:R};ew={3:R};ec={4};centre={5};cw={6:R};cc={7};dash={8:R};gap={9:R};wear={10:R};ground={11}",
            baseName, edgeMode, edgeInset, edgeWidth, edgeColorName, centreMode, centreWidth, centreColorName, dash, gap, wear, ground);
    }
}
