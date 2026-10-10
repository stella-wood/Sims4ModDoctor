using Sims4ModDoctor.Core.Duplicates;
using Sims4ModDoctor.Core.Packages;

namespace Sims4ModDoctor.Core.Conflicts;

/// <summary>
/// 要读取的一条资源：索引里的 TGI 与它在索引中的位置。
/// 两者都要对上才算同一条资源，不能只凭 TGI 去找。
/// </summary>
public readonly record struct ResourceContentTarget(ResourceKey Key, int Ordinal);

/// <summary>
/// 对同一个 package 的一批资源计算内容哈希。
/// </summary>
/// <param name="ExpectedStamp">候选扫描读取索引时的文件戳。</param>
public sealed record ResourceContentBatchRequest(
    string PackagePath,
    FileStamp ExpectedStamp,
    IReadOnlyList<ResourceContentTarget> Targets,
    ResourceContentLimits Limits);

/// <summary>
/// 一批资源的结果。<see cref="Results"/> 与请求里的 <c>Targets</c> 一一对应、顺序相同。
/// </summary>
/// <param name="StampAfterRead">读取结束后观察到的文件戳；取不到时为 <see langword="null"/>。</param>
public sealed record ResourceContentBatchResult(
    string PackagePath,
    FileStamp? StampAfterRead,
    IReadOnlyList<ResourceContentResult> Results);

/// <summary>
/// 本项目自己的资源内容读取入口。实现位于独立项目，Core 不引用任何第三方 package 库。
/// </summary>
/// <remarks>
/// 契约：
/// <list type="bullet">
/// <item>只读取请求里的资源，不解压整个 package；不写入、不修复、不保存 package。</item>
/// <item>哈希对象是解压后的完整内容；不同压缩方式存储的同一份内容必须得到同一个哈希。</item>
/// <item>读取量从 <c>budget</c> 里扣除；预算不足时那条资源失败，不越过预算继续读。</item>
/// <item>可预期的失败按资源返回结构化问题，不抛异常；不返回部分内容的哈希。</item>
/// <item>文件在扫描之后或读取期间被改动时，这一批的结果全部作废。</item>
/// <item><see cref="OperationCanceledException"/> 与 <see cref="OutOfMemoryException"/> 必须原样向上传播。</item>
/// </list>
/// </remarks>
public interface IResourceContentHasher
{
    Task<ResourceContentBatchResult> HashAsync(
        ResourceContentBatchRequest request,
        ResourceContentBudget budget,
        CancellationToken cancellationToken = default);
}
