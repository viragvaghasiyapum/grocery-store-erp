using GroceryErp.Reports;

namespace GroceryErp.App.Presentation;

public sealed class ReportNavigationState
{
    private ReportRequest? _pending;

    public void Open(ReportKind kind, DateOnly from, DateOnly to) => _pending = new(kind, from, to);

    public ReportRequest? Consume()
    {
        ReportRequest? request = _pending;
        _pending = null;
        return request;
    }
}

public sealed record ReportRequest(ReportKind Kind, DateOnly From, DateOnly To);
