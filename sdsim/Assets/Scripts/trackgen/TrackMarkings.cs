using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Seeded look of a gen_track track. Every choice is independent and uniform (equal probability):
///   1. surface family: asphalt, concrete, dirt, sand, carpet; then one variant of that family
///   2. edge lines: always present, flush with the road edges; one colour for both lines
///   3. centre line: present or not; if present solid or dashed, and its own colour
/// Carpet tracks: the whole floor is that carpet and the road only draws its tape lines
/// (ProceduralRoad _LinesOnly), so the track exists only through its edge lines.
/// Other tracks: the floor is an outdoor ground (grass, mud, sand, concrete) that differs from the
/// road surface.
///
/// Segmentation rule: class 1 = the whole road mesh = from the outer edge of the left edge line to
/// the outer edge of the right edge line (V 0..1). Centre lines are inside class 1.
/// Every value is always drawn, then replaced by an override if one is given, so forcing one value
/// never changes the others.
/// </summary>
public class TrackMarkings
{
    public class Variant
    {
        public string name, family, material;   // material: Resources/TrackSurfaces/<material>.mat
        public float vMin, vMax, tileU, tileV, gloss;
        public Vector3 tint;                    // linear multiplier
        public Variant(string name, string family, string material, float vMin, float vMax, float tileU, float tileV,
                       float gloss, Vector3 tint)
        {
            this.name = name; this.family = family; this.material = material; this.vMin = vMin; this.vMax = vMax;
            this.tileU = tileU; this.tileV = tileV; this.gloss = gloss; this.tint = tint;
        }
    }

    public static readonly string[] Families = { "asphalt", "concrete", "dirt", "sand", "carpet" };

    // vMin/vMax: line-free window of the texture (0..1 = whole texture); tileU/tileV: metres covered
    // by one texture repeat along / across the road (tileV = by the window for windowed textures).
    static readonly Vector3 One = Vector3.one;
    public static readonly Variant[] Variants =
    {
        new Variant("asphalt_grey", "asphalt", "asphalt_grey", 0.52f, 0.87f, 12f, 4.2f, 0.15f, One),
        new Variant("asphalt_coarse", "asphalt", "asphalt_coarse", 0.32f, 0.70f, 17f, 5.6f, 0.15f, One),
        new Variant("asphalt_dark", "asphalt", "asphalt_dark", 0.316f, 0.42f, 12f, 0.8f, 0.15f, One),
        new Variant("concrete_cast", "concrete", "concrete_cast", 0f, 1f, 4f, 4f, 0.2f, One),
        new Variant("concrete_slab", "concrete", "concrete_slab", 0f, 1f, 4f, 4f, 0.25f, One),
        new Variant("concrete_panels", "concrete", "concrete_panels", 0f, 1f, 6f, 6f, 0.25f, One),
        new Variant("concrete_polished", "concrete", "concrete_polished", 0f, 1f, 4f, 4f, 0.5f, One),
        new Variant("dirt_track", "dirt", "dirt_track", 0.23f, 0.63f, 16f, 4f, 0.05f, One),
        new Variant("dirt_rocky", "dirt", "dirt_rocky", 0f, 1f, 6f, 6f, 0.05f, One),
        new Variant("sand", "sand", "sand", 0f, 1f, 6f, 6f, 0.05f, One),
        new Variant("carpet_office", "carpet", "carpet_office", 0f, 1f, 3.5f, 3.5f, 0.0f, One),
        new Variant("carpet_weave", "carpet", "carpet_weave", 0f, 1f, 1.5f, 1.5f, 0.0f, One),
        new Variant("carpet_grey", "carpet", "carpet_felt", 0f, 1f, 1.5f, 1.5f, 0.0f, new Vector3(1.6f, 1.6f, 1.6f)),
        new Variant("carpet_blue", "carpet", "carpet_felt", 0f, 1f, 1.5f, 1.5f, 0.0f, new Vector3(0.34f, 0.57f, 1.48f)),
        new Variant("carpet_green", "carpet", "carpet_felt", 0f, 1f, 1.5f, 1.5f, 0.0f, new Vector3(0.38f, 0.86f, 0.42f)),
        new Variant("carpet_red", "carpet", "carpet_felt", 0f, 1f, 1.5f, 1.5f, 0.0f, new Vector3(1.35f, 0.29f, 0.28f)),
    };

