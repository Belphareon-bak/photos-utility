using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using FotoArchiv.App.Common;
using FotoArchiv.App.Models;
using FotoArchiv.App.Services;
using Microsoft.Win32;

namespace FotoArchiv.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly MediaScanner _scanner = new();
    private readonly MetadataReaderService _metadata = new();
    private readonly GeoNamesService _geoNames = new();
    private readonly SidecarMetadataService _sidecars = new();
    private readonly DuplicateDetectionService _duplicates = new();
    private readonly OrganizationPlanner _planner = new();
    private readonly CatalogService _catalog = new();
    private readonly ThumbnailService _thumbnails = new();
    private readonly OperationExecutor _executor;
    private CancellationTokenSource? _previewCancellation;

    private SourceFolder? _selectedSource;
    private DuplicateGroup? _selectedDuplicateGroup;
    private MediaItem? _selectedMedia;
    private OperationHistoryItem? _selectedHistory;
    private BitmapSource? _previewImage;
    private string _destinationRoot = string.Empty;
    private bool _splitLocationByDate = true;
    private bool _includeCountry;
    private bool _includeRegion;
    private bool _isMoveMode;
    private bool _isBusy;
    private bool _duplicateScanCompleted;
    private bool _duplicatePhaseCompleted;
    private int _activeTabIndex;
    private double _progressValue;
    private string _statusMessage = "Přidejte jednu nebo více zdrojových složek.";

    public MainViewModel()
    {
        _executor = new OperationExecutor(_catalog);

        AddSourceCommand = new RelayCommand(AddSource);
        RemoveSourceCommand = new RelayCommand<SourceFolder>(RemoveSource, source => source is not null && !IsBusy);
        SelectDestinationCommand = new RelayCommand(SelectDestination, () => !IsBusy);
        ScanDuplicatesCommand = new AsyncRelayCommand(ScanDuplicatesAsync, () => Sources.Any(source => source.IsEnabled) && !IsBusy, HandleError);
        KeepRecommendedCommand = new RelayCommand(KeepRecommended, () => SelectedDuplicateGroup is not null && !IsBusy);
        KeepSelectedCommand = new RelayCommand(KeepSelected, () => SelectedDuplicateGroup is not null && SelectedMedia is not null && !IsBusy);
        KeepAllCommand = new RelayCommand(KeepAll, () => SelectedDuplicateGroup is not null && !IsBusy);
        RunQuarantineCommand = new AsyncRelayCommand(RunQuarantineAsync,
            () => DuplicateGroups.Count > 0 && DuplicateGroups.All(group => group.IsResolved) && !DuplicatePhaseCompleted && !IsBusy,
            HandleError);
        BuildPlanCommand = new AsyncRelayCommand(BuildPlanAsync,
            () => DuplicatePhaseCompleted && !string.IsNullOrWhiteSpace(DestinationRoot) && !IsBusy,
            HandleError);
        ExecutePlanCommand = new AsyncRelayCommand(ExecutePlanAsync,
            () => PlanItems.Any(item => item.Action is PlannedAction.Copy or PlannedAction.Move) && !IsBusy,
            HandleError);
        CancelCommand = new RelayCommand(CancelCurrent, () => IsBusy);
        UndoRunCommand = new AsyncRelayCommand(UndoRunAsync, () => SelectedHistory is not null && !IsBusy, HandleError);

        _ = LoadHistoryAsync(CancellationToken.None);
    }

    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<MediaItem> MediaItems { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<OrganizationPlanItem> PlanItems { get; } = [];
    public ObservableCollection<OperationHistoryItem> History { get; } = [];

    public RelayCommand AddSourceCommand { get; }
    public RelayCommand<SourceFolder> RemoveSourceCommand { get; }
    public RelayCommand SelectDestinationCommand { get; }
    public AsyncRelayCommand ScanDuplicatesCommand { get; }
    public RelayCommand KeepRecommendedCommand { get; }
    public RelayCommand KeepSelectedCommand { get; }
    public RelayCommand KeepAllCommand { get; }
    public AsyncRelayCommand RunQuarantineCommand { get; }
    public AsyncRelayCommand BuildPlanCommand { get; }
    public AsyncRelayCommand ExecutePlanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand UndoRunCommand { get; }

    public SourceFolder? SelectedSource
    {
        get => _selectedSource;
        set => SetProperty(ref _selectedSource, value);
    }

    public DuplicateGroup? SelectedDuplicateGroup
    {
        get => _selectedDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedDuplicateGroup, value))
            {
                SelectedMedia = value?.Items.FirstOrDefault(item => item.IsRecommendedKeep) ?? value?.Items.FirstOrDefault();
                NotifyCommands();
            }
        }
    }

    public MediaItem? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (SetProperty(ref _selectedMedia, value))
            {
                NotifyCommands();
                _ = LoadPreviewAsync(value);
            }
        }
    }

    public OperationHistoryItem? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (SetProperty(ref _selectedHistory, value)) NotifyCommands();
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public string DestinationRoot
    {
        get => _destinationRoot;
        set
        {
            if (SetProperty(ref _destinationRoot, value)) NotifyCommands();
        }
    }

    public bool SplitLocationByDate
    {
        get => _splitLocationByDate;
        set => SetProperty(ref _splitLocationByDate, value);
    }

    public bool IncludeCountry
    {
        get => _includeCountry;
        set => SetProperty(ref _includeCountry, value);
    }

    public bool IncludeRegion
    {
        get => _includeRegion;
        set => SetProperty(ref _includeRegion, value);
    }

    public bool IsMoveMode
    {
        get => _isMoveMode;
        set => SetProperty(ref _isMoveMode, value);
    }

    public bool IsCopyMode
    {
        get => !IsMoveMode;
        set { if (value) IsMoveMode = false; }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) NotifyCommands();
        }
    }

    public bool DuplicateScanCompleted
    {
        get => _duplicateScanCompleted;
        private set => SetProperty(ref _duplicateScanCompleted, value);
    }

    public bool DuplicatePhaseCompleted
    {
        get => _duplicatePhaseCompleted;
        private set
        {
            if (SetProperty(ref _duplicatePhaseCompleted, value)) NotifyCommands();
        }
    }

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => SetProperty(ref _activeTabIndex, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string MediaSummary => $"{MediaItems.Count:N0} souborů";
    public string DuplicateSummary => DuplicateScanCompleted
        ? $"{DuplicateGroups.Count:N0} skupin k posouzení"
        : "Kontrola zatím neproběhla";

    private void AddSource()
    {
        var dialog = new OpenFolderDialog { Title = "Vyberte zdrojové složky", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var folder in dialog.FolderNames)
        {
            var fullPath = Path.GetFullPath(folder);
            if (Sources.All(source => !source.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                Sources.Add(new SourceFolder(fullPath));
            }
        }

        NotifyCommands();
    }

    private void RemoveSource(SourceFolder? source)
    {
        if (source is not null) Sources.Remove(source);
        NotifyCommands();
    }

    private void SelectDestination()
    {
        var dialog = new OpenFolderDialog { Title = "Vyberte cílovou složku" };
        if (dialog.ShowDialog() == true) DestinationRoot = dialog.FolderName;
    }

    private async Task ScanDuplicatesAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            MediaItems.Clear();
            DuplicateGroups.Clear();
            PlanItems.Clear();
            DuplicateScanCompleted = false;
            DuplicatePhaseCompleted = false;
            ProgressValue = 0;

            var warnings = new ConcurrentQueue<string>();
            StatusMessage = "Procházím zdrojové složky…";
            var descriptors = await Task.Run(() => _scanner.EnumerateFiles(Sources, warnings.Enqueue, cancellationToken), cancellationToken);
            var sourceOffsets = Sources
                .Where(source => source.IsEnabled)
                .ToDictionary(source => Path.GetFullPath(source.Path), source => source.CaptureTimeOffsetHours, StringComparer.OrdinalIgnoreCase);
            var scanned = new ConcurrentBag<MediaItem>();
            var completed = 0;
            var progress = new Progress<string>(message => StatusMessage = message);

            await Parallel.ForEachAsync(descriptors,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                async (descriptor, token) =>
                {
                    var item = await _metadata.ReadAsync(descriptor.Path, descriptor.Root, descriptor.Kind, token);
                    if (item.CapturedAt.HasValue && sourceOffsets.TryGetValue(descriptor.Root, out var offset) && offset != 0)
                    {
                        item.CapturedAt = item.CapturedAt.Value.AddHours(offset);
                        item.CaptureDateSource += $"; korekce {offset:+0.##;-0.##} h";
                    }
                    scanned.Add(item);
                    var current = Interlocked.Increment(ref completed);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProgressValue = descriptors.Count == 0 ? 0 : current * 45d / descriptors.Count;
                        StatusMessage = $"Metadata: {current:N0} / {descriptors.Count:N0}";
                    });
                });

            var scannedItems = scanned.ToList();
            await _sidecars.ApplyAsync(scannedItems, progress, cancellationToken);
            var ordered = scannedItems.OrderBy(item => item.CapturedAt).ThenBy(item => item.FilePath).ToList();
            await _geoNames.ResolveAsync(ordered, progress, cancellationToken);
            ProgressValue = 55;
            var duplicateGroups = await _duplicates.DetectAsync(ordered, progress, cancellationToken);

            foreach (var item in ordered) MediaItems.Add(item);
            foreach (var group in duplicateGroups) DuplicateGroups.Add(group);
            SelectedDuplicateGroup = DuplicateGroups.FirstOrDefault();
            SelectedMedia ??= MediaItems.FirstOrDefault();
            DuplicateScanCompleted = true;
            DuplicatePhaseCompleted = DuplicateGroups.Count == 0;
            ProgressValue = 100;
            StatusMessage = warnings.TryPeek(out var warning)
                ? $"Kontrola dokončena. Upozornění: {warning}"
                : $"Kontrola dokončena: {MediaItems.Count:N0} souborů, {DuplicateGroups.Count:N0} skupin duplicit.";
            ActiveTabIndex = DuplicateGroups.Count == 0 ? 2 : 1;
            OnPropertyChanged(nameof(MediaSummary));
            OnPropertyChanged(nameof(DuplicateSummary));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void KeepRecommended()
    {
        if (SelectedDuplicateGroup is null) return;
        foreach (var item in SelectedDuplicateGroup.Items) item.WillKeep = item.IsRecommendedKeep;
        SelectedDuplicateGroup.IsResolved = true;
        RefreshDuplicatePhaseState();
    }

    private void KeepSelected()
    {
        if (SelectedDuplicateGroup is null || SelectedMedia is null) return;
        foreach (var item in SelectedDuplicateGroup.Items) item.WillKeep = ReferenceEquals(item, SelectedMedia);
        SelectedDuplicateGroup.IsResolved = true;
        RefreshDuplicatePhaseState();
    }

    private void KeepAll()
    {
        if (SelectedDuplicateGroup is null) return;
        foreach (var item in SelectedDuplicateGroup.Items) item.WillKeep = true;
        SelectedDuplicateGroup.IsResolved = true;
        RefreshDuplicatePhaseState();
    }

    private void RefreshDuplicatePhaseState()
    {
        if (DuplicateGroups.All(group => group.IsResolved) && MediaItems.All(item => item.WillKeep))
        {
            DuplicatePhaseCompleted = true;
            StatusMessage = "Kontrola duplicit je uzavřená; všechny soubory zůstávají.";
        }
        else if (DuplicateGroups.All(group => group.IsResolved))
        {
            StatusMessage = "Rozhodnutí jsou připravena. Spusťte přesun do kontrolní složky.";
        }

        NotifyCommands();
    }

    private async Task RunQuarantineAsync(CancellationToken cancellationToken)
    {
        var plan = _planner.BuildQuarantinePlan(MediaItems, CurrentSettings());
        if (plan.Count == 0)
        {
            DuplicatePhaseCompleted = true;
            return;
        }

        var answer = MessageBox.Show(
            $"{plan.Count} souborů bude přesunuto do složek _DuplicatesReview u původních zdrojů. Pokračovat?",
            "Potvrdit přesun duplicit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                ProgressValue = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                StatusMessage = $"Přesouvám duplicity: {value.Completed} / {value.Total}";
            });
            await _executor.ExecuteAsync(plan, "Duplicity", progress, cancellationToken);
            var hasErrors = plan.Any(item => item.Status == "Chyba");
            DuplicatePhaseCompleted = !hasErrors;
            StatusMessage = hasErrors
                ? "Běh duplicit skončil s chybami. Organizace zůstává zablokovaná."
                : "Samostatný běh duplicit byl dokončen.";
            if (!hasErrors) ActiveTabIndex = 2;
            await LoadHistoryAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task BuildPlanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsBusy = true;
        try
        {
            ValidateDestination();
            PlanItems.Clear();
            foreach (var item in _planner.Build(MediaItems, CurrentSettings())) PlanItems.Add(item);
            StatusMessage = $"Náhled připraven: {PlanItems.Count:N0} operací. Zatím nebyl změněn žádný soubor.";
            ProgressValue = 100;
            ActiveTabIndex = 3;
            NotifyCommands();
            return Task.CompletedTask;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecutePlanAsync(CancellationToken cancellationToken)
    {
        var action = IsMoveMode ? "přesunuto" : "zkopírováno";
        var answer = MessageBox.Show(
            $"Bude {action} {PlanItems.Count(item => item.Action is PlannedAction.Copy or PlannedAction.Move):N0} souborů. Každá kopie se ověří kontrolním součtem. Pokračovat?",
            "Spustit organizaci", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                ProgressValue = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                StatusMessage = $"Organizuji archiv: {value.Completed} / {value.Total}";
            });
            await _executor.ExecuteAsync(PlanItems.ToList(), "Organizace", progress, cancellationToken);
            StatusMessage = PlanItems.Any(item => item.Status == "Chyba")
                ? "Organizace skončila s chybami; podrobnosti jsou v tabulce a historii."
                : "Organizace byla dokončena. Výsledek je uložen v historii.";
            ActiveTabIndex = 4;
            await LoadHistoryAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ValidateDestination()
    {
        var destination = Path.GetFullPath(DestinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var source in Sources.Where(source => source.IsEnabled))
        {
            var root = Path.GetFullPath(source.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cílová složka nesmí ležet uvnitř zdrojové složky; další sken by znovu načetl výstup.");
            }
        }
    }

    private AppSettings CurrentSettings() => new()
    {
        DestinationRoot = DestinationRoot,
        SplitLocationByDate = SplitLocationByDate,
        IncludeCountry = IncludeCountry,
        IncludeRegion = IncludeRegion,
        TransferMode = IsMoveMode ? TransferMode.Move : TransferMode.Copy
    };

    private async Task LoadPreviewAsync(MediaItem? item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        try
        {
            PreviewImage = await _thumbnails.LoadAsync(item, _previewCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.InitializeAsync(cancellationToken);
            var history = await _catalog.GetHistoryAsync(cancellationToken);
            History.Clear();
            foreach (var item in history) History.Add(item);
            SelectedHistory = History.FirstOrDefault();
        }
        catch
        {
            // History is supplementary; file analysis can continue if its local database is unavailable.
        }
    }

    private async Task UndoRunAsync(CancellationToken cancellationToken)
    {
        if (SelectedHistory is null) return;
        var answer = MessageBox.Show(
            $"Vrátit běh #{SelectedHistory.RunId}? Změněné soubory a obsazené původní cesty budou přeskočeny.",
            "Vrátit běh", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                ProgressValue = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                StatusMessage = $"Vracím běh: {value.Completed} / {value.Total}";
            });
            await _executor.UndoAsync(SelectedHistory.RunId, progress, cancellationToken);
            StatusMessage = $"Vrácení běhu #{SelectedHistory.RunId} bylo dokončeno.";
            await LoadHistoryAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CancelCurrent()
    {
        ScanDuplicatesCommand.Cancel();
        RunQuarantineCommand.Cancel();
        BuildPlanCommand.Cancel();
        ExecutePlanCommand.Cancel();
        UndoRunCommand.Cancel();
        StatusMessage = "Probíhající operace bude bezpečně ukončena po aktuálním souboru.";
    }

    private void HandleError(Exception exception)
    {
        IsBusy = false;
        StatusMessage = exception.Message;
        MessageBox.Show(exception.Message, "FotoArchiv", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void NotifyCommands()
    {
        RemoveSourceCommand.NotifyCanExecuteChanged();
        SelectDestinationCommand.NotifyCanExecuteChanged();
        ScanDuplicatesCommand.NotifyCanExecuteChanged();
        KeepRecommendedCommand.NotifyCanExecuteChanged();
        KeepSelectedCommand.NotifyCanExecuteChanged();
        KeepAllCommand.NotifyCanExecuteChanged();
        RunQuarantineCommand.NotifyCanExecuteChanged();
        BuildPlanCommand.NotifyCanExecuteChanged();
        ExecutePlanCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        UndoRunCommand.NotifyCanExecuteChanged();
    }
}
