using System.IO;
using System.Text;
using System.Windows;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Win32;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Reviews a bulk action before it commits: the server-resolved count, eligible and skipped rows
/// and the effect. Runs eligible rows in bounded batches with progress, stops between batches,
/// and reports changed, skipped, conflicted and unprocessed rows with a downloadable list.
/// </summary>
// The window disposes its lifetime token source when it closes.
#pragma warning disable CA1001
public partial class BulkActionWindow : Window
#pragma warning restore CA1001
{
    private static readonly string[] CsvHeadings = ["Bulk_Csv_Id", "Bulk_Csv_Row", "Bulk_Csv_Outcome", "Bulk_Csv_Reason"];
    private readonly BulkAction _action;
    private readonly string _rootOperationId = OperationGuard.NewId();
    private readonly Dictionary<long, (BulkItemOutcome Outcome, string? Reason)> _results = [];
    private readonly HashSet<int> _committedBatches = [];
    private readonly CancellationTokenSource _lifetime = new();
    private BulkPlan? _plan;
    private long[][] _batches = [];
    private bool _running;
    private bool _stopRequested;

    internal BulkActionWindow(BulkAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
        InitializeComponent();
        Title = action.Title;
        TitleText.Text = action.Title;
        SelectionText.Text = action.SelectionSummary;
        RunButton.Content = action.RunLabel;
        PlanText.Text = TranslationSource.Instance["Bulk_Checking"];
        Loaded += async (_, _) => await PlanAsync();
        Closing += (_, args) =>
        {
            if (!_running) return;
            // Never abandon a batch mid-transaction; stop after it instead.
            args.Cancel = true;
            RequestStop();
        };
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    /// <summary>Gets whether any batch committed, so the caller refreshes its table.</summary>
    public bool ChangedAny { get; private set; }

    private async Task PlanAsync()
    {
        try
        {
            _plan = await _action.Plan(_lifetime.Token);
            BulkCandidate[] eligible = _plan.Candidates.Where(static candidate => candidate.Eligible).ToArray();
            _batches = eligible.Select(static candidate => candidate.Id).Chunk(Math.Clamp(_action.BatchSize, 1, BulkBatch.MaxBatchSize)).ToArray();
            PlanText.Text = TranslationSource.Instance.Format("Bulk_PlanSummary", _plan.Candidates.Count, eligible.Length,
                _plan.Candidates.Count - eligible.Length);
            string[] reasons = _plan.Candidates.Where(static candidate => !candidate.Eligible)
                .GroupBy(static candidate => candidate.SkipReasonKey ?? "Bulk_NotEligible")
                .Select(static group => $"{TranslationSource.Instance[group.Key]} ({group.Count()})").ToArray();
            SkipText.Text = reasons.Length == 0 ? string.Empty
                : TranslationSource.Instance.Format("Bulk_SkipReasons", string.Join(" · ", reasons));
            EffectText.Text = eligible.Length == 0 ? TranslationSource.Instance["Bulk_NoneEligible"] : _plan.Effect ?? string.Empty;
            RunButton.Content = TranslationSource.Instance.Format("Bulk_RunCount", _action.RunLabel, eligible.Length);
            RunButton.IsEnabled = eligible.Length > 0;
            SaveResultButton.IsEnabled = _plan.Candidates.Count > 0;
            if (_action.Destructive && eligible.Length > 0) RunButton.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PlanText.Text = string.Empty;
            ErrorText.Text = UserError.Message(exception);
        }
    }

    private async void Run_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _running) return;
        _running = true;
        _stopRequested = false;
        ErrorText.Text = string.Empty;
        RunButton.IsEnabled = false;
        CloseButton.Content = TranslationSource.Instance["Bulk_Stop"];
        ProgressPanel.Visibility = Visibility.Visible;
        int total = _batches.Sum(static batch => batch.Length);
        try
        {
            for (int index = 0; index < _batches.Length; index++)
            {
                if (_committedBatches.Contains(index)) continue;
                if (_stopRequested)
                {
                    ErrorText.Text = TranslationSource.Instance.Format("Bulk_Stopped", _committedBatches.Count);
                    break;
                }
                ShowProgress(index, total);
                IReadOnlyList<BulkItemResult> results = await _action.Run(_batches[index],
                    BulkBatch.OperationId(_rootOperationId, index), _lifetime.Token);
                foreach (BulkItemResult result in results) _results[result.Id] = (result.Outcome, result.ReasonKey);
                _committedBatches.Add(index);
                ChangedAny = true;
            }
            ShowProgress(_committedBatches.Count, total);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            ErrorText.Text = TranslationSource.Instance.Format("Bulk_Failed", _committedBatches.Count + 1,
                UserError.Message(exception));
        }
        finally
        {
            _running = false;
        }
        bool remaining = _committedBatches.Count < _batches.Length;
        RunButton.Content = TranslationSource.Instance["Bulk_Resume"];
        RunButton.IsEnabled = remaining;
        RunButton.Visibility = remaining ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = TranslationSource.Instance["Common_Close"];
        ShowResult();
    }

    private void ShowProgress(int batchIndex, int total)
    {
        int done = _batches.Where((_, index) => _committedBatches.Contains(index)).Sum(static batch => batch.Length);
        Progress.Value = total == 0 ? 1 : (double)done / total;
        ProgressText.Text = TranslationSource.Instance.Format("Bulk_ProgressText",
            Math.Min(batchIndex + 1, _batches.Length), _batches.Length, done, total);
    }

    private void ShowResult()
    {
        if (_plan is null) return;
        (int changed, int skipped, int conflicted, int pending) = Count();
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = TranslationSource.Instance.Format("Bulk_ResultSummary", changed, skipped, conflicted, pending);
        OperationText.Text = TranslationSource.Instance.Format("Bulk_Operation", _rootOperationId);
        SaveResultButton.IsEnabled = true;
    }

    private (int Changed, int Skipped, int Conflicted, int Pending) Count()
    {
        int changed = 0, skipped = 0, conflicted = 0, pending = 0;
        foreach (BulkCandidate candidate in _plan!.Candidates)
        {
            switch (OutcomeOf(candidate).Code)
            {
                case "Bulk_Outcome_Changed" or "Bulk_Outcome_Earlier": changed++; break;
                case "Bulk_Outcome_Skipped": skipped++; break;
                case "Bulk_Outcome_Conflicted": conflicted++; break;
                default: pending++; break;
            }
        }
        return (changed, skipped, conflicted, pending);
    }

    private (string Code, string? Reason) OutcomeOf(BulkCandidate candidate)
    {
        if (!candidate.Eligible) return ("Bulk_Outcome_Skipped", candidate.SkipReasonKey ?? "Bulk_NotEligible");
        if (!_results.TryGetValue(candidate.Id, out (BulkItemOutcome Outcome, string? Reason) result))
            return ("Bulk_Outcome_NotProcessed", null);
        return result.Outcome switch
        {
            BulkItemOutcome.Changed => ("Bulk_Outcome_Changed", null),
            BulkItemOutcome.AlreadyDone => ("Bulk_Outcome_Earlier", null),
            BulkItemOutcome.Missing => ("Bulk_Outcome_Conflicted", "Bulk_Missing"),
            _ => ("Bulk_Outcome_Conflicted", result.Reason ?? "Bulk_ChangedSincePreview"),
        };
    }

    private void RequestStop()
    {
        _stopRequested = true;
        CloseButton.IsEnabled = false;
        ProgressText.Text = TranslationSource.Instance["Bulk_Stopping"];
    }

    private void Close_OnClick(object sender, RoutedEventArgs e)
    {
        if (_running) RequestStop();
        else Close();
    }

    private void SaveResult_OnClick(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var picker = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "bulk-result.csv" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var csv = new StringBuilder();
            csv.AppendLine(CsvFormat.Text(_action.Title));
            csv.AppendLine(CsvFormat.Text(_action.SelectionSummary));
            csv.AppendLine(CsvFormat.Text(TranslationSource.Instance.Format("Bulk_Operation", _rootOperationId)));
            csv.AppendLine(string.Join(',', CsvHeadings.Select(static key => CsvFormat.Text(TranslationSource.Instance[key]))));
            foreach (BulkCandidate candidate in _plan.Candidates)
            {
                (string code, string? reason) = OutcomeOf(candidate);
                csv.Append(candidate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(CsvFormat.Text(candidate.Label)).Append(',')
                    .Append(CsvFormat.Text(TranslationSource.Instance[code])).Append(',')
                    .Append(CsvFormat.Text(reason is null ? string.Empty : TranslationSource.Instance[reason])).AppendLine();
            }
            File.WriteAllText(picker.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception exception) { ErrorText.Text = UserError.Message(exception); }
    }
}
