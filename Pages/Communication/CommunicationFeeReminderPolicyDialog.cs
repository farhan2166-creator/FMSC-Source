using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Explicit administrator limits for review-only family fee suggestions.
/// Saving these preferences never creates a batch, confirms a message or sends.
/// </summary>
internal sealed class CommunicationFeeReminderPolicyDialog : ApplicationWindowForm
{
    private sealed record FeeEvent(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ModernComboBox eventChoice = HumanUi.Combo();
    private readonly NumericUpDown gapDays = new()
    {
        Minimum = 1, Maximum = 90, Value = 7, TextAlign = HorizontalAlignment.Right
    };
    private readonly NumericUpDown monthlyLimit = new()
    {
        Minimum = 1, Maximum = 12, Value = 2, TextAlign = HorizontalAlignment.Right
    };
    private readonly NumericUpDown dailyFamilyLimit = new()
    {
        Minimum = 1, Maximum = 20, Value = 3, TextAlign = HorizontalAlignment.Right
    };
    private readonly ModernButton save = new() { Text = "Save Reminder Limits", ThemeRole = "Primary" };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private DataTable? rules;
    private DataTable? settings;
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationFeeReminderPolicyDialog()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Only a communication administrator can change reminder limits.");
        Text = "Fee Reminder Limits";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(515), AppScale.Scale(405));
        BackColor = AppTheme.PageBackground;

        eventChoice.Items.AddRange(new object[]
        {
            new FeeEvent("OutstandingFees", "Outstanding fee"),
            new FeeEvent("UpcomingDue", "Upcoming due date"),
            new FeeEvent("OverdueFollowUp", "Overdue follow-up")
        });

        var description = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = AppTheme.MutedText,
            Text = "These limits reduce repeated fee reminders to the same registered family. They apply to Event Suggestions, not manually composed messages. Nothing is sent automatically."
        };
        var close = new ModernButton { Text = "Close", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var actions = HumanUi.DialogButtons(save, close);
        actions.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(AppScale.Scale(16))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 54, 50, 50, 50, 50, 52, 47 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(height)));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(HumanUi.Field("Fee reminder type", eventChoice), 0, 1);
        layout.Controls.Add(HumanUi.Field("Minimum days between family reminders", gapDays), 0, 2);
        layout.Controls.Add(HumanUi.Field("Maximum family fee reminders per month", monthlyLimit), 0, 3);
        layout.Controls.Add(HumanUi.Field("Maximum family fee reminders per day (all fee types)", dailyFamilyLimit), 0, 4);
        layout.Controls.Add(feedback, 0, 5);
        layout.Controls.Add(actions, 0, 6);
        Controls.Add(layout);

        eventChoice.SelectedIndexChanged += (_, _) => ApplySelected();
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        Shown += async (_, _) => await RunAsync(LoadAsync);
        FormClosing += (_, e) => e.Cancel |= busy;
        FormClosed += (_, _) => { rules?.Dispose(); settings?.Dispose(); };
    }

    private async Task LoadAsync()
    {
        var newRules = await CommunicationQueuePolicyStore.ReadAutomationRulesAsync();
        DataTable newSettings;
        try { newSettings = await CommunicationQueuePolicyStore.ReadDispatchControlAsync(); }
        catch { newRules.Dispose(); throw; }
        if (IsDisposed)
        {
            newRules.Dispose();
            newSettings.Dispose();
            return;
        }
        rules?.Dispose();
        settings?.Dispose();
        rules = newRules;
        settings = newSettings;
        if (newSettings.Rows.Count != 1 ||
            !newSettings.Columns.Contains("RoutineFamilyDailyLimit") ||
            !newRules.Columns.Contains("FeeReminderGapDays") ||
            !newRules.Columns.Contains("FeeRemindersPerMonth"))
            throw new InvalidDataException("Fee reminder settings are not supported by this school database.");
        dailyFamilyLimit.Value = Convert.ToDecimal(newSettings.Rows[0]["RoutineFamilyDailyLimit"]);
        if (eventChoice.SelectedIndex < 0) eventChoice.SelectedIndex = 0;
        else ApplySelected();
    }

    private void ApplySelected()
    {
        if (rules is null || eventChoice.SelectedItem is not FeeEvent selected) return;
        DataRow row = rules.AsEnumerable().Single(r =>
            string.Equals(r.Field<string>("EventCode"), selected.Code, StringComparison.Ordinal));
        gapDays.Value = Convert.ToDecimal(row["FeeReminderGapDays"]);
        monthlyLimit.Value = Convert.ToDecimal(row["FeeRemindersPerMonth"]);
    }

    private async Task SaveAsync()
    {
        if (eventChoice.SelectedItem is not FeeEvent selected || rules is null || settings is null)
            throw new InvalidOperationException("Load the fee reminder settings before saving.");
        int actor = CurrentUser.UserId;
        int gap = Decimal.ToInt32(gapDays.Value);
        int month = Decimal.ToInt32(monthlyLimit.Value);
        int day = Decimal.ToInt32(dailyFamilyLimit.Value);
        await CommunicationQueuePolicyStore.SaveFeeReminderPolicyAsync(selected.Code, gap, month, day);
        if (IsDisposed || actor != CurrentUser.UserId)
            throw new UnauthorizedAccessException("The signed-in administrator changed.");
        rules.AsEnumerable().Single(row => row.Field<string>("EventCode") == selected.Code)
            ["FeeReminderGapDays"] = gap;
        rules.AsEnumerable().Single(row => row.Field<string>("EventCode") == selected.Code)
            ["FeeRemindersPerMonth"] = month;
        settings.Rows[0]["RoutineFamilyDailyLimit"] = day;
        Changed = true;
        feedback.Text = "Limits saved. Existing messages remain unchanged; new suggestions obey these limits.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        save.Enabled = false;
        feedback.Text = "";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = DbMessage.UserMessage(ex); }
        finally { busy = false; if (!IsDisposed) save.Enabled = true; }
    }
}
