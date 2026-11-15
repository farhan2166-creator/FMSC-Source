using System.Net;
using System.Data;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;

int count=0;
void Check(bool value,string description){if(!value)throw new Exception(description);count++;}
void Refuse(Action action,string description){try{action();}catch(InvalidOperationException){count++;return;}throw new Exception(description);}
var credential=new CommunicationProviderCredentialStore.Credential(1,"WhatsApp","MetaCloud","123456789","987654321","fictional-token-never-real","");
CommunicationMetaTransport.ValidateCredentials(credential);
Check(!credential.ToString().Contains(credential.ApiToken),"Credential diagnostics redact secrets");
Check(CommunicationProviderCredentialStore.IdentityHash(credential)==CommunicationProviderCredentialStore.IdentityHash(credential with {ApiToken="different-fictional-secret"}),"Token rotation retains account identity");
Check(CommunicationProviderCredentialStore.IdentityHash(credential)!=CommunicationProviderCredentialStore.IdentityHash(credential with {SenderId="987654322"}),"Changing the sender invalidates reviewed identity");
var template=new CommunicationMetaTransport.Template("fee_reminder","en_US","Dear {{1}}, balance PKR {{2}}.","School","Thank you");
var selection=new CommunicationMetaTransport.Selection(template,new[]{"[StudentName]","[OutstandingAmount]"});
var values=new CommunicationPreparedMessages.Values(1,"علی & Ahmed","001",new DateTime(2026,11,1),2300);
Check(CommunicationPreparedMessages.Render(selection.Format(),values)=="School\nDear علی & Ahmed, balance PKR 2,300.00.\nThank you","Rendered preview matches Unicode template and amounts");
using(var payload=JsonDocument.Parse(selection.Payload(values)))
{
    Check(payload.RootElement.GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("text").GetString()==values.RecordName,"JSON parameters preserve Unicode and symbols");
    Check(payload.RootElement.GetProperty("language").GetProperty("code").GetString()=="en_US","Approved language is preserved");
}
Refuse(()=>new CommunicationMetaTransport.Selection(template,new[]{"[StudentName]"}).Format(),"Incomplete mappings refused");
Refuse(()=>new CommunicationMetaTransport.Selection(template,new[]{"[Unknown]","[OutstandingAmount]"}).Format(),"Unknown school fields refused");
Refuse(()=>CommunicationMetaTransport.ValidateTemplate(template with {Body="Dear {{2}}"}),"Nonconsecutive fields refused");
Refuse(()=>CommunicationMetaTransport.ValidateTemplate(template with {Body="Dear {{name}}"}),"Unsupported named fields never silently replaced");
Refuse(()=>CommunicationMetaTransport.ValidateTemplate(template with {Header="Hello {{1}}"}),"Variable header never silently omitted");
var target=new CommunicationTarget(CommunicationAudience.OutstandingFees,1,2,0,0,"Sms");
var entry=new CommunicationPreviewEntry(1,"Student","Student","+923001234567","Included",2300,"001") {EligiblePhone="+923001234567",PreparedValues=values};
var sibling=new CommunicationPreviewEntry(2,"Sibling","Student",null,"Duplicate destination",500,"002") {EligiblePhone="+923001234567"};
var preview=new CommunicationPreview(new[]{entry,sibling});
string reviewed=CommunicationReviewedContent.ReviewJson(target,preview,"Due [OutstandingAmount]");
Check(reviewed==CommunicationReviewedContent.ReviewJson(target,new CommunicationPreview(new[]{sibling,entry}),"Due [OutstandingAmount]"),"Review signatures are independent of query row order");
Check(reviewed!=CommunicationReviewedContent.ReviewJson(target,new CommunicationPreview(new[]{entry,sibling with {RecordId=3}}),"Due [OutstandingAmount]"),"Same total with changed child membership invalidates review");
Check(reviewed!=CommunicationReviewedContent.ReviewJson(target,new CommunicationPreview(new[]{entry with {PreparedValues=values with {Outstanding=2200}},sibling}),"Due [OutstandingAmount]"),"Changed current balance invalidates review");
Check(reviewed!=CommunicationReviewedContent.ReviewJson(target,new CommunicationPreview(new[]{entry with {PhoneE164="+923001234568"},sibling}),"Due [OutstandingAmount]"),"Changed destination invalidates review");
Check(JsonSerializer.Deserialize<CommunicationReviewedContent.Reviewed[]>(reviewed)![0].Text=="Due 2,300.00","Saved review retains exact rendered content for history and resuming");
var savedReview=JsonSerializer.Deserialize<CommunicationReviewedContent.Reviewed[]>(reviewed)![0];
Check(CommunicationReviewedContent.Matches(savedReview,target,preview,entry,"Due [OutstandingAmount]")
    && !CommunicationReviewedContent.Matches(savedReview with {Text="A different reviewed message"},target,preview,entry,"Due [OutstandingAmount]"),
    "Dispatch refuses a saved preview whose text differs from the actual rendered message, even if its hash was retained");
