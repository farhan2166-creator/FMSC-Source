namespace FMSC_SMS.Core;
/// <summary>Small shared mechanics used by the certificate screens; certificate-specific business rules stay on their pages.</summary>
internal static class CertificateUi
{
    internal static async Task<int> ReadSavedCertificateIdAsync(string procedure, string key,
        params Microsoft.Data.SqlClient.SqlParameter[] parameters)
    {
        using var saved = await DatabaseHelper.QueryUserAsync(procedure, parameters);
        return ReadSavedCertificateId(saved, key);
    }

    internal static int ReadSavedCertificateId(DataTable saved, string key)
    {
        if (saved.Rows.Count == 1 && GetInt(saved.Rows[0], key) is > 0 and var id) return id;
        throw new InvalidOperationException("The certificate save result could not be read. Reload its history before trying again; do not repeat the create action.");
    }

    internal static bool FocusEntry(StudentPicker picker, DataGridView grid, Control firstField) =>
        picker.SelectedId <= 0 ? picker.Focus() : grid.Rows.Count > 0 ? grid.Focus() : FormFlow.FocusFirstInput(firstField);

    internal static void WireNavigation(Control page, StudentPicker picker, DataGridView grid,
        Control newButton, Control editor, Control editorActions, Control historyActions,
        Action<int>? loadHistoryRow = null)
    {
        editor.TabIndex = 0;
        editorActions.TabIndex = 1;
        grid.TabIndex = 0;
        historyActions.TabIndex = 1;
        foreach (Control row in new[] { editorActions, historyActions })
            foreach (FlowLayoutPanel flow in row.Controls.OfType<FlowLayoutPanel>())
                for (int index = 0; index < flow.Controls.Count; index++)
                    flow.Controls[index].TabIndex = index;

        FormFlow.ScheduleInitialFocus(page, picker, () => picker.SelectedId <= 0);
        // The page's reset handler runs first. A refused discard keeps its focus.
        newButton.Click += (_, _) => {
            if (picker.SelectedId <= 0) picker.Focus();
        };
        if (loadHistoryRow is not null)
            grid.KeyUp += (_, e) => {
                if (e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown &&
                    grid.CurrentRow is { IsNewRow: false, Selected: true } row)
                    loadHistoryRow(row.Index);
            };
    }

    internal static void FocusHistory(DataGridView grid, Action selectLatest, Control firstField)
    {
        if (grid.Rows.Cast<DataGridViewRow>().Any(row => !row.IsNewRow))
        {
            selectLatest();
            grid.Focus();
        }
        else
            FormFlow.FocusFirstInput(firstField);
    }

    internal static string PromotionClassText(string value)
    {
        // Older certificates saved the picker caption (class name plus class code).
        int separator=value.LastIndexOf(" — ",StringComparison.Ordinal);
        return (separator<0?value:value[..separator]).Trim();
    }

    internal static string NormalizeGender(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MALE"=>"Male", "FEMALE"=>"Female", _=>""
    };

    internal static DataTable PrintData(string reportPath,DataTable source,Func<DataRow,string?>? readGender=null)
    {
        var data=RecordTextPolicy.PrintCopy(source);
        if(!data.Columns.Contains("Gender"))data.Columns.Add("Gender",typeof(string));
        foreach(DataRow row in data.Rows)
            row["Gender"]=NormalizeGender(readGender is null?Convert.ToString(row["Gender"]):readGender(row));
        if(CertificateLetterhead.UsesPreprintedPad(reportPath) && data.Columns.Contains("ClassPromotedTo"))
            foreach(DataRow row in data.Rows)
                row["ClassPromotedTo"]=PromotionClassText(Convert.ToString(row["ClassPromotedTo"])??"");
        if(CertificateLetterhead.UsesPreprintedPad(reportPath))FormatLeavingReasons(data);
        return data;
    }

