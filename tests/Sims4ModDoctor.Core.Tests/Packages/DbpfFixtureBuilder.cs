using System.Buffers.Binary;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Tests.Packages;

/// <summary>
/// 测试期间生成的 DBPF 字节。不使用、也不提交任何真实玩家 Mod。
/// </summary>
/// <remarks>
/// 这个构造器存在的唯一理由是 indexType 位域：第三方库自带的样本位域为 0，
/// 每条记录恰好 32 字节，因此测不出「按固定长度步进读索引」这一类错误。
/// 这里可以指定任意位域，从而覆盖记录长度随位域变化的情形。
/// </remarks>
internal sealed class DbpfFixtureBuilder
{
    private readonly List<Resource> _resources = [];

    private uint _major = 2;
    private uint _minor = 1;
    private uint _indexVersion = 3;
    private uint _indexType;
    private byte[] _magic = "DBPF"u8.ToArray();

    /// <summary>一条逻辑索引记录的 8 个 DWORD。</summary>
    internal readonly record struct Resource(
        uint Type,
        uint Group,
        uint InstanceHi,
        uint InstanceLo,
        uint Payload,
        uint Compressed);

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
    /// 设置 indexType 位域。置位的字段将只在索引头里出现一次，
    /// 不再逐条存储；<see cref="Build"/> 会据此改变每条记录的长度。
    /// </summary>
    public DbpfFixtureBuilder WithIndexType(uint indexType)
    {
        _indexType = indexType;
        return this;
    }

    /// <param name="compressed">
    /// 压缩标记。默认写 0（未压缩），因为这里的 payload 是构造出来的假数据，
    /// 标成 ZLIB 会让真正去解压的读取器失败。
    /// </param>
    public DbpfFixtureBuilder AddResource(
        uint type = 0x0904DF10,
        uint group = 0,
        uint instanceHi = 0,
        uint? instanceLo = null,
        uint payload = 0x11223344,
        uint compressed = 0)
    {
        _resources.Add(new Resource(
            type,
            group,
            instanceHi,
            instanceLo ?? (uint)(0xD1D50000 + _resources.Count),
            payload,
            compressed));
        return this;
    }

    public DbpfFixtureBuilder AddResources(int count)
    {
        for (var index = 0; index < count; index++)
        {
            AddResource();
        }

        return this;
    }

    public byte[] Build()
    {
        // 每条资源在数据区写 4 字节 payload，从 header 之后开始摆放。
        const int payloadLength = sizeof(uint);
        var dataLength = _resources.Count * payloadLength;
        var indexPosition = DbpfPrecheck.HeaderLength + dataLength;

        var constantFields = System.Numerics.BitOperations.PopCount(_indexType);
        var perEntryFields = DbpfPrecheck.IndexFieldsPerEntry - constantFields;
        var indexLength = DbpfPrecheck.IndexTypeFieldLength
            + (constantFields * sizeof(uint))
            + (_resources.Count * perEntryFields * sizeof(uint));

        // 没有资源时索引整体不存在：header 里的数量、大小与位置必须同时为零。
        var hasIndex = _resources.Count > 0;
        var buffer = new byte[hasIndex ? indexPosition + indexLength : DbpfPrecheck.HeaderLength];
        var span = buffer.AsSpan();

        _magic.CopyTo(span);
        WriteUInt32(span, 0x04, _major);
        WriteUInt32(span, 0x08, _minor);
        WriteUInt32(span, 0x24, (uint)_resources.Count);
        WriteUInt32(span, 0x2C, hasIndex ? (uint)indexLength : 0);
        WriteUInt32(span, 0x3C, _indexVersion);
        WriteUInt32(span, 0x40, hasIndex ? (uint)indexPosition : 0);

        if (!hasIndex)
        {
            return buffer;
        }

        for (var index = 0; index < _resources.Count; index++)
        {
            WriteUInt32(
                span,
                DbpfPrecheck.HeaderLength + (index * payloadLength),
                _resources[index].Payload);
        }

        var cursor = indexPosition;
        WriteUInt32(span, cursor, _indexType);
        cursor += sizeof(uint);

        // 索引头里的公共常量取第一条资源的值；没有资源时位域也不应有置位。
        var template = _resources.Count > 0 ? _resources[0] : default;
        foreach (var field in FieldsOf(template, payloadLength, ordinal: 0))
        {
            if ((_indexType & (1u << field.Bit)) != 0)
            {
                WriteUInt32(span, cursor, field.Value);
                cursor += sizeof(uint);
            }
        }

        for (var index = 0; index < _resources.Count; index++)
        {
            foreach (var field in FieldsOf(_resources[index], payloadLength, index))
            {
                if ((_indexType & (1u << field.Bit)) != 0)
                {
                    continue;
                }

                WriteUInt32(span, cursor, field.Value);
                cursor += sizeof(uint);
            }
        }

        return buffer;
    }

    /// <summary>
    /// 把 <paramref name="length"/> 之后的字节全部截掉，用来制造截断文件。
    /// </summary>
    public static byte[] Truncate(byte[] package, int length) => package[..length];

    private static IEnumerable<(int Bit, uint Value)> FieldsOf(
        Resource resource,
        int payloadLength,
        int ordinal)
    {
        yield return (0, resource.Type);
        yield return (1, resource.Group);
        yield return (2, resource.InstanceHi);
        yield return (3, resource.InstanceLo);
        yield return (4, (uint)(DbpfPrecheck.HeaderLength + (ordinal * payloadLength)));
        // Filesize 的最高位在真实 Sims 4 package 里恒为 1（社区文档称它 Unknown1）。
        // 95 个真实 mod 无一例外，且第三方库据此决定记录里是否还有压缩字段——
        // 不置位就会造出一个现实中不存在的形态，被按 7 个 DWORD 读，从第二条起全部错位。
        yield return (5, (uint)payloadLength | 0x8000_0000);
        yield return (6, (uint)payloadLength);
        yield return (7, resource.Compressed);
    }

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);
}
