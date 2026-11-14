using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationSendingOptionsDialog : ApplicationWindowForm
{
    private readonly record struct Method(string Channel, string Provider, string Title);

    private sealed class MethodRow(Method method, Label status, ModernButton setup, ModernButton toggle)
    {
        internal Method Method { get; } = method;
        internal Label Status { get; } = status;
        internal ModernButton Setup { get; } = setup;
        internal ModernButton Toggle { get; } = toggle;
        internal bool Active { get; set; }
    }

    private readonly List<MethodRow> methods = new();
    private readonly ModernButton close = new() { Text = "Close" };
    private readonly ModernButton block = new() { Text = "Block a Number…", Enabled = false };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Func<Task<DataTable>> read;
    private readonly TableLayoutPanel layout;
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationSendingOptionsDialog(Func<Task<DataTable>>? read = null)
    {
        this.read = read ?? CommunicationManagementStore.ReadSettingsAsync;
        Text = "Configure Sending";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(AppScale.Scale(660), AppScale.Scale(375));
        BackColor = AppTheme.PageBackground;
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var method in new[]
        {
            new Method("WhatsApp", "WhatsAppBusinessManual", "WhatsApp Business · manual"),
            new Method("WhatsApp", "MetaCloud", "WhatsApp · Meta API"),
            new Method("Sms", "Twilio", "SMS · Twilio")
        })
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            layout.Controls.Add(BuildMethod(method), 0, methods.Count - 1);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        layout.Controls.Add(feedback, 0, 3);
        var footer = new Panel { Dock = DockStyle.Fill };
        block.Dock = DockStyle.Left;
        block.Width = AppScale.Scale(175);
        close.Dock = DockStyle.Right;
        close.Width = AppScale.Scale(100);
        footer.Controls.AddRange(new Control[] { block, close });
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
        close.DialogResult = DialogResult.Cancel;
        CancelButton = close;
        FormClosing += (_, e) => e.Cancel |= busy;
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        block.Click += async (_, _) => await RunAsync(RecordOptOutAsync);
    }

    private Control BuildMethod(Method method)
    {
        var status = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Loading…",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var setup = new ModernButton
        {
            Name = method.Provider + "Setup", Text = "Set Up…", Dock = DockStyle.Top,
            Height = AppMetrics.ButtonHeight, Enabled = false
        };
        var toggle = new ModernButton
        {
            Name = method.Provider + "Toggle", Dock = DockStyle.Top, Height = AppMetrics.ButtonHeight,
            Enabled = false, ThemeRole = "Secondary"
        };
        bool manual = method.Provider == "WhatsAppBusinessManual";
        setup.Visible = !manual;
        var row = new MethodRow(method, status, setup, toggle);
        methods.Add(row);
        var title = HumanUi.GroupHeading(method.Title);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2,
            BackColor = AppTheme.Surface, Padding = new Padding(AppScale.Scale(8), 0, AppScale.Scale(8), 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(100)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(150)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, HumanUi.GroupHeadingHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 3);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(setup, 1, 1);
        layout.Controls.Add(toggle, 2, 1);
        setup.Click += async (_, _) => await RunAsync(() => ConfigureAsync(method));
        toggle.Click += async (_, _) => await RunAsync(() => ToggleAsync(row));
        return layout;
    }

    private async Task RefreshAsync()
    {
        using var channels = await read();
        foreach (var row in methods)
        {
            Method method = row.Method;
            DataRow? channel = channels.AsEnumerable().FirstOrDefault(r => r.Field<string>("Channel") == method.Channel);
            bool enabled = channel?.Field<bool>("Enabled") == true;
            bool selected = channel?.Field<string>("ProviderCode") == method.Provider;
            bool manual = method.Provider == "WhatsAppBusinessManual";
            bool hasProfile = !manual && HasProfile(method);
            row.Active = enabled && selected;
            row.Status.Text = row.Active
                ? manual ? "Ready for manual chats" : hasProfile ? "Enabled" : "Enabled · set up this computer"
                : enabled ? "Another method is active for this channel"
                : !manual && (!selected || !hasProfile) ? "Setup required" : selected ? "Paused" : "Available";
            row.Status.ForeColor = row.Active ? AppTheme.Success : AppTheme.MutedText;
            row.Setup.Enabled = channel is not null;
            row.Toggle.Text = row.Active ? "Pause" : manual ? "Enable" : "Verify & Enable";
            row.Toggle.Enabled = row.Active || (!enabled && channel is not null && (manual || (selected && hasProfile)));
        }
        block.Enabled = true;
    }

    private static bool HasProfile(Method method)
    {
        try { return CommunicationProviderCredentialStore.ReadMetadata(method.Channel).ProviderCode == method.Provider; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { return false; }
    }

    private async Task ConfigureAsync(Method method)
    {
        bool switching = methods.Any(row => row.Active && row.Method.Channel == method.Channel && row.Method != method);
        if (switching && ModernMessageDialog.Show(this,
                "Setting up this provider will pause the currently active method for this channel. Continue?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes) return;
        using var dialog = new CommunicationProviderDialog(method.Channel);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Changed = true; // The channel can be paused even if saving the profile fails.
        try { await CommunicationManagementStore.ConfigureProviderAsync(dialog.Profile); }
        finally { await RefreshAsync(); }
    }

    private async Task ToggleAsync(MethodRow row)
    {
        Method method = row.Method;
        bool manual = method.Provider == "WhatsAppBusinessManual";
        string question = row.Active ? $"Pause {method.Title}?"
            : manual ? "Enable manual WhatsApp chats? The operator presses Send in WhatsApp."
            : $"Verify and enable {method.Title}? Provider charges may apply when you send.";
        if (ModernMessageDialog.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (row.Active) await CommunicationManagementStore.PauseAsync(method.Channel);
        else if (manual) await CommunicationManagementStore.EnableManualWhatsAppAsync();
        else await CommunicationManagementStore.VerifyAndEnableAsync(method.Channel);
        Changed = true;
        await RefreshAsync();
    }

    private async Task RecordOptOutAsync()
    {
        using var dialog = new ApplicationWindowForm
        {
            Text = "Block a Number", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(460), AppScale.Scale(205)),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            Padding = new Padding(AppScale.Scale(14)), ShowInTaskbar = false
        };
        var channel = HumanUi.Combo();
        channel.Items.AddRange(new object[] { "WhatsApp", "SMS" });
        channel.SelectedIndex = 0;
        var phone = HumanUi.TextBox(false, 100);
        var save = new ModernButton { Text = "Block Number", ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        string? number = null;
        save.Click += (_, _) =>
        {
            number = CommunicationRecipientResolver.NormalizeMobile(phone.Text);
            if (number is null) ModernMessageDialog.ShowWarning(dialog, "Enter a valid Pakistan mobile number.");
            else dialog.DialogResult = DialogResult.OK;
        };
        var actions = HumanUi.DialogButtons(save, cancel);
        actions.Dock = DockStyle.Bottom;
        var methodField = HumanUi.Field("Channel", channel);
        var phoneField = HumanUi.Field("Mobile number", phone, true);
        foreach (var field in new[] { methodField, phoneField })
        {
            field.Dock = DockStyle.Top;
            field.Height = AppMetrics.FieldRowHeight;
        }
        dialog.Controls.AddRange(new Control[] { phoneField, methodField, actions });
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || number is null) return;
        await CommunicationManagementStore.RecordChannelOptOutAsync(channel.SelectedIndex == 0 ? "WhatsApp" : "Sms", number);
        Changed = true;
        feedback.Text = "Number blocked for " + channel.Text + ".";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
        busy = true;
        layout.Enabled = false;
        feedback.Text = "";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = DbMessage.UserMessage(ex); }
        finally
        {
            busy = false;
            if (!IsDisposed) layout.Enabled = true;
        }
    }
}
