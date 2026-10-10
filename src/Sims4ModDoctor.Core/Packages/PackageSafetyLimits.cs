namespace Sims4ModDoctor.Core.Packages;

/// <summary>
/// 交给第三方库之前必须满足的安全上限。
/// 这些值防止损坏或恶意构造的 package 触发巨大分配。
/// </summary>
public sealed record PackageSafetyLimits(
    int MaxResourceCount = PackageSafetyLimits.DefaultMaxResourceCount,
    long MaxFileSizeBytes = PackageSafetyLimits.DefaultMaxFileSizeBytes)
{
    /// <summary>
    /// 目前见过的大型合并 package 在数万条量级；
    /// 取 100 万作为「几乎必定是损坏或恶意」的分界，而不是性能调优值。
    /// </summary>
    public const int DefaultMaxResourceCount = 1_000_000;

    /// <summary>
    /// 8 GiB。超出这个尺寸的单个 package 不属于本工具的支持范围。
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 8L * 1024 * 1024 * 1024;

    public static PackageSafetyLimits Default { get; } = new();
}
