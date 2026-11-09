using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;
namespace FMSC_SMS.Pages.Security;
public sealed class LoginForm : ApplicationWindowForm
{
    private readonly Func<string, string, (bool Success, string Message, Exception? ConnectionFailure)> signIn;
    private bool signingIn;
    private readonly StartupUpdateController startupUpdates;
    internal PreparedUpdate? PendingUpdate => startupUpdates.PendingUpdate;
    readonly ModernTextBox user = HumanUi.TextBox(false, 100);
    readonly ModernTextBox pass = HumanUi.TextBox(false, 100);
    readonly ModernButton login = new() { Text = "Sign In", Dock = DockStyle.Fill };
    /* Validate missing credentials inline; rejected credentials still use the refusal dialog. */
    readonly FormValidator validator = new();
    internal LoginForm(bool checkConnectionOnStartup) : this(AttemptSignIn, checkConnectionOnStartup) { }
    public LoginForm() : this(AttemptSignIn) { }
    internal LoginForm(Func<string, string, (bool Success, string Message, Exception? ConnectionFailure)> signIn,
        bool checkConnectionOnStartup = false,
        Func<bool, (bool Connected, string Message, Exception? Failure)>? connect = null,
        Func<bool>? completeFirstRun = null,
        Func<Form, Control, StartupUpdateController>? startupUpdateFactory = null)
    {
        this.signIn = signIn;
        FormClosing += (_, e) => { if (signingIn) e.Cancel = true; };
        Text = "FMSC - Sign In";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = AppMetrics.StartupDialogClientSize;
        MinimumSize = new Size(AppScale.Scale(680), AppScale.Scale(460));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        pass.UseSystemPasswordChar = true;
        user.PlaceholderText = "Enter username";
        pass.PlaceholderText = "Enter password";
        login.SetColors(AppTheme.Primary);
        login.Click += Login_Click;
        var recover = new ModernButton {
            Text = "Forgot Login Details?", Dock = DockStyle.Fill, ThemeRole = "Secondary"
        };
        recover.Click += (_, _) => {
            using var recovery = new AdministratorRecoveryForm();
            if (recovery.ShowDialog(this) != DialogResult.OK) return;
            user.Text = recovery.RecoveredUsername;
            pass.Text = "";
            pass.Focus();
        };
        var shell = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(AppScale.Scale(28, AppScale.SpacingScale)),
            Margin = Padding.Empty
        };
        shell.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 48F));
        shell.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 52F));
        var brand = new RoundedPanel {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Primary,
            BorderColor = AppTheme.Primary,
            ShowBorder = false,
            CornerRadius = AppTheme.CardRadius,
            Padding = new Padding(AppScale.Scale(28, AppScale.SpacingScale)),
            Margin = new Padding(0, 0, AppMetrics.SpacingMd, 0)
        };
        var logo = new PictureBox {
            Image = File.Exists(
                Path.Combine(AppContext.BaseDirectory, "Assets", "FMSC_Logo_Rectangular.png"))
                    ? UiHelper.LoadImageUnlocked(
                        Path.Combine(AppContext.BaseDirectory, "Assets", "FMSC_Logo_Rectangular.png"))
                    : null,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, AppMetrics.SpacingXl)
        };
        UiHelper.DisposePictureImageOnDispose(logo);
        var brandLayout = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        brandLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 26F));
        brandLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 25F));
        brandLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 17F));
        brandLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 32F));
        var schoolTitle = new Label {
            Text = "Fahim Model \nSchool & College",
            Dock = DockStyle.Fill,
            Font = AppTheme.PageTitleFont,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            AutoEllipsis = false,
            Tag = "ThemeOnPrimaryText",
            Padding = new Padding(0, 2, 0, 2)
        };
        var systemTitle = new Label {
            Text = "School Management System",
            Dock = DockStyle.Fill,
            Font = AppTheme.HeadingFont,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            AutoEllipsis = false,
            Tag = "ThemeOnPrimaryText",
            Padding = new Padding(0, 2, 0, 2)
        };
        var description = new Label {
            Text = "A clean, secure workspace for admissions, students, fees and school operations.",
            Dock = DockStyle.Fill,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Tint(Color.White, 0.18f),
            TextAlign = ContentAlignment.TopLeft,
            AutoSize = false,
            AutoEllipsis = false,
            Tag = "ThemeOnPrimaryText",
            Padding = new Padding(0, 4, 0, 0)
        };
        brandLayout.Controls.Add(logo, 0, 0);
        brandLayout.Controls.Add(schoolTitle, 0, 1);
        brandLayout.Controls.Add(systemTitle, 0, 2);
        brandLayout.Controls.Add(description, 0, 3);
        brand.Controls.Add(brandLayout);
        var card = new RoundedPanel {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Surface,
            BorderColor = AppTheme.Border,
            ShowBorder = true,
            CornerRadius = AppTheme.CardRadius,
            Padding = new Padding(AppScale.Scale(34, AppScale.SpacingScale)),
            Margin = new Padding(AppMetrics.SpacingMd, 0, 0, 0)
        };
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppScale.Scale(62, AppScale.ControlSizeScale)));
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.SpacingXl));
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(
            new Label {
                Text = "Welcome back",
                Dock = DockStyle.Fill,
                Font = AppTheme.PageTitleFont,
                ForeColor = AppTheme.HeadingText,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            },
            0,
            0);
        root.Controls.Add(
            HumanUi.Field("Username", user, true),
            0,
            1);
        root.Controls.Add(
            HumanUi.Field("Password", pass, true),
            0,
            2);
        root.Controls.Add(
            login,
            0,
            4);
        root.Controls.Add(recover, 0, 5);
        root.Controls.Add(
            new JustifiedNote {
                Text = "Use your credentials to continue.",
                Dock = DockStyle.Fill,
                Font = AppTheme.NormalFont,
                ForeColor = AppTheme.MutedText,
                Padding = new Padding(0, AppMetrics.SpacingXs, 0, 0)
            },
            0,
            6);
        card.Controls.Add(root);
        shell.Controls.Add(brand, 0, 0);
        shell.Controls.Add(card, 1, 0);
        // Keep both startup stages under one root. The window's responsive
        // viewport must not wrap the hidden login shell over the connection view.
        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        content.Controls.Add(shell);
        Controls.Add(content);
        startupUpdates = startupUpdateFactory?.Invoke(this, content) ?? new StartupUpdateController(this, content);
        FormClosed += (_, _) => logo.Image?.Dispose();
        AcceptButton = login;
        // Login must retain username -> Tab -> password -> Enter to sign in.
        // Do not opt this form into generic Enter-to-next-field navigation:
        // it consumes Password's Enter before the form's AcceptButton can submit.
        FormFlow.OrderTableFields(root);
        if (checkConnectionOnStartup)
        {
            var connection = new DatabaseConnectionView(this, view =>
            {
                if (!(completeFirstRun ?? CompleteFirstRun)())
                {
                    DialogResult = DialogResult.Cancel;
                    return;
                }

                AcceptButton = login;
                CancelButton = null;
                content.Controls.Remove(view);
                view.Dispose();
                Text = "FMSC - Sign In";
                ClientSize = AppMetrics.StartupDialogClientSize;
                shell.Visible = true;
                user.Focus();
            }, connect, startupUpdates.Open);
            content.Controls.Add(connection);
            shell.Visible = false;
            connection.BringToFront();
            Text = DatabaseConnectionView.WindowTitle;
            AcceptButton = connection.AcceptAction;
            CancelButton = connection.CancelAction;
            FormClosing += (_, e) => { if (connection.Busy) e.Cancel = true; };
            Shown += async (_, _) => await connection.ConnectAsync(false);
        }
        else
        {
            Shown += (_, _) => user.Focus();
        }
        Disposed += (_, _) => validator.Dispose();
    }
    private bool CompleteFirstRun()
    {
        try
        {
            var state = DatabaseHelper.Query("GetFirstRunStatus");
            if (state.Rows.Count == 0 || !Convert.ToBoolean(state.Rows[0]["IsFirstRun"]))
                return true;

            using var setup = new FirstRunSetupForm();
            return setup.ShowDialog(this) == DialogResult.OK;
        }
        catch (Exception ex)
        {
            ModernMessageDialog.ShowError(this, DbMessage.UserMessage(ex), "FMSC Startup");
            return false;
        }
    }

    private sealed class JustifiedNote : Label
    {
        public JustifiedNote()
        {
            AutoSize = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int available = ClientSize.Width - Padding.Horizontal;
            if (available <= 0) return;
            int lineHeight = TextRenderer.MeasureText(e.Graphics, "Ag", Font, Size.Empty, flags).Height;
            int space = TextRenderer.MeasureText(e.Graphics, " ", Font, Size.Empty, flags).Width;
            string[] words = Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int index = 0;
            int y = Padding.Top;
            while (index < words.Length)
            {
                int start = index;
                int width = 0;
                var widths = new List<int>();
                while (index < words.Length)
                {
                    int wordWidth = TextRenderer.MeasureText(e.Graphics, words[index], Font, Size.Empty, flags).Width;
                    int nextWidth = width + (index > start ? space : 0) + wordWidth;
                    if (index > start && nextWidth > available) break;
                    width = nextWidth;
                    widths.Add(wordWidth);
                    index++;
                }
                // Justify wrapped lines; keep the final line naturally left aligned.
                float gap = index < words.Length && widths.Count > 1
                    ? (available - widths.Sum()) / (float)(widths.Count - 1)
                    : space;
                float x = Padding.Left;
                for (int word = start; word < index; word++)
                {
                    TextRenderer.DrawText(e.Graphics, words[word], Font, new Point((int)Math.Round(x), y), ForeColor, flags);
                    x += widths[word - start] + gap;
                }
                y += lineHeight;
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && user.ContainsFocus)
        {
            pass.Focus();
            pass.SelectAll();
            return true;
        }
        if (keyData == Keys.Enter && pass.ContainsFocus)
        {
            if (login.Enabled) login.PerformClick();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private static (bool Success, string Message, Exception? ConnectionFailure) AttemptSignIn(string username, string password)
    {
        if (!DatabaseHelper.TestConnection(out string message, out var failure))
            return (false, message, failure);
        bool success = AuthenticationService.Authenticate(username, password, out message, out failure);
        return (success, message, failure is not null && DatabaseConnectionForm.IsConnectionProblem(failure) ? failure : null);
    }
    async void Login_Click(object? s, EventArgs e)
    {
        /* Reject missing credentials before querying the database. */
        if (!login.Enabled)
            return;
        validator.Begin();
        validator.Required(user, "Enter your username");
        validator.Check(pass, pass.Text.Length > 0, "Enter your password", () => pass.Text.Length > 0);
        if (!validator.Finish())
            return;
        login.Enabled = false;
        // Capture credentials on the UI thread; neither SQL nor password hashing
        // should freeze the form, and the operation must not read live controls.
        string username = user.Text;
        string password = pass.Text;
        signingIn = true;
        user.Enabled = false;
        pass.Enabled = false;
        UseWaitCursor = true;
        string idleText = login.Text;
        login.Text = "Signing in…";
        bool focusPassword = false;
        try
        {
            var result = await Task.Run(() => signIn(username, password));
            if (IsDisposed) return;
            if (result.ConnectionFailure is not null)
            {
                using var connection = new DatabaseConnectionForm(startup: false);
                connection.ShowDialog(this);
                focusPassword = true;
                return;
            }
            if (result.Success)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            ModernMessageDialog.ShowWarning(this, result.Message, "Login");
            focusPassword = true;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Sign-in operation");
            if (!IsDisposed) ModernMessageDialog.ShowError(this, DbMessage.UserMessage(ex), "Sign In");
        }
        finally
        {
            signingIn = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                user.Enabled = true;
                pass.Enabled = true;
                if (focusPassword) { pass.Focus(); pass.SelectAll(); }
            }
            if (!login.IsDisposed)
            {
                login.Text = idleText;
                login.Enabled = true;
            }
        }
    }
}

