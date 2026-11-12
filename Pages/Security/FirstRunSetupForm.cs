using System.Security.Cryptography;
using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Security;
public sealed class FirstRunSetupForm : ApplicationWindowForm
{
    private readonly ModernTextBox username = new() { PlaceholderText = "Administrator username", Dock = DockStyle.Fill };
    private readonly ModernTextBox password = new() { PlaceholderText = "Administrator password", UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly ModernTextBox confirm = new() { PlaceholderText = "Confirm password", UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly ModernButton create = new() { SavesChanges = true, Text = "Create Administrator", Dock = DockStyle.Fill };
    private readonly ModernButton restoreExisting = new() {
        Text = "Restore an Existing School", ThemeRole = "Secondary", Dock = DockStyle.Fill
    };
    private readonly Label status = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter };
    /* The sibling security forms guard typed input on close; this one did not, so the administrator credentials typed here were thrown away by the window's close button. */
    private bool savedSuccessfully;
    private bool discardConfirmed;
    /* Mark missing or mismatched setup fields inline. */
    private readonly FormValidator validator = new();
    public FirstRunSetupForm()
    {
        Text = "FMSC - First Run Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(AppScale.Scale(620), AppScale.Scale(520));
        MinimumSize = new Size(AppScale.Scale(560), AppScale.Scale(460));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        username.Font = AppTheme.NormalFont;
        password.Font = AppTheme.NormalFont;
        confirm.Font = AppTheme.NormalFont;
        username.Dock = DockStyle.Fill;
        password.Dock = DockStyle.Fill;
        confirm.Dock = DockStyle.Fill;
        var card = new RoundedPanel {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Surface,
            BorderColor = AppTheme.Border,
            ShowBorder = true,
            CornerRadius = AppTheme.CardRadius,
            Padding = new Padding(AppScale.Scale(30, AppScale.SpacingScale))
        };
        var logo = new PictureBox {
            Image = File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "FMSC_Logo_Rectangular.png"))
                ? UiHelper.LoadImageUnlocked(Path.Combine(AppContext.BaseDirectory, "Assets", "FMSC_Logo_Rectangular.png"))
                : null,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        UiHelper.DisposePictureImageOnDispose(logo);
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8
        };
        root.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            AppScale.Scale(74, AppScale.ControlSizeScale)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.AuthenticationTitleRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        root.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
            AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(logo, 0, 0);
        root.Controls.Add(new Label {
            Text = "First-Time Administrator Setup",
            Dock = DockStyle.Fill,
            Font = AppTheme.PageTitleFont,
            ForeColor = AppTheme.Primary,
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 1);
        root.Controls.Add(HumanUi.Field("Administrator Username", username, true), 0, 2);
        root.Controls.Add(HumanUi.Field("Password", password, true), 0, 3);
        root.Controls.Add(HumanUi.Field("Confirm Password", confirm, true), 0, 4);
        root.Controls.Add(create, 0, 5);
        root.Controls.Add(restoreExisting, 0, 6);
        root.Controls.Add(status, 0, 7);
        create.SetColors(AppTheme.Success);
        create.Dock = DockStyle.Fill;
        status.Font = AppTheme.NormalFont;
        card.Controls.Add(root);
        var outer = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(
                AppScale.Scale(40, AppScale.SpacingScale),
                AppScale.Scale(28, AppScale.SpacingScale),
                AppScale.Scale(40, AppScale.SpacingScale),
                AppScale.Scale(28, AppScale.SpacingScale))
        };
        outer.Controls.Add(card, 0, 0);
        Controls.Add(outer);
        FormClosed += (_, _) => logo.Image?.Dispose();
        AcceptButton = create;
        SetSaveShortcut(create);
        FormFlow.EnterMovesToNext(this);
        Shown += (_, _) => username.Focus();
        validator.BindSummary(status);
        Disposed += (_, _) => validator.Dispose();
        create.Click += Create_Click;
#if FMSC_LEVEL2
        restoreExisting.Visible = false;
#else
        restoreExisting.Click += (_, _) => {
            try
            {
                FMSC_SMS.Services.Backup.ReplacementSchoolRecovery.RequireAbsentOrVirginSchool();
                using var wizard = new RestoreExistingSchoolForm();
                if (wizard.ShowDialog(this) == DialogResult.OK)
                {
                    savedSuccessfully = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                ModernMessageDialog.ShowWarning(this, DbMessage.UserMessage(ex), "School recovery unavailable");
            }
        };
#endif
        /* see savedSuccessfully. The window's close is the form's only discard gesture; programmatic closes are left alone. */
        FormClosing += (_, e) => {
            if (e.CloseReason != CloseReason.UserClosing)
                return;
            e.Cancel = !ConfirmDiscard();
        };
    }
    /// <summary>dirty means "something typed and not saved yet", across the three fields, because the validation messages above leave the text in place for the operator to correct.</summary>
    private bool IsDirty() =>
        !savedSuccessfully &&
        DialogResult == DialogResult.None &&
        (!string.IsNullOrWhiteSpace(username.Text) ||
         password.Text.Length > 0 ||
         confirm.Text.Length > 0);
    /// <summary>asks at most once per close gesture. The message says what closing this particular form actually costs, which the shared wording cannot know.</summary>
    private bool ConfirmDiscard()
    {
        return ModernMessageDialog.ConfirmDiscardOnce(
            this,
            IsDirty,
            ref discardConfirmed,
            "The administrator details you typed have not been saved." + Environment.NewLine +
                Environment.NewLine +
                "Closing this window leaves FMSC with no administrator account, so it cannot start." +
                Environment.NewLine +
                Environment.NewLine +
                "Close anyway?");
    }
    private void Create_Click(object? sender, EventArgs e)
    {
        validator.Begin();
        validator.Required(username, "Enter a username");
        validator.Check(
            password,
            PasswordPolicy.IsValid(password.Text),
            PasswordPolicy.Requirement,
            () => PasswordPolicy.IsValid(password.Text));
        if (password.Text.Length > 0)
        {
            validator.Check(
                confirm,
                string.Equals(password.Text, confirm.Text, StringComparison.Ordinal),
                "Does not match",
                () => string.Equals(password.Text, confirm.Text, StringComparison.Ordinal));
        }
        if (!validator.Finish())
            return;
        create.Enabled = false;
        try
        {
            var state = DatabaseHelper.Query("GetFirstRunStatus");
            if (state.Rows.Count == 0 || !Convert.ToBoolean(state.Rows[0]["IsFirstRun"]))
            {
                ShowError("Initial administrator setup has already been completed. Please use the normal login screen.");
                return;
            }
            using var rng = RandomNumberGenerator.Create();
            var salt = new byte[32];
            rng.GetBytes(salt);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password.Text, salt, 100_000, HashAlgorithmName.SHA512, 64);
            DatabaseHelper.Query(
                "BootstrapFirstAdministrator",
                DatabaseHelper.P("@Username", username.Text.Trim()),
                new Microsoft.Data.SqlClient.SqlParameter("@PasswordHash", hash),
                new Microsoft.Data.SqlClient.SqlParameter("@PasswordSalt", salt));
            ModernMessageDialog.ShowInfo(
                this,
                "The administrator account has been created.\n\nYou can now sign in with the username and password you entered.",
                "FMSC Setup");
            /* The administrator was created, so this close is not a discard and must not ask. */
            savedSuccessfully = true;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            ShowError(DbMessage.UserMessage(ex));
        }
        finally
        {
            create.Enabled = true;
        }
    }
    private void ShowError(string message)
    {
        status.Text = message;
        status.ForeColor = AppTheme.Danger;
        ModernMessageDialog.ShowWarning(this, message, "FMSC Setup");
    }
}