    public static readonly string[] PaletteNames = { "white", "yellow", "orange", "red", "blue", "green" };
    static readonly Color[] Palette =
    {
        new Color(0.95f, 0.95f, 0.95f), new Color(0.95f, 0.80f, 0.10f), new Color(1.00f, 0.50f, 0.05f),
        new Color(0.85f, 0.10f, 0.10f), new Color(0.10f, 0.30f, 0.90f), new Color(0.10f, 0.70f, 0.20f),
    };

    public static readonly string[] CentreModes = { "none", "solid", "dashed" };
    public static readonly string[] OutdoorGrounds = { "grass", "mud", "sand", "concrete" };   // sand = scene default

    public string family, variant, ground, edgeColorName, centreMode, centreColorName;
    public Variant surface;
    public Texture baseTex;
    public Vector3 tint;
    public Color edgeColor, centreColor;
    public double width, edgeWidth, centreWidth, dash, gap, wear;   // widths and lengths in m
    public bool LinesOnly { get { return family == "carpet"; } }

    /// <summary>Drivable band in V: always the whole road (edge lines are flush with the edges).</summary>
    public Vector2 Band { get { return new Vector2(0f, 1f); } }

    static Color Jitter(SeededRng rng, Color c, double lo, double hi)
    {
        float b = (float)rng.Range(lo, hi);
        return new Color(c.r * b * (float)rng.Range(0.93, 1.07), c.g * b * (float)rng.Range(0.93, 1.07),
                         c.b * b * (float)rng.Range(0.93, 1.07), 1f);
    }

    static List<Variant> VariantsOf(string family)
    {
        var l = new List<Variant>();
        foreach (Variant v in Variants) if (v.family == family) l.Add(v);
        return l;
    }

    static int PickIndex(double u, int count) { return Math.Min((int)(u * count), count - 1); }

    static int IndexOf(string[] list, string value, string what)
    {
        int i = Array.IndexOf(list, value);
        if (i < 0) throw new ArgumentException("unknown " + what + " '" + value + "' (" + string.Join("|", list) + ")");
        return i;
    }

    /// <summary>Overrides: null = from the seed.</summary>
    public static TrackMarkings Sample(ulong seed, double width, string familyO, string variantO, string edgeColorO,
                                       string centreModeO, string centreColorO, string groundO)
    {
        SeededRng rng = SeededRng.Sub(seed, "look");
        var m = new TrackMarkings { width = width };

        // 1. surface: family, then variant (drawn as a fraction so the variant draw does not
        //    depend on the family)
        int fi = rng.IntIncl(0, Families.Length - 1);
        double uVariant = rng.Uniform();
        float bright = (float)rng.Range(0.85, 1.1);
        var tj = new Vector3((float)rng.Range(0.96, 1.04), (float)rng.Range(0.96, 1.04), (float)rng.Range(0.96, 1.04));

        // 2. edge lines (always present)
        int ec = rng.IntIncl(0, Palette.Length - 1);
        m.edgeWidth = Math.Max(rng.Range(0.03, 0.07) * width, 0.15);   // proportional: visible on wide roads
        Color ecJ = Jitter(rng, Palette[ec], 0.85, 1.1);

        // 3. centre line: present?, then solid / dashed, colour
        bool present = rng.Uniform() < 0.5;
        bool dashed = rng.Uniform() < 0.5;
        int cc = rng.IntIncl(0, Palette.Length - 1);
        m.centreWidth = Math.Max(rng.Range(0.015, 0.04) * width, 0.10);
        Color ccJ = Jitter(rng, Palette[cc], 0.85, 1.1);
        m.dash = rng.Range(1.0, 4.0);
        m.gap = rng.Range(1.0, 4.0);
        m.wear = rng.Range(0.0, 0.35);

        double uGround = SeededRng.Sub(seed, "ground").Uniform();

        // ---- overrides
        if (familyO != null) fi = IndexOf(Families, familyO, "surface");
        m.family = Families[fi];
        List<Variant> vs = VariantsOf(m.family);
        m.surface = vs[PickIndex(uVariant, vs.Count)];
        if (variantO != null)
        {
            m.surface = vs.Find(v => v.name == variantO);
            if (m.surface == null)
                throw new ArgumentException("unknown surface_variant '" + variantO + "' for surface " + m.family + " (" +
                                            string.Join("|", vs.ConvertAll(v => v.name).ToArray()) + ")");
        }
        m.variant = m.surface.name;
        m.tint = Vector3.Scale(m.surface.tint, tj) * bright;

        if (edgeColorO != null) ec = IndexOf(PaletteNames, edgeColorO, "edge_color");
        m.edgeColorName = PaletteNames[ec];
        m.edgeColor = edgeColorO != null ? Palette[ec] : ecJ;

        m.centreMode = !present ? "none" : dashed ? "dashed" : "solid";
        if (centreModeO != null) m.centreMode = CentreModes[IndexOf(CentreModes, centreModeO, "centre_mode")];
        if (centreColorO != null) cc = IndexOf(PaletteNames, centreColorO, "centre_color");
        m.centreColorName = PaletteNames[cc];
        m.centreColor = centreColorO != null ? Palette[cc] : ccJ;

        // ground: carpet tracks -> the floor is the same carpet; others -> an outdoor ground that is
        // not the road surface itself
        if (m.LinesOnly)
        {
            if (groundO != null && groundO != "carpet")
                throw new ArgumentException("carpet tracks always use the carpet as ground");
            m.ground = "carpet";
        }
        else
        {
            var allowed = new List<string>();
            foreach (string g in OutdoorGrounds)
                if (!(g == m.family || (g == "mud" && m.family == "dirt"))) allowed.Add(g);
            m.ground = allowed[PickIndex(uGround, allowed.Count)];
            if (groundO != null)
            {
                IndexOf(OutdoorGrounds, groundO, "ground");
                m.ground = groundO;
            }
        }

        m.baseTex = LoadTexture(m.surface.material);
        return m;
    }