string approved="""{"data":[{"name":"fee_reminder","language":"en_US","status":"APPROVED","components":[{"type":"HEADER","format":"TEXT","text":"School"},{"type":"BODY","text":"Dear {{1}}, balance PKR {{2}}."},{"type":"FOOTER","text":"Thank you"}]},{"name":"draft","language":"en","status":"PENDING","components":[{"type":"BODY","text":"Draft"}]},{"name":"media","language":"en","status":"APPROVED","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Body"}]},{"name":"buttons","language":"en","status":"APPROVED","components":[{"type":"BODY","text":"Body"},{"type":"BUTTONS","buttons":[]}]}]}""";
var handler=new Fake(async request=>{
    Check(request.RequestUri!.Host=="graph.facebook.com" && request.RequestUri.Scheme=="https","Only official fixed HTTPS host used");
    Check(!request.RequestUri.Query.Contains(credential.ApiToken) && request.Headers.Authorization?.Parameter==credential.ApiToken,"Bearer token stays out of URL");
    if(request.Method==HttpMethod.Get)return Json(HttpStatusCode.OK,request.RequestUri.AbsolutePath.EndsWith("phone_numbers")
        ? """{"data":[{"id":"987654321","display_phone_number":"+92 300 1234567"}]}""" : approved);
    using var document=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Check(document.RootElement.GetProperty("type").GetString()=="template" && document.RootElement.GetProperty("to").GetString()=="923001234567","POST uses approved template and digits-only reviewed number");
    Check(document.RootElement.GetProperty("template").GetProperty("name").GetString()==template.Name,"Exact approved template is sent");
    return Json(HttpStatusCode.OK,"""{"messages":[{"id":"wamid.fictional"}]}""");
});
using(var client=new HttpClient(handler))
{
    await CommunicationMetaTransport.VerifyAsync(credential,client,default);
    var templates=await CommunicationMetaTransport.ReadTemplatesAsync(credential,client,default);
    Check(templates.Count==1 && templates[0]==template,"Only supported APPROVED templates are offered; content exactly retained");
    var outcome=await CommunicationMetaTransport.SendOnceAsync(credential,"+923001234567",selection.Payload(values),client,default);
    Check(outcome.State=="Accepted" && outcome.ProviderMessageId=="wamid.fictional","Submission never claims sent or delivered");
    Check(handler.Posts==1,"One explicit send makes exactly one POST");
}
foreach(var scenario in new[]{(HttpStatusCode.BadRequest,"{}","Failed"),(HttpStatusCode.Unauthorized,"{}","Failed"),
    (HttpStatusCode.InternalServerError,"{}","Unknown"),(HttpStatusCode.TooManyRequests,"{}","Unknown"),
    (HttpStatusCode.OK,"{}","Unknown"),(HttpStatusCode.OK,"[]","Unknown"),(HttpStatusCode.OK,"bad json","Unknown"),
    (HttpStatusCode.OK,new string('x',1024*1024+1),"Unknown")})
{
    var fake=new Fake(_=>Task.FromResult(Json(scenario.Item1,scenario.Item2)));
    using var client=new HttpClient(fake);
    var outcome=await CommunicationMetaTransport.SendOnceAsync(credential,"+923001234567",selection.Payload(values),client,default);
    Check(outcome.State==scenario.Item3 && fake.Posts==1,"Definite rejection vs uncertain result is preserved without retry");
}
foreach(bool cancelled in new[]{false,true})
{
    var fake=new Fake(_=>cancelled ? throw new OperationCanceledException() : throw new HttpRequestException());
    using var client=new HttpClient(fake);
    var outcome=await CommunicationMetaTransport.SendOnceAsync(credential,"+923001234567",selection.Payload(values),client,default);
    Check(outcome.State=="Unknown" && fake.Posts==1,"Cancellation or lost response never implies safe resend");
}
var smsCredential=new CommunicationProviderCredentialStore.Credential(1,"Sms","Twilio","AC"+new string('a',32),"+923001234567","fictional-twilio-token","fictional-twilio-token");
var smsHandler=new Fake(async request=>{
    string form=await request.Content!.ReadAsStringAsync();
    Check(form.Contains("Body="+Uri.EscapeDataString(CommunicationPreparedMessages.Render("Hello [StudentName]",values)).Replace("%20","+")),"SMS receives rendered school data, never a raw placeholder");
    return Json(HttpStatusCode.Created,"{\"sid\":\"SM"+new string('d',32)+"\",\"status\":\"delivered\"}");
});
using(var client=new HttpClient(smsHandler))
{
    var outcome=await CommunicationTwilioTransport.SendOnceAsync(smsCredential,"+923001234567",CommunicationPreparedMessages.Render("Hello [StudentName]",values),client,default);
    Check(outcome.State=="Accepted" && smsHandler.Posts==1,"Even a create response claiming delivered is submission evidence only");
}
var stalled=new Fake(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new StalledBody())}));
using(var client=new HttpClient(stalled){Timeout=TimeSpan.FromMilliseconds(150)})
{
    var outcome=await CommunicationMetaTransport.SendOnceAsync(credential,"+923001234567",selection.Payload(values),client,default);
    Check(outcome.State=="Unknown" && stalled.Posts==1,"Meta response-body streaming obeys the deadline and cannot hang the app");
}
int pages=0;
var paged=new Fake(request=>{
    pages++;
    Check(request.RequestUri!.Host=="graph.facebook.com","Provider paging never follows an arbitrary returned host");
    if(pages==1)return Task.FromResult(Json(HttpStatusCode.OK,"""{"data":[],"paging":{"next":"http://localhost/secrets","cursors":{"after":"opaque & cursor"}}}"""));
    Check(request.RequestUri.Query.Contains("opaque%20%26%20cursor"),"Provider cursor is escaped as query data");
    return Task.FromResult(Json(HttpStatusCode.OK,approved));
});
using(var client=new HttpClient(paged))
{
    var result=await CommunicationMetaTransport.ReadTemplatesAsync(credential,client,default);
    Check(pages==2 && result.Count==1,"Approved template paging uses fixed-host bounded requests");
}
var staticSelection=new CommunicationMetaTransport.Selection(template with {Body="School is closed tomorrow."},Array.Empty<string>());
using(var payload=JsonDocument.Parse(staticSelection.Payload(values)))
    Check(payload.RootElement.GetProperty("components").GetArrayLength()==0,"Static approved templates do not invent empty parameters");
