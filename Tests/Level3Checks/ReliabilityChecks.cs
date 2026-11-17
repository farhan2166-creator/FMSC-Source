using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

// Compiled only into the SQL test runner. Production binaries contain no fault switches.
internal sealed class ReliabilityChecks(Fixture f, Report r)
{
    private int student, enrollment;
    private long charge;
    private long communicationRecipient,communicationScheduled,manualPending,manualOpened;
    private string? backup, restored, snapshot;
    private Guid Owner => Guid.Parse(Environment.GetEnvironmentVariable("FMSC_TEST_RUN_ID")!);
    private string Catalog => new SqlConnectionStringBuilder(DatabaseHelper.ConnectionString).InitialCatalog;

    internal async Task Run()
    {
        bool ready = await r.Stage("L3 fixture: production admission for controlled faults", () => {
            Sql.ValidateTarget();
            var row = Sql.Proc("AdmitStudent", f.AdmissionArgs("Reliability Example")).Rows[0];
            student = Convert.ToInt32(row["AdmissionId"]); enrollment = Convert.ToInt32(row["EnrollmentId"]);
            charge = Convert.ToInt64(Sql.Scalar("SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F;", Sql.P("@E", enrollment), Sql.P("@F", f.Tuition)));
            r.Check(Balance() == 7000, "Fault fixture has a production-created opening bill of 7000.");
            r.Measurements["SqlContainerCpuLimit"] = 2;
            r.Measurements["SqlContainerMemoryMiB"] = 3072;
            return Task.CompletedTask;
        });
        await r.Stage("L3-REC-01 | uncommitted production collection rollback", Rollback, ready);
        await r.Stage("L3-REC-03 | terminate owned SQL session during uncommitted write", Disconnect, ready);
        await r.Stage("L3-REC-04 | command timeout, rollback and deliberate retry", Timeout, ready);
        await r.Stage("L3-REC-05 | killed process retains collection journal across restart", JournalRestart, ready);
        await r.Stage("L3-REC-06 | cancelled SQL command rolls back staged collection", Cancel, ready);
        await r.Stage("L3-FIN-07 | concurrent production posting and competing collections", Concurrent, ready);
        await r.Stage("L3-FIN-05 | exact correction and reversal ledger persistence", Adjustments, ready);
        await r.Stage("L3-SEC-04 | SQL permissions rechecked after role removal", StalePermission, ready);
        bool communicationReady=await r.Stage("L3-COM-01 | reviewed communication before backup",PrepareCommunicationRecovery,ready);
        bool saved = await r.Stage("L3-DB-01 | checksum backup and VERIFYONLY", Backup, ready && communicationReady);
        await r.Stage("L3-COM-02 | durable post-backup submission without HTTP",ClaimCommunicationRecovery,saved);
        bool loaded = await r.Stage("L3-DB-02 | restore to absent owned catalog and new files", Restore, saved);
        await r.Stage("L3-DB-03 | restored compatibility, complete ledger and CHECKDB", CheckRestore, loaded);
        await r.Stage("L3-COM-03 | restored pending message cannot repeat submission",RestoredCommunicationRecovery,loaded);
        await r.Stage("L3-DB-04 | old, new and structurally incompatible schema refusal", Compatibility, ready);
        await r.Stage("L3 database target guards | wrong owner and non-test catalog refusal", TargetGuards, ready);
        await r.Stage("L3-PRF-01 | 1000 production admissions and repeated real searches", SearchWorkload, ready);
        await r.Stage("L3-PRF-04 | repeated full register and financial report queries", ReportWorkload, ready);
        await r.Stage("L3 restored catalog cleanup | ownership checked before DROP", CleanupRestore, loaded);
    }

