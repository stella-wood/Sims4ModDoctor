using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sims4ModDoctor.Core.Scripts;

internal enum ScriptPycDisposition
{
    Comparable,
    TryNextCandidate,
    Incomplete,
}

internal sealed record ScriptModuleContentResult(
    string? Sha256,
    ScriptPycDisposition Disposition,
    string? IssueCode = null,
    string? Detail = null);

/// <summary>
/// A bounded streaming comparison of source or Python 3.7 bytecode, without executing or unmarshalling code.
/// Header decisions follow CPython 3.7 zipimport with the default hash-based-pyc checking mode.
/// </summary>
internal sealed class ScriptModuleContentHasher : IDisposable
{
    // CPython 3.7 final magic 3394, followed by CR LF; no other bytecode versions are inferred from CR LF.
    private const uint Python37Magic = 0x0A0D0D42;
    private const int HeaderLength = 16;

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly ScriptModuleFormat _format;
    private readonly uint? _sourceDosTimestamp;
    private readonly byte[] _header = new byte[HeaderLength];
    private int _headerBytes;
    private long _length;

    public ScriptModuleContentHasher(ScriptModuleFormat format, uint? sourceDosTimestamp = null)
    {
        _format = format;
        _sourceDosTimestamp = sourceDosTimestamp;
        // Domain separation prevents a .py and a .pyc payload from comparing equal.
        _hash.AppendData(format == ScriptModuleFormat.Compiled ? "ts4script-pyc-v1\0"u8 : "ts4script-source-v1\0"u8);
    }

    public void Append(byte[] buffer, int count)
    {
        _length += count;
        var data = buffer.AsSpan(0, count);
        if (_format == ScriptModuleFormat.Compiled && _headerBytes < HeaderLength)
        {
            var take = Math.Min(HeaderLength - _headerBytes, data.Length);
            data[..take].CopyTo(_header.AsSpan(_headerBytes));
            _headerBytes += take;
            data = data[take..];
            if (_headerBytes == HeaderLength)
            {
                // Retain version; accepted timestamp/unchecked-hash headers describe the same payload format.
                // Flags are validated before exposing a digest, and their metadata does not affect bytecode identity.
                _hash.AppendData(_header.AsSpan(0, 4));
            }
        }
        _hash.AppendData(data);
    }

    public ScriptModuleContentResult Finish()
    {
        if (_format == ScriptModuleFormat.Compiled)
        {
            var decision = CheckHeader();
            if (decision is not null)
            {
                return decision;
            }
        }

        return new ScriptModuleContentResult(Convert.ToHexString(_hash.GetHashAndReset()), ScriptPycDisposition.Comparable);
    }

    private ScriptModuleContentResult? CheckHeader()
    {
        if (_headerBytes < HeaderLength)
        {
            // zipimport raises before checking magic; it does not fall back to source for a short header.
            return Failure(ScriptPycDisposition.Incomplete, ScriptArchiveIssueCode.EntryCorrupt, "pyc header shorter than 16 bytes");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if (magic != Python37Magic)
        {
            return Failure(ScriptPycDisposition.TryNextCandidate, ScriptArchiveIssueCode.PycUnsupported,
                $"unsupported pyc magic 0x{magic:X8}; expected Python 3.7 0x{Python37Magic:X8}");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
        // Python 3.7 zipimport defaults accept timestamp or unchecked-hash pycs only.
        // Checked-hash and unsupported flags continue to the next candidate; we never hash them as valid code.
        if (flags is not (0 or 1))
        {
            return Failure(ScriptPycDisposition.TryNextCandidate, ScriptArchiveIssueCode.PycUnsupported,
                $"pyc flags 0x{flags:X8} rejected by Python 3.7 default zipimport mode");
        }

        if (flags == 0 && _sourceDosTimestamp is uint dosTimestamp)
        {
            if (!TryGetSourceTimestamp(dosTimestamp, out var sourceTimestamp))
            {
                return Failure(ScriptPycDisposition.Incomplete, ScriptArchiveIssueCode.PycTimestampUnknown,
                    "source DOS timestamp is invalid or ambiguous in the local time zone");
            }

            var pycTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(8));
            // ZIP stores even seconds; CPython allows a one-second discrepancy and ignores source size.
            if (sourceTimestamp != 0 && Math.Abs((long)pycTimestamp - sourceTimestamp) > 1)
            {
                return Failure(ScriptPycDisposition.TryNextCandidate, ScriptArchiveIssueCode.PycStale,
                    $"pyc timestamp {pycTimestamp} does not match source timestamp {sourceTimestamp}");
            }
        }

        if (_length == HeaderLength)
        {
            return Failure(ScriptPycDisposition.Incomplete, ScriptArchiveIssueCode.EntryCorrupt, "pyc has no marshal payload");
        }

        return null;
    }

    private static bool TryGetSourceTimestamp(uint packed, out long timestamp)
    {
        timestamp = 0;
        var time = (ushort)packed;
        var date = (ushort)(packed >> 16);
        try
        {
            var local = new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31,
                time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Unspecified);
            var zone = TimeZoneInfo.Local;
            // CPython uses local mktime; guessing the UTC offset during DST transitions could choose the wrong candidate.
            if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
            {
                return false;
            }
            timestamp = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUnixTimeSeconds();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ScriptModuleContentResult Failure(ScriptPycDisposition disposition, string code, string detail) =>
        new(null, disposition, code, detail);

    public void Dispose() => _hash.Dispose();
}
