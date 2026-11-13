using System;
using System.Collections.Generic;

/// <summary>2D point on the ground plane (Unity x/z), double precision.</summary>
public struct V2
{
    public double x, z;
    public V2(double x, double z) { this.x = x; this.z = z; }
    public static double Dist(V2 a, V2 b) { double dx = a.x - b.x, dz = a.z - b.z; return Math.Sqrt(dx * dx + dz * dz); }
}

public class LayoutResult
{
    public string shape;
    public double width;
    public List<V2> nodes;   // closed loop, node 0 = start
    public double step;      // node spacing (m)
    public double length;
    public double minRadius;
    public int attempts;
}

/// <summary>
/// Seeded closed-loop track layouts for gen_track. Line-by-line port of tools/trackgen_ref.py
/// (same SplitMix64 draws, double precision). Never uses UnityEngine.Random.
///
/// Hard rules (a candidate that fails is replaced by the next attempt of the same seed):
///   min curve radius >= 1.5 * half width (no folding)
///   centreline self-distance >= width + 3 m for non-neighbouring parts
///   the two road edges never touch or cross
///   length in [80, 450] m, control-point radii in [15, 70] m
/// </summary>
public static class TrackLayouts
{
    public const double NodeSpacing = 2.0;
    public const double WidthMin = 3.5, WidthMax = 13.5;
    public const double MinRadiusFactor = 1.5;
    public const double Margin = 3.0;
    public const double LenMin = 80.0, LenMax = 450.0;
    public const double RMin = 15.0, RMax = 70.0;
    public const int MaxAttempts = 500;
    public static readonly string[] Shapes = { "oval", "polar", "s_bends" };   // equal probability

    // ------------------------------------------------------------------ geometry

    static int Mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

    static List<V2> CatmullRomClosed(List<V2> ctrl, int samples = 32, double alpha = 0.5)
    {
        int n = ctrl.Count;
        var outp = new List<V2>(n * samples);
        for (int i = 0; i < n; i++)
        {
            V2 p0 = ctrl[Mod(i - 1, n)], p1 = ctrl[i], p2 = ctrl[(i + 1) % n], p3 = ctrl[(i + 2) % n];
            double t0 = 0.0;
            double t1 = t0 + Math.Pow(Math.Max(V2.Dist(p0, p1), 1e-6), alpha);
            double t2 = t1 + Math.Pow(Math.Max(V2.Dist(p1, p2), 1e-6), alpha);
            double t3 = t2 + Math.Pow(Math.Max(V2.Dist(p2, p3), 1e-6), alpha);
            for (int k = 0; k < samples; k++)
            {
                double t = t1 + (t2 - t1) * k / samples;
                outp.Add(new V2(CR(p0.x, p1.x, p2.x, p3.x, t0, t1, t2, t3, t), CR(p0.z, p1.z, p2.z, p3.z, t0, t1, t2, t3, t)));
            }
        }
        return outp;
    }

