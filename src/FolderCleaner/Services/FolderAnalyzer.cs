using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FolderCleaner.Models;

namespace FolderCleaner.Services;

public sealed partial class FolderAnalyzer
{
    private const long LargeFileThresholdBytes = 50L * 1024 * 1024; // 50 МБ
    private const long LogSizeThresholdBytes = 100L * 1024;         // 100 КБ
    private const long InstallerExeMinBytes = 1024L * 1024;         // 1 МБ
    private const long MaxDuplicateHashBytes = 512L * 1024 * 1024;  // 512 МБ cap for fast hashing

    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".bak", ".old", ".dmp", ".chk", ".crdownload", ".part", ".download", ".~tmp"
    };

    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", ".DS_Store", "ehthumbs.db", "ehthumbs_vista.db"
    };

    private static readonly HashSet<string> CacheDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", ".cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "Crashpad"
    };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msix", ".msixbundle", ".msp", ".dmg", ".iso"
    };

    [GeneratedRegex(@"(setup|install|installer|uninst|redist|vcredist|update|updater|patch|x64|x86|win64|win32)", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerNameRegex();

    public Task<ScanResult> AnalyzeAsync(
        string rootFolder,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => AnalyzeCore(rootFolder, progress, cancellationToken), cancellationToken);
    }

    private ScanResult AnalyzeCore(
        string rootFolder,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var rootFull = Path.GetFullPath(rootFolder);
        var recommendations = new List<CleanupItem>();
        var candidateFilesForDuplicates = new List<FileMeta>();
        var flaggedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int totalFiles = 0;
        int totalDirs = 0;
        long totalSize = 0;
        int skippedCount = 0;
        var lastProgressTick = Environment.TickCount64;

        void ReportProgress(string status, bool force = false)
        {
            long now = Environment.TickCount64;
            if (force || now - lastProgressTick >= 80)
            {
                lastProgressTick = now;
                progress?.Report(new ScanProgress(status, totalFiles));
            }
        }

        ReportProgress("Сканирование файлов и папок...", force: true);

        // Traverse directory tree; return true if directory is completely empty (no files, no non-empty subdirs)
        bool ScanDirectory(string dirPath, bool isRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(dirPath);
                if (!dirInfo.Exists) return false;
                if (!isRoot && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            catch
            {
                skippedCount++;
                return false;
            }

            if (!isRoot)
            {
                totalDirs++;

                // Check if this is a known cache directory
                if (CacheDirNames.Contains(dirInfo.Name))
                {
                    var (cacheSize, cacheFiles, cacheSkipped) = MeasureDirectory(dirPath, cancellationToken);
                    totalFiles += cacheFiles;
                    totalSize += cacheSize;
                    skippedCount += cacheSkipped;

                    recommendations.Add(new CleanupItem
                    {
                        FullPath = dirPath,
                        Name = dirInfo.Name,
                        RelativePath = GetRelativeDisplay(rootFull, dirPath),
                        IsDirectory = true,
                        SizeBytes = cacheSize,
                        LastModified = dirInfo.LastWriteTime,
                        Category = CleanupCategory.TempAndCache,
                        Recommendation = $"Папка кэша ({cacheFiles} файлов) — можно безопасно удалить",
                        IsSelected = true
                    });
                    flaggedPaths.Add(dirPath);
                    return false;
                }
            }

            bool hasAnyContent = false;
            var emptySubdirs = new List<(string Path, DirectoryInfo Info)>();

            // Enumerate subdirectories
            try
            {
                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool subIsEmpty = ScanDirectory(subDir.FullName, isRoot: false);
                    if (subIsEmpty)
                    {
                        emptySubdirs.Add((subDir.FullName, subDir));
                    }
                    else
                    {
                        hasAnyContent = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skippedCount++;
                hasAnyContent = true;
            }

            // Enumerate files
            try
            {
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasAnyContent = true;
                    totalFiles++;

                    long fileSize;
                    DateTime modified;
                    try
                    {
                        fileSize = file.Length;
                        modified = file.LastWriteTime;
                    }
                    catch
                    {
                        skippedCount++;
                        continue;
                    }

                    totalSize += fileSize;
                    ReportProgress($"Проверено файлов: {totalFiles}...");

                    if (TryClassifyFile(rootFull, file, fileSize, modified, out var item))
                    {
                        recommendations.Add(item!);
                        flaggedPaths.Add(file.FullName);
                    }
                    else if (fileSize > 0 && fileSize <= MaxDuplicateHashBytes)
                    {
                        candidateFilesForDuplicates.Add(new FileMeta(file.FullName, file.Name, fileSize, modified));
                    }
                    else if (fileSize >= LargeFileThresholdBytes)
                    {
                        recommendations.Add(CreateLargeFileItem(rootFull, file.FullName, file.Name, fileSize, modified));
                        flaggedPaths.Add(file.FullName);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skippedCount++;
                hasAnyContent = true;
            }

            // If this directory itself has other content (or is root), flag its empty subdirectories
            if (hasAnyContent || isRoot)
            {
                foreach (var (emptyPath, emptyInfo) in emptySubdirs)
                {
                    recommendations.Add(new CleanupItem
                    {
                        FullPath = emptyPath,
                        Name = emptyInfo.Name,
                        RelativePath = GetRelativeDisplay(rootFull, emptyPath),
                        IsDirectory = true,
                        SizeBytes = 0,
                        LastModified = emptyInfo.LastWriteTime,
                        Category = CleanupCategory.EmptyItems,
                        Recommendation = "Пустая папка — не содержит файлов",
                        IsSelected = true
                    });
                    flaggedPaths.Add(emptyPath);
                }
                return false;
            }

            // Otherwise, this directory is completely empty (may contain only empty subdirs)
            return true;
        }

        ScanDirectory(rootFull, isRoot: true);

        // Stage 2: Detect exact duplicates among non-flagged files
        ReportProgress("Поиск дубликатов файлов...", force: true);
        var duplicateFlagged = FindDuplicates(rootFull, candidateFilesForDuplicates, recommendations, cancellationToken);

        // Stage 3: Flag remaining large files (>= 50 MB) that weren't duplicates
        foreach (var meta in candidateFilesForDuplicates)
        {
            if (meta.Size >= LargeFileThresholdBytes && !duplicateFlagged.Contains(meta.FullPath))
            {
                recommendations.Add(CreateLargeFileItem(rootFull, meta.FullPath, meta.Name, meta.Size, meta.LastModified));
            }
        }

        // Sort: safe items first by size desc, then review items by size desc
        recommendations.Sort((a, b) =>
        {
            int safeCmp = b.IsSafe.CompareTo(a.IsSafe);
            if (safeCmp != 0) return safeCmp;
            int sizeCmp = b.SizeBytes.CompareTo(a.SizeBytes);
            if (sizeCmp != 0) return sizeCmp;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        sw.Stop();
        ReportProgress("Анализ завершён", force: true);

        return new ScanResult
        {
            RootFolder = rootFull,
            TotalFilesScanned = totalFiles,
            TotalDirectoriesScanned = totalDirs,
            TotalFolderSizeBytes = totalSize,
            SkippedPathsCount = skippedCount,
            Duration = sw.Elapsed,
            Recommendations = recommendations
        };
    }

    private static bool TryClassifyFile(
        string rootFull,
        FileInfo file,
        long size,
        DateTime modified,
        out CleanupItem? item)
    {
        string ext = file.Extension;
        string name = file.Name;

        // 1. Temporary / junk files
        if (TempExtensions.Contains(ext) || JunkFileNames.Contains(name) || name.StartsWith("~$", StringComparison.Ordinal))
        {
            item = new CleanupItem
            {
                FullPath = file.FullName,
                Name = name,
                RelativePath = GetRelativeDisplay(rootFull, file.FullName),
                IsDirectory = false,
                SizeBytes = size,
                LastModified = modified,
                Category = CleanupCategory.TempAndCache,
                Recommendation = "Временный или служебный файл — можно безопасно удалить",
                IsSelected = true
            };
            return true;
        }

        // 2. Empty 0-byte files
        if (size == 0)
        {
            item = new CleanupItem
            {
                FullPath = file.FullName,
                Name = name,
                RelativePath = GetRelativeDisplay(rootFull, file.FullName),
                IsDirectory = false,
                SizeBytes = 0,
                LastModified = modified,
                Category = CleanupCategory.EmptyItems,
                Recommendation = "Пустой файл (0 Б) — не содержит данных",
                IsSelected = true
            };
            return true;
        }

        // 3. Log files
        if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
            (size >= LogSizeThresholdBytes || (DateTime.Now - modified).TotalDays >= 7))
        {
            item = new CleanupItem
            {
                FullPath = file.FullName,
                Name = name,
                RelativePath = GetRelativeDisplay(rootFull, file.FullName),
                IsDirectory = false,
                SizeBytes = size,
                LastModified = modified,
                Category = CleanupCategory.Logs,
                Recommendation = "Файл журнала (.log) — обычно не требуется для работы",
                IsSelected = true
            };
            return true;
        }

        // 4. Installers (.msi, .iso, or setup .exe)
        if (InstallerExtensions.Contains(ext) ||
            (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && size >= InstallerExeMinBytes && InstallerNameRegex().IsMatch(name)))
        {
            item = new CleanupItem
            {
                FullPath = file.FullName,
                Name = name,
                RelativePath = GetRelativeDisplay(rootFull, file.FullName),
                IsDirectory = false,
                SizeBytes = size,
                LastModified = modified,
                Category = CleanupCategory.Installers,
                Recommendation = "Установщик или образ диска — обычно не нужен после установки",
                IsSelected = true
            };
            return true;
        }

        item = null;
        return false;
    }

    private static CleanupItem CreateLargeFileItem(string rootFull, string fullPath, string name, long size, DateTime modified)
    {
        int ageDays = Math.Max(0, (int)(DateTime.Now - modified).TotalDays);
        string ageHint = ageDays >= 30
            ? $"не изменялся {ageDays / 30} мес."
            : "проверьте, нужен ли он ещё";

        return new CleanupItem
        {
            FullPath = fullPath,
            Name = name,
            RelativePath = GetRelativeDisplay(rootFull, fullPath),
            IsDirectory = false,
            SizeBytes = size,
            LastModified = modified,
            Category = CleanupCategory.LargeFiles,
            Recommendation = $"Крупный файл ({SizeFormatter.Format(size)}) — {ageHint}",
            IsSelected = false
        };
    }

    private static HashSet<string> FindDuplicates(
        string rootFull,
        List<FileMeta> candidates,
        List<CleanupItem> recommendations,
        CancellationToken ct)
    {
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Stage 1: Group by exact file size
        var bySize = candidates
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1);

        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();

            // Stage 2: Group by fast partial hash (first 4KB + last 4KB)
            var byPartial = new Dictionary<string, List<FileMeta>>();
            foreach (var file in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();
                string? partial = ComputePartialHash(file.FullPath, file.Size);
                if (partial is null) continue;
                if (!byPartial.TryGetValue(partial, out var list))
                {
                    list = [];
                    byPartial[partial] = list;
                }
                list.Add(file);
            }

            // Stage 3: Full SHA-256 only for matching partial hashes
            foreach (var partialList in byPartial.Values.Where(l => l.Count > 1))
            {
                var byFullHash = new Dictionary<string, List<FileMeta>>();
                foreach (var file in partialList)
                {
                    ct.ThrowIfCancellationRequested();
                    string? fullHash = ComputeFullHash(file.FullPath);
                    if (fullHash is null) continue;
                    if (!byFullHash.TryGetValue(fullHash, out var list))
                    {
                        list = [];
                        byFullHash[fullHash] = list;
                    }
                    list.Add(file);
                }

                foreach (var dupGroup in byFullHash.Values.Where(l => l.Count > 1))
                {
                    // Keep the best "original": shortest filename (avoids "file (1).pdf"), then oldest modified date
                    var ordered = dupGroup
                        .OrderBy(f => f.Name.Length)
                        .ThenBy(f => f.LastModified)
                        .ThenBy(f => f.FullPath.Length)
                        .ToList();

                    var original = ordered[0];
                    for (int i = 1; i < ordered.Count; i++)
                    {
                        var dup = ordered[i];
                        flagged.Add(dup.FullPath);
                        recommendations.Add(new CleanupItem
                        {
                            FullPath = dup.FullPath,
                            Name = dup.Name,
                            RelativePath = GetRelativeDisplay(rootFull, dup.FullPath),
                            IsDirectory = false,
                            SizeBytes = dup.Size,
                            LastModified = dup.LastModified,
                            Category = CleanupCategory.Duplicates,
                            Recommendation = $"Точный дубликат файла «{original.Name}»",
                            IsSelected = true
                        });
                    }
                }
            }
        }

        return flagged;
    }

    private static string? ComputePartialHash(string path, long size)
    {
        try
        {
            const int chunkSize = 4096;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();

            if (size <= chunkSize * 2)
            {
                return Convert.ToHexString(sha.ComputeHash(fs));
            }

            byte[] buffer = new byte[chunkSize * 2];
            int read1 = fs.Read(buffer, 0, chunkSize);
            fs.Seek(-chunkSize, SeekOrigin.End);
            int read2 = fs.Read(buffer, chunkSize, chunkSize);
            return Convert.ToHexString(sha.ComputeHash(buffer, 0, read1 + read2));
        }
        catch
        {
            return null;
        }
    }

    private static string? ComputeFullHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch
        {
            return null;
        }
    }

    private static (long Size, int Files, int Skipped) MeasureDirectory(string dirPath, CancellationToken ct)
    {
        long size = 0;
        int files = 0;
        int skipped = 0;
        var stack = new Stack<string>();
        stack.Push(dirPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();
            try
            {
                var info = new DirectoryInfo(current);
                foreach (var f in info.EnumerateFiles())
                {
                    try
                    {
                        size += f.Length;
                        files++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                foreach (var d in info.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0)
                        stack.Push(d.FullName);
                }
            }
            catch
            {
                skipped++;
            }
        }

        return (size, files, skipped);
    }

    private static string GetRelativeDisplay(string rootFull, string targetFull)
    {
        string rel = Path.GetRelativePath(rootFull, targetFull);
        string? dir = Path.GetDirectoryName(rel);
        return string.IsNullOrEmpty(dir) ? "В корне выбранной папки" : dir;
    }

    private readonly record struct FileMeta(string FullPath, string Name, long Size, DateTime LastModified);
}
