using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using System.Diagnostics;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
DataTable Sample(int count = 3)
{
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int)); table.Columns.Add("Name", typeof(string));
    table.Columns.Add("Amount", typeof(decimal)); table.Columns.Add("Date", typeof(DateTime));
    for (int i = 0; i < count; i++) table.Rows.Add(i, i % 2 == 0 ? $"Name {i}" : DBNull.Value, i * 100m, new DateTime(2026, 1, 1).AddDays(i));
    return table;
}
SqlParameter[] Parameters(int user = 1) => new[] { DatabaseHelper.Int("@UserId", user) };
int loads = 0;
Task<DataTable> Load(SqlParameter[] _) { loads++; return Task.FromResult(Sample()); }
LookupDataCache.Invalidate();
var first = await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default);
first.Rows[0]["Name"] = "Edited";
var second = await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default);
Check(loads == 1 && (string)second.Rows[0]["Name"] == "Name 0", "Cached results must be independently editable");
foreach (string read in new[] { "CheckClassFeeConfiguration", "CheckStudentConsistency", "DailyClosingSnapshot", "PreviewLifecycleDateRepair", "PreviewMigrationBillingCleanup", "PreviewMonthlyFees", "PreviewPostingReversal", "ReportClassSectionLookup", "msg.ReadComposeTemplates" })
{
    LookupDataCache.OperationCompleted(read);
    await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default);
    Check(loads == 1, $"Read-only {read} must preserve lookups");
}
Check(StudentSearchCache.Invalidations == 0, "Read-only checks must preserve student searches");
LookupDataCache.OperationCompleted("SaveCampus");
await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default);
Check(loads == 2 && StudentSearchCache.Invalidations == 1, "Writes must invalidate cached data");
LookupDataCache.OperationCompleted("UnknownOperation");
await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default);
Check(loads == 3, "Unknown operations must still invalidate conservatively");
await LookupDataCache.GetAsync("GetCampuses", Parameters(2), Load, default);
Check(loads == 4, "Users must not share lookup entries");
await LookupDataCache.GetAsync("GetOutstandingAndAgingReport", Parameters(), Load, default);
await LookupDataCache.GetAsync("GetOutstandingAndAgingReport", Parameters(), Load, default);
Check(loads == 6, "Financial balances must never be cached");

