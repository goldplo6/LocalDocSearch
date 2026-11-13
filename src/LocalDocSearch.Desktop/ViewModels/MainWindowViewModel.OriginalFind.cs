using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Navigation;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private string originalFindSearchText = string.Empty;
    [ObservableProperty] private string originalFindHint = string.Empty;
    public bool HasOriginalFindTerm => OriginalFindSearchText.Length > 0;
    partial void OnOriginalFindSearchTextChanged(string value) => OnPropertyChanged(nameof(HasOriginalFindTerm));

    [RelayCommand]
    private Task FindOriginalMatchAsync(MatchViewModel? match) => match?.FullPath is null
        ? Task.CompletedTask : FindOriginalAsync(match.FullPath, match.SourceMatch.Text);

    private async Task FindOriginalAsync(string path, string text)
    {
        if (IsLocating) return;
        IsLocating = true;
        OriginalFindSearchText = string.Empty;
        OriginalFindHint = string.Empty;
        try
        {
            StatusText = "正在打开原文件并核实查找控件…";
            var result = await _findAssistant.FindAsync(new FindAssistRequest(path, text));
            if (result.Status != FindAssistStatus.Failed) OriginalFindSearchText = result.SearchText;
            OriginalFindHint = result.Message;
            StatusText = $"原文件查找：{result.Status} · {result.Message}";
        }
        catch (Exception ex)
        {
            OriginalFindHint = ex.Message;
            StatusText = $"原文件查找：Failed · {ex.Message}";
        }
        finally { IsLocating = false; }
    }
}
