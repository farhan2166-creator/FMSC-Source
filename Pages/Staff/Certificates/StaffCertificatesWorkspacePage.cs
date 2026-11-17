using System.Data;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Certificates;

namespace FMSC_SMS.Pages.Staff.Certificates;

/// <summary>
/// One staff selection and one saved-copy history, with the original lecturer
/// and bank editors retained as the only owners of certificate mutations.
/// </summary>
public sealed class StaffCertificatesWorkspacePage : V2Page, IBusyPage, IUnsavedChanges
{
    private readonly StaffPicker staff = new();
    private readonly Func<int, DataTable> readRegister;
    private readonly Func<string> registerEndpoint;
    private readonly HumanCard registerCard;
    private readonly ModernComboBox typeFilter = HumanUi.Combo();
    private readonly SplitContainer split = new();
    private readonly Label selectedDetails = new()
    {
        Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText,
        Text = "Select a saved certificate to see its details and actions.",
        TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true
    };
    private readonly Label actionHint = new()
    {
        Dock = DockStyle.Top, Height = AppScale.Scale(94),
        AutoEllipsis = true, ForeColor = AppTheme.MutedText
    };
    private readonly TableLayoutPanel copyFacts = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false
    };
    private readonly Dictionary<string, Label> facts = new();
    private readonly List<ModernButton> newActions = new();
    private readonly ModernButton edit;
    private readonly ModernButton print;
    private readonly ModernButton collect;
    private readonly ModernButton reissue;
    private readonly ModernButton refresh;
    private DataTable records = StaffCertificateRegisterReader.CreateTable();
    private Panel? editorHost;
    private Control? activeEditor;
    private bool loading;
    private bool editorOpen;
    private bool arranging;
    private int generation;
    private int lastStaff;
    private CertificateWorkspaceSelection.Key? nextSelection;

    public StaffCertificatesWorkspacePage() : this(true) { }
    internal StaffCertificatesWorkspacePage(bool initialize, Func<int, DataTable>? readRegister = null,
        Func<string>? registerEndpoint = null)
        : base("Teacher / Staff Certificates",
            "Select a staff member to create, review, print or reissue certificates.")
    {
        if (!CurrentUser.HasPermission("Certificates.Create"))
            throw new UnauthorizedAccessException("Certificate access is required.");
        this.readRegister = readRegister ?? StaffCertificateRegisterReader.Read;
        this.registerEndpoint = registerEndpoint ?? (() => DatabaseHelper.DatabaseEndpoint);

        UseWrappingToolbar();
        foreach (var kind in StaffCertificateRegisterReader.Kinds)
        {
            ModernButton action = AddAction(kind.Caption, (_, _) => OpenEditor(kind, 0), "Secondary");
            action.Enabled = false;
            newActions.Add(action);
        }
        edit = new ModernButton { Text = "Edit Details", AutoSize = true, Height = AppMetrics.ButtonHeight };
        edit.Click += (_, _) => OpenSelected();
        print = new ModernButton { Text = "Print", AutoSize = true, Height = AppMetrics.ButtonHeight };
        print.Click += (_, _) => RunSelectedAction("print");
        collect = new ModernButton { Text = "Mark Collected", AutoSize = true, Height = AppMetrics.ButtonHeight };
        collect.Click += (_, _) => RunSelectedAction("collect");
        reissue = new ModernButton { Text = "Issue Again", AutoSize = true, Height = AppMetrics.ButtonHeight };
        reissue.Click += (_, _) => RunSelectedAction("reissue");
        refresh = AddBusyAction("Refresh", ReloadAsync, "Secondary");
        edit.Enabled = print.Enabled = collect.Enabled = reissue.Enabled = refresh.Enabled = false;

        staff.PlaceholderText = StaffPicker.SearchPlaceholder;
        staff.SearchFailed += Fail;
        staff.UserSelectionChanged += async (_, _) => await ReloadAsync();
        staff.SelectionCleared += (_, _) => ClearSelection();
        staff.SelectionChangeGuard = _ => !editorOpen && !loading;

        Grid.AutoGenerateColumns = false;
        Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Grid.ReadOnly = true;
        Grid.MultiSelect = false;
        Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Type", HeaderText = "Certificate Type",
            DataPropertyName = "Certificate Type", FillWeight = 35, MinimumWidth = 150
        });
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Date", HeaderText = "Certificate Date",
            DataPropertyName = "Certificate Date", FillWeight = 23, MinimumWidth = 104
        });
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Reference", HeaderText = "Reference",
            DataPropertyName = "Reference", FillWeight = 25, MinimumWidth = 120
        });
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Collection", HeaderText = "Collection",
            DataPropertyName = "Collection", FillWeight = 20, MinimumWidth = 95
        });
        Grid.SelectionChanged += (_, _) => UpdateSelected();
        Grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        Grid.DataSource = records.DefaultView;

        var searchCard = HumanUi.Card("Staff Member");
        searchCard.Content.Controls.Add(HumanUi.Field("Staff / Staff No.", staff, true));
        registerCard = HumanUi.Card("Saved Certificates");
        typeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        typeFilter.Items.Add("All certificate types");
        foreach (var kind in StaffCertificateRegisterReader.Kinds)
            typeFilter.Items.Add(kind.Caption);
        typeFilter.SelectedIndex = 0;
        typeFilter.Dock = DockStyle.Top;
        typeFilter.SelectedIndexChanged += (_, _) => ApplyTypeFilter();
        registerCard.Content.Controls.Add(Grid);
        registerCard.Content.Controls.Add(typeFilter);

        var selectedCard = HumanUi.Card("Selected Copy");
        var info = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        info.Controls.Add(selectedDetails);
        info.Controls.Add(actionHint);
        copyFacts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(92)));
        copyFacts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (key, caption) in new[]
        {
            ("Certificate Type", "Type"), ("Reference", "Reference"),
            ("Certificate Date", "Date"), ("Collection", "Collection"),
            ("Collected On", "Collected"), ("Recorded On", "Recorded")
        })
        {
            int row = copyFacts.RowCount++;
            copyFacts.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(24)));
            var label = new Label { Text = caption, Dock = DockStyle.Fill,
                ForeColor = AppTheme.MutedText, TextAlign = ContentAlignment.MiddleLeft };
            var value = new Label { Dock = DockStyle.Fill, ForeColor = AppTheme.Text,
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            copyFacts.Controls.Add(label, 0, row);
            copyFacts.Controls.Add(value, 1, row);
            facts.Add(key, value);
        }
        info.Controls.Add(copyFacts);
        selectedCard.Content.Controls.Add(info);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true,
            ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new[] { edit, print, collect, reissue };
        for (int i = 0; i < buttons.Length; i++)
        {
            actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buttons[i].AutoSize = false;
            buttons[i].Dock = DockStyle.Top;
            buttons[i].Margin = new Padding(0, 0, 0, AppMetrics.SpacingSm);
            actions.Controls.Add(buttons[i], 0, i);
        }
        selectedCard.Content.Controls.Add(actions);

        var sidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(132)));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchCard.Margin = new Padding(0, 0, 0, AppMetrics.SpacingSm);
        sidebar.Controls.Add(searchCard, 0, 0);
        sidebar.Controls.Add(selectedCard, 0, 1);
        split.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(sidebar);
        split.Panel2.Controls.Add(registerCard);
        split.FixedPanel = FixedPanel.Panel1;
        split.SizeChanged += (_, _) => ArrangeWorkspace();
        SetContent(split);
        SetStatus("Select a staff member to see available certificates.");
    }

    private IBusyPage? BusyEditor => activeEditor is IBusyPage page && page.IsBusy ? page : null;
    public bool IsBusy => loading || (editorOpen && activeEditor is null) || BusyEditor is not null;
    public string BusyMessage => BusyEditor?.BusyMessage ?? (loading
        ? "Wait for the staff certificate history to load."
        : "Wait for the certificate operation to finish.");
    public bool HasUnsavedChanges => activeEditor is IUnsavedChanges page && page.HasUnsavedChanges;
    public string UnsavedChangesMessage => (activeEditor as IUnsavedChanges)?.UnsavedChangesMessage ??
        "The certificate has changes that have not been confirmed.";

    private void ArrangeWorkspace()
    {
        if (arranging || split.Width < AppScale.Scale(200) || split.Height < AppScale.Scale(200)) return;
        arranging = true;
        try
        {
            split.Panel1MinSize = split.Panel2MinSize = 0;
            split.Orientation = Orientation.Vertical;
            int desired = Math.Max(AppScale.Scale(250), (int)(split.Width * .20));
            split.SplitterDistance = Math.Clamp(desired, 0, Math.Max(0, split.Width - split.SplitterWidth));
        }
        finally { arranging = false; }
    }

    private DataRowView? CurrentRecord => Grid.CurrentRow?.DataBoundItem as DataRowView;

    private static string Read(DataRow row, string field) =>
        row.Table.Columns.Contains(field) && !row.IsNull(field) &&
        !string.IsNullOrWhiteSpace(Convert.ToString(row[field])) ? Convert.ToString(row[field])! : "—";

    private void ApplyTypeFilter()
    {
        records.DefaultView.RowFilter = typeFilter.SelectedIndex <= 0 ? "" :
            "[Certificate Type] = '" + Convert.ToString(typeFilter.SelectedItem)!.Replace("'", "''") + "'";
        UpdateSelected();
    }

    private void ClearSelection()
    {
        nextSelection = null;
        lastStaff = 0;
        generation++;
        loading = false;
        records = StaffCertificateRegisterReader.CreateTable();
        Grid.DataSource = records.DefaultView;
        selectedDetails.Text = "Select a staff member to review saved certificates.";
        UpdateSelected();
    }

    private async Task ReloadAsync()
    {
        if (editorOpen) return;
        int requested = staff.SelectedId;
        if (requested <= 0 || staff.SelectedRow is null) { ClearSelection(); return; }
        var preferred = nextSelection ?? (lastStaff == requested
            ? CertificateWorkspaceSelection.FromRow(CurrentRecord) : null);
        nextSelection = null;
        lastStaff = requested;
        int request = ++generation;
        int actor = CurrentUser.UserId;
        bool permitted = CurrentUser.HasPermission("Certificates.Create");
        if (!permitted) { ClearSelection(); return; }
        string endpoint = registerEndpoint();
        records = StaffCertificateRegisterReader.CreateTable();
        Grid.DataSource = records.DefaultView;
        edit.Enabled = print.Enabled = collect.Enabled = reissue.Enabled = refresh.Enabled = false;
        selectedDetails.Text = "Loading saved certificates…";
        loading = true;
        try
        {
            DataTable loaded = await Task.Run(() => readRegister(requested));
            if (IsDisposed || request != generation || staff.SelectedId != requested ||
                CurrentUser.UserId != actor || registerEndpoint() != endpoint ||
                !CurrentUser.HasPermission("Certificates.Create")) return;
            records = loaded;
            ApplyTypeFilter();
            Grid.DataSource = records.DefaultView;
            SelectAfterRefresh(CertificateWorkspaceSelection.Resolve(records.DefaultView, preferred));
            selectedDetails.Text = records.Rows.Count == 0
                ? "No saved certificates for this staff member."
                : "Select a saved copy for details, printing, collection or reissue.";
            SetStatus(records.Rows.Count + " saved certificate(s) for the selected staff member.");
        }
        catch (Exception ex)
        {
            if (IsDisposed || request != generation) return;
            records = StaffCertificateRegisterReader.CreateTable();
            Grid.DataSource = records.DefaultView;
            selectedDetails.Text = "Certificate history could not be loaded. Refresh and try again.";
            Fail(ex);
        }
        finally
        {
            if (!IsDisposed && request == generation)
            {
                loading = false;
                refresh.Enabled = staff.SelectedId > 0;
                UpdateSelected();
            }
        }
    }

    private void SelectAfterRefresh(DataRowView? selected)
    {
        Grid.ClearSelection();
        try { Grid.CurrentCell = null; } catch (InvalidOperationException) { }
        if (selected is null) return;
        for (int i = 0; i < Grid.Rows.Count; i++)
        {
            if (Grid.Rows[i].DataBoundItem is not DataRowView candidate ||
                !ReferenceEquals(candidate.Row, selected.Row)) continue;
            DataGridViewCell? cell = Grid.Rows[i].Cells.Cast<DataGridViewCell>()
                .FirstOrDefault(item => item.OwningColumn.Visible);
            if (cell is null) return;
            Grid.CurrentCell = cell;
            Grid.Rows[i].Selected = true;
            return;
        }
    }

    private void UpdateSelected()
    {
        bool allowCreate = !loading && !editorOpen && staff.SelectedId > 0 &&
            CurrentUser.HasPermission("Certificates.Create");
        foreach (var button in newActions) button.Enabled = allowCreate;
        refresh.Enabled = allowCreate;
        var selected = CurrentRecord;
        bool available = allowCreate && selected is not null;
        int id = selected is null ? 0 : Convert.ToInt32(selected["RecordId"]);
        string? kind = selected is null ? null : Convert.ToString(selected["Kind"]);
        bool collected = selected is not null &&
            string.Equals(Convert.ToString(selected["Collection"]), "Collected", StringComparison.OrdinalIgnoreCase);
        bool successor = selected is not null && records.AsEnumerable().Any(row =>
            Convert.ToString(row["Kind"]) == kind &&
            Convert.ToInt32(row["SourceCopyId"]) == id && id > 0);
        edit.Enabled = available && id > 0;
        edit.Text = collected ? "View Details" : "Edit Details";
        print.Text = collected ? "Reprint" : "Print";
        print.Enabled = available && id > 0 && CurrentUser.HasPermission("Certificates.Print");
        collect.Visible = !collected;
        collect.Enabled = available && id > 0 && !collected;
        reissue.Visible = collected;
        reissue.Enabled = available && id > 0 && collected && !successor;
        actionHint.Visible = selected is not null;
        selectedDetails.Visible = selected is null;
        copyFacts.Visible = selected is not null;
        actionHint.Text = !available ? "Select a saved certificate." : collected
            ? (successor ? "This historical copy is locked. Reprint it or choose the latest copy."
                : "Collected copies are locked. Issue Again makes a separate copy.")
            : "Save any edits before printing. Mark Collected only after handing over the paper.";
        if (selected is null) return;
        foreach (var (name, value) in facts)
            value.Text = Read(selected.Row, name);
    }

    private bool TrySelected(out StaffCertificateRegisterReader.Kind kind, out int recordId)
    {
        kind = null!;
        recordId = 0;
        if (CurrentRecord is not DataRowView row || !CurrentUser.HasPermission("Certificates.Create")) return false;
        kind = StaffCertificateRegisterReader.Kinds.FirstOrDefault(item =>
            item.Key == Convert.ToString(row["Kind"]))!;
        if (kind is null) return false;
        recordId = Convert.ToInt32(row["RecordId"]);
        return recordId > 0;
    }

    private void OpenSelected()
    {
        if (loading || editorOpen || !TrySelected(out var kind, out int record)) return;
        OpenEditor(kind, record);
    }

    private static Control CreateEditor(string key) => key switch
    {
        "Lecturer" => new RegularLecturerCertificatePage(false),
        "Bank" => new BankAccountOpeningCertificatePage(false),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private void OpenEditor(StaffCertificateRegisterReader.Kind kind, int recordId)
    {
        if (loading || editorOpen || staff.SelectedId <= 0 ||
            !CurrentUser.HasPermission("Certificates.Create")) return;
        DataRow? selectedStaff = staff.SelectedRow;
        if (selectedStaff is null) { Warn("Select a staff member first."); return; }

        editorOpen = true;
        staff.Enabled = false;
        UpdateSelected();
        Panel? host = null;
        try
        {
            var editor = CreateEditor(kind.Key);
            activeEditor = editor;
            host = new Panel { Dock = DockStyle.Fill };
            editor.Dock = DockStyle.None;
            editor.Location = new Point(-10000, -10000);
            editor.Size = new Size(AppScale.Scale(1120), AppScale.Scale(780));
            host.Controls.Add(editor);
            var bridge = (IStaffCertificateWorkspaceEditor)editor;
            bridge.ConfigureWorkspaceDetails(selectedStaff, editing: recordId > 0);
            HumanCard card = bridge.DetailsCard;
            card.Parent?.Controls.Remove(card);
            card.Dock = DockStyle.Fill;
            card.MinimumSize = Size.Empty;
            var header = new Label
            {
                Dock = DockStyle.Top, Height = AppScale.Scale(65),
                Text = (recordId > 0 ? "Saved Certificate — " : "Prepare Certificate — ") +
                    kind.Caption + Environment.NewLine + "Staff: " +
                    CertificateUi.GetString(selectedStaff, "StaffName") + "  ·  " +
                    CertificateUi.GetString(selectedStaff, "StaffNumber"),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                ForeColor = AppTheme.HeadingText, Font = AppTheme.NormalFont
            };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = AppScale.Scale(95) };
            var back = new ModernButton
            {
                Text = "Back to Certificates", Dock = DockStyle.Right,
                Width = AppScale.Scale(190), Height = AppMetrics.ButtonHeight
            };
            back.Click += (_, _) => CloseEditor(saved: false);
            footer.Controls.Add(back);
            Label feedback = bridge.Feedback;
            feedback.Parent?.Controls.Remove(feedback);
            feedback.Dock = DockStyle.Fill;
            feedback.AutoEllipsis = true;
            footer.Controls.Add(feedback);
            host.Controls.Add(card);
            host.Controls.Add(footer);
            host.Controls.Add(header);
            split.Panel2.Controls.Add(host);
            editorHost = host;
            registerCard.Visible = false;
            host.BringToFront();
            host.CreateControl();
            editor.CreateControl();

            bridge.Saved = savedId =>
            {
                if (!editorOpen || IsDisposed || savedId <= 0) return;
                nextSelection = new CertificateWorkspaceSelection.Key(kind.Key, savedId, 0);
                BeginInvoke(new Action(() => { if (!IsDisposed && editorOpen) CloseEditor(saved: true); }));
            };
            // The original page's bound grid and controls must have handles before
            // selecting the exact stored certificate row.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || !editorOpen || editor.IsDisposed) return;
                try
                {
                    bridge.LoadWorkspaceSelection(selectedStaff, recordId);
                    FormFlow.FocusFirstInput(card);
                }
                catch (Exception ex) { Fail(ex); CloseEditor(saved: false, force: true); }
            }));
        }
        catch (Exception ex)
        {
            host?.Dispose();
            editorHost = null;
            activeEditor?.Dispose();
            activeEditor = null;
            editorOpen = false;
            staff.Enabled = true;
            registerCard.Visible = true;
            UpdateSelected();
            Fail(ex);
        }
    }

    private void CloseEditor(bool saved, bool force = false)
    {
        if (!editorOpen) return;
        if (!force)
        {
            if (IsBusy) { Warn(BusyMessage); return; }
            if (HasUnsavedChanges &&
                !ModernMessageDialog.ConfirmDiscardIfDirty(FindForm(), () => HasUnsavedChanges,
                    UnsavedChangesMessage)) return;
        }
        editorOpen = false;
        editorHost?.Dispose();
        editorHost = null;
        activeEditor = null;
        staff.Enabled = true;
        registerCard.Visible = true;
        if (saved) _ = ReloadAsync();
        else { UpdateSelected(); Grid.Focus(); }
    }

    private void RunSelectedAction(string action)
    {
        if (loading || editorOpen || !TrySelected(out var kind, out int recordId)) return;
        DataRow? selectedStaff = staff.SelectedRow;
        if (selectedStaff is null || staff.SelectedId <= 0) return;
        int actor = CurrentUser.UserId;
        string endpoint = registerEndpoint();
        editorOpen = true;
        staff.Enabled = false;
        Grid.Enabled = false;
        UpdateSelected();
        Control? editor = null;
        try
        {
            editor = CreateEditor(kind.Key);
            activeEditor = editor;
            editor.Dock = DockStyle.None;
            editor.Location = new Point(-10000, -10000);
            editor.Size = new Size(AppScale.Scale(1100), AppScale.Scale(780));
            Controls.Add(editor);
            editor.CreateControl();
            var workflow = (IStaffCertificateWorkspaceEditor)editor;
            workflow.LoadWorkspaceSelection(selectedStaff, recordId);
            if (actor != CurrentUser.UserId || endpoint != registerEndpoint() ||
                !CurrentUser.HasPermission("Certificates.Create"))
                throw new UnauthorizedAccessException("Certificate access changed. Refresh the workspace.");
            switch (action)
            {
                case "print":
                    if (!workflow.CanPrintFromWorkspace)
                        throw new InvalidOperationException("This certificate cannot currently be printed.");
                    workflow.PrintFromWorkspace();
                    break;
                case "collect":
                    if (!workflow.CanCollectFromWorkspace)
                        throw new InvalidOperationException("This copy is not eligible for collection.");
                    workflow.CollectFromWorkspace();
                    break;
                case "reissue":
                    if (!workflow.CanIssueAgainFromWorkspace)
                        throw new InvalidOperationException("Select the latest eligible collected copy.");
                    workflow.IssueAgainFromWorkspace();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
            int selectedId = workflow.SelectedRecordId;
            nextSelection = new CertificateWorkspaceSelection.Key(kind.Key,
                selectedId > 0 ? selectedId : recordId, 0, PreferNewest: action == "reissue");
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            if (editor is not null)
            {
                Controls.Remove(editor);
                editor.Dispose();
            }
            activeEditor = null;
            editorOpen = false;
            if (!IsDisposed)
            {
                Grid.Enabled = true;
                staff.Enabled = true;
                if (actor == CurrentUser.UserId && endpoint == registerEndpoint())
                    _ = ReloadAsync();
                else ClearSelection();
            }
        }
    }
}
