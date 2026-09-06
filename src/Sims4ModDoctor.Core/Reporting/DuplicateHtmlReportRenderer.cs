using System.Globalization;
using System.Net;
using System.Text;
using Sims4ModDoctor.Core.Duplicates;

namespace Sims4ModDoctor.Core.Reporting;

public static class DuplicateHtmlReportRenderer
{
    public static string Render(DuplicateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>Sims 4 Mod Doctor｜重复文件报告</title>");
        html.AppendLine("<style>body{font-family:system-ui,'Microsoft YaHei',sans-serif;max-width:1100px;margin:40px auto;padding:0 20px;color:#202124;background:#faf9f6}h1,h2{color:#173f35}.summary{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px}.card,section{background:white;border:1px solid #dfe5e2;border-radius:12px;padding:16px;margin:16px 0}.value{font-size:1.55rem;font-weight:700}.path{font-family:Consolas,monospace;overflow-wrap:anywhere}.keep{color:#176b4d}.delete{color:#9a3d32}.issue{color:#7a4b00}small{color:#66736f}ul{padding-left:22px}</style></head><body>");
        html.AppendLine("<h1>Sims 4 Mod Doctor｜重复文件报告</h1>");
        html.Append("<p><small>扫描时间：")
            .Append(Encode(report.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
            .Append(" ～ ")
            .Append(Encode(report.CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
            .AppendLine("</small></p>");
        html.AppendLine("<div class=\"summary\">");
        AppendCard(html, "发现文件", report.DiscoveredFileCount.ToString(CultureInfo.InvariantCulture));
        AppendCard(html, "重复组", report.Groups.Count.ToString(CultureInfo.InvariantCulture));
        AppendCard(html, "重复文件", report.DuplicateFileCount.ToString(CultureInfo.InvariantCulture));
        AppendCard(html, "预计可释放", FormatBytes(report.ReclaimableBytes));
        AppendCard(html, "未完成分析", report.Issues.Count.ToString(CultureInfo.InvariantCulture));
        html.AppendLine("</div>");

        foreach (var section in Enum.GetValues<DuplicateSection>())
        {
            var groups = report.Groups.Where(group => group.Section == section).ToArray();
            if (groups.Length == 0)
            {
                continue;
            }

            html.Append("<h2>").Append(SectionTitle(section)).AppendLine("</h2>");
            foreach (var group in groups)
            {
                html.AppendLine("<section>");
                html.Append("<strong>").Append(FormatBytes(group.FileSize)).Append(" × ")
                    .Append(group.Files.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 个文件</strong>");
                html.Append("<p><small>SHA-256：").Append(Encode(group.Sha256)).AppendLine("</small></p><ul>");

                foreach (var file in group.Files)
                {
                    var keep = string.Equals(file.Path, group.SuggestedKeepPath, StringComparison.OrdinalIgnoreCase);
                    html.Append("<li class=\"").Append(keep ? "keep" : "delete").Append("\"><span class=\"path\">")
                        .Append(Encode(file.Path)).Append("</span> — ")
                        .Append(keep ? "建议保留" : "可勾选删除")
                        .Append(" <small>[来源：")
                        .Append(Encode(string.Join(", ", file.SourceIds)))
                        .AppendLine("]</small></li>");
                }

                html.AppendLine("</ul></section>");
            }
        }

        if (report.Groups.Count == 0)
        {
            html.AppendLine("<section><strong>没有发现内容完全相同的文件。</strong></section>");
        }

        if (report.Issues.Count > 0)
        {
            html.AppendLine("<h2>未完成分析</h2><section><ul>");
            foreach (var issue in report.Issues)
            {
                html.Append("<li class=\"issue\"><span class=\"path\">")
                    .Append(Encode(issue.Path)).Append("</span> — ")
                    .Append(Encode(issue.Code)).Append(": ")
                    .Append(Encode(issue.Message)).AppendLine("</li>");
            }

            html.AppendLine("</ul></section>");
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void AppendCard(StringBuilder html, string label, string value)
    {
        html.Append("<div class=\"card\"><div class=\"value\">")
            .Append(Encode(value)).Append("</div><div>")
            .Append(Encode(label)).AppendLine("</div></div>");
    }

    private static string SectionTitle(DuplicateSection section) => section switch
    {
        DuplicateSection.FocusCrossScope => "重点结果：重点范围 ↔ 参照范围",
        DuplicateSection.FocusInternal => "次级结果：重点范围内部重复",
        DuplicateSection.ReferenceInternal => "次级结果：参照范围内部重复",
        DuplicateSection.Ordinary => "重复文件",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
