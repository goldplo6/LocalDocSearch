using CommunityToolkit.Mvvm.ComponentModel;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using LocalDocSearch.Settings;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;
    public SettingsViewModel(AppSettings settings)
    {
        _original = settings;
        ContextCharacters = settings.ContextCharacters; EncodingIndex = (int)settings.EncodingMode;
        SortMode = settings.SortMode; AllowOfficeAutomation = settings.AllowOfficeAutomation;
        WordBody = settings.Scope.WordBody; WordTables = settings.Scope.WordTables;
        ExcelHiddenSheets = settings.Scope.ExcelHiddenSheets;
        PresentationText = settings.Scope.PresentationText; PresentationTables = settings.Scope.PresentationTables;
    }
    [ObservableProperty] private int contextCharacters;
    [ObservableProperty] private int encodingIndex;
    [ObservableProperty] private int sortMode;
    [ObservableProperty] private bool allowOfficeAutomation;
    [ObservableProperty] private bool wordBody;
    [ObservableProperty] private bool wordTables;
    [ObservableProperty] private bool excelHiddenSheets;
    [ObservableProperty] private bool presentationText;
    [ObservableProperty] private bool presentationTables;
    public IReadOnlyList<string> EncodingNames => MainWindowViewModel.EncodingNames;
    public IReadOnlyList<string> SortNames => MainWindowViewModel.SortNames;
    public AppSettings Build() => _original with
    {
        ContextCharacters = Math.Clamp(ContextCharacters, 0, 500),
        EncodingMode = (TextEncodingMode)Math.Clamp(EncodingIndex, 0, 2),
        SortMode = Math.Clamp(SortMode, 0, 3), AllowOfficeAutomation = AllowOfficeAutomation,
        Scope = new DocumentScopeOptions(WordBody, WordTables, ExcelHiddenSheets, PresentationText, PresentationTables)
    };
}
