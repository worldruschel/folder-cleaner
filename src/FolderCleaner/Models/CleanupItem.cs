using System.ComponentModel;
using System.Runtime.CompilerServices;
using FolderCleaner.Services;

namespace FolderCleaner.Models;

public sealed class CleanupItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDeleting;

    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsDirectory { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
    public required CleanupCategory Category { get; init; }
    public required string Recommendation { get; init; }

    public string SizeDisplay => IsDirectory && SizeBytes == 0 ? "Папка" : SizeFormatter.Format(SizeBytes);
    public string CategoryName => CleanupCategoryInfo.GetDisplayName(Category);
    public string BadgeBackground => CleanupCategoryInfo.GetBadgeBackground(Category);
    public string BadgeForeground => CleanupCategoryInfo.GetBadgeForeground(Category);
    public bool IsSafe => CleanupCategoryInfo.IsSafeByDefault(Category);
    public string SafetyTag => IsSafe ? "Рекомендуется" : "На проверку";
    public string ModifiedDisplay => LastModified.ToString("dd.MM.yyyy");
    public string ItemIcon => IsDirectory ? "📁" : "📄";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        set
        {
            if (_isDeleting != value)
            {
                _isDeleting = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
