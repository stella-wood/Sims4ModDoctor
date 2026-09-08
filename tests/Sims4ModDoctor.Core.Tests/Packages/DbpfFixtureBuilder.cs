using System.Buffers.Binary;
using System.Numerics;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Packages;

/// <summary>
/// 测试期间生成的 DBPF 字节。不使用、也不提交任何真实玩家 Mod。
/// </summary>
/// <remarks>
/// 这个构造器要覆盖的是真实样本覆盖不到的形态：
/// <list type="bullet">
/// <item>indexType 位域非零时，记录里少几个字段；</item>
/// <item>扩展压缩字段逐条可选，因此每条记录的长度本身就是可变的；</item>
/// <item>索引位置写在 64 位字段还是老式 32 位字段；</item>
/// <item>同一个 TGI 出现多次。</item>
/// </list>
/// </remarks>
internal sealed class DbpfFixtureBuilder
{
    private readonly List<Resource> _resources = [];

    private uint _major = 2;
    private uint _minor = 1;
    private uint _indexVersion = 3;
    private uint _indexType;
    private bool _useLegacyIndexPosition;
    private byte[] _magic = "DBPF"u8.ToArray();

    internal readonly record struct Resource(
        uint Type,
        uint Group,
        uint InstanceHi,
        uint InstanceLo,
        bool Extended);

    public DbpfFixtureBuilder WithMagic(string magic)
    {
        _magic = System.Text.Encoding.ASCII.GetBytes(magic);
        return this;
    }

    public DbpfFixtureBuilder WithVersion(uint major, uint minor)
    {
        _major = major;
        _minor = minor;
        return this;
    }

    public DbpfFixtureBuilder WithIndexVersion(uint indexVersion)
    {
        _indexVersion = indexVersion;
        return this;
    }

    /// <summary>
    /// 设置 indexType 位域（只有 0x01 / 0x02 / 0x04 是有效位）。
    /// 置位的字段只在索引头里出现一次，不再逐条存储。
    /// </summary>
    public DbpfFixtureBuilder WithIndexType(uint indexType)
    {
        _indexType = indexType;
        return this;
    }

    /// <summary>把索引位置写进老式的 32 位字段，64 位字段留零。</summary>
    public DbpfFixtureBuilder WithLegacyIndexPosition()
    {
        _useLegacyIndexPosition = true;
        return this;
    }

    /// <param name="extended">
    /// 是否带 4 字节扩展压缩信息。逐条独立——真实 package 里两种都存在，
    /// 因此记录长度不是定值。
    /// </param>
    public DbpfFixtureBuilder AddResource(
        uint type = 0x0904DF10,
        uint group = 0,
        uint instanceHi = 0,
        uint? instanceLo = null,
        bool extended = true)
    {
        _resources.Add(new Resource(
            type,
            group,
            instanceHi,
            instanceLo ?? (uint)(0xD1D50000 + _resources.Count),
            extended));
        return this;
    }

    public DbpfFixtureBuilder AddResources(int count, bool extended = true)
    {
        for (var index = 0; index < count; index++)
        {
            AddResource(extended: extended);
        }

        return this;
    }

    public byte[] Build()
    {
        const int payloadLength = sizeof(uint);
        var dataLength = _resources.Count * payloadLength;
        var indexPosition = DbpfPrecheck.HeaderLength + dataLength;

        var constantFields = BitOperations.PopCount(_indexType & DbpfPrecheck.KnownIndexTypeMask);
        var perEntryFields = DbpfPrecheck.IndexFieldsPerEntry - constantFields;
        var indexLength = DbpfPrecheck.IndexTypeFieldLength
            + (constantFields * sizeof(uint))
            + (_resources.Count * perEntryFields * sizeof(uint))
            + (_resources.Count(resource => resource.Extended)
                * DbpfPrecheck.ExtendedCompressionFieldLength);

        // 没有资源时索引整体不存在：数量、大小与位置必须同时为零。
        var hasIndex = _resources.Count > 0;
        var buffer = new byte[hasIndex ? indexPosition + indexLength : DbpfPrecheck.HeaderLength];
        var span = buffer.AsSpan();

        _magic.CopyTo(span);
        WriteUInt32(span, 0x04, _major);
        WriteUInt32(span, 0x08, _minor);
        WriteUInt32(span, 0x24, (uint)_resources.Count);
        WriteUInt32(span, 0x2C, hasIndex ? (uint)indexLength : 0);
        WriteUInt32(span, 0x3C, _indexVersion);

        if (hasIndex)
        {
            if (_useLegacyIndexPosition)
            {
                WriteUInt32(span, 0x28, (uint)indexPosition);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(span[0x40..], (ulong)indexPosition);
            }
        }

        if (!hasIndex)
        {
            return buffer;
        }

        for (var index = 0; index < _resources.Count; index++)
        {
            WriteUInt32(span, DbpfPrecheck.HeaderLength + (index * payloadLength), 0x11223344);
        }

        var cursor = indexPosition;
        WriteUInt32(span, cursor, _indexType);
        cursor += sizeof(uint);

        // 索引头里的公共常量取第一条资源的值。
        var template = _resources[0];
        foreach (var (bit, value) in ConstantCandidates(template))
        {
            if ((_indexType & (1u << bit)) != 0)
            {
                WriteUInt32(span, cursor, value);
                cursor += sizeof(uint);
            }
        }

        for (var index = 0; index < _resources.Count; index++)
        {
            var resource = _resources[index];

            foreach (var (bit, value) in ConstantCandidates(resource))
            {
                if ((_indexType & (1u << bit)) == 0)
                {
                    WriteUInt32(span, cursor, value);
                    cursor += sizeof(uint);
                }
            }

            // Instance 低 32 位没有常量选项，永远逐条存储。
            WriteUInt32(span, cursor, resource.InstanceLo);
            cursor += sizeof(uint);

            WriteUInt32(span, cursor, (uint)(DbpfPrecheck.HeaderLength + (index * payloadLength)));
            cursor += sizeof(uint);

            // Size 的最高位就是扩展压缩标志。
            var size = (uint)payloadLength;
            WriteUInt32(span, cursor, resource.Extended ? size | 0x8000_0000 : size);
            cursor += sizeof(uint);

            WriteUInt32(span, cursor, (uint)payloadLength);
            cursor += sizeof(uint);

            if (resource.Extended)
            {
                // CompressionTypeMethodNumber(ushort) + mnCommitted(ushort)
                WriteUInt32(span, cursor, 0x0001_0000);
                cursor += DbpfPrecheck.ExtendedCompressionFieldLength;
            }
        }

        return buffer;
    }

    /// <summary>把 <paramref name="length"/> 之后的字节截掉，用来制造截断文件。</summary>
    public static byte[] Truncate(byte[] package, int length) => package[..length];

    private static IEnumerable<(int Bit, uint Value)> ConstantCandidates(Resource resource)
    {
        yield return (0, resource.Type);
        yield return (1, resource.Group);
        yield return (2, resource.InstanceHi);
    }

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);
}
