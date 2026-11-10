using System;
using UnityEngine;
using GameSystems.Persistence;

/// <summary>
/// Fast 64-bit SplitMix pseudo-random number generator for seeding and hashing.
/// </summary>
[System.Serializable]
public struct SplitMix64
{
    public ulong State;

    public SplitMix64(ulong seed)
    {
        State = (seed == 0) ? 0x853c49e6748fea9bUL : seed;
    }

    public ulong NextUInt64()
    {
        ulong z = (State += 0x9e3779b97f4a7c15UL);
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// High-speed, high-quality 128-bit Xorshift+ PRNG with period 2^128 - 1.
/// Value type with zero heap allocations, fully serializable.
/// </summary>
[System.Serializable]
public struct Xorshift128Plus
{
    public ulong S0;
    public ulong S1;

    public Xorshift128Plus(ulong seed)
    {
        var sm = new SplitMix64(seed);
        S0 = sm.NextUInt64();
        S1 = sm.NextUInt64();
        if (S0 == 0 && S1 == 0)
        {
            S0 = 0x123456789abcdef0UL;
            S1 = 0xfedcba9876543210UL;
        }
    }

    public Xorshift128Plus(ulong s0, ulong s1)
    {
        S0 = s0;
        S1 = s1;
        if (S0 == 0 && S1 == 0)
        {
            S0 = 0x123456789abcdef0UL;
            S1 = 0xfedcba9876543210UL;
        }
    }

    public ulong NextUInt64()
    {
        ulong x = S0;
        ulong y = S1;
        S0 = y;
        x ^= x << 23;
        S1 = x ^ y ^ (x >> 17) ^ (y >> 26);
        return S1 + y;
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>
    /// Returns a float in the range [0.0, 1.0).
    /// </summary>
    public float NextFloat()
    {
        return (NextUInt64() >> 40) * (1.0f / 16777216.0f);
    }

    public float NextFloat(float min, float max)
    {
        return min + (NextFloat() * (max - min));
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive) return minInclusive;
        uint range = (uint)(maxExclusive - minInclusive);
        return (int)(minInclusive + (NextUInt32() % range));
    }

    public Vector2 NextInsideUnitCircle()
    {
        float angle = NextFloat() * Mathf.PI * 2.0f;
        float radius = Mathf.Sqrt(NextFloat());
        return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
    }

    public Vector3 NextInsideUnitSphere()
    {
        float u = NextFloat();
        float v = NextFloat();
        float theta = u * 2.0f * Mathf.PI;
        float phi = Mathf.Acos(2.0f * v - 1.0f);
        float r = Mathf.Pow(NextFloat(), 1.0f / 3.0f);
        float sinPhi = Mathf.Sin(phi);
        return new Vector3(r * sinPhi * Mathf.Cos(theta), r * sinPhi * Mathf.Sin(theta), r * Mathf.Cos(phi));
    }
}

/// <summary>
/// Fast mathematical coherent noise library featuring OpenSimplex2, Worley cellular noise,
/// Fractal Brownian Motion (fBm), and Domain Warping in both 2D and 3D.
/// </summary>
public static class FastNoiseCore
{
    private const float SKEW_2D = 0.366025403784439f; // 0.5 * (sqrt(3) - 1)
    private const float UNSKEW_2D = 0.211324865405187f; // (3 - sqrt(3)) / 6

    private const float SKEW_3D = 1.0f / 3.0f;
    private const float UNSKEW_3D = 1.0f / 6.0f;

    // Fast deterministic integer hash
    public static int Hash(int x, int y, long seed)
    {
        int h = (int)seed ^ (x * 374761393) ^ (y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    public static int Hash(int x, int y, int z, long seed)
    {
        int h = (int)seed ^ (x * 374761393) ^ (y * 668265263) ^ (z * 951234569);
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    // =========================================================================
    // OPENSIMPLEX2 NOISE (2D & 3D)
    // =========================================================================

    public static float OpenSimplex2_2D(float x, float y, long seed = 0)
    {
        float s = (x + y) * SKEW_2D;
        int i = Mathf.FloorToInt(x + s);
        int j = Mathf.FloorToInt(y + s);

        float t = (i + j) * UNSKEW_2D;
        float X0 = i - t;
        float Y0 = j - t;
        float x0 = x - X0;
        float y0 = y - Y0;

        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; }
        else { i1 = 0; j1 = 1; }

        float x1 = x0 - i1 + UNSKEW_2D;
        float y1 = y0 - j1 + UNSKEW_2D;
        float x2 = x0 - 1.0f + 2.0f * UNSKEW_2D;
        float y2 = y0 - 1.0f + 2.0f * UNSKEW_2D;

        float n0 = 0f, n1 = 0f, n2 = 0f;

        float t0 = 0.5f - x0 * x0 - y0 * y0;
        if (t0 > 0)
        {
            t0 *= t0;
            int gi = Hash(i, j, seed) & 7;
            n0 = t0 * t0 * Grad2D(gi, x0, y0);
        }

        float t1 = 0.5f - x1 * x1 - y1 * y1;
        if (t1 > 0)
        {
            t1 *= t1;
            int gi = Hash(i + i1, j + j1, seed) & 7;
            n1 = t1 * t1 * Grad2D(gi, x1, y1);
        }

        float t2 = 0.5f - x2 * x2 - y2 * y2;
        if (t2 > 0)
        {
            t2 *= t2;
            int gi = Hash(i + 1, j + 1, seed) & 7;
            n2 = t2 * t2 * Grad2D(gi, x2, y2);
        }

        return 70.0f * (n0 + n1 + n2);
    }

    public static float OpenSimplex2_3D(float x, float y, float z, long seed = 0)
    {
        float s = (x + y + z) * SKEW_3D;
        int i = Mathf.FloorToInt(x + s);
        int j = Mathf.FloorToInt(y + s);
        int k = Mathf.FloorToInt(z + s);

        float t = (i + j + k) * UNSKEW_3D;
        float x0 = x - (i - t);
        float y0 = y - (j - t);
        float z0 = z - (k - t);

        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }

        float x1 = x0 - i1 + UNSKEW_3D;
        float y1 = y0 - j1 + UNSKEW_3D;
        float z1 = z0 - k1 + UNSKEW_3D;

        float x2 = x0 - i2 + 2.0f * UNSKEW_3D;
        float y2 = y0 - j2 + 2.0f * UNSKEW_3D;
        float z2 = z0 - k2 + 2.0f * UNSKEW_3D;

        float x3 = x0 - 1.0f + 3.0f * UNSKEW_3D;
        float y3 = y0 - 1.0f + 3.0f * UNSKEW_3D;
        float z3 = z0 - 1.0f + 3.0f * UNSKEW_3D;

        float n0 = 0f, n1 = 0f, n2 = 0f, n3 = 0f;

        float t0 = 0.6f - x0 * x0 - y0 * y0 - z0 * z0;
        if (t0 > 0)
        {
            t0 *= t0;
            n0 = t0 * t0 * Grad3D(Hash(i, j, k, seed) & 15, x0, y0, z0);
        }

        float t1 = 0.6f - x1 * x1 - y1 * y1 - z1 * z1;
        if (t1 > 0)
        {
            t1 *= t1;
            n1 = t1 * t1 * Grad3D(Hash(i + i1, j + j1, k + k1, seed) & 15, x1, y1, z1);
        }

        float t2 = 0.6f - x2 * x2 - y2 * y2 - z2 * z2;
        if (t2 > 0)
        {
            t2 *= t2;
            n2 = t2 * t2 * Grad3D(Hash(i + i2, j + j2, k + k2, seed) & 15, x2, y2, z2);
        }

        float t3 = 0.6f - x3 * x3 - y3 * y3 - z3 * z3;
        if (t3 > 0)
        {
            t3 *= t3;
            n3 = t3 * t3 * Grad3D(Hash(i + 1, j + 1, k + 1, seed) & 15, x3, y3, z3);
        }

        return 32.0f * (n0 + n1 + n2 + n3);
    }

    private static float Grad2D(int hash, float x, float y)
    {
        switch (hash & 7)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x;
            case 5: return -x;
            case 6: return y;
            default: return -y;
        }
    }

    private static float Grad3D(int hash, float x, float y, float z)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    // =========================================================================
    // WORLEY (CELLULAR) NOISE (2D & 3D)
    // =========================================================================

    public static void Worley2D(float x, float y, out float f1, out float f2, long seed = 0)
    {
        int cellX = Mathf.FloorToInt(x);
        int cellY = Mathf.FloorToInt(y);

        f1 = float.MaxValue;
        f2 = float.MaxValue;

        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int cx = cellX + offsetX;
                int cy = cellY + offsetY;

                int h = Hash(cx, cy, seed);
                float px = cx + ((h & 0xFFFF) / 65535.0f);
                float py = cy + (((h >> 16) & 0xFFFF) / 65535.0f);

                float dx = px - x;
                float dy = py - y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (dist < f1)
                {
                    f2 = f1;
                    f1 = dist;
                }
                else if (dist < f2)
                {
                    f2 = dist;
                }
            }
        }
    }