using(var history=new DataTable())
{
    foreach(string field in new[]{"Outcome","Channel","ProviderCode","Audience","Body","SelectedStudentName","SelectedStaffName","TemplateJson"})
        history.Columns.Add(field);
    foreach(string field in new[]{"CampusId","ClassId","StudentId","StaffId"})history.Columns.Add(field,typeof(int));
    var approvedNotice=new CommunicationMetaTransport.Selection(
        new("school_notice","en","School meeting.","",""),Array.Empty<string>());
    history.Rows.Add("Accepted","WhatsApp","MetaCloud","School","School meeting.","","",JsonSerializer.Serialize(approvedNotice),0,0,0,0);
    CurrentUser.Permissions.UnionWith(new[]{"Communication.Send","Students.View"});
    var draft=CommunicationHistoryStore.CreateDraft(history);
    Check(draft is not null && draft.Target.Channel=="WhatsApp" && draft.Body=="School meeting."
        && draft.MetaTemplate?.Template==approvedNotice.Template,"Accepted Meta submission can create a separate reviewed draft with its approved template");
    Check((string)history.Rows[0]["Outcome"]=="Accepted","Preparing another draft never promotes the original submission to Delivered");
    foreach(string state in new[]{"Pending","Opened","Submitted","Unknown"})
    {
        history.Rows[0]["Outcome"]=state;
        Check(CommunicationHistoryStore.CreateDraft(history) is null,"Pending, opened, submitted and uncertain records cannot use Send Again");
    }
    history.Rows[0]["Outcome"]="Accepted";
    history.ImportRow(history.Rows[0]);history.Rows[1]["Outcome"]="Unknown";
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"One uncertain recipient blocks Send Again for the whole mixed batch");
    history.Rows.RemoveAt(1);
    history.Rows[0]["Channel"]="Sms";history.Rows[0]["ProviderCode"]="Twilio";
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"Twilio acceptance still requires its existing delivery reconciliation");
    history.Rows[0]["ProviderCode"]="MetaCloud";
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"Meta acceptance on an unsupported channel stays blocked");
    history.Rows[0]["Channel"]="WhatsApp";
    CurrentUser.Permissions.Remove("Communication.Send");
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"Accepted Meta history does not bypass sending permission");
    CurrentUser.Permissions.Add("Communication.Send");CurrentUser.Permissions.Remove("Students.View");
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"Accepted Meta history does not bypass school-record access");
    CurrentUser.Permissions.Add("Students.View");history.Rows[0]["Audience"]="OutstandingFees";
    Check(CommunicationHistoryStore.CreateDraft(history) is null,"Fee-reminder drafts retain financial-view authorization");
    CurrentUser.Permissions.Clear();
}
var feeChild = new CommunicationFeeDetails.Student(42, "علی & Ahmed", "0042", "Eight - F", 825.50m);
var feeSibling = new CommunicationFeeDetails.Student(43, "علی & Ahmed", "0043", "Nine - A", 424.50m);
var statementDate = new DateTime(2026, 11, 15);
string studentFees = CommunicationFeeDetails.Format(statementDate, new[] { feeChild }, 825.50m, false);
Check(studentFees.Contains("علی & Ahmed") && studentFees.Contains("0042") && studentFees.Contains("Eight - F")
    && studentFees.Contains("825.50") && studentFees.Contains("15-Nov-2026"),
    "Student fee details identify the child, class, actual balance and statement date");
