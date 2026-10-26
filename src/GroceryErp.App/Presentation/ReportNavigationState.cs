using GroceryErp.Reports;

namespace GroceryErp.App.Presentation;

public sealed class ReportNavigationState
{
    private ReportRequest? _pending;

    public void Open(ReportKind kind, DateOnly from, DateOnly to, string? status = null,
        string? paymentMode = null, long? productId = null, bool mismatchesOnly = false) =>
        _pending = new(kind, from, to, status, paymentMode, productId, mismatchesOnly);

    public ReportRequest? Consume()
    {
        ReportRequest? request = _pending;
        _pending = null;
        return request;
    }
}

public sealed record ReportRequest(ReportKind Kind, DateOnly From, DateOnly To,
    string? Status, string? PaymentMode, long? ProductId, bool MismatchesOnly = false);
