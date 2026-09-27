using System.Text;
using System.Windows;
using System.Windows.Input;

namespace GroceryErp.UI.Common;

public sealed class ScanBurstDetector(int maximumGapMilliseconds, int minimumLength) : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private Window? _window;
    private DateTime _lastInputUtc;

    public event EventHandler<string>? ScanReceived;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null) throw new InvalidOperationException("The detector is already attached.");
        _window = window;
        window.PreviewTextInput += OnTextInput;
        window.PreviewKeyDown += OnKeyDown;
    }

    public void Dispose()
    {
        if (_window is null) return;
        _window.PreviewTextInput -= OnTextInput;
        _window.PreviewKeyDown -= OnKeyDown;
        _window = null;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (_buffer.Length > 0 && (now - _lastInputUtc).TotalMilliseconds > maximumGapMilliseconds)
            _buffer.Clear();
        _lastInputUtc = now;
        _buffer.Append(e.Text);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string value = _buffer.ToString();
        bool burst = value.Length >= minimumLength &&
                     (DateTime.UtcNow - _lastInputUtc).TotalMilliseconds <= maximumGapMilliseconds * 2;
        _buffer.Clear();
        if (!burst) return;
        e.Handled = true;
        ScanReceived?.Invoke(this, value);
    }
}
