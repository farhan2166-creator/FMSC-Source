using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>A school-wide default, never an automatic translation setting.</summary>
internal sealed class CommunicationLanguageSettingsDialog : ApplicationWindowForm
{
    private readonly ModernComboBox language = HumanUi.Combo();
    private readonly ModernButton save = new()
    {
        Name = "communicationSaveDefaultLanguage",
        Text = "Save Default", ThemeRole = "Primary"
    };
    private readonly Label status = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = AppTheme.MutedText
    };
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationLanguageSettingsDialog()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Administrator communication permission is required.");
        Text = "Default Message Language";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(475), AppScale.Scale(235));
        BackColor = AppTheme.PageBackground;
        language.Name = "communicationDefaultMessageLanguage";
        language.Items.AddRange(new object[] { "English", "اردو / Urdu" });
        language.SelectedIndex = 0;
        var note = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = false,
            Text = "New messages start in this language. The sender can choose another language. Saved Urdu and English templates remain separate; no message is translated automatically.",
            ForeColor = AppTheme.MutedText
        };
        var cancel = new ModernButton
        {
            Text = "Close", ThemeRole = "Secondary",
            DialogResult = DialogResult.Cancel
        };
        CancelButton = cancel;
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(AppScale.Scale(14))
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(65)));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(9)));
        form.Controls.Add(note, 0, 0);
        form.Controls.Add(HumanUi.Field("Default language", language), 0, 1);
        form.Controls.Add(status, 0, 2);
        form.Controls.Add(HumanUi.DialogButtons(save, cancel), 0, 3);
        Controls.Add(form);
        Shown += async (_, _) => await RunAsync(async () =>
        {
            int actor = CurrentUser.UserId;
            using DataTable result = await CommunicationManagementStore.ReadMessageLanguageAsync();
            if (!IsDisposed && actor == CurrentUser.UserId && result.Rows.Count == 1)
                language.SelectedIndex = Convert.ToString(result.Rows[0]["DefaultMessageLanguage"]) == "ur" ? 1 : 0;
        });
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            int actor = CurrentUser.UserId;
            await CommunicationManagementStore.SaveMessageLanguageAsync(language.SelectedIndex == 1 ? "ur" : "en");
            if (IsDisposed || actor != CurrentUser.UserId)
                throw new UnauthorizedAccessException("The active administrator changed.");
            Changed = true;
            status.Text = "Default saved. Existing messages and scheduled batches are unchanged.";
        });
        FormClosing += (_, e) => e.Cancel |= busy;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        save.Enabled = false;
        status.Text = "";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) status.Text = DbMessage.UserMessage(ex); }
        finally { busy = false; if (!IsDisposed) save.Enabled = true; }
    }
}
