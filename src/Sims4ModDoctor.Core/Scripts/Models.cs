using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Scripts;

public enum ScriptModuleKind
{
    /// <summary>普通模块：<c>a/b.pyc</c> 或 <c>a/b.py</c>。</summary>
    Module,

    /// <summary>包：<c>a/b/__init__.pyc</c> 或 <c>a/b/__init__.py</c>，模块名取 <c>a.b</c>。</summary>
    Package,
}

public enum ScriptModuleFormat
{
    /// <summary>编译后的字节码 .pyc。</summary>
    Compiled,

    /// <summary>Python 源码 .py。</summary>
    Source,
}

/// <summary>
/// 脚本模块碰撞扫描请求。
/// </summary>
public sealed record ScriptModuleScanRequest(
    IReadOnlyList<ScanSource> Sources,
    ScriptModuleLimits? Limits = null);

public enum ScriptModuleScanPhase
{
    Starting,
    Discovering,
    ReadingArchives,
    Grouping,
    Completed,
}

public sealed record ScriptModuleScanProgress(
    ScriptModuleScanPhase Phase,
    string Message,
    int CompletedItems = 0,
    int? TotalItems = null,
    int DiscoveredArchiveCount = 0);

public enum ScriptArchiveIssueStage
{
    SourceValidation,
    Discovery,
    Metadata,

    /// <summary>打不开文件、取不到文件戳。</summary>
    Access,

    /// <summary>文件在读取期间被改动。</summary>
    Stability,

    /// <summary>不是可读的 zip：中央目录找不到、条目数超限、Zip64 等。整个归档未分析。</summary>
    Structure,

    /// <summary>单个模块条目读不出来：损坏、加密、超出单条上限。归档其他部分照常分析。</summary>
    Entry,

    /// <summary>单次扫描的共享预算用完。</summary>
    Budget,

    /// <summary>出现了契约之外的异常。</summary>
    Unexpected,
}

/// <summary>
/// 结构化的扫描问题。<paramref name="Code"/> 是稳定错误码，<paramref name="Message"/> 是给玩家看的摘要。
/// </summary>
/// <param name="EntryName">问题出在某个 zip 条目上时，给出原始条目名。</param>
public sealed record ScriptArchiveIssue(
    string Code,
    ScriptArchiveIssueStage Stage,
    string Path,
    string Message,
    string? SourceId = null,
    string? EntryName = null,
    string? Detail = null);

public static class ScriptArchiveIssueCode
{
    public const string AccessFailed = "script-archive-access-failed";
    public const string ChangedDuringRead = "script-archive-changed-during-read";

    public const string TooLarge = "script-archive-too-large";
    public const string NotZip = "script-archive-not-zip";
    public const string Zip64Unsupported = "script-archive-zip64-unsupported";
    public const string EntryCountExceedsLimit = "script-archive-entry-count-exceeds-limit";
    public const string Corrupt = "script-archive-corrupt";

    public const string EntryCorrupt = "script-entry-corrupt";
    public const string EntrySizeExceedsLimit = "script-entry-size-exceeds-limit";

    public const string BudgetExhausted = "script-scan-budget-exhausted";

    public const string UnexpectedError = "script-archive-unexpected-error";
}

/// <summary>
/// 某个模块名在某个归档里的一次出现。
/// </summary>
/// <param name="EntryName">zipimport 实际会加载的那个条目（同名时按查找顺序选中的）。</param>
/// <param name="Sha256">
/// 模块内容的 SHA-256，大写十六进制。.pyc 跳过开头 16 字节的头部再算，
/// 见 <see cref="ScriptModuleScanner"/> 的说明。读取失败时为 <see langword="null"/>。
/// </param>
/// <param name="ContentLength">实际解压输出的字节数（不扣除跳过的头部），不是 zip 里的声明值。</param>
public sealed record ScriptModuleOccurrence(
    string ModuleName,
    string ArchivePath,
    FileStamp ArchiveStamp,
    IReadOnlyList<string> SourceIds,
    string EntryName,
    ScriptModuleKind Kind,
    ScriptModuleFormat Format,
    string? Sha256,
    long? ContentLength,
    ScriptArchiveIssue? Issue)
{
    public bool IsSuccess => Sha256 is not null;
}

/// <summary>内容相同的一组出现。</summary>
public sealed record ScriptModuleContentGroup(
    string Sha256,
    IReadOnlyList<ScriptModuleOccurrence> Occurrences);

public enum ScriptModuleVerdict
{
    /// <summary>全部读取成功，只有一种内容：多半是同一个库被两个 mod 各带了一份。</summary>
    Identical,

    /// <summary>已经确认至少两种不同内容。可能同时有读取失败的出现。</summary>
    Different,

    /// <summary>有出现没读成功，现有结果既不能确认差异，也不能说相同。</summary>
    Incomplete,
}

/// <summary>
/// 模块碰撞：同一个模块名出现在至少两个不同的 .ts4script 里。
/// </summary>
/// <remarks>
/// Python 对一个模块名只加载一次，先被找到的那个生效，其余的不会被加载。
/// 内容相同时通常无害；内容不同时，某个 mod 实际用上的是别人带的版本，可能出错。
/// 本结果不判断游戏会先找到哪一个，也不给出删除建议。
/// </remarks>
public sealed record ScriptModuleCollision(
    string ModuleName,
    IReadOnlyList<ScriptModuleContentGroup> ContentGroups,
    IReadOnlyList<ScriptModuleOccurrence> Uncompared)
{
    /// <summary>顶层包名，便于把同一个库的一串子模块碰撞归在一起显示。</summary>
    public string TopLevelName
    {
        get
        {
            var dot = ModuleName.IndexOf('.');
            return dot < 0 ? ModuleName : ModuleName[..dot];
        }
    }

    public IEnumerable<ScriptModuleOccurrence> Occurrences =>
        ContentGroups.SelectMany(group => group.Occurrences).Concat(Uncompared);

    public int ArchiveCount => Occurrences
        .Select(occurrence => occurrence.ArchivePath)
        .Distinct(PathRules.Comparer)
        .Count();

    public bool IsComplete => Uncompared.Count == 0;

    public bool HasConfirmedDifference => ContentGroups.Count >= 2;

    public ScriptModuleVerdict Verdict =>
        HasConfirmedDifference ? ScriptModuleVerdict.Different
        : IsComplete && ContentGroups.Count == 1 ? ScriptModuleVerdict.Identical
        : ScriptModuleVerdict.Incomplete;
}

/// <summary>
/// 脚本模块碰撞扫描结果。
/// </summary>
/// <param name="AnalyzedArchiveCount">成功列出模块、参与碰撞分组的归档数。</param>
/// <param name="IncompleteArchivePaths">
/// 整个归档没能分析的 .ts4script。它们里面的模块不在结果里，
/// 因此「没有碰撞」不代表这些文件没有碰撞。
/// </param>
/// <param name="ModuleCount">已分析归档里不同模块名的个数。</param>
/// <param name="ProcessedBytes">实际解压输出的字节总数（共享预算里扣掉的量）。</param>
public sealed record ScriptModuleScanReport(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<ScanSourceSnapshot> Sources,
    ScriptModuleLimits Limits,
    int DiscoveredArchiveCount,
    int AnalyzedArchiveCount,
    IReadOnlyList<string> IncompleteArchivePaths,
    int ModuleCount,
    IReadOnlyList<ScriptModuleCollision> Collisions,
    IReadOnlyList<ScriptArchiveIssue> Issues,
    long ProcessedBytes)
{
    public int IncompleteArchiveCount => IncompleteArchivePaths.Count;
}