    internal static void FormatLeavingReasons(DataTable data)
    {
        if(data.Columns.Contains("Remarks"))
            foreach(DataRow row in data.Rows)
                if(row["Remarks"] is string text)row["Remarks"]=RecordTextPolicy.TitleCase(text);
    }
    internal const string FieldGridTag="CertificateFieldGrid";
    internal static bool ConfirmCertificateSelection(IWin32Window? owner, DataGridView grid,
        string idColumn, int currentId, int requestedId, bool hasEdits,Func<bool>? confirmDiscard=null)
    {
        if (!hasEdits) return true;
        if (requestedId == currentId) return false;
        if(confirmDiscard?.Invoke()??ModernMessageDialog.Confirm(owner,
            "Discard the unsaved certificate changes and select another certificate?",
            UiText.Dialogs.UnsavedChanges))return true;
        if(currentId<=0)
        {
            // Stay in a new draft: there is no saved row to restore.
            grid.ClearSelection();grid.CurrentCell=null;return false;
        }
        if (grid.Columns.Contains(idColumn))
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow || !int.TryParse(Convert.ToString(row.Cells[idColumn].Value), out int id) || id != currentId) continue;
                grid.ClearSelection();
                var cell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.OwningColumn.Visible);
                if (cell is not null) grid.CurrentCell = cell;
                row.Selected = true;
                break;
            }
        }
        return false;
    }

    public static Panel Field(string caption, Control control, bool required = false, bool fillHeight = true)
    {
        Size requestedMaximum=control.MaximumSize;
        var field = (TableLayoutPanel)HumanUi.Field(caption, control, required, fillHeight);
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        if (control is ModernTextBox { Multiline: false } || control is ModernComboBox ||
            control is ModernDatePicker || control is StudentPicker || control is NumericUpDown)
        {
            // The certificate's field grid owns widths; keep each input directly
            // below its caption instead of vertically centring it in spare space.
            control.MaximumSize=requestedMaximum;
            control.Height = AppMetrics.InputControlHeight;
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0);
        }
        return field;
    }

    internal static void CompactFields(HumanCard details,TableLayoutPanel fields,TableLayoutPanel root,
        float[] wideWidths,(int Column,int Row,int Span)[] widePositions,int minimumWidth=1200)
    {
        fields.Tag=FieldGridTag;
        var controls=fields.Controls.Cast<Control>().ToArray();
        var original=controls.Select(control=>(Column:fields.GetColumn(control),Row:fields.GetRow(control),Span:fields.GetColumnSpan(control))).ToArray();
        var originalWidths=fields.ColumnStyles.Cast<ColumnStyle>().Select(style=>style.Width).ToArray();
        bool? wideLayout=null;
        void Arrange()
        {
            bool wide=details.Content.ClientSize.Width>=AppScale.Scale(minimumWidth);
            if(wideLayout==wide)return;
            wideLayout=wide;
            var positions=wide?widePositions:original;
            var widths=wide?wideWidths:originalWidths;
            fields.SuspendLayout();root.SuspendLayout();
            try
            {
                foreach(Control control in controls)fields.SetColumnSpan(control,1);
                fields.ColumnCount=widths.Length;fields.RowCount=positions.Max(position=>position.Row)+1;
                fields.ColumnStyles.Clear();fields.RowStyles.Clear();
                foreach(float width in widths)fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,width));
                for(int row=0;row<fields.RowCount;row++)fields.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
                for(int index=0;index<controls.Length;index++)
                {
                    var position=positions[index];
                    fields.SetCellPosition(controls[index],new TableLayoutPanelCellPosition(position.Column,position.Row));
                    fields.SetColumnSpan(controls[index],position.Span);
                    controls[index].TabIndex=position.Row*fields.ColumnCount+position.Column;
                }
                root.RowStyles[0].Height=AppMetrics.FieldCardHeight(fields.RowCount)+AppMetrics.ButtonRowHeight;
            }
            finally{fields.ResumeLayout(true);root.ResumeLayout(true);}
        }
        details.Content.SizeChanged+=(_,_)=>Arrange();
        Arrange();
    }

    public static int GetInt(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
            return 0;
        return int.TryParse(Convert.ToString(row[column]), out int value) ? value : 0;
    }
    public static decimal GetDecimal(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
            return 0m;
        return decimal.TryParse(Convert.ToString(row[column]), out decimal value) ? value : 0m;
    }
    public static string GetString(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
            return string.Empty;
        return Convert.ToString(row[column])?.Trim() ?? string.Empty;
    }
    public static DateTime? GetDate(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
            return null;
        if (row[column] is DateTime date)
            return date;
        return DateTime.TryParse(Convert.ToString(row[column]), out DateTime parsed) ? parsed : null;
    }
    public static void ShowReport(
        IWin32Window? owner,
        string title,
        string reportPath,
        string dataSourceName,
        DataTable data,
        IReadOnlyList<string>? provisionalSubjects = null)
    {
        using var printable=PrintData(reportPath,data);
        // Defaults are per certificate. SLC and CLC start in their existing pad mode.
        var selectedMode = CertificateLetterhead.PreferredMode(reportPath);
        DataRow? school = null;
        bool schoolLoaded = false;
        using var signatureSettings = DatabaseHelper.QueryUser("GetPrintedCertificateSignature",
            DatabaseHelper.Bit("@ForLeaving", CertificateLetterhead.UsesPreprintedPad(reportPath)));
        var signature = signatureSettings.Rows.Count > 0 ? signatureSettings.Rows[0] : null;
        bool printSignature = signature is not null
            && signature["PrintPrincipalSignature"] is not DBNull
            && Convert.ToBoolean(signature["PrintPrincipalSignature"]);
        byte[]? signatureImage = printSignature ? signature?["PrincipalSignatureImage"] as byte[] : null;
        string? signatureContentType = signature is null ? null : Convert.ToString(signature["PrincipalSignatureContentType"]);

        string BuildDefinition(CertificatePaperMode mode)
        {
            if (mode == CertificatePaperMode.PlainPaper && !schoolLoaded)
            {
                using var profile = DatabaseHelper.QueryUser("GetSchoolProfile");
                school = profile.Rows.Count > 0 ? profile.Rows[0] : null;
                schoolLoaded = true;
            }
            string definition = CertificateLetterhead.Definition(reportPath, school,
                data.Rows.Count > 0 ? data.Rows[0] : null, provisionalSubjects, mode);
            return signatureImage is { Length: > 0 }
                ? CertificateSignature.Apply(definition, signatureImage, signatureContentType, true)
                : definition;
        }

        RdlcReportPreview.Show(
            owner,
            title,
            reportPath,
            dataSourceName,
            printable,
            reportDefinition: BuildDefinition(selectedMode),
            enableExternalImages: true,
            certificatePaper: new CertificatePaperOptions(
                selectedMode,
                BuildDefinition,
                mode =>
                {
                    var saved = ReportLayoutStore.SetPreference(CertificateLetterhead.PreferenceKey(reportPath), mode.ToString());
                    if (!saved.Succeeded) throw saved.Error ?? new IOException("Unable to save certificate paper preference.");
                }));
    }
}
