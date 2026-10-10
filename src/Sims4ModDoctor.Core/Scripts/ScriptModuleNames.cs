namespace Sims4ModDoctor.Core.Scripts;

/// <summary>
/// 把 zip 条目名换算成 Python 能 import 的模块名，规则按 zipimport 的查找方式。
/// </summary>
/// <remarks>
/// <para>游戏把每个 .ts4script 当作 zip 加进 import 路径，由 zipimport 加载。
/// 对模块 <c>a.b</c>，zipimport 在归档里依次找
/// <c>a/b/__init__.pyc</c>、<c>a/b/__init__.py</c>、<c>a/b.pyc</c>、<c>a/b.py</c>，用第一个找到的。
/// 所以同一个归档里一个模块名只对应一个条目，优先级见 <see cref="ScriptModuleEntry.Priority"/>。</para>
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