LookupDataCache.Invalidate();
var gate = new TaskCompletionSource<DataTable>(TaskCreationOptions.RunContinuationsAsynchronously);
int concurrentLoads = 0;
Task<DataTable> SharedLoad(SqlParameter[] _) { Interlocked.Increment(ref concurrentLoads); return gate.Task; }
using var cancellation = new CancellationTokenSource();
var cancelled = LookupDataCache.GetAsync("GetCampuses", Parameters(), SharedLoad, cancellation.Token);
var retained = LookupDataCache.GetAsync("GetCampuses", Parameters(), SharedLoad, default);
cancellation.Cancel();
try { await cancelled; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { checks++; }
gate.SetResult(Sample()); await retained;
Check(concurrentLoads == 1, "Cancelling one caller must preserve the shared load");

LookupDataCache.Invalidate();
var staleGate = new TaskCompletionSource<DataTable>(TaskCreationOptions.RunContinuationsAsynchronously);
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int generations = 0;
using var originalCommand = new SqlCommand();
using var retryCommand = new SqlCommand();
Task<DataTable> ChangingLoad(SqlParameter[] parameters)
{
    if (Interlocked.Increment(ref generations) == 1)
    {
        originalCommand.Parameters.AddRange(parameters);
        started.SetResult();
        return staleGate.Task;
    }
    retryCommand.Parameters.AddRange(parameters);
    var fresh = Sample(); fresh.Rows[0]["Name"] = "Fresh"; return Task.FromResult(fresh);
}
var racing = LookupDataCache.GetAsync("GetCampuses", Parameters(), ChangingLoad, default);
await started.Task; LookupDataCache.Invalidate(); staleGate.SetResult(Sample());
Check((string)(await racing).Rows[0]["Name"] == "Fresh" && generations == 2, "Invalidation during loading must discard stale results");
Check((int)retryCommand.Parameters["@UserId"].Value == 1,
    "An invalidated lookup retries with fresh command parameters and preserves the captured user");
LookupDataCache.Invalidate();
try { await LookupDataCache.GetAsync("GetCampuses", Parameters(), _ => Task.FromException<DataTable>(new InvalidOperationException("Failure")), default); }
catch (InvalidOperationException) { checks++; }
Check((await LookupDataCache.GetAsync("GetCampuses", Parameters(), Load, default)).Rows.Count == 3, "Failed lookups must remain retryable");

using var source = Sample(20000);
using (var reader = source.CreateDataReader())
{
    var result = await AsyncTableLoader.ReadAsync(reader, "Table", default);
    Check(result.Rows.Count == source.Rows.Count && result.Columns.Count == source.Columns.Count, "Loader must preserve shape");
    for (int i = 0; i < result.Rows.Count; i++)
        Check(result.Rows[i].ItemArray.SequenceEqual(source.Rows[i].ItemArray) && result.Rows[i].RowState == DataRowState.Unchanged, "Buffer reuse must preserve independent row values and state");
}
using (var empty = Sample(0).CreateDataReader())
    Check((await AsyncTableLoader.ReadAsync(empty, "Empty", default)).Columns.Count == 4, "Empty results retain schema");
using (var reader = new DataTableReader(new[] { Sample(2), Sample(1) }))
{
    Check((await AsyncTableLoader.ReadAsync(reader, "Table", default)).Rows.Count == 2, "First result is read independently");
    Check(await reader.NextResultAsync(), "Loader must leave later results available");
    Check((await AsyncTableLoader.ReadAsync(reader, "Table1", default)).Rows.Count == 1, "Later result is read independently");
}
using (var reader = source.CreateDataReader())
{
    using var stop = new CancellationTokenSource(); stop.Cancel();
    try { await AsyncTableLoader.ReadAsync(reader, "Table", stop.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { checks++; }
}
var register = new DataTable(); register.Columns.Add("AdmissionNumber", typeof(string));
foreach (string number in new[] { "10b", "2", "10a", "QR-1", "", "999999999999999999999" }) register.Rows.Add(number);
AdmissionNumbers.AddSortColumns(register); register.AcceptChanges();
int changed = 0; register.RowChanged += (_, _) => changed++;
AdmissionNumbers.AddSortColumns(register);
Check(changed == 0 && register.Rows.Cast<DataRow>().All(r => r.RowState == DataRowState.Unchanged), "Repeated sorting must not change rows or emit events");
register.Rows[0]["AdmissionNumber"] = "1"; AdmissionNumbers.AddSortColumns(register);
Check((long)register.Rows[0]["__AdmissionNumberBase"] == 1, "Edited numbers must refresh derived keys");
Check(AdmissionNumbers.Sort(register).Rows[0]["AdmissionNumber"].Equals("1"), "Natural number order must be preserved");
Console.WriteLine($"PASS: {checks} loader, cache concurrency/invalidation and sorting checks.");
Console.WriteLine($"PASS: {await WorkflowLoadingChecks.RunAsync()} report pagination, cancellation and picker generation checks.");
Console.WriteLine($"PASS: {await RecordOpeningChecks.RunAsync()} latest-read and register-order equivalence checks.");

if (args.Contains("--profile"))
{
    WorkflowLoadingChecks.ProfilePagination();
    RecordOpeningChecks.ProfileSorting();
    async Task<DataTable> Baseline()
    {
        using var reader = source.CreateDataReader(); var table = source.Clone();
        while (await reader.ReadAsync()) { var buffer = new object[reader.FieldCount]; reader.GetValues(buffer); table.Rows.Add(buffer); }
        table.AcceptChanges(); return table;
    }
    async Task<DataTable> Improved() { using var reader = source.CreateDataReader(); return await AsyncTableLoader.ReadAsync(reader, "Table", default); }
    foreach (var run in new Func<Task<DataTable>>[] { Baseline, Improved }) { using var warm = await run(); }
    foreach (var (label, run) in new[] { ("Before", (Func<Task<DataTable>>)Baseline), ("After", (Func<Task<DataTable>>)Improved) })
    {
        long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) { using var result = await run(); }
        watch.Stop();
        Console.WriteLine($"PROFILE {label}: 5 x {source.Rows.Count} synthetic rows, {watch.Elapsed.TotalMilliseconds:F1} ms, {(GC.GetAllocatedBytesForCurrentThread()-before)/1024:N0} KiB allocated. Excludes SQL, WinForms and retained process memory.");
    }
}

Console.WriteLine($"PASS: {await StatementReadBatchChecks.RunAsync()} nonblocking statement read/assembly checks.");
