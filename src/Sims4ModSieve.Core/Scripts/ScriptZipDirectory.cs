using System.Buffers.Binary;
using System.Text;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using Sims4ModSieve.Core.Conflicts;

namespace Sims4ModSieve.Core.Scripts;

/// <summary>A bounded ZIP directory reader for script archives; never executes entry contents.</summary>
internal sealed class ScriptZipDirectory
{
    private const uint DirectorySignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private const uint EndSignature = 0x06054b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const int ChunkBytes = 64 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding LegacyNameEncoding = CodePagesEncodingProvider.Instance.GetEncoding(437)!;

    private ScriptZipDirectory(IReadOnlyList<ScriptZipEntry> entries) => Entries = entries;

    public IReadOnlyList<ScriptZipEntry> Entries { get; }

    public static async Task<ScriptZipDirectory> ReadAsync(
        Stream stream, ScriptModuleLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanSeek) throw new NotSupportedException("ZIP directory requires a seekable stream.");
        var length = stream.Length;
        if (length < 22) throw Error(ScriptArchiveIssueCode.NotZip, "Missing ZIP end record.");
        if (length > limits.MaxArchiveBytes) throw Error(ScriptArchiveIssueCode.TooLarge, "Archive exceeds byte limit.");

        var tail = new byte[(int)Math.Min(length, 22 + ushort.MaxValue + 20)];
        stream.Position = length - tail.Length;
        try
        {
            await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
            var end = ParseEnd(tail, length, limits);
            stream.Position = end.DirectoryOffset;
            var entries = new List<ScriptZipEntry>();
            var header = new byte[46];
            while (stream.Position < end.DirectoryEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Check the actual count BEFORE reading/allocating the next record. EOCD is untrusted.
                if (entries.Count >= limits.MaxEntriesPerArchive)
                    throw Error(ScriptArchiveIssueCode.EntryCountExceedsLimit, "Actual directory count exceeds entry limit.");
                if (end.DirectoryEnd - stream.Position < header.Length)
                    throw Error(ScriptArchiveIssueCode.Corrupt, "Truncated central directory header.");
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var fields = ParseDirectoryHeader(header);
                var variableLength = (long)fields.NameLength + fields.ExtraLength + fields.CommentLength;
                if (variableLength > end.DirectoryEnd - stream.Position)
                    throw Error(ScriptArchiveIssueCode.Corrupt, "Central directory record crosses its boundary.");
                var name = new byte[fields.NameLength];
                await stream.ReadExactlyAsync(name, cancellationToken).ConfigureAwait(false);
                var extra = new byte[fields.ExtraLength];
                await stream.ReadExactlyAsync(extra, cancellationToken).ConfigureAwait(false);
                ValidateExtra(extra, ScriptArchiveIssueCode.Corrupt, ScriptArchiveIssueCode.Zip64Unsupported);
                stream.Position += fields.CommentLength;
                entries.Add(new ScriptZipEntry(
                    DecodeName(name, fields.Flags), fields.Method, fields.Flags, fields.CompressedLength,
                    fields.Length, fields.Crc, fields.Offset, fields.Timestamp, name));
            }
            if (entries.Count != end.EntryCount)
                throw Error(ScriptArchiveIssueCode.Corrupt, "Actual central directory count differs from EOCD.");

            // Entry data must not cross the next local record or the central directory.
            var byOffset = entries.OrderBy(entry => entry.LocalHeaderOffset).ToArray();
            long boundary = end.DirectoryOffset;
            for (var index = byOffset.Length - 1; index >= 0; index--)
            {
                var entry = byOffset[index];
                entry.DataBoundary = boundary;
                if (index == 0 || byOffset[index - 1].LocalHeaderOffset != entry.LocalHeaderOffset)
                    boundary = Math.Min(boundary, entry.LocalHeaderOffset);
            }
            return new ScriptZipDirectory(entries.AsReadOnly());
        }
        catch (EndOfStreamException exception)
        {
            throw Error(ScriptArchiveIssueCode.Corrupt, "Truncated ZIP directory.", exception);
        }
    }

    /// <summary>Delivers bounded output, validating CRC, length and the actual DEFLATE end marker.</summary>
    public async Task<long> ReadEntryAsync(
        Stream stream, ScriptZipEntry entry, ScriptModuleLimits limits, ResourceContentBudget budget,
        Action<byte[], int> append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Length > limits.MaxModuleBytes)
            throw Error(ScriptArchiveIssueCode.EntrySizeExceedsLimit, "Declared output exceeds module byte limit.");
        if ((entry.Flags & ~0x080e) != 0 || entry.CompressionMethod is not (0 or 8))
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Encrypted entry, unsupported flags or compression method.");
        try
        {
            var dataStart = await ValidateLocalHeaderAsync(stream, entry, cancellationToken).ConfigureAwait(false);
            var dataEnd = dataStart + entry.CompressedLength;
            if (dataEnd > entry.DataBoundary)
                throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Entry data crosses a ZIP record boundary.");
            stream.Position = dataStart;
            var crc = new Crc32();
            var output = new byte[ChunkBytes];
            long total;
            if (entry.CompressionMethod == 0)
            {
                if (entry.CompressedLength != entry.Length)
                    throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Stored entry sizes differ.");
                total = 0;
                while (total < entry.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var want = (int)Math.Min(output.Length, entry.Length - total);
                    using var reservation = await budget.ReserveUpToAsync(want, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (reservation.GrantedBytes == 0) throw BudgetError();
                    var read = await stream.ReadAsync(output.AsMemory(0, reservation.GrantedBytes), cancellationToken)
                        .ConfigureAwait(false);
                    reservation.Complete(read);
                    if (read == 0) throw new EndOfStreamException();
                    total += read;
                    crc.Update(new ArraySegment<byte>(output, 0, read));
                    append(output, read);
                }
            }
            else
            {
                total = await InflateAsync(stream, entry, limits, budget, output, crc, append, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (total != entry.Length || (uint)crc.Value != entry.Crc32)
                throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Output length or CRC-32 differs from central directory.");
            if ((entry.Flags & 8) != 0)
                await ValidateDescriptorAsync(stream, entry, dataEnd, cancellationToken).ConfigureAwait(false);
            return total;
        }
        catch (Exception exception) when (exception is EndOfStreamException or SharpZipBaseException)
        {
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Entry is truncated or its compressed data is invalid.", exception);
        }
    }

    private static async Task<long> InflateAsync(
        Stream stream, ScriptZipEntry entry, ScriptModuleLimits limits, ResourceContentBudget budget,
        byte[] output, Crc32 crc, Action<byte[], int> append, CancellationToken cancellationToken)
    {
        var inflater = new Inflater(noHeader: true);
        var input = new byte[ChunkBytes];
        long remaining = entry.CompressedLength;
        long total = 0;
        var zeroOutputSteps = 0;
        while (!inflater.IsFinished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(output.Length, Math.Min(entry.Length, limits.MaxModuleBytes) - total + 1);
            var inputBefore = inflater.TotalIn;
            int read;
            int granted;
            using (var reservation = await budget.ReserveUpToAsync(want, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                granted = reservation.GrantedBytes;
                if (granted == 0 && total < entry.Length) throw BudgetError();
                var outputBefore = inflater.TotalOut;
                try { read = inflater.Inflate(output, 0, granted); }
                finally { reservation.Complete(checked((int)(inflater.TotalOut - outputBefore))); }
            }
            total += read;
            if (total > limits.MaxModuleBytes)
                throw Error(ScriptArchiveIssueCode.EntrySizeExceedsLimit, "Actual output exceeds module byte limit.");
            if (total > entry.Length)
                throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Actual output exceeds declared length.");
            if (read > 0)
            {
                crc.Update(new ArraySegment<byte>(output, 0, read));
                append(output, read);
                zeroOutputSteps = 0;
                continue;
            }
            if (inflater.IsFinished) break;
            if (inflater.IsNeedingDictionary)
                throw Error(ScriptArchiveIssueCode.EntryCorrupt, "DEFLATE dictionary is unsupported.");
            if (inflater.IsNeedingInput)
            {
                if (remaining == 0)
                {
                    // With a zero-length output buffer the decoder advances one state at a time.
                    // IsNeedingInput can already be true while its buffered bits/end state still
                    // permit completion; allow these transitions before treating physical EOF as corruption.
                    if (granted == 0)
                    {
                        zeroOutputSteps = inflater.TotalIn != inputBefore ? 0 : zeroOutputSteps + 1;
                        if (zeroOutputSteps < 4) continue;
                    }
                    throw Error(ScriptArchiveIssueCode.EntryCorrupt, "DEFLATE stream has no complete end marker.");
                }
                var count = await stream.ReadAsync(input.AsMemory(0, (int)Math.Min(input.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                remaining -= count;
                inflater.SetInput(input, 0, count);
                zeroOutputSteps = 0;
                continue;
            }
            if (granted == 0)
            {
                // Permit end-block processing with no output when the budget is exactly exhausted.
                // Never inflate an unreserved probe byte to distinguish EOF from more content.
                zeroOutputSteps = inflater.TotalIn != inputBefore ? 0 : zeroOutputSteps + 1;
                if (zeroOutputSteps < 4) continue;
                throw BudgetError();
            }
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "DEFLATE decoder made no progress.");
        }
        if (remaining != 0 || inflater.RemainingInput != 0)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Trailing bytes after DEFLATE end marker.");
        return total;
    }

    private static async Task<long> ValidateLocalHeaderAsync(
        Stream stream, ScriptZipEntry entry, CancellationToken cancellationToken)
    {
        if (entry.LocalHeaderOffset > entry.DataBoundary - 30)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local header crosses a ZIP record boundary.");
        stream.Position = entry.LocalHeaderOffset;
        var header = new byte[30];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var fields = ParseLocalHeader(header, entry);
        if ((long)fields.NameLength + fields.ExtraLength > entry.DataBoundary - stream.Position)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local header fields cross a ZIP record boundary.");
        var name = new byte[fields.NameLength];
        await stream.ReadExactlyAsync(name, cancellationToken).ConfigureAwait(false);
        if (!name.AsSpan().SequenceEqual(entry.NameBytes))
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local and central entry names differ.");
        var extra = new byte[fields.ExtraLength];
        await stream.ReadExactlyAsync(extra, cancellationToken).ConfigureAwait(false);
        ValidateExtra(extra, ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueCode.EntryCorrupt);
        return stream.Position;
    }

    private static async Task ValidateDescriptorAsync(
        Stream stream, ScriptZipEntry entry, long offset, CancellationToken cancellationToken)
    {
        if (entry.DataBoundary - offset < 12)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Truncated ZIP data descriptor.");
        stream.Position = offset;
        var descriptor = new byte[16];
        await stream.ReadExactlyAsync(descriptor.AsMemory(0, 12), cancellationToken).ConfigureAwait(false);
        // Signatureless descriptors are legal, including a CRC equal to the optional signature.
        if (MatchesDescriptor(descriptor, 0, entry)) return;
        if (U32(descriptor, 0) != DescriptorSignature || entry.DataBoundary - offset < 16)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "ZIP data descriptor differs from directory.");
        await stream.ReadExactlyAsync(descriptor.AsMemory(12, 4), cancellationToken).ConfigureAwait(false);
        if (!MatchesDescriptor(descriptor, 4, entry))
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "ZIP data descriptor differs from directory.");
    }

    private static bool MatchesDescriptor(byte[] bytes, int offset, ScriptZipEntry entry) =>
        U32(bytes, offset) == entry.Crc32 && U32(bytes, offset + 4) == entry.CompressedLength
        && U32(bytes, offset + 8) == entry.Length;

    private static EndRecord ParseEnd(byte[] tail, long length, ScriptModuleLimits limits)
    {
        var offset = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (U32(tail, index) == EndSignature && index + 22 + U16(tail, index + 20) == tail.Length)
            { offset = index; break; }
        }
        if (offset < 0) throw Error(ScriptArchiveIssueCode.NotZip, "ZIP end record not found.");
        var entries = U16(tail, offset + 10);
        var size = U32(tail, offset + 12);
        var start = U32(tail, offset + 16);
        if ((offset >= 20 && U32(tail, offset - 20) == 0x07064b50)
            || entries == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
            throw Error(ScriptArchiveIssueCode.Zip64Unsupported, "ZIP64 is unsupported.");
        if (U16(tail, offset + 4) != 0 || U16(tail, offset + 6) != 0 || U16(tail, offset + 8) != entries)
            throw Error(ScriptArchiveIssueCode.Corrupt, "Multi-disk archive or inconsistent directory count.");
        if (entries > limits.MaxEntriesPerArchive)
            throw Error(ScriptArchiveIssueCode.EntryCountExceedsLimit, "Declared directory count exceeds entry limit.");
        var endPosition = length - tail.Length + offset;
        if ((long)start + size != endPosition)
            throw Error(ScriptArchiveIssueCode.Corrupt, "Central directory boundary differs from end record.");
        return new EndRecord(start, endPosition, entries);
    }

    private static DirectoryFields ParseDirectoryHeader(byte[] header)
    {
        if (U32(header, 0) != DirectorySignature)
            throw Error(ScriptArchiveIssueCode.Corrupt, "Invalid central directory signature.");
        if (U32(header, 20) == uint.MaxValue || U32(header, 24) == uint.MaxValue
            || U32(header, 42) == uint.MaxValue || U16(header, 34) == ushort.MaxValue)
            throw Error(ScriptArchiveIssueCode.Zip64Unsupported, "ZIP64 entry is unsupported.");
        if (U16(header, 34) != 0)
            throw Error(ScriptArchiveIssueCode.Corrupt, "Multi-disk entry is unsupported.");
        return new DirectoryFields(U16(header, 28), U16(header, 30), U16(header, 32), U16(header, 10),
            U16(header, 8), U32(header, 20), U32(header, 24), U32(header, 16), U32(header, 42), U32(header, 12));
    }

    private static LocalFields ParseLocalHeader(byte[] header, ScriptZipEntry entry)
    {
        if (U32(header, 0) != LocalSignature || U16(header, 6) != entry.Flags
            || U16(header, 8) != entry.CompressionMethod || U32(header, 10) != entry.DosTimestamp)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local header differs from central directory.");
        if (U32(header, 18) == uint.MaxValue || U32(header, 22) == uint.MaxValue)
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "ZIP64 local entry is unsupported.");
        if ((entry.Flags & 8) == 0)
        {
            if (U32(header, 14) != entry.Crc32 || U32(header, 18) != entry.CompressedLength || U32(header, 22) != entry.Length)
                throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local CRC or sizes differ from central directory.");
        }
        else if ((U32(header, 14) != 0 && U32(header, 14) != entry.Crc32)
            || (U32(header, 18) != 0 && U32(header, 18) != entry.CompressedLength)
            || (U32(header, 22) != 0 && U32(header, 22) != entry.Length))
            throw Error(ScriptArchiveIssueCode.EntryCorrupt, "Local descriptor placeholders differ from central directory.");
        return new LocalFields(U16(header, 26), U16(header, 28));
    }

    private static void ValidateExtra(byte[] extra, string corruptCode, string zip64Code)
    {
        var offset = 0;
        while (offset < extra.Length)
        {
            if (extra.Length - offset < 4) throw Error(corruptCode, "Truncated ZIP extra field.");
            var id = U16(extra, offset);
            var size = U16(extra, offset + 2);
            if (id == 1) throw Error(zip64Code, "ZIP64 extra field is unsupported.");
            offset += 4;
            if (size > extra.Length - offset) throw Error(corruptCode, "ZIP extra field crosses boundary.");
            offset += size;
        }
    }

    private static string DecodeName(byte[] bytes, ushort flags)
    {
        try { return ((flags & 0x0800) != 0 ? Utf8 : LegacyNameEncoding).GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw Error(ScriptArchiveIssueCode.Corrupt, "Entry name is not valid UTF-8.", exception);
        }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ScriptZipException BudgetError() => Error(ScriptArchiveIssueCode.BudgetExhausted, "Output budget exhausted.");
    private static ScriptZipException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
    private sealed record EndRecord(long DirectoryOffset, long DirectoryEnd, int EntryCount);
    private sealed record DirectoryFields(ushort NameLength, ushort ExtraLength, ushort CommentLength,
        ushort Method, ushort Flags, uint CompressedLength, uint Length, uint Crc, uint Offset, uint Timestamp);
    private sealed record LocalFields(ushort NameLength, ushort ExtraLength);
}

internal sealed record ScriptZipEntry(
    string Name, ushort CompressionMethod, ushort Flags, long CompressedLength, long Length,
    uint Crc32, long LocalHeaderOffset, uint DosTimestamp, byte[] NameBytes)
{
    internal long DataBoundary { get; set; }
}

internal sealed class ScriptZipException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}
