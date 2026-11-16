using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Administrator-only, read-only historical snapshots. Restoring explicitly
/// creates a fresh revision; it never overwrites historical versions.
/// </summary>
internal sealed class CommunicationTemplateVersionsDialog : ApplicationWindowForm
{
    private readonly int templateId;
    private readonly Func<int, Task<DataTable>> read;
    private readonly Func<int, int, Task> restore;
    private readonly DataGridView versions = HumanUi.Grid(false);
    private readonly ModernTextBox body = HumanUi.TextBox(true, 1600);
    private readonly Label feedback = new() { Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText };
    private readonly ModernButton restoreButton = new()
    {
        Name = "communicationRestoreTemplateVersion", Text = "Restore Selected Version…",
        ThemeRole = "Secondary", Enabled = false
    };
    private bool busy;
    internal bool Restored { get; private set; }

    internal CommunicationTemplateVersionsDialog(int templateId, string name,
        Func<int,Task<DataTable>>? read = null, Func<int,int,Task>? restore = null)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Template version history requires administrator access.");
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        this.templateId = templateId;
        this.read = read ?? CommunicationManagementStore.ReadTemplateRevisionsAsync;
        this.restore = restore ?? CommunicationManagementStore.RestoreTemplateRevisionAsync;
        Text = "Template Versions — " + name;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(AppScale.Scale(830), AppScale.Scale(510));
        MinimumSize = new Size(AppScale.Scale(640), AppScale.Scale(430));
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = AppTheme.PageBackground;
        versions.Name = "communicationTemplateVersionGrid";
        versions.AutoGenerateColumns = false;
        foreach (var (key, caption, width) in new[]
        {
            ("RevisionNumber","Version",65), ("Operation","Change",100),
            ("ChangedUtc","Saved (UTC)",150), ("ChangedBy","By user",75),
            ("LanguageCode","Language",75), ("Active","Active",60)
        })
            versions.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name=key,DataPropertyName=key,HeaderText=caption,
                AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=width
            });
        versions.Columns["ChangedUtc"]!.DefaultCellStyle.Format = "dd MMM yyyy HH:mm";
        versions.CurrentCellChanged += (_,_) => UpdateSelection();
        body.ReadOnly = true;
        body.ScrollBars = ScrollBars.Vertical;
        var close = new ModernButton { Text = "Close", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var buttons = HumanUi.DialogButtons(restoreButton, close);
        buttons.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,
            Padding=new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,53));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,47));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(28)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(8)));
        layout.Controls.Add(versions,0,0);
        layout.Controls.Add(HumanUi.Field("Saved message body (read-only)",body),0,1);
        layout.Controls.Add(feedback,0,2);
        layout.Controls.Add(buttons,0,3);
        Controls.Add(layout);
        Shown += async (_,_) => await LoadAsync();
        restoreButton.Click += async (_,_) => await RestoreAsync();
        FormClosing += (_,e) => { if (busy) { e.Cancel=true;DialogResult=DialogResult.None; } };
        FormClosed += (_,_) => (versions.DataSource as DataTable)?.Dispose();
    }

    private void UpdateSelection()
    {
        var row=(versions.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        body.Text=row?.Field<string>("Body") ?? "";
        restoreButton.Enabled=!busy && row is not null && CurrentUser.HasPermission("Communication.Configure");
    }

    private async Task LoadAsync()
    {
        if (busy) return;
        busy=true;
        restoreButton.Enabled=false;
        try
        {
            int actor=CurrentUser.UserId;
            var rows=await read(templateId);
            if (IsDisposed || actor!=CurrentUser.UserId) { rows.Dispose(); return; }
            var old=versions.DataSource as DataTable;
            versions.DataSource=rows;
            old?.Dispose();
            UiHelper.SetGridEmptyMessage(versions,"No saved template versions were found.");
        }
        catch(Exception ex) { if(!IsDisposed) feedback.Text=DbMessage.UserMessage(ex); }
        finally { busy=false;if(!IsDisposed)UpdateSelection(); }
    }

    private async Task RestoreAsync()
    {
        if (busy || versions.CurrentRow?.DataBoundItem is not DataRowView row) return;
        int revision=Convert.ToInt32(row["RevisionNumber"]);
        if (ModernMessageDialog.Show(this,
                $"Restore version {revision}? This changes the current template. All earlier versions remain saved and the restore is audited as a new version.",
                "Restore School Template",MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning)!=DialogResult.Yes) return;
        busy=true;
        restoreButton.Enabled=false;
        try
        {
            int actor=CurrentUser.UserId;
            await restore(templateId,revision);
            if (IsDisposed || actor!=CurrentUser.UserId) return;
            Restored=true;
            DialogResult=DialogResult.OK;
            Close();
        }
        catch(Exception ex) { if(!IsDisposed) feedback.Text=DbMessage.UserMessage(ex); }
        finally { busy=false;if(!IsDisposed)UpdateSelection(); }
    }
}
