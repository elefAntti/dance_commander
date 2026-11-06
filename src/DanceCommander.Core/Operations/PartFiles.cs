using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Operations;

/// <summary>
/// A file cut into numbered parts, <c>name.001</c>, <c>name.002</c> …, and put back together.
/// </summary>
/// <remarks>
/// The plainest kind of split there is: the parts are the file's bytes in order and nothing else,
/// so <c>copy /b</c>, <c>cat</c> and 7-Zip all join them without us. It exists for servers that
/// take files only up to a size (a Nextcloud whose joining of uploads fails above a hundred
/// megabytes or so), and for anything else with a limit, as floppies once had.
///
/// Plain parts know nothing about each other, so a missing or short one would join into a file
/// that looks whole and isn't. A <c>name.sha256</c> written beside them carries the whole file's
/// hash, in the format <c>sha256sum -c</c> reads, and joining checks it before the joined file
/// gets its name. Parts that are not all one size but the last are refused even without it.
/// </remarks>
public static class PartFiles
{
    /// <summary>Three digits of numbering, as 7-Zip writes them.</summary>
    public const int MostParts = 999;

    private const int BufferSize = 1024 * 1024;
    private const int LargestChecksumFile = 4096;

    public static string PartName(string name, int number) =>
        $"{name}.{number.ToString("D3", CultureInfo.InvariantCulture)}";

    public static string ChecksumName(string name) => name + ".sha256";

    /// <summary>Whether <paramref name="name"/> is the first part of a set: something <c>.001</c>.</summary>
    public static bool IsFirstPart(string name) =>
        name.Length > 4 && name.EndsWith(".001", StringComparison.Ordinal);

    /// <summary>"big.iso.001" is part of "big.iso".</summary>
    public static string WholeName(string firstPart) => firstPart[..^4];

