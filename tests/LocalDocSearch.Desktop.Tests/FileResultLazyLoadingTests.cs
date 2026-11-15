using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class FileResultLazyLoadingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(123)]
    public void FirstProgrammaticExpansionLoadsOnePageAndReopeningKeepsIt(int count)
    {
        var matches = CreateMatches(count);
        var file = CreateFile(matches);
        var pageNotifications = new List<string?>();
        file.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(file.HasMore) or nameof(file.LoadedText))
                pageNotifications.Add(args.PropertyName);
        };

        Assert.False(file.IsExpanded);
        Assert.Empty(file.VisibleMatches);
        Assert.Equal(count, file.TotalOccurrences);
        Assert.Equal(count > 0, file.HasMore);
        Assert.Equal($"已展示 {0:N0} / {count:N0} 处", file.LoadedText);
        Assert.Same(matches.FirstOrDefault(), file.FirstMatch);
        Assert.Equal(matches.FirstOrDefault()?.Before ?? string.Empty, file.FirstBefore);
        Assert.Equal(matches.FirstOrDefault()?.Text ?? string.Empty, file.FirstText);
        Assert.Equal(matches.FirstOrDefault()?.After ?? string.Empty, file.FirstAfter);
        Assert.Empty(file.VisibleMatches); // Reading collapsed summaries never creates wrappers.
        Assert.Empty(pageNotifications);

        file.IsExpanded = true;

        var firstPage = file.VisibleMatches.ToArray();
        Assert.Equal(Math.Min(count, 50), firstPage.Length);
        Assert.Equal(count > 50, file.HasMore);
        Assert.Equal($"已展示 {firstPage.Length:N0} / {count:N0} 处", file.LoadedText);
        Assert.Equal(new[] { nameof(file.HasMore), nameof(file.LoadedText) }, pageNotifications);
        for (var i = 0; i < firstPage.Length; i++)
        {
            Assert.Same(matches[i], firstPage[i].SourceMatch);
            Assert.Equal(file.FullPath, firstPage[i].FullPath);
        }

        pageNotifications.Clear();
        file.IsExpanded = true;
        file.IsExpanded = false;
        file.IsExpanded = true;

        Assert.Equal(firstPage.Length, file.VisibleMatches.Count);
        for (var i = 0; i < firstPage.Length; i++) Assert.Same(firstPage[i], file.VisibleMatches[i]);
        Assert.Empty(pageNotifications); // Includes empty files: initialization happens once.
        Assert.Equal(count, file.TotalOccurrences);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(3, 123)]
    public void LoadMoreBeforeFirstExpansionKeepsLoadedPagesWithoutAnExtraPage(int commands, int expected)
    {
        var matches = CreateMatches(123);
        var file = CreateFile(matches);
        for (var i = 0; i < commands; i++) file.LoadMoreCommand.Execute(null);
        Assert.False(file.IsExpanded);
        Assert.Equal(expected, file.VisibleMatches.Count);
        var loaded = file.VisibleMatches.ToArray();

        file.IsExpanded = true;
        file.IsExpanded = false;
        file.IsExpanded = true;

        Assert.Equal(expected, file.VisibleMatches.Count);
        for (var i = 0; i < loaded.Length; i++)
        {
            Assert.Same(loaded[i], file.VisibleMatches[i]);
            Assert.Same(matches[i], file.VisibleMatches[i].SourceMatch);
        }
        Assert.Equal(expected < matches.Length, file.HasMore);
        Assert.Equal($"已展示 {expected:N0} / {matches.Length:N0} 处", file.LoadedText);
    }

    [Fact]
    public void ReopeningAfterLoadingMorePreservesEveryPageAndOriginalMatch()
    {
        var matches = CreateMatches(123);
        var file = CreateFile(matches);
        file.IsExpanded = true;
        file.LoadMoreCommand.Execute(null);
        var loaded = file.VisibleMatches.ToArray();
        Assert.Equal(100, loaded.Length);

        file.IsExpanded = false;
        file.IsExpanded = true;
        Assert.Equal(100, file.VisibleMatches.Count);
        for (var i = 0; i < loaded.Length; i++) Assert.Same(loaded[i], file.VisibleMatches[i]);

        file.LoadMoreCommand.Execute(null);
        Assert.Equal(123, file.VisibleMatches.Count);
        Assert.False(file.HasMore);
        file.IsExpanded = false;
        file.IsExpanded = true;
        file.LoadMoreCommand.Execute(null);
        Assert.Equal(123, file.VisibleMatches.Count);
        Assert.Equal(123, file.VisibleMatches.Select(match => match.SourceMatch).Distinct().Count());
        for (var i = 0; i < matches.Length; i++) Assert.Same(matches[i], file.VisibleMatches[i].SourceMatch);
        Assert.Equal($"已展示 {123:N0} / {123:N0} 处", file.LoadedText);
    }

    private static MatchItem[] CreateMatches(int count) => Enumerable.Range(1, count)
        .Select(line => new MatchItem("西瓜", $"前-{line}", "西瓜", $"后-{line}", new SourceLocation(line, 2)))
        .ToArray();

    private static FileResultViewModel CreateFile(MatchItem[] matches) =>
        new(new FileResult("C:/fixtures/lazy.txt", ".txt", DateTime.UtcNow, matches));
}
