using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Profile;

internal static partial class Program
{
    private static void CheckProfileShortcutContext()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(903, "Profile fixture", "Administrator", false, Array.Empty<string>());
            CheckAdministratorProfileShortcuts();
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
    private static void CheckAdministratorProfileShortcuts()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;int opens=0;
        Action<Func<Control>,string,string> navigate=(_,_,_)=>opens++;
        using var page=(StudentProfilePage)Activator.CreateInstance(typeof(StudentProfilePage),flags,null,new object[]{navigate,false},null)!;
        var buttons=(List<ModernButton>)typeof(StudentProfilePage).GetField("studentShortcuts",flags)!.GetValue(page)!;
        Check(buttons.Count>0&&buttons.All(button=>!button.Enabled),"Record-specific profile shortcuts start unavailable with no selected student");
        foreach(var button in buttons)button.PerformClick();Check(opens==0,"Disabled blank-profile shortcuts cannot dispatch an accidental default record page");
        var picker=(StudentPicker)typeof(StudentProfilePage).GetField("student",flags)!.GetValue(page)!;picker.SetStudent(41,"Student");typeof(StudentProfilePage).GetMethod("UpdateStudentShortcuts",flags)!.Invoke(page,null);
        Check(buttons.All(button=>button.Enabled),"A committed profile student makes record-specific shortcuts available");
        // Family now intentionally opens an owned window. Exercise a named
        // navigation shortcut rather than assuming the first button navigates.
        buttons.Single(button=>button.Text=="Open "+PageTitles.StudentRecordChecks).PerformClick();
        Check(opens==1,"A deliberate selected-student review shortcut still navigates normally");
        picker.SetStudent(0,"");typeof(StudentProfilePage).GetMethod("UpdateStudentShortcuts",flags)!.Invoke(page,null);Check(buttons.All(button=>!button.Enabled),"Clearing the committed profile identity removes shortcut targets");
        Check(!page.IsHandleCreated,"Profile shortcut fixture starts no SQL or destination form");

