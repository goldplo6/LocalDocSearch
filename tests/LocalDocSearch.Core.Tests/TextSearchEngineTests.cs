using System.Security.Cryptography;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class TextSearchEngineTests
{
    [Theory]
    [InlineData("瓜", 3)]
    [InlineData("西瓜", 3)]
    [InlineData("西瓜汁", 1)]
    public async Task ChineseOneTwoAndThreeCharacterTermsAreLiteralSubstrings(string term, int expected)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("中文.txt", "西瓜和西瓜汁及大西瓜");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, [term]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(expected, Assert.Single(capture.Results).TotalOccurrences);
    }

    [Fact]
    public async Task ReturnsEveryOneOfFiftyRepeatedOccurrences()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("重复.log", string.Concat(Enumerable.Repeat("西瓜 ", 50)));
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        var result = Assert.Single(capture.Results);
        Assert.Equal(50, result.TotalOccurrences);
        Assert.Equal(50, summary.Progress.Occurrences);
        Assert.Equal(148, result.Matches[^1].Location.Column);
    }

    [Fact]
    public async Task RegexMetacharactersAreLiteralText()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("literal.md", "任意文本 .* 不是正则 .* aaaaa");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, [".*"]));
        Assert.Equal(2, Assert.Single(capture.Results).TotalOccurrences);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task EnglishUsesExplicitOrdinalCaseMode(bool matchCase, int expected)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("case.txt", "Apple apple APPLE");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["apple"], MatchCase: matchCase));
        Assert.Equal(expected, Assert.Single(capture.Results).TotalOccurrences);
    }

    [Fact]
    public async Task CaseInsensitiveDeduplicationPreservesFirstTermAndOriginalMatchedText()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("dedupe.txt", "APPLE apple");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["apple", "APPLE", "apple"]));
        var matches = Assert.Single(capture.Results).Matches;
        Assert.Equal(2, matches.Count);
        Assert.All(matches, item => Assert.Equal("apple", item.Term));
        Assert.Equal("APPLE", matches[0].Text);
    }

    [Fact]
    public async Task CaseSensitiveDeduplicationKeepsDifferentlyCasedTerms()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("dedupe.txt", "APPLE apple");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["apple", "APPLE", "apple"], MatchCase: true));
        Assert.Equal(2, Assert.Single(capture.Results).TotalOccurrences);
        Assert.Equal(new[] { "APPLE", "apple" }, capture.Results[0].Matches.Select(item => item.Term));
    }

    [Fact]
    public async Task NestedTermsCountIndependentlyAndEachTermDoesNotOverlapItself()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("nested.txt", "aaaaa");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["aa", "aaa", "aa"]));
        var matches = Assert.Single(capture.Results).Matches;
        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { 1, 3 }, matches.Where(item => item.Term == "aa").Select(item => item.Location.Column));
        Assert.Equal(1, Assert.Single(matches, item => item.Term == "aaa").Location.Column);
    }

    [Fact]
    public async Task DefaultAndIsFileScopedAcrossPhysicalLines()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("both.txt", "西瓜\r\n\r\n苹果");
        await directory.WriteAsync("one.txt", "只有西瓜");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]));
        var result = Assert.Single(capture.Results);
        Assert.Equal("both.txt", Path.GetFileName(result.FullPath));
        Assert.Equal(new long[] { 1, 3 }, result.Matches.Select(item => item.Location.Line));
        Assert.Equal(2, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.MatchedFiles);
        Assert.Equal(2, summary.Progress.Occurrences);
    }

    [Fact]
    public async Task OrEmitsFilesContainingAnyTerm()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "西瓜");
        await directory.WriteAsync("two.txt", "苹果");
        await directory.WriteAsync("none.txt", "梨");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"], SearchOperator.Or));
        Assert.Equal(2, capture.Results.Count);
        Assert.Equal(3, summary.Progress.ScannedFiles);
        Assert.Equal(2, summary.Progress.MatchedFiles);
        Assert.Equal(2, summary.Progress.Occurrences);
    }

    [Fact]
    public async Task RecursesAndDefaultsToAllRegisteredTextFormats()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("root.txt", "西瓜");
        await directory.WriteAsync(Path.Combine("子目录", "inner.MD"), "西瓜");
        await directory.WriteAsync(Path.Combine("子目录", "更深", "trace.log"), "西瓜");
        await directory.WriteAsync("excluded.csv", "西瓜");
        await directory.WriteAsync("~$lock.txt", "西瓜");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(4, capture.Results.Count);
        Assert.Equal(4, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.SkippedFiles);
        Assert.All(capture.Results, result => Assert.True(Path.IsPathFullyQualified(result.FullPath)));
        Assert.Contains(capture.Results, result => result.Extension == ".md");
    }

    [Fact]
    public async Task ExtensionFilteringAndTemporaryFileSkippingHappenBeforeDecoding()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("included.MD", "西瓜");
        await directory.WriteBytesAsync("excluded.txt", [0xff]);
        await directory.WriteBytesAsync("~$temporary.md", [0xff]);
        await directory.WriteBytesAsync("excluded.pdf", [0xff]);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"], Extensions: [" MD ", ".pdf"]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Single(capture.Results);
        Assert.Empty(capture.Issues);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(3, summary.Progress.SkippedFiles);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task StrictUnicodeEncodingVariantsReturnIdenticalTextAndLocations(string name)
    {
        Encoding encoding = name switch
        {
            "utf8" => new UTF8Encoding(false, true),
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true)
        };
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("encoded.txt", "开头\r\n西瓜", encoding);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        var match = Assert.Single(Assert.Single(capture.Results).Matches);
        Assert.Equal("西瓜", match.Text);
        Assert.Equal(new SourceLocation(2, 1), match.Location);
        Assert.Empty(capture.Issues);
    }

    [Fact]
    public async Task InvalidUtf8IsIsolatedAndDiscardsMatchesSeenBeforeLateDecodeFailure()
    {
        using var directory = new SearchTestDirectory();
        var content = Encoding.UTF8.GetBytes("西瓜\n" + new string('x', 40_000));
        var badPath = await directory.WriteBytesAsync("bad.txt", [.. content, 0xff]);
        await directory.WriteAsync("good.txt", "西瓜");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal("good.txt", Path.GetFileName(Assert.Single(capture.Results).FullPath));
        var issue = Assert.Single(capture.Issues);
        Assert.Equal(badPath, issue.Path);
        Assert.Contains("解码失败", issue.Message);
        Assert.Contains("GB18030", issue.Message);
        Assert.Equal(2, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.SkippedFiles);
        Assert.Equal(1, summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedBomUnicodeIsStrictRatherThanSilentlyReplacingCharacters(bool utf16)
    {
        using var directory = new SearchTestDirectory();
        byte[] content = utf16 ? [0xff, 0xfe, 0x61] : [0xef, 0xbb, 0xbf, 0xff];
        await directory.WriteBytesAsync("malformed.txt", content);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["a"]));
        Assert.Equal(SearchStatus.Failed, summary.Status);
        Assert.Empty(capture.Results);
        Assert.Contains("解码失败", Assert.Single(capture.Issues).Message);
    }

    [Fact]
    public async Task Utf32BomIsDetectedBeforeItsUtf16Prefix()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("utf32.txt", "A西瓜", new UTF32Encoding(false, true, true));
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["A西瓜"]));
        Assert.Equal("A西瓜", Assert.Single(Assert.Single(capture.Results).Matches).Text);
    }

    [Fact]
    public async Task LocationsAndContextsAreOneBasedAndStayOnTheSamePhysicalLine()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("locations.txt", "前缀西瓜后缀\r\n苹果西瓜尾\n西瓜");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"], ContextCharacters: 1));
        var matches = Assert.Single(capture.Results).Matches;
        Assert.Collection(matches,
            item => Assert.Equal(new MatchItem("西瓜", "缀", "西瓜", "后", new SourceLocation(1, 3), "line:1", 2), item),
            item => Assert.Equal(new MatchItem("西瓜", "果", "西瓜", "尾", new SourceLocation(2, 3), "line:2", 2), item),
            item => Assert.Equal(new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(3, 1), "line:3", 0), item));
    }

    [Fact]
    public async Task ColumnUsesUtf16CodeUnitsAndZeroContextIsAllowed()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("emoji.txt", "😀西瓜后");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"], ContextCharacters: 0));
        var match = Assert.Single(Assert.Single(capture.Results).Matches);
        Assert.Equal(3, match.Location.Column);
        Assert.Empty(match.Before);
        Assert.Empty(match.After);
    }

    [Fact]
    public async Task TermAcrossReaderBufferBoundaryIsNotMissed()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("buffer.log", new string('x', 16_383) + "西瓜汁" + new string('y', 20_000));
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜汁"], ContextCharacters: 2));
        var match = Assert.Single(Assert.Single(capture.Results).Matches);
        Assert.Equal(new SourceLocation(1, 16_384), match.Location);
        Assert.Equal("xx", match.Before);
        Assert.Equal("yy", match.After);
    }

    [Fact]
    public async Task EmptyDirectoryCompletesWithNoIssuesOrResults()
    {
        using var directory = new SearchTestDirectory();
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(new SearchProgress(0, 0, 0, 0, null), summary.Progress);
        Assert.Empty(capture.Results);
        Assert.Empty(capture.Issues);
    }

    [Fact]
    public async Task ValidEmptyFileIsSuccessfulEvenWithoutMatches()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("empty.txt", "");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Empty(capture.Results);
    }

    [Fact]
    public void EmptyAndWhitespaceTermsAreClearArgumentErrors()
    {
        using var directory = new SearchTestDirectory();
        var engine = new TextSearchEngine();
        foreach (IReadOnlyList<string> terms in new IReadOnlyList<string>[] { [], [""], [" "], ["西瓜", ""] })
        {
            var error = Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(new SearchRequest(directory.Root, terms)); });
            Assert.Contains("关键词", error.Message);
        }
    }

    [Fact]
    public void UnsupportedOrEmptyExtensionSelectionsAreClearArgumentErrors()
    {
        using var directory = new SearchTestDirectory();
        var engine = new TextSearchEngine();
        foreach (IReadOnlyList<string> extensions in new IReadOnlyList<string>[] { [], ["pdf"], [" "] })
        {
            var error = Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(new SearchRequest(directory.Root, ["西瓜"], Extensions: extensions)); });
            Assert.Contains("类型", error.Message);
        }
    }

    [Fact]
    public void MissingRootNegativeContextAndUnknownOperatorAreArgumentErrors()
    {
        using var directory = new SearchTestDirectory();
        var engine = new TextSearchEngine();
        foreach (var request in new[]
        {
            new SearchRequest("", ["西瓜"]),
            new SearchRequest(Path.Combine(directory.Root, "missing"), ["西瓜"]),
            new SearchRequest(directory.Root, ["西瓜"], ContextCharacters: -1),
            new SearchRequest(directory.Root, ["西瓜"], (SearchOperator)100)
        })
            Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(request); });
        Assert.Throws<ArgumentNullException>(() => { _ = engine.SearchAsync(null!); });
        Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(new SearchRequest(directory.Root, null!)); });
    }

    [Fact]
    public async Task PreCancellationReturnsCancelledRatherThanThrowingOrPublishingResults()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.txt", "西瓜");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]), cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Equal(0, summary.Progress.ScannedFiles);
        Assert.Empty(capture.Results);
        Assert.Empty(capture.Issues);
    }

    [Fact]
    public async Task CancellingOnFirstResultStopsAllSubsequentFileResults()
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < 5; index++)
            await directory.WriteAsync($"file-{index}.txt", "西瓜\n苹果");
        using var cancellation = new CancellationTokenSource();
        var emitted = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]),
            results: new InlineProgress<FileResult>(result =>
            {
                emitted.Add(result);
                cancellation.Cancel();
            }), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Single(emitted);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.MatchedFiles);
        Assert.Equal(2, summary.Progress.Occurrences);
    }

    [Fact]
    public async Task CancellingAtFileStartDoesNotEmitAnIncompleteAndResult()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("partial.txt", "西瓜\n苹果");
        using var cancellation = new CancellationTokenSource();
        var emitted = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]),
            progress: new InlineProgress<SearchProgress>(value =>
            {
                if (value.CurrentPath == path) cancellation.Cancel();
            }), results: new InlineProgress<FileResult>(emitted.Add), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Equal(0, summary.Progress.ScannedFiles);
        Assert.Empty(emitted);
    }

    [Fact]
    public async Task SearchDoesNotChangeOriginalSha256OrUtcModificationTime()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("只读核验.md", "原创资料\r\n西瓜 苹果\r\n西瓜");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var modified = File.GetLastWriteTimeUtc(path);
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        var after = SHA256.HashData(await File.ReadAllBytesAsync(path));
        Assert.Equal(before, after);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var result = Assert.Single(capture.Results);
        Assert.Equal(modified, result.LastWriteTimeUtc);
        Assert.Equal(DateTimeKind.Utc, result.LastWriteTimeUtc.Kind);
    }

    [Fact]
    public async Task SharedOpenFileRemainsSearchable()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("shared.log", "西瓜");
        await using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Single(capture.Results);
        Assert.Empty(capture.Issues);
    }

    [Fact]
    public async Task ExclusivelyLockedFileDoesNotStopOtherFiles()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("locked.log", "西瓜");
        await directory.WriteAsync("readable.txt", "西瓜");
        await using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal("readable.txt", Path.GetFileName(Assert.Single(capture.Results).FullPath));
        Assert.Equal(path, Assert.Single(capture.Issues).Path);
        Assert.Equal(2, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.SkippedFiles);
    }

    [Fact]
    public async Task ProgressIsReportedPerFileRatherThanPerLine()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("many-lines.log", string.Concat(Enumerable.Repeat("西瓜\n", 2_000)));
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.InRange(capture.Progress.Count, 1, 4);
        Assert.Equal(summary.Progress, capture.Progress[^1]);
        Assert.Equal(2_000, summary.Progress.Occurrences);
    }
}
