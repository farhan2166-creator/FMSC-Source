using System.Data;
using System.Drawing.Imaging;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Certificates;

internal static partial class Program
{
    /// <summary>
    /// Bounded noninteractive screenshots of the real current WinForms controls.
    /// All person/certificate data in these captures is fictional and injected
    /// directly into read-only presentation controls, never persisted.
    /// </summary>
    private static void CaptureStudentCertificateScreenshots()
    {
        const BindingFlags privateFields = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, Permissions: CurrentUser.GetPermissions().ToArray());
        string root = Path.GetFullPath("artifacts/student-certificates-preview");
        Directory.CreateDirectory(root);
        try
        {
            CurrentUser.SignIn(991, "CI screenshot fixture", "Administrator", false,
                Array.Empty<string>());

            using var students = new DataTable();
            students.Columns.Add("AdmissionId", typeof(int));
            students.Columns.Add("StudentName");
            students.Columns.Add("AdmissionNumber");
            students.Columns.Add("FatherName");
            students.Columns.Add("CampusName");
            students.Columns.Add("ClassName");
            students.Columns.Add("SectionName");
            students.Columns.Add("Status");
            students.Columns.Add("DateOfBirth", typeof(DateTime));
            students.Columns.Add("EnrollmentId", typeof(int));
            students.Columns.Add("ClassId", typeof(int));
            students.Columns.Add("ProgramType");
            DataRow sample = students.Rows.Add(41, "Ayesha Khan", "SYN-2026-041",
                "Imran Khan", "Main Campus", "Class 10", "A", "Active",
                new DateTime(2010, 3, 14), 101, 10, "School");

            using var refreshed = StudentCertificateRegisterReader.CreateTable();
            using (var workspace = new StudentCertificatesWorkspacePage(false, (_, _, _, _) => refreshed.Copy(), () => "fixture"))
            using (var host = FixtureHost(workspace, new Size(1510, 910), "Student Certificates Workspace"))
            {
                // Populate the actual WinForms presentation with synthetic rows.
                // SetStudent deliberately does not fire a load event or query SQL.
                var picker = (StudentPicker)typeof(StudentCertificatesWorkspacePage)
                    .GetField("student", privateFields)!.GetValue(workspace)!;
                picker.SetSource(students);
                picker.SetStudent(sample);
                using var records = StudentCertificateRegisterReader.CreateTable();
                records.Rows.Add("Character", "Character", "12 Oct 2026", "CHAR-102",
                    "Issue logged", "Collected", "13 Oct 2026", "12 Oct 2026 09:30",
                    102, 902, new DateTime(2026,10,12,9,30,0));
                records.Rows.Add("Regular", "Regular Student", "15 Sep 2026", "REG-341",
                    "Issue logged", "Not Collected", "—", "15 Sep 2026 11:00",
                    341, 441, new DateTime(2026,9,15,11,0,0));
                records.Rows.Add("Sports", "Sports", "09 Aug 2026", "SPORT-77",
                    "Issue logged", "Collected", "10 Aug 2026", "09 Aug 2026 14:10",
                    77, 0, new DateTime(2026,8,9,14,10,0));
                records.Rows.Add("DOB", "Date of Birth", "20 Jun 2026", "DOB-29",
                    "Issue logged", "Not Collected", "—", "20 Jun 2026 10:15",
                    29, 329, new DateTime(2026,6,20,10,15,0));
                records.Rows.Add("NOC", "No Objection", "01 May 2026", "NOC-58",
                    "Issue logged", "Collected", "03 May 2026", "01 May 2026 12:40",
                    58, 0, new DateTime(2026,5,1,12,40,0));
                var grid = (DataGridView)typeof(V2Page)
                    .GetField("Grid", privateFields)!.GetValue(workspace)!;
                typeof(StudentCertificatesWorkspacePage).GetField("records", privateFields)!.SetValue(workspace, records);
                grid.DataSource = records.DefaultView;
                host.Show(); Application.DoEvents();
                var certificateButtons = (List<ModernButton>)typeof(StudentCertificatesWorkspacePage)
                    .GetField("certificateButtons", privateFields)!.GetValue(workspace)!;
                Check(certificateButtons.Select(button => button.Text).SequenceEqual(
                    StudentCertificateRegisterReader.Kinds.Select(kind => kind.Caption)),
                    "Each permitted certificate type has its own button.");
                var filter = (ModernComboBox)typeof(StudentCertificatesWorkspacePage).GetField("typeFilter", privateFields)!.GetValue(workspace)!;
                filter.SelectedItem = "Character";
                Check(grid.Rows.Count == 1, "Type filtering shows only the selected certificate type.");
                filter.SelectedIndex = 0;
                Check(grid.Rows.Count == 5, "Clearing the type filter restores the student's register.");
                if (grid.Rows.Count > 0)
                {
                    grid.ClearSelection();
                    grid.CurrentCell = grid.Rows[0].Cells["Type"];
                    grid.Rows[0].Selected = true;
                }
                Capture(host, root, "01-workspace-student-issued-certificates.png");
                host.ClientSize = new Size(980, 750);
                Application.DoEvents();
                var split = (SplitContainer)typeof(StudentCertificatesWorkspacePage).GetField("workspaceSplit", privateFields)!.GetValue(workspace)!;
                Check(split.Panel1.ClientSize.Width>0 && split.Panel2.ClientSize.Width>0,
                    "Compact certificate workspaces retain usable search and detail areas.");
                Check(split.Panel1.Controls.Cast<Control>().Any(control=>control is TableLayoutPanel),
                    "Search and saved-copy controls remain available without fixing the sidebar grid.");
                Capture(host, root, "02-workspace-compact-window.png");
                foreach (var kind in StudentCertificateRegisterReader.Kinds)
                {
                    workspace.OpenEditor(kind, 0, 0, loadSelection: false);
                    Application.DoEvents();
                    var editor = (Form?)typeof(StudentCertificatesWorkspacePage)
                        .GetField("activeEditor", privateFields)!.GetValue(workspace);
                    Check(editor is { TopLevel: false, Visible: true } && !grid.Visible,
                        kind.Caption + " opens in place of the register.");
                    Check(!workspace.IsBusy, kind.Caption + " can be left while the editor is idle.");
                    editor!.CancelButton!.PerformClick();
                    Application.DoEvents();
                    Check(grid.Visible && picker.Enabled, kind.Caption + " returns to the register and student search.");
                }
                foreach (DataRow copy in records.Rows) refreshed.ImportRow(copy);
                refreshed.Rows.Add("Sports", "Sports", "08 Aug 2026", "SPORT-80",
                    "Issue logged", "Not Collected", "—", "08 Aug 2026 10:00",
                    80, 0, new DateTime(2026, 8, 8, 10, 0, 0), 0);
                var sports = StudentCertificateRegisterReader.Kinds.Single(kind => kind.Key == "Sports");
                workspace.OpenEditor(sports, 0, 0, loadSelection: false);
                Application.DoEvents();
                CompleteSportsSave();
                Check(grid.CurrentRow?.DataBoundItem is DataRowView created && Convert.ToInt32(created["RecordId"]) == 80,
                    $"A confirmed create returns to the grid and selects the new backdated copy. Rows: {grid.Rows.Count}, current: {(grid.CurrentRow?.DataBoundItem as DataRowView)?["RecordId"]}.");
                workspace.OpenEditor(sports, 80, 0, loadSelection: false);
                Application.DoEvents();
                CompleteSportsSave();
                Check(grid.CurrentRow?.DataBoundItem is DataRowView edited && Convert.ToInt32(edited["RecordId"]) == 80,
                    "A confirmed edit returns to the same saved copy.");
                workspace.OpenEditor(sports, 80, 0, loadSelection: false);
                Application.DoEvents();
                ModalPageCloseGuard.Attach(host, workspace);
                host.Close();
                Check(!host.Visible, "Closing the app host succeeds with an idle certificate editor open.");

                void CompleteSportsSave()
                {
                    var editor = (Form)typeof(StudentCertificatesWorkspacePage)
                        .GetField("activeEditor", privateFields)!.GetValue(workspace)!;
                    var owner = editor.Controls.OfType<SportsCertificatePage>().Single();
                    var saved = (Action<DataRowView?>)typeof(SportsCertificatePage)
                        .GetField("workspaceSaved", privateFields)!.GetValue(owner)!;
                    saved(null);
                    PumpUntil(() => grid.Visible && !workspace.IsBusy, "Confirmed save closes the editor and refreshes the grid.");
                }
            }

            CaptureCompactEditor("03-character-certificate-details-dialog.png", "Character",
                () => new CharacterCertificatePage(false), root, sample);
            CaptureCompactEditor("04-regular-student-details-dialog.png", "Regular Student",
                () => new RegularStudentCertificatePage(false), root, sample);
            CaptureCompactEditor("05-leaving-certificate-details-dialog.png", "Leave",
                () => new LeaveCertificatePage(null, false), root, sample);
            CaptureCompactEditor("06-provisional-certificate-details-dialog.png", "Provisional",
                () => new ProvisionalCertificatePage(false), root, sample);
            CaptureCompactEditor("07-date-of-birth-details-dialog.png", "Date of Birth",
                () => new DateOfBirthCertificatePage(false), root, sample);
            CaptureCompactEditor("08-sports-certificate-details-dialog.png", "Sports",
                () => new SportsCertificatePage(false), root, sample);
            CaptureCompactEditor("09-no-objection-details-dialog.png", "No Objection",
                () => new NoObjectionCertificatePage(false), root, sample);
            DataRow collegeStudent = students.Rows.Add(42, "Zara Ali", "SYN-2026-042",
                "Rashid Ali", "College Campus", "Year 2", "B", "Active",
                new DateTime(2007, 5, 12), 102, 11, "College");
            CaptureCompactEditor("10-college-leaving-details-dialog.png", "College Leaving",
                () => new CollegeLeaveCertificatePage(false), root, collegeStudent);

            var files = Directory.GetFiles(root, "*.png");
            Check(files.Length == 10 && files.All(f => new FileInfo(f).Length > 4000),
                "All eight actual certificate details dialogs and two workspace layouts render with fictional fixture data.");
            Console.WriteLine("SCREENSHOTS: " + string.Join(", ", files.Select(Path.GetFileName)));
            Console.WriteLine("Noninteractive control render only; no database, operator input, printer, or installation was exercised.");
        }
        finally
        {
            CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName,
                previous.MustChangePassword, previous.Permissions);
        }
    }

    private static Form FixtureHost(Control page, Size size, string title)
    {
        var form = new Form
        {
            Text = title,
            AutoScaleMode = AutoScaleMode.None,
            ClientSize = size,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            BackColor = AppTheme.PageBackground
        };
        page.Dock = DockStyle.Fill;
        form.Controls.Add(page);
        return form;
    }

    private static void CaptureCompactEditor(string name, string type,
        Func<Control> factory, string folder, DataRow sample)
    {
        using var dialog = CertificateWorkspaceDetailsDialog.BuildForScreenshot(
            type, sample, factory);
        dialog.Show(); Application.DoEvents();
        Check(dialog.CancelButton is ModernButton { Text: "Return to Workspace", DialogResult: DialogResult.None },
            type + " routes Escape through the guarded close action.");
        var studentDisplay = Descendants(dialog).OfType<Label>().Single(label => label.Text ==
            CertificateUi.GetString(sample, "AdmissionNumber") + " — " + CertificateUi.GetString(sample, "StudentName"));
        Check(studentDisplay.Height >= AppMetrics.TextHeight(studentDisplay.Font),
            type + " keeps the selected student legible without fixing its line height.");
        Check(Descendants(dialog).OfType<StudentPicker>().All(picker => !picker.Visible && !picker.Enabled),
            type + " keeps the selected student locked without a second search.");
        var card = Descendants(dialog).OfType<HumanCard>()
            .FirstOrDefault(item => item.Parent is Panel && item.Width > AppScale.Scale(500));
        Check(card is not null && card.Height > AppScale.Scale(250) &&
            card.Right <= dialog.ClientSize.Width && card.Bottom <= dialog.ClientSize.Height,
            type + " shows its existing details card inside the compact dialog.");
        Check(Descendants(card!).OfType<TableLayoutPanel>().All(table =>
            table.Controls.Cast<Control>().OrderBy(control => table.GetPositionFromControl(control).Row)
                .ThenBy(control => table.GetPositionFromControl(control).Column)
                .Select(control => control.TabIndex).SequenceEqual(Enumerable.Range(0, table.Controls.Count))),
            type + " tabs through fields in visual row and column order.");
        var hiddenEditor = dialog.Controls.OfType<V2Page>().Single();
        Check(Descendants(hiddenEditor).All(control => !control.TabStop),
            type + " skips the hidden editor controls when tabbing.");
        var visibleWriteButtons = Descendants(card!).OfType<ModernButton>()
            .Where(button => button.Visible && (button.Text == UiText.Certificates.Create || button.Text == "Save Changes"))
            .Select(button => button.Text);
        Check(visibleWriteButtons.SequenceEqual(new[] { UiText.Certificates.Create }),
            type + " offers only Create when preparing a new certificate.");

        if (type == "Provisional")
        {
            var fields = Descendants(card!).OfType<TableLayoutPanel>()
                .FirstOrDefault(table => table.Dock == DockStyle.Top && table.RowCount >= 3);
            Check(fields is not null && fields.RowCount is 3 or 5 &&
                fields.Height >= fields.RowCount * AppMetrics.FieldRowHeight,
                "The provisional modal allocates every required row at its current width.");
            Check(fields?.Parent is TableLayoutPanel body &&
                body.RowStyles[body.GetRow(fields)].Height >= fields.Height,
                "The provisional fields are not clipped by the original full-page row budget.");
        }
        var controls = new Control[] { dialog }.Concat(Descendants(dialog));
        var layoutEvidence = controls
            .Where(c => c is Form or HumanCard or TableLayoutPanel or FlowLayoutPanel ||
                c is Panel && c.Controls.Count > 0)
            .Take(175)
            .Select(c =>
                $"{c.GetType().Name}: x={c.Left} y={c.Top} w={c.Width} h={c.Height}" +
                $" dock={c.Dock} visible={c.Visible} autosize={c.AutoSize}" +
                $" parent={c.Parent?.GetType().Name} parentSize={c.Parent?.ClientSize}")
            .ToArray();
        File.WriteAllLines(Path.Combine(folder, "layout-" + name + ".txt"), layoutEvidence);
        Capture(dialog, folder, name);
        typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { Keys.Escape });
        Check(!dialog.Visible, type + " closes when Escape is pressed.");
    }

    private static void Capture(Form form, string folder, string name)
    {
        form.PerformLayout();
        Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        using (var canvas = Graphics.FromImage(image))
        using (var bg = new SolidBrush(Color.FromArgb(238, 35, 47, 64)))
        using (var fg = new SolidBrush(Color.White))
        using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
        {
            const string note = "CI RENDER · FICTIONAL DATA · NOT A LIVE DESKTOP SESSION";
            var measurement = canvas.MeasureString(note, font);
            var area = new RectangleF(10, image.Height - 31,
                Math.Min(image.Width - 20, measurement.Width + 24), 25);
            canvas.FillRectangle(bg, area);
            canvas.DrawString(note, font, fg, area.Left + 9, area.Top + 2);
        }
        string path = Path.Combine(folder, name);
        image.Save(path, ImageFormat.Png);
        Console.WriteLine($"CAPTURED {path} ({image.Width}x{image.Height})");
    }
}