    /// <summary>
    /// "100", "100 MB", "1.5 GB", "1,5g", "500k": a size as typed. A bare number is megabytes,
    /// because that is the size anyone splitting a file has in mind. Units are binary (1 MB is
    /// 1024 × 1024 bytes), as 7-Zip's are. Null for anything else.
    /// </summary>
    public static long? ParseSize(string text)
    {
        var trimmed = text.Trim().Replace(',', '.');
        var digits = 0;
        while (digits < trimmed.Length && (char.IsAsciiDigit(trimmed[digits]) || trimmed[digits] == '.'))
            digits++;

        if (!double.TryParse(trimmed[..digits], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
            return null;

        double? unit = trimmed[digits..].Trim().ToUpperInvariant() switch
        {
            "" or "M" or "MB" or "MIB" => 1024d * 1024,
            "K" or "KB" or "KIB" => 1024d,
            "G" or "GB" or "GIB" => 1024d * 1024 * 1024,
            "B" => 1d,
            _ => null,
        };

        if (unit is null)
            return null;

        var bytes = number * unit.Value;
        return bytes is >= 1 and < long.MaxValue ? (long)bytes : null;
    }

    /// <summary>How many parts a file of <paramref name="size"/> bytes makes.</summary>
    public static long CountParts(long size, long partSize) => (size + partSize - 1) / partSize;

    /// <summary>
    /// Writes <paramref name="source"/> as parts in <paramref name="directory"/>, then the
    /// checksum file. Each part is committed whole, as any write is; if anything fails, the parts
    /// already made are removed again and the source is as it was, since it is only read.
    /// </summary>
    /// <returns>Every file made: the parts in order, then the checksum file.</returns>
    public static async Task<IReadOnlyList<FsUri>> SplitAsync(
        IProviderRegistry providers, FsUri source, FsUri directory, long partSize, CancellationToken ct)
    {
        var reader = providers.Resolve(source);
        var writer = providers.Resolve(directory);

        var before = await reader.StatAsync(source, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"{source.Display} is not there any more.");
        var size = before.Size;
        var count = CountParts(size, partSize);
        if (count < 2)
            throw new InvalidOperationException($"{source.Name} fits in one part of that size.");
        if (count > MostParts)
            throw new InvalidOperationException($"That would make {count:N0} parts; the most is {MostParts}.");

        // Nothing is written over: a set beside it from an earlier split is the user's to remove,
        // and a part of one set joined with another's would be a file that isn't either.
        var names = Enumerable.Range(1, (int)count).Select(n => PartName(source.Name, n))
            .Append(ChecksumName(source.Name)).ToList();
        foreach (var name in names)
        {
            if (await writer.StatAsync(directory.Child(name), ct).ConfigureAwait(false) is not null)
                throw new IOException($"{name} is already there; remove it first or split somewhere else.");
        }

        // One past the end too: a longer set from before would carry on where this one stops, and
        // a join would take its tail for part of this file.
        var beyond = PartName(source.Name, (int)count + 1);
        if (await writer.StatAsync(directory.Child(beyond), ct).ConfigureAwait(false) is not null)
            throw new IOException($"{beyond} is already there, from an earlier split; remove it first or split somewhere else.");

        var made = new List<FsUri>();
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];

            await using (var input = await reader.OpenReadAsync(source, ct).ConfigureAwait(false))
            {
                for (var n = 1; n <= count; n++)
                {
                    var part = directory.Child(PartName(source.Name, n));
                    var length = Math.Min(partSize, size - (n - 1) * partSize);

                    await using var output = await writer.OpenWriteAsync(part, length, ct).ConfigureAwait(false);
                    for (var left = length; left > 0;)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct)
                            .ConfigureAwait(false);
                        if (read == 0)
                            throw new IOException($"{source.Name} ended before its {size:N0} bytes, so it was not split.");

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        left -= read;
                    }

                    await output.CommitAsync(replace: false, ct).ConfigureAwait(false);
                    made.Add(part);
                }

                // The size was read once, at the start. A file still being written (a download, a
                // recording) or rewritten meanwhile would be split cut off, with a checksum that
                // matches the cut-off copy: so it must end where it said, and be as it was.
                if (await input.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
                    throw new IOException($"{source.Name} grew while it was being split, so the parts were removed.");
            }

            var after = await reader.StatAsync(source, ct).ConfigureAwait(false);
            if (after is null || after.Size != before.Size || after.Modified != before.Modified)
                throw new IOException($"{source.Name} changed while it was being split, so the parts were removed.");

            var checksum = directory.Child(ChecksumName(source.Name));
            var line = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(hash.GetHashAndReset())} *{source.Name}\n");
            await using (var output = await writer.OpenWriteAsync(checksum, line.Length, ct).ConfigureAwait(false))
            {
                await output.WriteAsync(line, ct).ConfigureAwait(false);
                await output.CommitAsync(replace: false, ct).ConfigureAwait(false);
            }