    static double CR(double p0, double p1, double p2, double p3, double t0, double t1, double t2, double t3, double t)
    {
        double a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
        double a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
        double a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
        double b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
        double b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
        return (t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2;
    }

    /// <summary>Equal arc-length nodes; spacing adjusted so the loop closes evenly.</summary>
    static List<V2> ResampleClosed(List<V2> pts, double spacing, out double step, out double length)
    {
        int m = pts.Count;
        double L = 0.0;
        for (int i = 0; i < m; i++) L += V2.Dist(pts[i], pts[(i + 1) % m]);
        int n = Math.Max((int)Math.Round(L / spacing, MidpointRounding.ToEven), 8);
        step = L / n;
        length = L;
        var outp = new List<V2>(n);
        int si = 0;
        double acc = 0.0;
        for (int k = 0; k < n; k++)
        {
            double s = k * step;
            while (si < m - 1 && acc + V2.Dist(pts[si], pts[si + 1]) < s)
            {
                acc += V2.Dist(pts[si], pts[si + 1]);
                si++;
            }
            V2 a = pts[si], b = pts[(si + 1) % m];
            double d = V2.Dist(a, b);
            double u = d < 1e-12 ? 0.0 : Math.Min(Math.Max((s - acc) / d, 0.0), 1.0);
            outp.Add(new V2(a.x + (b.x - a.x) * u, a.z + (b.z - a.z) * u));
        }
        return outp;
    }

    static double Curvature(List<V2> nodes, int i)
    {
        int n = nodes.Count;
        V2 a = nodes[Mod(i - 1, n)], b = nodes[i], c = nodes[(i + 1) % n];
        double cr = Math.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x));
        return 2.0 * cr / Math.Max(V2.Dist(a, b) * V2.Dist(b, c) * V2.Dist(c, a), 1e-12);
    }

    public static double MinRadius(List<V2> nodes)
    {
        double k = 0.0;
        for (int i = 0; i < nodes.Count; i++) k = Math.Max(k, Curvature(nodes, i));
        return k <= 0.0 ? double.PositiveInfinity : 1.0 / k;
    }

    /// <summary>Min distance between centreline nodes that are not neighbours along the loop.</summary>
    public static double SelfDistance(List<V2> nodes, double step, double width)
    {
        int n = nodes.Count;
        int k0 = (int)Math.Ceiling(0.5 * Math.PI * (width + Margin) / step);
        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
            for (int j = i + k0; j < n; j++)
                if (n - (j - i) >= k0)
                    best = Math.Min(best, V2.Dist(nodes[i], nodes[j]));
        return best;
    }

    /// <summary>Left/right road boundaries exactly as RoadBuilder.BuildGeneratedRoad builds them.</summary>
    public static void RoadEdges(List<V2> nodes, double halfWidth, List<V2> left, List<V2> right)
    {
        int n = nodes.Count;
        for (int i = 0; i < n; i++)
        {
            V2 p = nodes[i], a = nodes[Mod(i - 1, n)], b = nodes[(i + 1) % n];
            double tx = b.x - a.x, tz = b.z - a.z;
            double L = Math.Sqrt(tx * tx + tz * tz);
            double sx = -tz / L, sz = tx / L;   // Cross(tangent, up)
            left.Add(new V2(p.x + sx * halfWidth, p.z + sz * halfWidth));
            right.Add(new V2(p.x - sx * halfWidth, p.z - sz * halfWidth));
        }
    }

    static bool SegmentsTouch(V2 a, V2 b, V2 c, V2 d)
    {
        double rx = b.x - a.x, rz = b.z - a.z, qx = d.x - c.x, qz = d.z - c.z;
        double den = rx * qz - rz * qx;
        if (Math.Abs(den) < 1e-12) return false;
        double t = ((c.x - a.x) * qz - (c.z - a.z) * qx) / den;
        double u = ((c.x - a.x) * rz - (c.z - a.z) * rx) / den;
        return t >= 0.0 && t <= 1.0 && u >= 0.0 && u <= 1.0;
    }

    /// <summary>True if road-boundary segments of non-neighbouring quads touch or cross.</summary>
    public static bool EdgesTouch(List<V2> nodes, double halfWidth)
    {
        var left = new List<V2>();
        var right = new List<V2>();
        RoadEdges(nodes, halfWidth, left, right);
        int n = nodes.Count;
        var a = new V2[2 * n];
        var b = new V2[2 * n];
        var idx = new int[2 * n];
        for (int i = 0; i < n; i++)
        {
            a[i] = left[i]; b[i] = left[(i + 1) % n]; idx[i] = i;
            a[n + i] = right[i]; b[n + i] = right[(i + 1) % n]; idx[n + i] = i;
        }
        for (int x = 0; x < 2 * n; x++)
            for (int y = x + 1; y < 2 * n; y++)
            {
                int d = Mod(idx[x] - idx[y], n);
                if (Math.Min(d, n - d) <= 1) continue;
                if (SegmentsTouch(a[x], b[x], a[y], b[y])) return true;
            }
        return false;
    }

    /// <summary>"" if all hard rules hold, else the name of the first violated rule.</summary>
    public static string CheckRules(List<V2> nodes, double step, double width)
    {
        double hw = width / 2.0;
        if (MinRadius(nodes) < MinRadiusFactor * hw) return "min radius";
        if (SelfDistance(nodes, step, width) < width + Margin) return "self distance";
        double L = step * nodes.Count;
        if (L < LenMin || L > LenMax) return "length";
        if (EdgesTouch(nodes, hw)) return "edges touch";
        return "";
    }

    // ------------------------------------------------------------------ shapes (unit radius)

    static List<V2> ShapePoints(string shape, SeededRng rng, out double rMinU, out double rMaxU)
    {
        int n;
        Func<int, double, double> f;
        if (shape == "oval")
        {
            n = rng.IntIncl(12, 16);
            double asp = rng.Range(1.0, 3.0);
            f = (i, a) => rng.Range(0.96, 1.04) / Math.Sqrt(Math.Pow(Math.Cos(a) / asp, 2) + Math.Pow(Math.Sin(a), 2));
        }
        else if (shape == "polar")
        {
            n = rng.IntIncl(6, 14);
            double q = rng.Range(1.1, 3.5);
            f = (i, a) => rng.Range(1.0, q);
        }
        else // s_bends: alternating inner/outer control points
        {
            n = 2 * rng.IntIncl(3, 5);
            double q = rng.Range(1.3, 2.0);
            f = (i, a) => (i % 2 == 0 ? q : 1.0) * rng.Range(0.95, 1.05);
        }
        double step = 2 * Math.PI / n;
        var ctrl = new List<V2>(n);
        rMinU = double.PositiveInfinity;
        rMaxU = 0.0;
        for (int i = 0; i < n; i++)
        {
            double a = i * step + rng.Range(-0.3, 0.3) * step;
            double r = f(i, a);
            ctrl.Add(new V2(r * Math.Cos(a), r * Math.Sin(a)));
            rMinU = Math.Min(rMinU, r);
            rMaxU = Math.Max(rMaxU, r);
        }
        return CatmullRomClosed(ctrl);
    }

    static int StraightestStart(List<V2> nodes, int window = 5)
    {
        int n = nodes.Count;
        var curv = new double[n];
        for (int i = 0; i < n; i++) curv[i] = Curvature(nodes, i);
        int bestI = 0;
        double bestV = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double v = 0.0;
            for (int k = -window; k <= window; k++) v = Math.Max(v, curv[Mod(i + k, n)]);
            if (v < bestV - 1e-12) { bestI = i; bestV = v; }
        }
        return bestI;
    }

    /// <summary>Returns null if no candidate satisfied the rules within MaxAttempts.</summary>
    public static LayoutResult Generate(ulong seed, double? roadWidth, string shapeOverride)
    {
        double width = SeededRng.Sub(seed, "width").Range(WidthMin, WidthMax);
        if (roadWidth.HasValue) width = roadWidth.Value;
        string shape = Shapes[SeededRng.Sub(seed, "shape").IntIncl(0, Shapes.Length - 1)];
        if (!string.IsNullOrEmpty(shapeOverride)) shape = shapeOverride;
        double hw = width / 2.0;
        SeededRng rng = SeededRng.Sub(seed, "layout");

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            double rMinU, rMaxU;
            List<V2> dense = ShapePoints(shape, rng, out rMinU, out rMaxU);
            double rot = rng.Range(0.0, 2 * Math.PI);
            bool flip = rng.Uniform() < 0.5;
            double u = rng.Uniform();

            double stepU, LU;
            double minrU = MinRadius(ResampleClosed(dense, 0.05, out stepU, out LU));
            double sLo = Math.Max(Math.Max(MinRadiusFactor * hw / minrU, LenMin / LU), RMin / rMinU);
            double sHi = Math.Min(LenMax / LU, RMax / rMaxU);
            if (sLo > sHi) continue;
            double s = sLo + (sHi - sLo) * u;

            double c = Math.Cos(rot), sn = Math.Sin(rot);
            var pts = new List<V2>(dense.Count);
            foreach (V2 p in dense) pts.Add(new V2(s * (p.x * c - p.z * sn), s * (p.x * sn + p.z * c)));
            if (flip) pts.Reverse();

            double step, L;
            List<V2> nodes = ResampleClosed(pts, NodeSpacing, out step, out L);
            if (CheckRules(nodes, step, width) != "") continue;

            int st = StraightestStart(nodes);
            var rotated = new List<V2>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) rotated.Add(nodes[(st + i) % nodes.Count]);
            return new LayoutResult
            {
                shape = shape, width = width, nodes = rotated, step = step, length = L,
                minRadius = MinRadius(rotated), attempts = attempt
            };
        }
        return null;
    }
}
