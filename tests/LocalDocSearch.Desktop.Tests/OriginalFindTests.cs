using Avalonia.Headless.XUnit;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Navigation;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class OriginalFindTests
{
    [AvaloniaFact]
    public async Task OriginalOpenAndPerMatchFindPassTheActualSelectedMatchText()
    {
        var assistant = new FakeFind();
        var vm = new MainWindowViewModel(findAssistant: assistant);
        var file = File(); vm.Results.Add(file);
        await vm.OpenFileCommand.ExecuteAsync(file);
        Assert.Equal("西瓜", Assert.Single(assistant.Requests).SearchText);
        Assert.Contains("SearchFieldFilled", vm.StatusText);
        Assert.DoesNotContain("Exact", vm.StatusText);
        file.IsExpanded = true;
        await vm.FindOriginalMatchCommand.ExecuteAsync(file.VisibleMatches[1]);
        Assert.Equal("苹果", assistant.Requests[1].SearchText);
        Assert.Equal(file.FullPath, assistant.Requests[1].FullPath);
        Assert.False(vm.IsLocating);
    }

    [AvaloniaFact]
    public async Task ManualFindRetainsTheWordForExplicitCopyAndReportsItsActualLevel()
    {
        var assistant = new FakeFind { Status = FindAssistStatus.ManualFindRequired };
        var vm = new MainWindowViewModel(findAssistant: assistant);
        await vm.OpenFileCommand.ExecuteAsync(File());
        Assert.True(vm.HasOriginalFindTerm);
        Assert.Equal("西瓜", vm.OriginalFindSearchText);
        Assert.Contains("ManualFindRequired", vm.StatusText);
        Assert.DoesNotContain("Exact", vm.StatusText);
        assistant.Status = FindAssistStatus.Failed;
        await vm.OpenFileCommand.ExecuteAsync(File());
        Assert.False(vm.HasOriginalFindTerm);
        Assert.Contains("Failed", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task ConcurrentOriginalFindCommandsShareTheExistingNavigationGate()
    {
        var source = new TaskCompletionSource<FindAssistResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new FakeFind { Pending = source.Task };
        var vm = new MainWindowViewModel(findAssistant: assistant);
        var file = File();
        var pending = vm.OpenFileCommand.ExecuteAsync(file);
        Assert.True(vm.IsLocating);
        file.IsExpanded = true;
        await vm.FindOriginalMatchCommand.ExecuteAsync(file.VisibleMatches[1]);
        Assert.Single(assistant.Requests);
        source.SetResult(new FindAssistResult(FindAssistStatus.ManualFindRequired, "手工查找", "西瓜"));
        await pending;
        Assert.False(vm.IsLocating);
    }

    [AvaloniaFact]
    public async Task RevealDelegatesTheFullPathAndReportsShellFailure()
    {
        var folder = new FakeFolder();
        var vm = new MainWindowViewModel(folderRevealer: folder);
        var file = File();
        await vm.RevealFileCommand.ExecuteAsync(file);
        Assert.Equal(file.FullPath, Assert.Single(folder.Paths));
        Assert.Contains("所在目录", vm.StatusText);
        folder.Succeeded = false;
        await vm.RevealFileCommand.ExecuteAsync(file);
        Assert.Contains("打开目录失败", vm.StatusText);
    }

    private static FileResultViewModel File() => new(new FileResult(
        Path.Combine(Path.GetTempPath(), "LocalDocSearch-fixture.txt"), ".txt", DateTime.UtcNow,
        [new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(1, 1)),
         new MatchItem("苹果", "", "苹果", "", new SourceLocation(2, 1))]));

    private sealed class FakeFind : IOriginalFileFindAssistant
    {
        public List<FindAssistRequest> Requests { get; } = [];
        public FindAssistStatus Status { get; set; } = FindAssistStatus.SearchFieldFilled;
        public Task<FindAssistResult>? Pending { get; set; }
        public Task<FindAssistResult> FindAsync(FindAssistRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Pending ?? Task.FromResult(new FindAssistResult(Status, "受控后端测试", request.SearchText));
        }
    }

    private sealed class FakeFolder : IContainingFolderRevealer
    {
        public List<string> Paths { get; } = [];
        public bool Succeeded { get; set; } = true;
        public Task<FolderRevealResult> RevealAsync(string fullPath, CancellationToken cancellationToken = default)
        {
            Paths.Add(fullPath);
            return Task.FromResult(new FolderRevealResult(Succeeded, "受控 Shell 结果"));
        }
    }
}
