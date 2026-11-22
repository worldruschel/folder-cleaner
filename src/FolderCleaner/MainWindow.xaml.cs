using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderCleaner.Models;
using FolderCleaner.Services;
using Microsoft.Win32;

namespace FolderCleaner;

public partial class MainWindow : Window
{
    private readonly FolderAnalyzer _analyzer = new();
    private readonly UpdateService _updateService = new();
    private readonly List<CleanupItem> _allRecommendations = [];
    private readonly ObservableCollection<CleanupItem> _visibleItems = [];

    private CancellationTokenSource? _scanCts;
    private UpdateInfo? _pendingUpdate;
    private bool _isUpdating;
    private bool _isBusy;
    private bool _hasScanned;
    private CleanupCategory? _activeCategoryFilter;
    private long _sessionFreedBytes;
    private int _sessionFreedCount;

    public MainWindow()
    {
        InitializeComponent();
        RecommendationsList.ItemsSource = _visibleItems;
        CheckUpdatesButton.Content = $"{UpdateService.CurrentVersionDisplay}  🔄";

        // Pre-fill Downloads folder if it exists, so user can immediately click "Старт" or pick another folder
        string downloads = GetDownloadsFolder();
        if (Directory.Exists(downloads))
        {
            FolderPathBox.Text = downloads;
        }

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Silent background check for updates on startup
        await CheckUpdatesInternalAsync(userInitiated: false);
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        await CheckUpdatesInternalAsync(userInitiated: true);
    }

