using System.Globalization;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

public partial class MasterPage
{
    // Catalog dialogs share this compact layout; other master editors retain
    // their established split/tab sizing and reserved validation rows.
    internal void UseCompactCatalogEditor()
    {
        if (_compactCatalogEditor || _stackedEditorLayout is null || _editorCard is null || _editor is null) return;
        _compactCatalogEditor = true;
        _editor.AutoSize = false;
        _stackedEditorLayout.SizeChanged += (_, _) => SizeCatalogEditor();
        _editorCard.Content.Layout += (_, _) => SizeCatalogEditor();
        foreach (var label in _editorValidationLabels.Values)
        {
            label.VisibleChanged += (_, _) => SizeCatalogEditor();
            label.TextChanged += (_, _) => SizeCatalogEditor();
        }
        SizeCatalogEditor();
        // The button row gets its final preferred height only after the dialog
        // has laid out at its real width, rather than the control's initial size.
        Defer(SizeCatalogEditor);
    }

    private void SizeCatalogEditor()
    {
        if (!_compactCatalogEditor || _sizingCatalogEditor || _editor is null || _editorCard is null || _stackedEditorLayout is null) return;
        _sizingCatalogEditor = true;
        try
        {
            foreach (var label in _editorValidationLabels.Values)
                if (label.Parent is TableLayoutPanel field)
                    field.RowStyles[1].Height = label.Visible && label.Text.Length > 0 ? EditorValidationHeight : 0;
            for (int row = 0; row < _editor.RowCount; row++)
            {
                var fields = _editorFields.Where(field => _editor.GetRow(field.Control) == row).ToArray();
                int validation = _editorValidationLabels.Values.Where(label => fields.Any(field => ReferenceEquals(label.Parent, field.Control)))
                    .Select(label => label.Visible && label.Text.Length > 0 ? EditorValidationHeight : 0).DefaultIfEmpty().Max();
                int input = fields.Any(field => field.Multiline) ? AppMetrics.MultilineInputHeight(72) : AppMetrics.InputControlHeight;
                _editor.RowStyles[row].Height = input + EditorFieldGap + EditorInputVerticalInset * 2 + validation;
            }
            // Row styles are authoritative. AutoSize/GetPreferredSize can retain
            // the old reserved-error height for a layout pass and create a scrollbar.
            _editor.Height = (int)Math.Ceiling(_editor.RowStyles.Cast<RowStyle>().Sum(row => row.Height)) + _editor.Padding.Vertical;
            int bodyHeight = _editorCard.Content.Padding.Vertical;
            foreach (Control control in _editorCard.Content.Controls)
                if (control.Dock == DockStyle.Top)
                    bodyHeight += ReferenceEquals(control, _editor) ? _editor.Height
                        : control.GetPreferredSize(new Size(_editorCard.Content.ClientSize.Width, 0)).Height;
            int wanted = _editorCard.Header.Height + _editorCard.Padding.Vertical + bodyHeight + 2;
            int available = Math.Max(0, _stackedEditorLayout.ClientSize.Height - AppScale.Scale(140));
            if (available <= 0) return;
            int height = Math.Min(wanted, available);
            _editorCard.Content.AutoScroll = wanted > available;
            _stackedEditorLayout.RowStyles[0].Height = height;
            _stackedEditorLayout.PerformLayout();
        }
        finally { _sizingCatalogEditor = false; }
    }
    private static bool IsOperatorHidden(FieldSpec field)
    {
        if (field.Kind == FieldKind.ComboString &&
            !string.IsNullOrWhiteSpace(field.LookupProcedure))
        {
            return false;
        }
        return field.Key.EndsWith(
            "Code",
            StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsDisplayOrder(FieldSpec field) =>
        string.Equals(
            field.Key,
            "DisplayOrder",
            StringComparison.OrdinalIgnoreCase);
    private void OnEditorSizeChanged(object? sender, EventArgs e)
    {
        if (_editor is null)
            return;
        int width = _editor.ClientSize.Width;
        if (width <= 0)
            return;
        if (_editorBaselineWidth is null)
            _editorBaselineWidth = width;
        ReflowEditorIfNeeded(width);
    }
    private void ReflowEditorIfNeeded(int width)
    {
        if (_editor is null ||
            _editorBaselineWidth is null ||
            _editorFields.Count == 0)
        {
            return;
        }
        int minColumnWidth =
            AppScale.Scale(300, AppScale.ControlSizeScale);
        int columnsThatFit =
            Math.Max(1, width / minColumnWidth);
        int columns = Math.Min(
            _originalEditorColumns,
            columnsThatFit);
        if (columns == _editor.ColumnCount)
            return;
        RebuildEditorColumns(columns);
    }
    private void RebuildEditorColumns(int columns)
    {
        if (_editor is null)
            return;
        _editor.SuspendLayout();
        // The controls in _editorFields are the actual field controls that must be reused when the editor changes between one and two columns.
        _editor.Controls.Clear();
        _editor.ColumnStyles.Clear();
        _editor.RowStyles.Clear();
        _editor.RowCount = 0;
        _editor.ColumnCount = columns;
        for (int column = 0; column < columns; column++)
        {
            _editor.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F / columns));
        }
        int fieldIndex = 0;
        while (fieldIndex < _editorFields.Count)
        {
            int row = _editor.RowCount++;
            var firstField = _editorFields[fieldIndex];
            // A field can start its own full-width row without pretending to
            // be a multiline input. Preserve the preceding field's column
            // width when an explicit row break follows it.
            var rowFields = firstField.Multiline || firstField.SpanRow
                ? new[] { firstField }
                : _editorFields
                    .Skip(fieldIndex)
                    .Take(columns)
                    .TakeWhile((field, index) =>
                        index == 0 || !(field.StartNewRow || field.Multiline || field.SpanRow))
                    .ToArray();
            fieldIndex += rowFields.Length;
            int rowHeight =
                rowFields.Any(field => field.Multiline)
                    ? AppMetrics.MultilineInputHeight(72) + EditorFieldGap + (EditorInputVerticalInset * 2)
                    : AppMetrics.InputControlHeight + EditorFieldGap + (EditorInputVerticalInset * 2);
            _editor.RowStyles.Add(
                new RowStyle(SizeType.Absolute, rowHeight + EditorValidationHeight));
            for (int column = 0;
                 column < rowFields.Length;
                 column++)
            {
                Control fieldControl = rowFields[column].Control;
                _editor.Controls.Add(fieldControl, column, row);
                bool isLastFieldInRow =
                    column == rowFields.Length - 1;
                bool rowIsShort =
                    rowFields.Length < columns;
                bool shouldSpan = firstField.Multiline || firstField.SpanRow ||
                    fieldIndex == _editorFields.Count ||
                    !_editorFields[fieldIndex].StartNewRow;
                if (isLastFieldInRow && rowIsShort && shouldSpan)
                {
                    _editor.SetColumnSpan(
                        fieldControl,
                        columns - column);
                }
            }
        }
        _editor.ResumeLayout(true);
        SizeCatalogEditor();
    }
    /// <summary>
    /// Keep related boolean controls on one caption-free row, aligned with the
    /// input column of another field. The original checkbox instances remain
    /// in _controls, so data binding, validation and dirty tracking are unchanged.
    /// </summary>
    protected void PlaceBooleanOptionsBelow(string anchorKey, params string[] optionKeys)
    {
        if (_editor is null ||
            !_controls.TryGetValue(anchorKey, out var anchor) ||
            anchor.Parent is not TableLayoutPanel anchorPanel ||
            optionKeys.Length == 0)
            throw new InvalidOperationException("The editor checkbox layout requires an existing anchor input.");

        var options = optionKeys.Select(key =>
        {
            if (!_controls.TryGetValue(key, out var control) ||
                control is not ModernCheckBox checkBox ||
                control.Parent is not TableLayoutPanel fieldPanel ||
                !_editorFields.Any(item => ReferenceEquals(item.Control, fieldPanel)))
                throw new InvalidOperationException($"Checkbox field '{key}' was not found in the editor.");
            return (CheckBox: checkBox, FieldPanel: fieldPanel);
        }).ToArray();

        var group = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, EditorFieldGap),
            Padding = Padding.Empty,
            TabStop = false
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, anchorPanel.ColumnStyles[0].Width));
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var choices = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, EditorInputVerticalInset, 0, 0),
            TabStop = false
        };
        foreach (var (checkBox, fieldPanel) in options)
        {
            fieldPanel.Controls.Remove(checkBox);
            _editor.Controls.Remove(fieldPanel);
            _editorFields.RemoveAll(item => ReferenceEquals(item.Control, fieldPanel));
            _editorValidationLabels.Remove(checkBox);
            fieldPanel.Dispose();
            checkBox.Dock = DockStyle.None;
            checkBox.Margin = new Padding(0, 0, AppScale.Scale(24), 0);
            choices.Controls.Add(checkBox);
        }
        group.Controls.Add(choices, 1, 0);

        // Match the live caption width, including DPI/font adjustments.
        anchorPanel.Layout += (_, _) =>
        {
            float left = anchorPanel.ColumnStyles[0].Width;
            if (Math.Abs(group.ColumnStyles[0].Width - left) > 0.5f)
                group.ColumnStyles[0].Width = left;
        };
        int anchorIndex = _editorFields.FindIndex(item =>
            ReferenceEquals(item.Control, anchorPanel));
        if (anchorIndex < 0)
            throw new InvalidOperationException("The checkbox anchor is not in the editor.");
        _editorFields.Insert(anchorIndex + 1, (group, false, true, true));
        RebuildEditorColumns(_editor.ColumnCount);
    }

    private Control CreateField(FieldSpec field)
    {
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(
                0,
                0,
                0,
                EditorFieldGap),
            Padding = new Padding(0),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            TabStop = false
        };
        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                AppScale.Scale(
                    145,
                    AppScale.ControlSizeScale)));
        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100F));
        panel.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                field.ManageDialogFactory is null
                    ? 0
                    : AppMetrics.ControlHeight));
        var caption = new Label {
            Text = field.Required
                ? $"{field.Caption} *"
                : field.Caption,
            Dock = DockStyle.Fill,
            Font = AppTheme.FieldCaptionFont,
            ForeColor = AppTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(
                UiDesignSystem.Spacing.Xs,
                0,
                UiDesignSystem.Spacing.Md,
                0),
            AutoEllipsis = true,
            Margin = new Padding(0),
            TabStop = false,
            Tag = UiHelper.FieldCaptionTag
        };
        void FitCaption() => panel.ColumnStyles[0].Width = Math.Max(
            AppScale.Scale(145, AppScale.ControlSizeScale),
            TextRenderer.MeasureText(caption.Text, caption.Font).Width + caption.Padding.Horizontal);
        caption.FontChanged += (_, _) => FitCaption();
        caption.TextChanged += (_, _) => FitCaption();
        FitCaption();
        Control control = CreateControl(field);
        RecordTextPolicy.WireInput(control, "@" + field.Key);
        UiAccessibility.Describe(
            control,
            field.Caption,
            field.Required ? "Required" : null);
        HumanUi.ApplyFieldInputLayout(control);
        control.Margin = new Padding(
            0,
            EditorInputVerticalInset,
            0,
            EditorInputVerticalInset);
        control.TabStop = true;
        _controls[field.Key] = control;
        panel.Controls.Add(
            caption,
            0,
            0);
        if (ReserveEditorValidationSpace)
        {
            panel.RowCount = 2;
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, EditorValidationHeight));
            var message = new Label {
                Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = AppTheme.SmallFont, ForeColor = AppTheme.Danger,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
                Visible = false, AccessibleRole = AccessibleRole.Alert
            };
            panel.Controls.Add(message, EditorValidationSpansCaption ? 0 : 1, 1);
            panel.SetColumnSpan(message, EditorValidationSpansCaption ? 3 : 2);
            _editorValidationLabels[control] = message;
        }
        if (field.ManageDialogFactory is null)
        {
            panel.Controls.Add(
                control,
                1,
                0);
            panel.SetColumnSpan(
                control,
                2);
            return panel;
        }
        var manageButton = new ModernButton {
            Text = "+",
            Dock = DockStyle.Fill,
            Width = AppMetrics.ControlHeight,
            Height = AppMetrics.ControlHeight,
            ThemeRole = "Primary",
            Margin = new Padding(
                UiDesignSystem.Spacing.Xs,
                UiDesignSystem.Spacing.Xs,
                0,
                UiDesignSystem.Spacing.Xs),
            TabStop = true
        };
        manageButton.Click += async (_, _) =>
            await ManageLookup(field);
        _manageButtons[field.Key] =
            manageButton;
        panel.Controls.Add(
            control,
            1,
            0);
        panel.Controls.Add(
            manageButton,
            2,
            0);
        return panel;
    }
    private async Task ManageLookup(FieldSpec field)
    {
        if (field.ManageDialogFactory is null)
            return;
        if (!_controls.TryGetValue(field.Key, out var childControl))
            return;
        try
        {
            using Form dialog = field.ManageDialogFactory();
            var managed = dialog as IManagedLookupDialog;
            if (field.LookupParentField is { } parentField &&
                _controls.TryGetValue(parentField, out var contextControl) &&
                contextControl is ModernComboBox contextCombo)
                managed?.SetParentContext(Convert.ToString(contextCombo.SelectedItem));
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                return;
            int selectedId = managed?.SelectedId ?? 0;
            bool hasParent =
                !string.IsNullOrWhiteSpace(field.LookupParentField) &&
                _controls.TryGetValue(field.LookupParentField!, out var parentControl) &&
                parentControl is ModernComboBox;
            if (hasParent)
            {
                var parentCombo =
                    (ModernComboBox)_controls[field.LookupParentField!];
                string? parentValue = managed?.SelectedParentValue;
                int parentIndex =
                    string.IsNullOrWhiteSpace(parentValue)
                        ? -1
                        : parentCombo.Items.IndexOf(parentValue!);
                if (parentIndex >= 0 && parentCombo.SelectedIndex != parentIndex)
                    parentCombo.SelectedIndex = parentIndex;
                else
                    await RefreshDependentLookupAsync(field, parentCombo);
            }
            else if (!string.IsNullOrWhiteSpace(field.LookupProcedure))
            {
                DataTable table = await DatabaseHelper.QueryUserAsync(field.LookupProcedure!);
                BindLookupControl(field, childControl, table);
            }
            if (selectedId > 0 && childControl is LookupBox lookupBox)
                lookupBox.SelectedValue = selectedId;
        }
        catch (Exception ex) { Fail(ex); }
    }
    /* Fallback text lengths for MasterPage-generated editors.: this table was a SECOND, independent set of limits that disagreed both with the UI's own literals and with the schema. */
    private static readonly IReadOnlyDictionary<string, int> F1TextLengths =
        new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase) {
            ["Username"] = SchemaLengths.Username,
            ["Password"] = SchemaLengths.Username,
            ["CampusCode"] = SchemaLengths.CampusCode,
            ["CampusName"] = SchemaLengths.CampusName,
            ["CampusType"] = SchemaLengths.CampusType,
            ["AddressLine"] = SchemaLengths.AddressLine,
            ["City"] = SchemaLengths.City,
            ["ContactNumber"] = SchemaLengths.ContactNumber,
            ["Email"] = SchemaLengths.Email,
            ["YearName"] = SchemaLengths.YearName,
            ["ClassCode"] = SchemaLengths.ClassCode,
            ["ClassName"] = SchemaLengths.ClassName,
            ["SectionCode"] = SchemaLengths.SectionCode,
            ["SectionName"] = SchemaLengths.SectionName,
            ["SubjectCode"] = SchemaLengths.SubjectCode,
            ["SubjectName"] = SchemaLengths.SubjectName,
            ["Description"] = SchemaLengths.SubjectDescription,
            ["FeeTypeCode"] = SchemaLengths.FeeTypeCode,
            ["FeeTypeName"] = SchemaLengths.FeeTypeName,
            // No matching schema column - feature not present in this schema.
            ["DocumentCode"] = 30,
            ["DocumentName"] = 100,
            // No matching schema column - feature not present in this schema.
            ["SchemeCode"] = 30,
            ["SchemeName"] = 100,
            // No matching schema column - feature not present in this schema.
            ["GradeCode"] = 20,
            ["GradeName"] = 100,
            // No matching schema column - feature not present in this schema.
            ["PeriodCode"] = 30,
            ["PeriodName"] = 100,
            ["StaffNumber"] = SchemaLengths.StaffNumber,
            ["StaffName"] = SchemaLengths.StaffName,
            /* Staff.FatherName is nvarchar(150) while Students.FatherName is nvarchar(500), and a bare column name cannot express both. */
            ["FatherName"] = SchemaLengths.StaffFatherName,
            ["Gender"] = SchemaLengths.Gender,
            ["CNIC"] = SchemaLengths.Cnic,
            ["AlternateContactNumber"] = SchemaLengths.AlternateContactNumber,
            ["Address"] = SchemaLengths.Address,
            ["Qualification"] = SchemaLengths.Qualification,
            ["Specialization"] = SchemaLengths.Specialization,
            ["Designation"] = SchemaLengths.Designation,
            ["Department"] = SchemaLengths.Department,
            ["EmploymentType"] = SchemaLengths.EmploymentType,
            ["RoleName"] = SchemaLengths.RoleName,
            /* No matching schema column. Students.FatherOccupation is 250 but that field lives on StudentAdmissionPage, not here. */
            ["Occupation"] = 250,
            // No matching schema column - feature not present in this schema.
            ["RoomCode"] = 30,
            ["RoomName"] = 100,
            ["Remarks"] = SchemaLengths.ListingRemarks
        };
    private static int EffectiveMaxLength(FieldSpec field)
    {
        if (field.MaxLength != 1000)
            return field.MaxLength;
        return F1TextLengths.TryGetValue(
            field.Key,
            out int length)
            ? length
            : 1000;
    }
    private Control CreateControl(FieldSpec field)
    {
        if (!string.IsNullOrWhiteSpace(field.LookupProcedure))
        {
            if (field.Kind == FieldKind.ComboString)
            {
                return new ModernComboBox {
                    DropDownStyle =
                        field.LookupAllowCustomValue
                            ? ComboBoxStyle.DropDown
                            : ComboBoxStyle.DropDownList,
                    Height = AppMetrics.InputControlHeight,
                    Dock = DockStyle.Fill,
                    Font = AppTheme.NormalFont,
                    TabStop = true
                };
            }
            return new LookupBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Height = AppMetrics.InputControlHeight,
                Dock = DockStyle.Fill,
                Font = AppTheme.NormalFont,
                TabStop = true
            };
        }
        switch (field.Kind)
        {
            case FieldKind.Integer:
                return new ModernNumericTextBox {
                    Minimum = -100000000,
                    Maximum = 100000000,
                    Dock = DockStyle.Fill,
                    Font = AppTheme.NormalFont,
                    ThousandsSeparator = false,
                    TabStop = true
                };
            case FieldKind.Decimal:
                return new ModernNumericTextBox {
                    Minimum = 0,
                    Maximum = 1000000000,
                    DecimalPlaces = 0,
                    Increment = 1M,
                    Dock = DockStyle.Fill,
                    Font = AppTheme.NormalFont,
                    ThousandsSeparator = true,
                    TabStop = true
                };
            case FieldKind.Date:
                return new ModernDatePicker {
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = "dd-MMM-yyyy",
                    Height = AppMetrics.InputControlHeight,
                    Dock = DockStyle.Fill,
                    Font = AppTheme.NormalFont,
                    ShowCheckBox = !field.Required,
                    Checked = field.Required,
                    TabStop = true
                };
            case FieldKind.Boolean:
                return new ModernCheckBox {
                    Text = field.Caption,
                    AutoSize = true,
                    Dock = DockStyle.Left,
                    Padding = new Padding(2, 4, 0, 0),
                    TabStop = true
                };
            case FieldKind.ComboInt:
            case FieldKind.ComboString:
                {
                    var combo = new ModernComboBox {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Height = AppMetrics.InputControlHeight,
                        Dock = DockStyle.Fill,
                        Font = AppTheme.NormalFont,
                        TabStop = true
                    };
                    if (field.Options is not null)
                    {
                        combo.Items.AddRange(
                            field.Options.Cast<object>().ToArray());
                    }
                    return combo;
                }
            case FieldKind.Time:
                return new ModernDatePicker {
                    TimeOnly = true,
                    Height = AppMetrics.InputControlHeight,
                    Dock = DockStyle.Fill,
                    Font = AppTheme.NormalFont,
                    TabStop = true
                };
            case FieldKind.Password:
                return new ModernTextBox {
                    Dock = DockStyle.Fill,
                    Height = AppMetrics.InputControlHeight,
                    MaxLength = EffectiveMaxLength(field),
                    UseSystemPasswordChar = true,
                    TabStop = true
                };
            default:
                return new ModernTextBox {
                    Dock = DockStyle.Fill,
                    Multiline = field.Multiline,
                    ScrollBars = field.Multiline
                        ? ScrollBars.Vertical
                        : ScrollBars.None,
                    Height = field.Multiline
                        ? AppMetrics.MultilineInputHeight(72)
                        : AppMetrics.InputControlHeight,
                    MaxLength = EffectiveMaxLength(field),
                    TabStop = true
                };
        }
    }
    private async Task BindLookupsAsync()
    {
        var loads = new List<(FieldSpec Field, Control Control, Task<DataTable> Task)>();
        /* every lookup control in the editor and the Save action are held disabled until the data above has really arrived. */
        var restore = new List<(Control Control, bool Enabled)>();
        var changedWhileLoading = new HashSet<Control>();
        EventHandler onEnabledChanged = (sender, _) => {
            if (sender is Control control)
                changedWhileLoading.Add(control);
        };
        foreach (var field in _fields)
        {
            if (string.IsNullOrWhiteSpace(field.LookupProcedure) ||
                !_controls.TryGetValue(field.Key, out var control))
                continue;
            restore.Add((control, control.Enabled));
            if (!string.IsNullOrWhiteSpace(field.LookupParentField))
            {
                WireDependentLookup(field);
                ClearLookup(field);
                continue;
            }
            var task = DatabaseHelper.QueryUserAsync(field.LookupProcedure!);
            loads.Add((field, control, task));
        }
        if (_saveButton is not null)
            restore.Add((_saveButton, _saveButton.Enabled));
        foreach (var (control, _) in restore)
        {
            control.Enabled = false;
            control.EnabledChanged += onEnabledChanged;
        }
        bool loaded = false;
        try
        {
            await Task.WhenAll(loads.Select(x => x.Task));
            foreach (var item in loads)
            {
                if (!IsDisposed)
                    BindLookupControl(item.Field, item.Control, await item.Task);
            }
            loaded = true;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Fail(ex);
        }
        finally
        {
            foreach (var (control, _) in restore)
                control.EnabledChanged -= onEnabledChanged;
            if (!IsDisposed)
            {
                foreach (var (control, wasEnabled) in restore)
                {
                    if (control.IsDisposed ||
                        changedWhileLoading.Contains(control))
                        continue;
                    control.Enabled = loaded && wasEnabled;
                }
            }
        }
    }
    private void BindLookupControl(
    FieldSpec field,
    Control control,
    DataTable table)
    {
        if (field.Kind == FieldKind.ComboString &&
            control is ModernComboBox stringCombo)
        {
            stringCombo.Items.Clear();
            if (!table.Columns.Contains(
                    field.LookupTextColumn))
            {
                throw new InvalidOperationException(
                    $"Lookup procedure '{field.LookupProcedure}' " +
                    $"does not return column " +
                    $"'{field.LookupTextColumn}'.");
            }
            var rows =
                table.Rows
                    .Cast<DataRow>()
                    .Where(row =>
                        row[field.LookupTextColumn] != DBNull.Value &&
                        !string.IsNullOrWhiteSpace(
                            Convert.ToString(
                                row[field.LookupTextColumn],
                                CultureInfo.InvariantCulture)))
                    .ToList();
            _lookupRows[field.Key] = rows;
            foreach (DataRow row in rows)
            {
                string text =
                    Convert.ToString(
                        row[field.LookupTextColumn],
                        CultureInfo.InvariantCulture)
                    ?? string.Empty;
                stringCombo.Items.Add(text);
            }
            stringCombo.SelectedIndex = -1;
            if (!field.LookupAllowCustomValue)
            {
                stringCombo.Text = string.Empty;
            }
            return;
        }
        if (control is LookupBox lookup)
        {
            lookup.Bind(
                table,
                field.LookupIdColumn,
                field.LookupTextColumn);
            return;
        }
        throw new InvalidOperationException(
            $"Field '{field.Key}' specifies lookup procedure " +
            $"'{field.LookupProcedure}' but its control type " +
            $"is not supported.");
    }
    private void WireDependentLookup(
    FieldSpec field)
    {
        if (string.IsNullOrWhiteSpace(
                field.LookupParentField))
        {
            return;
        }
        if (!_controls.TryGetValue(
                field.Key,
                out var childControl))
        {
            return;
        }
        if (!_controls.TryGetValue(
                field.LookupParentField,
                out var parentControl))
        {
            return;
        }
        if (parentControl is ModernComboBox parentCombo)
        {
            parentCombo.SelectedIndexChanged +=
                async (_, _) => await RefreshDependentLookupAsync(
                    field,
                    parentCombo);
        }
    }
    private async Task RefreshDependentLookupAsync(
    FieldSpec field,
    ModernComboBox parentCombo)
    {
        if (!_controls.TryGetValue(
                field.Key,
                out var childControl))
        {
            return;
        }
        int version = _lookupVersions.TryGetValue(field.Key, out var current)
            ? current + 1
            : 1;
        _lookupVersions[field.Key] = version;
        if (parentCombo.SelectedIndex < 0 ||
            parentCombo is LookupBox { SelectedId: <= 0 })
        {
            ClearLookup(field);
            return;
        }
        /* Never leave a child value visible while a different parent's list is loading. */
        ClearLookup(field);
        object? parentValue = null;
        if (parentCombo is LookupBox parentLookup)
        {
            parentValue = parentLookup.SelectedId;
        }
        else if (_lookupRows.TryGetValue(
                field.LookupParentField!,
                out var parentRows))
        {
            if (parentCombo.SelectedIndex >= parentRows.Count)
            {
                ClearLookup(field);
                return;
            }
            DataRow parentRow =
                parentRows[parentCombo.SelectedIndex];
            if (!parentRow.Table.Columns.Contains(
                    field.LookupParentIdColumn))
            {
                Fail(
                    new InvalidOperationException(
                        $"Lookup '{field.LookupParentField}' " +
                        $"does not contain '{field.LookupParentIdColumn}'."));
                return;
            }
            parentValue =
                parentRow[field.LookupParentIdColumn];
        }
        else
        {
            parentValue =
                parentCombo.SelectedItem;
        }
        if (parentValue is null ||
            parentValue == DBNull.Value)
        {
            ClearLookup(field);
            return;
        }
        string? textValue =
            Convert.ToString(
                parentValue,
                CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(textValue))
        {
            ClearLookup(field);
            return;
        }
        try
        {
            SqlParameter parameter;
            if (int.TryParse(
                    textValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int intValue))
            {
                parameter =
                    DatabaseHelper.Int(
                        field.LookupParentParameter,
                        intValue);
            }
            else
            {
                parameter =
                    DatabaseHelper.P(
                        field.LookupParentParameter,
                        textValue);
            }
            DataTable table = await DatabaseHelper.QueryUserAsync(
                field.LookupProcedure!,
                parameter);
            if (IsDisposed ||
                !_lookupVersions.TryGetValue(field.Key, out var latest) ||
                latest != version)
                return;
            BindLookupControl(field, childControl, table);
        }
        catch (Exception ex)
        {
            if (IsDisposed ||
                !_lookupVersions.TryGetValue(field.Key, out var latestVersion) ||
                latestVersion != version)
                return;
            ClearLookup(field);
            Fail(ex);
        }
    }
    private void ClearLookup(
    FieldSpec field)
    {
        if (!_controls.TryGetValue(
                field.Key,
                out var control))
        {
            return;
        }
        _lookupRows.Remove(field.Key);
        if (control is ModernComboBox combo)
        {
            combo.DataSource = null;
            combo.Items.Clear();
            combo.SelectedIndex = -1;
            combo.Text = string.Empty;
        }
        else if (control is LookupBox lookup)
        {
            lookup.DataSource = null;
            lookup.Items.Clear();
            lookup.SelectedIndex = -1;
            lookup.Text = string.Empty;
        }
    }
    private bool LoadRow(DataGridViewRow row)
    {
        if (ReopenCurrentEditor(row)) return true;
        try
        {
            object? id =
                FindRowId(row);
            _id =
                id is null
                    ? 0
                    : Convert.ToInt32(
                        id,
                        CultureInfo.InvariantCulture);
            if (_id <= 0)
                throw new InvalidOperationException("The selected record has no valid ID and cannot be edited.");
            foreach (var field in _fields)
            {
                if (!_controls.TryGetValue(
                        field.Key,
                        out var control))
                {
                    continue;
                }
                object? value =
                    row.Cells
                        .Cast<DataGridViewCell>()
                        .FirstOrDefault(cell =>
                            string.Equals(
                                cell.OwningColumn?.Name,
                                field.Key,
                                StringComparison.OrdinalIgnoreCase))
                        ?.Value;
                MasterFieldValueCodec.Write(control, value);
            }
            /* say which record is in the editor and that this is an edit, so an opened record cannot be mistaken for a blank Add form. */
            string recordName =
                GetDeleteDisplayName(row).Trim();
            SetStatus($"Editing {(recordName.Length > 0 ? recordName : MasterEntityNaming.Singularize(NavigationTitle))}. Change fields, then Save (Ctrl+S).");
            if (string.IsNullOrWhiteSpace(recordName))
            {
                _editorCard?.SetHeaderText(
                    "Edit Record",
                    "Loaded from the list.");
            }
            else
            {
                string entityLabel =
                    MasterEntityNaming.Singularize(NavigationTitle);
                _editorCard?.SetHeaderText(
                    $"Edit {entityLabel}",
                    $"{entityLabel}: {recordName}");
            }
            /* The freshly loaded record IS the baseline, so simply viewing a record never counts as an unsaved change. */
            CaptureEditorBaseline();
            if (_getProc == "GetStaff" && _staffRecordPicker is not null)
            {
                string TextFor(string name) => Convert.ToString(row.Cells.Cast<DataGridViewCell>()
                    .FirstOrDefault(cell => string.Equals(cell.OwningColumn?.Name, name, StringComparison.OrdinalIgnoreCase))?.Value) ?? string.Empty;
                _staffRecordPicker.SetStaff(_id, string.Join(" - ", new[] { TextFor("StaffNumber"), TextFor("StaffName") }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }
            RecordLoaded(row);
            if (_entryRecordsTabs is not null) _entryRecordsTabs.SelectedIndex = 0;
            FocusFirstEditorField();
            return true;
        }
        catch (Exception ex)
        {
            // A partially loaded record must never remain available for saving.
            Clear();
            CaptureEditorBaseline();
            Fail(ex);
            return false;
        }
    }
    private bool ReopenCurrentEditor(DataGridViewRow row)
    {
        if (_saving || !HasUnsavedChanges || _id <= 0 || row.IsNewRow ||
            !int.TryParse(Convert.ToString(FindRowId(row), CultureInfo.InvariantCulture), out int target) || target != _id)
            return false;
        ShowRecordsTab(false);
        FocusFirstEditorField();
        return true;
    }
    private Dictionary<string, object?> Values()
    {
        var values =
            _controls.ToDictionary(
                pair => pair.Key,
                pair => MasterFieldValueCodec.Read(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fields)
        {
            if (string.IsNullOrWhiteSpace(
                    field.LookupProcedure))
            {
                continue;
            }
            if (!_controls.TryGetValue(
                    field.Key,
                    out var control))
            {
                continue;
            }
            if (control is not ModernComboBox combo)
            {
                continue;
            }
            if (!_lookupRows.TryGetValue(
                    field.Key,
                    out var rows))
            {
                continue;
            }
            if (combo.SelectedIndex < 0 ||
                combo.SelectedIndex >= rows.Count)
            {
                continue;
            }
            DataRow row =
                rows[combo.SelectedIndex];
            if (!row.Table.Columns.Contains(
                    field.LookupIdColumn))
            {
                continue;
            }
            object id =
                row[field.LookupIdColumn];
            if (id == DBNull.Value)
            {
                continue;
            }
            if (int.TryParse(
                    Convert.ToString(
                        id,
                        CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int lookupId))
            {
                values[field.Key + "Id"] =
                    lookupId;
            }
        }
        return values;
    }
    /* Enter advances through editable fields via the shared focus navigator. */
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && !_saving &&
            MasterEditorFocusNavigator.TryAdvance(FindForm()?.ActiveControl, _controls.Values, _saveButton))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private static bool FocusInside(Control control)
    {
        if (control.CanFocus && control.TabStop && control.Focus())
        {
            if (control is TextBoxBase { Multiline: false } text)
                text.SelectAll();
            return true;
        }
        foreach (Control child in control.Controls)
        {
            if (FocusInside(child))
                return true;
        }
        return false;
    }
    private async void Save(
        object? sender,
        EventArgs e)
    {
        /* This is an async void event handler with no protection at all, so clicking twice quickly. */
        if (!CanSaveRecord()) return;
        if (IsBusy)
            return;
        /* An existing record that was only loaded, not edited, has nothing to save.
           Return before validation or the save procedure so the record cannot be
           mistaken for a duplicate of itself. */
        if (!HasUnsavedChanges)
            return;
        if (!ValidateRequiredInputs())
            return;
        _saving = true;
        /* Holds the editor, actions, and grid while a save is in flight. */
        var busyControls =
            new List<(Control Control, bool WasEnabled)>();
        void TrackBusy(Control? control)
        {
            if (control is null ||
                control.IsDisposed)
            {
                return;
            }
            busyControls.Add((control, control.Enabled));
        }
        /* The control to return to is remembered here and given focus once the editor is enabled again (finally, below). */
        Control? focusAfterSave = FindForm()?.ActiveControl;
        bool focusNewRecord = false;
        TrackBusy(_saveButton);
        foreach (var editorControl in _controls.Values)
            TrackBusy(editorControl);
        TrackBusy(Grid);
        foreach (Control pageAction in Actions.Controls)
            TrackBusy(pageAction);
        foreach (var (control, _) in busyControls)
            control.Enabled = false;
        try
        {
            var values = Values();
            values["Id"] = _id;
            foreach (var hiddenField
                     in _fields.Where(IsOperatorHidden))
            {
                if (_id == 0)
                {
                    values[hiddenField.Key] =
                        GenerateInternalCode(
                            hiddenField);
                }
            }
            foreach (var field
                     in _fields.Where(x => x.Required))
            {
                object? value =
                    values[field.Key];
                if (value is null ||
                    value is string text &&
                    string.IsNullOrWhiteSpace(text))
                {
                    Warn(
                        $"{field.Caption} is required.");
                    focusAfterSave = _controls[field.Key];
                    return;
                }
            }
            if (values.TryGetValue("YearName", out var yearName) &&
                !RecordTextPolicy.IsValidYearName(Convert.ToString(yearName)?.Trim() ?? string.Empty))
            {
                Warn(AcademicYearName.ValidationMessage);
                focusAfterSave = _controls["YearName"];
                return;
            }
            if (HasReorderConfiguration || _fields.Any(IsDisplayOrder))
            {
                // The database assigns new positions and preserves existing positions.
                values["DisplayOrder"] = 0;
            }
            SqlParameter[] parameters =
                _buildParams(values);
            _id = await SaveRecordAsync(parameters);
            AfterSave(
                _id,
                values);

            Clear(afterSave: true);
            /* what is on screen now matches the database, so the page is no longer dirty and leaving it must not prompt. */
            CaptureEditorBaseline();
            bool refreshed = await ReloadAsync();
            if (!IsDisposed)
            {
                string entity = MasterEntityNaming.Singularize(NavigationTitle);
                if (refreshed)
                    Info($"{entity} saved successfully. Ready for a new record.");
                else
                    Warn($"{entity} saved successfully, but the records list could not refresh. Refresh the records before continuing.");
            }
            /* Return focus to the first editor field after saving. */
            focusAfterSave = null;
            focusNewRecord = true;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            _saving = false;
            /* Restored to the state each control was in before the save, not to a blanket true. */
            foreach (var (control, wasEnabled) in busyControls)
            {
                if (!control.IsDisposed)
                    control.Enabled = wasEnabled;
            }
            if (focusNewRecord)
                FocusFirstEditorField();
            else if (focusAfterSave is { IsDisposed: false } && focusAfterSave.CanFocus)
            {
                if (focusAfterSave.Focus() &&
                    focusAfterSave is TextBoxBase { Multiline: false } focusedText)
                {
                    focusedText.SelectAll();
                }
                else if (!focusAfterSave.Focused)
                {
                    MasterEditorFocusNavigator.FocusInside(focusAfterSave);
                }
            }
        }
    }
    protected virtual void AfterSave(
        int id,
        Dictionary<string, object?> values)
    {
    }
    private void Clear(bool afterSave = false)
    {
        _id = 0;
        foreach (var field in _fields)
        {
            if (!_controls.TryGetValue(
                    field.Key,
                    out var control))
            {
                continue;
            }
            if (IsOperatorHidden(field))
            {
                if (control is TextBox hiddenCode)
                {
                    hiddenCode.Text =
                        GenerateInternalCode(field);
                }
                else if (control is ModernTextBox hiddenModernCode)
                {
                    hiddenModernCode.Text =
                        GenerateInternalCode(field);
                }
                continue;
            }
            switch (control)
            {
                case TextBox text:
                    text.Clear();
                    break;
                case ModernNumericTextBox number:
                    number.Value =
                        number.Minimum;
                    break;
                case ModernTextBox modernText:
                    modernText.Clear();
                    break;
                case DateTimePicker date:
                    date.Value =
                        DateTime.Today;
                    if (date.ShowCheckBox)
                    {
                        date.Checked = false;
                    }
                    break;
                case ModernDatePicker modernDate:
                    modernDate.Value =
                        DateTime.Today;
                    if (modernDate.ShowCheckBox)
                    {
                        modernDate.Checked = false;
                    }
                    break;
                case CheckBox check:
                    check.Checked = false;
                    break;
                case ComboBox combo:
                    combo.SelectedIndex = -1;
                    combo.Text = string.Empty;
                    break;
                case ModernComboBox modernCombo:
                    modernCombo.SelectedIndex = -1;
                    modernCombo.Text = string.Empty;
                    break;
            }
        }
        SetStatus(
            "New record: enter details, then Save (Ctrl+S).");
        /* The editor is blank again, so the header must stop naming the record that was just saved, cleared or deleted. */
        _editorCard?.SetHeaderText(
            NewRecordHeaderTitle,
            NewRecordHeaderSubtitle);
        EditorCleared(afterSave);
        FocusFirstEditorField();
    }
    private string GenerateInternalCode(
        FieldSpec field)
    {
        string nameKey =
            field.Key[..^4] + "Name";
        string name =
            _controls.TryGetValue(
                nameKey,
                out var control)
                ? Convert.ToString(
                    MasterFieldValueCodec.Read(control),
                    CultureInfo.InvariantCulture)
                    ?.Trim() ?? string.Empty
                : string.Empty;
        string code =
            string.IsNullOrWhiteSpace(name)
                ? $"AUTO-{Guid.NewGuid():N}"
                : new string(
                    name
                        .Where(char.IsLetterOrDigit)
                        .ToArray())
                    .ToUpperInvariant();
        int maxLength =
            Math.Max(
                1,
                EffectiveMaxLength(field));
        return code.Length > maxLength
            ? code[..maxLength]
            : code;
    }
}
