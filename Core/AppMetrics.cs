namespace FMSC_SMS.Core
{
    public static class AppMetrics
    {
        public static int PagePadding =>
            AppScale.Scale(18, AppScale.SpacingScale);
        public static int PageTopPadding =>
            AppScale.Scale(12, AppScale.SpacingScale);
        public static int SectionGap =>
            AppScale.Scale(12, AppScale.SpacingScale);
        public static int SpacingXs =>
            AppScale.Scale(4, AppScale.SpacingScale);
        public static int SpacingSm =>
            AppScale.Scale(8, AppScale.SpacingScale);
        public static int SpacingMd =>
            AppScale.Scale(12, AppScale.SpacingScale);
        public static int SpacingLg =>
            AppScale.Scale(16, AppScale.SpacingScale);
        public static int SpacingXl =>
            AppScale.Scale(20, AppScale.SpacingScale);
        public static int SpacingXxl =>
            AppScale.Scale(24, AppScale.SpacingScale);
        // Dashboard minimum size Tuned for 1366 x 768 displays while remaining comfortable on larger screens.
        public const int DashboardMinimumWidth = 1080;
        public const int DashboardMinimumHeight = 600;
        public const int DashboardPagePadding = 18;
        // General controls
        public static int ControlHeight =>
            AppTheme.InputHeight;
        public static int ButtonHeight =>
            AppTheme.ButtonHeight;
        public static int LargeButtonHeight =>
            AppTheme.LargeButtonHeight;
        public static int ActionButtonMinimumWidth =>
            AppScale.Scale(122, AppScale.ControlSizeScale);
        public const int FieldLabelGap = 26;
        public const int LabelHeight = 24;
        public const int FieldHeight = 76;
        public const int SectionTitleFontSize = 14;
        public const int SectionTitleHeight = 42;
        public const int SectionHeaderHeight = 38;
        public static int SectionPadding =>
            AppScale.Scale(20, AppScale.SpacingScale);
        // DataGridView
        public static int GridRowHeight =>
            Math.Max(AppScale.Scale(38), TextHeight(AppTheme.NormalFont) + AppScale.Scale(12));
        public static int GridHeaderHeight =>
            Math.Max(AppScale.Scale(42), TextHeight(AppTheme.ButtonFont) + AppScale.Scale(14));
        // Sidebar
        public const int SidebarExpandedWidth = 280;
        public const int SidebarCompactWidth = 72;
        public const int SidebarCompactBreakpoint = 1450;
        public static int SidebarBrandHeight => AppScale.Scale(76);
        public static int SidebarUserHeight => AppScale.Scale(68);
        public static int SidebarCompactUserHeight => AppScale.Scale(54);
        public static int SidebarAccountHeight => AppScale.Scale(72);
        public static int SidebarModuleHeight => AppScale.Scale(40);
        public static int SidebarChildHeight => AppScale.Scale(34);
        public static int SidebarIconSize => AppScale.Scale(24);
        public static int SidebarLogoSize => AppScale.Scale(42);
        public static int SidebarAvatarSize => AppScale.Scale(38);
        public static int SidebarToggleSize => AppScale.Scale(32);
        public const int SeparatorHeight = 1;
        // Startup-window geometry follows the user login dialog. The installer
        // (Installer/core.ps1) uses the same logical 760 x 500 baseline.
        public const int StartupDialogWidth = 760;
        public const int StartupDialogHeight = 500;
        public static Size StartupDialogClientSize => new(
            AppScale.Scale(StartupDialogWidth), AppScale.Scale(StartupDialogHeight));
        // Related dialogs share a small set of content-appropriate size tiers.
        // These are *client* sizes at the current app/DPI scale. Opt-in only:
        // content-heavy grids, long forms and expandable dialogs keep their own sizes.
        public static Size CompactChoiceDialogClientSize => new(AppScale.S(540), AppScale.S(230));
        public static Size CompactEditorDialogClientSize => new(AppScale.S(520), AppScale.S(300));
        public static Size CredentialDialogClientSize => new(AppScale.S(560), AppScale.S(430));
        public static Size MediumDialogClientSize => new(AppScale.S(660), AppScale.S(430));
        public static Size RateWorkspaceDialogClientSize => new(AppScale.S(900), AppScale.S(640));
        // Authentication
        public const int AuthenticationTitleRowHeight = 70;
        public const int PasswordFieldRowHeight = 70;
        // Page header
        public const int PageHeaderVerticalPadding = 6;
        // Routed through AppScale so they give up some of that space on smaller screens along with everything else.
        public static int PageHeaderContentHeightAllowance =>
            AppScale.Scale(6);
        public const int PageHeaderTitleLeftPadding = 4;
        public const int PageHeaderTitleRightPadding = 8;
        public static int PageActionBarHeight =>
            AppScale.Scale(54);
        public const int PageActionStatusWidth = 260;
        public const int PageTitleHeight = 68;
        public const int PageTitleLeftPadding = 22;
        public const int PageTitleTopPadding = 15;
        // Dashboard title
        public const int DashboardTitleHeight = 58;
        public const int DashboardTitleLeftPadding = 5;
        public const int DashboardTitleTopPadding = 4;
        // Dashboard welcome panel
        public const int WelcomePanelHeight = 88;
        public const int WelcomePadding = 16;
        public const int WelcomeFontSize = 18;
        public const int NormalTextFontSize = 12;
        public const int WelcomeTextLeft = 16;
        public const int WelcomeTitleTop = 14;
        public const int WelcomeMessageTop = 46;
        // Dashboard cards
        public const int CardWidth = 210;
        public const int CardHeight = 100;
        public const int DashboardCardsAreaHeight = 145;
        public const int DashboardCardsTopPadding = 12;
        public static int CardGap =>
            AppScale.Scale(12, AppScale.SpacingScale);
        // Dashboard quick actions
        public const int QuickActionsAreaHeight = 400;
        public const int QuickActionsTopPadding = 10;
        public const int QuickActionsTitleFontSize = 13;
        public const int QuickActionsTitleTop = 4;
        // Dashboard action groups
        public const int ActionGroupWidth = 460;
        public const int ActionGroupHeight = 112;
        public const int ActionGroupLeftColumnX = 15;
        public const int ActionGroupRightColumnX = 490;
        public const int ActionGroupFirstRowY = 32;
        public const int ActionGroupSecondRowY = 156;
        public const int ActionGroupThirdRowY = 280;
        public const int ActionGroupPadding = 12;
        public const int ActionGroupTitleFontSize = 12;
        public const int ActionGroupTitleLeft = 12;
        public const int ActionGroupTitleTop = 8;
        // Dashboard action buttons
        public const int ActionButtonWidth = 215;
        public const int ActionButtonHeight = 42;
        public const int ActionButtonStartX = 12;
        public const int ActionButtonSpacing = 222;
        public const int ActionButtonTop = 36;
        public const int ActionGroupFourthRowY = 404;
        public const int ActionGroupFifthRowY = 528;
        public const int ActionGroupButtonAreaPadding = 4;
        public const int ActionButtonVerticalGap = 6;
        // Maximum dashboard quick-action content area. Reduced from 800 so a 1366 x 768 display does not force excessive vertical space.
        public const int DashboardQuickActionsAreaHeight = 620;
        // Text measurement cache
        private static readonly Dictionary<Font, int> _textHeightCache = new();
        private const int TextHeightCacheLimit = 256;
        private const int TextWidthCacheLimit = 2048;
        private static readonly Dictionary<(string Text, Font Font), int> _textWidthCache = new();
        static AppMetrics()
        {
            // Also invalidate outside the dashboard (for example, startup dialogs).
            AppScale.ScaleChanged += (_, _) => InvalidateScaleCache();
            Application.ApplicationExit += (_, _) => InvalidateScaleCache();
        }
        public static int TextHeight(Font font)
        {
            font = UiDpi.MeasurementFont(font);
            if (_textHeightCache.TryGetValue(font, out int cached))
                return cached;
            using var probe = new Label {
                AutoSize = true,
                Text = "Xg",
                Font = font
            };
            int height = Math.Max(12, probe.PreferredSize.Height);
            // Font compares by value (family, size, style, unit), so this safely reuses one cached measurement across every caller that shares one of the app's handful of theme fonts…
            if (_textHeightCache.Count >= TextHeightCacheLimit)
                _textHeightCache.Clear();
            _textHeightCache[font] = height;
            return height;
        }
        public static int TextWidth(string text, Font font, int horizontalPadding = 16)
        {
            font = UiDpi.MeasurementFont(font);
            string measuredText = string.IsNullOrEmpty(text) ? " " : text;
            var key = (measuredText, font);
            // Keep long, potentially user-entered content out of this layout cache.
            bool cacheable = measuredText.Length <= 256;
            if (cacheable && _textWidthCache.TryGetValue(key, out int cached))
                return cached + horizontalPadding;
            int width = TextRenderer.MeasureText(
                measuredText,
                font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width;
            if (cacheable)
            {
                if (_textWidthCache.Count >= TextWidthCacheLimit)
                    _textWidthCache.Clear();
                _textWidthCache[key] = width;
            }
            return width + horizontalPadding;
        }
        public static void FitButtonWidth(Button button, int minWidth, int horizontalPadding = 32)
        {
            int needed =
                TextWidth(button.Text, button.Font, horizontalPadding);
            button.Width = Math.Max(minWidth, needed);
        }
        // Input control sizing
        private static int? _inputControlHeight;
        public static int InputControlHeight
        {
            get
            {
                if (_inputControlHeight is null)
                {
                    using var probe = new ComboBox {
                        Font = AppTheme.NormalFont
                    };
                    _inputControlHeight = Math.Max(ControlHeight, probe.PreferredHeight);
                }
                return _inputControlHeight.Value;
            }
        }
        public static void InvalidateScaleCache()
        {
            _inputControlHeight = null;
            _fieldRowHeight = null;
            _textHeightCache.Clear();
            _textWidthCache.Clear();
            UiTextWidthCache.Clear();
        }
        public static int MultilineInputHeight(int baselineHeight) =>
            AppScale.Scale(baselineHeight, AppScale.ControlSizeScale);
        // Field sizing
        private static int? _fieldRowHeight;
        public static int FieldRowHeightFor(int controlHeight)
        {
            int captionHeight =
                Math.Max(16, TextHeight(AppTheme.ButtonFont));
            return captionHeight + controlHeight + SpacingSm;
        }
        public static int FieldRowHeight
        {
            get
            {
                if (_fieldRowHeight is null)
                    _fieldRowHeight =
                        FieldRowHeightFor(InputControlHeight);
                return _fieldRowHeight.Value;
            }
        }
        // Card sizing
        public static int CardHeaderHeight(bool hasSubtitle = true)
        {
            int titleLineHeight =
                Math.Max(16, TextHeight(AppTheme.ButtonFont));
            int subtitleLineHeight =
                hasSubtitle
                    ? Math.Max(14, TextHeight(AppTheme.NormalFont))
                    : 0;
            return titleLineHeight + subtitleLineHeight + 2 * SpacingXs;
        }
        public static int CardContentVerticalPadding =>
            2 * SpacingMd + SpacingSm;
        public static int FieldCardHeight(
            int fieldRows,
            bool hasSubtitle = true) =>
            CardHeaderHeight(hasSubtitle) +
            fieldRows * FieldRowHeight +
            CardContentVerticalPadding;
        // Button rows
        public static int ButtonRowHeight =>
            AppTheme.CompactButtonHeight + SpacingLg + SpacingXs;
    }
}