    private decimal Balance() => Sql.Money("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=@E;", Sql.P("@E", enrollment));
    private int Payments() => Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments WHERE EnrollmentId=@E;", Sql.P("@E", enrollment));
    private SqlParameter[] Collection(decimal amount = 100) => Lifecycle.CollectionArgs(student, enrollment, f.MonthlyDate, amount, new[] { (charge, amount) })
        .Concat(new[] { Sql.P("@UserId", f.Admin) }).ToArray();
    private void StageCollection(SqlConnection connection, SqlTransaction transaction)
    {
        using var command = new SqlCommand("dbo.ProcessFeeTransaction", connection, transaction) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.AddRange(Collection());
        using var reader = command.ExecuteReader();
        while (reader.Read()) { }
    }
    private Task Rollback()
    {
        Sql.ValidateTarget(); int before = Payments(); decimal due = Balance();
        using (var c = DatabaseHelper.GetConnection()) {
            c.Open(); using var transaction = c.BeginTransaction(); StageCollection(c, transaction); transaction.Rollback();
        }
        r.Check(Payments() == before && Balance() == due, "Rolling back an outer transaction removes the production payment and allocations together.");
        return Task.CompletedTask;
    }
    private async Task Disconnect()
    {
        Sql.ValidateTarget(); int before = Payments(); decimal due = Balance();
        var settings = new SqlConnectionStringBuilder(DatabaseHelper.ConnectionString) { ApplicationName = "FMSC Level3 " + Owner.ToString("N"), Pooling = false };
        using var c = new SqlConnection(settings.ConnectionString); c.Open();
        using var id = new SqlCommand("SELECT @@SPID;", c); int session = Convert.ToInt32(id.ExecuteScalar());
        using var transaction = c.BeginTransaction(); StageCollection(c, transaction);
        Sql.ValidateTarget();
        r.Check(Sql.Count("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id=@Id AND program_name=@Name AND is_user_process=1;", Sql.P("@Id", session), Sql.P("@Name", settings.ApplicationName)) == 1,
            "Only this run's explicitly identified write session can be terminated.");
        Sql.Query("KILL " + session.ToString(CultureInfo.InvariantCulture) + ";");
        var deadline = Stopwatch.StartNew();
        while (Sql.Count("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id=@Id AND program_name=@Name;", Sql.P("@Id", session), Sql.P("@Name", settings.ApplicationName)) != 0 && deadline.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
        r.Check(Payments() == before && Balance() == due, "Terminating the staged SQL write rolls back the payment and preserves dues on a separate connection.");
    }
    private async Task Timeout()
    {
        Sql.ValidateTarget(); int before = Payments(); decimal due = Balance();
        using (var c = DatabaseHelper.GetConnection()) {
            c.Open(); using var transaction = c.BeginTransaction(); StageCollection(c, transaction);
            using var command = new SqlCommand("WAITFOR DELAY '00:00:05';", c, transaction) { CommandTimeout = 1 };
            try { await command.ExecuteNonQueryAsync(); throw new AssertionFailure("Timeout fixture completed unexpectedly."); }
            catch (SqlException ex) when (ex.Number == -2) { r.Check(true, "The SQL client actually reported its command timeout."); r.ExpectedRefusals++; }
            transaction.Rollback();
        }
        r.Check(Payments() == before && Balance() == due, "Timed-out outer write leaves no payment or allocation after explicit rollback.");
        Sql.Proc("ProcessFeeTransaction", Collection());
        r.Check(Payments() == before + 1 && Balance() == due - 100, "Only a deliberate retry after confirmed rollback records one collection.");
    }
    private async Task Cancel()
    {
        Sql.ValidateTarget(); int before = Payments(); decimal due = Balance();
        using (var c = DatabaseHelper.GetConnection()) {
            c.Open(); using var transaction = c.BeginTransaction(); StageCollection(c, transaction);
            using var command = new SqlCommand("WAITFOR DELAY '00:00:05';", c, transaction);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await command.ExecuteNonQueryAsync(cancel.Token); throw new AssertionFailure("Cancelled command completed unexpectedly."); }
            catch (OperationCanceledException) { r.Check(true, "Cancellation reached the pending SQL command."); }
            catch (SqlException ex) when (ex.Number == 0 && cancel.IsCancellationRequested) { r.Check(true, "SQL client acknowledged user cancellation after the token was requested."); }
            transaction.Rollback();
        }
        r.Check(Payments() == before && Balance() == due, "Cancelled staged collection creates no persisted payment or allocation.");
    }
    internal static async Task<int> JournalChild()
    {
        Sql.ValidateTarget();
        using var journal = CollectionRecoveryJournal.Begin("Synthetic interrupted collection");
        Console.WriteLine("JOURNAL_FLUSHED"); Console.Out.Flush();
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan);
        return 1;
    }
    private async Task JournalRestart()
    {
        Sql.ValidateTarget(); int before = Payments();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if ((Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--level3-journal-child");
        using var child = Process.Start(start) ?? throw new AssertionFailure("Journal child did not start.");
        try {
            string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            r.Check(ready == "JOURNAL_FLUSHED", "Separate application-code process flushed its production journal before termination.");
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        var pending = CollectionRecoveryJournal.ReadPending();
        r.Check(pending is not null && Payments() == before, "A new process can reload the killed writer's pending journal without inventing a payment.");
        for (int i = 0; i < 3; i++) {
            try { using var ignored = CollectionRecoveryJournal.Begin("Must not resubmit"); throw new AssertionFailure("Retained journal allowed resubmission."); }
            catch (CollectionRecoveryException) { r.Check(CollectionRecoveryJournal.ReadPending()?.Id == pending!.Id, "Repeated recovery preserves the same pending identity and refuses duplicate writes."); }
        }
        CollectionRecoveryJournal.ClearReviewed(pending!.Id);
        r.Check(CollectionRecoveryJournal.ReadPending() is null && Payments() == before, "Explicit review clears only the matching intent and leaves SQL unchanged.");
    }
    private async Task Concurrent()
    {
        Sql.ValidateTarget();
        DateTime month = f.MonthlyDate.AddMonths(1);
        DataTable Post() => Sql.Proc("PostSelectedMonthlyFees", Sql.P("@UserId", f.Admin), Sql.P("@AcademicYearId", f.NextYear), Sql.P("@BillingMonth", month),
            Sql.TvpParameter("@Selection", "PostingSelectionType", Tvp.PostingSelections(new[] { (enrollment, f.Tuition, month) })), Sql.P("@Reason", "Synthetic competing posting"));
        using var gate = new ManualResetEventSlim(false);
        var posts = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { gate.Wait(); return Convert.ToInt32(Post().Rows[0]["ChargesCreated"]); })).ToArray(); gate.Set();
        var counts = await Task.WhenAll(posts);
        r.Check(counts.Sum() == 1 && Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F AND BillingMonth=@M;", Sql.P("@E", enrollment), Sql.P("@F", f.Tuition), Sql.P("@M", month)) == 1,
            "Four competing production posting requests commit exactly one monthly charge.");
        long original = charge;
        charge = Convert.ToInt64(Sql.Scalar("SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F AND BillingMonth=@M;", Sql.P("@E", enrollment), Sql.P("@F", f.Tuition), Sql.P("@M", month)));
        int before = Payments(); decimal due = Balance();
        using var collectGate = new ManualResetEventSlim(false);
        try {
            var payments = Enumerable.Range(0, 2).Select(_ => Task.Run(() => {
                collectGate.Wait();
                try { Sql.Proc("ProcessFeeTransaction", Collection(5000)); return 1; }
                catch (SqlException ex) when (ex.Number == 54013) { return 0; }
            })).ToArray(); collectGate.Set();
            var committed = await Task.WhenAll(payments);
            r.Check(committed.Sum() == 1 && Payments() == before + 1 && Balance() == due - 5000, "Competing full-balance allocations permit one payment and refuse the stale duplicate.");
            r.Check(Sql.Money("SELECT SUM(Amount) FROM dbo.FeePaymentAllocations WHERE BillingChargeId=@C;", Sql.P("@C", charge)) == 5000, "Independent allocation query proves the charge was collected once.");
        } finally { charge = original; }
    }
    private Task Adjustments()
    {
        Sql.ValidateTarget();
        var admitted = Sql.Proc("AdmitStudent", f.AdmissionArgs("Adjustment Example")).Rows[0];
        int id = Convert.ToInt32(admitted["AdmissionId"]), year = Convert.ToInt32(admitted["EnrollmentId"]);
        long bill = Convert.ToInt64(Sql.Scalar("SELECT MIN(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=@E;", Sql.P("@E", year)));
        decimal Due() => Sql.Money("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=@E;", Sql.P("@E", year));
        SqlParameter[] Collect(decimal amount) => Lifecycle.CollectionArgs(id, year, f.MonthlyDate, amount, new[] { (bill, amount) }).Concat(new[] { Sql.P("@UserId", f.Admin) }).ToArray();
        SqlParameter[] Change(string type, int payment, decimal amount) => new[] {
            Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", id), Sql.P("@EnrollmentId", year), Sql.P("@TransactionType", type), Sql.P("@Amount", amount),
            Sql.P("@TransactionDate", f.MonthlyDate), Sql.P("@PaymentMethod", null), Sql.P("@ReferenceNumber", null), Sql.P("@Remarks", "Fictional adjustment integrity"),
            Sql.P("@FeePaymentId", payment), new SqlParameter("@BillingChargeId", SqlDbType.BigInt) { Value = DBNull.Value },
            Sql.TvpParameter("@Allocations", "FeePaymentAllocationType", Tvp.FeeAllocations(Array.Empty<(long, decimal)>())) };
        int first = Convert.ToInt32(Sql.Proc("ProcessFeeTransaction", Collect(100)).Rows[0]["FeePaymentId"]);
        Sql.Proc("ProcessFeeTransaction", Change("Correction", first, 80));
        r.Check(Due() == 6920 && Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE RelatedFeePaymentId=@P AND Status=N'Completed';", Sql.P("@P", first)) == -20,
            "Correcting 100 to 80 persists a negative 20 adjustment and leaves exactly 6920 due.");
        int second = Convert.ToInt32(Sql.Proc("ProcessFeeTransaction", Collect(200)).Rows[0]["FeePaymentId"]);
        r.Check(Due() == 6720, "A distinct additional 200 collection reduces dues before reversal.");
        Sql.Proc("ProcessFeeTransaction", Change("Reversal", second, 200));
        r.Check(Due() == 6920 && Convert.ToString(Sql.Scalar("SELECT Status FROM dbo.FeePayments WHERE FeePaymentId=@P;", Sql.P("@P", second))) == "Reversed",
            "Reversal preserves its original payment identity/history and reopens exactly 200.");
        r.Check(Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE EnrollmentId=@E AND Status=N'Completed';", Sql.P("@E", year)) == 80 &&
            Sql.Money("SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.EnrollmentId=@E AND P.Status=N'Completed';", Sql.P("@E", year)) == 80,
            "Separate SQL reads reconcile completed net collections and allocations to exactly 80 after correction/reversal.");
        return Task.CompletedTask;
    }
    private async Task StalePermission()
    {
        Sql.ValidateTarget();
        Sql.Proc("RequirePermission", Sql.P("@UserId", f.Restricted), Sql.P("@PermissionCode", "Fees.Collect"));
        Sql.Query("DELETE dbo.UserRoles WHERE UserId=@U;", Sql.P("@U", f.Restricted));
        try {
            await r.Refused("The next SQL request rejects a stale actor after its role is removed", () => {
                Sql.Proc("RequirePermission", Sql.P("@UserId", f.Restricted), Sql.P("@PermissionCode", "Fees.Collect")); return Task.CompletedTask;
            }, 50003);
            Sql.Proc("RequirePermission", Sql.P("@UserId", f.Admin), Sql.P("@PermissionCode", "Fees.Collect"));
            r.Check(true, "Administrator authority remains available while restricted permissions are revoked.");
        } finally { Sql.Query("INSERT dbo.UserRoles(UserId,RoleId) SELECT UserId,RoleId FROM dbo.Users WHERE UserId=@U;", Sql.P("@U", f.Restricted)); }
    }
    private string FinancialSnapshot(SqlConnection? connection = null)
    {
        const string query = """
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(
              SELECT (SELECT * FROM dbo.Students ORDER BY AdmissionId FOR JSON PATH,INCLUDE_NULL_VALUES) Students,
                (SELECT * FROM dbo.Enrollments ORDER BY EnrollmentId FOR JSON PATH,INCLUDE_NULL_VALUES) Enrollments,
                (SELECT * FROM dbo.BillingCharges ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES) Charges,
                (SELECT * FROM dbo.FeePayments ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES) Payments,
                (SELECT * FROM dbo.FeePaymentAllocations ORDER BY FeePaymentId,BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES) Allocations
              FOR JSON PATH,WITHOUT_ARRAY_WRAPPER))),2);
            """;
        if (connection is null) return Convert.ToString(Sql.Scalar(query))!;
        using var command = new SqlCommand(query, connection); return Convert.ToString(command.ExecuteScalar())!;
    }
    private async Task PrepareCommunicationRecovery()
    {
        Sql.ValidateTarget();
        Sql.Query("UPDATE dbo.Students SET ContactNumber='03008000981' WHERE AdmissionId=@Id",Sql.P("@Id",student));
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        Sql.Proc("msg.ConfigureChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        communicationRecipient=await ConfirmCommunicationRecovery();
        communicationScheduled=await ConfirmCommunicationRecovery();
        using(var scheduleBatch=Sql.Query("SELECT BatchId FROM msg.Recipient WHERE RecipientId=@Id",Sql.P("@Id",communicationScheduled)))
            Sql.Proc("msg.ScheduleReviewedBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",scheduleBatch.Rows[0]["BatchId"]),
                Sql.P("@DueUtc",DateTime.UtcNow.AddDays(1)),Sql.P("@ExpiresUtc",DateTime.UtcNow.AddDays(2)),Sql.P("@Priority","Normal"));
        Sql.Proc("msg.EnableManualWhatsApp",Sql.P("@UserId",f.Admin));
        manualPending=await ConfirmCommunicationRecovery(manual:true);
        manualOpened=await ConfirmCommunicationRecovery(manual:true);
        Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",true));
        try
        {
            using var opened=Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualOpened));
            r.Check(Convert.ToBoolean(opened.Rows[0]["Opened"]),"API Pause All preserves explicitly controlled manual WhatsApp handoff.");
            Sql.Proc("msg.RecordShellHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualOpened),
                Sql.P("@OpenCount",opened.Rows[0]["OpenCount"]),Sql.P("@RenderedBody","Synthetic retained recovery notice & اطلاع"),Sql.P("@ShellOpened",true));
            await r.Refused("The same pause still blocks a provider attempt",()=>{
                Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",communicationRecipient),
                    Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));return Task.CompletedTask;
            },51240);
        }
        finally{Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",false));}
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",communicationRecipient))==0,
            "The backed-up reviewed message is genuinely unattempted.");
    }
    private async Task<long> ConfirmCommunicationRecovery(bool manual=false)
    {
        var target=new CommunicationTarget(CommunicationAudience.Student,0,0,student,0,manual ? "WhatsApp" : "Sms");
        var preview=await CommunicationRecipientResolver.ReadAsync(target);
        using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
        foreach(var entry in preview.Entries.Where(entry=>entry.Included))recipients.Rows.Add(entry.Kind,entry.RecordId,entry.PhoneE164);
        r.Check(recipients.Rows.Count==1,"The recovery message resolves one real, consented school record.");
        string body=manual ? "Synthetic retained recovery notice & اطلاع" : "Synthetic recovery notice.";
        var args=new List<SqlParameter>{Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
            Sql.P("@Channel",target.Channel),Sql.P("@Purpose","Individual"),Sql.P("@Body",body),Sql.P("@Audience","Student"),
            Sql.P("@CampusId",0),Sql.P("@ClassId",0),Sql.P("@StudentId",student),Sql.P("@StaffId",0),
            new SqlParameter("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients}};
        if(manual)args.Add(new SqlParameter("@ReviewedFees",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients.Clone()});
        else args.AddRange(new[]{Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),
            Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target,preview,body)),Sql.P("@TemplateJson",null)});
        using var saved=Sql.Proc(manual ? "msg.ConfirmReviewedManualBatch" : "msg.ConfirmReviewedApiBatch",args.ToArray());
        return Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",saved.Rows[0]["BatchId"])));
    }
    private Task ClaimCommunicationRecovery()
    {
        using var claimed=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",communicationRecipient),
            Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(Convert.ToBoolean(claimed.Rows[0]["Claimed"]),"A real SQL attempt is persisted after the backup; no provider request is issued.");
        return Task.CompletedTask;
    }
    private async Task RestoredCommunicationRecovery()
    {
        using(var connection=RestoredConnection())
        using(var command=new SqlCommand("msg.ClaimRecipient",connection){CommandType=CommandType.StoredProcedure})
        {
            command.Parameters.AddRange(new[]{Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",communicationRecipient),
                Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio")});
            bool refused=false;
            try{using var reader=command.ExecuteReader();while(reader.Read()){} }
            catch(SqlException ex) when(ex.Number==51341){refused=true;r.ExpectedRefusals++;}
            r.Check(refused,"Independent SQL restore history holds a restored Pending message even on an account without its original local journal.");
        }
        string original=DatabaseHelper.ConnectionString;
        try
        {
            // RestoredConnection above verified this exact owner-marked catalog.
            // ValidateTarget reads the source environment and would reset us to
            // the original database, defeating the real restored-state check.
            DatabaseHelper.ConnectionString=new SqlConnectionStringBuilder(original){InitialCatalog=restored!,Pooling=false}.ConnectionString;
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",communicationRecipient))==0,
                "The restored-message refusal rolls back the attempted claim completely.");
            await r.Refused("Restored approval evidence cannot be rewritten to bypass the restore hold",()=>{
                Sql.Query("UPDATE B SET RestoreBoundaryHash=msg.RestoreBoundary() FROM msg.Batch B JOIN msg.Recipient R ON R.BatchId=B.BatchId WHERE R.RecipientId=@Id",
                    Sql.P("@Id",communicationRecipient));return Task.CompletedTask;
            },51343);
            Guid Batch(long recipient)=>(Guid)Sql.Scalar("SELECT BatchId FROM msg.Recipient WHERE RecipientId=@Id",Sql.P("@Id",recipient))!;
            using var queue=Sql.Proc("msg.ReadMyUnfinishedBatches",Sql.P("@UserId",f.Admin));
            r.Check(new[]{communicationRecipient,communicationScheduled,manualPending,manualOpened}.All(id=>queue.AsEnumerable().Any(row=>
                (Guid)row["BatchId"]==Batch(id) && Convert.ToBoolean(row["RecoveryBlocked"]) && Convert.ToString(row["QueueStatus"])=="Needs Attention")),
                "Restored API and manual batches are visible in Needs Attention before any external operation.");
            using var api=Sql.Proc("msg.ReadApiBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(communicationRecipient)));
            r.Check(api.Rows.Count==1 && Convert.ToBoolean(api.Rows[0]["RecoveryBlocked"]),"The API sending session reads the same authoritative restore hold.");
            foreach(long manual in new[]{manualPending,manualOpened})
            {
                using var rows=Sql.Proc("msg.ReadManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(manual)));
                r.Check(rows.Rows.Count==1 && Convert.ToBoolean(rows.Rows[0]["RecoveryBlocked"]),"A restored manual session exposes its recovery boundary without opening a chat.");
                await r.Refused("A restored manual message cannot open or reopen WhatsApp",()=>{
                    Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manual));return Task.CompletedTask;
                },51341);
                await r.Refused("A restored manual send cannot be claimed without explicit recovery verification",()=>{
                    Sql.Proc("msg.MarkManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manual),Sql.P("@Outcome","ReportedSent"));return Task.CompletedTask;
                },51115);
            }
            await r.Refused("Stale callbacks cannot add or acknowledge shell evidence after recovery",()=>{
                Sql.Proc("msg.RecordShellHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualOpened),
                    Sql.P("@OpenCount",1),Sql.P("@RenderedBody","Synthetic retained recovery notice & اطلاع"),Sql.P("@ShellOpened",true));return Task.CompletedTask;
            },51341);
            using var retained=Sql.Proc("msg.ReadManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(manualOpened)));
            r.Check(Convert.ToString(retained.Rows[0]["RetainedBody"])=="Synthetic retained recovery notice & اطلاع",
                "Recovery exposes immutable historical text, rather than rendering today's financial values as earlier content.");
            r.Check(Sql.Count("SELECT OpenCount FROM msg.ManualHandoff WHERE RecipientId=@Id",Sql.P("@Id",manualPending))==0 &&
                Sql.Count("SELECT OpenCount FROM msg.ManualHandoff WHERE RecipientId=@Id",Sql.P("@Id",manualOpened))==1,
                "Refused manual recovery leaves opening history unchanged.");
            Sql.Proc("msg.MarkManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualOpened),Sql.P("@Outcome","ReportedSent"),Sql.P("@RecoveredReport",true));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ManualHandoff WHERE RecipientId=@Id AND State='ReportedSent'",Sql.P("@Id",manualOpened))==1,
                "An explicit checked operator report records only self-reported sending, without provider delivery claims.");
            Sql.Proc("msg.HoldScheduledBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(communicationScheduled)));
            object? dueBefore=Sql.Scalar("SELECT DueUtc FROM msg.BatchSchedule WHERE BatchId=@Id",Sql.P("@Id",Batch(communicationScheduled)));
            await r.Refused("A restored approval cannot be rescheduled to clear its recovery hold",()=>{
                Sql.Proc("msg.ScheduleReviewedBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(communicationScheduled)),
                    Sql.P("@DueUtc",DateTime.UtcNow.AddHours(1)),Sql.P("@ExpiresUtc",DateTime.UtcNow.AddHours(2)),Sql.P("@Priority","Normal"));return Task.CompletedTask;
            },51341);
            await r.Refused("A restored approval cannot be released through schedule resumption",()=>{
                Sql.Proc("msg.ReleaseScheduledHold",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(communicationScheduled)));return Task.CompletedTask;
            },51341);
            using(var takeoverChoices=Sql.Proc("msg.ReadTakeoverCandidates",Sql.P("@UserId",f.Admin)))
                r.Check(!takeoverChoices.AsEnumerable().Any(row=>(Guid)row["BatchId"]==Batch(communicationScheduled)),
                    "Takeover offers only resumable work and keeps restored approvals in the recovery Queue.");
            int takeovers=Sql.Count("SELECT COUNT(*) FROM msg.BatchTakeover");
            await r.Refused("Administrator takeover cannot release a restored approval or record a misleading resumption",()=>{
                Sql.Proc("msg.TakeOverScheduledBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",Batch(communicationScheduled)),
                    Sql.P("@Reason","Synthetic recovery review"));return Task.CompletedTask;
            },51341);
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.BatchTakeover")==takeovers &&
                Sql.Count("SELECT COUNT(*) FROM msg.BatchSchedule WHERE BatchId=@Id AND HoldForReview=1",Sql.P("@Id",Batch(communicationScheduled)))==1 &&
                Equals(Sql.Scalar("SELECT DueUtc FROM msg.BatchSchedule WHERE BatchId=@Id",Sql.P("@Id",Batch(communicationScheduled))),dueBefore),
                "Refused recovery actions preserve the due time, hold and original responsibility audit.");
            long fresh=await ConfirmCommunicationRecovery();
            using var claimed=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",fresh),
                Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
            r.Check(Convert.ToBoolean(claimed.Rows[0]["Claimed"]),"An explicitly reviewed new request after restore can proceed through the normal safeguards.");
        }
        finally{DatabaseHelper.ConnectionString=original;}
    }
    private Task Backup()
    {
        Sql.ValidateTarget(); snapshot = FinancialSnapshot(); backup = "/var/opt/mssql/data/level3_" + Owner.ToString("N") + ".bak";
        r.Check(Convert.ToInt32(Sql.Query("EXEC master.dbo.xp_fileexist @Path;", Sql.P("@Path", backup)).Rows[0][0]) == 0, "Backup destination must be absent before writing.");
        Sql.Query("BACKUP DATABASE [" + Catalog + "] TO DISK=@Path WITH COPY_ONLY,CHECKSUM;", Sql.P("@Path", backup));
        Sql.Query("RESTORE VERIFYONLY FROM DISK=@Path WITH CHECKSUM;", Sql.P("@Path", backup));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name=@Db AND is_copy_only=1 AND has_backup_checksums=1 AND type='D';", Sql.P("@Db", Catalog)) == 1 && FinancialSnapshot() == snapshot,
            "One copy-only checksum backup is engine-verified without changing the source financial snapshot.");
        return Task.CompletedTask;
    }
    private Task Restore()
    {
        Sql.ValidateTarget(); restored = "FMSC_AuditChecks_Restore_" + Owner.ToString("N");
        r.Check(Sql.Count("SELECT COUNT(*) FROM sys.databases WHERE name=@Db;", Sql.P("@Db", restored)) == 0, "Restore target must be absent; existing databases are never replaced.");
        var files = Sql.Query("RESTORE FILELISTONLY FROM DISK=@Path;", Sql.P("@Path", backup));
        r.Check(files.Rows.Count == 2, "Backup has exactly the expected data/log pair.");
        var moves = new List<string>(); int ordinal = 0;
        foreach (DataRow file in files.Rows) {
            string path = "/var/opt/mssql/data/level3_restore_" + Owner.ToString("N") + "_" + ordinal++ + (Convert.ToString(file["Type"]) == "L" ? ".ldf" : ".mdf");
            var exists = Sql.Query("EXEC master.dbo.xp_fileexist @Path;", Sql.P("@Path", path));
            r.Check(Convert.ToInt32(exists.Rows[0][0]) == 0, "Each restore destination file is absent before RESTORE.");
            moves.Add("MOVE N'" + Convert.ToString(file["LogicalName"])!.Replace("'", "''") + "' TO N'" + path + "'");
        }
        Sql.Query("RESTORE DATABASE [" + restored + "] FROM DISK=@Path WITH CHECKSUM," + string.Join(',', moves) + ";", Sql.P("@Path", backup));
        using var c = RestoredConnection();
        r.Check(FinancialSnapshot(c) == snapshot, "Restored students, enrollments, charges, payments and allocations match the source snapshot exactly.");
        return Task.CompletedTask;
    }
    private SqlConnection RestoredConnection()
    {
        Sql.ValidateTarget();
        if (restored != "FMSC_AuditChecks_Restore_" + Owner.ToString("N")) throw new AssertionFailure("Unexpected restored identity.");
        var target = new SqlConnectionStringBuilder(DatabaseHelper.ConnectionString) { InitialCatalog = restored, Pooling = false };
        var c = new SqlConnection(target.ConnectionString); c.Open();
        try {
            using var marker = new SqlCommand("SELECT TRY_CONVERT(uniqueidentifier,value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_Level1Owner';", c);
            if (marker.ExecuteScalar() is not Guid id || id != Owner) throw new AssertionFailure("Restored ownership mismatch; operation refused.");
            return c;
        } catch { c.Dispose(); throw; }
    }
    private Task CheckRestore()
    {
        using var c = RestoredConnection(); DatabaseCompatibility.Validate(c);
        using var command = new SqlCommand("DBCC CHECKDB WITH NO_INFOMSGS,ALL_ERRORMSGS,TABLERESULTS;", c) { CommandTimeout = 90 };
        var errors = new DataTable(); using var adapter = new SqlDataAdapter(command); adapter.Fill(errors);
        r.Check(errors.Rows.Count == 0 && FinancialSnapshot(c) == snapshot, "Restored schema passes the production runtime contract, CHECKDB and exact persisted-history comparison.");
        return Task.CompletedTask;
    }
    private Task Compatibility()
    {
        Sql.ValidateTarget(); string before = FinancialSnapshot();
        int requiredVersion=int.Parse(DatabaseHelper.RequiredDatabaseSchemaVersion,System.Globalization.CultureInfo.InvariantCulture);
        foreach (string version in new[] { (requiredVersion-1).ToString(System.Globalization.CultureInfo.InvariantCulture), (requiredVersion+1).ToString(System.Globalization.CultureInfo.InvariantCulture) }) {
            Sql.Query("INSERT dbo.DatabaseVersion(VersionNumber) VALUES(@V);", Sql.P("@V", version));
            try {
                using var c = DatabaseHelper.GetConnection(); c.Open();
                try { DatabaseCompatibility.Validate(c); throw new AssertionFailure("Wrong schema version was accepted."); }
                catch (DatabaseCompatibilityException) { r.Check(true, "Runtime validation refuses an incompatible schema version."); }
            } finally { Sql.Query("DELETE dbo.DatabaseVersion WHERE VersionNumber=@V;", Sql.P("@V", version)); }
        }
        using (var c = DatabaseHelper.GetConnection()) {
            c.Open(); using var transaction = c.BeginTransaction();
            // Temporarily hide one required procedure, then restore it in finally.
            using (var rename = new SqlCommand("EXEC sys.sp_rename N'dbo.GetFeeStatement',N'FMSC_Level3HiddenStatement';", c, transaction)) rename.ExecuteNonQuery();
            transaction.Commit();
        }
        try {
            using var c = DatabaseHelper.GetConnection(); c.Open();
            try { DatabaseCompatibility.Validate(c); throw new AssertionFailure("A current-schema database with a missing required procedure was accepted."); }
            catch (DatabaseCompatibilityException) { r.Check(true, "A matching version does not bypass structural requirements."); }
        } finally { Sql.Query("EXEC sys.sp_rename N'dbo.FMSC_Level3HiddenStatement',N'GetFeeStatement';"); }
        // Schema 11 and 12 must be checked just like the baseline objects:
        // version 12 alone must never authorize using a damaged school DB.
        void MustRejectHiddenObject(string original, string hidden, string label, bool column = false)
        {
            string suffix = column ? ",'COLUMN'" : "";
            Sql.Query($"EXEC sys.sp_rename N'{original}',N'{hidden}'{suffix};");
            try
            {
                using var c = DatabaseHelper.GetConnection(); c.Open();
                try { DatabaseCompatibility.Validate(c); throw new AssertionFailure("Missing " + label + " was accepted."); }
                catch (DatabaseCompatibilityException) { r.Check(true, "Runtime refuses missing " + label + " at schema 12."); }
            }
            finally
            {
                // Keep exact object/table/column identity in the isolated fixture.
                string parent = original[..original.LastIndexOf('.')];
                Sql.Query($"EXEC sys.sp_rename N'{(parent + "." + hidden)}',N'{original[(original.LastIndexOf('.') + 1)..]}'{suffix};");
            }
        }
        MustRejectHiddenObject("dbo.GetPrintedCertificateSignature", "FMSC_Level3HiddenSignature", "schema-11 signature procedure");
        MustRejectHiddenObject("dbo.SchoolProfile.PrincipalSignatureImage", "FMSC_Level3HiddenSignatureImage", "schema-11 signature field", column: true);
        MustRejectHiddenObject("dbo.AdministratorRecoveryHost", "FMSC_Level3HiddenRecoveryHost", "schema-12 registered recovery host");
        MustRejectHiddenObject("dbo.AdministratorRecoveryEvents", "FMSC_Level3HiddenRecoveryAudit", "schema-12 recovery audit");
        using (var c = DatabaseHelper.GetConnection()) { c.Open(); DatabaseCompatibility.Validate(c); }
        r.Check(FinancialSnapshot() == before, "Compatibility refusal and fixture restoration preserve every financial and academic row.");
        return Task.CompletedTask;
    }
    private Task TargetGuards()
    {
        Sql.ValidateTarget(); string original = Environment.GetEnvironmentVariable("FMSC_AUDIT_CONNECTION")!; Guid originalOwner = Owner;
        try {
            var target = new SqlConnectionStringBuilder(original) { InitialCatalog = "FMSC_DB" };
            Environment.SetEnvironmentVariable("FMSC_AUDIT_CONNECTION", target.ConnectionString);
            try { Sql.ValidateTarget(); throw new AssertionFailure("Production catalog guard accepted a non-test catalog."); }
            catch (InvalidOperationException) { r.Check(true, "Production catalog is rejected before any SQL connection."); }
            Environment.SetEnvironmentVariable("FMSC_AUDIT_CONNECTION", original);
            Environment.SetEnvironmentVariable("FMSC_TEST_RUN_ID", Guid.NewGuid().ToString());
            try { Sql.ValidateTarget(); throw new AssertionFailure("Wrong owner was accepted."); }
            catch (InvalidOperationException) { r.Check(true, "A catalog name alone cannot authorize destructive testing."); }
        } finally { Environment.SetEnvironmentVariable("FMSC_AUDIT_CONNECTION", original); Environment.SetEnvironmentVariable("FMSC_TEST_RUN_ID", originalOwner.ToString()); Sql.ValidateTarget(); }
        return Task.CompletedTask;
    }
    private Task SearchWorkload()
    {
        Sql.ValidateTarget(); int before = Sql.Count("SELECT COUNT(*) FROM dbo.Students;"); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) Sql.Proc("AdmitStudent", f.AdmissionArgs("Load Example " + i.ToString("D4", CultureInfo.InvariantCulture)));
        r.Measurements["AdmissionBatch1000Seconds"] = watch.Elapsed.TotalSeconds;
        r.Measurements["SyntheticStudentCount"] = Sql.Count("SELECT COUNT(*) FROM dbo.Students;");
        r.Check(r.Measurements["SyntheticStudentCount"] == before + 1000, "Every bounded workload student was created by production admission logic.");
        var samples = new List<double>();
        for (int i = 0; i < 20; i++) {
            watch.Restart(); var results = Sql.Proc("GetStudents", Sql.P("@UserId", f.Admin), Sql.P("@CampusId", f.Campus), Sql.P("@AcademicYearId", f.NextYear), Sql.P("@Search", "Load Example 09"), Sql.P("@IncludeFormerStudents", true));
            samples.Add(watch.Elapsed.TotalMilliseconds);
            r.Check(results.Rows.Count == 100, "Production search returns the expected 100 matches without omissions or unrelated students.");
        }
        samples.Sort(); r.Measurements["SearchMedianMs"] = samples[samples.Count / 2]; r.Measurements["SearchP95Ms"] = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
        return Task.CompletedTask;
    }
    private Task ReportWorkload()
    {
        var samples = new List<double>();
        for (int i = 0; i < 10; i++) {
            var watch = Stopwatch.StartNew();
            var register = Sql.Proc("GetStudents", Sql.P("@UserId", f.Admin), Sql.P("@CampusId", 0), Sql.P("@AcademicYearId", 0), Sql.P("@Search", null), Sql.P("@IncludeFormerStudents", true));
            var statement = Sql.Proc("GetFeeStatementTransactions", Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", student), Sql.P("@FromDate", f.AdmissionDate), Sql.P("@ToDate", f.MonthlyDate.AddMonths(2)));
            samples.Add(watch.Elapsed.TotalMilliseconds);
            r.Check(register.Rows.Count == Sql.Count("SELECT COUNT(*) FROM dbo.Students;"), "Full production register returns every synthetic student.");
            decimal displayedDue = statement.AsEnumerable().Where(row => Convert.ToString(row["TransactionType"]) == "CHARGE").Sum(row => Convert.ToDecimal(row["DebitAmount"]) - Convert.ToDecimal(row["PaidAmount"]));
            r.Check(displayedDue == Balance(), "Production statement debits minus payments match an independent ledger query.");
        }
        samples.Sort(); r.Measurements["RegisterAndStatementMedianMs"] = samples[samples.Count / 2]; r.Measurements["RegisterAndStatementP95Ms"] = samples[^1];
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId JOIN dbo.BillingCharges B ON B.BillingChargeId=A.BillingChargeId WHERE P.EnrollmentId<>B.EnrollmentId OR P.AdmissionId<>B.AdmissionId;") == 0,
            "Fault, concurrency, restore and workload checks leave no cross-student allocation.");
        return Task.CompletedTask;
    }
    private Task CleanupRestore()
    {
        using (var c = RestoredConnection()) { }
        Sql.ValidateTarget();
        Sql.Query("ALTER DATABASE [" + restored + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + restored + "];");
        r.Check(Sql.Count("SELECT COUNT(*) FROM sys.databases WHERE name=@Db;", Sql.P("@Db", restored)) == 0, "Only the independently verified owned restored catalog was dropped.");
        return Task.CompletedTask;
    }
}
