using System.Text;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

internal sealed class RollupOutputAggregator
{
    // Avoid unbounded memory usage when output contains extremely long lines without terminators.
    private const int MaxBufferedChars = 64_000;

    private readonly StringBuilder _buffer = new();
    private bool _pendingCr;

    public bool HasBufferedContent => _buffer.Length > 0;

    public void AppendDelta(
        DateTimeOffset createdAt,
        string delta,
        Action<RunRollupRecord> emit)
    {
        if (delta.Length == 0)
        {
            return;
        }

        if (!ContainsNewline(delta))
        {
            var trimmed = delta.Trim();
            if (IsControlMarker(trimmed))
            {
                if (_buffer.Length > 0)
                {
                    EmitFromBuffer(createdAt, endsWithNewline: false, emit);
                }

                emit(new RunRollupRecord
                {
                    Type = "outputLine",
                    CreatedAt = createdAt,
                    Source = "commandExecution",
                    Text = trimmed,
                    EndsWithNewline = false,
                    IsControl = true
                });

                return;
            }
        }

        var startIndex = 0;
        if (_pendingCr)
        {
            if (delta[0] == '\n')
            {
                startIndex = 1;
            }

            _pendingCr = false;
        }

        for (var i = startIndex; i < delta.Length; i++)
        {
            var ch = delta[i];

            if (ch == '\r')
            {
                if (i + 1 < delta.Length && delta[i + 1] == '\n')
                {
                    i++;
                }
                else if (i == delta.Length - 1)
                {
                    _pendingCr = true;
                }

                EmitFromBuffer(createdAt, endsWithNewline: true, emit);
                continue;
            }

            if (ch == '\n')
            {
                EmitFromBuffer(createdAt, endsWithNewline: true, emit);
                continue;
            }

            _buffer.Append(ch);
            if (_buffer.Length >= MaxBufferedChars)
            {
                EmitFromBuffer(createdAt, endsWithNewline: false, emit);
            }
        }
    }

    public void Flush(
        DateTimeOffset createdAt,
        Action<RunRollupRecord> emit)
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var text = _buffer.ToString().TrimEnd('\r');
        if (text.Length == 0)
        {
            _buffer.Clear();
            return;
        }

        var trimmed = text.Trim();
        var isControl = IsControlMarker(trimmed);

        emit(new RunRollupRecord
        {
            Type = "outputLine",
            CreatedAt = createdAt,
            Source = "commandExecution",
            Text = isControl ? trimmed : text,
            EndsWithNewline = false,
            IsControl = isControl
        });

        _buffer.Clear();
    }

    private void EmitFromBuffer(
        DateTimeOffset createdAt,
        bool endsWithNewline,
        Action<RunRollupRecord> emit)
    {
        var text = _buffer.ToString();
        var trimmed = text.Trim();
        var isControl = IsControlMarker(trimmed);

        emit(new RunRollupRecord
        {
            Type = "outputLine",
            CreatedAt = createdAt,
            Source = "commandExecution",
            Text = isControl ? trimmed : text,
            EndsWithNewline = endsWithNewline,
            IsControl = isControl
        });

        _buffer.Clear();
    }

    private static bool ContainsNewline(string value) =>
        value.Contains('\n') || value.Contains('\r');

    private static bool IsControlMarker(string value) =>
        string.Equals(value, "thinking", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "final", StringComparison.OrdinalIgnoreCase);
}

