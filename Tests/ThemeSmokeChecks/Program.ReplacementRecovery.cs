using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckReplacementRecoveryModal()
    {
        string file = Path.GetTempFileName();
        try
        {
            foreach (string scenario in new[] { "cancel", "success", "failure" })
            {
                using var dialog = new RestoreExistingSchoolForm();
                Field<TextBox>(dialog, "localPath").Text = file;
                Field<TextBox>(dialog, "administrator").Text = "Fictional administrator";
                Field<TextBox>(dialog, "password").Text = "test-only";
                int calls = 0, completed = 0, failed = 0;
                var backend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                dialog.ConfirmRecovery = () => scenario != "cancel";
                dialog.ShowRecoveryComplete = () => completed++;
                dialog.ShowRecoveryFailure = _ => failed++;
                dialog.RecoveryAction = (_, _, _, _, _, _, _) => { calls++; return backend.Task; };
                Exception? failure = null;
                using var timeout = new System.Windows.Forms.Timer { Interval = 10000 };
                timeout.Tick += (_, _) => {
                    failure = new Exception("Replacement recovery modal did not finish.");
                    typeof(RestoreExistingSchoolForm).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog,new object[]{ false });
                    dialog.Close();
                };
                dialog.Shown += (_, _) => dialog.BeginInvoke(new Action(async () => {
                    try
                    {
                        Task work = (Task)typeof(RestoreExistingSchoolForm).GetMethod("RecoverAsync",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog,null)!;
                        if (scenario == "cancel")
                        {
                            await work;
                            Check(calls == 0 && completed == 0 && dialog.DialogResult != DialogResult.OK,
                                "Declining confirmation neither activates SQL nor records recovery success");
                            dialog.Close();
                            return;
                        }
                        dialog.Close();
                        Check(!dialog.IsDisposed && dialog.Visible, "An in-progress recovery refuses modal close");
                        if (scenario == "success") backend.SetResult();
                        else backend.SetException(new IOException("Synthetic recovery failure"));
                        await work;
                        if (scenario == "failure")
                        {
                            Check(failed == 1 && completed == 0 && dialog.Visible
                                && Field<Button>(dialog,"recover").Enabled,
                                "Recovery errors leave the wizard open and permit a deliberate retry");
                            dialog.Close();
                        }
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }));
                timeout.Start();
                DialogResult result = dialog.ShowDialog();
                timeout.Stop();
                if (failure is not null) throw failure;
                if (scenario == "success")
                    Check(result == DialogResult.OK && calls == 1 && completed == 1 && failed == 0,
                        "Successful recovery exits the modal with OK after backend completion");
            }
            // Windows must prevent changing either bytes or pathname while SQL uses the validated source.
            using var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool denied = false;
            try { File.WriteAllText(file,"replacement bytes"); } catch (IOException) { denied = true; }
            Check(denied, "The recovery source read lease prevents overwriting verified backup bytes");
            denied = false;
            try { File.Delete(file); } catch (IOException) { denied = true; }
            Check(denied, "The recovery source read lease prevents pathname replacement");
        }
        finally { File.Delete(file); }
    }
}
