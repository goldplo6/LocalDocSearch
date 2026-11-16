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
        while (true)
        {
            if ((state is FieldState.Start or FieldState.Unquoted or FieldState.Quoted) &&
                input.TryAppendOrdinary(value, context.Limits.MaxSegmentCharacters,
                    state == FieldState.Quoted, cancellationToken))
            {
                pendingRecord = true;
                if (state == FieldState.Start) state = FieldState.Unquoted;
                continue;
            }
            if (!input.TryRead(out var current))
            {
                await input.RefillAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (current < 0) break;
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
        private bool _pendingCr, _endOfRead;
        public long Line { get; private set; } = 1;

        public bool TryAppendOrdinary(StringBuilder value, int limit, bool quoted, CancellationToken token)
        {
            // A raw lookahead belongs to TryRead, especially when CR crosses a refill.
            if (_pendingCr || _pending >= 0 || _position >= _count) return false;
            var start = _position;
            var available = _buffer.AsSpan(start, _count - start);
            // Commas are literal inside quotes; quotes and line breaks retain the
            // original scalar transitions, raw lookahead, and physical line count.
            var separator = quoted ? available.IndexOfAny('"', '\r', '\n') : available.IndexOfAny(",\"\r\n");
            var length = separator < 0 ? _count - start : separator;
            if (length == 0) return false;

            var remaining = limit - value.Length;
            for (var offset = 0; offset < length; offset++)
            {
                // The old loop consumes one raw character before cancellation, then
                // checks its budget. Keep both checks per character, including the
                // first over-budget character, rather than admitting a whole block.
                _position++;
                token.ThrowIfCancellationRequested();
                if (offset >= remaining)
                    throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
            }
            value.Append(_buffer.AsSpan(start, length));
            return true;
        }

        // False means that the caller must refill, not EOF; EOF is the raw value -1.
        public bool TryRead(out int current)
        {
            if (_pendingCr) current = '\r';
            else if (!TryReadRaw(out current)) return false;
            if (current == '\r')
            {
                // Complete the original CR lookahead before returning LF. In particular,
                // a refill/decoder failure here must precede the next field's yield.
                if (!TryReadRaw(out var next))
                {
                    _pendingCr = true;
                    return false;
                }
                _pendingCr = false;
                if (next != '\n') _pending = next;
                current = '\n';
            }
            if (current == '\n') Line++;
            return true;
        }

        public async ValueTask RefillAsync(CancellationToken token)
        {
            _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
            _position = 0;
            _endOfRead = _count == 0;
        }

        private bool TryReadRaw(out int current)
        {
            if (_pending >= 0)
            {
                current = _pending;
                _pending = -1;
                return true;
            }
            if (_position < _count)
            {
                current = _buffer[_position++];
                return true;
            }
            current = -1;
            if (!_endOfRead) return false;
            // Consume one EOF observation, just as one old raw read did. A terminal
            // CR still permits the subsequent read rather than caching EOF forever.
            _endOfRead = false;
            return true;
        }
    }
}
