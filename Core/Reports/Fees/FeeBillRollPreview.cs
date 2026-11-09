using System.Drawing.Printing;
using Microsoft.Reporting.WinForms;

namespace FMSC_SMS.Core;

internal static class FeeBillRollPreview
{
    internal static void Show(Form? owner,string title,string path,string dataSetName,DataTable display)
    {
        title=ReportDocumentNaming.PreviewTitle(title,path,display);
        using var document=FeeBillRollDocument.Render(path,dataSetName,display);
        Show(owner,title,document,ReportDocumentNaming.DocumentExportFileName(title,path,display));
    }

    internal static void Show(Form? owner,string title,FeeBillRollDocument document,string? suggestedExportName=null)
    {
        if(document.Pages.Count==0)return;
        var area=owner is null?Screen.PrimaryScreen!.WorkingArea:Screen.FromControl(owner).WorkingArea;
        using var form=new ApplicationWindowForm {Text=title,StartPosition=FormStartPosition.CenterParent,WindowState=FormWindowState.Maximized,
            Size=new Size(Math.Min(1180,area.Width),Math.Min(820,area.Height)),MinimumSize=new Size(Math.Min(850,area.Width),Math.Min(600,area.Height)),KeyPreview=true};
        using var overview=new ReportPageOverview {BackColor=SystemColors.AppWorkspace};
        // EMF resolution metadata can describe a different scale. Use the same
        // physical paper geometry as PDF export and the print job instead.
        overview.LoadPages(document.Pages.Select(p=>p.Image),clone:true,
            paperSizesInches:document.Pages.Select(p=>new SizeF(p.Settings.PaperSize.Width/100f,p.Settings.PaperSize.Height/100f)).ToArray());
        using var surface=new Panel {Dock=DockStyle.Fill};surface.Controls.Add(overview);
        var toolbar=new FlowLayoutPanel {Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Padding=new Padding(12,9,12,9),BackColor=AppTheme.Surface};
        int selected=1;var printer=new PrinterSettings();
        var number=new NumericUpDown {Minimum=1,Maximum=document.Pages.Count,Value=1,Width=66,AccessibleName="Go to page"};
        var label=new Label {AutoSize=true,Text=$"of {document.Pages.Count}",Margin=new Padding(4,8,8,0)};
        var zoom=HumanUi.Combo();zoom.Width=132;zoom.Items.AddRange(new object[]{"Fit page","Page width","50%","75%","100%","125%","150%","200%"});zoom.SelectedIndex=0;
        var view=HumanUi.Combo();view.Width=170;view.AccessibleName="Page view";view.Items.AddRange(new object[]{"Single page","Multiple pages"});
        view.SelectedIndex=ReportLayoutStore.GetPreference("ReportPageView")=="Multiple"?1:0;
        void Execute(string action,Action run)
        {
            try{run();}
            catch(Exception ex){AppExceptionHandler.LogNonFatal(ex,"Bill "+action);ModernMessageDialog.Show(form,"Unable to "+action+" the bills.\n\n"+ex.GetBaseException().Message,title,MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
        ModernButton Button(string text,Action click,string role="Secondary")
        {
            var button=new ModernButton {Text=text,ThemeRole=role,Width=AppMetrics.TextWidth(text,AppTheme.ButtonFont,32),Height=AppMetrics.ButtonHeight,Margin=new Padding(0,0,8,0)};
            button.Click+=(_,_)=>Execute(text.ToLowerInvariant(),click);toolbar.Controls.Add(button);return button;
        }
        void Print(bool current)
        {
            using var job=document.CreatePrintDocument(title);job.PrinterSettings=(PrinterSettings)printer.Clone();
            using var dialog=ReportPrintDialog.Create(job,selected,current);
            if(dialog.ShowDialog(form)!=DialogResult.OK)return;
            if(!job.PrinterSettings.IsValid)throw new InvalidOperationException("Select an available printer and try again.");
            job.PrintController=new StandardPrintController();job.Print();
            if(job.PagesPrinted==0)throw new InvalidOperationException("The print job did not produce any pages.");
            printer=(PrinterSettings)job.PrinterSettings.Clone();
        }
        var print=Button("Print",()=>Print(false),"Primary");var printCurrent=Button("Print this page",()=>Print(true));
        var setup=Button("Page setup",()=>{
            using var job=document.CreatePrintDocument(title);job.PrinterSettings=(PrinterSettings)printer.Clone();
            using var dialog=new PageSetupDialog {Document=job,AllowMargins=false,AllowOrientation=false,AllowPaper=false,AllowPrinter=true};
            if(dialog.ShowDialog(form)==DialogResult.OK)printer=(PrinterSettings)job.PrinterSettings.Clone();
        });
        var export=Button("Export PDF",()=>{
            using var dialog=new SaveFileDialog {Filter="PDF document (*.pdf)|*.pdf",DefaultExt="pdf",AddExtension=true,FileName=suggestedExportName??ReportDocumentNaming.ExportFileName(title),Title="Export report"};
            if(dialog.ShowDialog(form)==DialogResult.OK)AtomicExportFile.Publish(dialog.FileName,
                file=>File.WriteAllBytes(file,document.ExportPdf(title)),PdfTableExporter.ValidatePdf);
        });
        ModernButton? previous=null,next=null;
        void Navigate(int page)
        {
            selected=Math.Clamp(page,1,document.Pages.Count);number.Value=selected;overview.SelectPage(selected);
            if(previous is not null)previous.Enabled=selected>1;if(next is not null)next.Enabled=selected<document.Pages.Count;
            printCurrent.Text=$"Print page {selected}";
        }
        previous=Button("Previous",()=>Navigate(selected-1));toolbar.Controls.Add(number);toolbar.Controls.Add(label);
        var go=Button("Go",()=>Navigate((int)number.Value));next=Button("Next",()=>Navigate(selected+1));
        toolbar.Controls.Add(zoom);toolbar.Controls.Add(view);var close=Button("Close",form.Close);
        var search=new ReportPreviewSearchBar(form,()=>{
            byte[] pdf=document.ExportPdf(title);
            return Task.Run(()=>ReportPageSearchIndex.FromPdf(pdf));
        },Navigate);
        toolbar.Controls.Add(search);
        void ApplyZoom()
        {
            string text=Convert.ToString(zoom.SelectedItem)??"Fit page";
            overview.SetZoom(text=="Fit page"?ZoomMode.FullPage:text=="Page width"?ZoomMode.PageWidth:ZoomMode.Percent,
                text.EndsWith('%')?int.Parse(text.TrimEnd('%')):100);
        }
        void StepZoom(int direction)
        {
            string text=$"{Math.Clamp(overview.EffectiveZoomPercent+direction*25,25,400)}%";
            if(!zoom.Items.Contains(text))zoom.Items.Add(text);zoom.SelectedItem=text;ApplyZoom();
        }
        zoom.SelectionChangeCommitted+=(_,_)=>ApplyZoom();
        view.SelectionChangeCommitted+=(_,_)=>{overview.SetSinglePage(view.SelectedIndex==0);ReportLayoutStore.SetPreference("ReportPageView",view.SelectedIndex==1?"Multiple":"Single");Navigate(selected);};
        overview.PageSelected+=Navigate;
        number.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){Navigate((int)number.Value);e.SuppressKeyPress=true;}};
        foreach(var editor in number.Controls.OfType<TextBox>())WholeNumberInput.Attach(editor);
        void Shortcut(Control control,Keys keys,string hint,Action action)=>form.ShortcutController.RegisterShortcut(control,keys,hint,()=>Execute(hint,action));
        Shortcut(print,Keys.Control|Keys.P,"Ctrl+P",()=>Print(false));Shortcut(export,Keys.Alt|Keys.E,"Alt+E",()=>export.PerformClick());
        Shortcut(setup,Keys.Alt|Keys.P,"Alt+P",()=>setup.PerformClick());Shortcut(close,Keys.Escape,"Esc",form.Close);
        Shortcut(previous,Keys.Control|Keys.Left,"Ctrl+Left",()=>Navigate(selected-1));Shortcut(next,Keys.Control|Keys.Right,"Ctrl+Right",()=>Navigate(selected+1));
        Shortcut(go,Keys.Control|Keys.G,"Ctrl+G",()=>{number.Focus();number.Select(0,number.Text.Length);});
        Shortcut(go,Keys.Control|Keys.Home,"Ctrl+Home",()=>Navigate(1));Shortcut(go,Keys.Control|Keys.End,"Ctrl+End",()=>Navigate(document.Pages.Count));
        Shortcut(zoom,Keys.Control|Keys.D0,"Ctrl+0",()=>{zoom.SelectedIndex=0;ApplyZoom();});Shortcut(zoom,Keys.Control|Keys.D1,"Ctrl+1",()=>{zoom.SelectedIndex=1;ApplyZoom();});
        Shortcut(zoom,Keys.Control|Keys.D2,"Ctrl+2",()=>{zoom.SelectedItem="100%";ApplyZoom();});
        foreach(var key in new[]{Keys.Oemplus,Keys.Add})Shortcut(zoom,Keys.Control|key,"Ctrl++",()=>StepZoom(1));
        foreach(var key in new[]{Keys.OemMinus,Keys.Subtract})Shortcut(zoom,Keys.Control|key,"Ctrl+-",()=>StepZoom(-1));
        var menu=new ContextMenuStrip();menu.Items.Add("Zoom in",null,(_,_)=>StepZoom(1));menu.Items.Add("Zoom out",null,(_,_)=>StepZoom(-1));
        menu.Items.Add("Print this page",null,(_,_)=>Execute("print",()=>Print(true)));
        using var interactions=new ReportPreviewInteractionFilter(form,surface,StepZoom,menu);
        form.Controls.Add(surface);form.Controls.Add(toolbar);form.ShortcutController.RefreshHints();
        form.Shown+=(_,_)=>{overview.SetSinglePage(view.SelectedIndex==0);ApplyZoom();Navigate(1);};
        if(owner is null)form.ShowDialog();else form.ShowDialog(owner);
    }
}