    static Texture LoadTexture(string material)
    {
        Material mat = Resources.Load<Material>("TrackSurfaces/" + material);
        if (mat == null || mat.mainTexture == null)
            throw new InvalidOperationException("missing surface material Resources/TrackSurfaces/" + material);
        return mat.mainTexture;
    }

    /// <summary>Writes the road parameters into the renderer's per-material block (merged with SegLabel's).</summary>
    public void Apply(Renderer r)
    {
        var mpb = new MaterialPropertyBlock();
        r.GetPropertyBlock(mpb, 0);
        mpb.SetTexture("_BaseTex", baseTex);
        mpb.SetVector("_BaseWin", new Vector4(surface.vMin, surface.vMax, surface.tileU, surface.tileV));
        mpb.SetVector("_Tint", new Vector4(tint.x, tint.y, tint.z, 1f));   // SetVector: linear, no sRGB conversion
        mpb.SetFloat("_RoadWidth", (float)width);
        mpb.SetVector("_Edge", new Vector4(1f, 0f, (float)(edgeWidth / width), 0f));
        mpb.SetColor("_EdgeColor", edgeColor);
        mpb.SetVector("_Centre", new Vector4(Array.IndexOf(CentreModes, centreMode), (float)(centreWidth / width),
                                             (float)dash, (float)gap));
        mpb.SetColor("_CentreColor", centreColor);
        mpb.SetFloat("_Wear", (float)wear);
        mpb.SetFloat("_LinesOnly", LinesOnly ? 1f : 0f);
        mpb.SetFloat("_Glossiness", surface.gloss);
        r.SetPropertyBlock(mpb, 0);
        // a lines-only road must not shadow the carpet under it
        r.shadowCastingMode = LinesOnly ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
    }

    /// <summary>Floor material of a carpet track: the same carpet, mapped in world space.</summary>
    public Material MakeCarpetGround()
    {
        var mat = new Material(Resources.Load<Shader>("WorldGround"));
        mat.mainTexture = baseTex;
        mat.SetVector("_Tint", new Vector4(tint.x, tint.y, tint.z, 1f));
        mat.SetFloat("_Tile", surface.tileU);
        mat.SetFloat("_Glossiness", surface.gloss);
        return mat;
    }

    public string Describe()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "surface={0};variant={1};ew={2:R};ec={3};centre={4};cw={5:R};cc={6};dash={7:R};gap={8:R};wear={9:R};ground={10}",
            family, variant, edgeWidth, edgeColorName, centreMode, centreWidth, centreColorName, dash, gap, wear, ground);
    }
}
