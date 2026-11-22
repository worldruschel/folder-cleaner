namespace FolderCleaner.Models;

public enum CleanupCategory
{
    TempAndCache,
    Duplicates,
    EmptyItems,
    Logs,
    Installers,
    LargeFiles
}

public static class CleanupCategoryInfo
{
    public static string GetDisplayName(CleanupCategory category) => category switch
    {
        CleanupCategory.TempAndCache => "Временные и кэш",
        CleanupCategory.Duplicates => "Дубликаты",
        CleanupCategory.EmptyItems => "Пустые",
        CleanupCategory.Logs => "Логи",
        CleanupCategory.Installers => "Установщики",
        CleanupCategory.LargeFiles => "Крупные файлы",
        _ => "Прочее"
    };

    public static string GetBadgeBackground(CleanupCategory category) => category switch
    {
        CleanupCategory.TempAndCache => "#FEF3C7",
        CleanupCategory.Duplicates => "#EDE9FE",
        CleanupCategory.EmptyItems => "#F1F5F9",
        CleanupCategory.Logs => "#E0F2FE",
        CleanupCategory.Installers => "#CCFBF1",
        CleanupCategory.LargeFiles => "#FEE2E2",
        _ => "#F1F5F9"
    };

    public static string GetBadgeForeground(CleanupCategory category) => category switch
    {
        CleanupCategory.TempAndCache => "#B45309",
        CleanupCategory.Duplicates => "#6D28D9",
        CleanupCategory.EmptyItems => "#475569",
        CleanupCategory.Logs => "#0369A1",
        CleanupCategory.Installers => "#0F766E",
        CleanupCategory.LargeFiles => "#B91C1C",
        _ => "#475569"
    };

    public static bool IsSafeByDefault(CleanupCategory category) => category switch
    {
        CleanupCategory.LargeFiles => false,
        _ => true
    };
}
