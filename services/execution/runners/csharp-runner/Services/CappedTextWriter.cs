using System.Text;

namespace Runner.Services;

internal sealed class CappedTextWriter : TextWriter
{
    private readonly StringBuilder _buffer;
    private readonly int _maxChars;

    public CappedTextWriter(int maxChars)
    {
        _maxChars = Math.Max(0, maxChars);
        _buffer = new StringBuilder(Math.Min(_maxChars, 16 * 1024));
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (_buffer.Length < _maxChars)
        {
            _buffer.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }
        var available = _maxChars - _buffer.Length;
        if (available > 0)
        {
            _buffer.Append(buffer, index, Math.Min(count, available));
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        var available = _maxChars - _buffer.Length;
        if (available > 0)
        {
            _buffer.Append(value, 0, Math.Min(value.Length, available));
        }
    }

    public override string ToString() => _buffer.ToString();
}
