using FolderCleaner.Models;

namespace FolderCleaner.Models;

public sealed class ScanResult
{
    public required string RootFolder { get; init; }
    public required int TotalFilesScanned { get; init; }
    public required int TotalDirectoriesScanned { get; init; }
    public required long TotalFolderSizeBytes { get; init; }
    public required int SkippedPathsCount { get; init; }
    public required TimeSpan Duration { get; init; }
    public required List<CleanupItem> Recommendations { get; init; }
}

public sealed record ScanProgress(string StatusText, int FilesScanned);
