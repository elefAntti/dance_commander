using System.Text;
using DanceCommander.Providers.Archive;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A zip that 7-Zip and Explorer open and SharpCompress did not: every disk number set to Zip64's
/// "see the extended record" marker (0xFFFF), with the real one, 0, in the Zip64 field. The shape
/// of "21.08.2026 lastNodeId Issue_broken.zip" (2026-10-01), built here byte by byte.
/// </summary>
public class Zip64DiskMarkerTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{ "lastNodeId": 42 }""");

    [Fact]
    public void SharpCompressAloneFailsOnIt()
    {
        // The reason for the fallback, kept as a test so a fixed SharpCompress shows up here.
        using var stream = new MemoryStream(Build());
        Should.Throw<Exception>(() => SharpCompress.Archives.ArchiveFactory.OpenArchive(stream).Entries.ToList());
    }

    [Fact]
    public async Task ItOpensAndItsEntriesRead()
    {
        using var archive = await OpenArchive.OpenAsync(new MemoryStream(Build()), "issue.zip", CancellationToken.None);

        archive.List("/").ShouldHaveSingleItem().Name.ShouldBe("order.json");
        using var read = archive.OpenRead("order.json");
        using var copy = new MemoryStream();
        read.CopyTo(copy);
        copy.ToArray().ShouldBe(Content);
    }

    private static byte[] Build()
    {
        var name = Encoding.UTF8.GetBytes("order.json");
        var crc = Crc32(Content);
        var zip = new MemoryStream();
        var w = new BinaryWriter(zip);

        // Local header: stored, data descriptor follows, sizes in a Zip64 extra.
        w.Write(0x04034b50u); w.Write((ushort)45); w.Write((ushort)0x08); w.Write((ushort)0);
        w.Write((ushort)0); w.Write((ushort)0x5B21); w.Write(0u); w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu);
        w.Write((ushort)name.Length); w.Write((ushort)20); w.Write(name);
        w.Write((ushort)1); w.Write((ushort)16); w.Write(0L); w.Write(0L);
        w.Write(Content);
        w.Write(0x08074b50u); w.Write(crc); w.Write((long)Content.Length); w.Write((long)Content.Length);

        // Central directory: every field that can say "see Zip64" says it, the disk number included.
        var centralAt = zip.Position;
        w.Write(0x02014b50u); w.Write((ushort)(3 << 8 | 45)); w.Write((ushort)45); w.Write((ushort)0x08);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x5B21); w.Write(crc);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)name.Length); w.Write((ushort)32);
        w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0); w.Write(0x81A40000u); w.Write(0xFFFFFFFFu);
        w.Write(name);
        w.Write((ushort)1); w.Write((ushort)28);
        w.Write((long)Content.Length); w.Write((long)Content.Length); w.Write(0L); w.Write(0u);
        var centralSize = zip.Position - centralAt;

        // Zip64 end record and its locator, then the classic end record, all disks 0xFFFF.
        var zip64At = zip.Position;
        w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)(3 << 8 | 45)); w.Write((ushort)45);
        w.Write(0u); w.Write(0u); w.Write(1L); w.Write(1L); w.Write(centralSize); w.Write(centralAt);
        w.Write(0x07064b50u); w.Write(0u); w.Write(zip64At); w.Write(1u);
        w.Write(0x06054b50u); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)1); w.Write((ushort)1);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);

        return zip.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