        // The profile overview must be one coherent student, not competing
        // full-height grids or three automatically highlighted selections.
        var cards=Descendants(page).OfType<HumanCard>().ToArray();
        HumanCard Card(string heading) => cards.Single(card=>
            Descendants(card.Header).OfType<Label>().Any(label=>label.Text==heading));
        var overview=Card("Student Overview");
        var family=Card("Family / Siblings");
        var academic=Card("Academic Placement");
        var history=Card("Student History");
        Check(!cards.Any(card=>Descendants(card.Header).OfType<Label>()
                .Any(label=>label.Text=="Find Student")),
            "Student search does not consume a dedicated card");
        var titleHeader=Descendants(page).OfType<TableLayoutPanel>()
            .SingleOrDefault(table=>table.Controls.Contains(picker));
        Check(titleHeader is not null &&
            Descendants(page).Any(control=>ReferenceEquals(control,picker)) &&
            !Descendants(overview).Any(control=>ReferenceEquals(control,picker)),
            "Student selector remains available in the page heading");
        Check(Descendants(picker).OfType<TextBox>()
                .Any(input=>input.AccessibleName=="Student"),
            "Header search retains the accessible Student input identity");
        var profileTitle=Descendants(page).OfType<Label>()
            .FirstOrDefault(label=>(string?)label.Tag=="ThemeHeaderTitle" &&
                !cards.Any(card=>card.Contains(label)));
        Check(profileTitle is { Parent: TableLayoutPanel } && !string.IsNullOrWhiteSpace(profileTitle.Text),
            "The profile page retains an identifiable heading outside the cards without fixing its caption or column geometry");
        // An inline picker must not blank the heading. Test the size contract
        // relative to the live font, rather than fixing pixels or caption text.
        var headerLine=(TableLayoutPanel)profileTitle!.Parent!;
        int requiredTitleWidth=TextRenderer.MeasureText(profileTitle.Text, profileTitle.Font,
            new Size(int.MaxValue,int.MaxValue),
            TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
        Check(profileTitle.AutoSize && profileTitle.MinimumSize.Width>=requiredTitleWidth &&
                headerLine.ColumnStyles[0].SizeType==SizeType.Absolute &&
                headerLine.ColumnStyles[0].Width>=profileTitle.MinimumSize.Width,
            "Inline student search preserves a non-collapsing, auto-sized profile heading");

        var sectionLayout=(TableLayoutPanel)family.Parent!;
        Check(new[]{overview,academic,family,history}.All(card=>
                ReferenceEquals(card.Parent,sectionLayout) && sectionLayout.Controls.Contains(card)),
            "All profile sections remain available without fixing their grid positions or spans");

        // The student overview must retain every profile fact regardless of its grid shape.
        Label ProfileLabel(string field) =>
            (Label)typeof(StudentProfilePage).GetField(field,flags)!.GetValue(page)!;
        var detailGrid=Descendants(overview.Content).OfType<TableLayoutPanel>()
            .Single(table=>table.Controls.Contains(ProfileLabel("nameLabel")));
        Check(new[] { "nameLabel", "fatherLabel", "placementLabel", "genderLabel",
                     "dateOfBirthLabel", "admissionDateLabel", "admissionCampusLabel",
                     "contactLabel", "registrationLabel", "balanceLabel" }
                .All(field=>detailGrid.Controls.Contains(ProfileLabel(field))),
            "Student Overview retains every identity, contact and account field independent of row positions");

        // Verify identity facts without querying a school database. The same
        // original admission record continues to supply the placement summary.
        using var details=new DataTable();
        details.Columns.Add("AdmissionId",typeof(int));
        details.Columns.Add("StudentName");
        details.Columns.Add("FatherName");
        details.Columns.Add("ClassName");
        details.Columns.Add("SectionName");
        details.Columns.Add("Status");
        details.Columns.Add("RegistrationNumber");
        details.Columns.Add("ContactNumber");
        details.Columns.Add("AdmissionDate",typeof(DateTime));
        details.Columns.Add("DateOfBirth",typeof(DateTime));
        details.Columns.Add("Gender");
        details.Columns.Add("AdmissionCampusName");
        details.Rows.Add(41,"Example Student","Example Father","Five","M","Left",
            "REG-41","03000000000",new DateTime(2018,4,7),new DateTime(2010,1,5),"Male","Campus Boys");
        // The production name header separately resolves the authoritative
        // admission number. Verify the new fact mapper without opening SQL.
        typeof(StudentProfilePage).GetMethod("RenderIdentityFacts",flags)!
            .Invoke(page,new object[]{details.Rows[0]});
        string LabelText(string field) =>
            ((Label)typeof(StudentProfilePage).GetField(field,flags)!.GetValue(page)!).Text;
        Check(!LabelText("nameLabel").Contains("Example Father") &&
            LabelText("fatherLabel")=="Father: Example Father" &&
            LabelText("contactLabel").Contains("03000000000") &&
            LabelText("registrationLabel").Contains("REG-41"),
            "Parent, contact and registration remain separate from the main student heading");
        Check(LabelText("admissionDateLabel").Contains(new DateTime(2018,4,7).ToString("dd-MMM-yyyy")) &&
            LabelText("dateOfBirthLabel").Contains(new DateTime(2010,1,5).ToString("dd-MMM-yyyy")) &&
            LabelText("genderLabel").Contains("Male") &&
            LabelText("admissionCampusLabel").Contains("Campus Boys"),
            "Admission and identity facts are shown in the overview rather than the placement table");
        typeof(StudentProfilePage).GetMethod("ClearProfile",flags)!.Invoke(page,null);
        Check(new[]{"fatherLabel","admissionDateLabel","dateOfBirthLabel","genderLabel",
                "admissionCampusLabel"}.All(field=>LabelText(field)==string.Empty),
            "Clearing a selected profile also clears the new identity facts");
    }
}
