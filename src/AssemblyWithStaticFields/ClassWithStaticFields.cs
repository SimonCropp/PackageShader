using System;

namespace AssemblyWithStaticFields;

public static class ClassWithStaticFields
{
    // Static readonly fields with array initializers create FieldRVA entries
    // ReSharper disable once UnusedMember.Global
    public static ReadOnlySpan<byte> ByteData => [1, 2, 3, 4, 5, 6, 7, 8];

    public static ReadOnlySpan<int> IntData => [100, 200, 300, 400, 500];

    // Prime numbers similar to what FrozenSet uses
    public static ReadOnlySpan<int> Primes =>
    [
        3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761,
        919, 1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591
    ];

    public static int GetPrime(int index)
    {
        var primes = Primes;
        if (index < 0 || index >= primes.Length)
            return -1;
        return primes[index];
    }

    public static int SumInts()
    {
        var data = IntData;
        var sum = 0;
        foreach (var value in data)
            sum += value;
        return sum;
    }
}
