using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Operations;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Shows a plan before anything changes (Cancel is the default button), and afterwards shows
/// what actually happened to each step.
/// </summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(OperationPlan plan, IReadOnlyList<OperationResult>? results)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        var resultsByOperation = results?.ToDictionary(result => result.Operation);
        OperationsList.ItemsSource = plan.Operations
            .Select(operation => new OperationRowViewModel(
                operation,
                resultsByOperation is not null && resultsByOperation.TryGetValue(operation, out var result) ? result : null))
            .ToList();

        if (results is null)
        {
            Title = $"Review changes — {plan.Title}";
            HeadingText.Text = $"{plan.Title}: review before anything changes";
            SummaryText.Text = plan.BlockedCount == 0
                ? $"{plan.Description} Nothing has been changed yet."
                : $"{plan.Description} {plan.BlockedCount} step{(plan.BlockedCount == 1 ? "" : "s")} will be skipped (shown in red). Nothing has been changed yet.";
            FooterText.Text = "Each step is checked again just before it runs.";
            ApplyButton.Content = $"Apply {plan.RunnableCount} change{(plan.RunnableCount == 1 ? "" : "s")}";
            ApplyButton.IsEnabled = plan.HasRunnable;
        }
        else
        {
            var done = results.Count(result => result.Outcome == OperationOutcome.Done);
            var failed = results.Count(result => result.Outcome == OperationOutcome.Failed);
            var skipped = results.Count - done - failed;
            Title = $"Results — {plan.Title}";
            HeadingText.Text = failed == 0 ? $"{plan.Title}: finished" : $"{plan.Title}: finished with problems";
            SummaryText.Text = $"{done} done, {failed} failed, {skipped} skipped. Each step is also written to the log (Settings → General → Troubleshooting).";
            FooterText.Text = "";
            ApplyButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
