using System;
using System.Text;

/// <summary>
/// Deterministic random generator for track generation (SplitMix64).
/// Identical to Rng in tools/trackgen_ref.py. Track generation must never use
/// UnityEngine.Random, so that a seed reproduces the same track on any machine/run.
/// </summary>
public class SeededRng
{
    ulong s;

    public SeededRng(ulong seed)
    {
        s = seed;
    }

    public ulong NextU64()
    {
        unchecked
        {
            s += 0x9E3779B97F4A7C15UL;
            ulong z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double Uniform()
    {
        return (NextU64() >> 11) * (1.0 / 9007199254740992.0);
    }

    public double Range(double a, double b)
    {
        return a + (b - a) * Uniform();
    }

    /// <summary>Uniform integer in [a, b] (both inclusive).</summary>
    public int IntIncl(int a, int b)
    {
        return a + Math.Min((int)(Uniform() * (b - a + 1)), b - a);
    }

    public static ulong Fnv1a64(byte[] data)
    {
        unchecked
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in data)
            {
                h ^= b;
                h *= 0x100000001B3UL;
            }
            return h;
        }
    }

    public static ulong Fnv1a64(string s)
    {
        return Fnv1a64(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>Independent stream per purpose ("layout", "width", ...).</summary>
    public static SeededRng Sub(ulong seed, string label)
    {
        return new SeededRng(new SeededRng(seed ^ Fnv1a64(label)).NextU64());
    }

    /// <summary>Parses a decimal seed ("123", "-5"); negative values wrap like in Python.</summary>
    public static ulong ParseSeed(string text)
    {
        text = text.Trim();
        if (text.StartsWith("-"))
            return unchecked((ulong)long.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        return ulong.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }
}