    private async Task CheckUpdatesInternalAsync(bool userInitiated)
    {
        CheckUpdatesButton.IsEnabled = false;
        if (userInitiated)
        {
            CheckUpdatesButton.Content = "Проверка...";
        }

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is not null)
            {
                _pendingUpdate = update;
                UpdateBannerTitle.Text = $"🎉 Доступна новая версия {update.TagName} (у вас {UpdateService.CurrentVersionDisplay})";
                string firstLine = update.ReleaseNotes
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "Нажмите «Обновить сейчас» — приложение скачает и установит обновление автоматически.";
                UpdateBannerSubtitle.Text = firstLine;
                UpdateBanner.Visibility = Visibility.Visible;
            }
            else if (userInitiated)
            {
                BottomStatusText.Text = $"У вас установлена последняя версия ({UpdateService.CurrentVersionDisplay}).";
                MessageBox.Show(this,
                    $"У вас уже установлена актуальная версия ({UpdateService.CurrentVersionDisplay}).",
                    "Проверка обновлений",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (userInitiated)
            {
                MessageBox.Show(this,
                    $"Не удалось проверить обновления:\n{ex.Message}",
                    "Проверка обновлений",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            CheckUpdatesButton.Content = $"{UpdateService.CurrentVersionDisplay}  🔄";
            CheckUpdatesButton.IsEnabled = !_isUpdating;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _isUpdating) return;

        _isUpdating = true;
        InstallUpdateButton.IsEnabled = false;
        DismissUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateBannerSubtitle.Text = "Скачивание обновления: 0%...";

        var progress = new Progress<double>(pct =>
        {
            UpdateProgressBar.Value = pct;
            UpdateBannerSubtitle.Text = $"Скачивание обновления: {pct:0}%...";
        });

        try
        {
            string installerPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, progress);
            UpdateBannerSubtitle.Text = "Запуск установщика обновления...";
            UpdateService.LaunchInstallerAndExit(installerPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            InstallUpdateButton.IsEnabled = true;
            DismissUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateBannerSubtitle.Text = "Ошибка скачивания. Попробуйте ещё раз.";
            MessageBox.Show(this,
                $"Не удалось скачать обновление:\n{ex.Message}",
                "Ошибка обновления",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DismissUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://boosty.to/worldruschel/donate",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось открыть ссылку:\n{ex.Message}",
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void FolderPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FolderPlaceholder.Visibility = string.IsNullOrWhiteSpace(FolderPathBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для анализа и очистки",
            Multiselect = false
        };

        if (Directory.Exists(FolderPathBox.Text))
        {
            dialog.InitialDirectory = FolderPathBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderPathBox.Text = dialog.FolderName;
            BottomStatusText.Text = $"Выбрана папка: {dialog.FolderName}. Нажмите «Старт» для анализа.";
        }
    }

    private void QuickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        string path = tag switch
        {
            "Downloads" => GetDownloadsFolder(),
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Temp" => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            FolderPathBox.Text = path;
            BottomStatusText.Text = $"Выбрана папка: {path}. Нажмите «Старт» для анализа.";
        }
        else
        {
            MessageBox.Show(this, "Не удалось найти указанную системную папку.", "Чистильщик папок",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // If currently scanning, act as Cancel button
        if (_scanCts is not null)
        {
            _scanCts.Cancel();
            ProgressStatusText.Text = "Отмена анализа...";
            return;
        }

        string folder = FolderPathBox.Text.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            BrowseButton_Click(sender, e);
            folder = FolderPathBox.Text.Trim();
            if (string.IsNullOrEmpty(folder)) return;
        }

        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this,
                $"Указанная папка не существует:\n{folder}\n\nВыберите существующую папку.",
                "Папка не найдена",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await RunScanAsync(folder);
    }

    private async Task RunScanAsync(string folder)
    {
        _scanCts = new CancellationTokenSource();
        SetBusyState(true, isScanning: true);
        ProgressStatusText.Text = "Сканирование файлов и папок...";
        ProgressCountText.Text = "";
        BottomStatusText.Text = $"Анализ папки {folder}...";

        _allRecommendations.Clear();
        _visibleItems.Clear();

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressStatusText.Text = p.StatusText;
            ProgressCountText.Text = p.FilesScanned > 0 ? $"Файлов: {p.FilesScanned:N0}" : "";
        });

        try
        {
            var result = await _analyzer.AnalyzeAsync(folder, progress, _scanCts.Token);
            _hasScanned = true;

            _allRecommendations.AddRange(result.Recommendations);
            StatScannedFiles.Text = result.TotalFilesScanned.ToString("N0");
            StatTotalSize.Text = $"Общий размер: {SizeFormatter.Format(result.TotalFolderSizeBytes)}";
            StatDuration.Text = $"Анализ за {result.Duration.TotalSeconds:0.0} с";

            FilterAll.IsChecked = true;
            _activeCategoryFilter = null;
            SummarySection.Visibility = Visibility.Visible;

            ApplyFilterAndRefreshUi();

            string skippedNote = result.SkippedPathsCount > 0
                ? $" (пропущено без доступа: {result.SkippedPathsCount})"
                : "";
            BottomStatusText.Text = result.Recommendations.Count > 0
                ? $"Найдено рекомендаций: {result.Recommendations.Count}{skippedNote}. Вы можете удалять по одному или нажать «Очистить всё»."
                : $"Анализ завершён{skippedNote}. Лишних файлов не обнаружено.";
        }
        catch (OperationCanceledException)
        {
            BottomStatusText.Text = "Анализ отменён пользователем.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Ошибка при анализе папки:\n{ex.Message}",
                "Ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            BottomStatusText.Text = "Произошла ошибка при анализе.";
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            SetBusyState(false, isScanning: false);
        }
    }

    private void FilterPill_Checked(object sender, RoutedEventArgs e)
    {
        if (!_hasScanned || sender is not RadioButton rb || rb.Tag is not string tag) return;

        _activeCategoryFilter = tag switch
        {
            "TempAndCache" => CleanupCategory.TempAndCache,
            "Duplicates" => CleanupCategory.Duplicates,
            "EmptyItems" => CleanupCategory.EmptyItems,
            "Logs" => CleanupCategory.Logs,
            "Installers" => CleanupCategory.Installers,
            "LargeFiles" => CleanupCategory.LargeFiles,
            _ => null
        };

        ApplyFilterAndRefreshUi();
    }

    private void ApplyFilterAndRefreshUi()
    {
        _visibleItems.Clear();

        IEnumerable<CleanupItem> filtered = _activeCategoryFilter is null
            ? _allRecommendations
            : _allRecommendations.Where(x => x.Category == _activeCategoryFilter.Value);

        foreach (var item in filtered)
        {
            _visibleItems.Add(item);
        }

        UpdateFilterCounts();
        UpdateStatsAndButtons();
        UpdateMainContentVisibility();
    }

    private void UpdateFilterCounts()
    {
        FilterAll.Content = $"Все ({_allRecommendations.Count})";
        SetPillLabel(FilterTemp, "Временные и кэш", CleanupCategory.TempAndCache);
        SetPillLabel(FilterDuplicates, "Дубликаты", CleanupCategory.Duplicates);
        SetPillLabel(FilterEmpty, "Пустые", CleanupCategory.EmptyItems);
        SetPillLabel(FilterLogs, "Логи", CleanupCategory.Logs);
        SetPillLabel(FilterInstallers, "Установщики", CleanupCategory.Installers);
        SetPillLabel(FilterLarge, "Крупные файлы", CleanupCategory.LargeFiles);
    }

    private void SetPillLabel(RadioButton pill, string title, CleanupCategory category)
    {
        int count = _allRecommendations.Count(x => x.Category == category);
        pill.Content = count > 0 ? $"{title} ({count})" : title;
        pill.Visibility = count > 0 || pill.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatsAndButtons()
    {
        int totalFound = _allRecommendations.Count;
        long totalReclaimable = _allRecommendations.Sum(x => x.SizeBytes);

        var selectedItems = _allRecommendations.Where(x => x.IsSelected).ToList();
        int selectedCount = selectedItems.Count;
        long selectedBytes = selectedItems.Sum(x => x.SizeBytes);

        StatFoundCount.Text = totalFound.ToString("N0");
        StatSelectedHint.Text = $"Выбрано к очистке: {selectedCount}";
        StatReclaimable.Text = SizeFormatter.Format(totalReclaimable);
        StatFreedSize.Text = SizeFormatter.Format(_sessionFreedBytes);
        StatFreedCount.Text = $"Удалено объектов: {_sessionFreedCount}";

        // Sync SelectAllCheckBox for visible items
        if (_visibleItems.Count == 0)
        {
            SelectAllCheckBox.IsChecked = false;
            SelectAllCheckBox.IsEnabled = false;
        }
        else
        {
            SelectAllCheckBox.IsEnabled = !_isBusy;
            int visSelected = _visibleItems.Count(x => x.IsSelected);
            SelectAllCheckBox.IsChecked = visSelected == _visibleItems.Count
                ? true
                : visSelected == 0 ? false : null;
        }

        // Clean All button state
        if (totalFound == 0 || _isBusy)
        {
            CleanAllButton.IsEnabled = false;
            CleanAllButton.Content = "🗑️  Очистить всё";
        }
        else if (selectedCount > 0)
        {
            CleanAllButton.IsEnabled = true;
            CleanAllButton.Content = $"🗑️  Очистить всё ({selectedCount} • {SizeFormatter.Format(selectedBytes)})";
        }
        else
        {
            CleanAllButton.IsEnabled = true;
            CleanAllButton.Content = $"🗑️  Очистить всё ({totalFound} • {SizeFormatter.Format(totalReclaimable)})";
        }
    }

    private void UpdateMainContentVisibility()
    {
        if (!_hasScanned)
        {
            WelcomeState.Visibility = Visibility.Visible;
            CleanState.Visibility = Visibility.Collapsed;
            RecommendationsList.Visibility = Visibility.Collapsed;
            return;
        }

        WelcomeState.Visibility = Visibility.Collapsed;

        if (_visibleItems.Count == 0)
        {
            RecommendationsList.Visibility = Visibility.Collapsed;
            CleanState.Visibility = Visibility.Visible;

            if (_allRecommendations.Count == 0)
            {
                CleanStateTitle.Text = _sessionFreedCount > 0
                    ? "Очистка завершена!"
                    : "Папка в отличном порядке!";
                CleanStateSubtitle.Text = _sessionFreedCount > 0
                    ? $"Удалено объектов: {_sessionFreedCount}, освобождено {SizeFormatter.Format(_sessionFreedBytes)}. Все файлы перемещены в Корзину."
                    : "Временных файлов, дубликатов и пустых папок не обнаружено.";
            }
            else
            {
                CleanStateTitle.Text = "В этой категории пусто";
                CleanStateSubtitle.Text = "Переключитесь на вкладку «Все», чтобы увидеть остальные рекомендации.";
            }
        }
        else
        {
            CleanState.Visibility = Visibility.Collapsed;
            RecommendationsList.Visibility = Visibility.Visible;
        }
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatsAndButtons();
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool selectAll = SelectAllCheckBox.IsChecked == true;
        foreach (var item in _visibleItems)
        {
            item.IsSelected = selectAll;
        }
        UpdateStatsAndButtons();
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CleanupItem item)
        {
            FileCleaner.RevealInExplorer(item.FullPath);
        }
    }

    private async void DeleteSingleItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button btn || btn.Tag is not CleanupItem item) return;

        // Extra confirmation only for large personal files
        if (!item.IsSafe)
        {
            var confirm = MessageBox.Show(this,
                $"Переместить крупный файл «{item.Name}» ({item.SizeDisplay}) в Корзину?",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        item.IsDeleting = true;
        var (success, error) = await Task.Run(() => FileCleaner.MoveToRecycleBin(item.FullPath));
        item.IsDeleting = false;

        if (success)
        {
            _sessionFreedBytes += item.SizeBytes;
            _sessionFreedCount++;
            _allRecommendations.Remove(item);
            _visibleItems.Remove(item);

            UpdateFilterCounts();
            UpdateStatsAndButtons();
            UpdateMainContentVisibility();
            BottomStatusText.Text = $"Перемещено в Корзину: {item.Name} ({item.SizeDisplay})";
        }
        else
        {
            MessageBox.Show(this,
                $"Не удалось удалить «{item.Name}»:\n{error}",
                "Не удалось удалить",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CleanAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _allRecommendations.Count == 0) return;

        var targets = _allRecommendations.Where(x => x.IsSelected).ToList();
        if (targets.Count == 0)
        {
            targets = _allRecommendations.ToList();
        }

        long totalBytes = targets.Sum(x => x.SizeBytes);
        bool includesLarge = targets.Any(x => !x.IsSafe);
        string extraWarn = includesLarge
            ? "\n\nВнимание: в список включены крупные файлы из категории «На проверку»."
            : "";

        var confirm = MessageBox.Show(this,
            $"Переместить {targets.Count} объектов ({SizeFormatter.Format(totalBytes)}) в Корзину?{extraWarn}\n\nВы всегда сможете восстановить их из Корзины Windows.",
            "Очистить всё",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        SetBusyState(true, isScanning: false);
        MainProgressBar.IsIndeterminate = false;
        MainProgressBar.Minimum = 0;
        MainProgressBar.Maximum = targets.Count;
        MainProgressBar.Value = 0;

        int deletedNow = 0;
        long freedNow = 0;
        int failedNow = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var item = targets[i];
            ProgressStatusText.Text = $"Удаление: {item.Name}";
            ProgressCountText.Text = $"{i + 1} / {targets.Count}";
            MainProgressBar.Value = i + 1;

            var (success, _) = await Task.Run(() => FileCleaner.MoveToRecycleBin(item.FullPath));
            if (success)
            {
                deletedNow++;
                freedNow += item.SizeBytes;
                _sessionFreedCount++;
                _sessionFreedBytes += item.SizeBytes;
                _allRecommendations.Remove(item);
                _visibleItems.Remove(item);
            }
            else
            {
                failedNow++;
            }
        }

        MainProgressBar.IsIndeterminate = true;
        SetBusyState(false, isScanning: false);
        UpdateFilterCounts();
        UpdateStatsAndButtons();
        UpdateMainContentVisibility();

        string summaryMsg = failedNow == 0
            ? $"Готово! Перемещено в Корзину: {deletedNow} объектов ({SizeFormatter.Format(freedNow)})."
            : $"Перемещено в Корзину: {deletedNow} объектов ({SizeFormatter.Format(freedNow)}). Не удалось удалить (заняты системой): {failedNow}.";

        BottomStatusText.Text = summaryMsg;
        MessageBox.Show(this, summaryMsg, "Очистка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetBusyState(bool busy, bool isScanning)
    {
        _isBusy = busy;
        BrowseButton.IsEnabled = !busy;
        FolderPathBox.IsEnabled = !busy;
        CleanAllButton.IsEnabled = !busy && _allRecommendations.Count > 0;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (isScanning)
        {
            StartButton.Content = "✕  Отмена";
            StartButton.IsEnabled = true;
        }
        else
        {
            StartButton.Content = "▶  Старт";
            StartButton.IsEnabled = !busy;
        }
    }

    private static string GetDownloadsFolder()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads");
    }
}