    public static void Worley3D(float x, float y, float z, out float f1, out float f2, long seed = 0)
    {
        int cellX = Mathf.FloorToInt(x);
        int cellY = Mathf.FloorToInt(y);
        int cellZ = Mathf.FloorToInt(z);

        f1 = float.MaxValue;
        f2 = float.MaxValue;

        for (int ox = -1; ox <= 1; ox++)
        {
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int oz = -1; oz <= 1; oz++)
                {
                    int cx = cellX + ox;
                    int cy = cellY + oy;
                    int cz = cellZ + oz;

                    int h = Hash(cx, cy, cz, seed);
                    float px = cx + ((h & 0x7FF) / 2047.0f);
                    float py = cy + (((h >> 11) & 0x7FF) / 2047.0f);
                    float pz = cz + (((h >> 22) & 0x7FF) / 2047.0f);

                    float dx = px - x;
                    float dy = py - y;
                    float dz = pz - z;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (dist < f1)
                    {
                        f2 = f1;
                        f1 = dist;
                    }
                    else if (dist < f2)
                    {
                        f2 = dist;
                    }
                }
            }
        }
    }

    // =========================================================================
    // FRACTAL BROWNIAN MOTION (fBm)
    // =========================================================================

    public static float FractalBrownianMotion2D(float x, float y, int octaves, float persistence = 0.5f, float lacunarity = 2.0f, long seed = 0)
    {
        float total = 0f;
        float frequency = 1f;
        float amplitude = 1f;
        float maxValue = 0f;

        for (int i = 0; i < octaves; i++)
        {
            total += OpenSimplex2_2D(x * frequency, y * frequency, seed + i) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return maxValue > 0f ? (total / maxValue) : 0f;
    }

    public static float FractalBrownianMotion3D(float x, float y, float z, int octaves, float persistence = 0.5f, float lacunarity = 2.0f, long seed = 0)
    {
        float total = 0f;
        float frequency = 1f;
        float amplitude = 1f;
        float maxValue = 0f;

        for (int i = 0; i < octaves; i++)
        {
            total += OpenSimplex2_3D(x * frequency, y * frequency, z * frequency, seed + i) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return maxValue > 0f ? (total / maxValue) : 0f;
    }

    // =========================================================================
    // DOMAIN WARPING (2D & 3D)
    // =========================================================================

    public static void DomainWarp2D(float x, float y, float warpStrength, out float warpedX, out float warpedY, long seed = 0)
    {
        float qx = FractalBrownianMotion2D(x, y, 4, 0.5f, 2.0f, seed);
        float qy = FractalBrownianMotion2D(x + 5.2f, y + 1.3f, 4, 0.5f, 2.0f, seed + 1);

        warpedX = x + (qx * warpStrength);
        warpedY = y + (qy * warpStrength);
    }

    public static void DomainWarp3D(float x, float y, float z, float warpStrength, out float warpedX, out float warpedY, out float warpedZ, long seed = 0)
    {
        float qx = FractalBrownianMotion3D(x, y, z, 3, 0.5f, 2.0f, seed);
        float qy = FractalBrownianMotion3D(x + 5.2f, y + 1.3f, z + 2.8f, 3, 0.5f, 2.0f, seed + 1);
        float qz = FractalBrownianMotion3D(x + 1.7f, y + 9.2f, z + 4.1f, 3, 0.5f, 2.0f, seed + 2);

        warpedX = x + (qx * warpStrength);
        warpedY = y + (qy * warpStrength);
        warpedZ = z + (qz * warpStrength);
    }
}

/// <summary>
/// Component wrapper providing Inspector-configurable PRNG instances and noise visualization
/// with dual 2D/3D compatibility and ISaveableSystem integration.
/// </summary>
[DisallowMultipleComponent]
public class DeterministicPRNGAndNoiseLibrary : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Seed")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private ulong initialSeed = 1337420UL;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = false;
    [SerializeField] private int gizmoGridResolution = 16;
    [SerializeField] private float gizmoGridSpacing = 1.0f;

    // Active state PRNG
    private Xorshift128Plus rng;

    public string SaveKey => "DeterministicPRNG_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public ref Xorshift128Plus RNG => ref rng;

    private void Awake()
    {
        rng = new Xorshift128Plus(initialSeed);

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }
    }

    private void OnDestroy()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    public void Reseed(ulong seed)
    {
        initialSeed = seed;
        rng = new Xorshift128Plus(seed);
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public ulong S0;
        public ulong S1;
        public ulong InitialSeed;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            S0 = rng.S0,
            S1 = rng.S1,
            InitialSeed = initialSeed
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        initialSeed = container.InitialSeed;
        rng = new Xorshift128Plus(container.S0, container.S1);
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 origin = transform.position;
        int half = gizmoGridResolution / 2;

        for (int x = -half; x < half; x++)
        {
            for (int y = -half; y < half; y++)
            {
                float sampleX = origin.x + (x * gizmoGridSpacing);
                float sampleY = origin.y + (y * gizmoGridSpacing);

                float val;
                if (defaultTo2D)
                {
                    val = FastNoiseCore.FractalBrownianMotion2D(sampleX * 0.1f, sampleY * 0.1f, 3, 0.5f, 2.0f, (long)initialSeed);
                    val = Mathf.Clamp01((val + 1f) * 0.5f);
                    Gizmos.color = new Color(val, val, val, 0.7f);
                    Gizmos.DrawWireCube(new Vector3(sampleX, sampleY, 0f), Vector3.one * (gizmoGridSpacing * 0.8f));
                }
                else
                {
                    for (int z = -half; z < half; z += 4)
                    {
                        float sampleZ = origin.z + (z * gizmoGridSpacing);
                        val = FastNoiseCore.FractalBrownianMotion3D(sampleX * 0.1f, sampleY * 0.1f, sampleZ * 0.1f, 3, 0.5f, 2.0f, (long)initialSeed);
                        val = Mathf.Clamp01((val + 1f) * 0.5f);
                        Gizmos.color = new Color(val, val, val, 0.4f);
                        Gizmos.DrawWireSphere(new Vector3(sampleX, sampleY, sampleZ), gizmoGridSpacing * 0.4f);
                    }
                }
            }
        }
    }
}
