using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckAdmissionToolbarRows()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName, CurrentUser.MustChangePassword,
            Permissions: CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7777, "Layout fixture", "Administrator", false, Array.Empty<string>());
            using var admission = new StudentAdmissionPage();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var source = (FlowLayoutPanel)typeof(V2Page).GetField("Actions", flags)!.GetValue(admission)!;
            using var actions = new FlowLayoutPanel();
            foreach (Control child in source.Controls.Cast<Control>().ToArray()) actions.Controls.Add(child);
            using var window = new Form { ClientSize = new Size(1500, 200) };
            actions.Dock = DockStyle.Fill; actions.WrapContents = true; actions.Visible = true;
            window.Controls.Add(actions); window.Show(); Application.DoEvents();
            // Verify required operations and fit without freezing the toolbar to
            // a historical button count; optional messaging can coexist here.
            var visibleActions = actions.Controls.OfType<ModernButton>().Where(b=>b.Visible).Select(b=>b.Text).ToArray();
            Check(new[] { "Actions ▾", "Admit Sibling", "Clear / New", "Collect Payment", "Find Student", "Print ▾", UiText.Actions.Save }
                .All(visibleActions.Contains) && visibleActions.Distinct().Count() == visibleActions.Length,
                "Admission toolbar retains all ordinary operations without duplicate actions");
            Check(visibleActions.Contains("Message Student…"), "An authorized operator can reach optional saved-student messaging");
            Check(!actions.Controls.OfType<ModernButton>().Single(b=>b.Text=="Save & New").Visible,
                "Admission Save & New stays temporarily hidden to leave room for Print");
            foreach (int width in new[] { 1100, 1150, 1250, 1400, 1560 })
            {
                window.ClientSize = new Size(width, 200); Application.DoEvents();
                Check(actions.Controls.OfType<ModernButton>().Single(b=>b.Text=="Print ▾").Visible,
                    "Admission printing remains available when the toolbar wraps");
                Check(actions.Controls.OfType<ModernButton>().Where(b=>b.Visible).All(b=>actions.ClientRectangle.Contains(b.Bounds)),
                    "Narrow Admission toolbars wrap complete buttons without clipping them");
            }
            using var largerFont = new Font("Segoe UI", 14f);
            foreach (var button in actions.Controls.OfType<ModernButton>()) button.Font = largerFont;
            window.ClientSize = new Size(1560, 240); Application.DoEvents();
            foreach (var button in actions.Controls.OfType<ModernButton>().Where(b=>b.Visible))
                Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width + button.Padding.Horizontal,
                    "Larger Admission captions remain readable: " + button.Text);
            Check(!admission.IsHandleCreated, "Toolbar fixture never starts admission SQL loaders");
            window.Controls.Remove(actions); actions.Dispose();
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Permissions); }
    }

    private static void CheckReportCaptionOrder()
    {
        using var form = new ApplicationWindowForm { ClientSize = new Size(1100, 700) };
        using var surface = new Panel { Dock = DockStyle.Fill };
        using var toolbar = new Panel { Dock = DockStyle.Top, Height = 58 };
        form.Controls.Add(surface); form.Controls.Add(toolbar);
        form.Show(); Application.DoEvents();
        var caption = form.Controls.Cast<Control>().Single(c => c.GetType().Name == "ApplicationTitleBar");
        Check(caption.Top == 0 && toolbar.Top >= caption.Bottom,
            $"Report caption stays above its toolbar; caption={caption.Bounds}, toolbar={toolbar.Bounds}");
        var viewport = surface.Parent!;
        foreach (var size in new[] { new Size(1100, 700), new Size(850, 600), new Size(1250, 800) })
        {
            form.ClientSize = size; Application.DoEvents();
            Check(caption.Top == 0 && toolbar.Top >= caption.Bottom && viewport.Top >= toolbar.Bottom,
                "Resized report content starts below both caption and toolbar");
            Check(viewport.Bottom <= form.ClientSize.Height, "Report viewport remains inside the client area");
        }
        form.WindowState = FormWindowState.Maximized; Application.DoEvents();
        Check(caption.Top == 0 && toolbar.Top >= caption.Bottom && viewport.Top >= toolbar.Bottom,
            "Maximized report retains caption, toolbar and content ordering");
    }

    private static void CheckValidationBadges()
    {
        using var form=new Form {ClientSize=new Size(640,180)};
        var input=HumanUi.TextBox(false,30);
        var field=HumanUi.Field("Primary Phone (optional)",input);
        field.Dock=DockStyle.None;field.Height=76;form.Controls.Add(field);
        form.Show();
        using var validator=new FormValidator();
        foreach(int width in new[]{180,300,520})
        {
            field.Width=width;field.PerformLayout();
            validator.Begin();validator.Check(input,false,"Enter a full Pakistan phone number");validator.Finish();
            var caption=field.Controls.OfType<Label>().Single();
            var badge=caption.Controls.OfType<Label>().Single();
            int occupied=TextRenderer.MeasureText(caption.Text,caption.Font).Width+caption.Padding.Horizontal+AppScale.Scale(8);
            Check(badge.Width==0||badge.Left>=occupied,"Validation feedback must not paint over a long field caption");
            Check(input.IsInvalid,"Layout fitting must retain the validation state");
            Check(badge.Text=="Enter a full Pakistan phone number","Elision must preserve the full accessible validation message");
        }
        form.Close();
        // Exercise the real admission header without creating its handle or SQL loaders.
        using var admission=new FMSC_SMS.Pages.Students.Admissions.StudentAdmissionPage();
        var badgeField=typeof(FMSC_SMS.Pages.Students.Admissions.StudentAdmissionPage)
            .GetField("studentStatusBadge",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var status=(Label)badgeField.GetValue(admission)!;
        var row=(TableLayoutPanel)status.Parent!.Parent!;
        row.Dock=DockStyle.None; // exercise actual header at explicit available widths
        foreach(int width in new[]{850,1500})
        {
            row.Width=width;row.PerformLayout();
            foreach(string text in new[]{"New Admission","Active","Left"})
            {
                status.Text=text;row.PerformLayout();status.Parent.PerformLayout();
                Check(status.Width>=TextRenderer.MeasureText(text,status.Font).Width+status.Padding.Horizontal,
                    $"Admission header must show its complete ordinary status: {text}; badge={status.Width}, host={status.Parent.Width}, row={row.Width}, column={row.ColumnStyles[1].Width}");
            }
        }
        Check(!admission.IsHandleCreated,"Header regression must never start SQL loaders");
        foreach(string actionField in new[]{"findButton","newAdmissionButton","collectPaymentButton"})
        {
            var action=(Button)typeof(FMSC_SMS.Pages.Students.Admissions.StudentAdmissionPage)
                .GetField(actionField,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(admission)!;
            Check(action.AutoSize&&action.GetPreferredSize(Size.Empty).Width>=
                TextRenderer.MeasureText(action.Text,action.Font).Width+action.Padding.Horizontal,
                "Admission toolbar preferred width must fit the full caption and padding: "+action.Text);
        }
    }
}
