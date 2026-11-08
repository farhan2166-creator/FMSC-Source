using System.Reflection;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Run on a disposable Windows test profile; no SQL/database is used.
        // Unattended control failures must terminate with their exception rather
        // than wait indefinitely in Windows Forms' interactive error dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Control.CheckForIllegalCrossThreadCalls = true;
        // Match production's persistent UI loop. Standalone DoEvents calls can
        // uninstall the WinForms context and resume awaited page code on a worker.
        using var context = new ApplicationContext();
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        dispatcher.BeginInvoke((Action)(() => {
            try { RunChecks(args); }
            finally { context.ExitThread(); }
        }));
        Application.Run(context);
    }

    private static void RunChecks(string[] args)
    {
        if (args.Contains("--login-connection"))
        {
            CheckLoginConnection();
            Console.WriteLine($"PASS: {checks} combined login/connection visibility and recovery checks (no database connection).");
            return;
        }
        if(args.Contains("--compact-management-reports"))
        {
            CheckCompactReceiptScenarios();CheckCompactFinancialScenarios();Console.WriteLine($"PASS: {checks} compact management report PDF checks.");return;
        }
        if(args.Contains("--family-advance-layout"))
        {
            CheckFamilyAdvanceLayout();Console.WriteLine($"PASS: {checks} compact family form, grouped printing and multi-page row-preservation checks (no database connection).");return;
        }
        if (args.Contains("--dialog-size-tiers"))
        {
            CheckDialogSizeTiers();
            Console.WriteLine($"PASS: {checks} related dialog size and minimum-content checks.");
            return;
        }
        if (args.Contains("--startup-dialog-size"))
        {
            CheckStartupDialogSize();
            Console.WriteLine($"PASS: {checks} login-standard startup and update window sizing checks.");
            return;
        }
        if (args.Contains("--update-dialog"))
        {
            CheckUpdateDialog(); Console.WriteLine($"PASS: {checks} modeless update dialog, cancellation and single-installer checks."); return;
        }
        if (args.Contains("--student-certificates-screenshots"))
        {
            CaptureStudentCertificateScreenshots();
            Console.WriteLine($"PASS: {checks} synthetic WinForms capture checks.");
            return;
        }
        if (args.Contains("--student-certificates-workspace"))
        {
            CheckStudentCertificatesWorkspace();
            Console.WriteLine($"PASS: {checks} student certificates workspace projection, identity, and permission checks (no database connection).");
            return;
        }
        if(args.Contains("--finance-report-async"))
        {
            CheckFinanceReportAsync();Console.WriteLine($"PASS: {checks} delayed Billing Summary, thread access, total and print/export checks.");return;
        }
        if(args.Contains("--collection-receipt-presentation"))
        {
            CheckCollectionReceiptPresentation();Console.WriteLine($"PASS: {checks} collection receipt roster, placement, method, caption and PDF checks.");return;
        }
        if(args.Contains("--report-student-identity"))
        {
            CheckReportStudentIdentity();
            Console.WriteLine($"PASS: {checks} report identity, natural column order, projection and rendered PDF checks.");return;
        }
        if (args.Contains("--registration-workflow"))
        {
            CheckRegistrationWorkflow();
            Console.WriteLine($"PASS: {checks} native admission registration and status-row checks (no database connection).");
            return;
        }
        if (args.Contains("--fee-setup-review"))
        {
            CheckFeeSetupReview();
            CheckFeeMatrixClearing();
            Console.WriteLine($"PASS: {checks} fee setup validation/layout and matrix checks (no database connection).");
            return;
        }
        if (args.Contains("--report-purpose"))
        {
            CheckReportPurposeControls();
            Console.WriteLine($"PASS: {checks} report-purpose control checks (no database connection).");
            return;
        }
        if (args.Contains("--class-section-setup"))
        {
            CheckCompactCatalogLayouts();
            CheckAssignmentLayout();
            CheckSelectiveWorkspaceGuards();
            Console.WriteLine($"PASS: {checks} class/section catalog, draft and dialog checks (no database connection).");
            return;
        }
        if (args.Contains("--assignment-layout"))
        {
            CheckAssignmentLayout();
            Console.WriteLine($"PASS: {checks} assignment layout checks (no database connection).");
            return;
        }
        if (args.Contains("--setup-validation-layout"))
        {
            CheckSetupValidationLayout();
            Console.WriteLine($"PASS: {checks} setup validation/layout checks (no database connection).");
            return;
        }
        if (args.Contains("--academic-year-date-lock"))
        {
            CheckAcademicYearDateLock();
            Console.WriteLine($"PASS: {checks} native academic-year date-lock checks (no database connection).");
            return;
        }
        if (args.Contains("--certificate-submission"))
        {
            CheckMeaningfulCertificateContent();
            CheckCertificateSubmissionGuard();
            Console.WriteLine($"PASS: {checks} certificate discard-policy and submission guards (no database connection).");
            return;
        }
        if (args.Contains("--navigation-controls"))
        {
            CheckNavigationRecovery();
            CheckPaletteDestinations();
            CheckShortcutPermissions();
            CheckSelectiveWorkspaceGuards();
            CheckAdmissionActions();
            Console.WriteLine($"PASS: {checks} navigation recovery, palette dispatch, permission, busy/draft and admission-action checks (no database connection).");
            return;
        }
        if(args.Contains("--placement-workspace"))
        {
            CheckPlacementWorkspace();Console.WriteLine($"PASS: {checks} placement workspace checks.");return;
        }
        if (args.Contains("--action-shortcuts"))
        {
            CheckActionShortcuts();
            CheckAdmissionSaveShortcut();
            Console.WriteLine($"PASS: {checks} action shortcut mapping, focus, hints and save dispatch checks (no database connection).");
            return;
        }
        if(args.Contains("--grid-alignment"))
        {
            CheckSharedGridAlignment();
            Console.WriteLine($"PASS: {checks} shared and real-page grid heading/value, resize and data-preservation checks (no database connection).");return;
        }
        if(args.Contains("--individual-outstanding-bill"))
        {
            CheckHistoricalLifecycleControls();CheckIndividualOutstandingBill();
            Console.WriteLine($"PASS: {checks} individual/family bill scope, alignment, permission and stale-preview checks (no database connection).");return;
        }
        if(args.Contains("--windows-print-dialog")||args.Contains("--windows-print-dialog-preview"))
        {
            CheckWindowsPrintDialog(args.Contains("--windows-print-dialog-preview"));
            Console.WriteLine($"PASS: {checks} Windows print choices and current-page checks (no print job sent).");return;
        }
        if(args.Contains("--certificate-navigation"))
        {
            CheckCertificateNavigation();
            Console.WriteLine($"PASS: {checks} certificate launch, reset, history focus and keyboard-order checks (no database connection).");return;
        }
        if(args.Contains("--admission-fee-flow-sql-compat"))
        {
            CheckAdmissionFeeFlowSqlCompatibility();Console.WriteLine($"PASS: {checks} owned baseline compatibility check.");return;
        }
        if(args.Contains("--admission-fee-flow-fixes"))
        {
            CheckAdmissionFeeFlowFixes();
            Console.WriteLine($"PASS: {checks} admission/fee flow permission, status, account and refusal checks.");return;
        }
        if(args.Contains("--fee-account"))
        {
            CheckFeeAccountWorkspace();CheckPaymentAdjustmentContext();CheckPaymentDetailGeneration();CheckAdjustmentRecoveryGuard();
            Console.WriteLine($"PASS: {checks} account scope, receipts, permissions, stale reads, layouts and existing correction recovery checks.");return;
        }
        if(args.Contains("--fee-account-sql"))
        {
            var task=CheckFeeAccountSqlAsync();PumpUntil(()=>task.IsCompleted,"Owned account SQL scenarios finish",TimeSpan.FromMinutes(5));task.GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {checks} owned fictional account readers, receipts and correction SQL checks.");return;
        }
        if(args.Contains("--receipt-breakdown"))
        {
            CheckReceiptBreakdown();
            Console.WriteLine($"PASS: {checks} sibling receipt breakdown, original totals and layout checks (no database connection).");return;
        }
        if(args.Contains("--receipt-breakdown-sql"))
        {
            CheckReceiptBreakdownSql();
            Console.WriteLine($"PASS: {checks} read-only sibling receipt and existing-formula consistency checks.");return;
        }
        if(args.Contains("--admission-sibling-baseline"))
        {
            CheckAdmissionSiblingBaseline();CheckAdmissionPostSaveBaseline();
            Console.WriteLine($"PASS: {checks} saved-sibling and admission draft protection checks (no database connection).");return;
        }
        if(args.Contains("--transport-service"))
        {
            CheckTransportService();
            Console.WriteLine($"PASS: {checks} transport month, paid-month protection, shared-dialog and independent-profile checks (no database connection).");return;
        }
        if(args.Contains("--transport-service-sql"))
        {
            CheckTransportServiceSql();
            Console.WriteLine($"PASS: {checks} isolated transport cutoff, posting, cancellation and recovery checks.");return;
        }
        if(args.Contains("--receipt-carry-labels"))
        {
            CheckReceiptCarryLabels();CheckLatestReceipt();
            Console.WriteLine($"PASS: {checks} payment-history filtering and carried-balance label checks (no database connection).");
            return;
        }
        if(args.Contains("--whole-number-inputs"))
        {
            CheckWholeNumberInputs();CheckNumericPlaceholders();CheckClosingWholeAmounts();
            Console.WriteLine($"PASS: {checks} shared whole-number typing, paste, matrix and closing checks (no database connection).");
            return;
        }
        if(args.Contains("--sibling-father-search"))
        {
            CheckSiblingFatherSearch();
            Console.WriteLine($"PASS: {checks} exact father-name sibling search checks (no database connection).");
            return;
        }
        if(args.Contains("--collection-returns"))
        {
            CheckCollectionReturns();CheckOptionalFeeReason();
            Console.WriteLine($"PASS: {checks} collection return status, one-child review and optional-reason typing checks (no database connection).");
            return;
        }
        if(args.Contains("--historical-return-sql"))
        {
            CheckHistoricalReturnSql();
            Console.WriteLine($"PASS: {checks} historical admission and return SQL checks in an owned clone.");
            return;
        }
        if(args.Contains("--fee-change-documents"))
        {
            CheckFeeChangeDocuments();
            Console.WriteLine($"PASS: {checks} fee-change document checks with PDF/PNG rendering.");
            return;
        }
        if(args.Contains("--collection-waiver-sql"))
        {
            CheckCollectionWaiverSql();
            Console.WriteLine($"PASS: {checks} collection waiver SQL checks in an owned disposable clone.");
            return;
        }
        if(args.Contains("--collection-fee-actions"))
        {
            CheckCollectionFeeActions();
            Console.WriteLine($"PASS: {checks} collection fee shortcut and shared concession checks (no database writes).");
            return;
        }
        if(args.Contains("--collection-inline-amounts"))
        {
            CheckCollectionInlineAmounts();
            Console.WriteLine($"PASS: {checks} sibling total/pay labels and allocation checks (no database connection).");
            return;
        }
        if(args.Contains("--collection-sibling-navigation"))
        {
            CheckCollectionSiblingNavigation();
            Console.WriteLine($"PASS: {checks} collection sibling navigation guards and pending-read checks (no database connection).");
            return;
        }
        if(args.Contains("--promotion-campus-classes"))
        {
            CheckPromotionHistoryEntry();
            Console.WriteLine($"PASS: {checks} promotion campus classes and history checks (no database connection).");
            return;
        }
        if(args.Contains("--sports-certificate-input"))
        {
            CheckSportsCertificateInput();
            Console.WriteLine($"PASS: {checks} sports keyboard, populated-history and collection-lock checks (no database connection).");
            return;
        }
        if(args.Contains("--admission-actions"))
        {
            CheckAdmissionActions();
            Console.WriteLine($"PASS: {checks} focused admission menu and draft-confirmation checks (no database connection).");
            return;
        }
        if(args.Contains("--collection-allocation"))
        {
            CheckCollectionAllocationPolicy();
            CheckLargestDiscount();
            CheckCollectionCredit();
            Console.WriteLine($"PASS: {checks} collection allocation, discount and advance-credit checks (no database writes).");
            return;
        }
        if(args.Contains("--admission-fee-selection"))
        {
            CheckAdmissionFeeSelection();
            Console.WriteLine($"PASS: {checks} admission fee removal and saved-charge protection checks (no database connection).");
            return;
        }
        if(args.Contains("--class-sheet-buttons"))
        {
            CheckClassSheetButtons();
            Console.WriteLine($"PASS: {checks} class sheet button layout checks (no database connection).");
            return;
        }
        if(args.Contains("--fee-matrix-clearing"))
        {
            CheckFeeMatrixClearing();
            CheckFeeMatrixHostBusy();
            CheckFeeMatrixCampusDraft();
            Console.WriteLine($"PASS: {checks} matrix clearing, save-plan and operation-guard checks (no database connection).");
            return;
        }
        if(args.Contains("--student-certificate-layouts"))
        {
            CheckStudentCertificateLayouts();
            CheckCharacterCertificateLayout();
            CheckDatePickerVisibility();
            Console.WriteLine($"PASS: {checks} student certificate layout, keyboard and date visibility checks (no database connection).");
            return;
        }
        if(args.Contains("--departure-layout"))
        {
            CheckDepartureLayout();
            CheckLifecycleCloseGuard();
            Console.WriteLine($"PASS: {checks} departure layout and lifecycle review checks (no database connection).");
            return;
        }
        if(args.Contains("--spreadsheet-export"))
        {
            CheckSpreadsheetExport();
            Console.WriteLine($"PASS: {checks} Excel projection, typed-date and full-result checks (no database connection).");
            return;
        }
        if(args.Contains("--report-ordering"))
        {
            CheckReportOrderingControls();
            Console.WriteLine($"PASS: {checks} report ordering, saved-view and stale-result checks (no database connection).");
            return;
        }
        if(args.Contains("--global-search"))
        {
            CheckGlobalSearch();CheckPaletteDestinations();
            Console.WriteLine($"PASS: {checks} global search and feature-navigation checks (no database connection).");return;
        }
        if(args.Contains("--saved-report-views"))
        {
            CheckSavedReportViews();
            Console.WriteLine($"PASS: {checks} focused saved-view controls, rollback, dates, columns and report integration checks (no database connection).");
            return;
        }
        if(args.Contains("--new-reports"))
        {
            CheckNewReportForms();
            Console.WriteLine($"PASS: {checks} focused new report choices, permissions, CSV and RDLC/PDF checks (no database connection).");
            return;
        }
        if(args.Contains("--release-reports"))
        {
            CheckReleaseReports();
            Console.WriteLine($"PASS: {checks} focused release report forms, permissions, CSV and multipage PDF checks (no database connection).");
            return;
        }
        if (args.Contains("--character-certificate-layout"))
        {
            CheckCharacterCertificateLayout();
            Console.WriteLine($"PASS: {checks} character certificate alignment and keyboard checks (no database connection); previews in artifacts/character-certificate.");
            return;
        }
        if (args.Contains("--fee-tasks"))
        {
            CheckOperatorConcessionChoices();
            CheckFeeTaskFlow();
            Console.WriteLine($"PASS: {checks} fee task, scope, overpayment and recovery checks (no database writes); previews in artifacts/fee-tasks.");
            return;
        }
        if (args.Contains("--placement-history-flow"))
        {
            CheckPlacementHistoryFlow();
            Console.WriteLine($"PASS: {checks} placement history flow checks (no database writes); previews in artifacts/placement-history.");
            return;
        }
        if (args.Contains("--admission-fee-layout"))
        {
            CheckAdmissionFeeLayout();
            Console.WriteLine($"PASS: {checks} admission fee layout checks; preview in artifacts/admission-fees.png.");
            return;
        }
        if (args.Contains("--admission-header"))
        {
            CheckAdmissionHeader();
            Console.WriteLine($"PASS: {checks} admission header layout checks; preview in artifacts/admission-header.png.");
            return;
        }
        if (args.Contains("--admission-save-shortcut"))
        {
            CheckAdmissionSaveShortcut();
            Console.WriteLine($"PASS: {checks} admission Save shortcut checks (no database connection).");
            return;
        }
        if(args.Contains("--admission-number-policy"))
        {
            CheckAdmissionNumberPolicy();
            Console.WriteLine($"PASS: {checks} admission-number chronology checks (no database connection).");
            return;
        }
        if(args.Contains("--combined-tooltips"))
        {
            CheckCombinedTooltips();
            CheckFinanceHelp();
            CheckValidationBadges();
            CheckShortcutPermissions();
            Console.WriteLine($"PASS: {checks} combined tooltip, validation and shortcut checks (no database connection).");
            return;
        }
        if(args.Contains("--staff-certificate-subjects"))
        {
            CheckStaffCertificateSubjects();
            Console.WriteLine($"PASS: {checks} staff certificate subject checks (no database connection).");
            return;
        }
        if(args.Contains("--student-hold-preview-response"))
        {
            CheckStudentPostingHoldSqlResponse();
            Console.WriteLine($"PASS: {checks} read-only SQL response rendering checks (no database writes).");
            return;
        }
        if(args.Contains("--student-holds"))
        {
            CheckStudentPostingHolds();
            Console.WriteLine($"PASS: {checks} hold/resume UI and recovery checks.");
            return;
        }
        if(args.Contains("--character-certificate-reprint"))
        {
            CheckCharacterCertificateReprint();
            Console.WriteLine($"PASS: {checks} character certificate history, wording, collection and reprint checks (no database connection).");
            return;
        }
        if(args.Contains("--regular-certificate-search"))
        {
            CheckRegularCertificateSearch();
            Console.WriteLine($"PASS: {checks} regular certificate search, missing birth date and duplicate checks (no database connection).");
            return;
        }
        if(args.Contains("--certificate-reissue-snapshots"))
        {
            CheckCertificateReissueSnapshots();
            CheckRegularCertificateSearch();
            Console.WriteLine($"PASS: {checks} certificate reissue history, saved DOB, search and operation guard checks (no database connection).");
            return;
        }
        if(args.Contains("--certificate-pdf-review"))
        {
            CheckCertificatePdfReview();
            Console.WriteLine($"PASS: {checks} production certificate PDF rendering and bookmarked review checks.");
            return;
        }
        if(args.Contains("--certificate-headers"))
        {
            CheckReportCaptions(new[]{"BankAccountOpeningCertificate","CharacterCertificate","DateOfBirthCertificate",
                "NoObjectionCertificate","ProvisionalCertificate","RegularLecturerCertificate","RegularStudentCertificate","SportsCertificate"});
            Console.WriteLine($"PASS: {checks} certificate header, margin and single-page rendering checks; previews in artifacts/report-captions.");
            return;
        }
        if(args.Contains("--character-report-header"))
        {
            CheckReportCaptions(new[]{"CharacterCertificate"});
            Console.WriteLine($"PASS: {checks} character report header and single-page rendering checks; previews in artifacts/report-captions.");
            return;
        }
        if(args.Contains("--report-heading-alignment"))
        {
            CheckReportCaptions(new[]{"ClassSectionSummary","ClassOutstandingCharges","ClassMarksSheet","ClassAttendanceSheet","ClassMonthlyAttendanceSheet"});
            Console.WriteLine($"PASS: {checks} class report heading alignment, rendered geometry and PDF/PNG checks.");return;
        }
        if(args.Contains("--report-captions"))
        {
            CheckReportCaptions();
            Console.WriteLine($"PASS: {checks} report caption and rendering checks.");
            return;
        }
        if(args.Contains("--record-presentation"))
        {
            CheckRecordPresentation();
            Console.WriteLine($"PASS: {checks} gender, title-case, save-parameter and rendered certificate checks.");
            return;
        }
        if(args.Contains("--ui-guidance-review"))
        {
            CaptureUiGuidanceReview();
            Console.WriteLine($"PASS: {checks} UI guidance rendering checks; captures in artifacts/ui-guidance-review.");
            return;
        }
        if(args.Contains("--payment-details"))
        {
            CheckPaymentDetailsGrid();
            CheckPaymentDetailGeneration();
            Console.WriteLine($"PASS: {checks} focused payment detail grid and stale-response checks (no database connection).");
            return;
        }
        if(args.Contains("--collection-counter-layout"))
        {
            CheckCollectionCounterLayout();
            CheckCollectionCredit();
            Console.WriteLine($"PASS: {checks} counter payment layout and collection credit checks (no database connection).");
            return;
        }
        if(args.Contains("--family-list-selection"))
        {
            CheckFamilyListSelection();
            CheckFamilyAdmissionWorkspace();
            Console.WriteLine($"PASS: {checks} family checkbox, selection and workspace checks (no database writes).");
            return;
        }
        if(args.Contains("--confirm-admission"))
        {
            CheckAdmissionCompletion();
            CheckAdmissionPostSaveBaseline();
            CheckAdmissionSiblingBaseline();
            CheckAdmissionSharedBusy();
            Console.WriteLine($"PASS: {checks} admission confirmation, recovery and review-layout checks (no database writes).");
            return;
        }
        if(args.Contains("--search-selection"))
        {
            CheckSearchTextSelection();
            CheckStudentSearchModes();
            Console.WriteLine($"PASS: {checks} search selection and student search checks (no database connection).");
            return;
        }
        if(args.Contains("--finance-help"))
        {
            CheckFinanceHelp();
            CheckPaymentAdjustmentContext();
            CheckPaymentDetailGeneration();
            CheckAdjustmentRecoveryGuard();
            CheckOperatorConcessionChoices();
            Console.WriteLine($"PASS: {checks} finance wording, examples, transaction-code and review-safety checks (no database connection).");
            return;
        }
        if(args.Contains("--leave-fee-review"))
        {
            CheckLeaveFeeReview();
            Console.WriteLine($"PASS: {checks} leaving-fee decision, eligibility and checkbox checks (no database writes).");
            return;
        }
        if(args.Contains("--leave-student-dates"))
        {
            CheckLeaveStudentDates();
            Console.WriteLine($"PASS: {checks} leaving-certificate student date checks (no database connection).");
            return;
        }
        if(args.Contains("--certificate-collection-workflow"))
        {
            CheckCertificateCollectionWorkflow();
            CheckCertificateCollectorDialog();
            CheckCertificateNewDiscard();
            Console.WriteLine($"PASS: {checks} certificate collection, lock, print and unused-copy checks across all ten forms.");
            return;
        }
        if(args.Contains("--payment-adjustments"))
        {
            CheckPaymentAdjustmentContext();
            CheckPaymentDetailGeneration();
            CheckAdjustmentRecoveryGuard();
            Console.WriteLine($"PASS: {checks} payment adjustment wording, selection and recovery checks (no database connection).");
            return;
        }
        if(args.Contains("--placement-cutoff"))
        {
            CheckPlacementCutoff();
            Console.WriteLine($"PASS: {checks} placement cutoff and billing-date control checks (no database connection).");
            return;
        }
        if(args.Contains("--family-admission-workspace"))
        {
            CheckFamilyAdmissionWorkspace();
            Console.WriteLine($"PASS: {checks} focused family-workspace design and draft-retention checks (no database writes).");
            return;
        }
        if(args.Contains("--finance-presentation"))
        {
            CheckFinancePresentation();
            Console.WriteLine($"PASS: {checks} finance presentation and preserved interaction checks (no database connection).");
            return;
        }
        if(args.Contains("--transfer-review"))
        {
            CheckTransferReview();
            Console.WriteLine($"PASS: {checks} transfer scope, layout, cutoff and theme-selection checks.");
            return;
        }
        if(args.Contains("--blocking-feedback"))
        {
            CheckBlockingFeedback();
            CheckCompactStatusLayout();
            CheckMessageDialogs();
            Console.WriteLine($"PASS: {checks} blocking-message colour and reset checks across all themes.");
            return;
        }
        if(args.Contains("--direct-pdf-review"))
        {
            CheckDirectPdfReview();
            Console.WriteLine($"PASS: {checks} direct export, report identity and combined review PDF checks.");
            return;
        }
        if(args.Contains("--grid-pdf"))
        {
            CheckGridPdfReports();
            Console.WriteLine($"PASS: {checks} shared PDF layout, field-preservation and pagination checks.");
            return;
        }
        if(args.Contains("--statement-actions"))
        {
            CheckStudentAccountFlow();
            CheckStatementRegenerationFreshness();
            CheckAsyncStatementAssembly();
            Console.WriteLine($"PASS: {checks} statement action, mode and async safety checks (no database connection).");
            return;
        }
        if(args.Contains("--bill-roll-heights"))
        {
            CheckBillRollHeights();Console.WriteLine($"PASS: {checks} independent bill-height, complete-slip, PDF, printer and preview checks.");return;
        }
        if(args.Contains("--report-document-tools"))
        {
            CheckReportDocumentTools();Console.WriteLine($"PASS: {checks} report naming, export filename and printed-page search checks (no database connection).");return;
        }
        if(args.Contains("--bill-preparation"))
        {
            CheckBillPreparationChoices();Console.WriteLine($"PASS: {checks} bill grouping, explicit selection, receipt child copy and batch recovery checks.");return;
        }
        if(args.Contains("--outstanding-bills"))
        {
            CheckOutstandingBills();
            Console.WriteLine($"PASS: {checks} outstanding bill filters, layout and selection checks (no database connection).");
            return;
        }
        if (args.Contains("--latest-receipt"))
        {
            CheckLatestReceipt();
            Console.WriteLine($"PASS: {checks} latest receipt checks (no database connection).");
            return;
        }
        if(args.Contains("--restore-identity"))
        {
            CheckRestoreIdentity();
            Console.WriteLine($"PASS: {checks} restore identity checks (no database connection).");
            return;
        }
        if(args.Contains("--bill-line-identity"))
        {
            CheckBillLineIdentity();
            Console.WriteLine($"PASS: {checks} bill line identity checks (no database connection).");
            return;
        }
        if(args.Contains("--fee-document-review-pack"))
        {
            CheckFeeDocumentReviewPack();
            return;
        }
        if(args.Contains("--fee-documents"))
        {
            CheckFeeDocuments();
            int billAssertions=RegistrationBillChecks.Run();
            Console.WriteLine($"PASS: {checks} fee document checks, including RDLC PNG/PDF rendering.");
            Console.WriteLine($"PASS: {billAssertions} student and family bill data checks.");
            return;
        }
        if(args.Contains("--current-receipt-balances"))
        {
            CheckCurrentReceiptBalances();
            Console.WriteLine($"PASS: {checks} current receipt balance checks (no database connection).");
            return;
        }
        if (args.Contains("--collection-credit"))
        {
            CheckCollectionCredit();
            Console.WriteLine($"PASS: {checks} collection credit checks (no database connection).");
            return;
        }
        if (args.Contains("--largest-discount"))
        {
            CheckLargestDiscount();
            Console.WriteLine($"PASS: {checks} discount allocation checks (no database connection).");
            return;
        }
        if (args.Contains("--family-panel"))
        {
            using var form = new Form { ClientSize = new Size(320, 600) };
            using var list = new FamilyStudentList { Dock = DockStyle.Fill, CheckOnClick = true };
            list.Items.Add("6590 · Test One\nPlay Group - F · Sibling", true);
            list.Items.Add("6591 · Test Two\nClass 1 - A · Sibling", false);
            form.Controls.Add(list); form.Show(); Application.DoEvents();
            Check(list.GetItemRectangle(0).Height >= list.Font.Height * 2 + 12, "Family rows have space for two lines");
            Check(list.CheckedItems.Count == 1 && list.GetItemChecked(0), "CheckedListBox selection semantics are preserved");
            list.SetItemChecked(1, true);
            Check(list.CheckedItems.Count == 2, "Individual check state updates normally");
            Directory.CreateDirectory("artifacts/billing-ui");
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save("artifacts/billing-ui/family-panel.png");
            Console.WriteLine($"PASS: {checks} family panel checks.");
            return;
        }
        if (args.Contains("--family-confirmation"))
        {
            CheckFamilyConfirmationLayout();
            Console.WriteLine($"PASS: {checks} family confirmation layout checks.");
            return;
        }
        if (args.Contains("--billing-layout"))
        {
            CheckBillingLayout();
            Console.WriteLine($"PASS: {checks} Billing Center layout checks (no database connection).");
            return;
        }
        if (args.Contains("--numeric-placeholders"))
        {
            CheckNumericPlaceholders();
            Console.WriteLine($"PASS: {checks} numeric placeholder checks (no database connection).");
            return;
        }
        if (args.Contains("--advance-posting"))
        {
            CheckAdvancePosting();
            CheckMonthlyRepostClick();
            Console.WriteLine($"PASS: {checks} manual / advance posting checks (no database connection).");
            return;
        }
        if (args.Contains("--billing-grid"))
        {
            CheckBillingGrid();
            Console.WriteLine($"PASS: {checks} billing grid checks (no database connection).");
            return;
        }
        if (args.Contains("--placement-picker"))
        {
            CheckPlacementPickerNavigation();
            Console.WriteLine($"PASS: {checks} placement picker checks (no database connection).");
            return;
        }
        if(args.Contains("--billing-design"))
        {
            CheckBillingDesignDialogs();
            Console.WriteLine($"PASS: {checks} billing dialog checks (no database connection).");
            return;
        }
        if(args.Contains("--monthly-repost"))
        {
            CheckMonthlyRepostClick();
            Console.WriteLine($"PASS: {checks} monthly repost click checks.");
            return;
        }
        if(args.Contains("--additional-fee-recovery"))
        {
            CheckAdditionalFeeRecovery();
            Console.WriteLine($"PASS: {checks} additional-fee recovery checks (no database connection).");
            return;
        }
        if(args.Contains("--student-purchase-collection"))
        {
            CheckStudentPurchaseCollection();
            Console.WriteLine($"PASS: {checks} purchase-to-collection hand-off checks (no database connection).");
            return;
        }
        if(args.Contains("--additional-fee-scope"))
        {
            CheckAdditionalFeeScope();
            Console.WriteLine($"PASS: {checks} additional-fee selection scope checks.");
            return;
        }
        if(args.Contains("--family-counter-sql"))
        {
            async Task CounterChecks(){await CheckFamilyCounterSqlAsync();await CheckQuickCounterWorkspaceSqlAsync();}
            var task=CounterChecks();
            PumpUntil(()=>task.IsCompleted,"Owned family-counter SQL scenarios complete",TimeSpan.FromMinutes(5));
            task.GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {checks} owned family-counter SQL assertions.");
            return;
        }
        if(args.Contains("--family-counter"))
        {
            CheckFamilyCounter();
            Console.WriteLine($"PASS: {checks} family counter workflow checks.");
            return;
        }
        if(args.Contains("--admission-layout"))
        {
            CheckAdmissionToolbarRows();
            CheckReportCaptionOrder();
            CheckValidationBadges();
            CheckDpiGeometryAndMinimum();
            CheckDialogScrollHost();
            Console.WriteLine($"PASS: {checks} admission and report window layout checks.");
            return;
        }
        if(args.Contains("--help-center-interactive"))
        {
            ShowHelpCenterAcceptance();
            return;
        }
        if(args.Contains("--help-center"))
        {
            CheckHelpCenter();
            Console.WriteLine($"PASS: {checks} offline Help Center control checks.");
            return;
        }
        if(args.Contains("--validation-badges"))
        {
            CheckValidationBadges();
            Console.WriteLine($"PASS: {checks} validation-caption layout checks.");
            return;
        }
        if(args.Contains("--setup-readiness"))
        {
            CheckSetupReadinessActions();
            Console.WriteLine($"PASS: {checks} setup readiness control checks.");
            return;
        }
        if(args.Contains("--level3"))
        {
            CheckLevel3Components();
            return;
        }
        if(args.Contains("--post-fix"))
        {
            CheckValidationBadges();
            CheckAdmissionToolbarRows();
            CheckSchoolWorkflow();
            CheckReportCaptionOrder();
            CheckDatePickerVisibility();
            CheckPdfFileDurability();
            CheckDpiResponsiveLayouts();
            CheckShortcutPermissions();
            CheckPromotionHistoryEntry();
            CheckCompactStatusLayout();
            CheckMessageDialogs();
            CheckStudentAccountFlow();
            CheckStudentSearchModes();
            CheckHelpCenter();
            Console.WriteLine("ACCOUNT_FLOW: check refused regeneration");
            CheckStatementRegenerationFreshness();
            Console.WriteLine("ACCOUNT_FLOW: check multi-student async assembly");
            CheckAsyncStatementAssembly();
            Console.WriteLine("ACCOUNT_FLOW: existing statement safety checks completed");
            Console.WriteLine("POST_FIX: CheckClosingWholeAmounts");
            CheckClosingWholeAmounts();
            Console.WriteLine("POST_FIX: CheckPaletteDestinations");
            CheckPaletteDestinations();
            Console.WriteLine("POST_FIX: CheckSubmittedInputHolds");
            CheckSubmittedInputHolds();
            Console.WriteLine("POST_FIX: CheckAdmissionPostSaveBaseline");
            CheckAdmissionPostSaveBaseline();
            Console.WriteLine("POST_FIX: CheckAdmissionSiblingBaseline");
            CheckAdmissionSiblingBaseline();
            Console.WriteLine("POST_FIX: CheckMeaningfulCertificateContent");
            CheckMeaningfulCertificateContent();
            Console.WriteLine("POST_FIX: CheckSelectiveWorkspaceGuards");
            CheckSelectiveWorkspaceGuards();
            Console.WriteLine("POST_FIX: CheckSetupReadinessActions");
            CheckSetupReadinessActions();
            Console.WriteLine("POST_FIX: CheckFamilyCounter");
            CheckFamilyCounter();
            Console.WriteLine("POST_FIX: CheckLifecycleCloseGuard");
            CheckLifecycleCloseGuard();
            Console.WriteLine("POST_FIX: CheckOperatorConcessionChoices");
            CheckOperatorConcessionChoices();
            Console.WriteLine("POST_FIX: CheckCertificateSubmissionGuard");
            CheckCertificateSubmissionGuard();
            Console.WriteLine("POST_FIX: CheckQuickRegistrationTabGuard");
            CheckQuickRegistrationTabGuard();
            Console.WriteLine("POST_FIX: CheckRegularNewCertificateDraft");
            CheckRegularNewCertificateDraft();
            Console.WriteLine("POST_FIX: CheckCertificateNewDiscard");
            CheckCertificateNewDiscard();
            Console.WriteLine($"PASS: {checks} focused post-fix Windows UI contract checks.");
            return;
        }
        var originalTheme = AppTheme.CurrentTheme;
        try
        {
            Check((int)AppThemeType.Amber == 6, "Existing persisted enum values must stay stable");
            Check(AppTheme.SelectableThemes.Contains(AppThemeType.ProfessionalSimple), "Simple theme must be selectable");
            AppTheme.SetTheme((AppThemeType)999);
            Check(AppTheme.CurrentTheme == AppThemeType.OceanBlue, "Unknown saved/future theme values fall back to a supported appearance");
            AppTheme.SetTheme(AppThemeType.OceanBlue);
            using var form = new Form { ClientSize = new Size(720, 500), AutoScaleMode = AutoScaleMode.None };
            var card = new RoundedPanel { Bounds = new Rectangle(20, 20, 680, 460), CornerRadius = 12 };
            var field = new ModernTextBox { Bounds = new Rectangle(20, 20, 280, 42), Text = "Unsaved student details", IsInvalid = true };
            var choice = new ModernComboBox { Bounds = new Rectangle(20, 85, 280, 42) };
            choice.Items.AddRange(new object[] { "Class 1", "Class 2" }); choice.SelectedIndex = 1;
            var day = new ModernDatePicker { Bounds = new Rectangle(20, 150, 280, 42), Value = new DateTime(2026, 10, 13) };
            var student = new StudentPicker { Dock = DockStyle.None, Bounds = new Rectangle(20, 215, 280, 42) };
            var button = new ModernButton { Bounds = new Rectangle(20, 280, 200, 42), Text = "Save", TabIndex = 4 };
            var disabled = new ModernButton { Bounds = new Rectangle(240, 280, 200, 42), Text = "Disabled", Enabled = false };
            card.Controls.AddRange(new Control[] { field, choice, day, student, button, disabled });
            form.Controls.Add(card); form.Show(); Application.DoEvents();
            UiHelper.ApplyThemeToForm(form);
            Control[] controls = { card, field, choice, day, student, button, disabled };
            var before = controls.Select(c => (c.Bounds, c.Padding, c.Margin, c.Font, c.TabIndex, c.Enabled, c.Visible)).ToArray();
            var focus = form.ActiveControl;
            var timerField = typeof(UiColorTransition).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var motion = new UiColorTransition(button, button.BackColor, color => button.BackColor = color);
            // Start in a decorated theme; starting an animation may be disabled
            // by Windows accessibility settings. Either case must switch safely.
            motion.MoveTo(Color.Red);
            foreach (var theme in AppTheme.SelectableThemes.Concat(AppTheme.SelectableThemes.Reverse()))
            {
                AppTheme.SetTheme(theme); Application.DoEvents();
                for (int i = 0; i < controls.Length; i++)
                    Check(before[i] == (controls[i].Bounds, controls[i].Padding, controls[i].Margin, controls[i].Font, controls[i].TabIndex, controls[i].Enabled, controls[i].Visible), "Theme must preserve control structure and geometry");
                Check(field.Text == "Unsaved student details" && field.IsInvalid && choice.SelectedIndex == 1 && day.Value.Date == new DateTime(2026, 10, 13), "Theme must preserve draft values and validation state");
                Check(form.ActiveControl == focus, "Theme must preserve focus");
                Check(AppTheme.UseSimpleRendering == AppTheme.IsLightweight(theme), "Rendering mode must follow theme selection");
                foreach (var control in controls)
                    Check(AppTheme.UseSimpleRendering ? control.Region is null : control.Region is not null, "Switching must release or restore rounded clipping regions");
                if (AppTheme.UseSimpleRendering)
                {
                    string preference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FMSC_SMS", "theme.txt");
                    Check(File.ReadAllText(preference).Trim() == theme.ToString(), "Simple choice must use the saved theme preference");
                    motion.MoveTo(AppTheme.PrimaryHover);
                    Check(timerField.GetValue(motion) is null && motion.Value == AppTheme.PrimaryHover, "Simple feedback must apply immediately without an animation timer");
                    Check(Contrast(AppTheme.Text, AppTheme.Surface) >= 4.5 && Contrast(Color.White, AppTheme.Primary) >= 4.5, "Simple text and primary actions need readable contrast");
                }
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, form.ClientRectangle);
                Check(image.Width > 0, "Both themes must render without an exception");
            }
            AppTheme.SetTheme(AppThemeType.ProfessionalSimple);
            using (var flatShape = new UiPaintShape(new Rectangle(0, 0, 100, 30), 8))
                Check(typeof(UiPaintShape).GetField("path", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flatShape) is null, "Flat shapes must avoid allocating a rounded path");
            using var menu = new StyledContextMenu(); menu.Items.Add("Save");
            Check(menu.Items.Count == 1&&!menu.DropShadowEnabled, "Lightweight popup actions remain available without a shadow");
            CheckLightweightResources(form);
            using var students = new System.Data.DataTable();
            students.Columns.Add("AdmissionId", typeof(int)); students.Columns.Add("AdmissionNumber", typeof(string));
            students.Columns.Add("StudentName", typeof(string)); students.Columns.Add("FatherName", typeof(string));
            students.Rows.Add(1, "1", "Ali", "Parent One"); students.Rows.Add(2, "2", "Bilal", "Parent Two");
            student.SetSource(students); student.Focus();
            var input = student.Controls.OfType<TextBox>().Single();
            var debounceField = typeof(StudentPicker).GetField("_searchDebounce", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var keyHandler = typeof(StudentPicker).GetMethod("OnTextKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Key(Keys key) => keyHandler.Invoke(student, new object[] { input, new KeyEventArgs(key) });
            input.Text = "1";
            var debounce = (System.Windows.Forms.Timer)debounceField.GetValue(student)!;
            Check(debounce.Enabled && student.SelectedId == 0, "Typing must debounce without committing a student");
            Key(Keys.Enter);
            Check(student.SelectedId == 1 && !debounce.Enabled, "Enter must flush the search immediately");
            input.Text = "2";
            Check(student.SelectedId == 0 && debounce.Enabled, "Editing must clear the prior identity and debounce the new term");
            Key(Keys.Enter);
            Check(student.SelectedId == 2, "Enter after editing must never commit the old highlighted student");
            input.Text = "1"; Key(Keys.Escape);
            Check(!debounce.Enabled && student.SelectedId == 0, "Escape must cancel a pending search");
            input.Text = "2"; input.Clear();
            Check(!debounce.Enabled && student.MatchCount == 0, "Clearing input must stop pending work");
            var output = new ModernTextBox { Bounds = new Rectangle(360, 85, 260, 42), ReadOnly = true, Text = "Calculated output", TabIndex = 1 };
            var tick = new CheckBox { Bounds = new Rectangle(360, 150, 220, 30), Text = "Active", TabIndex = 2 };
            card.Controls.AddRange(new Control[] { output, tick }); Application.DoEvents();
            int saves = 0; button.Click += (_, _) => saves++;
            field.Focus();
            Check(!MasterEditorFocusNavigator.FocusInside(output), "Entry focus must skip read-only outputs");
            Check(MasterEditorFocusNavigator.TryAdvance(field.Controls.OfType<TextBox>().Single(), new Control[] { field, output, tick }, button), "Enter must skip outputs and deliberate checkbox choices, then reach Save");
            Check(button.Focused && saves == 0, "Completing a field must focus Save without submitting it");
            button.PerformClick(); Check(saves == 1, "A deliberate Save action still runs");

            using (var workspace = new WorkspacePage("Fee Posting", "", ("Monthly", () => new Panel()), ("Additional", () => new Panel())))
            {
                var tabs = (TabControl)typeof(WorkspacePage).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace)!;
                int updates = 0; workspace.CurrentSectionChanged += (_, _) => updates++;
                tabs.SelectedIndex = 1;
                Check(workspace.CurrentSection == "Additional" && updates == 1, "Workspace context must follow the selected section");
            }
            CheckSameRecordDraft();
            CheckChoiceFilters(form);
            CheckInformationDialogs(form);
            CheckPhotoPreparation();
            CheckPhotoViewer(form);
            CheckDashboard();
            CheckNavigationRecovery();
            CheckAdmissionSaveShortcut();
            CheckRegistrationPhotoRecovery();
            CheckHiddenRegistrationToolbar();
            CheckQuickRegistrationTabGuard();
            CheckAdditionalFeeScope();
            CheckAdditionalFeeRecovery();
            CheckFeeMatrixHostBusy();
            CheckFeeMatrixCampusDraft();
            CheckAdmissionSharedBusy();
            CheckModalPageCloseGuard();
            CheckDailyClosingLeaveContract();
            CheckPostingReversalBusy();
            CheckFamilyMaintenanceBusy();
            CheckDuplicateBatchSerialization();
            CheckStudentReviewBusyHost();
            CheckStudentCorrectionBusy();
            CheckCorrectionDraftTracking();
            CheckPaymentAdjustmentContext();
            CheckPaymentDetailGeneration();
            CheckAnnualReceiptPreparationGuard();
            CheckStatementRegenerationFreshness();
            CheckAsyncStatementAssembly();
            CheckNewCertificateDrafts();
            CheckRegularNewCertificateDraft();
            CheckCertificateNewDiscard();
            CheckBankCertificateBusy();
            CheckBankCertificateRecoveryGuard();
            CheckBankCertificateExactSelection();
            CheckReturnAdmissionDraft();
            CheckProfileShortcutContext();
            CheckAdjustmentRecoveryGuard();
            CheckPdfFileDurability();
            CheckAdministrationEditors();
            CheckSignInGuard();
            CheckHelpCenter();
            Console.WriteLine($"PASS: {checks} Windows theme switching, structure, rendering and resource checks.");
        }
        finally { AppTheme.SetTheme(originalTheme); }
    }

    private static void PumpUntil(Func<bool> completed, string message, TimeSpan? timeout=null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!completed() && deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)))
        { Application.DoEvents(); Thread.Yield(); }
        Check(completed(), message);
    }

    private static void CheckSignInGuard()
    {
        int attempts = 0;
        using var release = new ManualResetEventSlim();
        Func<string, string, (bool Success, string Message, Exception? ConnectionFailure)> attempt = (username, password) => {
            Interlocked.Increment(ref attempts);
            Check(username == "operator" && password == "captured password", "Sign-in uses the captured credentials");
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fake sign-in was not released");
            return (true, "", null);
        };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var login = (Form)Activator.CreateInstance(typeof(FMSC_SMS.Pages.Security.LoginForm), flags, null, new object[] { attempt }, null)!;
        var type = login.GetType();
        var username = (ModernTextBox)type.GetField("user", flags)!.GetValue(login)!;
        var password = (ModernTextBox)type.GetField("pass", flags)!.GetValue(login)!;
        var button = (ModernButton)type.GetField("login", flags)!.GetValue(login)!;
        username.Text = "operator"; password.Text = "captured password";
        var click = type.GetMethod("Login_Click", flags)!;
        click.Invoke(login, new object?[] { null, EventArgs.Empty });
        PumpUntil(() => Volatile.Read(ref attempts) == 1, "Sign-in starts on the worker thread");
        Check(!button.Enabled && !username.Enabled && !password.Enabled, "One sign-in holds its credentials and action");
        click.Invoke(login, new object?[] { null, EventArgs.Empty });
        Check(attempts == 1, "Repeated sign-in gestures must not start another authentication");
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(Form).GetMethod("OnFormClosing", flags)!.Invoke(login, new object[] { closing });
        Check(closing.Cancel, "Closing is blocked while authentication is in progress");
        release.Set();
        PumpUntil(() => button.Enabled, "Sign-in completion restores the form state");
        Check(login.DialogResult == DialogResult.OK && username.Enabled && password.Enabled, "A successful sign-in closes with restored field state");
    }

    private static void CheckInformationDialogs(Form owner)
    {
        Assembly application = typeof(ApplicationWindowForm).Assembly;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var failureType = application.GetType("FMSC_SMS.Core.DatabaseCompatibilityFailure", true)!;
        var issueType = application.GetType("FMSC_SMS.Core.DatabaseCompatibilityIssue", true)!;
        var exceptionType = application.GetType("FMSC_SMS.Core.DatabaseCompatibilityException", true)!;
        var connectionType = application.GetType("FMSC_SMS.Core.DatabaseConnectionForm", true)!;
        using var connection = (Form)Activator.CreateInstance(connectionType, flags, null, new object[] { false }, null)!;
        var showFailure = connectionType.GetMethod("ShowFailure", flags)!;
        var setBusy = connectionType.GetMethod("SetBusy", flags)!;
        foreach (var (version, title) in new[] { ("1", "Database update required"), ("3", "Application update required") })
        {
            var failure = Activator.CreateInstance(failureType, new object?[] {
                Enum.Parse(issueType, "SchemaVersion"), "2", version, "diagnostic detail", null })!;
            var exception = Activator.CreateInstance(exceptionType, flags, null, new[] { failure }, null)!;
            showFailure.Invoke(connection, new[] { "diagnostic detail", exception });
            setBusy.Invoke(connection, new object[] { false });
            Check(((Label)connectionType.GetField("_status", flags)!.GetValue(connection)!).Text == title, "Connection dialog must display the correct compatibility direction");
            var message = (Label)connectionType.GetField("_message", flags)!.GetValue(connection)!;
            Check(message.Text.Contains($"version: {version}") && message.Text.Contains("application: 2"), "Versions must be visible in the main message without expanding diagnostics");
            Check(!((Control)connectionType.GetField("_restart", flags)!.GetValue(connection)!).Enabled, "Restart must be disabled for compatibility failures");
            Check(!((Control)connectionType.GetField("_repair", flags)!.GetValue(connection)!).Enabled, "Dependency repair must be disabled for compatibility failures");
            Check(((Control)connectionType.GetField("_reconnect", flags)!.GetValue(connection)!).Enabled, "Reconnect remains available after an administrator fixes compatibility");
        }
        Check(!connection.IsHandleCreated, "Unshown connection fixture must never run its automatic SQL connection");
        showFailure.Invoke(connection, new object?[] { "Connection unavailable", null });
        setBusy.Invoke(connection, new object[] { false });
        Check(((Control)connectionType.GetField("_restart", flags)!.GetValue(connection)!).Enabled == File.Exists(FMSC_SMS.DatabaseHelper.DatabaseFile), "Restart is available for a connection failure only when the school file exists");
        var recoveryType = application.GetType("FMSC_SMS.Core.DatabaseRecoveryException", true)!;
        var recovery = Activator.CreateInstance(recoveryType, flags, null,
            new object[] { "EngineBusy", "Close other database programs first.", "Engine diagnostic 123" }, null)!;
        showFailure.Invoke(connection, new[] { "Generic outer text", recovery });
        setBusy.Invoke(connection, new object[] { false });
        Check(((Label)connectionType.GetField("_message", flags)!.GetValue(connection)!).Text.Contains("Close other database programs first."), "The main connection message retains the engine's explanation");
        Check(((Control)connectionType.GetField("_details", flags)!.GetValue(connection)!).Text.Contains("Engine diagnostic 123"), "Connection details retain engine command output");
        Check(!((Control)connectionType.GetField("_repair", flags)!.GetValue(connection)!).Enabled, "Busy engines must not suggest reinstalling software");
        Check(connection.AutoScaleMode == AutoScaleMode.None, "Connection dialog uses the application's existing scale metrics");

        var aboutType = application.GetType("FMSC_SMS.Core.AboutForm", true)!;
        using var about = (Form)Activator.CreateInstance(aboutType, flags, null, Array.Empty<object>(), null)!;
        about.Show(owner); Application.DoEvents();
        Control[] descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(descendants(child))).ToArray();
        var labels = descendants(about).OfType<Label>().Select(label => label.Text).ToArray();
        Check(labels.Contains("Required database schema") && labels.Contains(FMSC_SMS.DatabaseHelper.RequiredDatabaseSchemaVersion), "About shows the application's required schema");
        Check(labels.Contains("Application version") && labels.Contains("Build"), "About distinguishes application version and build");
        Check(about.AcceptButton is not null && ReferenceEquals(about.AcceptButton, about.CancelButton), "Enter and Escape provide a clear About close action");
        Check(descendants(about).OfType<ModernButton>().Any(button => button.Text == "Copy details"), "Support details can be copied without a database read");
        about.Close();
    }

    private static void CheckPhotoViewer(Form owner)
    {
        var type = typeof(ApplicationWindowForm).Assembly.GetType("FMSC_SMS.Core.StudentPhotoViewForm", true)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var enable = type.GetMethod("EnableZoom", BindingFlags.Static | BindingFlags.NonPublic)!;
        var replace = type.GetMethod("SetPreviewImage", BindingFlags.Static | BindingFlags.NonPublic)!;
        Control[] descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(descendants(child))).ToArray();
        using var preview = new PictureBox { Size = new Size(96, 100) };
        UiHelper.DisposePictureImageOnDispose(preview);
        bool contextRead = false;
        enable.Invoke(null, new object[] { preview, (Func<string>)(() => { contextRead = true; return "Student fixture"; }) });
        var click = typeof(Control).GetMethod("OnMouseClick", flags)!;
        click.Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
        Check(!contextRead && preview.Cursor == Cursors.Default, "An empty photo has no zoom action or hand cursor");
        var previousTheme = AppTheme.CurrentTheme;
        try
        {
            foreach (var (theme, width, height) in new[] { (AppThemeType.OceanBlue, 300, 600), (AppThemeType.ProfessionalSimple, 600, 300) })
            {
                AppTheme.SetTheme(theme);
                var original = new Bitmap(width, height);
                replace.Invoke(null, new object[] { preview, original });
                Check(preview.Cursor == Cursors.Hand, "Loaded photos indicate that they can be enlarged");
                click.Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0) });
                Check(!contextRead, "Right-click must not open a photo dialog");
                using var viewer = (Form)Activator.CreateInstance(type, flags, null, new object[] { original, "Student fixture" }, null)!;
                viewer.Show(owner); Application.DoEvents();
                var enlarged = descendants(viewer).OfType<PictureBox>().Single();
                Check(enlarged.SizeMode == PictureBoxSizeMode.Zoom && enlarged.Image!.Size == original.Size,
                    "Portrait and landscape viewers keep the complete image with proportional fitting");
                Check(enlarged.Width > preview.Width && enlarged.Height > preview.Height, "The dialog provides a larger photo area");
                Check(!ReferenceEquals(enlarged.Image, original), "The dialog owns an independent snapshot");
                Check(ReferenceEquals(viewer.AcceptButton, viewer.CancelButton) && viewer.CancelButton is not null,
                    "Enter and Escape share the close action");
                var replacement = new Bitmap(64, 64);
                replace.Invoke(null, new object[] { preview, replacement });
                Check(enlarged.Image!.Size == new Size(width, height), "Replacing the page image cannot invalidate the open viewer");
                viewer.Close(); viewer.Dispose();
                Check(enlarged.Image is null, "Closing releases the dialog's image");
                Check(preview.Image!.Size == new Size(64, 64), "Closing leaves the page's current image intact");
                replace.Invoke(null, new object?[] { preview, null });
                Check(preview.Cursor == Cursors.Default && preview.Image is null, "Clearing a photo removes its zoom hint");
            }
            owner.Controls.Add(preview);
            replace.Invoke(null, new object[] { preview, new Bitmap(64, 64) });
            bool closedByEscape = false;
            owner.BeginInvoke(new Action(() => {
                var modal = Application.OpenForms.Cast<Form>().Single(form => form.GetType() == type);
                Check(modal.Modal && modal.Owner == owner, "Clicking the photo opens a modal dialog owned by its page window");
                Check(descendants(modal).OfType<Label>().Any(label => label.Text == "Student fixture"), "The dialog identifies the displayed student");
                closedByEscape = (bool)typeof(ApplicationWindowForm).GetMethod("ProcessDialogKey", flags)!
                    .Invoke(modal, new object[] { Keys.Escape })!;
            }));
            click.Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
            Check(contextRead && closedByEscape, "Left-click opens the viewer and Escape closes it");
            Check(preview.Image!.Size == new Size(64, 64), "The thumbnail survives a complete open/close cycle");
            owner.Controls.Remove(preview);
        }
        finally { AppTheme.SetTheme(previousTheme); }
    }

    private static void CheckPhotoPreparation()
    {
        var processor = typeof(ApplicationWindowForm).Assembly.GetType("FMSC_SMS.Core.StudentPhotoProcessor", true)!;
        var prepare = processor.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] Prepared(byte[] source, CancellationToken token = default) =>
            (byte[])prepare.Invoke(null, new object[] { source, token })!;
        foreach (var (width, height, fittedWidth, fittedHeight) in new[] {
            (2000, 1200, 600, 360), (1200, 2000, 360, 600), (160, 90, 160, 90), (600, 600, 600, 600)
        })
        {
            using var source = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Red, 0, 0, width, height / 4);
                graphics.FillRectangle(Brushes.Blue, 0, height * 3 / 4, width, height / 4);
            }
            using var png = new MemoryStream(); source.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            byte[] jpeg = Prepared(png.ToArray());
            Check(jpeg.Length > 0 && jpeg.Length <= 80 * 1024, "Prepared photos fit the compact database storage limit");
            using var encoded = new MemoryStream(jpeg); using var result = Image.FromStream(encoded);
            Check(result.Width == fittedWidth && result.Height == fittedHeight, "Real image fitting preserves portrait/landscape proportions without upscaling");
            Check(result.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Jpeg.Guid, "Prepared photo bytes and JPEG content type agree");
            using var pixels = new Bitmap(result);
            Check(pixels.GetPixel(pixels.Width / 2, pixels.Height / 8).R > 200 && pixels.GetPixel(pixels.Width / 2, pixels.Height * 7 / 8).B > 200, "Photo fitting keeps both ends of the original image without cropping");
        }
        using (var transparent = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using var png = new MemoryStream(); transparent.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using var jpeg = new MemoryStream(Prepared(png.ToArray())); using var result = new Bitmap(jpeg);
            Check(result.GetPixel(0, 0).R > 245, "Transparent PNG areas flatten to white rather than black");
        }
        using (var noise = new Bitmap(600, 600, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        {
            var data = noise.LockBits(new Rectangle(0, 0, 600, 600), System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var pixels = new byte[data.Stride * 600]; new Random(1234).NextBytes(pixels);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally { noise.UnlockBits(data); }
            using var png = new MemoryStream(); noise.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            byte[] prepared = Prepared(png.ToArray());
            Check(prepared.Length <= 80 * 1024, "High-detail images fit the 80 KB cap through quality and proportional resolution selection");
            using var encoded = new MemoryStream(prepared); using var result = Image.FromStream(encoded);
            Check(result.Width == result.Height && result.Width <= 600 && result.Width >= 320,
                "Detailed images preserve proportions while keeping useful portrait resolution");
        }
        using (var landscape = new Bitmap(1200, 600))
        {
            using var jpeg = new MemoryStream(); landscape.Save(jpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
            byte[] original = jpeg.ToArray();
            // EXIF little-endian TIFF IFD0: orientation SHORT = 6 (rotate 90 degrees).
            byte[] exif = { 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
                1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0 };
            using var tagged = new MemoryStream();
            tagged.Write(original.AsSpan(0, 2)); tagged.Write(new byte[] { 255, 225, 0, 34 });
            tagged.Write(exif); tagged.Write(original.AsSpan(2));
            using var prepared = new MemoryStream(Prepared(tagged.ToArray())); using var portrait = Image.FromStream(prepared);
            Check(portrait.Width == 300 && portrait.Height == 600, "Phone orientation is corrected before fitting the aspect ratio");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { Prepared(new byte[] { 1, 2 }, cancelled.Token); throw new Exception("Expected photo cancellation"); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { checks++; }
    }

    private static void CheckSameRecordDraft()
    {
        // Keep this editor unparented: its deferred SQL loader is never started.
        using var editor = new MasterPage("Subjects", "", "UnusedRead", "UnusedSave",
            new[] { new FieldSpec("SubjectName", "Subject") }, _ => Array.Empty<Microsoft.Data.SqlClient.SqlParameter>(), recordIdColumn: "SubjectId");
        Check(!editor.IsHandleCreated, "SQL-free editor fixture must remain unparented and unshown");
        var grid = (DataGridView)typeof(V2Page).GetField("Grid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        grid.Columns.Clear(); grid.Columns.Add("SubjectId", "Id"); grid.Columns.Add("SubjectName", "Subject");
        grid.Rows.Add(7, "Maths");
        var load = typeof(MasterPage).GetMethod("LoadRow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Check((bool)load.Invoke(editor, new object[] { grid.Rows[0] })!, "Fixture record must load");
        var controls = (Dictionary<string, Control>)typeof(MasterPage).GetField("_controls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        controls["SubjectName"].Text = "Unsaved Maths";
        Check(editor.HasUnsavedChanges, "Draft must be recognized");
        Check((bool)load.Invoke(editor, new object[] { grid.Rows[0] })! && controls["SubjectName"].Text == "Unsaved Maths" && editor.HasUnsavedChanges,
            "Reopening the current record must preserve unsaved fields and baseline");
        typeof(MasterPage).GetMethod("CaptureEditorBaseline", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
        grid.Rows[0].Cells["SubjectName"].Value = "Refreshed Maths";
        Check((bool)load.Invoke(editor, new object[] { grid.Rows[0] })! && controls["SubjectName"].Text == "Refreshed Maths",
            "A clean editor must still reload refreshed values for the same record");
    }

    private static void CheckChoiceFilters(Form form)
    {
        CheckReportFilterSizing();
        var reportUi = typeof(AppTheme).Assembly.GetType("FMSC_SMS.Pages.Reports.ReportUi")!;
        using var filters = new Panel { Height = 50 };
        var method = new ModernComboBox { Width = 250 };
        method.Items.AddRange(new object[] { "All", "Cash", "Bank" }); method.SelectedIndex = 2;
        var view = new ModernComboBox { Left = 270, Width = 250 };
        view.Items.AddRange(new object[] { "Details", "Summary" }); view.SelectedIndex = 1;
        filters.Controls.AddRange(new Control[] { method, view });
        reportUi.GetMethod("RegisterChoiceFilter", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { method, 0 });
        using var grid = new DataGridView();
        var layoutMethod = reportUi.GetMethod("CompactLayout", new[] { typeof(Control), typeof(DataGridView), typeof(string), typeof(Control[]) })!;
        using var layout = (Control)layoutMethod.Invoke(null, new object[] { filters, grid, "Results", Array.Empty<Control>() })!;
        layout.Bounds = new Rectangle(20, 20, 680, 400); form.Controls.Add(layout); layout.BringToFront(); Application.DoEvents();
        IEnumerable<Control> Children(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Children(child)));
        Check(Children(layout).OfType<Label>().Any(label => label.Text == "1 filter active"), "Payment method must appear in active-filter feedback");
        Children(layout).OfType<ModernButton>().Single(control => control.Text == "Clear Filters").PerformClick();
        Check(method.SelectedIndex == 0 && view.SelectedIndex == 1, "Clear Filters must reset actual filters while preserving report view choices");
        Check(Children(layout).OfType<Label>().Any(label => label.Text == "No optional filters"), "Counter must update after clearing the choice filter");
        method.SelectedIndex=1;view.SelectedIndex=0;
        Children(layout).OfType<ModernButton>().Single(control=>control.Text=="Reset Filters").PerformClick();
        Check(method.SelectedIndex==0 && view.SelectedIndex==1 && grid.DataSource is null,
            "Reset Filters restores the launch view and default filters and clears report results");
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte value) { double v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
