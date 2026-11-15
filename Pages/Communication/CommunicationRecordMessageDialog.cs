using FMSC_SMS.Core;
using static FMSC_SMS.Core.CommunicationPreparedMessages;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationRecordMessageDialog : ApplicationWindowForm
{
    internal sealed record Method(string Label, string Channel, string? Provider = null)
    {
        internal bool IsMeta => Provider == "MetaCloud";
        public override string ToString() => Label;
    }

    internal sealed record Operations(
        Func<Method, Task<bool>> IsReady,
        Func<Task<IReadOnlyList<ComposeTemplate>>> ReadTemplates,
        Func<CommunicationTarget, Task<CommunicationPreview>> ReadRecipients,
        Func<Method, CommunicationTarget, CommunicationPreview, string, Guid, CommunicationMetaTransport.Selection?, Task<CommunicationConfirmation>> Confirm,
        Action<Method, CommunicationConfirmation> Dispatch);

    private sealed record ReviewedMessage(CommunicationTarget Target, CommunicationPreview Preview, string Body, Guid RequestId);
    private sealed record Recipient(string Label, string Text)
    {
        public override string ToString() => Label;
    }

    private static readonly Method[] Methods =
    {
        new("WhatsApp Business · manual", "WhatsApp"), new("WhatsApp · Meta API", "WhatsApp", "MetaCloud"),
        new("SMS · Twilio", "Sms", "Twilio")
    };

    private readonly int actor = CurrentUser.UserId;
    private readonly CommunicationTarget studentTarget;
    private readonly bool fees;
    private readonly Operations operations;
    private readonly ModernComboBox? scope;
    private readonly ModernComboBox method = HumanUi.Combo(), templates = HumanUi.Combo(), recipients = HumanUi.Combo();
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600), preview = HumanUi.TextBox(true, 1600);
    private readonly Label destination = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = AppTheme.ButtonFont, UseMnemonic = false };
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = AppTheme.Danger, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ModernButton chooseMeta = new() { Text = "Choose Approved Template…", Dock = DockStyle.Top };
    private readonly ModernButton action = new() { Text = "Open in WhatsApp", ThemeRole = "Primary", Enabled = false };
    private readonly ModernButton back = new() { Text = "Back" };
    private readonly ModernButton cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Control options, templateField, messageField, recipientField, previewField;
    private IReadOnlyList<ComposeTemplate> choices = BuiltInChoices;
    private CommunicationMetaTransport.Selection? metaSelection;
    private ReviewedMessage? review;
    private bool busy;
    private Method? SelectedMethod => method.SelectedItem as Method;
    private CommunicationTarget Target => studentTarget with
    {
        Audience = scope?.SelectedIndex == 1 ? CommunicationAudience.Family : studentTarget.Audience,
        Channel = SelectedMethod?.Channel ?? studentTarget.Channel
    };

    internal CommunicationRecordMessageDialog(int admissionId, string display, bool fees, Operations? operations = null)
    {
        this.fees = fees;
        studentTarget = CommunicationRecordEntry.Target(admissionId, fees);
        this.operations = operations ?? new Operations(
            option => option.Provider is { } provider
                ? CommunicationApiBatchStore.IsReadyAsync(option.Channel, provider)
                : CommunicationManualHandoffStore.IsEnabledAsync(),
            ReadComposeTemplatesAsync, CommunicationRecipientResolver.ReadAsync,
            (option, target, review, body, request, meta) => option.Provider is { } provider
                ? CommunicationApiBatchStore.ConfirmAsync(target, review, body, request, provider, meta)
                : CommunicationBatchStore.ConfirmPreviewAsync(target, review, body, request, manualWhatsApp: true),
            (option, batch) =>
            {
                using Form dispatch = option.Provider is null
                    ? new CommunicationManualHandoffDialog(batch.BatchId, openFirstChat: !batch.AlreadyConfirmed)
                    : new CommunicationApiDispatchDialog(batch.BatchId, startConfirmed: !batch.AlreadyConfirmed);
                dispatch.ShowDialog(this);
            });

        Text = fees ? "Fee Message" : "School Message";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(AppScale.Scale(630), AppScale.Scale(420));
        MinimumSize = new Size(AppScale.Scale(600), AppScale.Scale(405));
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        BackColor = AppTheme.PageBackground;
        CancelButton = cancel;

        var delivery = HumanUi.Field("Send using", method);
        if (fees) options = delivery;
        else
        {
            scope = HumanUi.Combo();
            scope.Name = "communicationRecordScope";
            scope.Items.AddRange(new object[] { "This student", "Linked family / siblings" });
            scope.SelectedIndex = 0;
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            fields.Controls.Add(HumanUi.Field("Send to", scope), 0, 0);
            fields.Controls.Add(delivery, 1, 0);
            options = fields;
        }
        templateField = HumanUi.Field("Template (optional)", templates);
        messageField = HumanUi.Field("Message", message);
        recipientField = HumanUi.Field("Recipient preview", recipients);
        previewField = HumanUi.Field("Message preview", preview);
        preview.ReadOnly = true;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(31)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(47)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        destination.Text = "To: " + display;
        layout.Controls.Add(destination, 0, 0);
        layout.Controls.Add(SwitchableRow(options, summary), 0, 1);
        layout.Controls.Add(SwitchableRow(templateField, chooseMeta, recipientField), 0, 2);
        layout.Controls.Add(SwitchableRow(messageField, previewField), 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(HumanUi.DialogButtons(back, action, cancel), 0, 5);
        Controls.Add(layout);

        if (scope is not null) scope.SelectedIndexChanged += (_, _) =>
        {
            destination.Text = (Target.Audience == CommunicationAudience.Family ? "Family of " : "To: ") + display;
            ClearDraft();
            RefreshTemplates();
        };
        method.SelectedIndexChanged += (_, _) =>
        {
            if (metaSelection is not null || SelectedMethod?.IsMeta == true) ClearDraft();
            message.ReadOnly = SelectedMethod?.IsMeta == true;
            RefreshTemplates();
            UpdateView();
        };
        templates.SelectedIndexChanged += (_, _) =>
        {
            if (templates.SelectedItem is ComposeTemplate selected) message.Text = selected.Body;
        };
        recipients.SelectedIndexChanged += (_, _) => preview.Text = (recipients.SelectedItem as Recipient)?.Text ?? "";
        chooseMeta.Click += async (_, _) => await RunAsync(ChooseMetaAsync);
        action.Click += async (_, _) => await RunAsync(review is null ? ReviewAsync : ConfirmAsync);
        back.Click += (_, _) => { review = null; status.Text = ""; UpdateView(); };
        Shown += async (_, _) => await RunAsync(LoadOptionsAsync);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; DialogResult = DialogResult.None; } };
        UpdateView();
    }

    private static Panel SwitchableRow(params Control[] controls)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private void EnsureAccess()
    {
        if (actor != CurrentUser.UserId || !CommunicationAccess.CanMessageStudent(CurrentUser.HasPermission, fees))
            throw new CommunicationValidationException("School-message access changed. Close this dialog and try again.");
    }

    private async Task LoadOptionsAsync()
    {
        bool[] ready = await Task.WhenAll(Methods.Select(operations.IsReady));
        EnsureAccess();
        method.Items.AddRange(Methods.Where((_, index) => ready[index]).Cast<object>().ToArray());
        if (method.Items.Count == 0)
        {
            status.Text = "No sending methods are ready on this computer. Check Configure Sending in Communication Center.";
            return;
        }
        method.SelectedIndex = 0;
        if (fees && templates.Items.Count > 1) templates.SelectedIndex = 1;
        choices = await operations.ReadTemplates();
        EnsureAccess();
        RefreshTemplates();
        if (fees && templates.Items.Count > 1) templates.SelectedIndex = 1;
    }

    private void ClearDraft()
    {
        metaSelection = null;
        message.Clear();
        status.Text = "";
    }

    private void RefreshTemplates()
    {
        templates.Items.Clear();
        templates.Items.Add("Write your own message");
        if (SelectedMethod?.IsMeta != true)
        {
            var target = Target;
            // A shared guardian contact should receive a family notice that reads correctly for every sibling.
            templates.Items.AddRange(choices.Where(item => item.Matches(target)
                && (target.Audience != CommunicationAudience.Family || !HasFields(item.Body))).Cast<object>().ToArray());
        }
        templates.SelectedIndex = 0;
    }

    private async Task ChooseMetaAsync()
    {
        var approved = await CommunicationApiBatchStore.ReadMetaTemplatesAsync();
        EnsureAccess();
        using var dialog = new CommunicationMetaTemplateDialog(approved, Target.Purpose,
            allowStudentFields: Target.Audience != CommunicationAudience.Family);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selection is null) return;
        metaSelection = dialog.Selection;
        message.Text = metaSelection.Format();
    }

    private async Task ReviewAsync()
    {
        if (SelectedMethod is not { } option) return;
        var target = Target;
        string body = option.IsMeta ? message.Text : message.Text.Trim();
        if (option.IsMeta && (metaSelection is null || metaSelection.Format() != body))
            throw new CommunicationValidationException("Choose an approved Meta template before reviewing.");
        ValidateFormat(body, target.Purpose);
        if (target.Audience == CommunicationAudience.Family && HasFields(body))
            throw new CommunicationValidationException("Use shared wording for a family message. Student fields require one student.");
        var result = await operations.ReadRecipients(target);
        EnsureAccess();
        var rows = result.Entries.OrderByDescending(entry => entry.Included).Select(entry => new Recipient(
            entry.Included && entry.PhoneE164 is { Length: >= 4 } phone ? $"{entry.RecordName} · {phone[^4..]}" : entry.RecordName,
            entry.Included ? Render(body, FromPreview(entry)) : entry.Reason)).ToArray();
        recipients.Items.Clear();
        recipients.Items.AddRange(rows);
        if (rows.Length > 0) recipients.SelectedIndex = 0;
        string eligible = result.Included == 1 ? "1 eligible contact" : $"{result.Included} eligible contacts";
        string excluded = result.Excluded == 0 ? "" : $" · {result.Excluded} excluded";
        summary.Text = $"{option.Label} · {eligible}{excluded}";
        status.ForeColor = result.Included == 0 ? AppTheme.Danger : AppTheme.MutedText;
        status.Text = result.Included == 0
            ? fees && rows.Length == 0 ? "No current outstanding fees for this student." : "No eligible contacts for this message."
            : option.Provider is null ? "Press Send in WhatsApp for each reviewed chat." : "Confirm to send to the reviewed contacts. Provider charges may apply.";
        review = new(target, result, body, Guid.NewGuid());
    }

    private async Task ConfirmAsync()
    {
        if (review is not { Preview.Included: > 0 } reviewed || SelectedMethod is not { } option) return;
        var batch = await operations.Confirm(option, reviewed.Target, reviewed.Preview, reviewed.Body, reviewed.RequestId, metaSelection);
        EnsureAccess();
        operations.Dispatch(option, batch);
        busy = false;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateView()
    {
        bool reviewing = review is not null;
        bool useMeta = SelectedMethod?.IsMeta == true;
        options.Visible = messageField.Visible = !reviewing;
        chooseMeta.Visible = !reviewing && useMeta;
        templateField.Visible = !reviewing && !useMeta;
        summary.Visible = previewField.Visible = reviewing;
        recipientField.Visible = reviewing && recipients.Items.Count > 0;
        back.Visible = reviewing;
        action.Text = !reviewing ? "Review Message" : SelectedMethod?.Provider is not null ? "Confirm & Send"
            : review!.Preview.Included == 1 ? "Open in WhatsApp" : "Start WhatsApp Chats";
        action.Enabled = !busy && (reviewing ? review!.Preview.Included > 0 : SelectedMethod is not null);
        options.Enabled = templateField.Enabled = chooseMeta.Enabled = messageField.Enabled = !busy;
        back.Enabled = cancel.Enabled = !busy;
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        status.Text = "";
        status.ForeColor = AppTheme.Danger;
        UpdateView();
        UseWaitCursor = true;
        try { EnsureAccess(); await work(); }
        catch (Exception ex) { if (!IsDisposed) status.Text = DbMessage.UserMessage(ex); }
        finally
        {
            busy = false;
            if (!IsDisposed) { UseWaitCursor = false; UpdateView(); }
        }
    }
}
