namespace Sims4ModDoctor.Core.Scripts;

/// <summary>
/// 静态换算 zip 条目名与 Python 模块名，候选顺序按 Python 3.7 zipimport。
/// </summary>
/// <remarks>
/// <para>游戏把每个 .ts4script 当作 zip 加进 import 路径，由 zipimport 加载。
/// 对模块 <c>a.b</c>，zipimport 在归档里依次找
/// <c>a/b/__init__.pyc</c>、<c>a/b/__init__.py</c>、<c>a/b.pyc</c>、<c>a/b.py</c>。
/// 无效 magic、默认检查模式不接受的 flags 或过期时间戳会使其继续尝试下一个候选；
/// 本分析只检查头部与内容字节，不证明源码或 marshal 数据能被 Python 加载。</para>
/// <para>zipimport 不查 <c>__pycache__</c>，那里的 .pyc 不会被 import，这里也不算模块。
/// 查找按条目名精确匹配，区分大小写，所以 <c>.PYC</c> 不算。</para>
/// </remarks>
internal static class ScriptModuleNames
{
    private const string CompiledSuffix = ".pyc";
    private const string SourceSuffix = ".py";
    private const string PackageInit = "__init__";
    private const string PyCache = "__pycache__";

    /// <summary>
    /// 中央目录中的同路径条目最后一个生效（Python 的字典覆盖语义），再按模块查找顺序分组。
    /// 保留所有候选，读取头部后才决定是否继续查找源码。
    /// </summary>
    public static IReadOnlyList<ScriptModuleCandidates<T>> GetCandidates<T>(
        IEnumerable<T> entries,
        Func<T, string> getEntryName)
    {
        var byPath = new Dictionary<string, ScriptModuleCandidate<T>>(StringComparer.Ordinal);
        foreach (var value in entries)
        {
            var name = getEntryName(value);
            var parsed = TryParse(name);
            if (parsed is not null)
            {
                byPath[name.Replace('\\', '/')] = new ScriptModuleCandidate<T>(parsed, value);
            }
        }

        return byPath.Values.GroupBy(candidate => candidate.Entry.ModuleName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ScriptModuleCandidates<T>(group.Key,
                group.OrderBy(candidate => candidate.Entry.Priority).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// 解析一个 zip 条目名。不是可 import 的模块条目时返回 <see langword="null"/>。
    /// </summary>
    public static ScriptModuleEntry? TryParse(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);

        // 有的打包工具在 Windows 上写出反斜杠；zipimport 在 Windows 上同样认它，统一成正斜杠再拆。
        var normalized = entryName.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.EndsWith('/'))
        {
            return null;
        }

        ScriptModuleFormat format;
        string stem;
        if (normalized.EndsWith(CompiledSuffix, StringComparison.Ordinal))
        {
            format = ScriptModuleFormat.Compiled;
            stem = normalized[..^CompiledSuffix.Length];
        }
        else if (normalized.EndsWith(SourceSuffix, StringComparison.Ordinal))
        {
            format = ScriptModuleFormat.Source;
            stem = normalized[..^SourceSuffix.Length];
        }
        else
        {
            return null;
        }

        var segments = stem.Split('/');
        foreach (var segment in segments)
        {
            // 空段、「.」「..」都拼不出合法的 import 路径；__pycache__ 不在 zipimport 的查找范围里。
            if (segment.Length == 0 || segment is "." or ".." || segment == PyCache || segment.Contains('.'))
            {
                return null;
            }
        }

        var kind = ScriptModuleKind.Module;
        if (segments[^1] == PackageInit)
        {
            if (segments.Length == 1)
            {
                // 归档根上的 __init__ 不对应任何可 import 的名字。
                return null;
            }
            kind = ScriptModuleKind.Package;
            segments = segments[..^1];
        }

        return new ScriptModuleEntry(string.Join('.', segments), entryName, kind, format);
    }
}

internal sealed record ScriptModuleCandidate<T>(ScriptModuleEntry Entry, T Value);

internal sealed record ScriptModuleCandidates<T>(
    string ModuleName,
    IReadOnlyList<ScriptModuleCandidate<T>> Entries);

/// <summary>归档里一个可 import 的模块条目。</summary>
/// <param name="ModuleName">点分模块名，例如 <c>turbolib2.utils</c>。包取包名本身。</param>
/// <param name="EntryName">zip 里的原始条目名，用于再次定位与报告。</param>
internal sealed record ScriptModuleEntry(
    string ModuleName,
    string EntryName,
    ScriptModuleKind Kind,
    ScriptModuleFormat Format)
{
    /// <summary>zipimport 的查找顺序，数字小的先被找到。</summary>
    public int Priority => (Kind, Format) switch
    {
        (ScriptModuleKind.Package, ScriptModuleFormat.Compiled) => 0,
        (ScriptModuleKind.Package, ScriptModuleFormat.Source) => 1,
        (ScriptModuleKind.Module, ScriptModuleFormat.Compiled) => 2,
        _ => 3,
    };
}
