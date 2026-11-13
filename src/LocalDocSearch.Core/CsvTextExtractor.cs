using System.Runtime.CompilerServices;
using System.Text;

namespace LocalDocSearch.Core;

/// <summary>Comma-separated fields with RFC-style quotes; normalizes CRLF/CR to LF inside fields.</summary>
public sealed class CsvTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".csv" };

    public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = await UnicodeTextReader.OpenAsync(path, context, cancellationToken).ConfigureAwait(false);
        var input = new CsvCharReader(reader);
        var value = new StringBuilder();
        var state = FieldState.Start;
        long record = 1, fieldStartLine = 1;
        var field = 1;
        var pendingRecord = false;
        while (await input.ReadAsync(cancellationToken).ConfigureAwait(false) is var current && current >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = (char)current;
            pendingRecord = true;
            if (state == FieldState.Quoted)
            {
                if (character == '"') state = FieldState.AfterQuote;
                else Append(value, character, context.Limits.MaxSegmentCharacters);
                continue;
            }
            if (character is ',' or '\n')
            {
                yield return new TextSegment($"csv:{record}:{field}", value.ToString(),
                    new SourceLocator(SourceKind.CsvField, Line: fieldStartLine, Record: record, Field: field));
                value.Clear();
                state = FieldState.Start;
                fieldStartLine = input.Line;
                if (character == ',') field++;
                else { record++; field = 1; pendingRecord = false; }
                continue;
            }
            if (character == '"')
            {
                if (state == FieldState.Start) state = FieldState.Quoted;
                else if (state == FieldState.AfterQuote) { Append(value, '"', context.Limits.MaxSegmentCharacters); state = FieldState.Quoted; }
                else throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的未加引号字段中出现双引号。");
                continue;
            }
            if (state == FieldState.AfterQuote)
                throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的结束引号后须为逗号或换行。");
            Append(value, character, context.Limits.MaxSegmentCharacters);
            state = FieldState.Unquoted;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (state == FieldState.Quoted)
            throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的引号字段未闭合。");
        if (pendingRecord)
            yield return new TextSegment($"csv:{record}:{field}", value.ToString(),
                new SourceLocator(SourceKind.CsvField, Line: fieldStartLine, Record: record, Field: field));
    }

    private static void Append(StringBuilder value, char character, int limit)
    {
        // Includes unescaped quotes and normalized LF in quoted multiline fields.
        if (value.Length >= limit)
            throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
        value.Append(character);
    }

    private enum FieldState { Start, Unquoted, Quoted, AfterQuote }

    private sealed class CsvCharReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[8192];
        private int _position, _count, _pending = -1;
        public long Line { get; private set; } = 1;

        public async ValueTask<int> ReadAsync(CancellationToken token)
        {
            var current = await ReadRawAsync(token).ConfigureAwait(false);
            if (current == '\r')
            {
                var next = await ReadRawAsync(token).ConfigureAwait(false);
                if (next != '\n') _pending = next;
                current = '\n';
            }
            if (current == '\n') Line++;
            return current;
        }

        private async ValueTask<int> ReadRawAsync(CancellationToken token)
        {
            if (_pending >= 0) { var next = _pending; _pending = -1; return next; }
            if (_position >= _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                _position = 0;
                if (_count == 0) return -1;
            }
            return _buffer[_position++];
        }
    }
}
