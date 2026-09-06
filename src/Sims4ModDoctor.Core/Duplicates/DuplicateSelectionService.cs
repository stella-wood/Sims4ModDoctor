namespace Sims4ModDoctor.Core.Duplicates;

public sealed class DuplicateSelectionService
{
    public (string KeepPath, IReadOnlyList<string> DeletePaths) Select(DuplicateFile[] files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Length < 2)
        {
            throw new ArgumentException("A duplicate group must contain at least two files.", nameof(files));
        }

        var ordered = files
            .OrderByDescending(file => file.IsInsideMods)
            .ThenByDescending(file => file.DirectoryDepth)
            .ThenBy(file => file.Path, PathRules.Comparer)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        return (
            ordered[0].Path,
            ordered.Skip(1).Select(file => file.Path).ToArray());
    }
}