string familyFees = CommunicationFeeDetails.Format(statementDate, new[] { feeChild, feeSibling }, 1250m, true);
Check(familyFees.Contains("0042") && familyFees.Contains("0043") && familyFees.Contains("Eight - F") && familyFees.Contains("Nine - A")
    && familyFees.Contains("825.50") && familyFees.Contains("424.50") && familyFees.Contains("1,250.00"),
    "Same-name siblings retain separate identities and balances alongside their exact family total");
string feeBody = CommunicationFeeDetails.Expand(CommunicationFeeDetails.Template, familyFees);
CommunicationPreparedMessages.ValidateFormat(feeBody, "FeeReminder");
Check(CommunicationPreparedMessages.Render(feeBody, values) == feeBody && !feeBody.Contains(CommunicationFeeDetails.Field),
    "The preview and saved batch use identical resolved fee details without new global template fields");
string edited = "A personal note\n" + CommunicationFeeDetails.Field + "\nPlease contact us tomorrow.";
Check(CommunicationFeeDetails.Expand(edited, studentFees) == edited.Replace(CommunicationFeeDetails.Field, studentFees),
    "Editing the wording around the fee details is preserved during review");
string literalLabels = CommunicationFeeDetails.Format(statementDate,
    new[] { feeChild with { Name = "Ali [OutstandingAmount] {{1}}\nExtra" } }, 825.50m, false);
CommunicationPreparedMessages.ValidateFormat(literalLabels, "FeeReminder");
Check(CommunicationPreparedMessages.Render(literalLabels, values) == literalLabels && !literalLabels.Contains("2,300.00"),
    "Brackets and line breaks in school records cannot inject prepared fields or substitute another child's values");
Refuse(() => CommunicationFeeDetails.Format(statementDate, new[] { feeChild, feeSibling }, 999m, true),
    "A mismatched family total is refused");
Refuse(() => CommunicationFeeDetails.Format(statementDate, new[] { feeChild, feeChild }, 1651m, true),
    "The same student cannot appear twice in a fee breakdown");
Refuse(() => CommunicationFeeDetails.Format(statementDate, new[] { feeChild, feeSibling }, 1250m, false),
    "A student-only message cannot expose another child's balance");
Refuse(() => CommunicationFeeDetails.Format(statementDate, Array.Empty<CommunicationFeeDetails.Student>(), 0m, true),
    "Empty fee details are refused");
Refuse(() => CommunicationFeeDetails.Format(statementDate, new[] { feeChild with { Outstanding = 0 } }, 0m, false),
    "Zero balances cannot be mislabeled as outstanding");
Refuse(() => CommunicationFeeDetails.Expand(CommunicationFeeDetails.Template, ""), "Missing details cannot produce a false empty reminder");
var largeFamily = Enumerable.Range(1, 30).Select(id => new CommunicationFeeDetails.Student(id, "Synthetic child " + id,
    id.ToString(), "Eight - F", 100m)).ToArray();
string largeDetails = CommunicationFeeDetails.Format(statementDate, largeFamily, 3000m, true);
Refuse(() => CommunicationFeeDetails.Expand(CommunicationFeeDetails.Template, largeDetails),
    "An oversized family message fails clearly rather than dropping children or cutting off totals");
Check(CommunicationFeeDetails.Expand("Total PKR 3,000.00", largeDetails) == "Total PKR 3,000.00",
    "Large families can still choose a short balance-only message and share the complete bill image");
Console.WriteLine($"PASS: {count} communication transport, history and reviewed-content behavior checks; no external network, credentials or SQL used.");
static HttpResponseMessage Json(HttpStatusCode status,string text)=>new(status){Content=new StringContent(text,Encoding.UTF8,"application/json")};
sealed class Fake(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond):HttpMessageHandler
{
    internal int Posts;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {if(request.Method==HttpMethod.Post)Posts++;return respond(request);}
}

sealed class StalledBody:MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    {await Task.Delay(Timeout.Infinite,token);return 0;}
}
