using System.Globalization;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Core;
public partial class MasterPage : V2Page, IUnsavedChanges, IBusyPage
{
    public bool IsBusy => _saving || _deleting;
    public string BusyMessage => _deleting ? "Please wait for the record to finish deleting." : "Please wait for the record to finish saving.";
    protected virtual bool CanChangeRecord() => true;
    protected virtual bool CanSaveRecord() => true;
    protected virtual bool ReserveEditorValidationSpace => false;
    protected virtual int EditorFieldGap => UiDesignSystem.Spacing.Sm;
    protected virtual int EditorInputVerticalInset => UiDesignSystem.Spacing.Xs;
    protected virtual int EditorValidationLines => 1;
    protected virtual bool EditorValidationSpansCaption => false;
    private int EditorValidationHeight => ReserveEditorValidationSpace ? AppMetrics.TextHeight(AppTheme.SmallFont) * EditorValidationLines + 4 : 0;
    private readonly Dictionary<Control, Label> _editorValidationLabels = new();
    private TableLayoutPanel? _stackedEditorLayout;
    private bool _compactCatalogEditor;
    private bool _sizingCatalogEditor;
    protected void BindEditorValidationMessages(FormValidator validator)
    {
        foreach (var pair in _editorValidationLabels)
            validator.BindMessageLabel(pair.Key, pair.Value);
    }
    protected virtual void LayoutRecordColumns() => MasterGridLayout.Apply(Grid, IsBooleanGridColumn);
    protected int EditorRecordId => _id;
    protected virtual async Task<int> SaveRecordAsync(SqlParameter[] parameters)
    {
        await DatabaseHelper.ExecuteUserAsync(_saveProc, parameters);
        return _id;
    }
    protected virtual void RecordLoaded(DataGridViewRow row) { }
    protected virtual void EditorCleared(bool afterSave) { }
    protected virtual void RecordsLoaded(DataTable table) { }
    protected void ShowRecordsTab(bool showRecords)
    {
        if (_entryRecordsTabs is not null)
            _entryRecordsTabs.SelectedIndex = showRecords ? 1 : 0;
    }
    protected bool EditRecordById(int id)
    {
        if (!_saving && HasUnsavedChanges && id > 0 && id == _id)
        {
            ShowRecordsTab(false);
            FocusFirstEditorField();
            return true;
        }
        if (_recordSearchBox is not null) _recordSearchBox.Clear();
        _staffRecordPicker?.Clear();
        ApplyRecordSearch();
        foreach (DataGridViewRow row in Grid.Rows)
        {
            if (row.IsNewRow || Convert.ToString(FindRowId(row)) != id.ToString(CultureInfo.InvariantCulture)) continue;
            if (!LoadRow(row)) return false;
            ShowRecordsTab(false);
            return true;
        }
        return false;
    }
    protected bool ConfirmEditorRecordChange() => ConfirmDiscardChanges();
    protected void AddEditorHeaderIdentity(Label identity)
    {
        if (_editorCard is null) return;
        identity.Dock = DockStyle.Right;
        _editorCard.Header.Controls.Add(identity);
        _editorCard.Header.Controls.SetChildIndex(identity, _editorCard.Header.Controls.Count - 1);
    }
    protected void ReplaceEditorInputWithFinder(string key, Control finder, string caption)
    {
        if (_editorCard is null || !_controls.TryGetValue(key, out var input) || input.Parent is not TableLayoutPanel field) return;
        var position = field.GetPositionFromControl(input);
        int span = field.GetColumnSpan(input);
        finder.Dock = input.Dock;
        finder.Margin = input.Margin;
        finder.TabIndex = input.TabIndex;
        field.Controls.Remove(input);
        input.Visible = false;
        _editorCard.Content.Controls.Add(input);
        field.Controls.Add(finder, position.Column, position.Row);
        field.SetColumnSpan(finder, span);
        if (field.Controls.OfType<Label>().FirstOrDefault(label => Equals(label.Tag, UiHelper.FieldCaptionTag)) is Label title)
            title.Text = caption;
        UiAccessibility.Describe(finder, caption);
    }
    protected void AddEditorSection(Control section)
    {
        if (_editor?.Parent is not Panel content) return;
        section.Dock = DockStyle.Top;
        content.Controls.Add(section);
        section.BringToFront();
    }
    private void RestoreEditorSelection()
    {
        Grid.ClearSelection();
        foreach (DataGridViewRow row in Grid.Rows)
        {
            if (row.IsNewRow || Convert.ToString(FindRowId(row)) != _id.ToString(CultureInfo.InvariantCulture)) continue;
            DataGridViewCell? cell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(candidate => candidate.Visible);
            if (cell is not null) { Grid.CurrentCell = cell; row.Selected = true; }
            return;
        }
        Grid.CurrentCell = null;
    }
    private readonly string _getProc;
    private readonly string _saveProc;
    private readonly Func<Dictionary<string, object?>, SqlParameter[]> _buildParams;
    private readonly Dictionary<
    string,
    List<DataRow>> _lookupRows =
    new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _manageButtons =
    new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _controls =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lookupVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FieldSpec[] _fields;
    private readonly Func<SqlParameter[]>? _getParams;
    private readonly bool _splitEditorAndRecords;
    private readonly string? _reorderProcedure;
    private readonly string? _reorderScopeField;
    private readonly string? _reorderItemField;
    private readonly string? _guidance;
    private readonly string? _deleteProcedure;
    private readonly string? _deleteIdParameter;
    private readonly string? _deleteDisplayField;
    private readonly string? _usageCountProcedure;
    private readonly string? _usageCountIdParameter;
    private readonly string? _usageCountLabel;
    /* A procedure returning one INT column: the ids of the records the delete procedure would REFUSE (assigned, configured, billed, ...). */
    private readonly string? _deleteBlockedProcedure;
    /* Null until the first successful read, and again if the read fails. */
    private HashSet<int>? _deleteBlockedIds;
    /* The permission the save procedure requires, when the page's navigation permission is weaker than it (the Staff pages are reached with Staff.View but save through procedures that require Staff.Manage). */
    private readonly string? _savePermission;
    /* The Save button, held so it can be disabled while a save is in flight. */
    private ModernButton? _saveButton;
    private bool _saving;
    private bool _deleting;
    private int _id;
    private readonly string? _recordIdColumn;
    private bool _updatingGridBoolean;
    private bool _gridLayoutPending;
    private readonly List<(Control Control, bool Multiline, bool StartNewRow, bool SpanRow)> _editorFields = new();
    /// <summary>The editor values as they were when the record was loaded, cleared or saved.</summary>
    private readonly MasterEditorChangeTracker _changeTracker;
    private bool IsSetupForm => GetType().Namespace?.StartsWith("FMSC_SMS.Pages.Setup", StringComparison.Ordinal) == true;
    bool IUnsavedChanges.HasUnsavedChanges => !IsSetupForm && HasUnsavedChanges;
    public bool HasUnsavedChanges => _changeTracker.HasChanges;
    public string UnsavedChangesMessage =>
        "This record has unsaved changes.\r\n\r\nIf you leave now, those changes will be lost.";
    private void CaptureEditorBaseline()
    {
        _changeTracker.Capture();
    }
    /// <summary>True when it is safe to replace what is on screen. Uses the same confirmation the navigation guard uses, so leaving a page and switching records within it behave identically.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (_saving || !CanChangeRecord())
            return false;
        if (IsSetupForm || !HasUnsavedChanges)
            return true;
        return ModernMessageDialog.Confirm(
            FindForm(),
            UnsavedChangesMessage,
            UiText.Dialogs.UnsavedChanges);
    }
    private TableLayoutPanel? _editor;
    private int _originalEditorColumns;
    private int? _editorBaselineWidth;
    /* The editor card is held so LoadRow/Clear can re-title its header in place with the record and the mode. */
    protected virtual string NewRecordHeaderTitle => "New Record";
    protected virtual string NewRecordHeaderSubtitle => "Enter the information below.";
    protected virtual string EntryTabCaption => "Entry";
    protected virtual string RecordsTabCaption => "Records";
    private HumanCard? _editorCard;
    private HumanCard? _recordsCard;
    private Control? _recordsToolbar;
    private bool _recordsSearchHidden;
    internal void HideRecordsChrome()
    {
        if (_recordsCard is null) return;
        _recordsSearchHidden = true;
        _recordsCard.Header.Visible = false;
        if (_recordsToolbar is not null) _recordsToolbar.Visible = false;
        _recordsCard.Content.Padding = Padding.Empty;
    }
    private TabControl? _entryRecordsTabs;
    private DataTable? _recordsTable;
    private ModernTextBox? _recordSearchBox;
    private StaffPicker? _staffRecordPicker;
    private Label? _recordCountLabel;
    private System.Windows.Forms.Timer? _recordSearchTimer;
    private readonly MasterReloadSession _reloadSession = new();
    public MasterPage(
        string title,
        string context,
        string getProc,
        string saveProc,
        FieldSpec[] fields,
        Func<Dictionary<string, object?>, SqlParameter[]> buildParams,
        Func<SqlParameter[]>? getParams = null,
        bool splitEditorAndRecords = false,
        string? reorderProcedure = null,
        string? reorderScopeField = null,
        string? reorderItemField = null,
        string? guidance = null,
        string? deleteProcedure = null,
        string? deleteIdParameter = null,
        string? deleteDisplayField = null,
        string? usageCountProcedure = null,
        string? usageCountIdParameter = null,
        string? usageCountLabel = null,
        string? savePermission = null,
        string? deleteBlockedProcedure = null,
        string? recordIdColumn = null,
        bool separateEntryAndRecords = false,
        bool editorOnly = false,
        bool initialize = true)
        : base(title, context)
    {
        _changeTracker = new MasterEditorChangeTracker(Values);
        _getProc = getProc;
        _recordIdColumn = recordIdColumn;
        _saveProc = saveProc;
        _fields = fields;
        _buildParams = buildParams;
        _getParams = getParams;
        _splitEditorAndRecords = splitEditorAndRecords;
        _reorderProcedure = reorderProcedure;
        _reorderScopeField = reorderScopeField;
        _reorderItemField = reorderItemField;
        _guidance = guidance;
        _deleteProcedure = deleteProcedure;
        _deleteIdParameter = deleteIdParameter;
        _deleteDisplayField = deleteDisplayField;
        _usageCountProcedure = usageCountProcedure;
        _usageCountIdParameter = usageCountIdParameter;
        _usageCountLabel = usageCountLabel;
        _savePermission = savePermission;
        _deleteBlockedProcedure = deleteBlockedProcedure;
        ValidateReorderConfiguration();
        ValidateDeleteConfiguration();
        /* A bare "Save" does not say what is being saved. Kept short by construction, so the existing action-bar width fitting still applies unchanged. */
        string saveActionText = "Save " + MasterEntityNaming.Singularize(NavigationTitle);
        var save = AddAction(saveActionText, Save, "Success");
        /* BD-25: New Record deliberately discards its draft without confirmation.
           Record switching and leaving keep their own unsaved-change safeguards. */
        string clearActionText = "New Record";
        var clear = AddAction(clearActionText, (_, _) => {
            if (_saving || !CanChangeRecord())
                return;
            Clear();
            CaptureEditorBaseline();
        }, "Secondary");
        SetToolTip(clear, "Clear entry fields and start a new record. Unsaved edits will be discarded.");
        clear.AccessibleDescription = "Clear entry fields and start a new record. Unsaved edits will be discarded.";
        // This editor's Clear action also starts a new record. Register that intent
        // explicitly rather than treating every Clear Filters button as New.
        if (!separateEntryAndRecords)
            ShortcutController.RegisterShortcut(clear, Keys.Control | Keys.N, "Ctrl+N", clear.PerformClick);
        /* SetActionIcon must use the button's current caption. */
        SetActionIcon(saveActionText, "✓");
        SetActionIcon(clearActionText, "↺");
        _saveButton = save;
        /* hide Save when the save procedure would refuse this user. Pages whose navigation permission already matches pass nothing and are unaffected. */
        if (!string.IsNullOrWhiteSpace(_savePermission))
            GateAction(save, _savePermission!);
        RelocateFromActions(save, clear);
        var editorCard = HumanUi.Card(
            NewRecordHeaderTitle,
            NewRecordHeaderSubtitle,
            Accent);
        _editorCard = editorCard;
        var editor = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            AutoSize = false,
            RowCount = 0,
            Padding = new Padding(0, 2, 0, 0),
            Margin = new Padding(0)
        };
        foreach (var hiddenField in _fields.Where(field =>
                     IsOperatorHidden(field)))
        {
            var hiddenControl = CreateControl(hiddenField);
            hiddenControl.Visible = false;
            _controls[hiddenField.Key] = hiddenControl;
        }
        int editorColumns = 2;
        editor.ColumnCount = editorColumns;
        for (int column = 0; column < editorColumns; column++)
        {
            editor.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    100F / editorColumns));
        }
        int fieldIndex = 0;
        int editorRowsHeight = 0;
        FieldSpec[] visibleFields = _fields
    .Where(field =>
        !IsOperatorHidden(field) &&
        !IsDisplayOrder(field))
    .ToArray();
        while (fieldIndex < visibleFields.Length)
        {
            int row = editor.RowCount++;
            FieldSpec firstField = visibleFields[fieldIndex];
            bool firstIsMultiline = firstField.Multiline;
            var rowFields = firstIsMultiline
                ? new[] { firstField }
                : visibleFields
                    .Skip(fieldIndex)
                    .Take(editorColumns)
                    .ToArray();
            fieldIndex += rowFields.Length;
            int rowHeight =
                rowFields.Any(field => field.Multiline)
                    ? AppMetrics.MultilineInputHeight(72) + EditorFieldGap + (EditorInputVerticalInset * 2)
                    : AppMetrics.InputControlHeight + EditorFieldGap + (EditorInputVerticalInset * 2);
            rowHeight += EditorValidationHeight;
            editorRowsHeight += rowHeight;
            editor.RowStyles.Add(
                new RowStyle(
                    SizeType.Absolute,
                    rowHeight));
            for (int column = 0;
                 column < rowFields.Length;
                 column++)
            {
                Control fieldControl =
                    CreateField(rowFields[column]);
                _editorFields.Add(
                    (fieldControl, rowFields[column].Multiline, false, false));
                editor.Controls.Add(
                    fieldControl,
                    column,
                    row);
                bool isLastFieldInRow =
                    column == rowFields.Length - 1;
                bool rowIsShort =
                    rowFields.Length < editorColumns;
                if (isLastFieldInRow && rowIsShort)
                {
                    editor.SetColumnSpan(
                        fieldControl,
                        editorColumns - column);
                }
            }
        }
        _editor = editor;
        _originalEditorColumns = editorColumns;
        editorCard.Content.Controls.Add(editor);
        var buttonRow = HumanUi.ButtonRow(save, clear);
        buttonRow.Dock = DockStyle.Top;
        editorCard.Content.Controls.Add(buttonRow);
        SetNaturalTabOrder(save, clear);
        int buttonRowHeight = AppMetrics.ButtonRowHeight;
        var gridCard = HumanUi.Card(
            "Records",
            HasReorderConfiguration
                ? "Use the ↑ and ↓ buttons to change the display order. Click a row to edit it."
                : "Previously saved records. Click a row to edit it.",
            Accent);
        var recordsToolbar = BuildRecordsToolbar();
        _recordsToolbar = recordsToolbar;
        _recordsCard = gridCard;
        gridCard.Content.Controls.Add(Grid);
        gridCard.Content.Controls.Add(recordsToolbar);
        Control host;
        if (editorOnly)
        {
            editorCard.Content.AutoScroll = true;
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            editorCard.Dock = DockStyle.Fill;
            // Keep the records grid alive for the existing load-by-ID and reload flow.
            gridCard.Visible = false;
            var editorHost = new Panel { Dock = DockStyle.Fill };
            editorHost.Controls.Add(gridCard);
            editorHost.Controls.Add(editorCard);
            host = editorHost;
        }
        else if (separateEntryAndRecords)
        {
            editorCard.Content.AutoScroll = true;
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            _entryRecordsTabs = new TabControl { Dock = DockStyle.Fill };
            TabStrip.Apply(_entryRecordsTabs, Accent);
            var entryTab = new TabPage(EntryTabCaption) { BackColor = AppTheme.Surface };
            var recordsTab = new TabPage(RecordsTabCaption) { BackColor = AppTheme.Surface };
            editorCard.Dock = gridCard.Dock = DockStyle.Fill;
            entryTab.Controls.Add(editorCard);
            recordsTab.Controls.Add(gridCard);
            _entryRecordsTabs.TabPages.AddRange(new[] { entryTab, recordsTab });
            _entryRecordsTabs.Selecting += (_, e) => { if (_saving) e.Cancel = true; };
            host = TabStrip.CreateHost(_entryRecordsTabs);
        }
        else if (_splitEditorAndRecords)
        {
            editorCard.Content.AutoScroll = true;
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            host = BuildSplitPanels(editorCard, gridCard);
            editor.SizeChanged += OnEditorSizeChanged;
        }
        else
        {
            editorCard.Content.AutoScroll = true;
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            editor.SizeChanged += OnEditorSizeChanged;
            var stacked = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            int editorHeight = AppScale.Scale(58) + editorRowsHeight + buttonRowHeight;
            stacked.RowStyles.Add(
                new RowStyle(
                    SizeType.Absolute,
                    Math.Max(190, editorHeight)));
            stacked.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    100));
            stacked.Controls.Add(editorCard, 0, 0);
            stacked.Controls.Add(gridCard, 0, 1);
            _stackedEditorLayout = stacked;
            host = stacked;
        }
        SetContent(host, _guidance);
        FormFlow.EnterMovesToNext(this);
        CaptureEditorBaseline();
        if (initialize) Defer(async () => {
            await BindLookupsAsync();
            Reload();
            FocusFirstEditorField();
        });
        Grid.CurrentCellDirtyStateChanged += (_, _) => {
            if (!Grid.IsCurrentCellDirty ||
                Grid.CurrentCell is null)
            {
                return;
            }
            if (IsBooleanGridColumn(
                    Grid.CurrentCell.OwningColumn))
            {
                Grid.CommitEdit(
                    DataGridViewDataErrorContexts.Commit);
            }
        };
        Grid.CellValueChanged += (_, e) => {
            if (e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                _updatingGridBoolean)
            {
                return;
            }
            DataGridViewColumn column =
                Grid.Columns[e.ColumnIndex];
            if (!IsBooleanGridColumn(column))
                return;
            DataGridViewRow row =
                Grid.Rows[e.RowIndex];
            string columnName =
                column.Name;
            BeginInvoke(
                new Action(() => {
                    UpdateBooleanFromGrid(
                        row,
                        columnName);
                }));
        };
        Grid.SizeChanged += (_, _) => {
            ScheduleGridColumnLayout();
        };
        Grid.CellClick += (_, e) => {
            if (e.RowIndex < 0)
                return;
            if (e.ColumnIndex < 0 ||
                MasterGridLayout.IsInternal(Grid.Columns[e.ColumnIndex]) ||
                IsBooleanGridColumn(Grid.Columns[e.ColumnIndex]))
                return;
            if (ReopenCurrentEditor(Grid.Rows[e.RowIndex])) return;
            /* Clicking another row replaces the editor, which is a context switch like any other: if the operator has unsaved edits, ask before they are replaced. */
            if (!ConfirmDiscardChanges())
            {
                RestoreEditorSelection();
                return;
            }
            LoadRow(Grid.Rows[e.RowIndex]);
        };
        Grid.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Enter &&
                Grid.CurrentRow is not null)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (ReopenCurrentEditor(Grid.CurrentRow)) return;
                if (!ConfirmDiscardChanges())
                {
                    RestoreEditorSelection();
                    return;
                }
                LoadRow(Grid.CurrentRow);
            }
        };
        WireDeleteBlockedDisplay();
        Grid.CellContentClick += async (_, e) => {
            if (e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                (!HasReorderConfiguration &&
                 !HasDeleteConfiguration))
            {
                return;
            }
            string columnName =
                Grid.Columns[e.ColumnIndex].Name;
            if (columnName == "__Delete")
            {
                await DeleteRecordAtRow(e.RowIndex);
            }
            else if (columnName == "__OrderUp")
            {
                await MoveRecordAtRow(
                    e.RowIndex,
                    -1);
            }
            else if (columnName == "__OrderDown")
            {
                await MoveRecordAtRow(
                    e.RowIndex,
                    1);
            }
        };
        Grid.CellFormatting += (_, e) => {
            if (!HasReorderConfiguration ||
                e.RowIndex < 0 ||
                e.ColumnIndex < 0)
            {
                return;
            }
            if (Grid.Columns[e.ColumnIndex].Name is not
                ("__OrderUp" or "__OrderDown"))
            {
                return;
            }
            int first = FirstOrderRowIndex();
            int last = LastOrderRowIndex();
            string columnName =
                Grid.Columns[e.ColumnIndex].Name;
            if (columnName == "__OrderUp")
            {
                e.Value =
                    e.RowIndex == first
                        ? string.Empty
                        : "↑";
            }
            else
            {
                e.Value =
                    e.RowIndex == last
                        ? string.Empty
                        : "↓";
            }
            e.FormattingApplied = true;
        };
    }
    private void SetNaturalTabOrder(
    Control save,
    Control clear)
    {
        int tabIndex = 0;
        foreach (var field in _fields)
        {
            if (IsOperatorHidden(field) ||
                IsDisplayOrder(field))
            {
                continue;
            }
            if (!_controls.TryGetValue(
                    field.Key,
                    out var control))
            {
                continue;
            }
            control.TabIndex = tabIndex++;
            if (_manageButtons.TryGetValue(
                    field.Key,
                    out var manageButton))
            {
                manageButton.TabIndex = tabIndex++;
            }
        }
        save.TabIndex = tabIndex++;
        clear.TabIndex = tabIndex++;
    }
    private void FocusFirstEditorField()
    {
        Defer(() => {
            if (IsDisposed || !Visible) return;
            foreach (var field in _fields)
            {
                if (!IsOperatorHidden(field) && !IsDisplayOrder(field) &&
                    _controls.TryGetValue(field.Key, out var control) &&
                    MasterEditorFocusNavigator.FocusInside(control)) return;
            }
        });
    }
    protected bool TryGetEditorControl(
    string fieldKey,
    out Control? control)
    {
        return _controls.TryGetValue(
            fieldKey,
            out control);
    }
    /// <summary>Keep a context-supplied value without presenting a second selector.</summary>
    protected void HideContextEditorField(string key)
    {
        if (!_controls.TryGetValue(key, out var input) || _editor is null || _editorCard is null) return;
        var field = _editorFields.FirstOrDefault(entry => entry.Control.Contains(input)).Control;
        if (field is null) return;
        _editorFields.RemoveAll(entry => ReferenceEquals(entry.Control, field));
        field.Visible = false;
        _editorCard.Content.Controls.Add(field);
        RebuildEditorColumns(_editor.ColumnCount);
    }
    protected int Id => _id;
}
