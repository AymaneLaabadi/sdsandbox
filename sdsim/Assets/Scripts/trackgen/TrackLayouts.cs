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
    public string shape;     // always "circuit"
    public int corners;
    public double width;
    public List<V2> nodes;   // closed loop, node 0 = start
    public double step;      // node spacing (m)
    public double length;
    public double minRadius;
    public int attempts;
}

/// <summary>
/// Seeded closed-circuit layouts for gen_track. Line-by-line port of tools/trackgen_ref.py
/// (same SplitMix64 draws and order, double precision). Never uses UnityEngine.Random.
///
/// Layout, like a real circuit: seeded points in a box -> convex hull; some hull edges get a
/// notch (hairpins, chicanes; at least one per track); every corner of that polygon is rounded
/// with its own radius -> true straights joined by constant-radius corners. Start = longest straight.
///
/// Hard rules (a candidate that fails is replaced by the next attempt of the same seed):
///   min curve radius >= 1.5 * half width (no folding)
///   centreline self-distance >= width + 3 m for non-neighbouring parts
///   the two road edges never touch or cross
///   length in [80, 500] m
/// </summary>
public static class TrackLayouts
{
    public const double NodeSpacing = 2.0;
    public const double WidthMin = 3.5, WidthMax = 13.5;
    public const double MinRadiusFactor = 1.5;
    public const double Margin = 3.0;
    public const double LenMin = 80.0, LenMax = 500.0;
    public const int MaxAttempts = 500;

    // control polygon (same values as SETTINGS in trackgen_ref.py)
    const double BoxMin = 30.0, BoxMax = 130.0, BoxWidthFactor = 10.0, AspectMin = 0.45;
    const int PointsMin = 6, PointsMax = 16;
    const double NotchPMin = 0.45, NotchPMax = 1.0, NotchDepthMin = 0.2, NotchDepthMax = 0.6;
    const double NotchInwardP = 0.75, NotchMinEdge = 3.0;
    // corners
    const double CornerRMin = 4.0, CornerRMax = 40.0;
    static readonly double MaxTurn = 165.0 * Math.PI / 180.0;
    const double EdgeUse = 0.92, ArcStep = 0.5, StraightCurv = 1.0 / 200.0;

    // ------------------------------------------------------------------ geometry

    static int Mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

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

    // ------------------------------------------------------------------ circuit layouts

    static double Cross(V2 o, V2 a, V2 b) { return (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x); }

