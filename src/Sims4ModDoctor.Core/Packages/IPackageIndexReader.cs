namespace Sims4ModDoctor.Core.Packages;

/// <summary>
/// 本项目自己的只读 package 入口。实现位于独立项目，
/// 使 Core 不引用也不暴露任何第三方 package 库的类型。
/// </summary>
/// <remarks>
/// 契约：
/// <list type="bullet">
/// <item>不写入、不合并、不修改 package。</item>
/// <item>可预期的失败以 <see cref="PackageReadResult.Failure"/> 返回，不抛异常。</item>
/// <item><see cref="OperationCanceledException"/> 必须原样向上传播，不得被转成失败结果。</item>
/// </list>
/// </remarks>
public interface IPackageIndexReader
{
    Task<PackageReadResult> ReadIndexAsync(
        string packagePath,
        CancellationToken cancellationToken = default);
}
