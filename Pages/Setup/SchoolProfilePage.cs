using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Setup;
public sealed class SchoolProfilePage : V2Page, IUnsavedChanges
{
    private readonly ModernTextBox schoolName =
        HumanUi.TextBox(false, 300);
    private readonly ModernTextBox addressLine =
        HumanUi.TextBox(false, 500);
    private readonly ModernTextBox city =
        HumanUi.TextBox(false, 200);
    private readonly ModernTextBox contactNumber =
        HumanUi.TextBox(false, SchemaLengths.SchoolProfileContactNumber);
    private readonly ModernTextBox email =
        HumanUi.TextBox(false, 250);
    private readonly ModernNumericTextBox feeProrationCutoffDay =
        HumanUi.Number(28);
    private readonly ModernNumericTextBox transportCutoffDay=HumanUi.Number(28);
    private readonly PictureBox logoPreview = new() {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = AppTheme.Surface,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label logoInfo = new() {
        Dock = DockStyle.Bottom,
        Height = Math.Max(16, AppMetrics.TextHeight(AppTheme.NormalFont)),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.Text,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private byte[]? _logoBytes;
    private string? _logoFileName;
    private string? _logoContentType;
    private readonly PictureBox signaturePreview = new() {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = AppTheme.Surface, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly CheckBox printSignature = new() {
        Text = "Print principal signature on certificates", Dock = DockStyle.Fill,
        AutoSize = false, Font = AppTheme.NormalFont, ForeColor = AppTheme.Text
    };
    private byte[]? _signatureBytes;
    private string? _signatureContentType;
    private readonly FormValidator contactValidator = new();
    public SchoolProfilePage():this(true){}
    internal SchoolProfilePage(bool initialize)
        : base(PageTitles.SchoolInformation, "Set the school details shown on reports, certificates and receipts.")
    {
        UiHelper.DisposePictureImageOnDispose(logoPreview);
        UiHelper.DisposePictureImageOnDispose(signaturePreview);
        RecordTextPolicy.WireInput(schoolName, "@SchoolName");
        RecordTextPolicy.WireInput(addressLine, "@AddressLine");
        RecordTextPolicy.WireInput(city, "@City");
        Disposed += (_, _) => contactValidator.Dispose();
        SchoolContactInput.AttachPhone(contactNumber, () => contactValidator);
        SchoolContactInput.AttachEmail(email, () => contactValidator);
        var saveDetailsButton = AddAction(UiText.Actions.Save, (_, _) => Save(), "Success");
        // Ctrl+S always saves the school details, even when focus is inside the
        // independently saved logo or signature section.
        SetSaveShortcut(saveDetailsButton);
        AddAction(UiText.Actions.Reload, (_, _) => LoadProfile());
        UseToolbar();
        var details =
            HumanUi.Card("School Identity", "Shown wherever a document needs the school's name, address or contact details.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        fields.Controls.Add(
            HumanUi.Field("School Name", schoolName, true), 0, 0);
        fields.Controls.Add(
            HumanUi.Field("Phone / Mobile", contactNumber), 1, 0);
        fields.Controls.Add(
            HumanUi.Field("Address", addressLine), 0, 1);
        fields.Controls.Add(
            HumanUi.Field("City", city), 1, 1);
        fields.Controls.Add(
            HumanUi.Field("Email", email), 0, 2);
        details.Content.Controls.Add(fields);
        var logoCard =
            HumanUi.Card("Logo", "Choose a PNG or JPEG image up to 2 MB.");
        var logoPanel =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
        logoPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, logoInfo.Height));
        logoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(46)));
        logoPanel.Controls.Add(logoPreview, 0, 0);
        logoPanel.Controls.Add(logoInfo, 0, 1);
        // The logo column can narrow on ordinary laptop screens. Proportional
        // columns keep all three actions inside the card without a fixed-width
        // FlowLayoutPanel silently clipping the last button.
        var logoButtons = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        logoButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        logoButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        logoButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        logoButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var chooseLogo = new ModernButton {
            Text = "Choose Logo…", Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Sm, 0),
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        var saveLogo = new ModernButton {
            Text = "Save Logo", Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Sm, 0),
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        var removeLogo = new ModernButton {
            Text = "Remove Logo", Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        chooseLogo.Click += async (_, _) => await ChooseLogoAsync();
        saveLogo.Click += (_, _) => SaveLogo();
        removeLogo.Click += (_, _) => RemoveLogo();
        logoButtons.Controls.Add(chooseLogo, 0, 0);
        logoButtons.Controls.Add(saveLogo, 1, 0);
        logoButtons.Controls.Add(removeLogo, 2, 0);
        logoPanel.Controls.Add(logoButtons, 0, 2);
        logoCard.Content.Controls.Add(logoPanel);
        var feePolicyCard =
            HumanUi.Card("Fee Policy", "Separate cutoffs for admission billing and transport changes.");
        var feePolicyFields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        feePolicyFields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        feePolicyFields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        feePolicyFields.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        feePolicyFields.Controls.Add(
            HumanUi.Field("Fee Proration Cutoff Day", feeProrationCutoffDay), 0, 0);
        feePolicyFields.Controls.Add(HumanUi.Field("Transport Cutoff Day",transportCutoffDay),1,0);
        const string cutoffExplanation = "New admissions on or after this day start monthly fees next month; earlier admissions start in their admission month. Readmission uses the month chosen during fee review.";
        SetToolTip(feeProrationCutoffDay, cutoffExplanation);
        feePolicyFields.RowCount = 2;
        int guidanceHeight = AppMetrics.TextHeight(AppTheme.SmallFont) * 4 + 8;
        feePolicyFields.RowStyles.Add(new RowStyle(SizeType.Absolute, guidanceHeight));
        var cutoffGuidance = new Label {
            Text = "Admission cutoff: earlier admissions start this month; on/after it, next month. Readmission uses its reviewed month.\r\nTransport cutoff: this month before it, next month on/after it. Paid or partially paid months stay charged. Blank allows the current month.",
            Dock = DockStyle.Fill,
            Font = AppTheme.SmallFont,
            ForeColor = AppTheme.MutedText,
            AutoEllipsis = true
        };
        SetToolTip(cutoffGuidance, cutoffGuidance.Text);
        SetToolTip(transportCutoffDay,"Transport only: 1–28. Before this day use this month; on or after it use next month. Paid and partially paid months remain protected. Blank allows current-month changes throughout the month.");
        feePolicyFields.Controls.Add(cutoffGuidance, 0, 1);
        feePolicyFields.SetColumnSpan(cutoffGuidance, 2);
        feePolicyCard.Content.Controls.Add(feePolicyFields);
        // Keep certificate signature management compact: the three independent
        // image actions share one horizontal row rather than overflowing a
        // fixed-height vertical stack on shorter windows.
        var signatureCard = HumanUi.Card("Principal Signature");
        const string signatureHelp =
            "Optional image for certificates. When turned off, the existing blank signature line remains.";
        SetToolTip(signaturePreview, signatureHelp);
        SetToolTip(printSignature, signatureHelp);
        var signatureFields = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        signatureFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(142)));
        signatureFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        signatureFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        signaturePreview.Margin = new Padding(0, 0, UiDesignSystem.Spacing.Md, 0);
        signatureFields.Controls.Add(signaturePreview, 0, 0);

        var signatureEditor = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        signatureEditor.RowStyles.Add(new RowStyle(
            SizeType.Absolute, AppMetrics.TextHeight(AppTheme.NormalFont) + AppScale.Scale(7)));
        signatureEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        printSignature.Margin = Padding.Empty;
        signatureEditor.Controls.Add(printSignature, 0, 0);

        var signatureButtons = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        signatureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        signatureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        signatureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        signatureButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var chooseSignature = new ModernButton {
            Text = "Choose Signature…", Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Sm, 0),
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        var saveSignature = new ModernButton {
            Text = "Save Signature", Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Sm, 0),
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        var removeSignature = new ModernButton {
            Text = "Remove Signature", Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ThemeRole = UiDesignSystem.Actions.Secondary
        };
        chooseSignature.Click += async (_, _) => await ChooseSignatureAsync();
        saveSignature.Click += (_, _) => SaveSignature();
        removeSignature.Click += (_, _) => RemoveSignature();
        signatureButtons.Controls.Add(chooseSignature, 0, 0);
        signatureButtons.Controls.Add(saveSignature, 1, 0);
        signatureButtons.Controls.Add(removeSignature, 2, 0);
        signatureEditor.Controls.Add(signatureButtons, 0, 1);
        signatureFields.Controls.Add(signatureEditor, 1, 0);
        signatureCard.Content.Controls.Add(signatureFields);

        int gap = UiDesignSystem.Spacing.Md;
        // Two independent columns, not a row span across mixed absolute and
        // percent rows. The former row-span metadata was correct but WinForms
        // visually stopped the logo at the fee-policy row on some sizes.
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var leftColumn = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = new Padding(0, 0, gap, 0), Padding = Padding.Empty
        };
        leftColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(3) + gap));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(1) + guidanceHeight + gap));
        int signatureRowHeight = signatureCard.Header.Height +
            signatureCard.Padding.Vertical + signatureCard.Content.Padding.Vertical +
            Math.Max(AppScale.Scale(78),
                AppMetrics.TextHeight(AppTheme.NormalFont) +
                AppMetrics.InputControlHeight + AppScale.Scale(9));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, signatureRowHeight));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Margin = new Padding(0, 0, 0, gap);
        feePolicyCard.Margin = new Padding(0, 0, 0, gap);
        signatureCard.Margin = Padding.Empty;
        logoCard.Margin = Padding.Empty;
        leftColumn.Controls.Add(details, 0, 0);
        leftColumn.Controls.Add(feePolicyCard, 0, 1);
        leftColumn.Controls.Add(signatureCard, 0, 2);
        root.Controls.Add(leftColumn, 0, 0);
        root.Controls.Add(logoCard, 1, 0);
        SetContent(
            root,
            "Save school details separately. Logo and principal signature each have their own Save and Remove buttons.");
        FormFlow.EnterMovesToNext(this);
        if(initialize)Defer(LoadProfile);
    }
    /* True only after a read of the profile completed (a profile, or none set up yet). */
    private bool _profileReadCompleted;
    private void LoadProfile()
    {
        try
        {
            _profileReadCompleted = false;
            contactValidator.ClearAll();
            var dt =
                DatabaseHelper.QueryUser("GetSchoolProfile");
            if (dt.Rows.Count == 0)
            {
                schoolName.Clear();
                addressLine.Clear();
                city.Clear();
                contactNumber.Clear();
                email.Clear();
                feeProrationCutoffDay.Value = 0;
                transportCutoffDay.Value=0;
                ClearLogoState();
                ClearSignatureState();
                CaptureBaseline();
                _profileReadCompleted = true;
                SetStatus("The school profile has not been set up yet. Fill in the details below and click Save.");
                return;
            }
            DataRow row =
                dt.Rows[0];
            schoolName.Text =
                TextOf(row["SchoolName"]);
            addressLine.Text =
                TextOf(row["AddressLine"]);
            city.Text =
                TextOf(row["City"]);
            contactNumber.Text =
                TextOf(row["ContactNumber"]);
            email.Text =
                TextOf(row["Email"]);
            feeProrationCutoffDay.Value =
                row["FeeProrationCutoffDay"] == DBNull.Value
                    ? 0
                    : Convert.ToDecimal(row["FeeProrationCutoffDay"]);
            transportCutoffDay.Value=row["TransportCutoffDay"] is DBNull?0:Convert.ToDecimal(row["TransportCutoffDay"]);
            if (row["LogoImage"] != DBNull.Value)
            {
                _logoBytes =
                    (byte[])row["LogoImage"];
                _logoFileName =
                    TextOf(row["LogoFileName"]);
                _logoContentType =
                    TextOf(row["LogoContentType"]);
                UiHelper.ReplacePictureImage(logoPreview, BitmapFromBytes(_logoBytes));
                logoInfo.Text =
                    string.IsNullOrWhiteSpace(_logoFileName)
                        ? "Logo on file."
                        : _logoFileName;
            }
            else
            {
                ClearLogoState();
            }
            var signatures = DatabaseHelper.QueryUser("GetSchoolCertificateSignature");
            if (signatures.Rows.Count > 0)
            {
                var saved = signatures.Rows[0];
                _signatureBytes = saved["PrincipalSignatureImage"] is byte[] image ? image : null;
                _signatureContentType = saved["PrincipalSignatureContentType"] is DBNull ? null : Convert.ToString(saved["PrincipalSignatureContentType"]);
                printSignature.Checked = saved["PrintPrincipalSignature"] is not DBNull
                    && Convert.ToBoolean(saved["PrintPrincipalSignature"]);
                UiHelper.ReplacePictureImage(signaturePreview,
                    _signatureBytes is null ? null : BitmapFromBytes(_signatureBytes));
            }
            else ClearSignatureState();
            SetStatus("School profile loaded.");
            /* # - the baseline Reload compares against. Save reloads the profile afterwards, so a committed save leaves the form clean and Reload never asks about work that was just saved. */
            CaptureBaseline();
            _profileReadCompleted = true;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private async Task ChooseLogoAsync()
    {
        try
        {
            using var dialog = new OpenFileDialog {
                Filter = "Image files (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
                Title = "Choose the school logo"
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            byte[] bytes = await BoundedImageFile.ReadAsync(dialog.FileName, 2_000_000);
            if (IsDisposed) return;
            using var validated = BitmapFromBytes(bytes);
            UiHelper.ReplacePictureImage(logoPreview, new Bitmap(validated));
            _logoBytes = bytes;
            _logoFileName = Path.GetFileName(dialog.FileName);
            _logoContentType = Path.GetExtension(dialog.FileName).Equals(".png",
                StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            logoInfo.Text = $"{_logoFileName} (not saved yet)";
            SetStatus("Logo selected. Click Save Logo to store it; school details are not changed.");
        }
        catch (InvalidDataException ex) { if (!IsDisposed) SetStatus(ex.Message, PageStatusKind.Warning); }
        catch (Exception ex) { Fail(ex); }
    }

    private void SaveLogo()
    {
        try
        {
            if (!_profileReadCompleted) { Warn("Reload the school profile first."); return; }
            if (_logoBytes is null) { Warn("Choose a logo before saving."); return; }
            DatabaseHelper.Execute("SaveSchoolLogo",
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                new SqlParameter("@LogoImage", SqlDbType.VarBinary, -1) { Value = _logoBytes },
                DatabaseHelper.P("@LogoFileName", _logoFileName),
                DatabaseHelper.P("@LogoContentType", _logoContentType));
            logoInfo.Text = _logoFileName ?? "Logo on file.";
            CaptureLogoBaseline();
            Info("School logo saved. Other school details were not changed.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void RemoveLogo()
    {
        if (!_profileReadCompleted) { Warn("Reload the school profile first."); return; }
        if (_logoBytes is null) { Warn("There is no logo to remove."); return; }
        if (!Confirm("Remove the school logo now? School details and signature will not change.",
            "Remove School Logo", defaultYes: false)) return;
        try
        {
            DatabaseHelper.Execute("SaveSchoolLogo",
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                new SqlParameter("@LogoImage", SqlDbType.VarBinary, -1) { Value = DBNull.Value },
                DatabaseHelper.P("@LogoFileName", null),
                DatabaseHelper.P("@LogoContentType", null));
            ClearLogoState();
            CaptureLogoBaseline();
            Info("School logo removed. Other school details were not changed.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ClearLogoState()
    {
        UiHelper.ReplacePictureImage(logoPreview, null);
        logoInfo.Text =
            "No logo on file.";
        _logoBytes = null;
        _logoFileName = null;
        _logoContentType = null;
    }
    private async Task ChooseSignatureAsync()
    {
        try
        {
            using var dialog = new OpenFileDialog {
                Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                Title = "Choose principal signature"
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            byte[] bytes = await BoundedImageFile.ReadAsync(dialog.FileName, 2_000_000);
            if (IsDisposed) return;
            using var validated = BitmapFromBytes(bytes);
            UiHelper.ReplacePictureImage(signaturePreview, new Bitmap(validated));
            _signatureBytes = bytes;
            _signatureContentType = Path.GetExtension(dialog.FileName).Equals(".png",
                StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            SetStatus("Signature selected. Click Save Signature to store it independently.");
        }
        catch (InvalidDataException ex) { if (!IsDisposed) SetStatus(ex.Message, PageStatusKind.Warning); }
        catch (Exception ex) { Fail(ex); }
    }

    private void SaveSignature()
    {
        try
        {
            if (!_profileReadCompleted) { Warn("Reload the school profile first."); return; }
            if (printSignature.Checked && _signatureBytes is null) {
                Warn("Choose a principal signature before enabling printing."); return;
            }
            DatabaseHelper.Execute("SaveSchoolCertificateSignature",
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                new SqlParameter("@SignatureImage", SqlDbType.VarBinary, -1) {
                    Value = (object?)_signatureBytes ?? DBNull.Value
                },
                DatabaseHelper.P("@SignatureContentType",
                    _signatureBytes is null ? null : _signatureContentType),
                DatabaseHelper.P("@PrintSignature", printSignature.Checked));
            CaptureSignatureBaseline();
            Info("Principal signature settings saved. School details and logo were not changed.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void RemoveSignature()
    {
        if (!_profileReadCompleted) { Warn("Reload the school profile first."); return; }
        if (_signatureBytes is null && loadedSignatureBytes is null && !printSignature.Checked) {
            Warn("There is no principal signature to remove."); return;
        }
        if (!Confirm("Remove the principal signature and disable signature printing? School details and logo will not change.",
            "Remove Principal Signature", defaultYes: false)) return;
        try
        {
            DatabaseHelper.Execute("SaveSchoolCertificateSignature",
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                new SqlParameter("@SignatureImage", SqlDbType.VarBinary, -1) { Value = DBNull.Value },
                DatabaseHelper.P("@SignatureContentType", null),
                DatabaseHelper.P("@PrintSignature", false));
            ClearSignatureState();
            CaptureSignatureBaseline();
            Info("Principal signature removed. School details and logo were not changed.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ClearSignatureState()
    {
        UiHelper.ReplacePictureImage(signaturePreview, null);
        _signatureBytes = null;
        _signatureContentType = null;
        printSignature.Checked = false;
    }
    private static Bitmap BitmapFromBytes(byte[] bytes)
    {
        using var stream =
            new MemoryStream(bytes);
        using var loaded =
            Image.FromStream(stream);
        return new Bitmap(loaded);
    }
    private void Save()
    {
        try
        {
            if (!_profileReadCompleted) {
                Warn("The school profile has not been read successfully. Use Reload first.");
                return;
            }
            if (!ValidateRequiredInputs()) return;
            contactValidator.Begin();
            contactValidator.Rule(contactNumber,
                () => SchoolContactValidation.IsValidPhoneList(contactNumber.Text),
                SchoolContactValidation.PhoneMessage);
            contactValidator.Rule(email,
                () => EmailAddressValidator.IsBlankOrValid(email.Text),
                "Enter a valid email address, such as name@example.com.");
            if (!contactValidator.Finish()) return;
            if (!WholeRupees.IsWhole(feeProrationCutoffDay.Value)
                || !WholeRupees.IsWhole(transportCutoffDay.Value)) {
                Warn("Cutoff days must be whole numbers from 1 to 28, or blank."); return;
            }
            if (string.IsNullOrWhiteSpace(schoolName.Text)) {
                Warn("Enter the school name."); return;
            }
            DatabaseHelper.Execute("SaveSchoolProfileDetails",
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                DatabaseHelper.P("@SchoolName", schoolName.Text.Trim()),
                DatabaseHelper.P("@AddressLine", DatabaseHelper.OptionalText(addressLine.Text)),
                DatabaseHelper.P("@City", DatabaseHelper.OptionalText(city.Text)),
                DatabaseHelper.P("@ContactNumber", DatabaseHelper.OptionalText(contactNumber.Text)),
                DatabaseHelper.P("@Email", DatabaseHelper.OptionalText(email.Text)),
                DatabaseHelper.NullableInt("@FeeProrationCutoffDay",
                    feeProrationCutoffDay.Value > 0 ? (int?)feeProrationCutoffDay.Value : null),
                DatabaseHelper.NullableInt("@TransportCutoffDay",
                    transportCutoffDay.Value > 0 ? (int?)transportCutoffDay.Value : null));
            CaptureDetailsBaseline();
            Info("School information and fee policy saved. Logo and signature were not changed.");
            Defer(() => FormValidator.FocusInput(schoolName));
        }
        catch (Exception ex) { Fail(ex); }
    }

    /* Track independent baselines so saving one section does not discard
       unsaved edits to either of the others. */
    private string[] loadedValues = Array.Empty<string>();
    private byte[]? loadedLogoBytes;
    private string? loadedLogoFileName;
    private byte[]? loadedSignatureBytes;
    private string? loadedSignatureContentType;
    private bool loadedPrintSignature;
    private string[] CurrentValues() => new[] {
        schoolName.Text, addressLine.Text, city.Text, contactNumber.Text,
        email.Text, feeProrationCutoffDay.Value.ToString(), transportCutoffDay.Value.ToString()
    };
    private void CaptureDetailsBaseline() => loadedValues = CurrentValues();
    private void CaptureLogoBaseline()
    {
        loadedLogoBytes = _logoBytes;
        loadedLogoFileName = _logoFileName;
    }
    private void CaptureSignatureBaseline()
    {
        loadedSignatureBytes = _signatureBytes;
        loadedSignatureContentType = _signatureContentType;
        loadedPrintSignature = printSignature.Checked;
    }
    private void CaptureBaseline()
    {
        CaptureDetailsBaseline();
        CaptureLogoBaseline();
        CaptureSignatureBaseline();
    }
    /// <summary>implements IUnsavedChanges so the dashboard's navigation guard can see this page.</summary>
    public bool HasUnsavedChanges =>
        ComputeHasUnsavedChanges();
    bool IUnsavedChanges.HasUnsavedChanges => ComputeHasUnsavedChanges();
    public string UnsavedChangesMessage =>
        "This record has unsaved changes.\r\n\r\nIf you leave now, those changes will be lost.";
    /* Renamed from HasUnsavedChanges so the interface property can take that name: the page had a private method of that name. */
    private bool ComputeHasUnsavedChanges()
    {
        if (loadedValues.Length == 0)
            return false;
        try
        {
            if (!ReferenceEquals(_signatureBytes, loadedSignatureBytes) ||
                !string.Equals(_signatureContentType, loadedSignatureContentType, StringComparison.Ordinal) ||
                printSignature.Checked != loadedPrintSignature)
                return true;
            if (!ReferenceEquals(_logoBytes, loadedLogoBytes))
                return true;
            if (!string.Equals(_logoFileName, loadedLogoFileName, StringComparison.Ordinal))
            {
                return true;
            }
            string[] current =
                CurrentValues();
            if (current.Length != loadedValues.Length)
                return true;
            for (int i = 0; i < current.Length; i++)
            {
                if (!string.Equals(current[i], loadedValues[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception nonFatalException36)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException36, "SchoolProfilePage: ComputeHasUnsavedChanges fallback");
            /* A dirty check must never throw, and it must never invent a prompt: an unreadable form is treated as clean. */
            return false;
        }
    }
    private static string TextOf(
        object value) =>
        value is null || value == DBNull.Value
            ? ""
            : Convert.ToString(value) ?? "";
}
