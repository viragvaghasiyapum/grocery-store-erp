namespace GroceryErp.App.Presentation;

/// <summary>
/// Cancels an obsolete page query when a newer one starts and tells a finishing query whether
/// it is still the newest, so a stale response never replaces newer rows. UI thread only.
/// </summary>
internal sealed class LatestRequest
{
    private Action? _cancelCurrent;
    private int _version;

    public Scope Begin()
    {
        _cancelCurrent?.Invoke();
        var scope = new Scope(this, ++_version);
        _cancelCurrent = scope.CancelIfLive;
        return scope;
    }

    public void Cancel()
    {
        _cancelCurrent?.Invoke();
        _version++;
    }

    internal sealed class Scope : IDisposable
    {
        private readonly LatestRequest _owner;
        private readonly int _version;
        private readonly CancellationTokenSource _source = new();
        private bool _disposed;

        internal Scope(LatestRequest owner, int version)
        {
            _owner = owner;
            _version = version;
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public bool IsCurrent => _owner._version == _version && !Token.IsCancellationRequested;

        internal void CancelIfLive()
        {
            if (!_disposed) _source.Cancel();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}
