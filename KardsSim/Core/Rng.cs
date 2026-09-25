namespace KardsSim.Core;

/// <summary>
/// 确定性随机源（xoshiro 风格）。同一 seed 必须给出完全相同的对局，
/// 否则 AI 训练无法复现。
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1;

    public Rng(int seed)
    {
        _s0 = (ulong)seed * 0x9E3779B97F4A7C15UL + 0x243F6A8885A308D3UL;
        _s1 = (ulong)seed ^ 0x13198A2E03707344UL;
        if (_s0 == 0 && _s1 == 0) _s0 = 1;
        for (var i = 0; i < 8; i++) NextULong();
    }

    public ulong NextULong()
    {
        var x = _s0;
        var y = _s1;
        var r = x + y;
        y ^= x;
        _s0 = Rotl(x, 55) ^ y ^ (y << 14);
        _s1 = Rotl(y, 36);
        return r;
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>[0, n) 的均匀整数。</summary>
    public int Next(int n)
    {
        if (n <= 1) return 0;
        var limit = ulong.MaxValue - ulong.MaxValue % (ulong)n;
        ulong v;
        do { v = NextULong(); } while (v >= limit);
        return (int)(v % (ulong)n);
    }

    /// <summary>Fisher-Yates 原地洗牌。</summary>
    public void Shuffle<T>(IList<T> l)
    {
        for (var i = l.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (l[i], l[j]) = (l[j], l[i]);
        }
    }
}