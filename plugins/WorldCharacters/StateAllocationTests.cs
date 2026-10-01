using System;
using System.IO;
using System.Linq;
using ValheimModPack.WorldCharacters;

// Separate runner: validating an existing map must not allocate another map.
internal static class StateAllocationTests
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        ++passed;
    }
    private static void Refuse(byte[] bytes, string name)
    {
        try { StateCodec.WithoutMap(bytes); }
        catch (InvalidDataException) { ++passed; return; }
        catch (EndOfStreamException) { ++passed; return; }
        throw new Exception("Did not refuse: " + name);
    }
    private static byte[] World(int mapSize)
    {
        byte[] bytes = new byte[59 + mapSize]; bytes[0] = 1;
        Buffer.BlockCopy(BitConverter.GetBytes(mapSize), 0, bytes, 55, 4);
        for (int i = 59; i < bytes.Length; ++i) bytes[i] = (byte)(i * 17);
        return bytes;
    }
    private static void Main()
    {
        byte[] original = World(23);
        byte[] positions = StateCodec.WithoutMap(original);
        Check(positions.Length == 59 && positions.Take(55).SequenceEqual(original.Take(55))
            && positions.Skip(55).All(v => v == 0), "position-only format unchanged");
        positions[5] = 7;
        byte[] merged = StateCodec.RetainMap(positions, original);
        Check(merged[5] == 7 && merged.Skip(55).SequenceEqual(original.Skip(55)), "opaque map unchanged by merge");
        Check(StateCodec.WithoutMap(new byte[0]).Length == 0, "empty world remains valid");
        for (int length = 1; length < original.Length; ++length)
            Refuse(original.Take(length).ToArray(), "truncated world at " + length);
        byte[] bad = (byte[])original.Clone(); bad[0] = 2; Refuse(bad, "future world format");
        foreach (int offset in new[] {4, 17, 30})
        { bad = (byte[])original.Clone(); bad[offset] = 2; Refuse(bad, "invalid world flag at " + offset); }
        foreach (float value in new[] {Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity})
        {
            bad = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bad, 5, 4);
            Refuse(bad, "non-finite position");
        }
        foreach (int length in new[] {-1, Int32.MaxValue, 10 * 1024 * 1024 + 1, 24, 22})
        {
            bad = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(length), 0, bad, 55, 4);
            Refuse(bad, "invalid map envelope " + length);
        }
        Refuse(original.Concat(new byte[] {0}).ToArray(), "trailing map data");
        Check(StateCodec.WithoutMap(World(10 * 1024 * 1024)).Length == 59, "map envelope maximum accepted");
        Refuse(World(10 * 1024 * 1024 + 1), "map envelope maximum exceeded");

        AppDomain.MonitoringIsEnabled = true;
        byte[] large = World(6 * 1024 * 1024);
        for (int i = 0; i < 100; ++i) StateCodec.WithoutMap(large);
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int i = 0; i < 100; ++i) StateCodec.WithoutMap(large);
        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Check(allocated < 1024 * 1024, "map validation must allocate less than 1 MiB over 100 passes");
        Console.WriteLine("PASS: " + passed + " map allocation assertions; 100 x 6 MiB validations allocated " + allocated + " bytes.");
    }
}