    /// <summary>Andrew's monotone chain, counter-clockwise, no collinear points.</summary>
    static List<V2> ConvexHull(List<V2> input)
    {
        var pts = new List<V2>(input);
        pts.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.z.CompareTo(q.z));
        if (pts.Count < 3) return pts;
        var lower = new List<V2>();
        var upper = new List<V2>();
        foreach (V2 p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            V2 p = pts[i];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>Signed heading change at b (radians, + = left turn).</summary>
    static double TurnAngle(V2 a, V2 b, V2 c)
    {
        double d1x = b.x - a.x, d1z = b.z - a.z, d2x = c.x - b.x, d2z = c.z - b.z;
        return Math.Atan2(d1x * d2z - d1z * d2x, d1x * d2x + d1z * d2z);
    }

    static List<V2> ControlPolygon(SeededRng rng, double width)
    {
        double boxW = rng.Range(Math.Max(BoxMin, BoxWidthFactor * width), BoxMax);
        double boxH = boxW * rng.Range(AspectMin, 1.0);
        int nPts = rng.IntIncl(PointsMin, PointsMax);
        var pts = new List<V2>(nPts);
        for (int i = 0; i < nPts; i++)
        {
            double x = rng.Range(0.0, boxW);
            double z = rng.Range(0.0, boxH);
            pts.Add(new V2(x, z));
        }
        List<V2> hull = ConvexHull(pts);
        double pNotch = rng.Range(NotchPMin, NotchPMax);
        int m = hull.Count;
        // every edge draws the same 4 numbers, used or not
        var doNotch = new bool[m];
        var tt = new double[m];
        var depth = new double[m];
        var inward = new bool[m];
        var len = new double[m];
        var eligible = new bool[m];
        bool any = false;
        for (int i = 0; i < m; i++)
        {
            len[i] = V2.Dist(hull[i], hull[(i + 1) % m]);
            bool d = rng.Uniform() < pNotch;
            tt[i] = rng.Range(0.35, 0.65);
            depth[i] = rng.Range(NotchDepthMin, NotchDepthMax);
            inward[i] = rng.Uniform() < NotchInwardP;
            eligible[i] = len[i] >= NotchMinEdge * width;
            doNotch[i] = d && eligible[i];
            any |= doNotch[i];
        }
        if (!any)   // at least one notch per track: the longest eligible edge
        {
            int best = -1;
            for (int i = 0; i < m; i++)
                if (eligible[i] && (best < 0 || len[i] > len[best])) best = i;
            if (best >= 0) doNotch[best] = true;
        }
        var poly = new List<V2>();
        for (int i = 0; i < m; i++)
        {
            V2 a = hull[i], b = hull[(i + 1) % m];
            poly.Add(a);
            if (doNotch[i])
            {
                double L = len[i];
                double nx = -(b.z - a.z) / L, nz = (b.x - a.x) / L;   // left normal = inside of a CCW hull
                double s = depth[i] * L * (inward[i] ? 1.0 : -1.0);
                poly.Add(new V2(a.x + (b.x - a.x) * tt[i] + nx * s, a.z + (b.z - a.z) * tt[i] + nz * s));
            }
        }
        return poly;
    }

    /// <summary>Rounds every corner with its own radius; null if a corner cannot be rounded legally.</summary>
    static List<V2> Fillet(List<V2> poly, double width, SeededRng rng, out int corners)
    {
        corners = 0;
        int n = poly.Count;
        double hw = width / 2.0;
        double rLo = Math.Max(MinRadiusFactor * hw * 1.02, CornerRMin);
        double rHi = Math.Max(CornerRMax, rLo);
        var th = new double[n];
        var t = new double[n];
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            double a = TurnAngle(poly[Mod(i - 1, n)], poly[i], poly[(i + 1) % n]);
            th[i] = a;
            double rr = Math.Exp(rng.Range(Math.Log(rLo), Math.Log(rHi)));   // log-uniform: more tight corners
            r[i] = rr;
            t[i] = rr * Math.Tan(Math.Abs(a) / 2.0);
        }
        for (int i = 0; i < n; i++)
            if (Math.Abs(th[i]) > MaxTurn) return null;
        for (int pass = 0; pass < 2; pass++)   // tangent lengths of one edge must fit on it
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double L = V2.Dist(poly[i], poly[j]);
                double s = t[i] + t[j];
                if (s > EdgeUse * L)
                {
                    double k = EdgeUse * L / s;
                    t[i] *= k;
                    t[j] *= k;
                }
            }
        for (int i = 0; i < n; i++)
        {
            r[i] = Math.Abs(th[i]) > 1e-9 ? t[i] / Math.Tan(Math.Abs(th[i]) / 2.0) : double.PositiveInfinity;
            if (r[i] < rLo) return null;
        }
        var outp = new List<V2>();
        for (int i = 0; i < n; i++)
        {
            V2 p = poly[i], a = poly[Mod(i - 1, n)];
            double la = V2.Dist(a, p);
            double dinx = (p.x - a.x) / la, dinz = (p.z - a.z) / la;
            V2 e = new V2(p.x - dinx * t[i], p.z - dinz * t[i]);
            if (Math.Abs(th[i]) <= 1e-9)
            {
                outp.Add(e);
                continue;
            }
            corners++;
            double sgn = th[i] > 0 ? 1.0 : -1.0;
            V2 c = new V2(e.x - dinz * r[i] * sgn, e.z + dinx * r[i] * sgn);   // centre on the inside
            double a0 = Math.Atan2(e.z - c.z, e.x - c.x);
            int k = Math.Max((int)Math.Ceiling(Math.Abs(th[i]) * r[i] / ArcStep), 2);
            for (int s = 0; s <= k; s++)
            {
                double ang = a0 + th[i] * s / k;
                outp.Add(new V2(c.x + r[i] * Math.Cos(ang), c.z + r[i] * Math.Sin(ang)));
            }
        }
        return outp;
    }

    /// <summary>Node index on the longest (near) straight, a little after its beginning.</summary>
    static int LongestStraightStart(List<V2> nodes)
    {
        int n = nodes.Count;
        var straight = new bool[n];
        bool all = true, none = true;
        for (int i = 0; i < n; i++)
        {
            straight[i] = Curvature(nodes, i) < StraightCurv;
            all &= straight[i];
            none &= !straight[i];
        }
        if (all || none) return 0;
        int first = 0;
        while (straight[first]) first++;   // rotate so a run never wraps
        int bestI = 0, bestLen = -1, k = 0;
        while (k < n)
        {
            if (straight[(first + k) % n])
            {
                int j = k;
                while (j < n && straight[(first + j) % n]) j++;
                if (j - k > bestLen)
                {
                    bestLen = j - k;
                    bestI = (first + k + (int)((j - k) * 0.2)) % n;
                }
                k = j;
            }
            else k++;
        }
        return bestI;
    }

    /// <summary>Returns null if no candidate satisfied the rules within MaxAttempts.</summary>
    public static LayoutResult Generate(ulong seed, double? roadWidth)
    {
        double width = SeededRng.Sub(seed, "width").Range(WidthMin, WidthMax);
        if (roadWidth.HasValue) width = roadWidth.Value;
        SeededRng rng = SeededRng.Sub(seed, "layout");

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            List<V2> poly = ControlPolygon(rng, width);
            double rot = rng.Range(0.0, 2 * Math.PI);
            bool flip = rng.Uniform() < 0.5;
            int corners;
            List<V2> dense = Fillet(poly, width, rng, out corners);
            if (dense == null) continue;

            double c = Math.Cos(rot), sn = Math.Sin(rot);
            var pts = new List<V2>(dense.Count);
            foreach (V2 p in dense) pts.Add(new V2(p.x * c - p.z * sn, p.x * sn + p.z * c));
            if (flip) pts.Reverse();

            double step, L;
            List<V2> nodes = ResampleClosed(pts, NodeSpacing, out step, out L);
            if (CheckRules(nodes, step, width) != "") continue;

            int st = LongestStraightStart(nodes);
            var rotated = new List<V2>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) rotated.Add(nodes[(st + i) % nodes.Count]);
            return new LayoutResult
            {
                shape = "circuit", corners = corners, width = width, nodes = rotated, step = step, length = L,
                minRadius = MinRadius(rotated), attempts = attempt
            };
        }
        return null;
    }
}
