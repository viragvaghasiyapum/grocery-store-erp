using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GroceryErp.UI.Common;

public sealed class ScanBurstDetector(int maximumGapMilliseconds, int minimumLength) : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private Window? _window;
    private long _lastInput;
    private TextBox? _target;
    private string? _originalText;
    private int _selectionStart;
    private int _selectionLength;

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
        Reset();
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        long now = Stopwatch.GetTimestamp();
        TextBox? focused = Keyboard.FocusedElement as TextBox;
        if (_buffer.Length > 0 &&
            (Stopwatch.GetElapsedTime(_lastInput, now).TotalMilliseconds > maximumGapMilliseconds ||
             !ReferenceEquals(focused, _target)))
            Reset();
        if (_buffer.Length == 0 && focused is not null)
        {
            _target = focused;
            _originalText = focused.Text;
            _selectionStart = focused.SelectionStart;
            _selectionLength = focused.SelectionLength;
        }
        _lastInput = now;
        _buffer.Append(e.Text);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string value = _buffer.ToString();
        bool burst = value.Length >= minimumLength &&
                     Stopwatch.GetElapsedTime(_lastInput).TotalMilliseconds <= maximumGapMilliseconds * 2;
        TextBox? target = _target;
        string? originalText = _originalText;
        int selectionStart = _selectionStart;
        int selectionLength = _selectionLength;
        Reset();
        if (!burst) return;
        e.Handled = true;
        if (target is not null && originalText is not null)
        {
            target.Text = originalText;
            target.Select(selectionStart, selectionLength);
        }
        ScanReceived?.Invoke(this, value);
    }

    private void Reset()
    {
        _buffer.Clear();
        _target = null;
        _originalText = null;
        _lastInput = 0;
    }
}
