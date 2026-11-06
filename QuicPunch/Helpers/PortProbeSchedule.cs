using System.Security.Cryptography;

namespace QuicPunch.Helpers;

/// <summary>A bounded, per-attempt random permutation instead of a fixed port grid.</summary>
internal sealed class PortProbeSchedule
{
    private sealed class Sequence(int next, int step, int width)
    {
        public int Next = next;
        public readonly int Step = step;
        public readonly int Width = width;
    }
    private readonly Dictionary<(int Min, int Max), Sequence> _sequences = new();
    private readonly Random? _testRandom;
    public int Round { get; private set; }
    public PortProbeSchedule(int? seed = null) => _testRandom = seed.HasValue ? new Random(seed.Value) : null;
    public void NextRound() => Round++;
    private int Pick(int maximum) => _testRandom?.Next(maximum) ?? RandomNumberGenerator.GetInt32(maximum);

    private static int Gcd(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }

    public ushort[] Take(int min, int max, int count)
    {
        if (min < 1 || max > 65535 || max < min || count <= 0) return [];
        int width = max - min + 1;
        if (!_sequences.TryGetValue((min, max), out var sequence))
        {
            int step = width == 1 ? 1 : 1 + Pick(width - 1);
            while (Gcd(step, width) != 1) step = step == width - 1 ? 1 : step + 1;
            sequence = new Sequence(Pick(width), step, width);
            _sequences[(min, max)] = sequence;
        }
        ushort[] result = new ushort[Math.Min(count, width)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (ushort)(min + sequence.Next);
            sequence.Next = (sequence.Next + sequence.Step) % sequence.Width;
        }
        return result;
    }
}