            made.Add(checksum);
            return made;
        }
        catch
        {
            await RemoveAsync(writer, made).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The parts of a set, in order, from its first part: as many as follow without a gap.</summary>
    public static async Task<IReadOnlyList<FsEntry>> FindPartsAsync(
        IFileSystemProvider provider, FsUri firstPart, CancellationToken ct)
    {
        var parent = firstPart.Parent ?? firstPart;
        var whole = WholeName(firstPart.Name);
        var parts = new List<FsEntry>();

        for (var n = 1; n <= MostParts; n++)
        {
            if (await provider.StatAsync(parent.Child(PartName(whole, n)), ct).ConfigureAwait(false) is not { IsDirectory: false } part)
                break;
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// Joins the set that starts at <paramref name="firstPart"/> into <paramref name="target"/>,
    /// which must not exist. The joined file gets its name only after its hash has matched the
    /// checksum file, when there is one; a mismatch leaves nothing at the name.
    /// </summary>
    /// <returns>How many parts were joined, and whether a checksum file was there to check against.</returns>
    public static async Task<(int Parts, bool Checked)> JoinAsync(
        IProviderRegistry providers, FsUri firstPart, FsUri target, CancellationToken ct)
    {
        var reader = providers.Resolve(firstPart);
        var writer = providers.Resolve(target);

        var parts = await FindPartsAsync(reader, firstPart, ct).ConfigureAwait(false);
        if (parts.Count == 0)
            throw new FileNotFoundException($"{firstPart.Display} is not there any more.");

        // Every part but the last is the part size, and the last no larger: anything else is a
        // part cut short on its way somewhere, or one from another set.
        var partSize = parts[0].Size;
        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            var fits = i == parts.Count - 1 ? part.Size is > 0 && part.Size <= partSize : part.Size == partSize;
            if (!fits)
                throw new IOException($"{part.Name} is not the size the set says, so a part is incomplete or from another set.");
        }

        // Plain parts carry no count, so where the numbering stops is only where it was found to
        // stop. A part after a gap says one is missing.
        var parent = firstPart.Parent ?? firstPart;
        var whole = WholeName(firstPart.Name);
        if (parts.Count < MostParts - 1
            && await reader.StatAsync(parent.Child(PartName(whole, parts.Count + 2)), ct).ConfigureAwait(false) is not null)
        {
            throw new IOException(
                $"{PartName(whole, parts.Count + 2)} is there but {PartName(whole, parts.Count + 1)} is not: "
                + "a part is missing, or one is left from another split. Nothing was joined.");
        }

        var expected = await ExpectedHashAsync(reader, firstPart, ct).ConfigureAwait(false);

        // Without a checksum, a last part as large as the others may be the end, or the last one
        // to arrive of a longer set: an unknown is not taken as permission to call it whole.
        if (expected is null && parts[^1].Size == partSize)
        {
            throw new IOException(
                $"There is no {ChecksumName(whole)} and the last part, {parts[^1].Name}, is full-size, "
                + "so there is no telling whether more parts belong to the set. Nothing was joined. "
                + $"Put {ChecksumName(whole)} beside the parts, or if you know the set is whole, join it with copy /b.");
        }

        var total = parts.Sum(p => p.Size);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];

        // Disposed without a commit on every way out but the last, which leaves nothing at the name.
        await using var output = await writer.OpenWriteAsync(target, total, ct).ConfigureAwait(false);
        foreach (var part in parts)
        {
            long copied = 0;
            await using var input = await reader.OpenReadAsync(part.Uri, ct).ConfigureAwait(false);
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
            }

            if (copied != part.Size)
                throw new IOException($"Reading {part.Name} gave {copied:N0} of its {part.Size:N0} bytes, so nothing was joined.");
        }

        if (expected is not null && !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected, StringComparison.Ordinal))
        {
            throw new IOException(
                $"The joined file does not match {ChecksumName(whole)}, so it was not kept: of the {parts.Count:N0} parts found, one is damaged or more are missing, or the checksum file is from another file.");
        }

        await output.CommitAsync(replace: false, ct).ConfigureAwait(false);
        return (parts.Count, expected is not null);
    }

    /// <summary>The hash in <c>name.sha256</c> beside the parts, lower-case, or null if there is none.</summary>
    private static async Task<string?> ExpectedHashAsync(IFileSystemProvider provider, FsUri firstPart, CancellationToken ct)
    {
        var file = (firstPart.Parent ?? firstPart).Child(ChecksumName(WholeName(firstPart.Name)));
        var found = await provider.StatAsync(file, ct).ConfigureAwait(false);
        if (found is null)
            return null;

        // There but the wrong size (empty, a truncated copy, or a size the server did not give) is
        // a checksum that can't be read, not the absence of one: the join is not done unchecked.
        if (found is not { IsDirectory: false, Size: > 0 and <= LargestChecksumFile })
            throw new IOException($"{file.Name} is not a SHA-256 checksum file, so the joined file could not be checked.");

        await using var input = await provider.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var text = new StreamReader(input, Encoding.UTF8);
        var first = (await text.ReadLineAsync(ct).ConfigureAwait(false))?.Trim().Split(' ', 2)[0].ToLowerInvariant();

        // A checksum file that is not one is said so, not taken as "nothing to check".
        if (first is not { Length: 64 } || !first.All(char.IsAsciiHexDigit))
            throw new IOException($"{file.Name} is not a SHA-256 checksum file, so the joined file could not be checked.");
        return first;
    }

    private static async Task RemoveAsync(IFileSystemProvider provider, IEnumerable<FsUri> made)
    {
        foreach (var file in made)
        {
            try
            {
#pragma warning disable RS0030 // §1.6: parts this split wrote, moments ago, and nothing else; the source is only read.
                await provider.DeleteAsync(file, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
            }
            catch (Exception)
            {
                // Left beside the source, named for what they are; the failure is already reported.
            }
        }
    }
}
