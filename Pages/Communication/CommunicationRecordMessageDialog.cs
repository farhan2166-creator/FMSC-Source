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
        Action<Method, CommunicationConfirmation> Dispatch,
        Func<int, bool, bool, Task<FeeMessageDocument>>? ReadFeeDocument = null,
        Func<FeeMessageDocument, Task<Bitmap>>? RenderImage = null,
        Action<Image>? CopyImage = null,
        CommunicationManualHandoff.Operations? FeeHandoff = null);

    private sealed record ReviewedMessage(CommunicationTarget Target, CommunicationPreview Preview, string Body, Guid RequestId,
        FeeMessageDocument? Document);
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
    private readonly bool useLiveSchoolLanguage;
    private readonly CommunicationTarget studentTarget;
    private readonly bool fees;
    private readonly Operations operations;
    private readonly ModernComboBox scope = HumanUi.Combo();
    private readonly ModernComboBox feeContent = HumanUi.Combo();
    private readonly CheckBox attachImage = new() { Text = "Share as image in WhatsApp", Checked = true, Dock = DockStyle.Fill };
    private readonly ModernComboBox method = HumanUi.Combo(), templates = HumanUi.Combo(), recipients = HumanUi.Combo();
    private readonly ModernComboBox language = HumanUi.Combo();
    private string SelectedLanguage => language.SelectedIndex == 1 ? "ur" : "en";
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600), preview = HumanUi.TextBox(true, 1600);
    private readonly Label destination = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Font = AppTheme.ButtonFont, UseMnemonic = false };
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = AppTheme.Danger, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ModernButton chooseMeta = new() { Text = "Choose Approved Template…", Dock = DockStyle.Top };
    private readonly ModernButton action = new() { Text = "Open in WhatsApp", ThemeRole = "Primary", Enabled = false };
    private readonly ModernButton back = new() { Text = "Back" };
    private readonly ModernButton cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Control options, templateField, messageField, recipientField, previewField;
    private readonly Panel feeOptions = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel composer;
    private IReadOnlyList<ComposeTemplate> choices = BuiltInChoices;
    private CommunicationMetaTransport.Selection? metaSelection;
    private ReviewedMessage? review;
    private CommunicationManualHandoff? handoff;
    private Bitmap? handoffImage;
    private bool busy;
    private bool IsFamily => scope.SelectedIndex == 1;
    private bool IsReceipt => feeContent.SelectedIndex == 1;
    private Method? SelectedMethod => method.SelectedItem as Method;
    private bool ShareImage => fees && attachImage.Checked && SelectedMethod is { Provider: null };
    private CommunicationTarget Target => studentTarget with
    {
        Audience = IsFamily ? fees ? CommunicationAudience.FeeFamily : CommunicationAudience.Family : studentTarget.Audience,
        Channel = SelectedMethod?.Channel ?? studentTarget.Channel
    };

    internal CommunicationRecordMessageDialog(int admissionId, string display, bool fees, Operations? operations = null)
    {
        this.fees = fees;
        useLiveSchoolLanguage = operations is null;
        studentTarget = CommunicationRecordEntry.Target(admissionId, fees);
        this.operations = operations ?? new Operations(
            option => option.Provider is { } provider
                ? CommunicationApiBatchStore.IsReadyAsync(option.Channel, provider)
                : CommunicationManualHandoffStore.IsEnabledAsync(),
            CommunicationManagementStore.ReadComposeTemplatesAsync, CommunicationRecipientResolver.ReadAsync,
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
        ClientSize = new Size(AppScale.Scale(630), AppScale.Scale(fees ? 480 : 420));
        MinimumSize = new Size(AppScale.Scale(600), AppScale.Scale(405));
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        BackColor = AppTheme.PageBackground;
        CancelButton = cancel;

        scope.Name = "communicationRecordScope";
        scope.Items.AddRange(new object[] { "This student", "Linked family / siblings" });
        scope.SelectedIndex = 0;
        language.Name = "communicationRecordLanguage";
        language.Items.AddRange(new object[] { "English", "اردو / Urdu" });
        language.SelectedIndex = 0;
        options = Triple(HumanUi.Field("Send to", scope),
            HumanUi.Field("Send using", method),HumanUi.Field("Language", language));
        feeContent.Items.AddRange(new object[] { "Outstanding balance / bill", "Latest payment / receipt" });
        feeContent.SelectedIndex = 0;
        feeOptions.Controls.Add(Pair(HumanUi.Field("Fee message", feeContent), attachImage));
        templateField = HumanUi.Field("Template (optional)", templates);
        messageField = HumanUi.Field("Message", message);
        recipientField = HumanUi.Field("Recipient preview", recipients);
        previewField = HumanUi.Field("Message preview", preview);
        preview.ReadOnly = true;
        var layout = composer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(31)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        var feeRow = new RowStyle(SizeType.Absolute, fees ? AppMetrics.FieldRowHeight : 0);
        layout.RowStyles.Add(feeRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(47)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        destination.Text = "To: " + display;
        layout.Controls.Add(destination, 0, 0);
        layout.Controls.Add(SwitchableRow(options, summary), 0, 1);
        layout.Controls.Add(feeOptions, 0, 2);
        layout.Controls.Add(SwitchableRow(templateField, chooseMeta, recipientField), 0, 3);
        layout.Controls.Add(SwitchableRow(messageField, previewField), 0, 4);
        layout.Controls.Add(status, 0, 5);
        action.Width = AppMetrics.TextWidth("Copy & Open Chat", action.Font, AppScale.Scale(32));
        layout.Controls.Add(HumanUi.DialogButtons(back, action, cancel), 0, 6);
        content.Controls.Add(layout);
        Controls.Add(content);

        scope.SelectedIndexChanged += (_, _) =>
        {
            destination.Text = (IsFamily ? "Family of " : "To: ") + display;
            ClearDraft();
            RefreshTemplates(selectDefault: fees);
        };
        feeContent.SelectedIndexChanged += (_, _) => { ClearDraft(); RefreshTemplates(selectDefault: true); };
        method.SelectedIndexChanged += (_, _) =>
        {
            if (metaSelection is not null || SelectedMethod?.IsMeta == true) ClearDraft();
            message.ReadOnly = SelectedMethod?.IsMeta == true;
            language.Enabled = SelectedMethod?.IsMeta != true;
            RefreshTemplates(selectDefault: fees && message.Text.Length == 0);
            UpdateView();
        };
        templates.SelectedIndexChanged += (_, _) =>
        {
            if (templates.SelectedItem is ComposeTemplate selected) message.Text = selected.Body;
        };
        language.SelectedIndexChanged += (_, _) =>
        {
            // Never silently translate or replace a draft when the sender
            // changes the language; only a separately selected template does.
            RefreshTemplates();
        };
        recipients.SelectedIndexChanged += (_, _) => preview.Text = (recipients.SelectedItem as Recipient)?.Text ?? "";
        chooseMeta.Click += async (_, _) => await RunAsync(ChooseMetaAsync);
        action.Click += async (_, _) => await RunAsync(review is null ? ReviewAsync : ConfirmAsync);
        back.Click += (_, _) => { review?.Document?.Dispose(); review = null; status.Text = ""; UpdateView(); };
        Load += (_, _) => SetResponsiveContentMinimum(MinimumSize - (Size - ClientSize));
        Shown += async (_, _) => await RunAsync(LoadOptionsAsync);
        FormClosing += (_, e) =>
        {
            if (busy || handoff?.IsBusy == true) { e.Cancel = true; DialogResult = DialogResult.None; }
        };
        Disposed += (_, _) => { review?.Document?.Dispose(); handoffImage?.Dispose(); };
        feeOptions.VisibleChanged += (_, _) => feeRow.Height = fees && feeOptions.Visible ? AppMetrics.FieldRowHeight : 0;
        UpdateView();
    }

    private static Control Triple(Control first, Control second, Control third)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        panel.Controls.Add(first,0,0);
        panel.Controls.Add(second,1,0);
        panel.Controls.Add(third,2,0);
        return panel;
    }

    private static Control Pair(Control left, Control right)
    {
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.Controls.Add(left, 0, 0);
        fields.Controls.Add(right, 1, 0);
        return fields;
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
        choices = await operations.ReadTemplates();
        EnsureAccess();
        if (useLiveSchoolLanguage)
        {
            using var preference = await CommunicationManagementStore.ReadMessageLanguageAsync();
            EnsureAccess();
            if (preference.Rows.Count == 1)
                language.SelectedIndex =
                    Convert.ToString(preference.Rows[0]["DefaultMessageLanguage"]) == "ur" ? 1 : 0;
        }
        RefreshTemplates(selectDefault: fees);
    }

    private void ClearDraft()
    {
        metaSelection = null;
        message.Clear();
        status.Text = "";
    }

    private void RefreshTemplates(bool selectDefault = false)
    {
        templates.Items.Clear();
        templates.Items.Add("Write your own message");
        if (SelectedMethod?.IsMeta != true)
        {
            IEnumerable<ComposeTemplate> options;
            if (fees && IsReceipt)
                options = SelectedLanguage == "ur"
                    ? new[] { new ComposeTemplate("Payment recorded (Urdu)", "Individual",
                        "محترم والدین/سرپرست، اسکول میں PKR [PaymentAmount] کی ادائیگی درج ہو گئی ہے۔ رسید نمبر: [ReceiptNumber]۔ شکریہ۔", LanguageCode: "ur") }
                    : new[] { new ComposeTemplate("Payment recorded", "Individual",
                        "Dear parent/guardian, the school recorded a payment of PKR [PaymentAmount]. Receipt: [ReceiptNumber]. Thank you.") };
            else if (fees && IsFamily)
                options = SelectedLanguage == "ur"
                    ? new[] { new ComposeTemplate("Family outstanding balance (Urdu)", "FeeReminder",
                        "محترم والدین/سرپرست، آپ کے خاندان کی موجودہ بقایا فیس PKR [OutstandingAmount] ہے۔ تصدیق شدہ تفصیل کے لیے اسکول کے اکاؤنٹس دفتر سے رابطہ کریں۔",
                        LanguageCode: "ur") }
                    : new[]
                    {
                        new ComposeTemplate("Family outstanding details", "FeeReminder", CommunicationFeeDetails.Template),
                        new ComposeTemplate("Family outstanding balance", "FeeReminder",
                            "Dear Parent/Guardian,\n\nFamily outstanding balance: PKR [OutstandingAmount]\n\nFor payment queries, please contact the school accounts office.\n\nRegards : FMSC")
                    };
            else
            {
                var target = fees ? Target with { Audience = CommunicationAudience.OutstandingFees } : Target;
                options = choices.Where(item => item.LanguageCode == SelectedLanguage
                    && item.Matches(target) && (!IsFamily || !HasFields(item.Body)));
                if (fees && SelectedLanguage == "en")
                    options = new[] { new ComposeTemplate("Student outstanding details",
                        "FeeReminder", CommunicationFeeDetails.Template) }.Concat(options);
            }
            templates.Items.AddRange(options.Cast<object>().ToArray());
        }
        templates.SelectedIndex = selectDefault && templates.Items.Count > 1 ? 1 : 0;
    }

    private async Task ChooseMetaAsync()
    {
        var approved = await CommunicationApiBatchStore.ReadMetaTemplatesAsync();
        EnsureAccess();
        using var dialog = new CommunicationMetaTemplateDialog(approved, "Individual", allowStudentFields: !IsFamily);
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
        if (body.Length == 0) throw new CommunicationValidationException("Write or choose a message.");
        var result = await operations.ReadRecipients(target);
        EnsureAccess();
        FeeMessageDocument? document = null;
        try
        {
            if (fees && result.Included > 0)
            {
                document = await ReadFeeDocumentAsync();
                EnsureAccess();
                if (!option.IsMeta)
                {
                    // Resolve composer-only fields before saving the exact reviewed batch text.
                    string amount = document.Amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
                    body = IsReceipt ? body.Replace("[PaymentAmount]", amount, StringComparison.Ordinal)
                            .Replace("[ReceiptNumber]", document.ReceiptNumber, StringComparison.Ordinal)
                        : CommunicationFeeDetails.Expand(body, document.OutstandingDetails)
                            .Replace("[OutstandingAmount]", amount, StringComparison.Ordinal);
                }
            }
            DisplayReview(option, target, result, body, document);
            document = null;
        }
        finally { document?.Dispose(); }
    }

    private Task<FeeMessageDocument> ReadFeeDocumentAsync() => (operations.ReadFeeDocument ?? FeeMessageDocument.ReadAsync)
        (studentTarget.StudentId, IsFamily, IsReceipt);

    private void DisplayReview(Method option, CommunicationTarget target, CommunicationPreview result, string body, FeeMessageDocument? document)
    {
        if (!fees || document is not null)
        {
            ValidateFormat(body, target.Purpose);
            if (IsFamily && HasFields(body))
                throw new CommunicationValidationException("Use shared wording for a family message. Student fields require one student.");
        }
        var rows = result.Entries.OrderByDescending(entry => entry.Included).Select(entry => new Recipient(
            entry.Included && entry.PhoneE164 is { Length: >= 4 } phone ? $"Guardian number ending in {phone[^4..]}" : entry.RecordName,
            entry.Included ? Render(body, FromPreview(entry)) : entry.Reason)).ToArray();
        recipients.Items.Clear();
        recipients.Items.AddRange(rows);
        if (rows.Length > 0) recipients.SelectedIndex = 0;
        string eligible = result.Included == 1 ? "1 eligible contact" : $"{result.Included} eligible contacts";
        string excluded = result.Excluded == 0 ? "" : $" · {result.Excluded} excluded";
        summary.Text = $"{option.Label} · {eligible}{excluded}";
        status.ForeColor = result.Included == 0 ? AppTheme.Danger : AppTheme.MutedText;
        status.Text = result.Included == 0
            ? "No eligible guardian contact for this message."
            : ShareImage ? "The button copies the image and opens WhatsApp. Paste with Ctrl+V, then press Send."
            : option.Provider is null ? "Press Send in WhatsApp." : "Confirm to send to the reviewed contact. Provider charges may apply.";
        review = new(target, result, body, Guid.NewGuid(), document);
    }

    private async Task ConfirmAsync()
    {
        if (review is not { Preview.Included: > 0 } reviewed || SelectedMethod is not { } option) return;
        Bitmap? image = null;
        try
        {
            if (option.Provider is not null)
            {
                bool unicode = option.Channel == "Sms" &&
                    (reviewed.Body.Any(ch => ch > 127) ||
                     reviewed.Preview.Entries.Where(row => row.Included)
                        .Any(row => row.RecordName.Any(ch => ch > 127)));
                string cost = unicode
                    ? "Personalized SMS contains Unicode text. Long or Unicode messages may use multiple separately billed SMS segments."
                    : option.Channel == "Sms"
                        ? "SMS messages may use multiple separately billed segments."
                        : "WhatsApp API provider charges may apply. Provider acceptance does not confirm delivery.";
                if (ModernMessageDialog.Show(this,
                    $"Send {reviewed.Preview.Included:N0} reviewed message(s)? {cost} "
                      + "The recipients and message must be checked before approval.",
                    "Confirm Paid Sending", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            if (ShareImage && reviewed.Document is { } imageDocument)
                image = await PrepareImageAsync(imageDocument);
            if (reviewed.Document is { } document)
                await VerifyDocumentAsync(document);
            var batch = await operations.Confirm(option, reviewed.Target, reviewed.Preview, reviewed.Body, reviewed.RequestId, metaSelection);
            EnsureAccess();
            if (fees && option.Provider is null)
            {
                await StartFeeHandoffAsync(batch, reviewed, image);
                if (!IsDisposed)
                {
                    handoffImage = image;
                    image = null;
                }
                return;
            }

            operations.Dispatch(option, batch);
            Finish();
        }
        finally { image?.Dispose(); }
    }

    private async Task StartFeeHandoffAsync(CommunicationConfirmation batch, ReviewedMessage reviewed, Bitmap? image)
    {
        Func<Task>? copyImage = image is null ? null : () =>
        {
            EnsureAccess();
            try { (operations.CopyImage ?? Clipboard.SetImage)(image); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                throw new CommunicationValidationException("Windows clipboard is busy. Try opening the chat again to copy the image.");
            }
            return Task.CompletedTask;
        };
        var session = new CommunicationManualHandoff(batch.BatchId, !batch.AlreadyConfirmed, copyImage,
            operations.FeeHandoff, compact: true,
            // Text-only fee details must stay current too, including retries and subsequent chats.
            validateContent: reviewed.Document is { } document ? () => VerifyDocumentAsync(document) : null);
        handoff = session;
        session.Finished += (_, _) => Finish();
        composer.Visible = false;
        content.Controls.Add(session);
        UiHelper.ApplyModernStyle(session);
        CancelButton = session.FinishButton;
        try { await session.StartAsync(); }
        catch
        {
            CancelButton = cancel;
            content.Controls.Remove(session);
            session.Dispose();
            handoff = null;
            composer.Visible = true;
            throw;
        }
    }

    private void Finish()
    {
        busy = false;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task<Bitmap> PrepareImageAsync(FeeMessageDocument document)
    {
        await VerifyDocumentAsync(document);
        return await (operations.RenderImage ?? (item => item.RenderImageAsync()))(document);
    }

    private async Task VerifyDocumentAsync(FeeMessageDocument document)
    {
        using var current = await ReadFeeDocumentAsync();
        EnsureAccess();
        if (current.ReviewKey != document.ReviewKey)
            throw new CommunicationValidationException("The fee document or family membership changed. Close the handoff and review it again.");
    }

    private void UpdateView()
    {
        bool reviewing = review is not null;
        bool useMeta = SelectedMethod?.IsMeta == true;
        options.Visible = messageField.Visible = !reviewing;
        feeOptions.Visible = fees && !reviewing;
        feeOptions.Enabled = !busy;
        attachImage.Visible = fees && SelectedMethod is { Provider: null };
        attachImage.Enabled = !busy && fees && SelectedMethod is { Provider: null };
        chooseMeta.Visible = !reviewing && useMeta;
        templateField.Visible = !reviewing && !useMeta;
        summary.Visible = previewField.Visible = reviewing;
        recipientField.Visible = reviewing && recipients.Items.Count > 0;
        back.Visible = reviewing;
        action.Text = !reviewing ? "Review Message" : ShareImage ? "Copy & Open Chat" : SelectedMethod?.Provider is not null ? "Confirm & Send"
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
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                if (handoff is null) UpdateView();
            }
        }
    }
}
